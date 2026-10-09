using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CanonicalOrderTests
{
    private static readonly ExecutionWalkOrder[] Permutations = [new(Reverse: true), new(Seed: 17), new(Seed: 93), new(Seed: 106)];

    [Fact]
    public void Store_evidence_names_the_store_first_in_source_order()
    {
        var heap = Solve("""
            public sealed class Note { public int Value; }
            public static class Holder { public static Note? Current; }
            public sealed class Worker : BackgroundService
            {
                private static void A(Note note) { Holder.Current = note; }
                private static void Z(Note note) { Holder.Current = note; }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    var note = new Note(); Z(note); A(note); return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();"));
        var scope = Scope(heap);
        var first = heap.Program.Result.Bodies.Values.Single(body => body.MethodSymbol == "Worker.A(Note)")
                        .Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>().Single().Provenance.Span;
        foreach (var mode in Permutations.Prepend(null))
        {
            var analysis = ExecutionModel.Build(scope, heap.Heap, CancellationToken.None, mode);
            var evidence = analysis.Ownership[heap.Region("alloc:Worker.ExecuteAsync(CancellationToken)#Note").Identity].Evidence;
            Assert.Contains(evidence, hop => hop.Contains($"at {first.Path}:{first.StartLine}.", StringComparison.Ordinal));
        }
        // Exercise the fallback with no collected access as well, over the same real summaries and heap.
        var builderType = typeof(ExecutionModel).GetNestedType("Builder", System.Reflection.BindingFlags.NonPublic)!;
        var builder = Activator.CreateInstance(builderType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                                              null, [scope, heap.Heap, CancellationToken.None, null], null)!;
        var storeSpan = builderType.GetMethod("StoreSpan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var source = heap.Region("static:Holder").Identity;
        var field = heap.Heap.FieldsOf(source).Single();
        Assert.Equal(first, storeSpan.Invoke(builder, [Array.Empty<CollectedAccess>(), source, field, heap.Region("alloc:Worker.ExecuteAsync(CancellationToken)#Note").Identity]));
        AssertPermutations(scope, heap.Heap);
    }

    [Fact]
    public void Reached_from_names_the_ordinally_first_execution()
    {
        var heap = Solve("""
            public sealed class Note { public int Value; }
            public sealed class Worker : BackgroundService
            {
                private static void A(Note note) { new Thread(() => note.Value = 1).Start(); }
                private static void Z(Note note) { Task.Run(() => note.Value = 2); }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    var note = new Note(); Z(note); A(note); return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();"));
        var scope = Scope(heap);
        foreach (var mode in Permutations.Prepend(null))
        {
            var analysis = ExecutionModel.Build(scope, heap.Heap, CancellationToken.None, mode);
            var region = heap.Region("alloc:Worker.ExecuteAsync(CancellationToken)#Note").Identity;
            var other = analysis.ExecutionAccesses().Where(access => access.RegionId == region && analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Spawn)
                                .Select(access => access.ExecutionId).Distinct().Order(StringComparer.Ordinal).First();
            Assert.Equal(OwnershipKind.Escaped, analysis.Ownership[region].Kind);
            Assert.Contains($"reached from {analysis.Execution(other).Display}.", Assert.Single(analysis.Ownership[region].Evidence), StringComparison.Ordinal);
        }
        AssertPermutations(scope, heap.Heap);
    }

    [Fact]
    public void Group_members_do_not_depend_on_visit_order()
    {
        var heap = Solve("""
            public static class State { public static int Value; }
            public sealed class Worker : BackgroundService
            {
                private static Task Group(Task work) => Task.WhenAll(work);
                protected override async Task ExecuteAsync(CancellationToken token)
                {
                    var a = Task.Run(() => State.Value = 1);
                    var b = Task.Run(() => State.Value = 2);
                    await Task.WhenAll(Group(a), Group(b));
                    State.Value = 3;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();"));
        Assert.NotEmpty(heap.Heap.TaskGroups);
        AssertPermutations(Scope(heap), heap.Heap);
    }

    [Fact]
    public async Task Startup_delegate_display_does_not_depend_on_visit_order()
    {
        var source = """
            public abstract class Job { public abstract int Run(); }
            public sealed class A : Job { public override int Run() => 1; }
            public sealed class Z : Job { public override int Run() => 2; }
            public class JobsController : ControllerBase
            {
                private static void Hand(Job job) { Func<int> work = job.Run; Fates.Lib.OnStart(work); }
                public void Post() { Hand(new Z()); Hand(new A()); }
            }
            """ + Startup();
        var library = MetadataReference.CreateFromImage(EmittedAssemblies.Image("CanonicalFates",
            "namespace Fates { public static class Lib { public static void OnStart(System.Func<int> work) { } } }"));
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [library] }, ("Case.cs", Usings + source));
        var directory = Directory.CreateTempSubdirectory("ch-canonical-fate-").FullName;
        HeapRun heap;
        try
        {
            var folder = Path.Combine(directory, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "fates.json"), """
                {"schemaVersion":1,"assemblies":["CanonicalFates"],"models":[
                  {"member":"M:Fates.Lib.OnStart(System.Func{System.Int32})","effects":{},"fates":{"work":{"fate":"startup"}}}
                ]}
                """);
            var files = ProjectModelFiles.Read(directory);
            var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
            var (models, rejected) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(directory, files));
            Assert.Empty(rejected);
            heap = Solve(ReachScope(solution, "scope:Fixture", models), new AnalysisLimits(MaxContextsPerMethod: 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Assert.NotEmpty(heap.Heap.StartupDelegates);
        var region = heap.Heap.StartupDelegates.GroupBy(fated => fated.RegionId).First(group => group.Select(fated => fated.CalleeInstance).Distinct().Count() > 1).Key;
        foreach (var mode in Permutations.Prepend(null))
        {
            var analysis = ExecutionModel.Build(Scope(heap), heap.Heap, CancellationToken.None, mode);
            Assert.Equal("unknown call of the delegate A.Run()", analysis.Execution(ExecutionModel.UnknownDelegateCallId(region)).Display);
        }
        AssertPermutations(Scope(heap), heap.Heap);
    }

    [Fact]
    public async Task Demo_observation_is_the_same_under_permuted_walks()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        await AssertSolution(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"));
    }

    [RequiresEShopFact]
    public async Task EShop_observation_is_the_same_under_permuted_walks()
    {
        await AssertSolution(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src", "eShopOnContainers-ServicesAndWebApps.sln"));
    }

    private static ScopeProgram Scope(HeapRun heap) =>
        new(heap.Program.ScopeId, heap.Program.Input.Roots, heap.Program.Result, heap.Summaries, heap.Program.Input.Program,
            heap.Program.Input.DiIndex, heap.Program.Input.InjectionBindings) { MetadataSupertypes = heap.Program.MetadataSupertypes };

    private static void AssertPermutations(ScopeProgram scope, HeapSolution heap)
    {
        var expected = ExecutionObservation.Capture(scope, heap, ExecutionModel.Build(scope, heap, CancellationToken.None));
        foreach (var mode in Permutations)
        {
            var actual = ExecutionObservation.Capture(scope, heap, ExecutionModel.Build(scope, heap, CancellationToken.None, mode));
            Assert.Equal(expected.Properties, actual.Properties);
            Assert.Equal(expected.Queries, actual.Queries);
            Assert.Equal(expected.Accesses, actual.Accesses);
            Assert.Equal(expected.Findings, actual.Findings);
        }
    }

    private static async Task AssertSolution(string path)
    {
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(path);
        Assert.True(loaded.Coverage.LoadComplete, string.Join(", ", loaded.Coverage.MissingProjects));
        var rootDirectory = Path.GetDirectoryName(path)!;
        var repository = RepositoryRoot.Find(rootDirectory);
        var projectModels = ProjectModelFiles.Read(repository);
        var modelLock = ModelLock.Read(repository, projectModels);
        var cache = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        var scopes = ProcessScopes.Discover(loaded.Solution, rootDirectory).Scopes;
        Assert.NotEmpty(scopes);
        foreach (var scoped in scopes)
        {
            var compiled = new List<(Compilation Compilation, string? ProjectFilePath)>();
            foreach (var project in scoped.Projects)
                compiled.Add(((await project.GetCompilationAsync())!, project.FilePath));
            var compilations = compiled.Select(item => item.Compilation).ToArray();
            var (models, _) = ProjectModelResolver.Resolve(projectModels, compilations, modelLock);
            var run = ScopePipeline.Run(scoped.Scope.Id, compilations, compiled, rootDirectory, ProviderRegistry.BuiltIn, models,
                                        AnalysisLimits.Default, cache, null, CancellationToken.None);
            Assert.False(run.Stopped);
            // Collection can materialize heap regions lazily through Resolve. Build every compared analysis
            // after that same pipeline collection, over the same region domain.
            AssertPermutations(run.ScopeProgram!, run.Heap!);
        }
    }
}
