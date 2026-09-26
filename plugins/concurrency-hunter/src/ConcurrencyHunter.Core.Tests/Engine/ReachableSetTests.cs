using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ReachableSetTests
{
    [Fact]
    public void Unreferenced_method_and_its_lambda_are_outside_the_set_and_counted()
    {
        var run = Reach("""
            public static class Counters { public static int Value; }
            public class OrdersController : ControllerBase
            {
                public void Post() => Counters.Value = 1;
                private static void Unused() { Counters.Value = 2; Action later = () => Counters.Value = 3; }
            }
            """ + Startup());

        const string UNUSED = "body:Fixture:M:OrdersController.Unused";
        Assert.True(run.Reaches("body:Fixture:M:OrdersController.Post"));
        Assert.DoesNotContain(run.Result.Members, member => member.MemberId == UNUSED);
        var unreached = Assert.Single(run.Result.Unreached, member => member.MemberId == UNUSED);
        Assert.Equal([UNUSED + "#lambda1"], unreached.NestedBodyIds);
        Assert.DoesNotContain(UNUSED, run.LoweredMembers);
    }

    [Fact]
    public void Three_layer_chain_across_two_projects_is_reachable()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Domain")]
            },
            ("Domain", "Inventory.cs", """
                namespace Domain
                {
                    public static class Inventory { public static int Reserved; public static void Reserve() => Store.Save(); }
                    public static class Store { public static void Save() => Inventory.Reserved++; }
                }
                """),
            ("App", "Program.cs", Usings + """
                System.Console.WriteLine();
                public class StockController : ControllerBase { public void Post() => Domain.Inventory.Reserve(); }
                """ + Startup()));

        var run = ReachScope(solution, "scope:App");

        Assert.True(run.Reaches("body:App:M:StockController.Post"));
        Assert.Equal("call:body:App:M:StockController.Post:" + CallId(run, "body:App:M:StockController.Post", "Domain.Inventory.Reserve()"),
                     run.Result.ReachedBodies["body:Domain:M:Domain.Inventory.Reserve"]);
        Assert.True(run.Reaches("body:Domain:M:Domain.Store.Save"));
    }

    [Fact]
    public void Interface_call_reaches_every_implementation()
    {
        var run = Reach("""
            public interface ISink { void Write(); }
            public sealed class FileSink : ISink { public void Write() { } }
            public sealed class MemorySink : ISink { public void Write() { } }
            public class LogController : ControllerBase { public void Post(ISink sink) => sink.Write(); }
            """ + Startup());

        Assert.True(run.Reaches("body:Fixture:M:FileSink.Write"));
        Assert.True(run.Reaches("body:Fixture:M:MemorySink.Write"));
        Assert.Empty(run.Result.OpaqueCalls.GetValueOrDefault("body:Fixture:M:LogController.Post(ISink)") ?? []);
    }

    [Fact]
    public void Controller_root_reaches_its_constructor_chain_with_field_initializers()
    {
        var run = Reach("""
            public static class Seeds { public static int Next() => 1; }
            public abstract class BaseController : ControllerBase
            {
                private readonly int _seed = Seeds.Next();
                protected BaseController() { }
            }
            public class OrdersController : BaseController { public void Get() { } }
            """ + Startup());

        var construction = run.Construction("OrdersController");
        Assert.Equal(ConstructionKind.Controller, construction.Kind);
        Assert.Equal(["body:Fixture:M:OrdersController.#ctor"], construction.ConstructorBodyIds);
        Assert.Equal([new ConstructionTrigger(ConstructionTriggerKind.Root, run.Root("OrdersController.Get()").StableRootId)], construction.Triggers);
        Assert.True(run.Reaches("body:Fixture:M:BaseController.#ctor"));
        Assert.True(run.Reaches("body:Fixture:M:Seeds.Next"));
    }

    [Fact]
    public void Hosted_constructor_its_singleton_and_that_singleton_s_unstored_transient_are_startup_constructions()
    {
        var run = Reach("""
            public sealed class Clock { public Clock() { } }
            public sealed class Cache { public Cache(Clock clock) { GC.KeepAlive(clock); } }
            public sealed class Warmup : BackgroundService
            {
                private readonly Cache _cache;
                public Warmup(Cache cache) => _cache = cache;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """ + Startup("services.AddHostedService<Warmup>(); services.AddSingleton<Cache>(); services.AddTransient<Clock>();"));

        Assert.All(new[] { "Warmup", "Cache", "Clock" }, type => Assert.Equal(ConstructionKind.Startup, run.Construction(type).Kind));
        Assert.Equal([new ConstructionTrigger(ConstructionTriggerKind.Construction, run.Construction("Cache").Id)], run.Construction("Clock").Triggers);
        Assert.Equal(ConstructionTriggerKind.Root, Assert.Single(run.Construction("Warmup").Triggers).Kind);
        Assert.True(run.Reaches("body:Fixture:M:Clock.#ctor"));
    }

    [Fact]
    public void Singleton_injected_into_a_hosted_service_and_a_controller_is_one_startup_construction()
    {
        var run = Reach("""
            public sealed class Ledger { }
            public sealed class Auditor(Ledger ledger) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { GC.KeepAlive(ledger); return Task.CompletedTask; }
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => GC.KeepAlive(ledger); }
            """ + Startup("services.AddHostedService<Auditor>(); services.AddSingleton<Ledger>();"));

        var ledger = run.Construction("Ledger");
        Assert.Equal(ConstructionKind.Startup, ledger.Kind);
        Assert.Equal(ConstructionTriggerKind.Construction, Assert.Single(ledger.Triggers).Kind);
    }

    [Fact]
    public void Transient_resolved_for_a_lazy_singleton_records_that_singleton_as_its_trigger()
    {
        var run = Reach("""
            public sealed class Formatter { }
            public sealed class Catalog(Formatter formatter) { public Formatter Formatter { get; } = formatter; }
            public class CatalogController(Catalog catalog) : ControllerBase { public void Get() => GC.KeepAlive(catalog); }
            """ + Startup("services.AddSingleton<Catalog>(); services.AddTransient<Formatter>();"));

        var catalog = run.Construction("Catalog");
        var formatter = run.Construction("Formatter");
        Assert.Equal(ConstructionKind.LazySingleton, catalog.Kind);
        Assert.Equal(ConstructionKind.ResolvedInExecution, formatter.Kind);
        Assert.Equal([new ConstructionTrigger(ConstructionTriggerKind.Construction, catalog.Id)], formatter.Triggers);
    }

    [Fact]
    public void Singleton_injected_only_into_a_controller_is_a_lazy_singleton_construction()
    {
        var run = Reach("""
            public sealed class Settings { }
            public class SettingsController : ControllerBase
            {
                private readonly Settings _settings;
                public SettingsController(Settings settings) => _settings = settings;
                public void Get() => GC.KeepAlive(_settings);
            }
            """ + Startup("services.AddSingleton<Settings>();"));

        var settings = run.Construction("Settings");
        Assert.Equal(ConstructionKind.LazySingleton, settings.Kind);
        Assert.Equal(ConcurrencyHunter.Di.DiIndex.RegionId("Fixture:Settings", "Fixture:Settings", ConcurrencyHunter.Di.DiLifetime.Singleton, 1),
                     settings.RegionId);
        Assert.Equal([new ConstructionTrigger(ConstructionTriggerKind.Construction, run.Construction("SettingsController").Id)], settings.Triggers);
    }

    [Fact]
    public void Scoped_service_injected_into_a_controller_is_resolved_inside_the_execution()
    {
        var run = Reach("""
            public sealed class Draft { }
            public class DraftController(Draft draft) : ControllerBase { public void Post() => GC.KeepAlive(draft); }
            """ + Startup("services.AddScoped<Draft>();"));

        Assert.Equal(ConstructionKind.ResolvedInExecution, run.Construction("Draft").Kind);
    }

    [Fact]
    public void Type_initializer_is_a_candidate_through_a_static_member_reference()
    {
        var run = Reach("""
            public static class Defaults { public static readonly string Region = Compute(); private static string Compute() => "eu"; }
            public class RegionController : ControllerBase { public string Get() => Defaults.Region; }
            """ + Startup());

        var candidate = Assert.Single(run.Result.TypeInitializers);
        Assert.Equal(("Fixture:Defaults", "body:Fixture:M:Defaults.#cctor"), (candidate.TypeKey, candidate.BodyId));
        Assert.Equal(["body:Fixture:M:RegionController.Get"], candidate.ReferencingBodyIds);
        Assert.True(run.Reaches("body:Fixture:M:Defaults.Compute"));
    }

    [Fact]
    public void Task_run_lambda_is_reached_as_spawn_work_and_not_recorded_as_a_delegate_to_an_opaque_call()
    {
        var run = Reach("""
            public static class Counters { public static int Value; }
            public class JobsController : ControllerBase
            {
                public void Post() { Task.Run(() => Counters.Value = 1); GC.KeepAlive(System.Linq.Enumerable.Select(new[] { 1 }, item => item)); }
            }
            """ + Startup());

        const string POST = "body:Fixture:M:JobsController.Post";
        var spawn = Assert.Single(run.Result.Bodies[POST].Blocks.SelectMany(block => block.Operations).OfType<IrSpawnOperation>());
        Assert.Equal($"spawn:{POST}:{spawn.Id}", run.Result.ReachedBodies[POST + "#lambda1"]);
        // The lambda Select is handed runs in an unknown execution since phase 5b (R3), so it is reached through that call.
        Assert.StartsWith($"call:{POST}:", run.Result.ReachedBodies[POST + "#lambda2"], StringComparison.Ordinal);
        var taskRun = Assert.Single(run.Result.OpaqueCalls[POST], call => call.Callee.StartsWith("System.Threading.Tasks.Task.Run", StringComparison.Ordinal));
        Assert.Empty(taskRun.DelegateTargets);
        var select = Assert.Single(run.Result.OpaqueCalls[POST], call => call.Callee.StartsWith("System.Linq.Enumerable.Select", StringComparison.Ordinal));
        Assert.Equal([POST + "#lambda2"], select.DelegateTargets);
    }

    [Fact]
    public void Delegate_stored_in_a_field_reaches_its_method_group()
    {
        var run = Reach("""
            public class HooksController : ControllerBase
            {
                private readonly Action _hook;
                public HooksController() => _hook = Notify;
                public void Post() => _hook();
                private static void Notify() { }
            }
            """ + Startup());

        Assert.StartsWith("delegate:body:Fixture:M:HooksController.Post:", run.Result.ReachedBodies["body:Fixture:M:HooksController.Notify"],
                          StringComparison.Ordinal);
    }

    [Fact]
    public void Local_function_is_reached()
    {
        var run = Reach("""
            public static class Counters { public static int Value; }
            public class TallyController : ControllerBase
            {
                public void Post() { Bump(); void Bump() => Counters.Value++; }
            }
            """ + Startup());

        Assert.True(run.Reaches("body:Fixture:M:TallyController.Post#local:Bump"));
    }

    [Fact]
    public void Opaque_calls_are_recorded_per_body_with_their_callees()
    {
        var run = Reach("""
            public static class Helper { public static void Log(string text) => Console.WriteLine(text); }
            public class EchoController : ControllerBase { public void Post(string text) { Helper.Log(text); GC.Collect(); } }
            """ + Startup());

        Assert.Equal(["System.GC.Collect()"], run.Result.OpaqueCalls["body:Fixture:M:EchoController.Post(System.String)"].Select(call => call.Callee));
        Assert.Equal(["System.Console.WriteLine(string)"], run.Result.OpaqueCalls["body:Fixture:M:Helper.Log(System.String)"].Select(call => call.Callee));
        Assert.All(run.Result.OpaqueCalls.Values.SelectMany(calls => calls), call => Assert.Empty(call.DelegateValues));
    }

    [Fact]
    public void Type_initializer_referenced_only_from_an_override_is_a_candidate_with_that_override()
    {
        var run = Reach("""
            public static class Limits { public static readonly int Max = Environment.ProcessorCount; }
            public abstract class Rule { public abstract int Check(); }
            public sealed class MaxRule : Rule { public override int Check() => Limits.Max; }
            public class RulesController : ControllerBase { public int Get(Rule rule) => rule.Check(); }
            """ + Startup());

        var candidate = Assert.Single(run.Result.TypeInitializers);
        Assert.Equal(["body:Fixture:M:MaxRule.Check"], candidate.ReferencingBodyIds);
    }

    [Fact]
    public void Generic_body_referencing_its_own_generic_type_gives_one_type_initializer_candidate()
    {
        var run = Reach("""
            public sealed class Order { }
            public sealed class Invoice { }
            public class Cache<T> { public static int Hits = Environment.ProcessorCount; public static void Touch() => Hits++; }
            public class CacheController : ControllerBase
            {
                public void Post() { Cache<Order>.Touch(); Cache<Invoice>.Touch(); }
            }
            """ + Startup());

        var candidate = Assert.Single(run.Result.TypeInitializers);
        Assert.Equal("Fixture:Cache<Fixture:T>", candidate.TypeKey);
        Assert.Equal(["body:Fixture:M:CacheController.Post", "body:Fixture:M:Cache`1.Touch"], candidate.ReferencingBodyIds);
    }

    [Fact]
    public void Virtual_method_group_invoked_through_a_delegate_reaches_every_override()
    {
        var run = Reach("""
            public class Step { public virtual void Run() { } }
            public sealed class FastStep : Step { public override void Run() { } }
            public sealed class SlowStep : Step { public override void Run() { } }
            public class PipelineController : ControllerBase
            {
                public void Post(Step step) { Action run = step.Run; run(); }
            }
            """ + Startup());

        Assert.True(run.Reaches("body:Fixture:M:Step.Run"));
        Assert.True(run.Reaches("body:Fixture:M:FastStep.Run"));
        Assert.True(run.Reaches("body:Fixture:M:SlowStep.Run"));
    }

    [Fact]
    public void Closed_delegate_invocation_reaches_only_its_own_type_while_an_open_creation_still_matches()
    {
        var run = Reach("""
            public static class Handlers
            {
                public static void Number(int value) { }
                public static void Text(string value) { }
                public static void Send<T>(T value) { }
                public static Action<T> Make<T>() => Send<T>;
            }
            public class HooksController : ControllerBase
            {
                public void Post()
                {
                    Action<string> unused = Handlers.Text;
                    GC.KeepAlive(unused);
                    Action<int> hook = Handlers.Number;
                    hook(1);
                }
                public void Put()
                {
                    Action<int> made = Handlers.Make<int>();
                    made(2);
                }
            }
            """ + Startup());

        Assert.True(run.Reaches("body:Fixture:M:Handlers.Number(System.Int32)"));
        Assert.False(run.Reaches("body:Fixture:M:Handlers.Text(System.String)"));
        Assert.True(run.Reaches("body:Fixture:M:Handlers.Send``1(``0)"));
    }

    [Fact]
    public void Factory_registration_has_no_construction_and_is_recorded()
    {
        var run = Reach("""
            public sealed class Rates { }
            public class RatesController(Rates rates) : ControllerBase { public void Get() => GC.KeepAlive(rates); }
            """ + Startup("services.AddSingleton<Rates>(_ => new Rates());"));

        Assert.Empty(run.Constructions("Rates"));
        var unanalysed = Assert.Single(run.Result.UnanalysedRegistrations);
        Assert.Equal(ConcurrencyHunter.Di.DiIndex.RegionId("Fixture:Rates", "Fixture:Rates", ConcurrencyHunter.Di.DiLifetime.Singleton, 1),
                     unanalysed.RegionId);
    }

    private static int CallId(WholeProgramRun run, string bodyId, string method) =>
        run.Result.Bodies[bodyId].Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().Single(call => call.Method == method).Id;
}
