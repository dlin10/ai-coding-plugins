using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Solving;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;
using GenerationCauses = ConcurrencyHunter.Analysis.GenerationCauses;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>What a run does with the generated model layer by mode (R9) and what it leaves in the process (R12). A fake generator and a package
/// in a fake NuGet layout stand for the real ones; the library compilations that the generator makes are real ones, of tiny emitted assemblies.</summary>
public sealed class ModelGenerationModeTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string TOUCH = "M:Acme.Api.Touch(System.Object)";
    private const string ALPHA = "M:Acme.Api.Alpha(System.Object)";
    private const string POKE = "M:Acme.Api.Poke(System.Object)";
    private const string LIBRARY_MEMBERS = """
        public static void Touch(object value) { }
        public static void Alpha(object value) { }
        public static void Poke(object value) { }
        """;

    private const string STATE = "public class State { public int Count = 1; public int Total; }";
    private const string ENTRY_POINT = "public static class Program { public static void Main() { } }";

    private readonly GenerationInstall _install = new();
    private readonly string _repository = Directory.CreateTempSubdirectory("ch-generation-mode-").FullName;
    private readonly List<string> _requested = [];
    private int _analyses;

    public ModelGenerationModeTests() => _install.SharedFramework("8.0.5");

    public void Dispose()
    {
        _install.Dispose();
        Directory.Delete(_repository, recursive: true);
    }

    [Fact]
    public async Task ReadOnly_applies_a_stored_model()
    {
        var solution = Solution("Acme.Api.Touch(_state);");
        var off = await Run(solution, ModelGeneration.None);
        _ = await Run(solution, ModelGeneration.Generate);
        _requested.Clear();
        var stored = Files();
        Assert.Single(stored);

        var result = await Run(solution, ModelGeneration.ReadOnly);

        // The model the generating run wrote describes the call, as a stored model does in a generating run: and nothing is generated or written.
        Assert.Empty(_requested);
        Assert.Equal((1, 0), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN)));
        Assert.True(Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED) >= 1);
        Assert.NotEmpty(CountFindings(off));
        Assert.Empty(CountFindings(result));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.Empty(result.GenerationWanted);
        Assert.Equal(stored, Files());
    }

    [Fact]
    public async Task ReadOnly_names_a_file_of_another_version_and_lists_its_target()
    {
        var image = Library("main");
        var solution = Solution("Acme.Api.Touch(_state);", image);
        var absent = await Run(solution, ModelGeneration.ReadOnly);
        _ = await Run(solution, ModelGeneration.Generate);
        var path = KeyOf(image).PathUnder(_repository)!;
        File.WriteAllText(path, File.ReadAllText(path).Replace(GeneratorVersion.Fingerprint, "another-generator", StringComparison.Ordinal));
        _requested.Clear();
        var stored = Files();

        var result = await Run(solution, ModelGeneration.ReadOnly);

        Assert.Empty(_requested);
        Assert.Equal(stored, Files());
        Assert.Equal(absent.GenerationWanted, result.GenerationWanted);
        Assert.Equal([(TOUCH, GenerationCauses.NOT_GENERATED)],
                     Assert.Single(result.Coverage).GenerationRefusals.Select(item => (item.MemberId, item.Cause)));
        Assert.Equal(0, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        var display = Path.GetRelativePath(_repository, path).Replace('\\', '/');
        Assert.EndsWith($"{display}: written by another generator version; its answers are not used.",
                        Assert.Single(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains(display, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Generate_replaces_a_file_of_another_version_whole()
    {
        var image = Library("main");
        _ = await Run(Solution("Acme.Api.Touch(_state); Acme.Api.Alpha(_state);", image), ModelGeneration.Generate);
        var key = KeyOf(image);
        var path = key.PathUnder(_repository)!;
        File.WriteAllText(path, File.ReadAllText(path).Replace(GeneratorVersion.Fingerprint, "another-generator", StringComparison.Ordinal));
        _requested.Clear();

        var result = await Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate);

        Assert.Equal([TOUCH], _requested);
        Assert.Equal(1, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.Empty(CountFindings(result));
        var file = GeneratedModelFiles.Read(_repository, key);
        Assert.Empty(file.Diagnostics);
        Assert.Equal([TOUCH], file.Entries.Keys);
        Assert.Contains($"\"generator\": \"{GeneratorVersion.Fingerprint}\"", File.ReadAllText(path), StringComparison.Ordinal);

        // The next write in the same version keeps the first run's answer and adds the newly reached member.
        _requested.Clear();
        _ = await Run(Solution("Acme.Api.Touch(_state); Acme.Api.Poke(_state);", image), ModelGeneration.Generate);
        Assert.Equal([POKE], _requested);
        Assert.Equal(new[] { POKE, TOUCH }, GeneratedModelFiles.Read(_repository, key).Entries.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadOnly_writes_nothing_and_lists_the_wanted_target()
    {
        var image = Library("main");
        var solution = Solution("Acme.Api.Touch(_state); Acme.Api.Touch(_state); Acme.Api.Alpha(_state); Acme.Api.Poke(_state);", image);
        WriteProjectModel($$"""{"member": "{{POKE}}", "opaque": true}""");
        var projectModels = Files();

        var result = await Run(solution, ModelGeneration.ReadOnly);

        // Nothing is generated, and nothing is created beside the project's own model: the generated layer has no folder.
        Assert.Empty(_requested);
        Assert.Equal(projectModels, Files());
        Assert.False(Directory.Exists(Path.Combine(_repository, ".concurrency-hunter", "models", "generated")));
        // The members a generating run would have generated, once each and in ordinal order; the member a project entry describes is none.
        var key = KeyOf(image).Text;
        Assert.Equal([new WantedGeneration(ALPHA, key), new WantedGeneration(TOUCH, key)], result.GenerationWanted);
        Assert.Equal([(ALPHA, GenerationCauses.NOT_GENERATED), (TOUCH, GenerationCauses.NOT_GENERATED)],
                     Assert.Single(result.Coverage).GenerationRefusals.Select(item => (item.MemberId, item.Cause)));
        Assert.Equal(0, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));

        // A run that generates lists nothing as wanted: it generated the members itself; one with the layer off does not look.
        Assert.Empty((await Run(solution, ModelGeneration.Generate)).GenerationWanted);
        Assert.Empty((await Run(solution, ModelGeneration.None)).GenerationWanted);
    }

    [Fact]
    public async Task ReadOnly_lists_a_target_of_two_scopes_once()
    {
        var result = await Run(TwoScopes("Acme.Api.Touch(_state);", "Acme.Api.Touch(_state); Acme.Api.Alpha(_state);"), ModelGeneration.ReadOnly);

        Assert.Equal(["A", "B"], result.Scopes.Select(scope => scope.Id));
        Assert.Equal([ALPHA, TOUCH], result.GenerationWanted.Select(item => item.MemberId));
    }

    [Fact]
    public async Task None_ignores_a_stored_model()
    {
        var solution = Solution("Acme.Api.Touch(_state);");
        var off = await Run(solution, ModelGeneration.None);
        _ = await Run(solution, ModelGeneration.Generate);
        _requested.Clear();
        var stored = Files();
        Assert.Single(stored);

        var result = await Run(solution, ModelGeneration.None);

        // The stored model describes nothing, the target is not looked for, and nothing is generated or written.
        Assert.Empty(_requested);
        Assert.Equal(0, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(Counter(off, CoverageCounters.OPAQUE_CALL), Counter(result, CoverageCounters.OPAQUE_CALL));
        Assert.Equal(Fingerprints(off), Fingerprints(result));
        Assert.NotEmpty(CountFindings(result));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.Empty(result.GenerationWanted);
        Assert.Equal(stored, Files());
    }

    [Fact]
    public void Analyzer_overloads_default_to_ReadOnly()
    {
        var overloads = typeof(PhaseOneAnalyzer).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                                .Where(method => method.Name == nameof(PhaseOneAnalyzer.AnalyzeAsync))
                                                .ToArray();

        // An overload either names the whole setup of the run (the mode, and for a test the install and the generator), or ends in the mode,
        // after the token, with ReadOnly as its default: so that no caller that passes none generates or writes.
        var withSetup = overloads.Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(GenerationSetup))).ToArray();
        var withMode = overloads.Except(withSetup).ToArray();
        Assert.NotEmpty(withSetup);
        Assert.Contains(withMode, method => method.IsPublic);
        Assert.All(withMode, method =>
        {
            var parameters = method.GetParameters();
            Assert.Equal(typeof(ModelGeneration), parameters[^1].ParameterType);
            Assert.True(parameters[^1].HasDefaultValue, $"{method} has no default mode");
            Assert.Equal(ModelGeneration.ReadOnly, parameters[^1].DefaultValue);
            Assert.Equal(typeof(CancellationToken), parameters[^2].ParameterType);
        });
    }

    [Fact]
    public async Task Compilations_created_by_a_run_are_released_after_it()
    {
        var library = Emitted("Fixture.Created");
        LibraryCompilationResult? during = null;

        _ = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(library, token);
            return Model(request);
        });

        // The run created the entry, and it left the cache with the run: compiling again builds another.
        Assert.NotNull(during);
        Assert.Equal(["M:Acme.Api.Touch(System.Object)"], _requested);
        Assert.NotSame(during, LibraryCompilation.Compile(library, CancellationToken.None));
    }

    [Fact]
    public async Task Compilations_are_released_after_a_cancelled_run()
    {
        var library = Emitted("Fixture.Cancelled");
        LibraryCompilationResult? during = null;
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(library, token);
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Model(request);
        }, cancel.Token));

        Assert.NotNull(during);
        Assert.NotSame(during, LibraryCompilation.Compile(library, CancellationToken.None));
    }

    [Fact]
    public async Task Compilations_are_released_after_an_analysis_failure()
    {
        var library = Emitted("Fixture.Failed");
        LibraryCompilationResult? during = null;

        // The generation is done; the analysis with its model is what fails, on the scope's second pairing.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(library, token);
            return Model(request);
        }, pairing: FailingSecond));

        Assert.Equal("the second pairing failed", error.Message);
        Assert.Equal(2, _analyses);
        Assert.NotNull(during);
        Assert.NotSame(during, LibraryCompilation.Compile(library, CancellationToken.None));
    }

    [Fact]
    public async Task Compilation_of_a_generation_that_throws_after_compiling_is_released()
    {
        var library = Emitted("Fixture.Throws");
        LibraryCompilationResult? during = null;

        var result = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(library, token);
            throw new InvalidOperationException("the generator broke after it compiled");
        });

        // The generation failed, which a file does not keep, and the entry it created has been handed over all the same.
        Assert.Equal(GenerationOutcome.FAILED, Assert.Single(Assert.Single(result.Coverage).GenerationRefusals).Cause);
        Assert.NotNull(during);
        Assert.NotSame(during, LibraryCompilation.Compile(library, CancellationToken.None));
    }

    [Fact]
    public async Task Compilations_present_before_the_run_are_kept()
    {
        var library = Emitted("Fixture.Before");
        var before = LibraryCompilation.Compile(library, CancellationToken.None);
        LibraryCompilationResult? during = null;

        _ = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(library, token);
            return Model(request);
        });

        // The generation met the entry and did not create it: it stays for whoever compiles the library next.
        Assert.Same(before, during);
        Assert.Same(before, LibraryCompilation.Compile(library, CancellationToken.None));
    }

    [Fact]
    public async Task Compilation_another_caller_created_during_the_run_stays()
    {
        var own = Emitted("Fixture.Own");
        var foreign = Emitted("Fixture.Foreign");
        using var go = new ManualResetEventSlim();
        // Started before the run opens any scope, so that it is another caller's flow.
        var other = Task.Run(() =>
        {
            go.Wait();
            return LibraryCompilation.Compile(foreign, CancellationToken.None);
        });
        LibraryCompilationResult? created = null;
        LibraryCompilationResult? during = null;

        _ = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, dependencies, token) =>
        {
            during = LibraryCompilation.Compile(own, token);
            go.Set();
            created = other.GetAwaiter().GetResult();
            return Model(request);
        });

        Assert.NotNull(created);
        Assert.Same(created, LibraryCompilation.Compile(foreign, CancellationToken.None));
        Assert.NotSame(during, LibraryCompilation.Compile(own, CancellationToken.None));
    }

    [Fact]
    public void Compilation_created_again_after_a_release_is_not_removed()
    {
        var library = Emitted("Fixture.Again");
        var ownership = new CompilationOwnership();
        LibraryCompilationResult first;
        using (ownership.Open())
            first = LibraryCompilation.Compile(library, CancellationToken.None);
        var owned = Assert.Single(ownership.Created);

        LibraryCompilation.Release(ownership.Created);
        var again = LibraryCompilation.Compile(library, CancellationToken.None);
        // The record is stale now: the key maps to another instance, which the run did not add.
        LibraryCompilation.Release([owned]);

        Assert.NotSame(first, again);
        Assert.Same(again, LibraryCompilation.Compile(library, CancellationToken.None));
        // A compilation made with no scope open is recorded nowhere.
        Assert.Single(ownership.Created);
    }

    [Fact]
    public async Task ReadOnly_and_None_runs_release_nothing()
    {
        var library = Emitted("Fixture.Reading");
        var before = LibraryCompilation.Compile(library, CancellationToken.None);
        var solution = Solution("Acme.Api.Touch(_state);");

        foreach (var mode in new[] { ModelGeneration.ReadOnly, ModelGeneration.None })
        {
            _ = await Run(solution, mode, (request, dependencies, token) =>
            {
                Assert.Fail("A run that does not generate called the generator.");
                return Model(request);
            });

            Assert.Same(before, LibraryCompilation.Compile(library, CancellationToken.None));
        }

        Assert.Empty(_requested);
    }

    /// <summary>One analysis of a solution, counting the analyses of its scopes and the members the fake generator is asked for.</summary>
    /// <param name="solution">The solution.</param>
    /// <param name="mode">What the run does with the generated layer.</param>
    /// <param name="generator">The generator call; a model with no effects for any member when omitted.</param>
    /// <param name="token">The run's token.</param>
    /// <param name="pairing">The pairing of each scope's accesses; the analyzer's, counting the analyses, when omitted.</param>
    private Task<AnalysisResult> Run(Solution solution, ModelGeneration mode, Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer>? generator = null,
                                     CancellationToken token = default,
                                     Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis>? pairing = null)
    {
        generator ??= (request, _, _) => Model(request);
        return PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, _repository, ProviderRegistry.BuiltIn, pairing ?? Counted, AnalysisLimits.Default,
                                             Z3ConstraintSolver.Create,
                                             new GenerationSetup(mode, _install.Resolver(), (request, dependencies, cancellation) =>
                                             {
                                                 _requested.Add(request.MemberId);
                                                 return generator(request, dependencies, cancellation);
                                             }), token);
    }

    private PairAnalysis Counted(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap)
    {
        _analyses++;
        return InterproceduralPairing.Pair(accesses, executions, heap);
    }

    private PairAnalysis FailingSecond(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap) =>
        ++_analyses == 2 ? throw new InvalidOperationException("the second pairing failed") : InterproceduralPairing.Pair(accesses, executions, heap);

    /// <summary>The application: a controller that reads a state, and a hosted service that runs the work.</summary>
    /// <param name="work">The statements of the service's <c>ExecuteAsync</c>.</param>
    /// <param name="extra">Further types of the file.</param>
    private static string Source(string work, string extra = "") => Usings + STATE + extra + $$"""
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

    /// <summary>A solution of one project that references the package's asset, written under the fake packages folder.</summary>
    /// <param name="work">The statements of the service's <c>ExecuteAsync</c>.</param>
    /// <param name="image">The package's asset, or a library of the fixture's.</param>
    private Solution Solution(string work, byte[]? image = null)
    {
        image ??= Library("main");
        return FixtureSolution.Create(Options(image), ("Case.cs", Source(work)));
    }

    /// <summary>A solution of two applications, A and B, each with its own state, controller and service; they reference the same package asset.</summary>
    /// <param name="first">The work of A's service.</param>
    /// <param name="second">The work of B's service.</param>
    private Solution TwoScopes(string first, string second)
    {
        var options = Options(Library("main")) with
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["A"] = OutputKind.ConsoleApplication, ["B"] = OutputKind.ConsoleApplication }
        };
        return FixtureSolution.CreateProjects(options, ("A", "Case.cs", Source(first, ENTRY_POINT)), ("B", "Case.cs", Source(second, ENTRY_POINT)));
    }

    /// <summary>The options of a fixture whose projects reference the package's asset, and a marker of the reference pack that tells the
    /// framework the projects target.</summary>
    /// <param name="image">The package's asset.</param>
    private FixtureOptions Options(byte[] image)
    {
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        return new FixtureOptions
        {
            MetadataReferences =
            [
                ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, "1.0.0", "lib", "net8.0", PACKAGE)),
                ReferenceScopes.Reference(EmittedAssemblies.Image("Fixture.Pack"), ReferenceScopes.PackFile(_install, 8, "Microsoft.NETCore.App.Ref", "Fixture.Pack.dll"))
            ]
        };
    }

    /// <summary>The package's library, told apart from another asset by a comment: a different source is a different MVID.</summary>
    /// <param name="comment">What tells it apart.</param>
    private static byte[] Library(string comment)
    {
        var compilation = CSharpCompilation.Create(PACKAGE,
                                                   [CSharpSyntaxTree.ParseText(ReferenceScopes.VERSION_ATTRIBUTE +
                                                                               "namespace Acme { public static class Api {\n" + LIBRARY_MEMBERS + "\n} }\n// " + comment)],
                                                   EmittedAssemblies.RuntimeReferences,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return stream.ToArray();
    }

    /// <summary>A tiny assembly emitted into the install, as the resolver would describe it, for the generator to compile for real. Each
    /// name is a path of its own, so each test has cache entries of its own.</summary>
    /// <param name="name">The assembly name.</param>
    private ImplementationAssembly Emitted(string name)
    {
        var path = EmittedAssemblies.Write(Path.Combine(_install.Root, name, name + ".dll"), name, "public sealed class Box { public int Value() => 1; }");
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return new ImplementationAssembly(name, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
                                          EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll")).ToArray(),
                                          [], "0.0.0.0", "", Guid.Empty);
    }

    /// <summary>Writes a project model file that declares one member of the package's assembly.</summary>
    /// <param name="entry">The entry's JSON.</param>
    private void WriteProjectModel(string entry)
    {
        var models = Directory.CreateDirectory(Path.Combine(_repository, ".concurrency-hunter", "models")).FullName;
        File.WriteAllText(Path.Combine(models, "model.json"), $$"""{"schemaVersion": 1, "assemblies": ["{{PACKAGE}}"], "models": [{{entry}}]}""", new UTF8Encoding(false));
    }

    /// <summary>Every file of the repository root with a hash of its bytes, in path order: what a run leaves behind.</summary>
    private IReadOnlyList<(string Path, string Hash)> Files() =>
        Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories)
                 .Order(StringComparer.Ordinal)
                 .Select(path => (Path.GetRelativePath(_repository, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
                 .ToArray();

    private static GeneratedAnswer Model(GenerationRequest request) =>
        new(3, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null,
            new LibraryModel(request.MemberId, [new SupportedAssemblyVersion(PACKAGE, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))], []), null, Record());

    private static GenerationRecord Record() =>
        new(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0);

    private static int Counter(AnalysisResult result, string counter) => Assert.Single(result.Coverage).Skips[counter];

    /// <summary>The findings on the state's count, which the hosted service's unknown effect makes against the controller's read.</summary>
    /// <param name="result">The analysis.</param>
    private static IReadOnlyList<Finding> CountFindings(AnalysisResult result) =>
        result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Count"]) || finding.Resource.IsWildcard).ToArray();

    private static string[] Fingerprints(AnalysisResult result) => result.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The key of the generated file of the package's asset.</summary>
    /// <param name="image">The asset.</param>
    private static GeneratedFileKey KeyOf(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        return GeneratedFileKey.ForPackage(PACKAGE, new Version(1, 0, 0, 0), PACKAGE, "1.0.0", reader.GetGuid(reader.GetModuleDefinition().Mvid));
    }
}
