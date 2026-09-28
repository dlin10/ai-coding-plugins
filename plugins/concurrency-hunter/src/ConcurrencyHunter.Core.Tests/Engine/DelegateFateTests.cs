using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A delegate a known call receives runs as its model's fate says (R3): <c>invoke-now</c> at the call, in the caller's
/// execution and under its locks; <c>startup</c> in startup for a call startup makes and in an unknown execution otherwise;
/// <c>unknown-execution</c> in an unknown execution without a gap. Each is handed what its inputs name, and the call returns what its
/// result says. The models are project models of a library the run has no source of.</summary>
public sealed class DelegateFateTests
{
    private const string PRIMARY = "alloc:State..ctor()#Primary";
    private const string SECONDARY = "alloc:State..ctor()#Secondary";

    [Fact]
    public void Invoke_now_enumerates_a_user_iterator_argument_at_the_call()
    {
        // The iterator's body runs where the call enumerates it, in the worker's execution, and in no unknown enumeration.
        var run = Run("Fates.Lib.Each(_state.Produce(), item => item.Hits = 1);");

        var write = Assert.Single(run.Run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.Contains(run.Run.Execution.Heap.Heap.ExecutionEdges, edge => IsWorker(run, edge.CallerInstance) && edge.Reason == "iterator-enumeration");
    }

    [Fact]
    public void Startup_fate_delegate_is_handed_its_inputs()
    {
        var run = Run("", startup: "var given = new Primary(); Fates.Lib.OnStart(given, value => ((Item)value).Hits = 1);");

        var write = Assert.Single(run.Run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(ExecutionModel.STARTUP, write.ExecutionId);
        Assert.EndsWith("#Primary", write.Resource.Region, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_execution_fate_delegate_is_handed_its_inputs()
    {
        var run = Run("Fates.Lib.Later(_state.First, value => ((Item)value).Hits = 1);");

        var write = Assert.Single(Worker(run, "Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(PRIMARY, write.Resource.Region);
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
    }

    [Fact]
    public void Invoke_now_runs_the_delegate_in_the_caller()
    {
        var run = Run("Fates.Lib.Run(() => _state.Count = 1);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(AccessOperation.Write, write.Operation);
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
    }

    [Fact]
    public void Invoke_now_delegate_is_under_the_callers_lock()
    {
        var run = Run("lock (_state.Gate) { Fates.Lib.Run(() => _state.Count = 1); }", other: "lock (_state.Gate) { _state.Count = 2; }");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.NotEmpty(write.HeldProtection);
        Assert.Empty(run.Run.PairsOn("Count"));
    }

    [Fact]
    public void Invoke_now_makes_no_gap_and_no_unknown_execution()
    {
        var run = Run("Fates.Lib.Run(() => _state.Count = 1);");

        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Fates.Lib", StringComparison.Ordinal));
        Assert.Equal(Run("").Run.Counter(CoverageCounters.SEMANTIC_GAP), run.Run.Counter(CoverageCounters.SEMANTIC_GAP));
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Invoke_now_delegate_is_not_kept_after_the_call()
    {
        // Nothing keeps the delegate: it runs at the call and nowhere after it, so no execution runs it again later.
        var run = Run("Action work = () => _state.Count = 1; Fates.Lib.Run(work);");

        Assert.All(run.Run.Of("Count"), access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
        Assert.Empty(run.Run.Execution.Heap.Heap.DelegateHandoffs);
        Assert.Empty(run.Run.Execution.Heap.Heap.StartupDelegates);
    }

    [Fact]
    public void Invoke_now_inputs_are_the_elements_of_a_list_argument()
    {
        var run = Run("Fates.Lib.Each(_state.Items, item => item.Hits = 1);");

        Assert.Equal([PRIMARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Invoke_now_inputs_are_the_cells_of_an_array_argument()
    {
        var run = Run("Fates.Lib.Each(_state.Cells, item => item.Hits = 1);");

        Assert.Equal([SECONDARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Invoke_now_input_of_an_unknown_sequence_is_every_compatible_object()
    {
        // A sequence of the run's own is enumerated by a body the call never runs: it may yield any object it reaches of the type it
        // enumerates, and nothing of another type.
        var run = Run("Fates.Lib.EachShape(_state.Shapes, shape => ((Item)shape).Hits = 1);\n" +
                      "Fates.Lib.EachShape(_state.Shapes, shape => ((Other)(object)shape).Hits = 2);");

        Assert.Equal(["alloc:Shapes..ctor()#Circle"], Written(run, "Hits = 1"));
        Assert.DoesNotContain("alloc:Shapes..ctor()#Other", Written(run, "Hits = 2"));
    }

    [Fact]
    public void Input_from_another_delegates_return()
    {
        var run = Run("Fates.Lib.Pipe(() => _state.First, value => ((Item)value).Hits = 1);");

        Assert.Equal([PRIMARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Delegate_handed_its_own_returns_gets_every_run()
    {
        // The accumulator is handed the seed and what any of its runs returned: the analysis does not order a delegate's runs.
        var run = Run("Fates.Lib.Fold(_state.First, value => { ((Item)value).Hits = 1; return _state.Second; });");

        Assert.Equal([PRIMARY, SECONDARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Input_the_model_leaves_empty_is_handed_nothing()
    {
        var run = Run("Fates.Lib.Skip(_state.First, value => ((Item)value).Hits = 1);");

        Assert.Empty(Written(run, "Hits = 1"));
        // The same delegate handed the argument by the model writes it.
        Assert.Equal([PRIMARY], Written(Run("Fates.Lib.Visit(_state.First, value => ((Item)value).Hits = 1);"), "Hits = 1"));
    }

    [Fact]
    public void Delegate_argument_without_a_delegate_object_is_an_unresolved_dispatch()
    {
        // As a call of the delegate would be: nothing is known of what it runs, and it sees the object it is handed.
        var run = Run("Fates.Lib.Visit(_state.First, Fates.Lib.Unknown());");

        Assert.Contains(run.Run.Execution.Heap.Heap.UnresolvedDispatches, dispatch => IsWorker(run, dispatch.Instance));
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == PRIMARY &&
                                                       access.Source.StartLine == Line(run.Text, "Fates.Lib.Visit"));
    }

    [Fact]
    public void Startup_fate_runs_in_startup()
    {
        var run = Run("", startup: "Fates.Lib.OnStart(null!, _ => Totals.Sum = 1);");

        var write = Assert.Single(run.Run.Of("Sum"));
        Assert.Equal(ExecutionModel.STARTUP, write.ExecutionId);
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Startup_fate_called_outside_startup_runs_in_an_unknown_execution()
    {
        // Startup has ended for a call a root or a hosted service makes, so the delegate runs, for that call, as an unknown-execution one.
        var run = Run("Fates.Lib.OnStart(_state.First, value => _state.Count = 1);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Of("Count"), access => access.ExecutionId == ExecutionModel.STARTUP);
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Fates.Lib", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_execution_fate_runs_in_an_unknown_execution_without_a_gap()
    {
        var run = Run("Fates.Lib.Later(_state.First, value => _state.Count = 1);");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Fates.Lib", StringComparison.Ordinal));
        Assert.Equal(Run("").Run.Counter(CoverageCounters.SEMANTIC_GAP), run.Run.Counter(CoverageCounters.SEMANTIC_GAP));
    }

    [Fact]
    public void Unknown_execution_fate_handed_once_by_a_once_running_execution_does_not_overlap_itself()
    {
        var run = Run("Fates.Lib.Later(_state.First, value => _state.Count = 1);");

        var execution = Assert.Single(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
        Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.Serialized, ""), execution.Policy);
        Assert.Empty(run.Run.PairsOn("Count"));
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Fates.Lib", StringComparison.Ordinal));
    }

    [Fact]
    public void Collection_result_holds_the_elements()
    {
        var run = Run("var list = Fates.Lib.Collect(_state.Items);\nlist[0].Hits = 1;");

        Assert.Equal([PRIMARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Array_result_holds_the_elements()
    {
        var run = Run("Fates.Lib.ToArray(_state.Items)[0].Hits = 1;");

        Assert.Equal([PRIMARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Dictionary_result_holds_keys_and_values_apart()
    {
        var run = Run("var map = Fates.Lib.Map(_state.Items, item => _state.Second);\n" +
                      "foreach (var pair in map) pair.Key.Hits = 1;\n" +
                      "foreach (var pair in map) pair.Value.Hits = 2;");

        Assert.Equal([SECONDARY], Written(run, "Hits = 1"));
        Assert.Equal([PRIMARY], Written(run, "Hits = 2"));
    }

    [Fact]
    public void Element_result_is_one_of_the_elements()
    {
        var run = Run("Fates.Lib.Pick(_state.Items, item => true).Hits = 1;");

        Assert.Equal([PRIMARY], Written(run, "Hits = 1"));
    }

    [Fact]
    public void Known_call_with_fates_counts_as_known_and_not_as_delegate_to_opaque()
    {
        var run = Run("Fates.Lib.Run(() => _state.Count = 1);");
        var none = Run("");

        Assert.Equal(none.Run.Counter(CoverageCounters.KNOWN_CALL_PROJECT) + 1, run.Run.Counter(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(none.Run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE), run.Run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Equal(none.Run.Counter(CoverageCounters.OPAQUE_CALL), run.Run.Counter(CoverageCounters.OPAQUE_CALL));
    }

    // ---- helpers ----

    /// <summary>A run with the text of the case file it analysed.</summary>
    private sealed record Case(EngineRun Run, string Text);

    private static ExecutionKind KindOf(Case run, Access access) => run.Run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    /// <summary>Whether an instance runs the worker's own body.</summary>
    private static bool IsWorker(Case run, string instanceId) =>
        run.Run.Execution.Heap.Heap.Instances[instanceId].BodyId.Contains("Worker.ExecuteAsync", StringComparison.Ordinal);

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

    private static Case Run(string work, string other = "", string startup = "")
    {
        var text = Usings + Source(work, other, startup);
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", text));
        return new Case(AnalyzeScope(solution, "scope:Fixture", Models(solution)), text);
    }

    /// <summary>The library's project models, resolved as a run resolves them; every entry must fit its member.</summary>
    private static LibraryModels Models(Solution solution)
    {
        var root = Directory.CreateTempSubdirectory("ch-fate-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "fates.json"), ModelFile(), new UTF8Encoding(false));
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
        string Entry(string member, string decision) => "{\"member\":\"" + Id(member) + "\",\"effects\":{}," + decision + "}";
        const string EACH = """
            "fates":{"action":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}
            """;
        const string VALUE = """
            "fates":{"action":{"fate":"invoke-now","inputs":[["arg:value"]]}}
            """;
        return "{\"schemaVersion\":1,\"assemblies\":[\"Fates\"],\"models\":[" + string.Join(",",
            Entry("Each", EACH),
            Entry("EachShape", EACH),
            Entry("Run", """ "fates":{"action":{"fate":"invoke-now"}}"""),
            Entry("Visit", VALUE),
            Entry("Skip", """ "fates":{"action":{"fate":"invoke-now"}}"""),
            Entry("Pipe", """ "fates":{"make":{"fate":"invoke-now"},"use":{"fate":"invoke-now","inputs":[["returns:make"]]}}"""),
            Entry("Fold", """ "result":"[arg:seed,returns:step]","fates":{"step":{"fate":"invoke-now","inputs":[["arg:seed","returns:step"]]}}"""),
            Entry("OnStart", VALUE.Replace("invoke-now", "startup")),
            Entry("Later", VALUE.Replace("invoke-now", "unknown-execution")),
            Entry("Collect", """ "result":"collection(elements(arg:source))" """),
            Entry("ToArray", """ "result":"collection(elements(arg:source))" """),
            Entry("Map", """ "result":"dictionary(returns:key,elements(arg:source))","fates":{"key":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Pick", """ "result":"[elements(arg:source)]","fates":{"predicate":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}""")) +
            "]}";
    }

    /// <summary>The declaration id of the one member of <c>Fates.Lib</c> with a name.</summary>
    private static string Id(string name) =>
        DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Fates.Lib")!.GetMembers(name).Single())!;

    /// <summary>A singleton <c>State</c> a worker does <paramref name="work"/> on while a reader does <paramref name="other"/>;
    /// <paramref name="startup"/> runs in <c>Configure</c>, which its factory registration makes a startup member.</summary>
    private static string Source(string work, string other, string startup) => $$"""
        using System.Collections;
        using System.Collections.Generic;

        public class Item { public int Hits; }
        public sealed class Primary : Item { }
        public sealed class Secondary : Item { }
        public sealed class Circle : Item, Fates.IShape { }
        public sealed class Other { public int Hits; }
        public static class Totals { public static int Sum; }
        public sealed class Marker { }

        public sealed class Shapes : IEnumerable<Fates.IShape>
        {
            public readonly Circle Circle = new Circle();
            public readonly Other Other = new Other();
            public IEnumerator<Fates.IShape> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class State
        {
            public readonly Primary First = new Primary();
            public readonly Secondary Second = new Secondary();
            public readonly List<Item> Items = new();
            public readonly Item[] Cells;
            public readonly Shapes Shapes = new Shapes();
            public readonly object Gate = new();
            public int Count;

            public State()
            {
                Items.Add(First);
                Cells = new Item[] { Second };
            }

            public IEnumerable<Item> Produce()
            {
                Count = 1;
                yield return First;
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

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Marker>(_ => new Marker()); " +
                      "services.AddHostedService<Worker>(); services.AddHostedService<Reader>(); " + startup);

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Fates", [CSharpSyntaxTree.ParseText("""
        using System;
        using System.Collections.Generic;

        namespace Fates
        {
            public interface IShape { }

            public static class Lib
            {
                public static void Each<T>(IEnumerable<T> source, Action<T> action) { }
                public static void EachShape(IEnumerable<IShape> source, Action<IShape> action) { }
                public static void Run(Action action) { }
                public static void Visit(object value, Action<object> action) { }
                public static void Skip(object value, Action<object> action) { }
                public static void Pipe(Func<object> make, Action<object> use) { }
                public static object Fold(object seed, Func<object, object> step) => seed;
                public static void OnStart(object value, Action<object> action) { }
                public static void Later(object value, Action<object> action) { }
                public static List<T> Collect<T>(IEnumerable<T> source) => null!;
                public static T[] ToArray<T>(IEnumerable<T> source) => null!;
                public static Dictionary<T, T> Map<T>(IEnumerable<T> source, Func<T, T> key) where T : notnull => null!;
                public static T Pick<T>(IEnumerable<T> source, Func<T, bool> predicate) => default!;
                public static Action<object> Unknown() => null!;
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
