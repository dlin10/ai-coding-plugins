using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What a known call's <c>task(…)</c> result gives the run outside the axes of <see cref="TaskResultRunMatrixTests"/> (R4): a
/// task argument handed back, a holder of the result inside one or two tasks, <c>keeps.result</c> under tasks, and a model silent about
/// a task result. The models are project models of a library the run has no source of.</summary>
public sealed class TaskResultRunTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private const string PREFIX = "body:Fixture:M:Facts.";

    private Facts Run => cache.Get("run", Analyze);

    [Fact]
    public void Task_argument_named_as_the_result_is_the_task_itself()
    {
        var task = Single(Roles("SameTask", "t"));
        Assert.Equal(HeapRegionKind.Task, Heap.Regions[task].Kind);
        Assert.Equal(task, Single(Roles("SameTask", "same")));
        Assert.Equal(Run.Shared.Single(), Single(Roles("SameTask", "consumed")));
    }

    [Fact]
    public void Holder_of_an_awaited_task_result_holds_the_delegate_and_runs_it_where_triggered()
    {
        var holder = Single(Roles("HolderTask", "runner"));
        Assert.Contains(holder, Heap.Holders);
        Assert.Equal(HeapRegionKind.Task, Heap.Regions[Single(Roles("HolderTask", "t"))].Kind);
        Assert.True(RunsLambda("HolderTask"));
        Assert.False(RunsLambda("HolderUntriggered"));
    }

    [Theory]
    [InlineData("HolderTaskOfTask")]
    [InlineData("HolderValueTaskOfTask")]
    public void Holder_two_tasks_deep_is_what_the_second_await_yields(string method)
    {
        var outer = Single(Roles(method, "t"));
        var inner = Single(Roles(method, "inner"));
        Assert.Equal(HeapRegionKind.Task, Heap.Regions[outer].Kind);
        Assert.Equal(HeapRegionKind.Task, Heap.Regions[inner].Kind);
        Assert.NotEqual(outer, inner);
        var holder = Single(Roles(method, "runner"));
        Assert.Contains(holder, Heap.Holders);
        Assert.Equal(HeapRegionKind.Allocation, Heap.Regions[holder].Kind);
        Assert.True(RunsLambda(method));
    }

    [Fact]
    public void Kept_result_under_one_task_keeps_into_the_completion_value()
    {
        var created = Single(Roles("KeepsTask", "consumed"));
        Assert.Equal("Fixture:Box", Heap.Regions[created].TypeKey);
        Assert.Equal(Run.Shared.Single(), Assert.Single(Heap.PointsTo(created, PathValue.KEPT)));
        Assert.Empty(Heap.PointsTo(Single(Roles("KeepsTask", "t")), PathValue.KEPT));
    }

    [Fact]
    public void Kept_result_under_two_tasks_keeps_into_the_innermost_completion_value()
    {
        var created = Single(Roles("KeepsTaskOfTask", "consumed"));
        Assert.Equal("Fixture:Box", Heap.Regions[created].TypeKey);
        Assert.Equal(Run.Shared.Single(), Assert.Single(Heap.PointsTo(created, PathValue.KEPT)));
        Assert.Empty(Heap.PointsTo(Single(Roles("KeepsTaskOfTask", "inner")), PathValue.KEPT));
    }

    [Fact]
    public void Model_silent_about_a_task_result_leaves_the_awaited_value_unfollowed()
    {
        Assert.Empty(Roles("Silent", "consumed"));
        Assert.True(TouchUnresolved("Silent"));
        // The same call with a task(…) result is followed: the mark comes from the silence, not from the await.
        Assert.False(TouchUnresolved("Spoken"));
        Assert.Equal(Run.Shared.Single(), Single(Roles("Spoken", "consumed")));
    }

    [Fact]
    public void Each_task_level_of_a_result_is_a_region_of_its_own_carrying_its_depth()
    {
        var outer = Single(Roles("TwoLevels", "t"));
        var inner = Single(Roles("TwoLevels", "inner"));
        Assert.DoesNotContain("@1|", outer, StringComparison.Ordinal);
        Assert.Contains("@1|", inner, StringComparison.Ordinal);
        Assert.Equal(inner, Assert.Single(Heap.Completion(outer)));
        Assert.Equal("Fixture:Box", Heap.Regions[Single(Roles("TwoLevels", "consumed"))].TypeKey);
    }

    /// <summary>A model naming the elements of a task argument's completion value, at any depth, enumerates the iterator the task completes
    /// with where its synchronous twin naming the elements of the argument does (R2, ADR 0011): at the call for a collection, where the
    /// sequence is enumerated for a sequence; and the iterator does not escape.</summary>
    /// <param name="method">The fixture method that calls the model.</param>
    [Theory]
    [InlineData("ListOfSync")]
    [InlineData("ListOfTask")]
    [InlineData("ListOfNestedTask")]
    [InlineData("LazyOfSync")]
    [InlineData("LazyOfTask")]
    public void Iterator_a_model_enumerates_through_a_completion_runs_where_its_twin_runs(string method)
    {
        var callers = Instances(method).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var iterator = Assert.Single(Heap.IteratorObjects, item => callers.Contains(item.Creation.CallerInstance));
        Assert.Contains(Heap.ExecutionEdges, edge => edge.CalleeInstance == iterator.Creation.CalleeInstance && callers.Contains(edge.CallerInstance));
        Assert.DoesNotContain(iterator.RegionId, Heap.UnknownIterators);
    }

    // ---- observing ----

    private sealed record Facts(HeapRun Heap, IReadOnlySet<string> Shared);

    private HeapSolution Heap => Run.Heap.Heap;

    private static string Single(IReadOnlySet<string> regions) => Assert.Single(regions);

    /// <summary>The regions a local of a method of <c>Facts</c> holds.</summary>
    /// <param name="method">The method's name.</param>
    /// <param name="variable">The local's name.</param>
    private IReadOnlySet<string> Roles(string method, string variable) =>
        Instances(method).SelectMany(instance => instance.Summary.Variables.Where(value => value.SymbolKey.Contains($"|{variable}|", StringComparison.Ordinal))
                                                         .SelectMany(value => Heap.Resolve(instance.Id, value.Values)))
                         .ToHashSet(StringComparer.Ordinal);

    private MethodInstance[] Instances(string method) =>
        Heap.Instances.Values.Where(instance => instance.BodyId == PREFIX + method ||
                                                instance.BodyId.StartsWith(PREFIX + method + "#", StringComparison.Ordinal)).ToArray();

    /// <summary>Whether a lambda written in the method runs: an edge of the heap reaches its body.</summary>
    /// <param name="method">The method's name.</param>
    private bool RunsLambda(string method) =>
        Heap.Edges.Any(edge => Heap.Instances[edge.CalleeInstance].BodyId.StartsWith(PREFIX + method + "#", StringComparison.Ordinal));

    private bool TouchUnresolved(string method) =>
        Instances(method).SelectMany(instance => instance.Summary.Calls.Where(call => call.Target.Contains("Box.Touch", StringComparison.Ordinal))
                                                         .Select(call => (instance.Id, call.OperationId)))
                         .Any(Heap.UnresolvedCallTargets.Contains);

    // ---- the fixture ----

    private const string LIBRARY = """
        using System;
        using System.Threading.Tasks;

        namespace TaskLib
        {
            public abstract class Runner { public abstract void Go(object value); }

            public static class Lib
            {
                public static Task<T> Same<T>(Task<T> t) => t;
                public static Task<Runner> MakeAsync(Action<object> action) => null!;
                public static Task<Task<Runner>> MakeNested(Action<object> action) => null!;
                public static ValueTask<Task<Runner>> MakeValueNested(Action<object> action) => default;
                public static Task<T> Made<T>(T x) => null!;
                public static Task<Task<T>> MadeNested<T>(T x) => null!;
                public static Task<T> Silent<T>(T x) => null!;
                public static Task<T> Spoken<T>(T x) => null!;
                public static Task<Task<T>> Levels<T>() => null!;
                public static System.Collections.Generic.List<T> ListOf<T>(Task<System.Collections.Generic.IEnumerable<T>> t) => null!;
                public static System.Collections.Generic.List<T> ListOfNested<T>(Task<Task<System.Collections.Generic.IEnumerable<T>>> t) => null!;
                public static System.Collections.Generic.List<T> ListOfSync<T>(System.Collections.Generic.IEnumerable<T> x) => null!;
                public static System.Collections.Generic.IEnumerable<T> LazyOf<T>(Task<System.Collections.Generic.IEnumerable<T>> t) => null!;
                public static System.Collections.Generic.IEnumerable<T> LazyOfSync<T>(System.Collections.Generic.IEnumerable<T> x) => null!;
            }
        }
        """;

    private static readonly (string Member, string Decision)[] Entries =
    [
        ("Same", "\"result\":\"[arg:t]\""),
        ("MakeAsync", "\"fates\":{\"action\":{\"fate\":\"holder\",\"holder\":\"result\",\"inputs\":[[\"holder-arg:0\"]]}}"),
        ("MakeNested", "\"fates\":{\"action\":{\"fate\":\"holder\",\"holder\":\"result\",\"inputs\":[[\"holder-arg:0\"]]}}"),
        ("MakeValueNested", "\"fates\":{\"action\":{\"fate\":\"holder\",\"holder\":\"result\",\"inputs\":[[\"holder-arg:0\"]]}}"),
        ("Made", "\"result\":\"task(new)\",\"keeps\":{\"result\":[\"arg:x\"]}"),
        ("MadeNested", "\"result\":\"task(task(new))\",\"keeps\":{\"result\":[\"arg:x\"]}"),
        ("Silent", ""),
        ("Spoken", "\"result\":\"task([arg:x])\""),
        ("Levels", "\"result\":\"task(task(new))\""),
        ("ListOf", "\"result\":\"collection(elements(completion(arg:t)))\""),
        ("ListOfNested", "\"result\":\"collection(elements(completion(completion(arg:t))))\""),
        ("ListOfSync", "\"result\":\"collection(elements(arg:x))\""),
        ("LazyOf", "\"result\":\"sequence(elements(completion(arg:t)))\""),
        ("LazyOfSync", "\"result\":\"sequence(elements(arg:x))\"")
    ];

    private const string SOURCE = """
        public class Box { public int Count; public virtual void Touch() { } }
        public static class Boxes { public static readonly Box Shared = new Box(); }

        public sealed class Facts
        {
            public async Task SameTask()
            {
                var t = Task.FromResult(Boxes.Shared);
                var same = TaskLib.Lib.Same(t);
                var consumed = await same;
                consumed.Touch();
            }

            public async Task HolderTask()
            {
                var t = TaskLib.Lib.MakeAsync(value => ((Box)value).Count = 1);
                var runner = await t;
                runner.Go(Boxes.Shared);
            }

            public async Task HolderUntriggered()
            {
                var runner = await TaskLib.Lib.MakeAsync(value => ((Box)value).Count = 2);
                GC.KeepAlive(runner);
            }

            public async Task HolderTaskOfTask()
            {
                var t = TaskLib.Lib.MakeNested(value => ((Box)value).Count = 3);
                var inner = await t;
                var runner = await inner;
                runner.Go(Boxes.Shared);
            }

            public async Task HolderValueTaskOfTask()
            {
                var t = TaskLib.Lib.MakeValueNested(value => ((Box)value).Count = 4);
                var inner = await t;
                var runner = await inner;
                runner.Go(Boxes.Shared);
            }

            public async Task KeepsTask()
            {
                var t = TaskLib.Lib.Made(Boxes.Shared);
                var consumed = await t;
                consumed.Touch();
            }

            public async Task KeepsTaskOfTask()
            {
                var t = TaskLib.Lib.MadeNested(Boxes.Shared);
                var inner = await t;
                var consumed = await inner;
                consumed.Touch();
            }

            public async Task Silent()
            {
                var consumed = await TaskLib.Lib.Silent(Boxes.Shared);
                consumed.Touch();
            }

            public async Task Spoken()
            {
                var consumed = await TaskLib.Lib.Spoken(Boxes.Shared);
                consumed.Touch();
            }

            public async Task TwoLevels()
            {
                var t = TaskLib.Lib.Levels<Box>();
                var inner = await t;
                var consumed = await inner;
                consumed.Touch();
            }

            private static IEnumerable<Box> Iterate() { yield return Boxes.Shared; }

            public void ListOfTask() => GC.KeepAlive(TaskLib.Lib.ListOf(Task.FromResult(Iterate())));

            public void ListOfNestedTask() => GC.KeepAlive(TaskLib.Lib.ListOfNested(Task.FromResult(Task.FromResult(Iterate()))));

            public void ListOfSync() => GC.KeepAlive(TaskLib.Lib.ListOfSync(Iterate()));

            public void LazyOfTask()
            {
                foreach (var item in TaskLib.Lib.LazyOf(Task.FromResult(Iterate())))
                    item.Touch();
            }

            public void LazyOfSync()
            {
                foreach (var item in TaskLib.Lib.LazyOfSync(Iterate()))
                    item.Touch();
            }
        }

        public sealed class FactsController : ControllerBase
        {
            public void ListOfTask() => new Facts().ListOfTask();
            public void ListOfNestedTask() => new Facts().ListOfNestedTask();
            public void ListOfSync() => new Facts().ListOfSync();
            public void LazyOfTask() => new Facts().LazyOfTask();
            public void LazyOfSync() => new Facts().LazyOfSync();
            public async Task SameTask() => await new Facts().SameTask();
            public async Task HolderTask() => await new Facts().HolderTask();
            public async Task HolderUntriggered() => await new Facts().HolderUntriggered();
            public async Task HolderTaskOfTask() => await new Facts().HolderTaskOfTask();
            public async Task HolderValueTaskOfTask() => await new Facts().HolderValueTaskOfTask();
            public async Task KeepsTask() => await new Facts().KeepsTask();
            public async Task KeepsTaskOfTask() => await new Facts().KeepsTaskOfTask();
            public async Task Silent() => await new Facts().Silent();
            public async Task Spoken() => await new Facts().Spoken();
            public async Task TwoLevels() => await new Facts().TwoLevels();
        }
        """;

    private static Facts Analyze()
    {
        var library = TaskModelFixture.Library(LIBRARY);
        var heap = TaskModelFixture.Solve(SOURCE, library, Entries.Select(entry => (TaskModelFixture.Id(library.Compilation, "Lib", entry.Member), entry.Decision)));
        var statics = heap.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey == "Fixture:Boxes").ToArray();
        var shared = statics.SelectMany(region => heap.Heap.PointsTo(region.Identity, "Shared")).ToHashSet(StringComparer.Ordinal);
        Assert.Single(shared);
        return new Facts(heap, shared);
    }
}

/// <summary>A library of task members the run has no source of, compiled as <c>TaskLib</c>, and its project models resolved as a run
/// resolves them; every entry must fit its member.</summary>
internal static class TaskModelFixture
{
    internal const string ASSEMBLY = "TaskLib";

    /// <summary>A compiled library.</summary>
    /// <param name="Compilation">The library's compilation, which names its members.</param>
    /// <param name="Reference">The library as a fixture references it.</param>
    internal sealed record Compiled(CSharpCompilation Compilation, MetadataReference Reference);

    /// <summary>Compiles the library's source.</summary>
    /// <param name="source">The library's source.</param>
    internal static Compiled Library(string source)
    {
        var compilation = CSharpCompilation.Create(ASSEMBLY, [CSharpSyntaxTree.ParseText(source)], StubAssemblies.PlatformWithout([]),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return new Compiled(compilation, MetadataReference.CreateFromImage(stream.ToArray()));
    }

    /// <summary>The documentation id of the one member of a library type with the name.</summary>
    /// <param name="library">The library's compilation.</param>
    /// <param name="type">The type's name in the <c>TaskLib</c> namespace.</param>
    /// <param name="member">The member's name.</param>
    internal static string Id(CSharpCompilation library, string type, string member) =>
        DocumentationCommentId.CreateDeclarationId(library.GetTypeByMetadataName($"{ASSEMBLY}.{type}")!.GetMembers(member).Single())!;

    /// <summary>Solves the heap of a fixture over the library with one model entry per member.</summary>
    /// <param name="source">The fixture's source, without its usings and startup.</param>
    /// <param name="library">The compiled library.</param>
    /// <param name="entries">Each member's documentation id and the decision of its entry, its <c>effects</c> aside unless it names
    /// them.</param>
    internal static HeapRun Solve(string source, Compiled library, IEnumerable<(string Member, string Decision)> entries)
    {
        var text = Usings + "using System.Collections.Generic;\n" + source + Startup();
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [library.Reference] }, ("Case.cs", text));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(20).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error => error.ToString())));
        var models = "{\"schemaVersion\":1,\"assemblies\":[\"" + ASSEMBLY + "\"],\"models\":[" +
                     string.Join(",", entries.Select(entry => "{\"member\":\"" + entry.Member + "\"" +
                                                             (entry.Decision.Contains("\"effects\"", StringComparison.Ordinal) ? "" : ",\"effects\":{}") +
                                                             (entry.Decision.Length == 0 ? "" : "," + entry.Decision) + "}")) + "]}";
        return EngineFixture.Solve(ReachScope(solution, "scope:Fixture", Resolve(compilation, models)));
    }

    private static LibraryModels Resolve(Compilation compilation, string models)
    {
        var root = Directory.CreateTempSubdirectory("ch-task-results-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "tasks.json"), models, new UTF8Encoding(false));
            var files = ProjectModelFiles.Read(root);
            var (resolved, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.True(rejections.Count == 0, string.Join("\n", rejections));
            return resolved;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
