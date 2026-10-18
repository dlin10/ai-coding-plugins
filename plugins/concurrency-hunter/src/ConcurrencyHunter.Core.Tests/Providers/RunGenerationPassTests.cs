using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

/// <summary>A run's generation in the analysis (R1, R2, R4, R5): the scope analysed without the generated layer, the members its direct opaque
/// calls reach that no layer describes generated, and the scope analysed again when the layer then describes one of its calls. A fake generator
/// and a package in a fake NuGet layout stand for the real ones; the application is a controller that reads a state and a hosted service that
/// hands it to the library.</summary>
public sealed class RunGenerationPassTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string LIBRARY_MEMBERS = """
        public static void Touch(object value) { }
        public static void Poke(object value) { }
        public static void Alpha(object value) { }
        public static void Run(System.Action callback) { }
        public static bool TryTouch(object value, out object result) { result = value; return true; }
        public static bool Same(object left, object right) => true;
        public static void F() { }
        public static unsafe void F(delegate*<void> callback) { }
        """;

    private const string FILL = "public static void Fill(Dep.Item item, Dep.Base[] bag) { }";

    private const string DEP_PLAIN = "namespace Dep { public class Base { } public class Item { } }";
    private const string DEP_INHERITING = "namespace Dep { public class Base { } public class Item : Base { } }";
    private const string STATE = "public class State { public int Count = 1; public int Total; }";
    private const string ENTRY_POINT = "public static class Program { public static void Main() { } }";

    private readonly GenerationInstall _install = new();
    private readonly string _repository = Directory.CreateTempSubdirectory("ch-run-pass-").FullName;
    private readonly List<string> _requested = [];
    private int _analyses;

    public RunGenerationPassTests() => _install.SharedFramework("8.0.5");

    public void Dispose()
    {
        _install.Dispose();
        Directory.Delete(_repository, recursive: true);
    }

    [Fact]
    public async Task Scope_without_generated_calls_is_analysed_once()
    {
        var solution = Solution("Acme.Api.Touch(_state);");

        // The generator refuses: no model describes the call, so there is nothing to analyse again.
        var refused = await Run(solution, ModelGeneration.Generate, Refusal);
        Assert.Equal(1, _analyses);
        Assert.Equal(["M:Acme.Api.Touch(System.Object)"], _requested);
        Assert.Equal((0, 0, 0), (Counter(refused, CoverageCounters.KNOWN_CALL_GENERATED), Counter(refused, CoverageCounters.GENERATED_IN_RUN),
                                 Counter(refused, CoverageCounters.PAIRS_REMOVED_GENERATED)));
        Assert.NotEmpty(CountFindings(refused));

        // A read-only run with nothing stored, and a run with the layer off, have nothing to apply either.
        _analyses = 0;
        var readOnly = await Run(solution, ModelGeneration.ReadOnly);
        var off = await Run(solution, ModelGeneration.None);
        Assert.Equal(2, _analyses);
        Assert.Equal(Fingerprints(off), Fingerprints(readOnly));
        Assert.Equal(Fingerprints(off), Fingerprints(refused));

        // A scope with no library call a target is analysed once as well.
        _analyses = 0;
        _ = await Run(Solution("_state.Total++;"), ModelGeneration.Generate);
        Assert.Equal(1, _analyses);
    }

    [Fact]
    public async Task Scope_with_a_generated_call_is_analysed_twice_and_keeps_the_second()
    {
        var solution = Solution("Acme.Api.Touch(_state);");
        var off = await Run(solution, ModelGeneration.None);
        _analyses = 0;

        var result = await Run(solution, ModelGeneration.Generate);

        Assert.Equal(2, _analyses);
        // The result is the second analysis: the call is a known call of the generated layer and the pair its unknown effect made is gone.
        Assert.Equal(Counter(off, CoverageCounters.OPAQUE_CALL) - 1, Counter(result, CoverageCounters.OPAQUE_CALL));
        Assert.Equal((1, 1), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN)));
        Assert.Equal(Counter(off, CoverageCounters.KNOWN_CALL) + 1, Counter(result, CoverageCounters.KNOWN_CALL));
        Assert.NotEmpty(CountFindings(off));
        Assert.Empty(CountFindings(result));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
    }

    [Fact]
    public async Task Pair_the_unknown_effect_made_and_the_model_did_not_is_removed()
    {
        var solution = Solution("Acme.Api.Touch(_state);");
        var off = await Run(solution, ModelGeneration.None);

        var result = await Run(solution, ModelGeneration.Generate);

        // The pair of the call's unknown effect and the controller's read is the analysis without the layer's, not the one with it.
        Assert.NotEmpty(CountFindings(off));
        Assert.Empty(CountFindings(result));
        Assert.True(Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED) >= 1);
        Assert.Equal(0, Counter(off, CoverageCounters.PAIRS_REMOVED_GENERATED));
    }

    [Fact]
    public async Task Pair_present_in_both_analyses_is_not_counted()
    {
        var solution = Solution("Acme.Api.Touch(_state); Acme.Api.Poke(_state);");
        var onlyTouch = Answering(("Touch", Model), ("Poke", Refusal));
        var touchAlone = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate);
        var removedByTouch = Counter(touchAlone, CoverageCounters.PAIRS_REMOVED_GENERATED);
        Assert.True(removedByTouch >= 1);

        var result = await Run(solution, ModelGeneration.Generate, onlyTouch);

        // `Poke` stays opaque in both analyses and its pair with the read is one of both: only what `Touch` made is removed.
        Assert.Equal(removedByTouch, Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
        Assert.NotEmpty(CountFindings(result));
    }

    [Fact]
    public async Task Pair_identity_survives_a_lowering_that_changes_operation_ids()
    {
        var solution = Solution("Acme.Api.TryTouch(_state, out var kept); Acme.Api.Poke(_state);");
        var off = await Run(solution, ModelGeneration.None);
        var generator = Answering(("TryTouch", ModelWithOutput), ("Poke", Refusal));

        var result = await Run(solution, ModelGeneration.Generate, generator);

        // The model's output changes how the call is lowered, so the operations after it are numbered differently in the two analyses.
        var pokeBefore = LastUnknownEffect(off);
        var pokeAfter = LastUnknownEffect(result);
        Assert.Equal(pokeBefore.Source, pokeAfter.Source);
        Assert.NotEqual(pokeBefore.OperationId, pokeAfter.OperationId);
        // `Poke`'s pair is one pair in both: only the pairs `TryTouch` made are removed.
        var touchAlone = await Run(Solution("Acme.Api.TryTouch(_state, out var kept);"), ModelGeneration.Generate, generator);
        Assert.True(Counter(touchAlone, CoverageCounters.PAIRS_REMOVED_GENERATED) >= 1);
        Assert.Equal(Counter(touchAlone, CoverageCounters.PAIRS_REMOVED_GENERATED), Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
        Assert.NotEmpty(CountFindings(result));
    }

    [Fact]
    public async Task Pairs_of_one_site_pair_in_two_executions_are_one()
    {
        // One site, `Hit.Touch`, reached by two roots; one read site. The pair of the two sites is met in two executions.
        var solution = FixtureSolution.Create(Options(Library("main"), "1.0.0"), ("Case.cs", Usings + STATE + """
            public static class Hit { public static void Touch(State state) { Acme.Api.Touch(state); } }
            [ApiController]
            public sealed class StateController : ControllerBase
            {
                private readonly State _state;
                public StateController(State state) => _state = state;
                [HttpGet("/read")] public int Read() => _state.Count;
                [HttpGet("/first")] public void First() => Hit.Touch(_state);
                [HttpGet("/second")] public void Second() => Hit.Touch(_state);
            }
            """ + Startup("services.AddSingleton<State>();")));
        var off = await Run(solution, ModelGeneration.None);
        var executions = off.Accesses.Where(access => access.Operation == AccessOperation.UnknownEffect).Select(access => access.ExecutionId).Distinct().Count();
        Assert.True(executions >= 2);
        // The findings fold the pairs of one site pair, each found once per pair of executions; there are more pairs than findings.
        Assert.True(off.Findings.Sum(finding => finding.OccurrenceCount) > off.Findings.Count);

        var result = await Run(solution, ModelGeneration.Generate);

        // The model removes every pair, and one site pair met in several executions is one pair identity: as many as there are findings.
        Assert.Empty(result.Findings);
        Assert.Equal(off.Findings.Count, Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
    }

    [Fact]
    public async Task Two_resources_with_one_display_text_are_two_identities()
    {
        // `Step` and `Read` are one site each; handed two objects they make a pair on each: two resources that display alike.
        var solution = FixtureSolution.Create(Options(Library("main"), "1.0.0"), ("Case.cs", Usings + STATE + """
            public sealed class One : State { }
            public sealed class Two : State { }
            public static class Shared
            {
                public static void Step(State state) { Acme.Api.Touch(state); }
                public static int Read(State state) => state.Count;
            }
            [ApiController]
            public sealed class StateController : ControllerBase
            {
                private readonly One _one;
                private readonly Two _two;
                public StateController(One one, Two two) { _one = one; _two = two; }
                [HttpGet("/read")] public int Get() => Shared.Read(_one) + Shared.Read(_two);
            }
            public sealed class StateWorker : BackgroundService
            {
                private readonly One _one;
                private readonly Two _two;
                public StateWorker(One one, Two two) { _one = one; _two = two; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Shared.Step(_one);
                    Shared.Step(_two);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<One>(); services.AddSingleton<Two>(); services.AddHostedService<StateWorker>();")));
        var off = await Run(solution, ModelGeneration.None);
        var resources = off.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Count"])).Select(finding => finding.Resource.RegionKey).Distinct().ToArray();
        Assert.Equal(2, resources.Length);

        var result = await Run(solution, ModelGeneration.Generate);

        // The pairs of the one site pair on the two resources are two.
        Assert.Equal(2, Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
    }

    [Fact]
    public async Task Generating_run_without_concurrency_hunter_folder_creates_models_generated()
    {
        Assert.False(Directory.Exists(Path.Combine(_repository, ".concurrency-hunter")));

        _ = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate);

        var generated = Path.Combine(_repository, ".concurrency-hunter", "models", "generated");
        var file = Assert.Single(Directory.GetFiles(generated, "*.json", SearchOption.AllDirectories));
        Assert.Equal(Path.Combine(generated, PACKAGE, "1.0.0") + Path.DirectorySeparatorChar, Path.GetDirectoryName(file) + Path.DirectorySeparatorChar);
        Assert.Contains("\"member\": \"M:Acme.Api.Touch(System.Object)\"", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_pairs_one_target_removes_count_two()
    {
        var solution = Solution("Acme.Api.Touch(_state); Acme.Api.Touch(_state);");

        var result = await Run(solution, ModelGeneration.Generate);

        // Two sites of one target, a pair each with the read: two pairs removed, one target generated.
        Assert.Equal(2, Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
        Assert.Equal(1, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.Equal(2, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
    }

    [Fact]
    public async Task Stored_model_is_not_counted_in_run()
    {
        var solution = Solution("Acme.Api.Touch(_state);");
        _ = await Run(solution, ModelGeneration.Generate);
        _requested.Clear();
        _analyses = 0;

        var result = await Run(solution, ModelGeneration.Generate);

        // The second run reads the model the first wrote: it applies, and the run generated nothing.
        Assert.Empty(_requested);
        Assert.Equal(2, _analyses);
        Assert.Equal((1, 0), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN)));
        Assert.True(Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED) >= 1);
        Assert.Empty(CountFindings(result));
    }

    [Fact]
    public async Task Model_generated_in_an_earlier_scope_counts_in_a_later_scope_with_that_target()
    {
        var solution = TwoScopes("Acme.Api.Touch(_state);", "Acme.Api.Touch(_state);");

        var result = await Run(solution, ModelGeneration.Generate);

        // The first scope generated `Touch`; the second reads it from the run's answers, and counts the target all the same.
        Assert.Equal(["M:Acme.Api.Touch(System.Object)"], _requested);
        Assert.Equal(["A", "B"], result.Scopes.Select(scope => scope.Id));
        Assert.All(result.Coverage, scope => Assert.Equal((1, 1), (scope.Skips[CoverageCounters.GENERATED_IN_RUN], scope.Skips[CoverageCounters.KNOWN_CALL_GENERATED])));
    }

    [Fact]
    public async Task Unreadable_file_still_applies_the_new_model_in_this_run()
    {
        var image = Library("main");
        var solution = Solution("Acme.Api.Touch(_state);", image);
        var path = StoredFile(image);
        var before = File.ReadAllBytes(path);

        GeneratedModelFile? seen = null;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await Run(solution, ModelGeneration.Generate);

            seen = GeneratedModelFiles.Read(_repository, KeyOf(image));
            Assert.True(seen.IsUnreadable);
            // The model describes the call in this run, though the file could not be read and so is not rewritten.
            Assert.Equal(["M:Acme.Api.Touch(System.Object)"], _requested);
            Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
            Assert.Empty(CountFindings(result));
            Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.StartsWith("library-models: generated file ", StringComparison.Ordinal));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Failed_write_still_applies_the_new_model_in_this_run()
    {
        var image = Library("main");
        var solution = Solution("Acme.Api.Touch(_state);", image);
        var path = StoredFile(image);
        var before = File.ReadAllBytes(path);
        FileStream? held = null;

        // The file reads well when the run starts; a moment before the run writes it, something opens it without sharing.
        try
        {
            var result = await Run(solution, ModelGeneration.Generate, (request, token) =>
            {
                held ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return Model(request);
            });

            Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
            Assert.Empty(CountFindings(result));
            Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.StartsWith("library-models: generated file ", StringComparison.Ordinal));
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Order_is_per_scope_in_discovery_order()
    {
        // The scopes are discovered by id, A before B; scope B's member sorts before scope A's.
        var solution = TwoScopes("Acme.Api.Poke(_state);", "Acme.Api.Alpha(_state);");

        _ = await Run(solution, ModelGeneration.Generate);

        Assert.Equal(["M:Acme.Api.Poke(System.Object)", "M:Acme.Api.Alpha(System.Object)"], _requested);
    }

    [Fact]
    public async Task Member_reached_only_through_a_model_is_listed_reached_through_model()
    {
        var solution = Solution("Acme.Api.Same(new Key(), _state);", extra: """
            public sealed class Key
            {
                public override bool Equals(object? other) { Acme.Api.Poke(other!); return true; }
                public override int GetHashCode() => 1;
            }
            """);
        var generator = Answering(("Same", request => ModelWithEquals(request)), ("Poke", Model));

        var result = await Run(solution, ModelGeneration.Generate, generator);

        // `Poke` is called by an override the model of `Same` makes reachable: the first analysis never met it, so it is neither generated nor kept.
        Assert.Equal(["M:Acme.Api.Same(System.Object,System.Object)"], _requested);
        var item = Assert.Single(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.Equal(("M:Acme.Api.Poke(System.Object)", GenerationCauses.REACHED_THROUGH_MODEL), (item.MemberId, item.Cause));
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
    }

    [Fact]
    public async Task Call_of_a_function_pointer_overload_is_no_target_and_its_parameterless_sibling_is_one()
    {
        // The id of `F(delegate*<void>)` is `M:Acme.Api.F()`, which finds the parameterless `F()`: whose own id is `M:Acme.Api.F`.
        var baseline = Counter(await Run(Solution("_state.Total++;"), ModelGeneration.Generate), CoverageCounters.OPAQUE_CALL);
        var pointer = await Run(Solution("unsafe { Acme.Api.F(default); }"), ModelGeneration.Generate);

        // The call is an opaque call of the scope, and no target.
        Assert.Equal(baseline + 1, Counter(pointer, CoverageCounters.OPAQUE_CALL));
        Assert.Empty(_requested);
        Assert.Empty(Assert.Single(pointer.Coverage).GenerationRefusals);
        Assert.Equal(0, Counter(pointer, CoverageCounters.GENERATED_IN_RUN));

        var plain = await Run(Solution("Acme.Api.F();"), ModelGeneration.Generate);

        Assert.Equal(["M:Acme.Api.F"], _requested);
        Assert.Equal(1, Counter(plain, CoverageCounters.GENERATED_IN_RUN));
        Assert.Equal(baseline, Counter(plain, CoverageCounters.OPAQUE_CALL));
    }

    [Fact]
    public async Task Member_a_project_entry_declares_opaque_is_neither_generated_nor_listed()
    {
        Directory.CreateDirectory(Path.Combine(_repository, ".concurrency-hunter", "models"));
        File.WriteAllText(Path.Combine(_repository, ".concurrency-hunter", "models", "model.json"),
                          "{\"schemaVersion\": 1, \"assemblies\": [\"Acme.Lib\"], \"models\": [{\"member\": \"M:Acme.Api.Touch(System.Object)\", \"opaque\": true}]}",
                          new UTF8Encoding(false));

        var result = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate);

        // A project entry describes the member, so no generated model is missing for it.
        Assert.Empty(_requested);
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.False(Directory.Exists(Path.Combine(_repository, ".concurrency-hunter", "models", "generated")));
    }

    [Fact]
    public async Task Generated_in_run_counts_a_target_once_for_repeated_calls()
    {
        var result = await Run(Solution("Acme.Api.Touch(_state); Acme.Api.Touch(_state); Acme.Api.Touch(_state);"), ModelGeneration.Generate);

        Assert.Equal(["M:Acme.Api.Touch(System.Object)"], _requested);
        Assert.Equal((1, 3), (Counter(result, CoverageCounters.GENERATED_IN_RUN), Counter(result, CoverageCounters.KNOWN_CALL_GENERATED)));
    }

    [Fact]
    public async Task New_model_that_does_not_apply_in_one_project_is_not_generated_again()
    {
        var solution = TwoProjects();

        var first = await Run(solution, ModelGeneration.Generate, request => FillModel(request));

        // The model stores the item in the bag, which converts where `Item` derives from `Base`: in the first project only.
        Assert.Equal(["M:Acme.Api.Fill(Dep.Item,Dep.Base[])"], _requested);
        Assert.Equal(1, Counter(first, CoverageCounters.KNOWN_CALL_GENERATED));
        var item = Assert.Single(Assert.Single(first.Coverage).GenerationRefusals);
        Assert.Equal(("M:Acme.Api.Fill(Dep.Item,Dep.Base[])", GenerationCauses.NOT_APPLICABLE), (item.MemberId, item.Cause));
        Assert.Contains(Assert.Single(first.Coverage).Diagnostics, diagnostic => diagnostic.Contains("does not apply to compilation", StringComparison.Ordinal));

        // The entry stays in the file, and a later run reads it: nothing is generated again, and the target is listed as before.
        _requested.Clear();
        var second = await Run(solution, ModelGeneration.Generate, request => FillModel(request));
        Assert.Empty(_requested);
        Assert.Equal(1, Counter(second, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(0, Counter(second, CoverageCounters.GENERATED_IN_RUN));
        Assert.Equal(GenerationCauses.NOT_APPLICABLE, Assert.Single(Assert.Single(second.Coverage).GenerationRefusals).Cause);
    }

    [Theory]
    [InlineData("Acme.Api.Run(() => { });", true)]
    [InlineData("Pass(() => { });", false)]
    [InlineData("Acme.Api.Run(_callback);", false)]
    [InlineData("Acme.Api.Run(_callback); Acme.Api.Run(() => { });", true)]
    public async Task Delegate_priority_follows_the_summary_s_delegate_arguments(string runCall, bool runFirst)
    {
        var solution = Solution($"{runCall} Acme.Api.Alpha(_state);",
                                members: "private readonly System.Action _callback = () => { }; private static void Pass(System.Action callback) { Acme.Api.Run(callback); }");

        _ = await Run(solution, ModelGeneration.Generate);

        // A delegate created as an argument of a reached call comes first; one that reaches the call through a parameter or a field does
        // not, and the order is then the declaration id's, `Alpha` before `Run`.
        Assert.Equal(runFirst ? ["M:Acme.Api.Run(System.Action)", "M:Acme.Api.Alpha(System.Object)"]
                              : ["M:Acme.Api.Alpha(System.Object)", "M:Acme.Api.Run(System.Action)"],
                     _requested);
    }

    [Fact]
    public async Task Run_cancelled_during_target_discovery_calls_no_generator()
    {
        using var cancel = new CancellationTokenSource();
        var solution = Solution("Acme.Api.Touch(_state);");
        var install = _install.Resolver(nuspecRead: _ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(solution, ModelGeneration.Generate, token: cancel.Token, install: install));

        Assert.Empty(_requested);
        Assert.False(Directory.Exists(Path.Combine(_repository, ".concurrency-hunter")));
    }

    /// <summary>One analysis of a solution, counting the analyses of its scopes and the members the fake generator is asked for.</summary>
    /// <param name="solution">The solution.</param>
    /// <param name="mode">What the run does with the generated layer.</param>
    /// <param name="generator">The generator call; a model with no effects for any member when omitted.</param>
    /// <param name="token">The run's token.</param>
    /// <param name="install">The install target resolution reads, or the fixture's.</param>
    private Task<AnalysisResult> Run(Solution solution, ModelGeneration mode, Func<GenerationRequest, CancellationToken, GeneratedAnswer>? generator = null,
                                     CancellationToken token = default, ImplementationAssemblies? install = null)
    {
        generator ??= (request, _) => Model(request);
        return PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, _repository, ProviderRegistry.BuiltIn, Counted, AnalysisLimits.Default,
                                             Z3ConstraintSolver.Create,
                                             new GenerationSetup(mode, install ?? _install.Resolver(), (request, cancellation) =>
                                             {
                                                 _requested.Add(request.MemberId);
                                                 return generator(request, cancellation);
                                             }), token);
    }

    private Task<AnalysisResult> Run(Solution solution, ModelGeneration mode, Func<GenerationRequest, GeneratedAnswer> generator) =>
        Run(solution, mode, (request, _) => generator(request));

    private PairAnalysis Counted(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap)
    {
        _analyses++;
        return InterproceduralPairing.Pair(accesses, executions, heap);
    }

    /// <summary>A generator that answers each member by its name, and with a model for a member it was told nothing about.</summary>
    /// <param name="answers">The answer for each member name.</param>
    private static Func<GenerationRequest, CancellationToken, GeneratedAnswer> Answering(params (string Member, Func<GenerationRequest, GeneratedAnswer> Answer)[] answers) =>
        (request, _) => (answers.FirstOrDefault(item => request.MemberId.StartsWith("M:Acme.Api." + item.Member + "(", StringComparison.Ordinal)).Answer ?? Model)(request);

    /// <summary>The application: a controller that reads a state, and a hosted service that runs the work.</summary>
    /// <param name="work">The statements of the service's <c>ExecuteAsync</c>.</param>
    /// <param name="extra">Further types of the file.</param>
    /// <param name="members">Further members of the service.</param>
    private static string Source(string work, string extra = "", string members = "") => Usings + STATE + extra + $$"""
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
            {{members}}
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
    /// <param name="extra">Further types of the file.</param>
    /// <param name="members">Further members of the service.</param>
    private Solution Solution(string work, byte[]? image = null, string extra = "", string members = "")
    {
        image ??= Library("main");
        return FixtureSolution.Create(Options(image, "1.0.0"), ("Case.cs", Source(work, extra, members)));
    }

    /// <summary>A solution of two applications, A and B, each with its own state, controller and service; they reference the same package asset.</summary>
    /// <param name="first">The work of A's service.</param>
    /// <param name="second">The work of B's service.</param>
    private Solution TwoScopes(string first, string second)
    {
        var image = Library("main");
        var options = Options(image, "1.0.0") with
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["A"] = OutputKind.ConsoleApplication, ["B"] = OutputKind.ConsoleApplication }
        };
        return FixtureSolution.CreateProjects(options, ("A", "Case.cs", Source(first, ENTRY_POINT)), ("B", "Case.cs", Source(second, ENTRY_POINT)));
    }

    /// <summary>Two projects of one scope that reference one asset of the package and two dependencies of one name: the item derives from
    /// the base in the first project's dependency and does not in the second's.</summary>
    private Solution TwoProjects()
    {
        var (inheriting, plain) = (Emit("Dep", DEP_INHERITING), Emit("Dep", DEP_PLAIN));
        var image = Emit(PACKAGE, LibrarySource(FILL), MetadataReference.CreateFromImage(inheriting));
        var options = Options(image, "1.0.0", dependency: inheriting) with { ProjectReferences = [("First", "Second")] };
        var solution = FixtureSolution.CreateProjects(options,
                                                      ("First", "Case.cs", Usings + STATE + """
                                                          public static class FirstUse { public static void Run() { Acme.Api.Fill(null!, null!); } }
                                                          [ApiController]
                                                          public sealed class StateController : ControllerBase
                                                          {
                                                              [HttpGet("/first")] public void First() { FirstUse.Run(); SecondUse.Run(); }
                                                          }
                                                          """ + Startup()),
                                                      ("Second", "Case.cs", "public static class SecondUse { public static void Run() { Acme.Api.Fill(null!, null!); } }"));
        var second = solution.Projects.Single(project => project.Name == "Second");
        var dependency = ReferenceScopes.Reference(inheriting, DependencyPath);
        return solution.RemoveMetadataReference(second.Id, second.MetadataReferences.Single(reference => reference.Display == dependency.Display))
                       .AddMetadataReference(second.Id, ReferenceScopes.Reference(plain, DependencyPath));
    }

    private string DependencyPath => ReferenceScopes.Join(_install.Packages, "dep", "1.0.0", "lib", "net8.0", "Dep.dll");

    /// <summary>The options of a fixture whose projects reference the package's asset, a marker of the reference pack that tells the
    /// framework the projects target, and, when given, the asset's dependency.</summary>
    /// <param name="image">The package's asset.</param>
    /// <param name="version">The package version.</param>
    /// <param name="dependency">The asset's dependency, or <c>null</c> for none.</param>
    private FixtureOptions Options(byte[] image, string version, byte[]? dependency = null)
    {
        ReferenceScopes.AddAsset(_install, PACKAGE, version, "net8.0", PACKAGE, image);
        var references = new List<MetadataReference>
        {
            ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, version, "lib", "net8.0", PACKAGE)),
            ReferenceScopes.Reference(EmittedAssemblies.Image("Fixture.Pack"), ReferenceScopes.PackFile(_install, 8, "Microsoft.NETCore.App.Ref", "Fixture.Pack.dll"))
        };
        if (dependency is not null)
            references.Add(ReferenceScopes.Reference(dependency, DependencyPath));
        return new FixtureOptions { AllowUnsafe = true, MetadataReferences = references };
    }

    /// <summary>The package's library, told apart from another asset by a comment: a different source is a different MVID.</summary>
    /// <param name="comment">What tells it apart.</param>
    private static byte[] Library(string comment) => Emit(PACKAGE, LibrarySource(LIBRARY_MEMBERS) + "\n// " + comment);

    /// <summary>The source of the package's only type, <c>Acme.Api</c>, with the given members.</summary>
    /// <param name="members">The members.</param>
    private static string LibrarySource(string members) => "namespace Acme { public static class Api {\n" + members + "\n} }";

    /// <summary>An assembly of version <c>1.0.0.0</c> compiled against the running runtime and the given references.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source.</param>
    /// <param name="references">What it references besides the runtime.</param>
    private static byte[] Emit(string assemblyName, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(ReferenceScopes.VERSION_ATTRIBUTE + source)],
                                                   [.. EmittedAssemblies.RuntimeReferences, .. references],
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return stream.ToArray();
    }

    private static GeneratedAnswer Model(GenerationRequest request) => Answer(request, new LibraryModel(request.MemberId, [Range()], []));

    private static GeneratedAnswer ModelWithOutput(GenerationRequest request) =>
        Answer(request, new LibraryModel(request.MemberId, [Range()], [])
        {
            Outputs = new Dictionary<string, LibraryResult> { ["result"] = new(LibraryResultKind.New, []) }
        });

    private static GeneratedAnswer ModelWithEquals(GenerationRequest request) =>
        Answer(request, new LibraryModel(request.MemberId, [Range()], [])
        {
            Calls = [new LibraryMemberCall("Equals", new ArgumentValue("left"), [[new ArgumentValue("right")]])]
        });

    private static GeneratedAnswer FillModel(GenerationRequest request) =>
        Answer(request, new LibraryModel(request.MemberId, [Range()], [LibraryEffect.WriteCellsOf("bag")])
        {
            Stores = new Dictionary<string, IReadOnlyList<LibraryValue>> { ["bag"] = [new ArgumentValue("item")] }
        });

    private static GeneratedAnswer Refusal(GenerationRequest request) =>
        new(3, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, GenerationReasons.DRIVER_NOT_SYNTHESIZED, null, null,
            Record())
        {
            Detail = "no driver"
        };

    private static GeneratedAnswer Answer(GenerationRequest request, LibraryModel model) =>
        new(3, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null, model, null, Record());

    private static GenerationRecord Record() =>
        new(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0);

    private static SupportedAssemblyVersion Range() => new(PACKAGE, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1));

    private static int Counter(AnalysisResult result, string counter) => Assert.Single(result.Coverage).Skips[counter];

    /// <summary>The findings on the state's count, which the hosted service's unknown effect makes against the controller's read.</summary>
    /// <param name="result">The analysis.</param>
    private static IReadOnlyList<Finding> CountFindings(AnalysisResult result) =>
        result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Count"]) || finding.Resource.IsWildcard).ToArray();

    private static string[] Fingerprints(AnalysisResult result) => result.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The unknown-effect access of the source's last call, the one the work's statements end with.</summary>
    /// <param name="result">The analysis.</param>
    private static Access LastUnknownEffect(AnalysisResult result) =>
        result.Accesses.Where(access => access.Operation == AccessOperation.UnknownEffect)
              .OrderBy(access => access.Source.StartLine).ThenBy(access => access.Source.StartColumn)
              .Last();

    /// <summary>The key of the generated file of the package's asset.</summary>
    /// <param name="image">The asset.</param>
    private static GeneratedFileKey KeyOf(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        return GeneratedFileKey.ForPackage(PACKAGE, new Version(1, 0, 0, 0), PACKAGE, "1.0.0", reader.GetGuid(reader.GetModuleDefinition().Mvid));
    }

    /// <summary>Writes a valid file for the asset, holding an answer for a member the scope does not call, and returns its path.</summary>
    /// <param name="image">The asset.</param>
    private string StoredFile(byte[] image)
    {
        var key = KeyOf(image);
        var other = GeneratedModelAnswer.OfRefusal("M:Acme.Api.Beta(System.Object)", new GeneratedRefusal(ModelReasons.UNKNOWN_TOUCH, "a probe was handed on", []));
        Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(_repository, key), [other], CancellationToken.None));
        return key.PathUnder(_repository)!;
    }
}
