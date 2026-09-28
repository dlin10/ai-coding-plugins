using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A delegate a known call's <c>holder</c> fate keeps runs wherever a member of its holder is called without a body of its
/// own, handed that call's arguments as the model says; the heap follows the holder wherever it goes, and one it cannot follow any
/// more runs what it keeps in an unknown execution as well (R4). The models are project models of a library the run has no source of,
/// and the built-in model of <c>Comparer&lt;T&gt;.Create</c>.</summary>
public sealed class HolderFateTests
{
    private const string PRIMARY = "alloc:State..ctor()#Primary";

    [Fact]
    public void Holder_of_the_result_is_what_the_call_returns()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);\nHolders.Lib.Keep(runner);");

        var holder = Assert.Single(WorkerHolders(run));
        Assert.Equal(HeapRegionKind.Allocation, Heap(run).Regions[holder].Kind);
        Assert.Contains("Holders.Runner", Heap(run).Regions[holder].TypeKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Holder_of_the_result_runs_its_delegate_where_a_member_is_called()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);\nrunner.Go(null!);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Holders.", StringComparison.Ordinal));
    }

    [Fact]
    public void Holder_member_called_through_an_interface_runs_the_delegate()
    {
        var run = Run("Holders.IRunner runner = Holders.Lib.Make(value => _state.Count = 1);\nrunner.Go(null!);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Holders.", StringComparison.Ordinal));
    }

    [Fact]
    public void Holder_member_called_from_two_requests_runs_in_both()
    {
        // The singleton keeps the holder it made at construction; the action and the worker each call its member, so the delegate runs
        // in both executions and its increments meet.
        var run = Run("_state.Shared.Go(null!);", action: "_state.Shared.Go(null!);");

        var increments = run.Run.Of("Tally");
        Assert.Equal(2, increments.Select(access => access.ExecutionId).Distinct().Count());
        Assert.All(increments, access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
        Assert.NotEmpty(run.Run.PairsOn("Tally"));
    }

    [Fact]
    public void Holder_argument_inputs_are_the_called_members_arguments()
    {
        var run = Run("var runner = Holders.Lib.Make(value => ((Item)value).Hits = 1);\nrunner.Go(_state.First);");

        Assert.Equal([PRIMARY], Written(run, "value => ((Item)value).Hits = 1"));
    }

    [Fact]
    public void Holder_member_with_fewer_arguments_hands_nothing_known()
    {
        var run = Run("var keeper = new Holders.Keeper(value => ((Item)value).Hits = 1);\nkeeper.Nudge();");

        Assert.Empty(Written(run, "value => ((Item)value).Hits = 1"));
        Assert.Single(WorkerHolders(run));
    }

    [Fact]
    public void Holder_of_the_receiver_runs_its_delegate_where_a_member_of_the_receiver_is_called()
    {
        var run = Run("var box = new Holders.Box();\nbox.Watch(value => { _state.Count = 1; return value; });\nbox.Poke(null!);");

        Assert.Contains(WorkerHolders(run), holder => Heap(run).Regions[holder].TypeKey?.Contains("Holders.Box", StringComparison.Ordinal) == true);
        Assert.All(Worker(run, "Count"), access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
        Assert.NotEmpty(Worker(run, "Count"));
    }

    [Fact]
    public void Holder_receiver_without_an_object_is_an_unknown_execution()
    {
        var run = Run("Holders.Lib.Existing().Watch(value => { _state.Count = 1; return value; });");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        Assert.Empty(WorkerHolders(run));
        AssertKnown(run, "Holders.Box.Watch");
    }

    [Fact]
    public void Holder_with_an_unresolved_delegate_is_a_dispatch_at_each_member_call()
    {
        var run = Run("var runner = Holders.Lib.Make(Holders.Lib.Unknown());\nrunner.Go(_state.First);\nrunner.Go(_state.First);");

        Assert.Equal(2, Heap(run).UnresolvedDispatches.Count(dispatch => IsWorker(run, dispatch.Instance)));
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == PRIMARY &&
                                                       access.Source.StartLine == Line(run.Text, "runner.Go(_state.First)"));
    }

    [Fact]
    public void Holder_receiver_without_an_object_and_a_delegate_without_one_is_a_dispatch()
    {
        // Review F-0067: neither the holder nor the delegate object is known, so the unknown execution is an unresolved dispatch.
        var run = Run("Holders.Lib.Existing().Watch(Holders.Lib.UnknownCallback());");

        Assert.Single(Heap(run).UnresolvedDispatches, dispatch => IsWorker(run, dispatch.Instance));
    }

    [Fact]
    public void Holder_receiver_without_an_object_and_a_method_group_without_a_body_is_a_dispatch()
    {
        // Review F-0067: the delegate object is known, but its target runs no body: an interface method on a receiver nothing set.
        var run = Run("Holders.Lib.Existing().Watch(_state.Echoer!.Echo);");

        Assert.Single(Heap(run).UnresolvedDispatches, dispatch => IsWorker(run, dispatch.Instance));
    }

    [Fact]
    public void Holder_with_an_unresolved_delegate_handed_to_a_call_without_a_body_is_a_dispatch_there()
    {
        // Review F-0068: the escaped holder's unresolved delegate is a dispatch where it escapes, as its resolved ones run there.
        var run = Run("var runner = Holders.Lib.Make(Holders.Lib.Unknown());\nHolders.Lib.Keep(runner);");

        Assert.Single(Heap(run).UnresolvedDispatches, dispatch => IsWorker(run, dispatch.Instance));
    }

    [Fact]
    public void Holder_in_a_field_of_a_source_object_is_followed()
    {
        var run = Run("_state.Kept = Holders.Lib.Make(value => _state.Count = 1);\n_state.UseKept();");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Holder_passed_as_a_parameter_is_followed()
    {
        var run = Run("State.Use(Holders.Lib.Make(value => _state.Count = 1));");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Holder_returned_from_a_source_method_is_followed()
    {
        var run = Run("_state.Made().Go(null!);");

        var write = Assert.Single(run.Run.Of("Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Holder_in_an_array_cell_is_followed()
    {
        var run = Run("var cells = new Holders.Runner[] { Holders.Lib.Make(value => _state.Count = 1) };\ncells[0].Go(null!);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Holder_in_a_list_is_followed()
    {
        // The list's own member is a call without a body the holder is handed to, so the holder also escapes (R4); the member called on
        // what the list hands out still runs the delegate where it is called.
        var run = Run("var list = new List<Holders.Runner>();\nlist.Add(Holders.Lib.Make(value => _state.Count = 1));\nlist[0].Go(null!);");

        Assert.Contains(Worker(run, "Count"), access => KindOf(run, access) == ExecutionKind.Root);
    }

    [Fact]
    public void Holder_captured_by_a_lambda_is_followed()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);\nAction go = () => runner.Go(null!);\ngo();");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Holder_handed_to_a_call_without_a_body_adds_an_unknown_execution()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);\nHolders.Lib.Keep(runner);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        AssertKnown(run, "Holders.Lib.Make");
    }

    [Fact]
    public void Holder_inside_an_array_handed_to_a_call_without_a_body_adds_an_unknown_execution()
    {
        var run = Run("Holders.Lib.Keep(new object[] { Holders.Lib.Make(value => _state.Count = 1) });");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        AssertKnown(run, "Holders.Lib.Make");
    }

    [Fact]
    public void Holder_given_as_a_timers_state_adds_an_unknown_execution()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);\n" +
                      "_ = new Timer(_ => { }, runner, Timeout.Infinite, Timeout.Infinite);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        AssertKnown(run, "Holders.Lib.Make");
    }

    [Fact]
    public void Holder_captured_by_a_delegate_run_in_an_unknown_execution_adds_an_unknown_execution()
    {
        var run = Run("object kept = Holders.Lib.Make(value => _state.Count = 1);\nHolders.Lib.Later(() => { var held = kept; });");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        AssertKnown(run, "Holders.Lib.Make");
    }

    [Fact]
    public void Holder_whose_members_nobody_calls_runs_its_delegate_nowhere()
    {
        var run = Run("var runner = Holders.Lib.Make(value => _state.Count = 1);");

        Assert.Single(WorkerHolders(run));
        Assert.Empty(run.Run.Of("Count"));
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Constructor_holder_is_the_created_object()
    {
        var run = Run("var keeper = new Holders.Keeper(value => ((Item)value).Hits = 1);\nkeeper.Touch(_state.First);");

        var holder = Assert.Single(WorkerHolders(run));
        Assert.Contains("Holders.Keeper", Heap(run).Regions[holder].TypeKey, StringComparison.Ordinal);
        Assert.Equal([PRIMARY], Written(run, "value => ((Item)value).Hits = 1"));
    }

    [Fact]
    public void Comparer_Create_runs_its_comparison_at_Compare()
    {
        var run = Run("var order = Comparer<int>.Create((a, b) => { _state.Count = 1; return 0; });\norder.Compare(1, 2);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.Contains("Comparer", StringComparison.Ordinal));
    }

    [Fact]
    public void Comparer_Create_holder_handed_to_List_Sort_is_an_unknown_execution()
    {
        var run = Run("var order = Comparer<int>.Create((a, b) => { _state.Count = 1; return 0; });\n_state.Numbers.Sort(order);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        AssertKnown(run, "System.Collections.Generic.Comparer");
    }

    // ---- helpers ----

    /// <summary>A run with the text of the case file it analysed.</summary>
    private sealed record Case(EngineRun Run, string Text);

    private static HeapSolution Heap(Case run) => run.Run.Execution.Heap.Heap;

    /// <summary>The call that made the holder is known: no gap names it, so an unknown execution of its delegate comes from where the
    /// holder went, not from the call itself.</summary>
    private static void AssertKnown(Case run, string callee) =>
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith(callee, StringComparison.Ordinal));

    /// <summary>The holders the worker creates, without the one the singleton makes at construction.</summary>
    private static string[] WorkerHolders(Case run) =>
        Heap(run).Holders.Where(holder => Heap(run).Regions[holder].Display.Contains("Worker.", StringComparison.Ordinal)).ToArray();

    private static ExecutionKind KindOf(Case run, Access access) => run.Run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static bool IsWorker(Case run, string instanceId) =>
        Heap(run).Instances[instanceId].BodyId.Contains("Worker.ExecuteAsync", StringComparison.Ordinal);

    private static IReadOnlyList<Access> Worker(Case run, string member) =>
        run.Run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>The objects the worker writes <c>Hits</c> of at the line containing <paramref name="at"/>.</summary>
    private static string[] Written(Case run, string at) =>
        Worker(run, "Hits").Where(access => access.Operation == AccessOperation.Write && access.Source.StartLine == Line(run.Text, at))
                           .Select(access => access.Resource.Region)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToArray();

    private static int Line(string file, string text) =>
        Array.FindIndex(file.Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static Case Run(string work, string action = "")
    {
        var text = Usings + Source(work, action);
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", text));
        return new Case(AnalyzeScope(solution, "scope:Fixture", Models(solution)), text);
    }

    /// <summary>The library's project models, resolved as a run resolves them; every entry must fit its member.</summary>
    private static LibraryModels Models(Solution solution)
    {
        var root = Directory.CreateTempSubdirectory("ch-holder-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "holders.json"), ModelFile(), new UTF8Encoding(false));
            var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
            var files = ProjectModelFiles.Read(root);
            var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.Empty(rejections);
            return models;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ModelFile()
    {
        string Entry(string type, string name, string decision) =>
            "{\"member\":\"" + Id(type, name) + "\",\"effects\":{}," + decision + "}";
        return "{\"schemaVersion\":1,\"assemblies\":[\"Holders\"],\"models\":[" + string.Join(",",
            Entry("Lib", "Make", """ "fates":{"action":{"fate":"holder","holder":"result","inputs":[["holder-arg:0"]]}}"""),
            Entry("Keeper", ".ctor", """ "fates":{"action":{"fate":"holder","holder":"result","inputs":[["holder-arg:0"]]}}"""),
            Entry("Box", "Watch", """ "fates":{"callback":{"fate":"holder","holder":"this","inputs":[["holder-arg:0"]]}}""")) +
            "]}";
    }

    private static string Id(string type, string name) =>
        DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Holders." + type)!.GetMembers(name).Single())!;

    /// <summary>A singleton <c>State</c> a worker does <paramref name="work"/> on, one statement a line, while a controller's action
    /// does <paramref name="action"/>. The singleton keeps a holder it made at construction, whose delegate increments <c>Tally</c>.</summary>
    private static string Source(string work, string action) => $$"""
        using System.Collections.Generic;

        public class Item { public int Hits; }
        public sealed class Primary : Item { }
        public interface IEcho { object Echo(object value); }

        public sealed class State
        {
            public readonly Primary First = new Primary();
            public readonly List<int> Numbers = new();
            public readonly Holders.Runner Shared;
            public Holders.Runner? Kept;
            public IEcho? Echoer;
            public int Count;
            public int Tally;

            public State() => Shared = Holders.Lib.Make(value => Tally++);

            public void UseKept() => Kept!.Go(null!);

            public static void Use(Holders.Runner runner) => runner.Go(null!);

            public Holders.Runner Made() => Holders.Lib.Make(value => Count = 1);
        }

        [ApiController]
        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;

            [HttpPost("/state")]
            public void Post()
            {
                {{action}}
            }
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Holders", [CSharpSyntaxTree.ParseText("""
        using System;

        namespace Holders
        {
            public interface IRunner { void Go(object value); }

            public abstract class Runner : IRunner
            {
                public abstract void Go(object value);
            }

            public sealed class Keeper
            {
                public Keeper(Action<object> action) { }
                public void Touch(object value) { }
                public void Nudge() { }
            }

            public sealed class Box
            {
                public void Watch(Func<object, object> callback) { }
                public object Poke(object value) => value;
            }

            public static class Lib
            {
                public static Runner Make(Action<object> action) => null!;
                public static Box Existing() => null!;
                public static Action<object> Unknown() => null!;
                public static Func<object, object> UnknownCallback() => null!;
                public static void Keep(object value) { }
                public static void Later(Action work) { }
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
