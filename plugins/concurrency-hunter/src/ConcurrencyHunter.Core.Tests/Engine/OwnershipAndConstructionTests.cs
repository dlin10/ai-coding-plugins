using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class OwnershipAndConstructionTests
{
    [Fact]
    public void Static_region_is_shared()
    {
        var run = Execute("""
            public static class Counters { public static object? Last; }
            public class CountController : ControllerBase { public void Post() => Counters.Last = new object(); }
            """ + Startup());

        Assert.Equal(OwnershipKind.Shared, run.Ownership("static:Counters").Kind);
    }

    [Fact]
    public void Per_request_scoped_region_is_thread_confined()
    {
        var run = Execute("""
            public sealed class Draft { public object? Text; }
            public class DraftController(Draft draft) : ControllerBase { public void Post() => draft.Text = new object(); }
            """ + Startup("services.AddScoped<Draft>();"));

        Assert.Equal(OwnershipKind.ThreadConfined, run.Ownership("di:Draft@Scoped").Kind);
    }

    [Fact]
    public void Local_allocation_that_never_leaves_an_action_is_thread_confined()
    {
        var run = Execute("""
            public sealed class Box { public object? Value; }
            public class BoxController : ControllerBase { public void Post() { var box = new Box(); box.Value = new object(); } }
            """ + Startup());

        Assert.Equal(OwnershipKind.ThreadConfined, run.Ownership("alloc:BoxController.Post()#Box").Kind);
    }

    [Fact]
    public void Worker_allocation_stored_into_a_singleton_is_escaped_with_the_store_as_evidence()
    {
        var run = Execute("""
            public sealed class Note { public object? Text; }
            public sealed class Holder { public Note? Current; }
            public sealed class NoteWorker(Holder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { var note = new Note(); holder.Current = note; return Task.CompletedTask; }
            }
            public class NoteController(Holder holder) : ControllerBase { public void Get() => GC.KeepAlive(holder.Current); }
            """ + Startup("services.AddSingleton<Holder>(); services.AddHostedService<NoteWorker>();"));

        var ownership = run.Ownership("alloc:NoteWorker.ExecuteAsync(CancellationToken)#Note");
        Assert.Equal(OwnershipKind.Escaped, ownership.Kind);
        Assert.Contains(ownership.Evidence, evidence => evidence.Contains("stored into Current of di:Holder@Singleton at Case.cs:", StringComparison.Ordinal));
    }

    [Fact]
    public void Escape_through_a_captured_closure_names_a_source_location_on_every_hop()
    {
        var run = Execute("""
            public sealed class Note { public object? Text; }
            public sealed class CallbackHolder { public Action? OnMessage; }
            public sealed class NoteWorker(CallbackHolder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var note = new Note();
                    holder.OnMessage = () => note.Text = new object();
                    return Task.CompletedTask;
                }
            }
            public class NoteController(CallbackHolder holder) : ControllerBase { public void Post() => holder.OnMessage?.Invoke(); }
            """ + Startup("services.AddSingleton<CallbackHolder>().AddHostedService<NoteWorker>();"));

        var ownership = run.Ownership("alloc:NoteWorker.ExecuteAsync(CancellationToken)#Note");
        Assert.Equal(OwnershipKind.Escaped, ownership.Kind);
        Assert.Equal(2, ownership.Evidence.Count);
        Assert.Contains("is stored into OnMessage of di:CallbackHolder@Singleton at Case.cs:", ownership.Evidence[0], StringComparison.Ordinal);
        Assert.Contains("is captured by delegate:NoteWorker.ExecuteAsync(CancellationToken)#", ownership.Evidence[1], StringComparison.Ordinal);
        Assert.All(ownership.Evidence, hop => Assert.Matches(@" at Case\.cs:\d+\.$", hop));
    }

    [Fact]
    public void Singleton_constructor_write_to_its_own_field_is_construction_local()
    {
        var run = Execute("""
            public sealed class Clock { public object? Zone; public Clock() { Zone = new object(); } }
            public class ClockController(Clock clock) : ControllerBase { public void Get() => GC.KeepAlive(clock.Zone); }
            """ + Startup("services.AddSingleton<Clock>();"));

        var store = Assert.Single(run.Accesses("Zone", SummaryAccessKind.Store));
        Assert.True(store.IsConstructionLocal);
        Assert.False(Assert.Single(run.Accesses("Zone", SummaryAccessKind.Load)).IsConstructionLocal);
    }

    [Fact]
    public void Constructor_storing_this_into_a_static_is_not_construction_local()
    {
        var run = Execute("""
            public static class BeaconRegistry { public static Beacon? Last; }
            public sealed class Beacon { public object? Status; public Beacon() { BeaconRegistry.Last = this; Status = new object(); } }
            public class BeaconController(Beacon beacon) : ControllerBase { public void Get() => GC.KeepAlive(BeaconRegistry.Last!.Status); }
            """ + Startup("services.AddSingleton<Beacon>();"));

        Assert.False(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.Contains(run.Heap.Region("di:Beacon@Singleton").Identity, run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Constructor_storing_a_lambda_that_captures_nothing_stays_construction_local()
    {
        var run = Execute("""
            public static class Hooks { public static Action? Last; public static object? Ticks; }
            public sealed class Beacon { public object? Status; public Beacon() { Hooks.Last = () => Hooks.Ticks = new object(); Status = new object(); } }
            public class BeaconController(Beacon beacon) : ControllerBase { public void Get() => GC.KeepAlive(beacon.Status); }
            """ + Startup("services.AddSingleton<Beacon>();"));

        Assert.True(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.DoesNotContain(run.Heap.Region("di:Beacon@Singleton").Identity, run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Open_type_initializer_of_a_merged_context_has_a_merged_static_region_of_unknown_ownership()
    {
        var run = Execute("""
            public sealed class Order { }
            public sealed class Invoice { }
            public static class Defaults<T> { public static object? Value; static Defaults() { Value = new object(); } }
            public static class Access
            {
                public static object? Read<T>() => Defaults<T>.Value;
            }
            public class DefaultsController : ControllerBase
            {
                public void Post() { GC.KeepAlive(Access.Read<Order>()); GC.KeepAlive(Access.Read<Invoice>()); }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        var open = Assert.Single(run.Heap.Heap.Regions.Values, region => region.Display == "static:Defaults<T>");
        Assert.True(open.IsMerged);
        Assert.Equal(OwnershipKind.Unknown, run.Analysis.Ownership[open.Identity].Kind);
        var initializer = Assert.Single(run.Heap.Heap.Instances.Values,
                                        instance => instance.BodyId == "body:Fixture:M:Defaults`1.#cctor" && instance.IsMerged);
        var created = Assert.Single(run.Heap.Heap.Regions.Values,
                                    region => region.Display.StartsWith("alloc:Defaults<T>", StringComparison.Ordinal) &&
                                              region.Context == initializer.Context);
        Assert.True(created.IsMerged);
        Assert.Equal(OwnershipKind.Unknown, run.Analysis.Ownership[created.Identity].Kind);
    }

    [Fact]
    public void Constructor_storing_this_into_a_child_a_static_also_holds_publishes()
    {
        var run = Execute("""
            public sealed class Holder { public object? Owner; }
            public static class Registry { public static Holder? Slot; }
            public class BeaconController : ControllerBase
            {
                public object? Status;
                private readonly Holder _holder = new();
                public BeaconController() { Registry.Slot = _holder; _holder.Owner = this; Status = new object(); }
                public void Get() => GC.KeepAlive(Status);
            }
            """ + Startup());

        Assert.False(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.Contains(run.Heap.Region("receiver:BeaconController").Identity, run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Constructor_storing_this_into_a_transient_a_static_also_holds_publishes()
    {
        var run = Execute("""
            public sealed class Registry { public object? Owner; }
            public static class Slots { public static Registry? Last; }
            public sealed class Beacon
            {
                public object? Status;
                public Beacon(Registry registry) { Slots.Last = registry; registry.Owner = this; Status = new object(); }
            }
            public class BeaconController(Beacon beacon) : ControllerBase { public void Get() => GC.KeepAlive(beacon.Status); }
            """ + Startup("services.AddSingleton<Beacon>(); services.AddTransient<Registry>();"));

        Assert.False(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.Contains(run.Heap.Region("di:Beacon@Singleton").Identity, run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Constructor_storing_this_into_a_transient_it_resolved_stays_construction_local()
    {
        var run = Execute("""
            public sealed class Registry { public object? Owner; }
            public sealed class Beacon { public object? Status; public Beacon(Registry registry) { registry.Owner = this; Status = new object(); } }
            public class BeaconController(Beacon beacon) : ControllerBase { public void Get() => GC.KeepAlive(beacon.Status); }
            """ + Startup("services.AddSingleton<Beacon>(); services.AddTransient<Registry>();"));

        Assert.True(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.DoesNotContain(run.Heap.Region("di:Beacon@Singleton").Identity, run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Lazy_singleton_construction_static_write_is_an_at_most_once_execution_overlapping_an_action()
    {
        var run = Execute("""
            public static class Tracing { public static object? LastSource; }
            public sealed class Clock { public Clock() => Tracing.LastSource = new object(); }
            public class StartupController(Clock clock) : ControllerBase { public object? Source() => Tracing.LastSource; }
            """ + Startup("services.AddSingleton<Clock>();"));

        var store = Assert.Single(run.Accesses("LastSource", SummaryAccessKind.Store));
        var execution = run.Analysis.Execution(store.ExecutionId);
        Assert.Equal(ExecutionKind.LazyConstruction, execution.Kind);
        Assert.Equal("construction of di:Clock@Singleton", execution.Display);
        Assert.Equal(Multiplicity.AtMostOnce, execution.Policy.Multiplicity);
        Assert.False(run.Analysis.Overlaps(execution.Id, execution.Id));
        var load = Assert.Single(run.Accesses("LastSource", SummaryAccessKind.Load));
        Assert.True(run.Analysis.Overlaps(execution.Id, load.ExecutionId));
        Assert.False(store.IsConstructionLocal);
    }

    [Fact]
    public void Controller_constructor_static_write_belongs_to_each_action()
    {
        var run = Execute("""
            public static class Counters { public static object? Created; }
            public class CounterController : ControllerBase
            {
                public CounterController() => Counters.Created = new object();
                public void Get() { }
                public void Post() { }
            }
            """ + Startup());

        var stores = run.Accesses("Created", SummaryAccessKind.Store);
        Assert.Equal(2, stores.Count);
        Assert.All(stores, store => Assert.Equal(ExecutionKind.Root, run.Analysis.Execution(store.ExecutionId).Kind));
        Assert.Equal(2, stores.Select(store => store.ExecutionId).Distinct().Count());
    }

    [Fact]
    public void Hosted_service_constructor_static_write_is_a_startup_access()
    {
        var run = Execute("""
            public static class Primed { public static object? Region; }
            public sealed class Primer { public Primer() => Primed.Region = new object(); }
            public sealed class PrimingWorker : BackgroundService
            {
                public PrimingWorker(Primer primer) => GC.KeepAlive(primer);
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public sealed class SecondWorker : BackgroundService
            {
                public SecondWorker(Primer primer) => GC.KeepAlive(primer);
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public class PrimedController : ControllerBase { public object? Get() => Primed.Region; }
            """ + Startup("services.AddTransient<Primer>().AddHostedService<PrimingWorker>().AddHostedService<SecondWorker>();"));

        var stores = run.Accesses("Region", SummaryAccessKind.Store);
        Assert.NotEmpty(stores);
        Assert.All(stores, store => Assert.Equal(ExecutionKind.Startup, run.Analysis.Execution(store.ExecutionId).Kind));
        Assert.Single(run.Accesses("Region", SummaryAccessKind.Load));
        Assert.Equal(2, run.Heap.Heap.Instances.Values.Count(instance => instance.BodyId == "body:Fixture:M:Primer.#ctor"));
    }

    [Fact]
    public void Type_initializer_own_static_write_is_construction_local_and_other_static_write_belongs_to_its_execution()
    {
        var run = Execute("""
            public static class Tracing { public static object? LastCulture; }
            public static class Defaults
            {
                public static readonly object Culture;
                static Defaults() { Culture = new object(); Tracing.LastCulture = new object(); }
            }
            public class CultureController : ControllerBase { public object Get() => Defaults.Culture; }
            """ + Startup());

        Assert.True(Assert.Single(run.Accesses("Culture", SummaryAccessKind.Store)).IsConstructionLocal);
        var other = Assert.Single(run.Accesses("LastCulture", SummaryAccessKind.Store));
        Assert.False(other.IsConstructionLocal);
        Assert.Equal("type initializer of Defaults", run.Analysis.Execution(other.ExecutionId).Display);
    }

    [Fact]
    public void Only_allocations_in_bodies_that_run_once_are_one_object_per_process()
    {
        var run = Execute("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private object? _scratch;
                public Ledger() { Refresh(); Refresh(); }
                private void Refresh() => _scratch = new object();
            }
            public sealed class Box { public object? Value; }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() { var box = new Box(); box.Value = new object(); } }
            """ + Startup("services.AddSingleton<Ledger>();"));

        Assert.True(run.Analysis.IsSingleObject(run.Heap.Region("alloc:Ledger..ctor()#object").Identity));
        Assert.False(run.Analysis.IsSingleObject(run.Heap.Region("alloc:LedgerController.Post()#object").Identity));
        Assert.False(run.Analysis.IsSingleObject(run.Heap.Region("alloc:Ledger.Refresh()#object").Identity));
        Assert.True(run.Analysis.IsSingleObject(run.Heap.Region("di:Ledger@Singleton").Identity));
    }

    [Fact]
    public void Hosted_service_registered_twice_as_a_singleton_hosted_service_is_not_one_object()
    {
        var run = Execute("""
            public sealed class Worker : BackgroundService { protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask; }
            """ + Startup("services.AddSingleton<IHostedService, Worker>(); services.AddSingleton<IHostedService, Worker>();"));

        var worker = run.Heap.Region("di:Worker@Singleton");
        Assert.True(worker.MayOverlapItself);
        Assert.False(run.Analysis.IsSingleObject(worker.Identity));
    }

    [Fact]
    public void Base_constructor_storing_this_into_a_static_makes_the_derived_own_field_write_not_construction_local()
    {
        var run = Execute("""
            public static class Registry { public static object? Last; }
            public class Tracked { public Tracked() => Registry.Last = this; }
            public sealed class Thing : Tracked { public object? Status; public Thing() { Status = new object(); } }
            public class ThingController(Thing thing) : ControllerBase { public void Get() => GC.KeepAlive(thing.Status); }
            """ + Startup("services.AddSingleton<Thing>();"));

        Assert.False(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
    }

    [Fact]
    public void Own_field_this_local_lambda_and_read_only_helper_do_not_publish()
    {
        var run = Execute("""
            public sealed class Thing
            {
                public Thing? Self;
                public object? Status;
                public Thing()
                {
                    Self = this;
                    Action keep = () => GC.KeepAlive(this);
                    Inspect(this);
                    Status = new object();
                }
                private static void Inspect(Thing thing) => GC.KeepAlive(thing.Status);
            }
            public class ThingController(Thing thing) : ControllerBase { public void Get() => GC.KeepAlive(thing.Status); }
            """ + Startup("services.AddSingleton<Thing>();"));

        Assert.True(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.True(Assert.Single(run.Accesses("Self", SummaryAccessKind.Store)).IsConstructionLocal);
        Assert.Empty(run.Analysis.PublishedObjects);
    }

    [Fact]
    public void Helper_called_by_the_constructor_is_construction_local_and_from_an_action_is_ordinary()
    {
        var run = Execute("""
            public sealed class Clock { public object? Zone; public Clock() => Reset(); public void Reset() => Zone = new object(); }
            public class ClockController(Clock clock) : ControllerBase { public void Post() => clock.Reset(); }
            """ + Startup("services.AddSingleton<Clock>();"));

        var stores = run.Accesses("Zone", SummaryAccessKind.Store);
        Assert.Equal(2, stores.Count);
        Assert.Single(stores, store => store.IsConstructionLocal && run.Analysis.Execution(store.ExecutionId).Kind == ExecutionKind.LazyConstruction);
        Assert.Single(stores, store => !store.IsConstructionLocal && run.Analysis.Execution(store.ExecutionId).Kind == ExecutionKind.Root);
    }

    [Fact]
    public void Static_helper_a_type_initializer_calls_to_write_its_own_static_is_construction_local()
    {
        var run = Execute("""
            public static class Defaults
            {
                public static object? Culture;
                static Defaults() { Init(); }
                private static void Init() => Culture = new object();
            }
            public class CultureController : ControllerBase { public object? Get() => Defaults.Culture; }
            """ + Startup());

        var store = Assert.Single(run.Accesses("Culture", SummaryAccessKind.Store));
        Assert.True(store.IsConstructionLocal);
        Assert.Equal(ExecutionKind.TypeInitializer, run.Analysis.Execution(store.ExecutionId).Kind);
    }

    [Fact]
    public void Type_initializer_used_at_startup_and_by_an_action_is_a_startup_construction()
    {
        var run = Execute("""
            public static class Defaults { public static readonly object Culture = new object(); }
            public sealed class Warmup : BackgroundService
            {
                public Warmup() => GC.KeepAlive(Defaults.Culture);
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public class CultureController : ControllerBase { public object Get() => Defaults.Culture; }
            """ + Startup("services.AddHostedService<Warmup>();"));

        Assert.DoesNotContain(run.Analysis.Executions, execution => execution.Kind == ExecutionKind.TypeInitializer);
        Assert.All(run.Accesses("Culture", SummaryAccessKind.Store),
                   store => Assert.Equal(ExecutionKind.Startup, run.Analysis.Execution(store.ExecutionId).Kind));
        Assert.NotEmpty(run.Accesses("Culture", SummaryAccessKind.Store));
    }

    [Fact]
    public void Object_created_in_a_worker_has_local_constructor_writes_and_an_ordinary_later_write()
    {
        var run = Execute("""
            public sealed class Note { public object? Text; public Note() { Text = new object(); } }
            public sealed class Holder { public Note? Current; }
            public sealed class NoteWorker(Holder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var note = new Note();
                    holder.Current = note;
                    note.Text = new object();
                    return Task.CompletedTask;
                }
            }
            public class NoteController(Holder holder) : ControllerBase { public void Get() => GC.KeepAlive(holder.Current!.Text); }
            """ + Startup("services.AddSingleton<Holder>(); services.AddHostedService<NoteWorker>();"));

        var note = run.Heap.Region("alloc:NoteWorker.ExecuteAsync(CancellationToken)#Note").Identity;
        var stores = run.Accesses("Text", SummaryAccessKind.Store).Where(store => store.RegionId == note).ToArray();
        Assert.Single(stores, store => store.IsConstructionLocal && store.InstanceId.StartsWith("body:Fixture:M:Note.#ctor@", StringComparison.Ordinal));
        var later = Assert.Single(stores, store => !store.IsConstructionLocal);
        var read = Assert.Single(run.Accesses("Text", SummaryAccessKind.Load), load => load.RegionId == note);
        Assert.NotEqual(later.ExecutionId, read.ExecutionId);
        Assert.True(run.Analysis.Overlaps(later.ExecutionId, read.ExecutionId));
    }

    [Fact]
    public void Constructor_passing_this_of_a_singleton_to_an_opaque_call_publishes_it()
    {
        // The call may keep the shared object anywhere, so what its construction writes next is no longer its own (R2, ADR 0006).
        var run = Execute("""
            public sealed class Thing { public object? Status; public Thing() { Console.WriteLine(this); Status = new object(); } }
            public class ThingController(Thing thing) : ControllerBase { public void Get() => GC.KeepAlive(thing.Status); }
            """ + Startup("services.AddSingleton<Thing>();"));

        Assert.False(Assert.Single(run.Accesses("Status", SummaryAccessKind.Store)).IsConstructionLocal);
    }

    [Fact]
    public void Root_scope_service_resolved_for_two_lazy_singletons_is_one_construction_execution()
    {
        var run = Execute("""
            public static class Log { public static object? Last; }
            public sealed class Session { public Session() => Log.Last = new object(); }
            public sealed class First { public readonly Session Session; public First(Session session) => Session = session; }
            public sealed class Second { public readonly Session Session; public Second(Session session) => Session = session; }
            public class SessionController(First first, Second second) : ControllerBase { public void Post() { GC.KeepAlive(first); GC.KeepAlive(second); } }
            """ + Startup("services.AddSingleton<First>(); services.AddSingleton<Second>(); services.AddScoped<Session>();"));

        Assert.Single(run.Analysis.Executions, execution => execution.Display == "construction of di:Session@Scoped");
        var store = Assert.Single(run.Accesses("Last", SummaryAccessKind.Store));
        Assert.Equal("construction of di:Session@Scoped", run.Analysis.Execution(store.ExecutionId).Display);
    }

    [Fact]
    public void Type_initializer_activated_by_a_startup_type_initializer_is_a_startup_construction()
    {
        var run = Execute("""
            public static class Inner { public static readonly object Value = new object(); }
            public static class Outer { public static readonly object Value = Inner.Value; }
            public sealed class Warmup : BackgroundService
            {
                public Warmup() => GC.KeepAlive(Outer.Value);
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public class ValueController : ControllerBase { public object Get() => Inner.Value; }
            """ + Startup("services.AddHostedService<Warmup>();"));

        Assert.Contains(run.Heap.Heap.TypeInitializers, initializer => initializer.TypeKey == "Fixture:Inner");
        Assert.DoesNotContain(run.Analysis.Executions, execution => execution.Kind == ExecutionKind.TypeInitializer);
    }

    [Fact]
    public void Delegate_created_in_a_worker_and_stored_into_a_singleton_is_escaped()
    {
        var run = Execute("""
            public sealed class Hooks { public Action? Run; }
            public sealed class HookWorker(Hooks hooks) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { hooks.Run = () => { }; return Task.CompletedTask; }
            }
            public class HookController(Hooks hooks) : ControllerBase { public void Post() => hooks.Run?.Invoke(); }
            """ + Startup("services.AddSingleton<Hooks>(); services.AddHostedService<HookWorker>();"));

        var region = Assert.Single(run.Heap.Heap.Regions.Values, candidate => candidate.Kind == HeapRegionKind.Delegate);
        Assert.Equal(OwnershipKind.Escaped, run.Analysis.Ownership[region.Identity].Kind);
    }

    [Fact]
    public void Hosted_service_type_initializer_with_no_other_reference_is_a_startup_construction()
    {
        var run = Execute("""
            public sealed class Worker : BackgroundService
            {
                private static readonly object Gate = new object();
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """ + Startup("services.AddHostedService<Worker>();"));

        Assert.Contains(run.Heap.Heap.TypeInitializers, initializer => initializer.TypeKey == "Fixture:Worker");
        Assert.DoesNotContain(run.Analysis.Executions, execution => execution.Kind == ExecutionKind.TypeInitializer);
        var store = Assert.Single(run.Accesses("Gate", SummaryAccessKind.Store));
        Assert.Equal(ExecutionKind.Startup, run.Analysis.Execution(store.ExecutionId).Kind);
    }

    [Fact]
    public void Unaccessed_region_is_owned_and_merged_region_stored_into_a_static_is_unknown()
    {
        var run = Execute("""
            public sealed class Box { public object? Value; }
            public static class Registry { public static Box? Last; }
            public static class Factory { public static void Make() => Registry.Last = new Box(); }
            public class BoxController : ControllerBase
            {
                public void Post() { var unused = new Box(); GC.KeepAlive(unused); Factory.Make(); Factory.Make(); }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        Assert.Equal(OwnershipKind.Owned, run.Ownership("alloc:BoxController.Post()#Box").Kind);
        var merged = Assert.Single(run.Heap.Heap.Regions.Values, region => region is { Kind: HeapRegionKind.Allocation, IsMerged: true });
        Assert.Equal(OwnershipKind.Unknown, run.Analysis.Ownership[merged.Identity].Kind);
    }

    [Fact]
    public void Static_readonly_field_object_is_one_object_per_process()
    {
        var run = Execute("""
            public static class Gates { public static readonly object Main = new object(); public static object? Loose = new object(); }
            public class GateController : ControllerBase { public void Post() { lock (Gates.Main) { GC.KeepAlive(Gates.Loose); } } }
            """ + Startup());

        var gates = run.Heap.Region("static:Gates");
        Assert.True(run.Analysis.IsSingleObject(Assert.Single(run.Heap.Heap.PointsTo(gates.Identity, "Main"))));
    }
}
