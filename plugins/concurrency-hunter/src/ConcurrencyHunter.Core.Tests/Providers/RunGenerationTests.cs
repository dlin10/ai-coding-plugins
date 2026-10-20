using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A run's generation (R1, R3, R4): which members it generates, in which order, within which limits, and what it keeps. A fake
/// generator, a fake clock and a package in a fake NuGet layout stand for the real ones.</summary>
public sealed class RunGenerationTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string LIBRARY = """
        namespace Acme
        {
            public static class Api
            {
                public static void Alpha(System.Action callback) { }
                public static void Beta(System.Action callback) { }
                public static void Gamma(System.Action callback) { }
                public static void Delta(System.Action callback) { }
                public static void Epsilon(System.Action callback) { }
            }
        }
        """;

    private readonly GenerationInstall _install = new();
    private readonly FakeClock _clock = new();
    private readonly List<GenerationRequest> _calls = [];
    private readonly string _repository = Directory.CreateTempSubdirectory("ch-run-generation-").FullName;
    private int _secondsUntilCut;

    public RunGenerationTests() => _install.SharedFramework("8.0.5");

    public void Dispose()
    {
        _install.Dispose();
        Directory.Delete(_repository, recursive: true);
    }

    [Fact]
    public void Default_limits_are_the_server_constants()
    {
        Assert.Equal(TimeSpan.FromSeconds(900), RunGeneration.RUN_LIMIT);
        Assert.Equal(TimeSpan.FromSeconds(60), RunGeneration.MEMBER_LIMIT);
    }

    [Fact]
    public void Undescribed_member_is_generated()
    {
        var alpha = Candidate(Project(), "Alpha");
        var subject = Subject((request, _) =>
        {
            _clock.Advance(Seconds(7));
            return ModelAnswer(request);
        });

        subject.Generate(LibraryModels.BuiltIn, [alpha]);
        subject.EndScope();

        var call = Assert.Single(_calls);
        Assert.Equal((PACKAGE, "M:Acme.Api.Alpha(System.Action)", PACKAGE, "1.0.0", "net8.0"),
                     (call.AssemblyName, call.MemberId, call.PackageId, call.Version, call.Framework));
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(alpha.Target)!.Outcome.Kind);
        Assert.Equal(Seconds(7), subject.Spent);
        var file = GeneratedModelFiles.Read(_repository, alpha.Target.Key);
        Assert.Equal(["M:Acme.Api.Alpha(System.Action)"], file.Entries.Keys);
        Assert.NotNull(file.Entries[alpha.Target.MemberId].Model);
        Assert.Empty(subject.Diagnostics);

        // The same call reached twice is one member: one generation.
        _calls.Clear();
        var again = Subject((request, _) => ModelAnswer(request));
        again.Generate(LibraryModels.BuiltIn, [Candidate(Project("3.0.0"), "Beta"), Candidate(Project("3.0.0"), "Beta")]);
        Assert.Equal(["Beta"], Called);
    }

    [Fact]
    public void Member_whose_implementation_is_missing_is_not_generated()
    {
        // The package ships a net462 asset only: the scope's net8.0 asset is not on disk.
        var image = Library();
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net462", PACKAGE, image);
        var alpha = Candidate(Compile(image, "1.0.0", "net8.0"), "Alpha");
        Assert.IsType<UnavailableGenerationTarget>(alpha.Target);
        var subject = Subject((request, _) => ModelAnswer(request));

        subject.Generate(LibraryModels.BuiltIn, [alpha]);
        subject.EndScope();

        Assert.Empty(_calls);
        Assert.Null(subject.AnswerOf(alpha.Target));
        Assert.False(Directory.Exists(Path.Combine(_repository, ".concurrency-hunter")));
    }

    [Fact]
    public void Described_member_is_not_generated()
    {
        var project = Project();
        var (alpha, beta, gamma, delta, epsilon) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"),
                                                    Candidate(project, "Delta"), Candidate(project, "Epsilon"));
        // A project entry describes a member whether it is a model or `opaque`.
        var projectModels = LibraryModels.WithProject([Entry(beta, 1, 2, ModelLayer.Project), Entry(epsilon, 1, 2, ModelLayer.Project, opaque: true)]);

        Assert.Equal(["Alpha"], Run(projectModels, alpha, beta, epsilon));

        // A built-in entry describes a member in range; out of range it names the member and leaves it to the generated layer.
        var builtInModels = new LibraryModels([Entry(gamma, 1, 2), Entry(delta, 2, 3)], []);

        Assert.Equal(["Delta"], Run(builtInModels, gamma, delta));
    }

    [Fact]
    public void Stored_model_is_read_not_generated()
    {
        var project = Project();
        var (alpha, beta, gamma, delta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"), Candidate(project, "Delta"));
        // Alpha's model applies; Beta's is valid and applies to no version of the package; Gamma's entry is invalid, a refusal of a word no file keeps.
        WriteFile(alpha.Target.Key, ModelEntry(alpha.Target.MemberId), ModelEntry(beta.Target.MemberId, major: 2),
                  Raw(gamma.Target.MemberId, "refusal", """{"reason":"budget","detail":"","causes":[]}"""));
        var subject = Subject((request, _) => ModelAnswer(request));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma, delta]);
        subject.EndScope();

        // A model that does not apply is an answer too; an invalid entry counts as absent, and its new answer replaces it.
        Assert.Equal(["Delta", "Gamma"], Called);
        Assert.Contains(subject.Diagnostics, diagnostic => diagnostic.Contains(gamma.Target.MemberId, StringComparison.Ordinal));
        var file = GeneratedModelFiles.Read(_repository, alpha.Target.Key);
        Assert.Equal(new[] { alpha, beta, gamma, delta }.Select(candidate => candidate.Target.MemberId).Order(StringComparer.Ordinal),
                     file.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(file.Diagnostics);
        Assert.All(file.Entries.Values, entry => Assert.NotNull(entry.Model));
        Assert.Null(subject.AnswerOf(alpha.Target));
        Assert.Null(subject.AnswerOf(beta.Target));
    }

    [Fact]
    public void Stored_refusal_is_read_not_generated()
    {
        var project = Project();
        var (alpha, beta, gamma) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"));
        // A refusal of the generator, and a member that ran past its own limit before: neither is generated again.
        WriteFile(alpha.Target.Key,
                  Raw(alpha.Target.MemberId, "refusal", """{"reason":"unknown-touch","detail":"a probe was handed on","causes":["probe-handed-to-unseen"]}"""),
                  Raw(beta.Target.MemberId, "refusal", """{"reason":"timeout","detail":"too slow","causes":[]}"""));
        var before = StoredEntries(alpha.Target.Key);
        var subject = Subject((request, _) => ModelAnswer(request));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma]);
        subject.EndScope();

        Assert.Equal(["Gamma"], Called);
        var after = StoredEntries(alpha.Target.Key);
        Assert.True(JsonNode.DeepEquals(before[alpha.Target.MemberId], after[alpha.Target.MemberId]));
        Assert.True(JsonNode.DeepEquals(before[beta.Target.MemberId], after[beta.Target.MemberId]));
        Assert.NotNull(after[gamma.Target.MemberId]["model"]);
    }

    [Fact]
    public void Delegate_handed_members_go_first()
    {
        var project = Project();
        var (alpha, beta, gamma, delta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"), Candidate(project, "Delta"));

        // Gamma is handed a delegate at one of its calls and not at the other: it goes with the handed members.
        Assert.Equal(["Beta", "Delta", "Gamma", "Alpha"],
                     Run(LibraryModels.BuiltIn, alpha, gamma, beta with { HandedDelegate = true }, delta with { HandedDelegate = true },
                         gamma with { HandedDelegate = true }));

        // Without a delegate anywhere the order is the declaration id's, whatever order the calls are met in. Another version of the package
        // is another file, so none of its members is stored yet.
        var other = Project("2.0.0");
        Assert.Equal(["Alpha", "Beta", "Delta", "Gamma"],
                     Run(LibraryModels.BuiltIn, Candidate(other, "Gamma"), Candidate(other, "Delta"), Candidate(other, "Beta"), Candidate(other, "Alpha")));
    }

    [Fact]
    public void Equal_ids_order_by_file_key()
    {
        var (first, second) = (Project("1.0.0"), Project("2.0.0"));

        // The same id through two files: the file key breaks the tie, whatever the order the calls are met in.
        Run(LibraryModels.BuiltIn, Candidate(second, "Alpha"), Candidate(first, "Alpha"));
        Assert.Equal(["Alpha@1.0.0", "Alpha@2.0.0"], _calls.Select(call => $"{Name(call)}@{call.Version}"));

        // The id comes before the key: Beta's file sorts after Gamma's, and Beta still goes first.
        _calls.Clear();
        Run(LibraryModels.BuiltIn, Candidate(first, "Gamma"), Candidate(second, "Beta"));
        Assert.Equal(["Beta@2.0.0", "Gamma@1.0.0"], _calls.Select(call => $"{Name(call)}@{call.Version}"));

        // A delegate comes before both.
        _calls.Clear();
        Run(LibraryModels.BuiltIn, Candidate(first, "Delta"), Candidate(second, "Epsilon") with { HandedDelegate = true });
        Assert.Equal(["Epsilon@2.0.0", "Delta@1.0.0"], _calls.Select(call => $"{Name(call)}@{call.Version}"));
    }

    [Fact]
    public void Member_past_its_limit_is_kept_as_timeout()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));
        var subject = Subject((request, token) => Name(request) == "Alpha" ? RunUntilCut(token) : ModelAnswer(request),
                              runLimit: Seconds(1000), memberLimit: Seconds(60));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta]);
        subject.EndScope();

        Assert.Equal(60, _secondsUntilCut);
        var answer = subject.AnswerOf(alpha.Target)!;
        Assert.Equal((GenerationOutcomeKind.Timeout, GenerationOutcome.TIMEOUT), (answer.Outcome.Kind, answer.Refusal!.Reason));
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(beta.Target)!.Outcome.Kind);
        // A file keeps the timeout, so the member is not tried again; the member after it still ran.
        var file = GeneratedModelFiles.Read(_repository, alpha.Target.Key);
        Assert.Equal(GenerationOutcome.TIMEOUT, file.Entries[alpha.Target.MemberId].Refusal!.Reason);
        Assert.NotNull(file.Entries[beta.Target.MemberId].Model);
        _calls.Clear();
        Subject((request, _) => ModelAnswer(request)).Generate(LibraryModels.BuiltIn, [alpha]);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Member_cut_by_the_run_budget_is_budget_not_timeout()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));
        var subject = Subject(Spending(("Alpha", 55)), runLimit: Seconds(100), memberLimit: Seconds(60));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta]);
        subject.EndScope();

        // Beta's own limit, 60 s, is larger than the 45 s the run has left: the run's limit cut it.
        Assert.Equal(45, _secondsUntilCut);
        var answer = subject.AnswerOf(beta.Target)!;
        Assert.Equal((GenerationOutcomeKind.Budget, GenerationOutcome.BUDGET), (answer.Outcome.Kind, answer.Refusal!.Reason));
        Assert.False(GeneratedModelFiles.Keeps(answer.Outcome));
        Assert.Equal([alpha.Target.MemberId], GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries.Keys);
    }

    [Fact]
    public void Remaining_run_budget_below_the_member_limit_bounds_the_member()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));

        Subject(Spending(("Alpha", 55)), runLimit: Seconds(100), memberLimit: Seconds(60)).Generate(LibraryModels.BuiltIn, [alpha, beta]);

        // The token fires after what is left of the run's limit, 45 s, not after the member's 60 s.
        Assert.Equal(45, _secondsUntilCut);

        // With the member's limit the smaller, the member's limit bounds it.
        _calls.Clear();
        Subject(Spending(("Alpha", 5)), runLimit: Seconds(100), memberLimit: Seconds(10)).Generate(LibraryModels.BuiltIn, [alpha, beta]);
        Assert.Equal(10, _secondsUntilCut);
    }

    [Fact]
    public void Member_limit_equal_to_the_remaining_budget_is_timeout()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));
        var subject = Subject(Spending(("Alpha", 40)), runLimit: Seconds(100), memberLimit: Seconds(60));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta]);
        subject.EndScope();

        // Alpha left 60 s, which is Beta's own limit: equal counts as its own.
        Assert.Equal(60, _secondsUntilCut);
        Assert.Equal(GenerationOutcomeKind.Timeout, subject.AnswerOf(beta.Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcome.TIMEOUT, GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries[beta.Target.MemberId].Refusal!.Reason);
    }

    [Fact]
    public void Members_past_the_run_budget_are_budget_and_not_kept()
    {
        var project = Project();
        var (alpha, beta, gamma, delta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"), Candidate(project, "Delta"));
        var subject = Subject(Spending(("Alpha", 55)), runLimit: Seconds(100), memberLimit: Seconds(60));

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma, delta]);
        subject.EndScope();

        // Beta used the rest of the run's limit; Gamma and Delta were never started.
        Assert.Equal(["Alpha", "Beta"], Called);
        Assert.Equal(Seconds(100), subject.Spent);
        Assert.All(new[] { beta, gamma, delta }, candidate => Assert.Equal(GenerationOutcomeKind.Budget, subject.AnswerOf(candidate.Target)!.Outcome.Kind));
        Assert.Equal([alpha.Target.MemberId], GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries.Keys);

        // Nothing was kept for them, so a later run, with a budget of its own, tries them.
        _calls.Clear();
        Subject((request, _) => ModelAnswer(request), runLimit: Seconds(100), memberLimit: Seconds(60)).Generate(LibraryModels.BuiltIn, [alpha, beta, gamma, delta]);
        Assert.Equal(["Beta", "Delta", "Gamma"], Called);
    }

    [Fact]
    public void Run_cancellation_during_a_member_is_cancelled_and_not_kept()
    {
        var project = Project();
        var (alpha, beta, gamma) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"));
        using var run = new CancellationTokenSource();
        var subject = Subject((request, token) =>
        {
            if (Name(request) == "Beta")
            {
                run.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return ModelAnswer(request);
        }, token: run.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma]));

        // Generation stopped at Beta and answered nothing for it. Alpha's answer, awaiting the end of the step, is not written either: the
        // writer observes the cancelled run.
        Assert.Equal(["Alpha", "Beta"], Called);
        Assert.Null(subject.AnswerOf(beta.Target));
        Assert.Null(subject.AnswerOf(gamma.Target));
        Assert.ThrowsAny<OperationCanceledException>(subject.EndScope);
        Assert.False(File.Exists(alpha.Target.Key.PathUnder(_repository)));

        // Whatever a generation throws once the run is cancelled is the cancellation's, not a failure.
        using var second = new CancellationTokenSource();
        var failing = Subject((_, _) =>
        {
            second.Cancel();
            throw new InvalidOperationException("torn down");
        }, token: second.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => failing.Generate(LibraryModels.BuiltIn, [alpha]));
        Assert.Null(failing.AnswerOf(alpha.Target));

        // A run cancelled before generation starts generates nothing.
        _calls.Clear();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Subject((request, _) => ModelAnswer(request), token: cancelled.Token)
                                                               .Generate(LibraryModels.BuiltIn, [alpha]));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Enclosing_cancellation_during_a_member_is_cancelled_and_not_kept()
    {
        var alpha = Candidate(Project(), "Alpha");
        using var cancellation = new CancellationTokenSource();
        var checksAfterCancellation = 0;
        var subject = Subject((_, token) =>
        {
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
            throw new OperationCanceledException(token);
        });
        using (WorkCancellation.Enter(cancellation.Token, () =>
        {
            if (cancellation.IsCancellationRequested)
                checksAfterCancellation++;
        }))
            Assert.ThrowsAny<OperationCanceledException>(() => subject.Generate(LibraryModels.BuiltIn, [alpha]));
        subject.EndScope();
        Assert.Null(subject.AnswerOf(alpha.Target));
        Assert.False(File.Exists(alpha.Target.Key.PathUnder(_repository)));
        Assert.Equal(0, checksAfterCancellation);
    }

    [Fact]
    public void Exception_is_failed_and_not_kept()
    {
        var project = Project();
        var (alpha, beta, gamma) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"));
        var subject = Subject((request, _) => Name(request) switch
        {
            "Alpha" => throw new InvalidOperationException("boom"),
            // Neither the member's token nor the run's raised this one.
            "Beta" => throw new OperationCanceledException(),
            _ => ModelAnswer(request)
        });

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma]);
        subject.EndScope();

        Assert.Equal(["Alpha", "Beta", "Gamma"], Called);
        var failed = subject.AnswerOf(alpha.Target)!;
        Assert.Equal((GenerationOutcomeKind.Failed, GenerationOutcome.FAILED), (failed.Outcome.Kind, failed.Refusal!.Reason));
        Assert.Equal("InvalidOperationException: boom", failed.Refusal.Detail);
        Assert.Equal(GenerationOutcomeKind.Failed, subject.AnswerOf(beta.Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(gamma.Target)!.Outcome.Kind);
        Assert.Equal([gamma.Target.MemberId], GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries.Keys);
    }

    [Fact]
    public void Member_met_in_two_scopes_is_generated_once()
    {
        var (alpha, beta) = (Candidate(Project(), "Alpha"), Candidate(Project(), "Beta"));
        var subject = Subject((request, _) => Name(request) == "Beta" ? throw new InvalidOperationException("boom") : ModelAnswer(request));
        subject.Generate(LibraryModels.BuiltIn, [alpha, beta]);
        subject.EndScope();
        Assert.Equal(["Alpha", "Beta"], Called);

        // The second scope has its own compilation of the same assets: what the run generated, kept or not, is not generated again.
        var second = Project();
        subject.Generate(LibraryModels.BuiltIn, [Candidate(second, "Alpha"), Candidate(second, "Beta")]);
        subject.EndScope();
        Assert.Equal(["Alpha", "Beta"], Called);
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(alpha.Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcomeKind.Failed, subject.AnswerOf(beta.Target)!.Outcome.Kind);

        // The same id through another file is another target.
        subject.Generate(LibraryModels.BuiltIn, [Candidate(Project("2.0.0"), "Alpha")]);
        Assert.Equal(["Alpha", "Beta", "Alpha"], Called);
        Assert.Equal(["1.0.0", "1.0.0", "2.0.0"], _calls.Select(call => call.Version));

        // A later run tries what was not kept, and only that.
        _calls.Clear();
        Subject((request, _) => ModelAnswer(request)).Generate(LibraryModels.BuiltIn, [alpha, beta]);
        Assert.Equal(["Beta"], Called);
    }

    [Fact]
    public void Budget_counts_only_time_inside_generations()
    {
        var project = Project();
        var (alpha, beta, gamma) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"));
        var subject = Subject(Spending(("Alpha", 30), ("Beta", 30), ("Gamma", 10)), runLimit: Seconds(100), memberLimit: Seconds(60));

        // Time that passes outside a generation is not charged, before the first scope's step and between the scopes.
        _clock.Advance(Seconds(500));
        subject.Generate(LibraryModels.BuiltIn, [alpha]);
        subject.EndScope();
        _clock.Advance(Seconds(500));
        subject.Generate(LibraryModels.BuiltIn, [beta, gamma]);
        subject.EndScope();

        Assert.Equal(Seconds(70), subject.Spent);
        Assert.All(new[] { alpha, beta, gamma }, candidate => Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(candidate.Target)!.Outcome.Kind));
    }

    /// <summary>The generator call of a run: spends the seconds a member is told to, then answers with a model; any other member is run
    /// until it is cut.</summary>
    /// <param name="spend">The seconds each member named spends before it answers.</param>
    private Func<GenerationRequest, CancellationToken, GeneratedAnswer> Spending(params (string Member, int Duration)[] spend) =>
        (request, token) =>
        {
            if (spend.FirstOrDefault(item => item.Member == Name(request)) is not { Member: not null } found)
                return RunUntilCut(token);
            _clock.Advance(Seconds(found.Duration));
            return ModelAnswer(request);
        };

    /// <summary>A generation that never ends on its own: a second of the clock at a time, until the token fires, and then it stops as the
    /// generator does.</summary>
    /// <param name="token">The member's token.</param>
    private GeneratedAnswer RunUntilCut(CancellationToken token)
    {
        var seconds = 0;
        while (!token.IsCancellationRequested)
        {
            if (seconds > 5000)
                throw new InvalidOperationException("The member was never cut.");
            _clock.Advance(Seconds(1));
            seconds++;
        }

        _secondsUntilCut = seconds;
        throw new OperationCanceledException(token);
    }

    /// <summary>A run's generation over this fixture's repository, clock and calls.</summary>
    /// <param name="generate">The generator call.</param>
    /// <param name="runLimit">The run's limit; a thousand seconds when omitted.</param>
    /// <param name="memberLimit">The member's limit; a minute when omitted.</param>
    /// <param name="token">The run's token.</param>
    private RunGeneration Subject(Func<GenerationRequest, CancellationToken, GeneratedAnswer> generate, TimeSpan? runLimit = null, TimeSpan? memberLimit = null,
                                  CancellationToken token = default) =>
        new(_repository, token, runLimit ?? Seconds(1000), memberLimit ?? Seconds(60), _clock, (request, cancellation) =>
        {
            _calls.Add(request);
            return generate(request, cancellation);
        });

    /// <summary>The members one scope's step generates, in the order it generated them.</summary>
    /// <param name="models">The scope's models.</param>
    /// <param name="candidates">The scope's candidates.</param>
    private string[] Run(LibraryModels models, params GenerationCandidate[] candidates)
    {
        var start = _calls.Count;
        var subject = Subject((request, _) => ModelAnswer(request));
        subject.Generate(models, candidates);
        subject.EndScope();
        return _calls.Skip(start).Select(Name).ToArray();
    }

    private string[] Called => _calls.Select(Name).ToArray();

    private static string Name(GenerationRequest request) => request.MemberId["M:Acme.Api.".Length..request.MemberId.IndexOf('(')];

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>A candidate: a member of the package as the scope's compilation sees it, with its generation target.</summary>
    /// <param name="compilation">The scope's compilation.</param>
    /// <param name="member">The member's name.</param>
    private GenerationCandidate Candidate(Compilation compilation, string member)
    {
        var method = compilation.GetTypeByMetadataName("Acme.Api")!.GetMembers(member).OfType<IMethodSymbol>().Single();
        var target = Assert.IsAssignableFrom<IdentifiedGenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(), CancellationToken.None));
        return new GenerationCandidate(compilation, method, target, false);
    }

    /// <summary>A scope that references the package version's net8.0 asset, written under the fake packages folder.</summary>
    /// <param name="version">The package version.</param>
    private Compilation Project(string version = "1.0.0")
    {
        var image = Library();
        ReferenceScopes.AddAsset(_install, PACKAGE, version, "net8.0", PACKAGE, image);
        return Compile(image, version, "net8.0");
    }

    private Compilation Compile(byte[] image, string version, string framework) =>
        ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, 8),
                                                                ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, version, "lib", framework, PACKAGE))]);

    private static byte[] Library() => EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + LIBRARY);

    /// <summary>An entry of a layer for a candidate's member, supported from one major version of the package up to another.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="minimum">The first major version supported.</param>
    /// <param name="maximum">The major version the range stops before.</param>
    /// <param name="layer">The entry's layer.</param>
    /// <param name="opaque">Whether the entry makes the member opaque.</param>
    private static LibraryModel Entry(GenerationCandidate candidate, int minimum, int maximum, ModelLayer layer = ModelLayer.BuiltIn, bool opaque = false) =>
        new(candidate.Target.LookupId, [new SupportedAssemblyVersion(PACKAGE, new Version(minimum, 0, 0, 0), new Version(maximum, 0, 0, 0))], [], layer, opaque,
            layer == ModelLayer.Project ? new Version(1, 0, 0, 0) : null);

    private static GeneratedAnswer ModelAnswer(GenerationRequest request) =>
        new(3, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null, Model(request.MemberId), null,
            new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0));

    /// <summary>A model of a member that applies to the package's first major version, or to another's.</summary>
    /// <param name="member">The member's declaration id.</param>
    /// <param name="major">The package's major version the model supports.</param>
    private static LibraryModel Model(string member, int major = 1) =>
        new(member, [new SupportedAssemblyVersion(PACKAGE, new Version(major, 0, 0, 0), new Version(major, 0, 0, 1))], [LibraryEffect.DeepReadOf("callback")]);

    private string ModelEntry(string member, int major = 1) => Raw(member, "model", ModelEntryWriter.Write(Model(member, major)).ToJsonString());

    private static string Raw(string member, string kind, string json) => $"{{\"member\":{JsonSerializer.Serialize(member)},\"{kind}\":{json}}}";

    /// <summary>Writes a file with the key's header and the entries as given, whatever they hold.</summary>
    /// <param name="key">The key whose file is written.</param>
    /// <param name="entries">The entries, each a JSON object.</param>
    private void WriteFile(GeneratedFileKey key, params string[] entries)
    {
        var path = key.PathUnder(_repository)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = $"{{\"name\":\"{key.AssemblyName}\",\"version\":\"{key.AssemblyVersion}\",\"mvid\":\"{key.Mvid:D}\",\"package\":\"{key.Package}\"," +
                     $"\"packageVersion\":\"{key.PackageVersion}\"}}";
        File.WriteAllText(path, $"{{\"schemaVersion\":2,\"generator\":\"{GeneratorVersion.Fingerprint}\",\"assembly\":{header},\"members\":[{string.Join(',', entries)}]}}", new UTF8Encoding(false));
    }

    /// <summary>The entries of a key's file by member.</summary>
    /// <param name="key">The file's key.</param>
    private IReadOnlyDictionary<string, JsonNode> StoredEntries(GeneratedFileKey key)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(key.PathUnder(_repository)!))!;
        return root["members"]!.AsArray().ToDictionary(entry => entry!["member"]!.GetValue<string>(), entry => entry!, StringComparer.Ordinal);
    }

    /// <summary>A clock that moves only when a test moves it, and fires the timers that fall due.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private TimeSpan _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now.Ticks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _dueAt;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _dueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner._now < _dueAt)
                    return;
                _dueAt = null;
                callback(state);
            }
        }
    }
}
