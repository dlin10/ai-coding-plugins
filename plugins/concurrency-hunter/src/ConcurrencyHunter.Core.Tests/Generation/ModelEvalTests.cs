using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Generation.ModelEvals;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The model evals of G-7: the generator on the 18 gold members of <c>System.Linq</c>, <c>System.Security.Claims</c>, Polly
/// and Google.Protobuf, against the gold fates and the recorded snapshot. They run only under <c>CH_MODEL_EVALS=1</c>, and there a
/// missing input fails them. They run alone, after every parallel test: one of them measures wall time.</summary>
[Collection(COLLECTION)]
public sealed class ModelEvalTests
{
    private const string RECORDING_SNAPSHOT = "{\"fateGoldEntries\":3,\"effectsExact\":8,\"linqExact\":{\"System.Linq\":12,\"System.Linq.Queryable\":1}}";
    public const string COLLECTION = "Model evals";

    [RequiresModelEvalsFact]
    public void Classified_fates_have_no_unsafe_narrowing()
    {
        var unsafeFates = Scored(InstalledRun).Where(score => !IsSafe(score.Answered, score.Gold))
                                              .Select(score => $"{score.Member}({score.Parameter}): gold {Show(score.Gold)}, answered {Show(score.Answered)}")
                                              .ToArray();

        Assert.True(unsafeFates.Length == 0, $"{unsafeFates.Length} unsafe narrowing(s):\n" + string.Join("\n", unsafeFates));
    }

    [RequiresModelEvalsFact]
    public void Exact_fates_meet_the_floor()
    {
        // A4: at least 14 of the 24 exact, or, when fewer are, no fewer than the snapshot recorded; recording sets that floor.
        var scores = Scored(InstalledRun);
        var exact = Exact(InstalledRun);
        int? recorded = Environment.GetEnvironmentVariable(RECORD_VARIABLE) == "1" ? exact
                      : File.Exists(SnapshotPath) ? RecordedExact(File.ReadAllText(SnapshotPath)) : null;
        var floor = Math.Min(EXACT_FLOOR, recorded ?? EXACT_FLOOR);

        Assert.Equal(GOLD_PARAMETERS, scores.Count);
        Assert.True(exact >= floor,
                    $"{exact} of {scores.Count} exact, under the floor {floor} (14, or the snapshot's {recorded?.ToString() ?? "none"}); not exact:\n" +
                    string.Join("\n", scores.Where(score => !IsExact(score.Answered, score.Gold))
                                            .Select(score => $"{score.Member}({score.Parameter}): gold {Show(score.Gold)}, answered {Show(score.Answered)}" +
                                                             (score.Reason is null ? "" : $" ({score.Reason})"))));
    }

    [RequiresModelEvalsFact]
    public void Entries_pass_the_project_reader()
    {
        foreach (var answer in InstalledRun.Answers.Where(answer => answer.Answer.Model is not null))
        {
            var files = ProjectModelFiles.Read("generated.json", ModelEntryWriter.File(answer.Answer.Model!));
            Assert.Empty(files.Rejections);
            var entry = Assert.Single(files.Entries);
            var (member, compilation) = DeclaringMember(answer);
            Assert.Null(ProjectModelResolver.EntryRejection(entry, member, compilation));
        }
    }

    [RequiresModelEvalsFact]
    public void Entry_fates_are_the_classified_delegate_fates()
    {
        foreach (var answer in InstalledRun.Answers.Where(answer => answer.Answer.Model is not null))
        {
            var (member, _) = DeclaringMember(answer);
            var expected = member.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                 .Select(parameter => parameter.Name).Order(StringComparer.Ordinal).ToArray();
            var actual = answer.Answer.Model!.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual.Select(fate => fate.Parameter));
            foreach (var fate in actual)
            {
                var classified = Assert.Contains(fate.Parameter, answer.Answer.Classified!);
                Assert.Equal(classified.Fate, LibraryFate.Text(fate.Kind));
                Assert.Equal(classified.Holder, fate.Holder is null ? null : fate.Holder == LibraryHolderKind.Result ? "result" : "this");
            }
        }
    }

    [RequiresModelEvalsFact]
    public void Fate_gold_members_with_an_entry_meet_the_recorded_count()
    {
        var entries = FateGoldEntries(InstalledRun);
        var recorded = RecordedCount("fateGoldEntries");

        Assert.True(entries >= recorded, $"{entries} fate-gold members have an entry, under the recorded count {recorded}");
    }

    [Fact]
    public void Recording_cannot_lower_the_fate_gold_entry_floor() => AssertRecordingPreservesFloor("fateGoldEntries");

    /// <summary>Checks that recording refuses a lower floor before touching bytes, and accepts an equal or higher floor.</summary>
    /// <param name="property">The count property.</param>
    /// <param name="group">The LINQ assembly group, or null for a top-level count.</param>
    internal static void AssertRecordingPreservesFloor(string property, string? group = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ch-floor-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, RECORDING_SNAPSHOT);
        try
        {
            var current = System.Text.Json.Nodes.JsonNode.Parse(RECORDING_SNAPSHOT)!;
            var counts = group is null ? current : current[property]!;
            var key = group ?? property;
            var floor = (int)counts[key]!;
            counts[key] = floor - 1;
            var error = Assert.Throws<InvalidOperationException>(() => RecordSnapshot(path, current.ToJsonString()));
            Assert.Contains(key, error.Message);
            Assert.Equal(RECORDING_SNAPSHOT, File.ReadAllText(path));
            foreach (var count in new[] { floor, floor + 1 })
            {
                counts[key] = count;
                RecordSnapshot(path, current.ToJsonString());
                Assert.Equal(current.ToJsonString(), File.ReadAllText(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresModelEvalsFact]
    public void Answers_match_the_recorded_snapshot()
    {
        var fresh = Snapshot(InstalledRun, EffectEvals.InstalledRun, LinqEvals.InstalledRun);
        if (Environment.GetEnvironmentVariable(RECORD_VARIABLE) == "1")
        {
            RecordSnapshot(SnapshotPath, fresh);
            return;
        }

        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        var differences = Differences(File.ReadAllText(SnapshotPath), fresh);
        Assert.True(differences.Count == 0, "The model evals differ from generator-snapshot.json:\n" + string.Join("\n", differences));
    }

    [RequiresModelEvalsFact]
    public void Every_package_records_its_compiled_bodies_and_drivers()
    {
        var run = InstalledRun;

        Assert.Equal(Inputs.Keys.Order(StringComparer.Ordinal), run.Packages.Select(package => package.Assembly));
        foreach (var package in run.Packages)
        {
            var answers = run.Answers.Where(answer => answer.Gold.Assembly == package.Assembly).ToArray();
            Assert.Equal(Inputs[package.Assembly].Package, package.Package);
            Assert.True(package.Bodies > 0, $"{package.Assembly}: no method bodies");
            Assert.InRange(package.ExternBodies, 0, package.Bodies);
            Assert.Equal(answers.Length, package.Members);
            Assert.Equal(answers.Count(answer => answer.DriverSynthesized), package.DriversSynthesized);
            Assert.All(answers, answer => Assert.Equal(package.ImplementationVersion, ImplementationVersionOf(answer)));
        }

        Assert.Equal([10, 1, 5, 2], new[] { "System.Linq", "System.Security.Claims", "Polly", "Google.Protobuf" }
                                       .Select(assembly => run.Packages.Single(package => package.Assembly == assembly).Members));
        Assert.Equal("7.2.3", run.Packages.Single(package => package.Assembly == "Polly").ImplementationVersion);
        Assert.Equal("3.21.9", run.Packages.Single(package => package.Assembly == "Google.Protobuf").ImplementationVersion);
        Assert.StartsWith("8.0.", run.Packages.Single(package => package.Assembly == "System.Linq").ImplementationVersion);
    }

    [RequiresModelEvalsFact]
    public void Missing_input_fails_under_the_flag()
    {
        using var install = new GenerationInstall();
        var noFramework = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), GoldPath));
        Assert.Contains("System.Linq", noFramework.Message);

        install.SharedFramework("8.0.31", ["System.Linq", "System.Security.Claims"]);
        var noPackage = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), GoldPath));
        Assert.Contains("Polly", noPackage.Message);

        var noGold = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), Path.Combine(install.Root, "gold.json")));
        Assert.Contains("gold.json", noGold.Message);
    }

    [RequiresModelEvalsFact]
    public void Message_parser_constructor_finishes_under_the_bound()
    {
        // The constructor's trigger actions reach Protobuf's JSON parser; with the triggers no root of the fate run (A1), and a holder's
        // confirmation run under the same bound (A5), the driver gets its answer — a fate, unknown-execution from the confirmation run,
        // or closure-bound — in under a minute.
        var input = Inputs["Google.Protobuf"];
        var request = new GenerationRequest(input.Assembly, input.Version, "M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})", input.Package,
                                            input.Framework);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var answer = ModelGenerator.Generate(request, ImplementationAssemblies.ForThisProcess(), timeout.Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"{clock.Elapsed.TotalSeconds:0.0} s");
        Assert.True(answer.Classified is { Count: > 0 } || answer.Reason == GenerationReasons.CLOSURE_BOUND, $"{answer.Reason}: {answer.Detail}");
    }

    private static string ImplementationVersionOf(EvalAnswer answer)
    {
        var input = Inputs[answer.Gold.Assembly];
        return input.Package is null
            ? Path.GetFileName(Path.GetDirectoryName(answer.Answer.Generation.Implementation!.Path))!
            : input.Version;
    }

    internal static int RecordedCount(string property)
    {
        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        return (int?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnapshotPath))![property]
               ?? throw new Xunit.Sdk.XunitException($"generator-snapshot.json has no {property}");
    }

    internal static int RecordedLinqExact(string group)
    {
        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        return (int?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnapshotPath))!["linqExact"]?[group]
               ?? throw new Xunit.Sdk.XunitException($"generator-snapshot.json has no linqExact.{group}");
    }

    private static (IMethodSymbol Member, Compilation Compilation) DeclaringMember(EvalAnswer answer)
    {
        var input = Inputs[answer.Gold.Assembly];
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve(input.Assembly, input.Version, input.Package, input.Framework);
        var library = LibraryCompilation.Compile(resolution.Assembly!, CancellationToken.None);
        var compilation = Assert.IsType<Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(library.Compilation);
        return (Assert.IsAssignableFrom<IMethodSymbol>(DriverSynthesizer.FindMember(compilation, answer.Gold.Id)), compilation);
    }

    private static IReadOnlyList<Score> Scored(EvalRun run) =>
        run.Answers.SelectMany(answer => answer.Gold.DelegateParams.Select(parameter =>
                                   new Score(answer.Gold.Id, parameter, answer.Gold.Fate, Answered(answer.Answer, parameter), answer.Answer.Reason)))
           .ToArray();

    /// <summary>One gold parameter's fate and the answer's.</summary>
    /// <param name="Member">The member id.</param>
    /// <param name="Parameter">The parameter.</param>
    /// <param name="Gold">The gold fate.</param>
    /// <param name="Answered">The answered fate.</param>
    /// <param name="Reason">The answer's reason when it classified nothing.</param>
    private sealed record Score(string Member, string Parameter, ClassifiedFate Gold, ClassifiedFate Answered, string? Reason);
}

/// <summary>The model evals' collection: not run in parallel with any other test.</summary>
[CollectionDefinition(ModelEvalTests.COLLECTION, DisableParallelization = true)]
public sealed class ModelEvalCollection;
