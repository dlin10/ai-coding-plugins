using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The stages one scope runs through, apart from what belongs to the analysis run: project models, the model lock,
/// pairing, the solver and findings.</summary>
public sealed class ScopePipelineTests
{
    private const string SERIALIZE = "M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)~System.Byte[]";

    private const string QUEUE_SOURCE = """
        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class QueueHandlerAttribute : System.Attribute { }

        public static class Inventory { public static int Reserved; }

        public static class OrderQueue
        {
            [QueueHandler]
            public static void Reserve(int quantity) => Inventory.Reserved += quantity;

            public static void NotAHandler() => Inventory.Reserved = 0;
        }
        """;

    [Fact]
    public async Task Pipeline_gives_the_accesses_the_analyzer_reports_for_the_scope()
    {
        var solution = FixtureSolution.Create(("Case.cs", Source("_state.Touch(); System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_state);")));

        var analysis = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, AnalysisLimits.Default, CancellationToken.None);
        var run = await RunPipeline(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn);

        Assert.False(run.Stopped);
        Assert.Null(run.StoppedAtReachableBodies);
        Assert.NotEmpty(analysis.Findings);
        Assert.Equal(analysis.Roots.Select(root => root.StableRootId), run.Roots.Select(root => root.StableRootId));
        Assert.Equal(Assert.Single(analysis.Coverage).RootsPerProvider, run.RootsPerProvider);
        Assert.NotEmpty(run.Collection.Accesses);
        Assert.Equal(analysis.Accesses.Select(Key), run.Collection.Accesses.Select(Key));
        Assert.Equal(analysis.ScopeSizes.Single().ReachableBodies, run.Heap.ReachableBodies.Count);
        Assert.Equal(analysis.ScopeSizes.Single().Summaries, run.Summaries.Built);
        Assert.Same(run.ScopeProgram, run.Interprocedural.Scope);
        Assert.Same(run.Heap, run.Interprocedural.Heap);
        Assert.Same(run.Executions, run.Interprocedural.Executions);
    }

    [Fact]
    public async Task Pipeline_reads_no_project_model_and_writes_no_lock()
    {
        using var repo = new Repository();
        var model = repo.Model("{\"schemaVersion\": 1, \"assemblies\": [\"System.Text.Json\"], \"models\": [{\"member\": \"" + SERIALIZE +
                               "\", \"opaque\": true}]}");
        var rootDirectory = Directory.CreateDirectory(Path.Combine(repo.Root, "src", "app")).FullName;
        var solution = FixtureSolution.Create(("Case.cs", Source("System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_state);")));
        var before = repo.Files();
        var modelBytes = File.ReadAllBytes(model);

        var run = await RunPipeline(solution, rootDirectory, ProviderRegistry.BuiltIn);

        Assert.False(run.Stopped);
        Assert.Equal(before, repo.Files());
        Assert.Equal(modelBytes, File.ReadAllBytes(model));
        Assert.False(File.Exists(Path.Combine(repo.Models, "models.lock.json")));
        Assert.Equal(0, run.Collection.Coverage.Counters.GetValueOrDefault(CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, run.Collection.Coverage.Counters.GetValueOrDefault(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.True(run.Collection.Coverage.Counters.GetValueOrDefault(CoverageCounters.KNOWN_CALL_BUILT_IN) > 0);

        // The same scope under the analysis run takes the project's entry: the pipeline left it out, it did not miss it.
        var analysis = await PhaseOneAnalyzer.AnalyzeAsync(solution, rootDirectory, CancellationToken.None);
        Assert.Equal(1, Assert.Single(analysis.Coverage).Skips.GetValueOrDefault(CoverageCounters.OPAQUE_BY_PROJECT));
    }

    [Fact]
    public async Task Pipeline_runs_the_roots_of_the_registry_it_is_given()
    {
        var solution = FixtureSolution.Create(("Queue.cs", QUEUE_SOURCE));

        var run = await RunPipeline(solution, ROOT_DIRECTORY, new ProviderRegistry([new QueueHandlerProvider("queue", "queue")]));

        Assert.False(run.Stopped);
        Assert.Equal(["queue"], run.RootsPerProvider.Keys);
        var root = Assert.Single(run.Roots);
        Assert.Equal("queue", root.ProviderId);
        Assert.Equal(1, run.RootsPerProvider["queue"]);
        Assert.NotEmpty(run.Collection.Accesses);
        Assert.All(run.Collection.Accesses, access => Assert.Equal(root.StableRootId, access.Root.RootId));
        Assert.Contains(run.Collection.Accesses, access => access.Symbol == "OrderQueue.Reserve(int)");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.StartsWith("OrderQueue.NotAHandler", StringComparison.Ordinal));

        var builtIn = await RunPipeline(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn);
        Assert.Empty(builtIn.Roots);
        Assert.Equal(["aspnetcore", "hosting"], builtIn.RootsPerProvider.Keys);
    }

    [Fact]
    public async Task Pipeline_keeps_the_first_root_of_a_stable_id_and_reports_the_later_one()
    {
        var solution = FixtureSolution.Create(("Queue.cs", QUEUE_SOURCE));
        var registry = new ProviderRegistry([new QueueHandlerProvider("first", "queue"), new QueueHandlerProvider("second", "queue")]);

        var run = await RunPipeline(solution, ROOT_DIRECTORY, registry);

        var root = Assert.Single(run.Roots);
        Assert.Equal("first", root.ProviderId);
        Assert.Equal(1, run.RootsPerProvider["first"]);
        Assert.Equal(0, run.RootsPerProvider["second"]);
        Assert.Contains(run.DiscoveryDiagnostics,
                        diagnostic => diagnostic.StartsWith($"second: {RootDiscoveryDiagnosticCode.DiscoveryFailed} {root.StableRootId}: ", StringComparison.Ordinal) &&
                                      diagnostic.EndsWith("was already produced by an earlier provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pipeline_over_its_limit_stops_after_reachability()
    {
        var solution = FixtureSolution.Create(("Case.cs", Source("_state.Touch();")));
        var unlimited = await RunPipeline(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn);
        Assert.False(unlimited.Stopped);
        var reached = unlimited.Reachable.ReachedBodies.Count;
        Assert.True(reached > 1);

        var stopped = await RunPipeline(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, reached - 1);

        Assert.True(stopped.Stopped);
        Assert.Equal(reached, stopped.StoppedAtReachableBodies);
        Assert.Equal(reached, stopped.Reachable.ReachedBodies.Count);
        Assert.Equal(unlimited.Roots.Select(root => root.StableRootId), stopped.Roots.Select(root => root.StableRootId));
        Assert.Null(stopped.Summaries);
        Assert.Null(stopped.ScopeProgram);
        Assert.Null(stopped.Heap);
        Assert.Null(stopped.Executions);
        Assert.Null(stopped.Interprocedural);
        Assert.Null(stopped.Collection);
        Assert.Equal(TimeSpan.Zero, stopped.Times.SummariesAndFixpoint);
        Assert.Equal(TimeSpan.Zero, stopped.Times.Executions);
        Assert.Equal(TimeSpan.Zero, stopped.Times.Accesses);

        // The limit is the most bodies a run may reach: one that reaches exactly that many runs on.
        var atLimit = await RunPipeline(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, reached);
        Assert.False(atLimit.Stopped);
        Assert.Null(atLimit.StoppedAtReachableBodies);
        Assert.NotNull(atLimit.Summaries);
        Assert.NotNull(atLimit.ScopeProgram);
        Assert.NotNull(atLimit.Heap);
        Assert.NotNull(atLimit.Executions);
        Assert.NotNull(atLimit.Interprocedural);
        Assert.Equal(unlimited.Collection.Accesses.Select(Key), atLimit.Collection.Accesses.Select(Key));
    }

    private static async Task<ScopeRun> RunPipeline(Solution solution, string rootDirectory, ProviderRegistry registry, int? limit = null)
    {
        var scoped = Assert.Single(ProcessScopes.Discover(solution, rootDirectory).Scopes);
        var projectFiles = new List<(Compilation Compilation, string? ProjectFilePath)>();
        foreach (var project in scoped.Projects)
        {
            if (await project.GetCompilationAsync() is { } compilation)
                projectFiles.Add((compilation, project.FilePath));
        }

        return ScopePipeline.Run(scoped.Scope.Id, projectFiles.Select(project => project.Compilation).ToArray(), projectFiles, rootDirectory,
                                 registry, LibraryModels.BuiltIn, AnalysisLimits.Default, new(), limit, CancellationToken.None);
    }

    private static string Key(Access access) =>
        string.Join("|", access.Root.RootId, access.ExecutionId, access.Symbol, access.Operation, access.Source, access.Resource.Region,
                    string.Join(".", access.Resource.AccessPath), access.Resource.RegionId, access.Ownership);

    private static string Source(string work) => Usings + $$"""
        public sealed class State
        {
            public int Count = 1;
            public void Touch() { Count++; }
        }

        [ApiController]
        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;
            [HttpGet("/state")]
            public int Get() => _state.Count;
        }

        public sealed class StateWorker : BackgroundService
        {
            private readonly State _state;
            public StateWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<StateWorker>();");

    /// <summary>Every source method marked <c>[QueueHandler]</c> is a root; two providers given one root prefix produce one
    /// stable root id for the same method.</summary>
    private sealed class QueueHandlerProvider(string providerId, string rootPrefix) : IExecutionRootProvider
    {
        public string ProviderId => providerId;

        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions =>
            [new SupportedAssemblyVersion("Fixture.Queues", new Version(1, 0), new Version(2, 0))];

        public RootDiscoveryResult Discover(RootDiscoveryContext context)
        {
            var roots = new List<ExecutionRootDescriptor>();
            foreach (var compilation in context.Compilations)
            {
                foreach (var method in Types(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
                {
                    if (!method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "QueueHandlerAttribute"))
                        continue;

                    var symbol = SymbolNames.Method(method);
                    var location = method.Locations[0].GetLineSpan();
                    roots.Add(new ExecutionRootDescriptor(
                        $"{rootPrefix}:{compilation.AssemblyName}:{method.GetDocumentationCommentId()}",
                        "queue-handler",
                        ProviderId,
                        new RootEntry(IrLowering.RootBodyId(method), symbol, $"queue handler {symbol}",
                                      new SourceSpan(Path.GetFileName(location.Path), location.StartLinePosition.Line + 1,
                                                     location.StartLinePosition.Character + 1, location.EndLinePosition.Line + 1,
                                                     location.EndLinePosition.Character + 1)),
                        new InstanceBindings(ReceiverKind.None,
                            method.Parameters.Select(parameter => new ParameterBinding(parameter.Name, SymbolNames.Type(parameter.Type),
                                                                                       ParameterBindingKind.RequestData)).ToArray()),
                        new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId),
                        [],
                        [],
                        [],
                        []));
                }
            }

            return new RootDiscoveryResult(RootDiscoveryStatus.Checked, roots, []);
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
            @namespace.GetTypeMembers().Concat(@namespace.GetNamespaceMembers().SelectMany(Types));
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-pipeline-").FullName;
        public string Models => Path.Combine(Root, ".concurrency-hunter", "models");

        public string Model(string text)
        {
            var path = Path.Combine(Models, "model.json");
            Directory.CreateDirectory(Models);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public string[] Files() =>
            Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
