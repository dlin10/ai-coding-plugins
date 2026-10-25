using System.Globalization;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>A library member that a direct opaque call of a scope reached, as a run weighs it for generation (R1, R4).</summary>
/// <param name="Compilation">The compilation the call is in.</param>
/// <param name="Member">The called member.</param>
/// <param name="Target">The member's generation target.</param>
/// <param name="HandedDelegate">Whether the scope's summary of a reached call of the member records a delegate created as an argument of that
/// call (<c>SummaryOpaqueCall.Delegates</c>); it decides only which members the budget reaches first.</param>
internal readonly record struct GenerationCandidate(Compilation Compilation, IMethodSymbol Member, IdentifiedGenerationTarget Target, bool HandedDelegate);

/// <summary>The generated layer one scope's analysis reads (R2): the stored answers of the files its candidates name together with the run's own
/// answers, as the table lowering consults.</summary>
/// <param name="Table">The layer.</param>
/// <param name="Describes">Whether the layer describes at least one of the scope's candidates, so that the scope is analysed again with it.</param>
/// <param name="Diagnostics">One diagnostic, naming the compilation, for each model, stored or generated, that does not apply in a compilation of
/// the scope.</param>
internal readonly record struct ScopeLayer(GeneratedModelTable Table, bool Describes, IReadOnlyList<string> Diagnostics);

/// <summary>A run's generation (R1–R4): over the candidates of one scope at a time it decides which members no layer describes and no file
/// answers for, generates them in R4's order within the <b>Generation budget</b>, remembers every answer by member and file key so that a
/// member met in several scopes is generated once, and writes the answers a file keeps when the caller ends a scope's step. It is used by one
/// thread at a time: scopes are analysed one after another.</summary>
internal sealed class RunGeneration
{
    /// <summary>The run's limit: the time spent inside generations in one run.</summary>
    internal static readonly TimeSpan RUN_LIMIT = TimeSpan.FromSeconds(900);

    /// <summary>One member's limit.</summary>
    internal static readonly TimeSpan MEMBER_LIMIT = GenerationLimits.MEMBER;

    private readonly string _repositoryRoot;
    private readonly CancellationToken _runToken;
    private readonly TimeSpan _runLimit;
    private readonly TimeSpan _memberLimit;
    private readonly TimeProvider _time;
    private readonly Func<GenerationRequest, CancellationToken, GeneratedAnswer> _generate;
    private readonly Dictionary<GeneratedFileKey, GeneratedModelFile> _files = new();
    private readonly Dictionary<(string MemberId, GeneratedFileKey Key), GeneratedModelAnswer> _answers = new();
    private readonly Dictionary<GeneratedFileKey, List<GeneratedModelAnswer>> _pending = new();
    private readonly List<string> _diagnostics = [];
    private readonly CompilationOwnership _compilations = new();
    private (IReadOnlyList<GenerationCandidate> Candidates, GeneratedModelTable Table, IReadOnlyList<string> Diagnostics)? _view;
    private TimeSpan _spent;

    /// <summary>A run's generation with the server's limits, the system clock and the model generator over this process's install.</summary>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/generated/</c> the run reads and writes.</param>
    /// <param name="cancellationToken">The run's token: its own cancellation or deadline stops generation and the cancellation propagates.</param>
    internal RunGeneration(string repositoryRoot, CancellationToken cancellationToken)
        : this(repositoryRoot, cancellationToken, RUN_LIMIT, MEMBER_LIMIT, TimeProvider.System, DefaultGenerator())
    {
    }

    /// <summary>A run's generation with limits, a clock and a generator of the caller's choosing, for tests.</summary>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/generated/</c> the run reads and writes.</param>
    /// <param name="cancellationToken">The run's token: its own cancellation or deadline stops generation and the cancellation propagates.</param>
    /// <param name="runLimit">The time the run may spend inside generations.</param>
    /// <param name="memberLimit">The time one member may spend inside its generation.</param>
    /// <param name="time">The clock both limits are measured on, and the timer that cuts a member.</param>
    /// <param name="generate">The generator call: a member's request and the token that cuts the generation, and the generator's answer.</param>
    internal RunGeneration(string repositoryRoot, CancellationToken cancellationToken, TimeSpan runLimit, TimeSpan memberLimit, TimeProvider time,
                           Func<GenerationRequest, CancellationToken, GeneratedAnswer> generate)
    {
        _repositoryRoot = repositoryRoot;
        _runToken = cancellationToken;
        _runLimit = runLimit;
        _memberLimit = memberLimit;
        _time = time;
        _generate = generate;
    }

    /// <summary>The time spent inside generations so far: the budget's measure, and the run's <c>generation</c> timing.</summary>
    internal TimeSpan Spent => _spent;

    /// <summary>The diagnostics of the generated files met so far, in the order they arose: an unreadable file, an invalid entry, a write
    /// that failed.</summary>
    internal IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>The file a key names as the run read it; read once per run, so a scope that meets the file again sees what the run first saw.</summary>
    /// <param name="key">The file's key.</param>
    internal GeneratedModelFile FileOf(GeneratedFileKey key)
    {
        if (!_files.TryGetValue(key, out var file))
        {
            _files.Add(key, file = GeneratedModelFiles.Read(_repositoryRoot, key));
            _diagnostics.AddRange(file.Diagnostics);
        }

        return file;
    }

    /// <summary>What this run's generation came to for a target, kept or not, in this scope or an earlier one.</summary>
    /// <param name="target">The generation target.</param>
    /// <returns>The answer, or <c>null</c> when the run generated nothing for the target.</returns>
    internal GeneratedModelAnswer? AnswerOf(IdentifiedGenerationTarget target) => _answers.GetValueOrDefault((target.MemberId, target.Key));

    /// <summary>Generates what a scope's candidates call for. A candidate is generated when <see cref="GeneratedLayer.Decide"/> says so — no
    /// project entry, no built-in entry in range and no stored answer describes it, and its implementation is on disk — and the run has not
    /// answered its target yet. Members handed a delegate come first, then the rest, each group in ordinal order of declaration id and then
    /// of file key. A member runs under a token cancelled after the smaller of its own limit and what is left of the run's; when its own
    /// limit is not larger than what is left a cut is <c>timeout</c>, otherwise <c>budget</c>. A member the run's limit has already been spent
    /// before is <c>budget</c> without a generation. An answer a file keeps waits for <see cref="EndScope"/>.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, one per reached call.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal void Generate(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var wanted = Wanted(models, candidates);
        foreach (var (target, _) in wanted.OrderByDescending(item => item.HandedDelegate)
                                          .ThenBy(item => item.Target.MemberId, StringComparer.Ordinal)
                                          .ThenBy(item => item.Target.Key.Text, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            var answer = GenerateOne(target);
            _answers[(target.MemberId, target.Key)] = answer;
            if (!GeneratedModelFiles.Keeps(answer.Outcome))
                continue;
            if (!_pending.TryGetValue(target.Key, out var kept))
                _pending.Add(target.Key, kept = []);
            kept.Add(answer);
        }
    }

    /// <summary>The targets <see cref="Generate"/> would generate for a scope's candidates, in no particular order: what a read-only run lists
    /// as wanted.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, one per reached call.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal IReadOnlyList<GenerationTarget> WouldGenerate(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        return [.. Wanted(models, candidates).Select(item => item.Target)];
    }

    /// <summary>Removes the library compilations this run's generations created from the cache (R12), however the analysis ended. An entry that
    /// was in the cache before the run, one another caller created meanwhile and one created again after this run's entry left stay.</summary>
    // bounded: forwards once to library compilation cleanup.
    internal void Release() => LibraryCompilation.Release(_compilations.Created);

    /// <summary>Ends a scope's generation step without a test hook.</summary>
    internal void EndScope()
    {
        WorkCancellation.Check();
        EndScope(null);
    }

    /// <summary>Ends a scope's generation step: writes the answers a file keeps, each file under the lock every writer of it takes. An answer
    /// whose file cannot be written stays in memory and describes its calls in this run; the write error is a diagnostic.</summary>
    /// <param name="beforeFileRead">A test hook before a writer re-reads a generated file under its lock.</param>
    /// <exception cref="OperationCanceledException">The run's own token fired, before or while a writer waited for a file's lock.</exception>
    internal void EndScope(Action? beforeFileRead)
    {
        var pending = _pending.OrderBy(entry => entry.Key.Text, StringComparer.Ordinal).ToArray();
        _pending.Clear();
        foreach (var (key, answers) in pending)
        {
            WorkCancellation.Check();
            _diagnostics.AddRange(GeneratedModelFiles.Write(FileOf(key), answers, _runToken, beforeRead: beforeFileRead));
        }
    }

    /// <summary>The generated layer a scope's analysis reads: the files its candidates name as the run read them, and the run's own answers
    /// on top, each read back as a file's entry is, so that a model the run generated describes its calls whether or not its file could be
    /// written or read. It is built over the same candidates <see cref="Generate"/> was given, and reuses the view that call built.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, the list <see cref="Generate"/> was given.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal ScopeLayer Layer(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var (table, stored) = StoredView(candidates);
        var diagnostics = new List<string>(stored);
        foreach (var key in candidates.Select(candidate => candidate.Target.Key).Distinct())
        {
            WorkCancellation.Check();
            var file = FileOf(key);
            var entries = new Dictionary<string, GeneratedModelEntry>(StringComparer.Ordinal);
            foreach (var ((member, answerKey), answer) in _answers)
            {
                WorkCancellation.Check();
                if (answerKey == key && GeneratedModelFiles.EntryOf(answer, file.DisplayPath) is { } entry)
                    entries[member] = entry;
            }

            if (entries.Count != 0)
                diagnostics.AddRange(table.AddFile(file with { Entries = entries, Diagnostics = [] }));
        }

        return new ScopeLayer(table, candidates.Any(candidate => Decide(models, table, candidate).Layer == DescribingLayer.Generated), diagnostics);
    }

    /// <summary>The targets of a scope's candidates the run generates now, once each however many calls reach them, with whether a delegate is
    /// handed to any of those calls: the candidates no layer describes, no stored answer covers and the run has not answered yet.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, one per reached call.</param>
    private (GenerationTarget Target, bool HandedDelegate)[] Wanted(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        var stored = StoredView(candidates).Table;
        var wanted = new Dictionary<(string MemberId, GeneratedFileKey Key), (GenerationTarget Target, bool HandedDelegate)>();
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            var identity = (candidate.Target.MemberId, candidate.Target.Key);
            if (_answers.ContainsKey(identity) || !Decide(models, stored, candidate).Generate || candidate.Target is not GenerationTarget target)
                continue;
            wanted[identity] = wanted.TryGetValue(identity, out var earlier) ? (earlier.Target, earlier.HandedDelegate || candidate.HandedDelegate)
                                                                              : (target, candidate.HandedDelegate);
        }

        return [.. wanted.Values];
    }

    /// <summary>The generator over this process's install, which is what the product asks.</summary>
    private static Func<GenerationRequest, CancellationToken, GeneratedAnswer> DefaultGenerator()
    {
        var resolver = ImplementationAssemblies.ForThisProcess();
        return (request, token) => ModelGenerator.Generate(request, resolver, token);
    }

    /// <summary>What the generated files say about the candidates, as the analysis with the generated layer will see it: the declaring type
    /// of each candidate mapped to its file, and the files added.</summary>
    /// <param name="candidates">The scope's candidates. The view is built once for a list: <see cref="Layer"/> reuses what <see cref="Generate"/> built.</param>
    private (GeneratedModelTable Table, IReadOnlyList<string> Diagnostics) StoredView(IReadOnlyList<GenerationCandidate> candidates)
    {
        if (_view is { } cached && ReferenceEquals(cached.Candidates, candidates))
            return (cached.Table, cached.Diagnostics);

        var table = new GeneratedModelTable();
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            table.MapType(candidate.Compilation, Definition(candidate.Member).ContainingType, candidate.Target.Key);
        }
        var diagnostics = new List<string>();
        foreach (var key in candidates.Select(candidate => candidate.Target.Key).Distinct())
        {
            WorkCancellation.Check();
            diagnostics.AddRange(table.AddFile(FileOf(key)));
        }
        _view = (candidates, table, diagnostics);
        return (table, diagnostics);
    }

    /// <summary>Which layer describes a candidate, and whether the run generates it now.</summary>
    /// <param name="models">The scope's models without the generated layer.</param>
    /// <param name="stored">What the generated files say about the scope's candidates.</param>
    /// <param name="candidate">The candidate.</param>
    internal static GeneratedDecision Decide(LibraryModels models, GeneratedModelTable stored, GenerationCandidate candidate)
    {
        WorkCancellation.Check();
        // Without a compilation the models answer from the project and built-in layers alone; the match names its layer.
        var match = models.Find(candidate.Member);
        var answer = stored.Lookup(candidate.Compilation, Definition(candidate.Member), candidate.Target.LookupId) ?? StoredGeneratedAnswer.Absent;
        return GeneratedLayer.Decide(match is { Layer: ModelLayer.Project } ? match : null, match is { Layer: ModelLayer.BuiltIn } ? match : null, answer,
                                     GeneratedLayer.OriginOf(candidate.Target));
    }

    /// <summary>One member's generation within its limits, classified as R3 and R4 say.</summary>
    /// <param name="target">The target.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    private GeneratedModelAnswer GenerateOne(GenerationTarget target)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var left = _runLimit - _spent;
        if (left <= TimeSpan.Zero)
            return Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s was spent before the member was reached");

        var ownLimitBinds = _memberLimit <= left;
        using var member = CancellationTokenSource.CreateLinkedTokenSource(_runToken, WorkCancellation.Token);
        using var timer = _time.CreateTimer(_ => Cut(member), null, ownLimitBinds ? _memberLimit : left, Timeout.InfiniteTimeSpan);
        var started = _time.GetTimestamp();
        try
        {
            // The compilations the generator adds to the cache belong to this run, and are released with it.
            using var owned = _compilations.Open();
            return Answer(target, _generate(target.Request, member.Token));
        }
        catch (Exception error)
        {
            // Cancellation of the run or enclosing work takes precedence over a member's slice or failure.
            if (_runToken.IsCancellationRequested)
                throw new OperationCanceledException(_runToken);
            if (WorkCancellation.Token.IsCancellationRequested)
                throw new OperationCanceledException(WorkCancellation.Token);
            if (error is OperationCanceledException && member.IsCancellationRequested)
            {
                return ownLimitBinds
                    ? Refused(target, GenerationOutcome.TIMEOUT, $"the generation did not finish within the member limit of {Seconds(_memberLimit)} s")
                    : Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s ended while the member was generated");
            }

            return Refused(target, GenerationOutcome.FAILED, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            _spent += _time.GetElapsedTime(started);
        }
    }

    /// <summary>Cancels a member's token when its timer fires; a generation that ended first has disposed it.</summary>
    /// <param name="member">The member's token source.</param>
    private static void Cut(CancellationTokenSource member)
    {
        try
        {
            member.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The generator's answer as a file keeps it: the model, or a refusal with the generator's reason word, detail and cause words.</summary>
    /// <param name="target">The target generated.</param>
    /// <param name="answer">The generator's answer.</param>
    private static GeneratedModelAnswer Answer(GenerationTarget target, GeneratedAnswer answer) =>
        answer.Model is { } model
            ? GeneratedModelAnswer.OfModel(model)
            : GeneratedModelAnswer.OfRefusal(target.MemberId, new GeneratedRefusal(answer.Reason ?? answer.ModelReason ?? "", answer.Detail,
                                                                                   answer.Causes.Select(cause => cause.Code).ToArray()));

    private static GeneratedModelAnswer Refused(GenerationTarget target, string reason, string detail) =>
        GeneratedModelAnswer.OfRefusal(target.MemberId, new GeneratedRefusal(reason, detail, []));

    private static IMethodSymbol Definition(IMethodSymbol member) => (member.ReducedFrom ?? member).OriginalDefinition;

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
