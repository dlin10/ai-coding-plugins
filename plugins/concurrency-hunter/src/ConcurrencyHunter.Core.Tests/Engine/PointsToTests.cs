using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class PointsToTests
{
    [Fact]
    public void One_object_stored_in_two_fields_is_one_region()
    {
        var run = Solve("""
            public sealed class Box { }
            public sealed class Holder { public Box? A; public Box? B; }
            public class BoxController : ControllerBase
            {
                public void Post() { var holder = new Holder(); var box = new Box(); holder.A = box; holder.B = box; }
            }
            """ + Startup());

        var holder = run.Region("alloc:BoxController.Post()#Holder");
        Assert.Equal(["alloc:BoxController.Post()#Box"], run.Targets(holder, "A"));
        Assert.Equal(run.Heap.PointsTo(holder.Identity, "A"), run.Heap.PointsTo(holder.Identity, "B"));
    }

    [Fact]
    public void Two_new_sites_of_one_type_are_numbered_by_site()
    {
        var run = Solve("""
            public sealed class Box { }
            public sealed class Holder { public Box? A; public Box? B; }
            public class BoxController : ControllerBase
            {
                public void Post() { var holder = new Holder(); holder.A = new Box(); holder.B = new Box(); }
            }
            """ + Startup());

        var holder = run.Region("alloc:BoxController.Post()#Holder");
        Assert.Equal(["alloc:BoxController.Post()#Box"], run.Targets(holder, "A"));
        Assert.Equal(["alloc:BoxController.Post()#Box#2"], run.Targets(holder, "B"));
    }

    [Fact]
    public void Field_hidden_by_a_new_field_of_a_derived_type_is_a_slot_of_its_own()
    {
        var run = Solve("""
            public class Base { public object? _slot; }
            public sealed class Derived : Base { public new object? _slot; }
            public class SlotController : ControllerBase
            {
                public void Post()
                {
                    var derived = new Derived();
                    derived._slot = new object();
                    ((Base)derived)._slot = new object();
                    GC.KeepAlive(derived);
                }
            }
            """ + Startup());

        var region = run.Region("alloc:SlotController.Post()#Derived");
        Assert.Equal(["Fixture:Base._slot", "Fixture:Derived._slot"], run.Heap.FieldsOf(region.Identity).Order(StringComparer.Ordinal));
        Assert.Equal(["alloc:SlotController.Post()#object"], run.Targets(region, "Fixture:Derived._slot"));
        Assert.Equal(["alloc:SlotController.Post()#object#2"], run.Targets(region, "Fixture:Base._slot"));
    }

    [Fact]
    public void Access_path_of_the_base_and_the_field_together_decides_the_wildcard()
    {
        var source = """
            public sealed class Leaf { public object? Value; }
            public sealed class Chain { public Leaf Child { get; } = new(); public object? Value; }
            public class ChainController : ControllerBase
            {
                public void Shallow([FromServices] Chain chain) => chain.Value = new object();
                public void Deep([FromServices] Chain chain) => chain.Child.Value = new object();
            }
            """ + Startup("services.AddSingleton<Chain>();");

        var run = Analyze(source, new AnalysisLimits(MaxAccessPathDepth: 1));

        var shallow = Assert.Single(run.Of("Value"), access => access.Symbol == "ChainController.Shallow(Chain)");
        Assert.False(shallow.Resource.IsWildcard);
        Assert.Equal(["Value"], shallow.Resource.AccessPath);
        var deep = Assert.Single(run.Collection.Accesses, access => access.Symbol == "ChainController.Deep(Chain)" && access.Resource.IsWildcard);
        Assert.Equal(["*"], deep.Resource.AccessPath);
        Assert.DoesNotContain(run.Of("Value"), access => access.Symbol == "ChainController.Deep(Chain)");
        Assert.Equal("di:Chain@Singleton", deep.Resource.Region);
        Assert.All(Analyze(source).Of("Value"), access => Assert.False(access.Resource.IsWildcard));
    }

    [Fact]
    public void Initializer_lambda_of_a_primary_constructor_reaches_the_injected_object()
    {
        var run = Solve("""
            public sealed class Ledger { public object? Entry; }
            public sealed class Recorder(Ledger ledger)
            {
                public readonly Action Run = () => ledger.Entry = new object();
            }
            public class RecorderController(Recorder recorder) : ControllerBase { public void Post() => recorder.Run(); }
            """ + Startup("services.AddSingleton<Ledger>(); services.AddSingleton<Recorder>();"));

        var recorder = run.Region("di:Recorder@Singleton");
        Assert.Equal(["di:Ledger@Singleton"], run.Targets(recorder, "ledger"));
        var lambda = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.EndsWith("#lambda1", StringComparison.Ordinal));
        Assert.Equal(["di:Recorder@Singleton"], lambda.Receivers.Select(region => run.Heap.Regions[region].Display));
        // The lambda writes through the injected object, so the singleton Ledger holds what it allocates.
        Assert.Equal(["alloc:Recorder..ctor(Ledger)#object"], run.Targets(run.Region("di:Ledger@Singleton"), "Entry"));
    }

    [Fact]
    public void Same_named_types_of_two_assemblies_keep_their_own_field_slots()
    {
        static string Library(string name) => Usings + $$"""
            namespace Shared { public sealed class Holder { public object? _slot; } }
            public class {{name}}Controller : ControllerBase
            {
                public void Post() { var holder = new Shared.Holder(); holder._slot = new object(); GC.KeepAlive(holder); }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Alpha"), ("App", "Beta")]
            },
            ("Alpha", "Alpha.cs", Library("Alpha")),
            ("Beta", "Beta.cs", Library("Beta")),
            ("App", "Program.cs", Usings + "System.Console.WriteLine();" + Startup()));

        var run = Solve(ReachScope(solution, "scope:App"));

        var alpha = Assert.Single(run.Heap.Regions.Values, region => region.TypeKey == "Alpha:Shared.Holder");
        var beta = Assert.Single(run.Heap.Regions.Values, region => region.TypeKey == "Beta:Shared.Holder");
        Assert.Equal(["Alpha:Shared.Holder._slot"], run.Heap.FieldsOf(alpha.Identity));
        Assert.Equal(["Beta:Shared.Holder._slot"], run.Heap.FieldsOf(beta.Identity));
        Assert.Empty(run.Heap.PointsTo(alpha.Identity, "Beta:Shared.Holder._slot"));
        Assert.Single(run.Heap.PointsTo(alpha.Identity, "_slot"));
    }

    [Fact]
    public void Constructor_parameter_of_a_closed_generic_resolves_its_substituted_service()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Store<T> { public object? Last; }
            public sealed class Consumer<T> { public readonly Store<T> Store; public Consumer(Store<T> store) => Store = store; }
            public class OrderController(Consumer<Order> consumer) : ControllerBase { public void Post() => consumer.Store.Last = new object(); }
            """ + Startup("services.AddSingleton<Store<Order>>(); services.AddSingleton<Consumer<Order>>();"));

        var consumer = run.Region("di:Consumer<Order>@Singleton");
        Assert.Equal(["di:Store<Order>@Singleton"], run.Targets(consumer, "Store"));
    }

    [Fact]
    public void Generic_body_reached_with_two_type_arguments_allocates_and_creates_a_delegate_per_substitution()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public sealed class Slot { public object? Value; }
            public static class Store { public static object? Last; }
            public static class Relay
            {
                public static void Handle<T>(T value) { }
                public static void Run<T>(T value)
                {
                    var slot = new Slot();
                    Action<T> hook = Handle<T>;
                    hook(value);
                    slot.Value = hook;
                    Store.Last = slot;
                }
            }
            public class RelayController : ControllerBase
            {
                public void Post() { Forward(new Order()); Forward(new Invoice()); }
                private static void Forward<T>(T value) => Relay.Run(value);
            }
            """ + Startup());

        var allocations = run.Regions("alloc:Relay.Run<T>(T)#Slot");
        var delegates = run.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Delegate).ToArray();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(2, allocations.Select(region => region.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, delegates.Length);
        Assert.Single(delegates.Select(region => region.Display).Distinct(StringComparer.Ordinal));
        Assert.Equal(["System.Action<Invoice>", "System.Action<Order>"],
                     delegates.Select(region => region.TypeKey).Order(StringComparer.Ordinal));
        var targets = run.Instances("body:Fixture:M:Relay.Handle``1(``0)");
        Assert.Equal(2, targets.Count);
        Assert.Equal(["Fixture:Invoice", "Fixture:Order"],
                     targets.SelectMany(instance => instance.Substitution.Values).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void One_method_on_two_receivers_keeps_two_instantiations()
    {
        var run = Solve(TwoBoxes + Startup());

        Assert.Equal(2, run.Instances("body:Fixture:M:Box.Set(System.Object)").Count);
        Assert.Equal(["alloc:BoxController.Post()#object"], run.Targets(run.Region("alloc:BoxController.Post()#Box"), "Value"));
        Assert.Equal(["alloc:BoxController.Post()#object#2"], run.Targets(run.Region("alloc:BoxController.Post()#Box#2"), "Value"));
    }

    [Fact]
    public void Static_factory_called_from_two_field_initializers_gives_two_objects()
    {
        var run = Solve("""
            public sealed class Draft { }
            public static class Drafts { public static Draft Create() => new Draft(); }
            public sealed class Editor { public readonly Draft First = Drafts.Create(); public readonly Draft Second = Drafts.Create(); }
            public class EditorController : ControllerBase { public void Post() { var editor = new Editor(); GC.KeepAlive(editor.First); } }
            """ + Startup());

        var editor = run.Region("alloc:EditorController.Post()#Editor");
        var first = Assert.Single(run.Heap.PointsTo(editor.Identity, "First"));
        var second = Assert.Single(run.Heap.PointsTo(editor.Identity, "Second"));
        Assert.NotEqual(first, second);
        Assert.Equal("alloc:Drafts.Create()#Draft", run.Heap.Regions[first].Display);
        Assert.Equal("alloc:Drafts.Create()#Draft", run.Heap.Regions[second].Display);
    }

    [Fact]
    public void Factory_returning_a_static_field_object_gives_that_object()
    {
        var run = Solve("""
            public sealed class Settings { }
            public static class SettingsFactory { private static readonly Settings Shared = new Settings(); public static Settings Get() => Shared; }
            public sealed class Holder { public Settings? Current; }
            public class SettingsController : ControllerBase { public void Post() { var holder = new Holder(); holder.Current = SettingsFactory.Get(); } }
            """ + Startup());

        Assert.Equal(["alloc:SettingsFactory..cctor()#Settings"], run.Targets(run.Region("alloc:SettingsController.Post()#Holder"), "Current"));
        Assert.Equal(run.Targets(run.Region("static:SettingsFactory"), "Shared"),
                     run.Targets(run.Region("alloc:SettingsController.Post()#Holder"), "Current"));
    }

    [Fact]
    public void Interface_call_through_a_di_binding_reaches_only_the_registered_implementation()
    {
        var run = Solve("""
            public interface ISink { void Write(); }
            public static class Log { public static object? Last; }
            public sealed class FileSink : ISink { public void Write() => Log.Last = new object(); }
            public sealed class MemorySink : ISink { public void Write() => Log.Last = new object(); }
            public class LogController(ISink sink) : ControllerBase { public void Post() => sink.Write(); }
            """ + Startup("services.AddSingleton<ISink, MemorySink>();"));

        var instance = Assert.Single(run.Instances("body:Fixture:M:MemorySink.Write"));
        Assert.Empty(run.Instances("body:Fixture:M:FileSink.Write"));
        Assert.Contains(run.Heap.Edges, edge => edge.CalleeInstance == instance.Id && edge.Reason == "di-binding");
        Assert.Equal(["alloc:MemorySink.Write()#object"], run.Targets(run.Region("static:Log"), "Last"));
    }

    [Fact]
    public void Abstract_call_reaches_only_the_override_with_an_allocation()
    {
        var run = Solve(Shapes + Startup());

        Assert.Single(run.Instances("body:Fixture:M:Circle.Draw"));
        Assert.Empty(run.Instances("body:Fixture:M:Square.Draw"));
        Assert.Contains(run.Heap.Edges, edge => edge.Reason == "points-to");
    }

    [Fact]
    public void Delegate_stored_in_a_field_and_invoked_reaches_its_method_group_with_its_receiver()
    {
        var run = Solve("""
            public sealed class Counter { public object? Last; public void Bump() => Last = new object(); }
            public sealed class Hooks { public Action? OnPost; }
            public class HookController : ControllerBase
            {
                public void Post() { var counter = new Counter(); var hooks = new Hooks(); hooks.OnPost = counter.Bump; hooks.OnPost!(); }
            }
            """ + Startup());

        var bump = Assert.Single(run.Instances("body:Fixture:M:Counter.Bump"));
        var counter = run.Region("alloc:HookController.Post()#Counter");
        Assert.Equal([counter.Identity], bump.Receivers);
        Assert.Contains(run.Heap.Edges, edge => edge.CalleeInstance == bump.Id && edge.Reason == "delegate");
        Assert.Equal(["alloc:Counter.Bump()#object"], run.Targets(counter, "Last"));
        Assert.StartsWith("delegate:HookController.Post()#Counter.Bump()", Assert.Single(run.Targets(run.Region("alloc:HookController.Post()#Hooks"), "OnPost")),
                          StringComparison.Ordinal);
    }

    [Fact]
    public void Out_parameter_returns_the_callee_object_to_the_caller()
    {
        var run = Solve("""
            public sealed class Memo { }
            public sealed class Editor { private readonly Memo _memo = new(); public void Open(out Memo memo) => memo = _memo; }
            public sealed class Holder { public Memo? Opened; }
            public class EditorController : ControllerBase
            {
                public void Put() { var editor = new Editor(); editor.Open(out var memo); var holder = new Holder(); holder.Opened = memo; }
            }
            """ + Startup());

        Assert.Equal(["alloc:Editor..ctor()#Memo"], run.Targets(run.Region("alloc:EditorController.Put()#Holder"), "Opened"));
    }

    [Fact]
    public void Captured_local_reaches_the_lambda_body_through_a_delegate_stored_in_a_singleton()
    {
        var run = Solve("""
            public sealed class Note { public object? Text; }
            public sealed class CallbackHolder { public Action<object>? OnMessage; }
            public sealed class NoteWorker(CallbackHolder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var note = new Note();
                    holder.OnMessage = text => note.Text = text;
                    return Task.CompletedTask;
                }
            }
            public class NoteController(CallbackHolder holder) : ControllerBase { public void Post() => holder.OnMessage!(new object()); }
            """ + Startup("services.AddSingleton<CallbackHolder>(); services.AddHostedService<NoteWorker>();"));

        Assert.Single(run.Instances("body:Fixture:M:NoteWorker.ExecuteAsync(System.Threading.CancellationToken)#lambda1"));
        Assert.Equal(["alloc:NoteController.Post()#object"], run.Targets(run.Region("alloc:NoteWorker.ExecuteAsync(CancellationToken)#Note"), "Text"));
    }

    [Fact]
    public void Store_of_order_and_store_of_invoice_singletons_are_two_regions()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public sealed class Store<T> { public object? Last; }
            public class StoreController(Store<Order> orders, Store<Invoice> invoices) : ControllerBase
            {
                public void Post() { orders.Last = new object(); invoices.Last = new object(); }
            }
            """ + Startup("services.AddSingleton<Store<Order>>(); services.AddSingleton<Store<Invoice>>();"));

        Assert.Equal(["alloc:StoreController.Post()#object"], run.Targets(run.Region("di:Store<Order>@Singleton"), "Last"));
        Assert.Equal(["alloc:StoreController.Post()#object#2"], run.Targets(run.Region("di:Store<Invoice>@Singleton"), "Last"));
    }

    [Fact]
    public void Factory_and_instance_registrations_bind_their_di_regions_without_a_construction()
    {
        var run = Solve("""
            public sealed class Rates { public object? Current; }
            public sealed class Prices { public object? Current; }
            public class RatesController(Rates rates, Prices prices) : ControllerBase
            {
                public void Post() { rates.Current = new object(); prices.Current = new object(); }
            }
            """ + Startup("services.AddSingleton<Rates>(_ => new Rates()); services.AddSingleton(new Prices());"));

        var rates = run.Region("di:Rates@Singleton");
        var prices = run.Region("di:Prices@Singleton");
        Assert.Equal(["alloc:RatesController.Post()#object"], run.Targets(rates, "Current"));
        Assert.Equal(["alloc:RatesController.Post()#object#2"], run.Targets(prices, "Current"));
        Assert.DoesNotContain(run.Heap.Constructions, construction => construction.RegionId == rates.Identity || construction.RegionId == prices.Identity);
        Assert.Empty(run.Instances("body:Fixture:M:Rates.#ctor"));
    }

    [Fact]
    public void Recursive_method_terminates()
    {
        var run = Solve("""
            public sealed class Node { public Node? Next; }
            public class WalkController : ControllerBase
            {
                public void Post() => Walk(new Node(), 5);
                private static void Walk(Node node, int depth) { node.Next = new Node(); if (depth > 0) Walk(node.Next, depth - 1); }
            }
            """ + Startup());

        Assert.InRange(run.Instances("body:Fixture:M:WalkController.Walk(Node,System.Int32)").Count, 1, 2);
        Assert.NotEmpty(run.Targets(run.Region("alloc:WalkController.Post()#Node"), "Next"));
    }

    [Fact]
    public void Two_receivers_merge_at_one_context_and_the_merged_instance_keeps_both_effects()
    {
        var run = Solve(TwoBoxes + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        var merged = Assert.Single(run.Instances("body:Fixture:M:Box.Set(System.Object)"), instance => instance.IsMerged);
        Assert.Equal(2, merged.Receivers.Count);
        Assert.Contains("alloc:BoxController.Post()#object", run.Targets(run.Region("alloc:BoxController.Post()#Box"), "Value"));
        Assert.Contains("alloc:BoxController.Post()#object#2", run.Targets(run.Region("alloc:BoxController.Post()#Box#2"), "Value"));
        Assert.True(run.Counter(HeapCounters.MERGED_CONTEXT) > 0);
    }

    [Fact]
    public void Lock_object_aliased_through_a_second_field_is_one_region()
    {
        var run = Solve("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private readonly object _second;
                public object? Last;
                public Ledger() => _second = _gate;
                public void Write(object entry) { lock (_second) { Last = entry; } }
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => ledger.Write(new object()); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var ledger = run.Region("di:Ledger@Singleton");
        Assert.Equal(["alloc:Ledger..ctor()#object"], run.Targets(ledger, "_gate"));
        Assert.Equal(run.Heap.PointsTo(ledger.Identity, "_gate"), run.Heap.PointsTo(ledger.Identity, "_second"));
    }

    [Fact]
    public void Transient_parameters_and_consumers_never_share_an_object()
    {
        var run = Solve("""
            public sealed class Clock { }
            public sealed class Pair { public readonly Clock First; public readonly Clock Second; public Pair(Clock first, Clock second) { First = first; Second = second; } }
            public sealed class Audit { public readonly Clock Clock; public Audit(Clock clock) => Clock = clock; }
            public class PairController(Pair pair, Audit audit) : ControllerBase { public void Post() { GC.KeepAlive(pair); GC.KeepAlive(audit); } }
            """ + Startup("services.AddScoped<Pair>(); services.AddScoped<Audit>(); services.AddTransient<Clock>();"));

        var pair = run.Region("di:Pair@Scoped");
        var first = Assert.Single(run.Heap.PointsTo(pair.Identity, "First"));
        var second = Assert.Single(run.Heap.PointsTo(pair.Identity, "Second"));
        var audit = Assert.Single(run.Heap.PointsTo(run.Region("di:Audit@Scoped").Identity, "Clock"));
        Assert.Equal(3, new[] { first, second, audit }.Distinct().Count());
        Assert.All(new[] { first, second, audit }, id => Assert.Equal("di:Clock@Transient", run.Heap.Regions[id].Display));
    }

    [Fact]
    public void Scoped_service_injected_into_two_singletons_is_one_root_scope_object()
    {
        var run = Solve("""
            public sealed class Session { }
            public sealed class First { public readonly Session Session; public First(Session session) => Session = session; }
            public sealed class Second { public readonly Session Session; public Second(Session session) => Session = session; }
            public class SessionController(First first, Second second) : ControllerBase { public void Post() { GC.KeepAlive(first); GC.KeepAlive(second); } }
            """ + Startup("services.AddSingleton<First>(); services.AddSingleton<Second>(); services.AddScoped<Session>();"));

        var session = Assert.Single(run.Heap.PointsTo(run.Region("di:First@Singleton").Identity, "Session"));
        Assert.Equal([session], run.Heap.PointsTo(run.Region("di:Second@Singleton").Identity, "Session"));
        Assert.Equal("root-scope", run.Heap.Regions[session].Context);
    }

    [Fact]
    public void Generic_static_method_writes_one_static_region_per_type_argument()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public static class Cache<T> { public static object? Last; }
            public static class Writer { public static void Write<T>(object value) => Cache<T>.Last = value; }
            public class CacheController : ControllerBase
            {
                public void Post() { Writer.Write<Order>(new object()); Writer.Write<Invoice>(new object()); }
            }
            """ + Startup());

        Assert.Equal(["alloc:CacheController.Post()#object"], run.Targets(run.Region("static:Cache<Order>"), "Last"));
        Assert.Equal(["alloc:CacheController.Post()#object#2"], run.Targets(run.Region("static:Cache<Invoice>"), "Last"));
    }

    [Fact]
    public void Local_function_uses_the_capture_cell_of_its_method()
    {
        var run = Solve("""
            public sealed class Holder { public object? Seen; }
            public class LocalController : ControllerBase
            {
                public void Post()
                {
                    var holder = new Holder();
                    object current = new object();
                    Replace();
                    void Replace() { current = new Holder(); holder.Seen = current; }
                }
            }
            """ + Startup());

        var method = Assert.Single(run.Instances("body:Fixture:M:LocalController.Post"));
        var local = Assert.Single(run.Instances("body:Fixture:M:LocalController.Post#local:Replace"));
        Assert.Equal([method.Id], local.CellOwners);
        var key = Assert.Single(method.Summary.Variables, variable => variable.SymbolKey.Contains("|current|", StringComparison.Ordinal)).SymbolKey;
        var cell = run.Heap.Cell(method.Id, key).Select(id => run.Heap.Regions[id].Display).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["alloc:LocalController.Post()#Holder#2", "alloc:LocalController.Post()#object"], cell);
        Assert.Contains("alloc:LocalController.Post()#object", run.Targets(run.Region("alloc:LocalController.Post()#Holder"), "Seen"));
    }

    [Fact]
    public void Lambda_and_its_creating_method_write_one_capture_cell()
    {
        var run = Solve("""
            public sealed class Hooks { public Action? Run; }
            public class HookController(Hooks hooks) : ControllerBase
            {
                public void Post()
                {
                    object current = new object();
                    hooks.Run = () => current = new Hooks();
                    current = new System.Collections.Generic.List<int>();
                    hooks.Run();
                }
            }
            """ + Startup("services.AddSingleton<Hooks>();"));

        var method = Assert.Single(run.Instances("body:Fixture:M:HookController.Post"));
        Assert.Single(run.Instances("body:Fixture:M:HookController.Post#lambda1"));
        var key = Assert.Single(method.Summary.Delegates).Delegate.CapturedValues.Keys.Single(candidate => candidate != "this");
        var cell = run.Heap.Cell(method.Id, key).Select(id => run.Heap.Regions[id].Display).ToArray();
        Assert.Contains("alloc:HookController.Post()#Hooks", cell);
        Assert.Contains("alloc:HookController.Post()#System.Collections.Generic.List<int>", cell);
        Assert.Contains("alloc:HookController.Post()#object", cell);
    }

    [Fact]
    public void Scc_budget_merges_a_mutually_recursive_pair_without_changing_its_facts()
    {
        const string SOURCE = """
            public sealed class Holder { public object? Value; }
            public static class Ping
            {
                public static object Even(Holder holder, object value, int depth) { if (depth > 0) value = Odd(holder, value, depth - 1); holder.Value = value; return value; }
                public static object Odd(Holder holder, object value, int depth) { if (depth > 0) return Even(holder, value, depth - 1); return value; }
            }
            public class PingController : ControllerBase { public void Post() { var holder = new Holder(); Ping.Even(holder, new object(), 4); } }
            """;
        var exact = Solve(SOURCE + Startup());
        var budgeted = Solve(SOURCE + Startup(), new AnalysisLimits(MaxSccIterations: 1));

        Assert.Contains("alloc:PingController.Post()#Holder.Value->alloc:PingController.Post()#object", Facts(exact));
        Assert.Equal(Facts(exact), Facts(budgeted));
        Assert.Equal(0, exact.Counter(HeapCounters.SCC_BUDGET_EXCEEDED));
        Assert.True(budgeted.Counter(HeapCounters.SCC_BUDGET_EXCEEDED) > 0);
    }

    [Fact]
    public void Controller_injected_field_points_through_its_receiver_region_and_a_cha_only_override_is_lowered_not_reached()
    {
        var run = Solve("""
            public sealed class Settings { }
            public abstract class Rule { public abstract void Check(); }
            public sealed class StrictRule : Rule { public override void Check() { } }
            public class SettingsController : ControllerBase
            {
                private readonly Settings _settings;
                private readonly Rule? _rule;
                public SettingsController(Settings settings) => _settings = settings;
                public void Get() { GC.KeepAlive(_settings); _rule?.Check(); }
            }
            """ + Startup("services.AddSingleton<Settings>();"));

        var receiver = run.Region("receiver:SettingsController");
        Assert.Equal(["di:Settings@Singleton"], run.Targets(receiver, "_settings"));
        Assert.Contains("body:Fixture:M:StrictRule.Check", run.Heap.LoweredNotReached);
        Assert.True(run.Counter(HeapCounters.LOWERED_NOT_REACHED) > 0);
    }

    [Fact]
    public void Minimal_api_service_parameter_and_from_services_parameter_point_to_singleton_regions()
    {
        var run = Solve("""
            public sealed class Signal { public object? Value; }
            public sealed class Relay { public object? Target; }
            public static class Endpoints { public static void Map(IEndpointRouteBuilder app) => app.MapPost("/signal", (Signal signal) => signal.Value = new object()); }
            public class RelayController : ControllerBase { public void Post([FromServices] Relay relay) => relay.Target = new object(); }
            """ + Startup("services.AddSingleton<Signal>(); services.AddSingleton<Relay>();"));

        Assert.Equal(["alloc:Endpoints.Map(IEndpointRouteBuilder)#object"], run.Targets(run.Region("di:Signal@Singleton"), "Value"));
        Assert.Equal(["alloc:RelayController.Post(Relay)#object"], run.Targets(run.Region("di:Relay@Singleton"), "Target"));
    }

    [Fact]
    public void Direct_call_on_request_data_reaches_its_static_write_discards_this_and_allocates_per_action()
    {
        var run = Solve("""
            public static class Counters { public static object? Last; }
            public sealed class Payload { public object? Data; public void Stamp() { Counters.Last = new object(); Data = new object(); } }
            public class PayloadController : ControllerBase
            {
                public void Post(Payload payload) => payload.Stamp();
                public void Put(Payload payload) => payload.Stamp();
            }
            """ + Startup());

        var stamps = run.Instances("body:Fixture:M:Payload.Stamp");
        Assert.Equal(2, stamps.Count);
        Assert.All(stamps, stamp => Assert.True(stamp.IsReceiverless));
        Assert.All(stamps, stamp => Assert.Empty(run.Heap.Resolve(stamp.Id, ThisValue.Instance)));
        var written = run.Heap.PointsTo(run.Region("static:Counters").Identity, "Last");
        Assert.Equal(2, written.Count);
        Assert.All(written, id => Assert.Equal("alloc:Payload.Stamp()#object", run.Heap.Regions[id].Display));
        Assert.Single(run.Heap.NoReceiverObjects, item => item.BodyId == "body:Fixture:M:PayloadController.Post(Payload)");
        Assert.Equal(2, run.Counter(HeapCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Interface_calls_on_request_data_and_on_an_interface_typed_factory_call_nothing()
    {
        var run = Solve("""
            public interface ISink { void Write(); }
            public static class Counters { public static object? Last; }
            public sealed class Sink : ISink { public void Write() => Counters.Last = new object(); }
            public class SinkController(ISink registered) : ControllerBase { public void Post(ISink requested) { requested.Write(); registered.Write(); } }
            """ + Startup("services.AddSingleton<ISink>(_ => new Sink());"));

        Assert.Empty(run.Instances("body:Fixture:M:Sink.Write"));
        Assert.Equal(2, run.Heap.NoReceiverObjects.Count(item => item.BodyId == "body:Fixture:M:SinkController.Post(ISink)"));
        Assert.Equal(2, run.Counter(HeapCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Inherited_generic_base_method_writes_the_closed_static_of_the_derived_type_argument()
    {
        var run = Solve("""
            public sealed class Order { }
            public static class Cache<T> { public static object? Last; }
            public class Base<T> { public void Remember(object value) => Cache<T>.Last = value; }
            public sealed class Derived : Base<Order> { }
            public class DerivedController(Derived derived) : ControllerBase { public void Post() => derived.Remember(new object()); }
            """ + Startup("services.AddSingleton<Derived>();"));

        Assert.Equal(["alloc:DerivedController.Post()#object"], run.Targets(run.Region("static:Cache<Order>"), "Last"));
    }

    [Fact]
    public void Growing_generic_recursion_ends_in_a_merged_context_with_an_open_region()
    {
        var run = Solve("""
            public static class Cache<T> { public static object? Last; }
            public static class Grow
            {
                public static void F<T>(object value, int depth) { Cache<T>.Last = value; if (depth > 0) F<System.Collections.Generic.List<T>>(value, depth - 1); }
            }
            public class GrowController : ControllerBase { public void Post() => Grow.F<int>(new object(), 3); }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 2));

        var open = run.Region("static:Cache<T>");
        Assert.True(open.IsOpen);
        Assert.True(open.IsMerged);
        Assert.Contains(run.Instances("body:Fixture:M:Grow.F``1(System.Object,System.Int32)"), instance => instance.IsMerged);
        Assert.Equal(["alloc:GrowController.Post()#object"], run.Targets(open, "Last"));
    }

    [Fact]
    public void Type_initializer_referenced_only_from_an_override_points_to_rules_out_is_not_activated()
    {
        var run = Solve("""
            public static class Limits { public static readonly object Max = new object(); }
            public abstract class Rule { public abstract object Check(); }
            public sealed class MaxRule : Rule { public override object Check() => Limits.Max; }
            public sealed class FreeRule : Rule { public override object Check() => new object(); }
            public class RulesController : ControllerBase { public object Get() { Rule rule = new FreeRule(); return rule.Check(); } }
            """ + Startup());

        Assert.Contains(run.Program.Result.TypeInitializers, candidate => candidate.TypeKey == "Fixture:Limits");
        Assert.DoesNotContain(run.Heap.TypeInitializers, construction => construction.TypeKey == "Fixture:Limits");
        Assert.False(run.Summaries.IsBuilt("body:Fixture:M:Limits.#cctor"));
    }

    [Fact]
    public void Generic_touch_activates_one_type_initializer_per_closed_type()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public class Cache<T> { public static object? Last = new object(); }
            public static class Toucher { public static object? Touch<T>() => Cache<T>.Last; }
            public class TouchController : ControllerBase { public void Post() { Toucher.Touch<Order>(); Toucher.Touch<Invoice>(); } }
            """ + Startup());

        Assert.Equal(["Fixture:Cache<Fixture:Invoice>", "Fixture:Cache<Fixture:Order>"],
                     run.Heap.TypeInitializers.Select(construction => construction.TypeKey).Order(StringComparer.Ordinal));
        Assert.All(run.Heap.TypeInitializers, construction => Assert.NotEmpty(construction.TriggeringInstances));
        Assert.Equal(["alloc:Cache<T>..cctor()#object"], run.Targets(run.Region("static:Cache<Order>"), "Last"));
    }

    [Fact]
    public void Generic_method_group_on_a_closed_type_instantiates_both_substitutions()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public static class Cache<T> { public static object? Last; public static void Put<U>() => Cache<T>.Last = new object(); }
            public sealed class Hooks { public Action? Run; }
            public class HookController(Hooks hooks) : ControllerBase { public void Post() { hooks.Run = Cache<Order>.Put<Invoice>; hooks.Run(); } }
            """ + Startup("services.AddSingleton<Hooks>();"));

        var put = Assert.Single(run.Instances("body:Fixture:M:Cache`1.Put``1"));
        Assert.Equal(["Fixture:Invoice", "Fixture:Order"], put.Substitution.Values.Order(StringComparer.Ordinal));
        Assert.Equal(["alloc:Cache<T>.Put<U>()#object"], run.Targets(run.Region("static:Cache<Order>"), "Last"));
    }

    [Fact]
    public void Expanded_params_call_passes_its_objects_through_the_array_element()
    {
        var run = Solve("""
            public sealed class Holder { public object? First; }
            public class ParamsController : ControllerBase
            {
                public void Post() { var holder = new Holder(); Keep(holder, new Holder(), new object()); }
                private static void Keep(Holder target, params object[] items) => target.First = items[0];
            }
            """ + Startup());

        Assert.Equal(["alloc:ParamsController.Post()#Holder#2", "alloc:ParamsController.Post()#object"],
                     run.Targets(run.Region("alloc:ParamsController.Post()#Holder"), "First"));
        Assert.Equal(["alloc:ParamsController.Post()#Holder#2", "alloc:ParamsController.Post()#object"],
                     run.Targets(run.Region("alloc:ParamsController.Post()#object[]"), "[]"));
    }

    [Fact]
    public void Opaque_call_result_points_to_no_object()
    {
        var run = Solve("""
            public sealed class Holder { public object? Value; }
            public class OpaqueController : ControllerBase { public void Post() { var holder = new Holder(); holder.Value = Activator.CreateInstance(typeof(Holder)); } }
            """ + Startup());

        Assert.Empty(run.Targets(run.Region("alloc:OpaqueController.Post()#Holder"), "Value"));
    }

    [Fact]
    public void Virtual_method_group_on_a_derived_object_runs_only_the_override()
    {
        var run = Solve("""
            public class Base { public virtual void M() { } }
            public sealed class Derived : Base { public override void M() { } }
            public class GroupController : ControllerBase { public void Post() { Base b = new Derived(); Action a = b.M; a(); } }
            """ + Startup());

        Assert.Single(run.Instances("body:Fixture:M:Derived.M"));
        Assert.Empty(run.Instances("body:Fixture:M:Base.M"));
    }

    [Fact]
    public void Cha_only_override_without_an_instance_is_never_summarized()
    {
        var run = Solve(Shapes + Startup());

        Assert.False(run.Summaries.IsBuilt("body:Fixture:M:Square.Draw"));
        Assert.True(run.Summaries.IsBuilt("body:Fixture:M:Circle.Draw"));
        Assert.Equal(run.Heap.ReachableBodies.Count, run.Summaries.Built);
    }

    [Fact]
    public void Interface_property_write_reaches_the_synthesized_setter_and_stores_into_its_backing_field()
    {
        var run = Solve("""
            public interface IState { object? Value { get; set; } }
            public sealed class State : IState { public object? Value { get; set; } }
            public class StateController(IState state) : ControllerBase { public void Post() => state.Value = new object(); }
            """ + Startup("services.AddSingleton<IState, State>();"));

        Assert.Single(run.Instances("body:Fixture:M:State.set_Value(System.Object)"));
        Assert.Equal(["alloc:StateController.Post()#object"], run.Targets(run.Region("di:State@Singleton"), "Value"));
    }

    [Fact]
    public void Open_static_region_of_a_merged_context_and_the_closed_region_see_each_other()
    {
        var run = Solve("""
            public sealed class Order { }
            public sealed class Invoice { }
            public static class Cache<T> { public static object? Last; }
            public sealed class Holder { public object? Seen; }
            public static class Access
            {
                public static void Store<T>(object value) => Cache<T>.Last = value;
                public static object? Load<T>() => Cache<T>.Last;
            }
            public class CacheController : ControllerBase
            {
                public void Post()
                {
                    Access.Store<Order>(new object());
                    Access.Store<Invoice>(new object());
                    var closed = new Holder();
                    closed.Seen = Access.Load<Order>();
                    var open = new Holder();
                    open.Seen = Access.Load<Invoice>();
                }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        Assert.True(run.Region("static:Cache<T>").IsOpen);
        Assert.Contains("alloc:CacheController.Post()#object#2", run.Targets(run.Region("alloc:CacheController.Post()#Holder"), "Seen"));
        Assert.Contains("alloc:CacheController.Post()#object", run.Targets(run.Region("alloc:CacheController.Post()#Holder#2"), "Seen"));
    }

    [Fact]
    public void Primary_constructor_injected_service_pseudo_field_points_to_its_singleton_region()
    {
        var run = Solve("""
            public sealed class Ledger { }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => GC.KeepAlive(ledger); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        Assert.Equal(["di:Ledger@Singleton"], run.Targets(run.Region("receiver:LedgerController"), "ledger"));
    }

    [Fact]
    public void Controller_construction_alone_activates_its_type_initializer()
    {
        var run = Solve("""
            public class PrimedController : ControllerBase { private static readonly object Gate = new object(); public void Get() { } }
            """ + Startup());

        var construction = Assert.Single(run.Heap.TypeInitializers);
        Assert.Equal("Fixture:PrimedController", construction.TypeKey);
        Assert.Equal([run.Region("receiver:PrimedController").Identity], construction.TriggeringRegions);
        Assert.Equal(["alloc:PrimedController..cctor()#object"], run.Targets(run.Region("static:PrimedController"), "Gate"));
    }

    private const string TwoBoxes = """
        public sealed class Box { public object? Value; public void Set(object value) => Value = value; }
        public class BoxController : ControllerBase
        {
            public void Post() { var first = new Box(); var second = new Box(); first.Set(new object()); second.Set(new object()); }
        }
        """;

    private const string Shapes = """
        public abstract class Shape { public abstract void Draw(); }
        public sealed class Circle : Shape { public override void Draw() { } }
        public sealed class Square : Shape { public override void Draw() { } }
        public class ShapeController : ControllerBase { public void Post() { Shape shape = new Circle(); shape.Draw(); } }
        """;

    /// <summary>The points-to facts by display: every region field and its targets.</summary>
    private static string[] Facts(HeapRun run) =>
        run.Heap.Regions.Values
           .SelectMany(region => new[] { "Value", "Last" }.SelectMany(field => run.Targets(region, field).Select(target => $"{region.Display}.{field}->{target}")))
           .Order(StringComparer.Ordinal)
           .ToArray();
}
