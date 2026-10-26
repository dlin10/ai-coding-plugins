using System.Globalization;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>A library member that an opaque or generated-layer call of a scope reached, as a run weighs it for generation (R1, R4).</summary>
/// <param name="Compilation">The compilation the call is in.</param>
/// <param name="Member">The called member.</param>
/// <param name="Target">The member's generation target.</param>
/// <param name="HandedDelegate">Whether the scope's summary of a reached call of the member records a delegate created as an argument of that
/// call (<c>SummaryOpaqueCall.Delegates</c>); it decides only which members the budget reaches first.</param>
internal readonly record struct GenerationCandidate(Compilation Compilation, IMethodSymbol Member, IdentifiedGenerationTarget Target, bool HandedDelegate);

/// <summary>The generated layer one scope's analysis reads (R2): the stored answers of the files its candidates name together with the run's own
/// answers, as the table lowering consults.</summary>
/// <param name="Table">The layer.</param>
/// <param name="Describes">Whether the layer describes at least one of the scope's candidates.</param>
/// <param name="Diagnostics">One diagnostic, naming the compilation, for each model, stored or generated, that does not apply in a compilation of
/// the scope.</param>
internal readonly record struct ScopeLayer(GeneratedModelTable Table, bool Describes, IReadOnlyList<string> Diagnostics);

/// <summary>A run's generation (R1–R4): over the candidates of one scope at a time it decides which members no layer describes and no file
/// answers for, generates them in R4's order within the <b>Generation budget</b>, remembers every answer by member and file key so that a
/// member met in several scopes shares its answer, and writes only final answers when the caller ends a scope's step. It is used by one
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
    private readonly Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> _generate;
    private readonly Dictionary<GeneratedFileKey, GeneratedModelFile> _files = new();
    private readonly Dictionary<(string MemberId, GeneratedFileKey Key), RunAnswer> _answers = new();
    private readonly Dictionary<GeneratedFileKey, List<GeneratedModelAnswer>> _pending = new();
    private readonly List<string> _diagnostics = [];
    private readonly CompilationOwnership _compilations = new();
    private readonly Dictionary<Compilation, Dictionary<INamedTypeSymbol, GeneratedFileKey>> _mappedTypes = new();
    private TimeSpan _spent;

    private sealed class RunAnswer(GenerationTarget target, GeneratedModelAnswer answer, IReadOnlyList<DependencyView> views)
    {
        internal GenerationTarget Target { get; } = target;
        internal GeneratedModelAnswer Answer { get; } = answer;
        internal IReadOnlyList<DependencyView> Views { get; } = views;
        internal bool RetryStopped { get; set; }
        internal bool Final { get; set; }
        internal bool Written { get; set; }
    }

    /// <summary>Whether dependencies or changed views still call for another generation round within the budget.</summary>
    internal bool HasWork => _spent < _runLimit && NextRound().Count != 0;

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
    /// <param name="generate">The generator call: a member's request, the run's raw dependency answers and the token that cuts the generation.</param>
    internal RunGeneration(string repositoryRoot, CancellationToken cancellationToken, TimeSpan runLimit, TimeSpan memberLimit, TimeProvider time,
                           Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> generate)
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
    internal GeneratedModelAnswer? AnswerOf(IdentifiedGenerationTarget target) => _answers.GetValueOrDefault((target.MemberId, target.Key))?.Answer;

    /// <summary>Generates what a scope's candidates call for. A candidate is generated when <see cref="GeneratedLayer.Decide"/> says so — no
    /// project entry, no built-in entry in range and no stored answer describes it, and its implementation is on disk — and the run has not
    /// answered its target yet. The round also answers dependencies and repeats members whose dependency models changed, except cycles.
    /// Members handed a delegate come first, then the rest, each group in ordinal order of declaration id and then
    /// of file key. A member runs under a token cancelled after the smaller of its own limit and what is left of the run's; when its own
    /// limit is not larger than what is left a cut is <c>timeout</c>, otherwise <c>budget</c>. A member the run's limit has already been spent
    /// before is <c>budget</c> without a generation. Final answers wait for <see cref="EndScope"/>; a stopped repeat keeps its old answer
    /// applied in memory and unkept.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, one per reached call.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal void Generate(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var wanted = NextRound();
        foreach (var (target, handed) in Wanted(models, candidates))
        {
            WorkCancellation.Check();
            wanted[(target.MemberId, target.Key)] = (target, handed);
        }
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            var identity = (candidate.Target.MemberId, candidate.Target.Key);
            if (candidate.HandedDelegate && wanted.TryGetValue(identity, out var item))
                wanted[identity] = (item.Target, true);
        }
        foreach (var (target, _) in wanted.Values.OrderByDescending(item => item.HandedDelegate)
                                                .ThenBy(item => item.Target.MemberId, StringComparer.Ordinal)
                                                .ThenBy(item => item.Target.Key.Text, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            var identity = (target.MemberId, target.Key);
            var answer = GenerateOne(target);
            if (_answers.TryGetValue(identity, out var earlier) &&
                answer.Answer.Outcome.Kind is GenerationOutcomeKind.Budget or GenerationOutcomeKind.Failed)
            {
                // A stopped retry keeps the previous answer applied, but never makes it final.
                earlier.RetryStopped = true;
            }
            else
                _answers[identity] = answer;
        }
        FinalizeAnswers();
    }

    /// <summary>The run's answer before applicability, otherwise the current generator version's stored answer.</summary>
    /// <param name="memberId">The dependency's member id.</param>
    /// <param name="key">The dependency's own file key.</param>
    private GeneratedModelEntry? DependencyAnswer(string memberId, GeneratedFileKey key) =>
        _answers.TryGetValue((memberId, key), out var answer)
            ? GeneratedModelFiles.EntryOf(answer.Answer, FileOf(key).DisplayPath)
            : FileOf(key).Entries.GetValueOrDefault(memberId);

    /// <summary>The next round's unanswered dependencies and members whose raw dependency models changed.</summary>
    private Dictionary<(string MemberId, GeneratedFileKey Key), (GenerationTarget Target, bool HandedDelegate)> NextRound()
    {
        var wanted = new Dictionary<(string MemberId, GeneratedFileKey Key), (GenerationTarget Target, bool HandedDelegate)>();
        foreach (var (identity, answer) in _answers)
        {
            WorkCancellation.Check();
            foreach (var view in answer.Views)
            {
                WorkCancellation.Check();
                var dependency = (view.Target.MemberId, view.Target.Key);
                if (view.Target is GenerationTarget target && !_answers.ContainsKey(dependency) &&
                    DependencyAnswer(dependency.MemberId, dependency.Key) is null)
                    wanted[dependency] = (target, false);
            }
            if (!answer.RetryStopped && answer.Views.Any(view => !SameView(view)) && !InCycle(identity))
                wanted[identity] = (answer.Target, false);
        }
        return wanted;
    }

    /// <summary>Whether the dependency's current raw model has the bytes the generation saw; refusals and no answer look alike.</summary>
    /// <param name="view">What the generation saw.</param>
    private bool SameView(DependencyView view) =>
        string.Equals(view.ModelText, DependencyAnswer(view.Target.MemberId, view.Target.Key) is { } entry
            ? GeneratedModelFiles.ModelText(entry) : null, StringComparison.Ordinal);

    /// <summary>Whether a member's dependency views lead back to that member.</summary>
    /// <param name="identity">The member and its file.</param>
    private bool InCycle((string MemberId, GeneratedFileKey Key) identity)
    {
        var seen = new HashSet<(string MemberId, GeneratedFileKey Key)>();
        var pending = new Stack<(string MemberId, GeneratedFileKey Key)>();
        pending.Push(identity);
        while (pending.TryPop(out var current))
        {
            WorkCancellation.Check();
            if (!seen.Add(current) || !_answers.TryGetValue(current, out var answer))
                continue;
            foreach (var view in answer.Views)
            {
                WorkCancellation.Check();
                var dependency = (view.Target.MemberId, view.Target.Key);
                if (dependency == identity)
                    return true;
                pending.Push(dependency);
            }
        }
        return false;
    }

    /// <summary>The single owner of finality: the least fixed point of kept outcomes with unchanged, final dependency answers.</summary>
    private void FinalizeAnswers()
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        _pending.Clear();
        foreach (var answer in _answers.Values)
        {
            WorkCancellation.Check();
            answer.Final = false;
        }
        var changed = true;
        while (changed)
        {
            WorkCancellation.Check();
            changed = false;
            foreach (var answer in _answers.Values)
            {
                WorkCancellation.Check();
                if (answer.Final || answer.RetryStopped || !GeneratedModelFiles.Keeps(answer.Answer.Outcome) ||
                    answer.Views.Any(view => !SameView(view) || !IsFinal(view.Target)))
                    continue;
                answer.Final = true;
                changed = true;
            }
        }
        foreach (var answer in _answers.Values)
        {
            WorkCancellation.Check();
            if (!answer.Final || answer.Written)
                continue;
            if (!_pending.TryGetValue(answer.Target.Key, out var kept))
                _pending.Add(answer.Target.Key, kept = []);
            kept.Add(answer.Answer);
        }
    }

    /// <summary>Whether a member's current answer is final; a stored answer is final even without an implementation on disk.</summary>
    /// <param name="target">The member's target.</param>
    private bool IsFinal(IdentifiedGenerationTarget target) =>
        _answers.TryGetValue((target.MemberId, target.Key), out var answer) ? answer.Final : FileOf(target.Key).Entries.ContainsKey(target.MemberId);

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

    /// <summary>Ends a scope's generation step: recalculates finality and writes final answers to their own files, each under the lock every
    /// writer of it takes. An answer
    /// whose file cannot be written stays in memory and describes its calls in this run; the write error is a diagnostic.</summary>
    /// <param name="beforeFileRead">A test hook before a writer re-reads a generated file under its lock.</param>
    /// <exception cref="OperationCanceledException">The run's own token fired, before or while a writer waited for a file's lock.</exception>
    internal void EndScope(Action? beforeFileRead)
    {
        FinalizeAnswers();
        var pending = _pending.OrderBy(entry => entry.Key.Text, StringComparer.Ordinal).ToArray();
        _pending.Clear();
        foreach (var (key, answers) in pending)
        {
            WorkCancellation.Check();
            _diagnostics.AddRange(GeneratedModelFiles.Write(FileOf(key), answers, _runToken, beforeRead: beforeFileRead));
            foreach (var answer in answers)
            {
                WorkCancellation.Check();
                _answers[(answer.Member, key)].Written = true;
            }
        }
    }

    /// <summary>The generated layer a scope's analysis reads: the files its candidates name as the run read them, and the run's own answers
    /// on top, each read back as a file's entry is, so that a model the run generated describes its calls whether or not its file could be
    /// written or read. A fresh table includes every type mapped in this run, with run answers replacing stored entries.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, the list <see cref="Generate"/> was given.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal ScopeLayer Layer(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var (table, diagnostics) = StoredView(candidates, includeRun: true);
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
    private static Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> DefaultGenerator()
    {
        var resolver = ImplementationAssemblies.ForThisProcess();
        return (request, dependencies, token) => ModelGenerator.Generate(request, resolver, token, dependencies);
    }

    /// <summary>What the generated files say about the candidates, as the analysis with the generated layer will see it: the declaring type
    /// of each candidate mapped to its file, and the files added.</summary>
    /// <param name="candidates">The scope's candidates, whose type mappings join those of earlier rounds and scopes.</param>
    /// <param name="includeRun">Whether the run's answers replace the entries read from files.</param>
    private (GeneratedModelTable Table, IReadOnlyList<string> Diagnostics) StoredView(IReadOnlyList<GenerationCandidate> candidates, bool includeRun = false)
    {
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            if (!_mappedTypes.TryGetValue(candidate.Compilation, out var types))
                _mappedTypes.Add(candidate.Compilation, types = new Dictionary<INamedTypeSymbol, GeneratedFileKey>(SymbolEqualityComparer.Default));
            types[Definition(candidate.Member).ContainingType] = candidate.Target.Key;
        }

        var table = new GeneratedModelTable();
        foreach (var (compilation, types) in _mappedTypes)
        {
            WorkCancellation.Check();
            foreach (var (type, key) in types)
            {
                WorkCancellation.Check();
                table.MapType(compilation, type, key);
            }
        }
        var diagnostics = new List<string>();
        foreach (var key in _mappedTypes.Values.SelectMany(types => types.Values).Distinct())
        {
            WorkCancellation.Check();
            var file = FileOf(key);
            if (includeRun)
            {
                var entries = new Dictionary<string, GeneratedModelEntry>(file.Entries, StringComparer.Ordinal);
                foreach (var ((member, answerKey), answer) in _answers)
                {
                    WorkCancellation.Check();
                    if (answerKey == key && GeneratedModelFiles.EntryOf(answer.Answer, file.DisplayPath) is { } entry)
                        entries[member] = entry;
                }
                file = file with { Entries = entries, Diagnostics = [] };
            }
            diagnostics.AddRange(table.AddFile(file));
        }
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
    private RunAnswer GenerateOne(GenerationTarget target)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var left = _runLimit - _spent;
        if (left <= TimeSpan.Zero)
            return new RunAnswer(target, Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s was spent before the member was reached"), []);

        var ownLimitBinds = _memberLimit <= left;
        using var member = CancellationTokenSource.CreateLinkedTokenSource(_runToken, WorkCancellation.Token);
        using var timer = _time.CreateTimer(_ => Cut(member), null, ownLimitBinds ? _memberLimit : left, Timeout.InfiniteTimeSpan);
        var started = _time.GetTimestamp();
        try
        {
            // The compilations the generator adds to the cache belong to this run, and are released with it.
            using var owned = _compilations.Open();
            var generated = _generate(target.Request, DependencyAnswer, member.Token);
            return new RunAnswer(target, Answer(target, generated), generated.DependencyViews);
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
                return new RunAnswer(target, ownLimitBinds
                    ? Refused(target, GenerationOutcome.TIMEOUT, $"the generation did not finish within the member limit of {Seconds(_memberLimit)} s")
                    : Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s ended while the member was generated"), []);
            }

            return new RunAnswer(target, Refused(target, GenerationOutcome.FAILED, $"{error.GetType().Name}: {error.Message}"), []);
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
