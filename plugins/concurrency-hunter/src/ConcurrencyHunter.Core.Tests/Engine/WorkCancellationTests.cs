using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class WorkCancellationTests
{
    private const string SOURCE = """
        public sealed class Box
        {
            public int Value;
            public void F() { Value = 1; }
            public void P() { Value = 2; }
        }
        public sealed class SampleController : ControllerBase
        {
            public static readonly object Shared = new object();
            [HttpPost("/work")]
            public void Post(Task unknown)
            {
                var box = new Box();
                var task = Task.Run(() => box.F());
                unknown.Wait();
                task.Wait();
                box.P();
            }
        }
        """;

    private static readonly Lazy<HeapRun> Solved = new(() => EngineFixture.Solve(SOURCE + EngineFixture.Startup()));
    private static readonly Lazy<HeapRun> AsyncSolved = new(() => EngineFixture.Solve(
        SOURCE.Replace("unknown.Wait();", "Wait(unknown);", StringComparison.Ordinal)
              .Replace("public void Post(Task unknown)", "private async Task Wait(Task unknown) { await unknown; } public void Post(Task unknown)", StringComparison.Ordinal) +
        EngineFixture.Startup()));
    private static readonly Lazy<IReadOnlyList<Access>> AsyncAccesses = new(() => EngineFixture.Collect(EngineFixture.Execute(AsyncSolved.Value)).Accesses);
    private static readonly Lazy<IReadOnlyList<Access>> Accesses = new(() => EngineFixture.Collect(EngineFixture.Execute(Solved.Value)).Accesses);

    [Fact]
    public void Check_outside_any_work_does_nothing()
    {
        WorkCancellation.Check();
        Assert.Equal(CancellationToken.None, WorkCancellation.Token);
    }

    [Fact]
    public void Nested_work_restores_the_outer_check()
    {
        var outer = 0;
        var inner = 0;
        using (WorkCancellation.Enter(CancellationToken.None, () => outer++))
        {
            WorkCancellation.Check();
            using (WorkCancellation.Enter(CancellationToken.None, () => inner++))
                WorkCancellation.Check();
            WorkCancellation.Check();
        }
        WorkCancellation.Check();
        Assert.Equal(2, outer);
        Assert.Equal(1, inner);
        Assert.Equal(CancellationToken.None, WorkCancellation.Token);
    }

    [Fact]
    public void Nested_work_without_an_observer_reports_to_the_outer_one()
    {
        var seen = 0;
        using var outer = WorkCancellation.Enter(CancellationToken.None, () => seen++);
        using var inner = WorkCancellation.Enter(CancellationToken.None);
        WorkCancellation.Check();
        Assert.Equal(1, seen);
    }

    [Fact]
    public void Nested_work_observes_an_enclosing_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var member = new CancellationTokenSource();
        var seen = 0;
        using var outer = WorkCancellation.Enter(cancellation.Token, () => seen++);
        using (WorkCancellation.Enter(member.Token))
        {
            var effective = WorkCancellation.Token;
            Assert.NotEqual(member.Token, effective);
            Assert.NotEqual(cancellation.Token, effective);
            cancellation.Cancel();
            Assert.Equal(effective, Assert.Throws<OperationCanceledException>(WorkCancellation.Check).CancellationToken);
        }
        Assert.Equal(cancellation.Token, WorkCancellation.Token);
        Assert.Equal(1, seen);
    }

    [Fact]
    public void Outside_suspends_an_enclosing_cancellation()
    {
        var seen = 0;
        using var outer = WorkCancellation.Enter(new CancellationToken(true), () => seen++);
        using (WorkCancellation.Outside())
        {
            WorkCancellation.Check();
            Assert.Equal(CancellationToken.None, WorkCancellation.Token);
            using (WorkCancellation.Enter(new CancellationToken(true)))
                Assert.Throws<OperationCanceledException>(WorkCancellation.Check);
            using (WorkCancellation.Enter(CancellationToken.None))
                WorkCancellation.Check();
            WorkCancellation.Check();
            Assert.Equal(0, seen);
        }
        Assert.Throws<OperationCanceledException>(WorkCancellation.Check);
        Assert.Equal(1, seen);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Lock")]
    [InlineData("Resolve")]
    public async Task Analysis_stages_outside_the_pipeline_observe_the_work(string stage)
    {
        using var repository = new Repository();
        repository.Model();
        var files = ProjectModelFiles.Read(repository.Root);
        Assert.Single(files.Entries);
        var modelLock = ModelLock.Read(repository.Root, files);
        var compilation = (await FixtureSolution.Create(("Model.cs", "public class Sample { public static void Run() { } }"))
                                         .Projects.Single().GetCompilationAsync())!;
        string Query() => stage switch
        {
            "Read" => string.Join(",", ProjectModelFiles.Read(repository.Root).Entries.Select(entry => entry.Member)),
            "Lock" => string.Join(",", ModelLock.Read(repository.Root, files).Diagnostics),
            "Resolve" => string.Join(",", ProjectModelResolver.Resolve(files, [compilation], modelLock).Models.Members.Select(model => model.Id)),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        var expected = Query();
        using (WorkCancellation.Enter(new CancellationToken(true)))
            Assert.ThrowsAny<OperationCanceledException>(() => Query());
        Assert.Equal(expected, Query());
    }

    public static IEnumerable<object[]> GenerationCells() =>
        from enclosing in new[] { false, true }
        from when in new[] { "before", "started", "printed" }
        select new object[] { enclosing, when };

    [Theory]
    [MemberData(nameof(GenerationCells))]
    public async Task Generation_observes_an_enclosing_cancellation_in_every_cell(bool enclosing, string when)
    {
        using var install = new GenerationInstall();
        const string name = "Fixture.Cancellation";
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, name + ".dll"), name, """
            public sealed class First { public int Value() => 1; }
            public sealed class Second { public int Value() => 2; }
            public sealed class Third { public int Value() => 3; }
            """);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var assembly = new ImplementationAssembly(name, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
            EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll")).ToArray(), [], "0.0.0.0", "", Guid.Empty);
        using var outerToken = new CancellationTokenSource();
        using var memberToken = new CancellationTokenSource();
        using var outer = WorkCancellation.Enter(outerToken.Token);
        using var member = WorkCancellation.Enter(memberToken.Token);
        var effective = WorkCancellation.Token;
        var cancellation = enclosing ? outerToken : memberToken;
        var started = 0;
        var printed = 0;
        var ownership = new CompilationOwnership();
        using var owned = ownership.Open();
        var probe = new LibraryCompilationProbe
        {
            Workers = 1,
            Started = () =>
            {
                Interlocked.Increment(ref started);
                if (when == "started")
                    cancellation.Cancel();
            },
            TypePrinted = _ =>
            {
                Interlocked.Increment(ref printed);
                if (when == "printed")
                    cancellation.Cancel();
            }
        };
        if (when == "before")
            cancellation.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => LibraryCompilation.Compile(assembly, new HashSet<string>(),
            new GenerationChecks(effective), probe));
        Assert.Equal(effective, error.CancellationToken);
        Assert.InRange(started, 0, 1);
        Assert.Equal(when == "before" ? 0 : 1, started);
        foreach (var entry in ownership.Created)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => entry.Entry.Value);
            var cache = (ConcurrentDictionary<string, Lazy<Task<LibraryCompilationResult>>>)typeof(LibraryCompilation)
                .GetField("Compilations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.True(SpinWait.SpinUntil(() => !cache.ContainsKey(entry.Key), TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(when == "printed" ? 1 : 0, printed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Member_generation_uses_the_effective_token(bool alreadyCompiled)
    {
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.5", runtime: true);
        const string name = "Fixture.Linked";
        const string source = "namespace Lib { public static class Api { public static void Run(System.Action a) => a(); } }";
        var package = install.Package(name, "1.0.0", "", $"net8.0/{name}");
        EmittedAssemblies.Write(Path.Combine(package, "lib", "net8.0", name + ".dll"), name, source);
        var request = new GenerationRequest(name, "1.0.0", "M:Lib.Api.Run(System.Action)", name, "net8.0");
        var library = alreadyCompiled
            ? LibraryCompilation.CompileTrees(name, [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
                                              EmittedAssemblies.RuntimeReferences, CancellationToken.None)
            : null;
        using var cancellation = new CancellationTokenSource();
        using var member = new CancellationTokenSource();
        var effective = CancellationToken.None;
        using var work = WorkCancellation.Enter(cancellation.Token);
        void Report(GenerationStage stage, int ordinal)
        {
            if (stage != (alreadyCompiled ? GenerationStage.EngineClaims : GenerationStage.CompileLibrary) || ordinal != 1)
                return;
            effective = WorkCancellation.Token;
            cancellation.Cancel();
        }
        var error = Assert.ThrowsAny<OperationCanceledException>(() => alreadyCompiled
            ? ModelGenerator.Trace(request, library!, member.Token, checkReported: Report)
            : ModelGenerator.Trace(request, install.Resolver(), member.Token, Report));
        Assert.NotEqual(CancellationToken.None, effective);
        Assert.NotEqual(cancellation.Token, effective);
        Assert.NotEqual(member.Token, effective);
        Assert.Equal(effective, error.CancellationToken);
    }

    public static IEnumerable<object[]> StageCells() =>
        from each in new[] { false, true }
        from generation in new[] { "none", "live", "cancelled" }
        from work in new[] { "outside", "live", "cancelled" }
        select new object[] { each, generation, work };

    [Theory]
    [MemberData(nameof(StageCells))]
    public void Stage_checks_observe_the_work_in_every_cell(bool each, string generation, string work)
    {
        var observed = 0;
        var reported = 0;
        var checks = new StageChecks(generation == "none" ? null : new GenerationChecks(new CancellationToken(generation == "cancelled"),
            (_, _) => reported++), GenerationStage.ReadEffects);
        using var frame = Work(work, () => observed++);
        void Query()
        {
            if (each)
                Assert.Equal(new[] { 1, 2, 3 }, checks.Each(new[] { 1, 2, 3 }).ToArray());
            else
                checks.Check();
        }
        var cancelled = work == "cancelled" || generation == "cancelled";
        if (cancelled)
            Assert.Throws<OperationCanceledException>(Query);
        else
            Query();
        var count = cancelled || !each ? 1 : 3;
        Assert.Equal(work == "outside" ? 0 : count, observed);
        Assert.Equal(generation == "none" ? 0 : count, reported);
    }

    public static IEnumerable<object[]> HeapCells() =>
        from query in new[] { "Solve", "Resolve", "PointsTo", "StaticRegionOf", "FieldsOf", "DelegateCaptures", "Completion" }
        from work in new[] { "outside", "live", "cancelled" }
        select new object[] { query, work };

    [Theory]
    [MemberData(nameof(HeapCells))]
    public void Solved_heap_queries_observe_the_work_in_every_cell(string query, string work)
    {
        var solved = Solved.Value;
        var heap = solved.Heap;
        var instance = heap.Instances.Values.First(instance => instance.BodyId.Contains("SampleController.Post", StringComparison.Ordinal));
        var region = heap.Regions.Values.First(region => region.Kind == HeapRegionKind.Allocation).Identity;
        var closure = heap.Regions.Values.First(region => region.Kind == HeapRegionKind.Delegate).Identity;
        var task = heap.Regions.Values.First(region => region.Kind == HeapRegionKind.Task).Identity;
        var field = new IrFieldRef("Fixture", "SampleController", "Shared", IrFieldKind.Field, true, true, "object");
        string Query() => query switch
        {
            "Solve" => string.Join(",", WholeProgram.Solve(Scope(solved), AnalysisLimits.Default, CancellationToken.None).Regions.Keys.Order()),
            "Resolve" => string.Join(",", heap.Resolve(instance.Id, new ConcurrencyHunter.Heap.ThisValue()).Order()),
            "PointsTo" => string.Join(",", heap.PointsTo(region, "Value").Order()),
            "StaticRegionOf" => heap.StaticRegionOf(instance.Id, field),
            "FieldsOf" => string.Join(",", heap.FieldsOf(region).Order()),
            "DelegateCaptures" => string.Join(",", heap.DelegateCaptures(closure).Order()),
            "Completion" => string.Join(",", heap.Completion(task).Order()),
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        var expected = Query();
        var seen = 0;
        using (Work(work, () => seen++))
        {
            if (work == "cancelled")
                Assert.ThrowsAny<OperationCanceledException>(() => Query());
            else
                Assert.Equal(expected, Query());
        }
        Assert.Equal(expected, Query());
        Assert.Equal(work != "outside", seen > 0);
    }

    public static IEnumerable<object[]> GraphCells() =>
        from query in new[] { "Ordered", "UnprovenJoins", "JoinDominates" }
        from work in new[] { "outside", "live", "cancelled" }
        select new object[] { query, work };

    [Theory]
    [MemberData(nameof(GraphCells))]
    public void Happens_before_queries_observe_the_work_in_every_cell(string query, string work)
    {
        var expected = GraphQuery(Graph(), query);
        var graph = Graph();
        var seen = 0;
        using (Work(work, () => seen++))
        {
            if (work == "cancelled")
                Assert.Throws<OperationCanceledException>(() => GraphQuery(graph, query));
            else
                Assert.Equal(expected, GraphQuery(graph, query));
        }
        Assert.Equal(expected, GraphQuery(graph, query));
        Assert.Equal(work != "outside", seen > 0);
    }

    public static IEnumerable<object[]> InterruptedCells() =>
        from asynchronous in new[] { false, true }
        from query in new[] { "Ordered", "Creator", "UnprovenJoins", "JoinDominates" }
        from warm in new[] { false, true }
        // The second check also covers a lazy calculation that has just published its in-progress marker.
        from cut in asynchronous && !warm && query == "UnprovenJoins"
            ? new[] { "first", "middle", "last", "second", "segment-first", "segment-middle", "segment-last" }
            : new[] { "first", "middle", "last", "second" }
        from next in new[] { "outside", "live" }
        select new object[] { query, warm, cut, next, asynchronous };

    [Theory]
    [MemberData(nameof(InterruptedCells))]
    public void Interrupted_happens_before_query_leaves_the_graph_unchanged(string query, bool warm, string cut, string next, bool asynchronous)
    {
        _ = asynchronous ? AsyncAccesses.Value : Accesses.Value;
        var baseline = Graph(asynchronous);
        if (warm)
            GraphQuery(baseline, query, asynchronous);
        var count = 0;
        var segmentChecks = new List<int>();
        var segmentCut = cut.StartsWith("segment-", StringComparison.Ordinal);
        string expected;
        using (WorkCancellation.Enter(CancellationToken.None, () =>
        {
            count++;
            if (segmentCut && new StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod() is { } method && method.DeclaringType == typeof(AsyncSegments) && method.Name == "Of"))
                segmentChecks.Add(count);
        }))
            expected = GraphQuery(baseline, query, asynchronous);
        Assert.True(count > 0);
        var expectedJoins = GraphQuery(Graph(asynchronous), "UnprovenJoins", asynchronous);
        Assert.NotEmpty(expectedJoins);
        var graph = Graph(asynchronous);
        if (warm)
            Assert.Equal(expected, GraphQuery(graph, query, asynchronous));
        using var cancellation = new CancellationTokenSource();
        var k = cut == "first" ? 1 : cut == "middle" ? (count + 1) / 2 : cut == "second" ? Math.Min(2, count) : count;
        if (segmentCut)
        {
            Assert.NotEmpty(segmentChecks);
            k = cut == "segment-first" ? segmentChecks[0] : cut == "segment-middle" ? segmentChecks[segmentChecks.Count / 2] : segmentChecks[^1];
        }
        var seen = 0;
        using (WorkCancellation.Enter(cancellation.Token, () =>
        {
            if (++seen == k)
                cancellation.Cancel();
        }))
            Assert.Throws<OperationCanceledException>(() => GraphQuery(graph, query, asynchronous));
        Assert.Equal(k, seen);
        using (Work(next, () => { }))
        {
            Assert.Equal(expected, GraphQuery(graph, query, asynchronous));
            Assert.Equal(expectedJoins, GraphQuery(graph, "UnprovenJoins", asynchronous));
        }
    }

    [Fact]
    public void Shared_initialization_ignores_a_cancelled_work()
    {
        var expected = LibraryModels.LoadBuiltIn();
        var seen = 0;
        LibraryModels actual;
        using (WorkCancellation.Enter(new CancellationToken(true), () => seen++))
            actual = LibraryModels.LoadBuiltIn();
        Assert.Equal(expected.Members.Select(model => ModelEntryWriter.Write(model).ToJsonString()),
                     actual.Members.Select(model => ModelEntryWriter.Write(model).ToJsonString()));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.ImmutableTypes), System.Text.Json.JsonSerializer.Serialize(actual.ImmutableTypes));
        Assert.Equal(0, seen);
    }

    public static IEnumerable<object[]> DirectCells() =>
        from place in new[] { "Pipeline", "Generate", "WouldGenerate", "Layer", "Write", "Targets" }
        from enclosing in new[] { false, true }
        select new object[] { place, enclosing };

    [Theory]
    [MemberData(nameof(DirectCells))]
    public async Task Direct_checks_report_before_any_token(string place, bool enclosing)
    {
        using var repository = new Repository();
        var own = new CancellationToken(!enclosing);
        var compilations = new[] { (await FixtureSolution.Create(("Case.cs", EngineFixture.Usings + SOURCE + EngineFixture.Startup()))
            .Projects.Single().GetCompilationAsync())! };
        var method = compilations[0].GetTypeByMetadataName("SampleController")!.GetMembers("Post").OfType<IMethodSymbol>().Single();
        var models = LibraryModels.BuiltIn;
        var key = GeneratedFileKey.ForFramework("Fixture", new Version(1, 0), "Microsoft.NETCore.App", 10);
        var file = GeneratedModelFiles.Read(repository.Root, key);
        var answer = GeneratedModelAnswer.OfRefusal("M:Sample.Run", new GeneratedRefusal(GenerationReasons.ACCESSOR, "accessor", []));
        var generation = new RunGeneration(repository.Root, own);
        void Query()
        {
            switch (place)
            {
                case "Pipeline":
                    ScopePipeline.Run("scope:Fixture", compilations, [], EngineFixture.ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
                                      models, AnalysisLimits.Default, new(), null, own);
                    break;
                case "Generate": generation.Generate(models, []); break;
                case "WouldGenerate": generation.WouldGenerate(models, []); break;
                case "Layer": generation.Layer(models, []); break;
                case "Write": GeneratedModelFiles.Write(file, [answer], own); break;
                case "Targets": GenerationTargets.Of(method, compilations[0], ImplementationAssemblies.ForThisProcess(), own); break;
                default: throw new ArgumentOutOfRangeException(nameof(place));
            }
        }
        var seen = 0;
        using var work = WorkCancellation.Enter(new CancellationToken(enclosing), () => seen++);
        var error = Assert.ThrowsAny<OperationCanceledException>(Query);
        if (place == "Pipeline")
            Assert.Equal(ScopeStep.RootDiscovery, Assert.IsType<ScopeCancelledException>(error).Step);
        Assert.Equal(1, seen);
    }

    private static IDisposable Work(string state, Action observed) => state == "outside"
        ? WorkCancellation.Outside()
        : WorkCancellation.Enter(new CancellationToken(state == "cancelled"), observed);

    private static ScopeProgram Scope(HeapRun run) => new(run.Program.ScopeId, run.Program.Input.Roots, run.Program.Result, run.Summaries,
        run.Program.Input.Program, run.Program.Input.DiIndex, run.Program.Input.InjectionBindings)
    {
        MetadataSupertypes = run.Program.MetadataSupertypes
    };

    private static HappensBefore Graph(bool asynchronous = false) => (HappensBefore)typeof(ExecutionAnalysis)
        .GetField("_order", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(EngineFixture.Execute(asynchronous ? AsyncSolved.Value : Solved.Value).Analysis)!;

    private static string GraphQuery(HappensBefore graph, string query, bool asynchronous = false)
    {
        var accesses = asynchronous ? AsyncAccesses.Value : Accesses.Value;
        var first = accesses.First(access => access.Symbol.EndsWith(".F()", StringComparison.Ordinal));
        var second = accesses.First(access => access.Symbol.EndsWith(".P()", StringComparison.Ordinal));
        var heap = (asynchronous ? AsyncSolved.Value : Solved.Value).Heap;
        var instance = heap.Instances.Values.First(instance => instance.BodyId.Contains("SampleController.Post", StringComparison.Ordinal));
        var join = instance.Summary.Joins.First();
        return query switch
        {
            "Ordered" => graph.Ordered(first, second).ToString(),
            "Creator" => graph.Ordered(first, second, second.ExecutionId).ToString(),
            "UnprovenJoins" => string.Join(",", graph.UnprovenJoins.Select(site => $"{site.BodyId}:{site.OperationId}")),
            "JoinDominates" => graph.JoinDominates(second.ExecutionId, instance.Id, join.OperationId, second).ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("ch-work-cancellation-").FullName;

        internal void Model()
        {
            var folder = Path.Combine(Root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "model.json"), """
                {"schemaVersion":1,"assemblies":["Fixture"],"models":[{"member":"M:Sample.Run","effects":{}}]}
                """);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
