using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed partial class ReachableSetTests
{
    [Fact]
    public void Edges_are_not_recorded_by_default()
    {
        var ordinary = Reach("public class ApiController : ControllerBase { public void Post() => Helper.Go(); } " +
                             "public static class Helper { public static void Go() { } }" + Startup());
        Assert.Empty(ordinary.Result.ReachEdges);
        var recorded = Record(ordinary);
        Assert.Equal(ordinary.Result.ReachedBodies.OrderBy(pair => pair.Key), recorded.Result.ReachedBodies.OrderBy(pair => pair.Key));
        Assert.NotEmpty(recorded.Result.ReachEdges);
    }

    public enum ReachKind { Call, LocalFunction, Dispatch, DeferredDispatch, Delegate, ArrayDelegate, OpaqueDelegate, DynamicDelegate,
                            CollectionFactory, DiFactory, ModelFate, ModelCall, Comparer, Spawn, Thread, Timer, Locator, Construction,
                            ConstructedInitializer }

    [Theory]
    [InlineData(ReachKind.Call)]
    [InlineData(ReachKind.LocalFunction)]
    [InlineData(ReachKind.Dispatch)]
    [InlineData(ReachKind.DeferredDispatch)]
    [InlineData(ReachKind.Delegate)]
    [InlineData(ReachKind.ArrayDelegate)]
    [InlineData(ReachKind.OpaqueDelegate)]
    [InlineData(ReachKind.DynamicDelegate)]
    [InlineData(ReachKind.CollectionFactory)]
    [InlineData(ReachKind.DiFactory)]
    [InlineData(ReachKind.ModelFate)]
    [InlineData(ReachKind.ModelCall)]
    [InlineData(ReachKind.Comparer)]
    [InlineData(ReachKind.Spawn)]
    [InlineData(ReachKind.Thread)]
    [InlineData(ReachKind.Timer)]
    [InlineData(ReachKind.Locator)]
    [InlineData(ReachKind.Construction)]
    [InlineData(ReachKind.ConstructedInitializer)]
    public void Every_kind_of_reach_records_its_edge(ReachKind kind)
    {
        const string POST = "body:Fixture:M:ApiController.Post";
        const string TOUCH = "body:Fixture:M:Target.Touch";
        if (kind == ReachKind.Comparer)
        {
            ComparerEdge();
            return;
        }
        var entry = kind switch
        {
            ReachKind.Call => "Target.Touch();",
            ReachKind.LocalFunction => "Local(); void Local() { }",
            ReachKind.Dispatch or ReachKind.DeferredDispatch => "IWork work = null; work.Go(); _ = new Worker();",
            ReachKind.Delegate => "Action f = Target.Touch; f();",
            ReachKind.ArrayDelegate => "Action[] fs = new Action[1]; fs[0] = Target.Touch;",
            ReachKind.OpaqueDelegate or ReachKind.ModelFate or ReachKind.ModelCall => "Ext.Take(Target.Touch);",
            ReachKind.DynamicDelegate => "dynamic d = null; d.Take((Action)Target.Touch);",
            ReachKind.CollectionFactory => "var d = new System.Collections.Concurrent.ConcurrentDictionary<int, int>(); d.GetOrAdd(1, _ => 2);",
            ReachKind.Spawn => "_ = Task.Run(Target.Touch);",
            ReachKind.Thread => "_ = new Thread(Target.Touch);",
            ReachKind.Timer => "_ = new Timer(_ => Target.Touch(), null, 0, 1);",
            ReachKind.Locator => "_ = Provider.GetRequiredService<Target>();",
            _ => ""
        };
        var registration = kind == ReachKind.DiFactory ? "services.AddSingleton<Target>(_ => new Target());" :
                           kind == ReachKind.Locator ? "services.AddSingleton<Target>();" :
                           kind is ReachKind.Construction or ReachKind.ConstructedInitializer ? "services.AddSingleton<Owner>(); services.AddSingleton<Target>();" : "";
        var parameter = kind is ReachKind.Construction or ReachKind.ConstructedInitializer ? "public ApiController(Owner owner) { }" : "";
        var ordinary = Reach("public interface IWork { void Go(); } public sealed class Worker : IWork { public void Go() { } } " +
                     "public static class Ext { public static extern void Take(Action a); } " +
                     "public sealed class Owner { public Owner(Target target) { } } " +
                     "public sealed class Target { static Target() { } public Target() { } public static void Touch() { } } " +
                     "public class ApiController : ControllerBase { public static IServiceProvider Provider; " + parameter +
                     " public void Post() { " + entry + " } }" + Startup(registration));
        IrBody Rewrite(IrBody body) => body with
        {
            Blocks = body.Blocks.Select(block => block with
            {
                Operations = block.Operations.Select(operation => operation is IrCallOperation call && call.Method.Contains("Ext.Take", StringComparison.Ordinal)
                    ? call with { Library = new IrLibraryCall("fixture", true, [])
                    {
                        Fates = kind == ReachKind.ModelFate ? [new IrLibraryFate(0, IrFateKind.InvokeNow, null, [])] : [],
                        Calls = kind == ReachKind.ModelCall ? [new IrModelCall([TOUCH], new IrModelArgument(0), [])] : []
                    } } : operation).ToArray()
            }).ToArray()
        };
        var run = Record(ordinary, kind is ReachKind.ModelFate or ReachKind.ModelCall ? Rewrite : null,
                         kind == ReachKind.DeferredDispatch);
        if (kind is ReachKind.Construction or ReachKind.ConstructedInitializer)
        {
            var target = kind == ReachKind.Construction ? "body:Fixture:M:Target.#ctor" : "body:Fixture:M:Target.#cctor";
            var from = kind == ReachKind.Construction ? "body:Fixture:M:Owner.#ctor(Target)" : "body:Fixture:M:Target.#ctor";
            Assert.Contains(new ReachEdge(from, null, target), run.Result.ReachEdges);
            return;
        }
        var sourceBody = kind == ReachKind.DiFactory ? "body:Fixture:M:Startup.Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection,Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)" : POST;
        var targetBody = kind switch
        {
            ReachKind.LocalFunction => POST + "#local:Local",
            ReachKind.Dispatch or ReachKind.DeferredDispatch => "body:Fixture:M:Worker.Go",
            ReachKind.CollectionFactory or ReachKind.Timer => POST + "#lambda1",
            ReachKind.DiFactory => sourceBody + "#lambda1",
            ReachKind.Locator => "body:Fixture:M:Target.#ctor",
            _ => TOUCH
        };
        var operations = run.Result.Bodies[sourceBody].Blocks.SelectMany(block => block.Operations).ToArray();
        IrOperation operation = kind switch
        {
            ReachKind.ArrayDelegate => Assert.Single(operations.OfType<IrStoreElementOperation>()),
            ReachKind.Spawn or ReachKind.Thread or ReachKind.Timer => Assert.Single(operations, operation => operation is IrSpawnOperation or IrThreadWorkOperation or IrTimerOperation),
            ReachKind.DynamicDelegate => Assert.Single(operations.OfType<IrUnknownOperation>(), operation => operation.DynamicCallee is not null),
            _ => Assert.Single(operations.OfType<IrCallOperation>(), call => kind switch
            {
                ReachKind.Call => call.TargetMethodId == TOUCH,
                ReachKind.LocalFunction => call.Method == targetBody,
                ReachKind.Dispatch or ReachKind.DeferredDispatch => call.Method.Contains("IWork.Go", StringComparison.Ordinal),
                ReachKind.Delegate => call.CallKind == IrCallKind.Delegate,
                ReachKind.CollectionFactory => call.Method.Contains("GetOrAdd", StringComparison.Ordinal),
                ReachKind.DiFactory => call.Method.Contains("AddSingleton", StringComparison.Ordinal),
                ReachKind.Locator => call.Method.Contains("GetRequiredService", StringComparison.Ordinal),
                _ => call.Method.Contains("Ext.Take", StringComparison.Ordinal)
            })
        };
        var edgeKind = kind switch
        {
            ReachKind.Delegate => ReachEdgeKind.Delegate,
            ReachKind.ModelCall => ReachEdgeKind.Other,
            ReachKind.Spawn or ReachKind.Thread or ReachKind.Timer => ReachEdgeKind.Spawn,
            ReachKind.ArrayDelegate or ReachKind.OpaqueDelegate or ReachKind.DynamicDelegate or ReachKind.CollectionFactory or
            ReachKind.DiFactory or ReachKind.ModelFate => ReachEdgeKind.ArgumentDelegate,
            _ => ReachEdgeKind.Call
        };
        Assert.Contains(new ReachEdge(sourceBody, operation.Id, targetBody) { Kind = edgeKind }, run.Result.ReachEdges);
    }

    [Fact]
    public void A_body_reached_twice_records_both_edges()
    {
        var run = Record(Reach("public class ApiController : ControllerBase { public void Post() { Target.Touch(); Target.Touch(); } } " +
                               "public static class Target { public static void Touch() { } }" + Startup()));
        const string POST = "body:Fixture:M:ApiController.Post";
        var calls = run.Result.Bodies[POST].Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Contains(new ReachEdge(POST, call.Id, call.TargetMethodId!) { Kind = ReachEdgeKind.Call }, run.Result.ReachEdges));
    }

    [Fact]
    public void A_delegate_target_registered_after_its_invocations_records_an_edge_from_each()
    {
        var run = Record(Reach("public static class Target { public static void Touch() { } } " +
            "public class ApiController : ControllerBase { private Action _f; public void Post() { _f(); _f(); Register(); } " +
            "private void Register() { _f = Target.Touch; } }" + Startup()));
        const string POST = "body:Fixture:M:ApiController.Post";
        var invokes = run.Result.Bodies[POST].Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                         .Where(call => call.CallKind == IrCallKind.Delegate).ToArray();
        Assert.Equal(2, invokes.Length);
        Assert.All(invokes, call => Assert.Contains(new ReachEdge(POST, call.Id, "body:Fixture:M:Target.Touch") { Kind = ReachEdgeKind.Delegate }, run.Result.ReachEdges));
    }

    [Fact]
    public void A_trigger_added_to_a_built_construction_records_its_edge()
    {
        var run = Record(Reach("public sealed class Target { public Target() { } } " +
            "public class ApiController : ControllerBase { public void Post(IServiceProvider sp) { " +
            "sp.GetRequiredService<Target>(); sp.GetRequiredService<Target>(); } }" + Startup("services.AddSingleton<Target>();")));
        const string POST = "body:Fixture:M:ApiController.Post(System.IServiceProvider)";
        var calls = run.Result.Bodies[POST].Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                       .Where(call => call.Method.Contains("GetRequiredService", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Contains(new ReachEdge(POST, call.Id, "body:Fixture:M:Target.#ctor") { Kind = ReachEdgeKind.Call }, run.Result.ReachEdges));
    }

    [Fact]
    public void Roots_startup_members_and_root_constructions_have_no_source()
    {
        var run = Record(Reach("public sealed class Target { } public class ApiController : ControllerBase { public void Post() { } }" +
                               Startup("services.AddSingleton<Target>(_ => new Target());")));
        Assert.Contains(new ReachEdge(null, null, "body:Fixture:M:ApiController.Post"), run.Result.ReachEdges);
        Assert.Contains(new ReachEdge(null, null, "body:Fixture:M:ApiController.#ctor"), run.Result.ReachEdges);
        Assert.Contains(run.Result.ReachEdges, edge => edge.SourceBody is null && edge.Operation is null && edge.TargetBody.Contains("Startup.Configure(", StringComparison.Ordinal));
    }

    private static WholeProgramRun Record(WholeProgramRun run, Func<IrBody, IrBody>? rewrite = null, bool constructed = false)
    {
        var bodies = run.Result.Members.ToDictionary(member => member.MemberId, member =>
            (IReadOnlyList<IrBody>)member.NestedBodyIds.Prepend(member.MemberId).Where(run.Result.Bodies.ContainsKey)
                                       .Select(id => rewrite?.Invoke(run.Result.Bodies[id]) ?? run.Result.Bodies[id]).ToArray());
        var input = run.Input with
        {
            Members = id => bodies.GetValueOrDefault(id) ?? run.Input.Members(id),
            Rules = new ReachabilityRules(constructed ? DispatchRule.ConstructedTypes : DispatchRule.ClassHierarchy, new HashSet<string>()) { RecordEdges = true }
        };
        return run with { Input = input, Result = ReachableSet.Build(input) };
    }

    private static void ComparerEdge()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions { ProjectReferences = [("Fixture", "Core")], ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = "System.Private.CoreLib" } },
            ("Core", "Comparer.cs", "namespace System.Collections.Generic { public sealed class ObjectComparer<T> { public ObjectComparer() { } } }"),
            ("Fixture", "Api.cs", Usings + "public static class Ext { public static extern object Get(); } " +
                "public class ApiController : ControllerBase { public void Post() { Ext.Get(); } }" + Startup()));
        var ordinary = ReachScope(solution, "scope:Fixture");
        var run = Record(ordinary, body => body with { Blocks = body.Blocks.Select(block => block with
        {
            Operations = block.Operations.Select(operation => operation is IrCallOperation call && call.Method.Contains("Ext.Get", StringComparison.Ordinal)
                ? call with { DefaultComparer = new IrDefaultComparer(IrComparerKind.Ordering, "Fixture:ApiController") } : operation).ToArray()
        }).ToArray() });
        const string POST = "body:Fixture:M:ApiController.Post";
        var marked = Assert.Single(run.Result.Bodies[POST].Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>(), call => call.DefaultComparer is not null);
        Assert.Contains(new ReachEdge(POST, marked.Id, "body:System.Private.CoreLib:M:System.Collections.Generic.ObjectComparer`1.#ctor") { Kind = ReachEdgeKind.Call }, run.Result.ReachEdges);
    }
}
