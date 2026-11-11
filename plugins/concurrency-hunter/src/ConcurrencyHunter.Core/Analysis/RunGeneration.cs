using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
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
/// answers for, generates them from one queue in R4's order within the <b>Generation budget</b> (ADR 0032), remembers every answer by member
/// and file key so that a member met in several scopes shares its answer, and writes only final answers when the caller ends a scope's step.
/// Scopes are analysed one after another. Within a round, taking the next member, queuing a member, publishing an answer, a generation's read
/// of the run's answers and the time spent hold one lock, so that several threads may share the queue: the recording's round drains it on
/// <see cref="RECORDING_WORKERS"/> threads (ADR 0033), every other run's on the caller's thread. The steps between rounds run on one thread
/// with no generation in flight.</summary>
internal sealed class RunGeneration
{
    /// <summary>The run's limit: the time spent inside generations in one run.</summary>
    internal static readonly TimeSpan RUN_LIMIT = TimeSpan.FromSeconds(900);

    /// <summary>The recording's limit: the time spent inside generations in the run that writes a repository's committed generated models.
    /// It must exceed what one run needs, so that the recording is one run, and still end a runaway: the demo's recording needs about 3,100 s,
    /// so two hours leave more than twice that.</summary>
    internal static readonly TimeSpan RECORDING_LIMIT = TimeSpan.FromHours(2);

    /// <summary>One member's limit.</summary>
    internal static readonly TimeSpan MEMBER_LIMIT = GenerationLimits.MEMBER;

    /// <summary>One member's limit in the run that regenerates or settles the shared frameworks' census: five minutes of the member thread's
    /// CPU time, the CoreLib tool's limit, since a census member the run limit cut would ship as a failed member (ADR 0022).</summary>
    internal static readonly TimeSpan CENSUS_MEMBER_LIMIT = TimeSpan.FromMinutes(5);

    /// <summary>The census run's limit, summed over its members like the recording's: twelve hours. With the census members' entries left out
    /// and five minutes for each member, the demo alone spent about 8,700 s, past the recording's two hours, and its second scope was cut;
    /// the limit still ends a regeneration that does not converge.</summary>
    internal static readonly TimeSpan CENSUS_LIMIT = TimeSpan.FromHours(12);

    /// <summary>The recording's worker threads: the members of its round it generates at once (ADR 0033). Every other run generates one at
    /// a time.</summary>
    private const int RECORDING_WORKERS = 4;

    /// <summary>The run limit a budget names: the recording's, the census's, or the run's.</summary>
    /// <param name="budget">The budget.</param>
    internal static TimeSpan RunLimitOf(GenerationBudget budget) => budget switch
    {
        GenerationBudget.Recording => RECORDING_LIMIT,
        GenerationBudget.Census => CENSUS_LIMIT,
        _ => RUN_LIMIT
    };

    /// <summary>The worker threads a budget names: <see cref="RECORDING_WORKERS"/> for the recording and the census, one otherwise.</summary>
    /// <param name="budget">The budget.</param>
    internal static int WorkersOf(GenerationBudget budget) => budget is GenerationBudget.Recording or GenerationBudget.Census ? RECORDING_WORKERS : 1;

    /// <summary>The member limit a budget names: <see cref="CENSUS_MEMBER_LIMIT"/> for the census, <see cref="MEMBER_LIMIT"/> otherwise.</summary>
    /// <param name="budget">The budget.</param>
    internal static TimeSpan MemberLimitOf(GenerationBudget budget) => budget == GenerationBudget.Census ? CENSUS_MEMBER_LIMIT : MEMBER_LIMIT;

    private readonly string _repositoryRoot;
    private readonly CancellationToken _runToken;
    private readonly TimeSpan _runLimit;
    private readonly TimeSpan _memberLimit;
    private readonly TimeProvider _time;
    private readonly Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> _generate;
    private readonly Func<GenerationRequest, CancellationToken, (Func<bool> RunOnly, Func<DependencyAnswers, CancellationToken, GeneratedAnswer> Generate)>? _prepare;
    private readonly Func<GenerationRequest, CancellationToken, int> _rank;
    private readonly int _workers;
    private readonly object _gate = new();
    private readonly Dictionary<GeneratedFileKey, GeneratedModelFile> _files = new();
    private readonly Dictionary<(string MemberId, GeneratedFileKey Key), RunAnswer> _answers = new();
    private readonly PriorityQueue<GenerationTarget, QueueOrder> _queue = new(QueueOrder.Comparer);
    private readonly HashSet<(string MemberId, GeneratedFileKey Key)> _queued = [];
    private readonly HashSet<(string MemberId, GeneratedFileKey Key)> _taken = [];
    private readonly Dictionary<(string MemberId, GeneratedFileKey Key), HashSet<(string MemberId, GeneratedFileKey Key)>> _readers = new();
    private readonly Dictionary<GeneratedFileKey, List<GeneratedModelAnswer>> _pending = new();
    private readonly List<string> _diagnostics = [];
    private readonly CompilationOwnership _compilations = new();
    private readonly Dictionary<Compilation, Dictionary<INamedTypeSymbol, GeneratedFileKey>> _mappedTypes = new();
    private TimeSpan _spent;

    private sealed class RunAnswer(GenerationTarget target, GeneratedModelAnswer answer, IReadOnlyList<DependencyView> views, bool runOnly = false)
    {
        internal GenerationTarget Target { get; } = target;
        internal GeneratedModelAnswer Answer { get; } = answer;
        internal IReadOnlyList<DependencyView> Views { get; } = views;
        internal bool RunOnly { get; } = runOnly;
        internal bool RetryStopped { get; set; }
        internal bool Final { get; set; }
        internal bool Written { get; set; }
    }

    /// <summary>A queued member's place in its round: the targets of the scope's last analysis that have no answer yet come first, members
    /// handed a delegate first among them; every other member comes after them. Within each, members go by the rank of their assembly, then
    /// by declaration id in ordinal order, then by file key.</summary>
    /// <param name="ScopeTarget">Whether the member is a target of the scope's last analysis that has no answer yet.</param>
    /// <param name="HandedDelegate">Whether such a target is handed a delegate at one of its calls; <c>false</c> for every other member.</param>
    /// <param name="Rank">The rank of the member's implementation assembly (<see cref="AssemblyRanks"/>).</param>
    /// <param name="MemberId">The member's declaration id.</param>
    /// <param name="Key">The text of the member's file key.</param>
    private readonly record struct QueueOrder(bool ScopeTarget, bool HandedDelegate, int Rank, string MemberId, string Key)
    {
        /// <summary>The order the summary gives.</summary>
        internal static readonly IComparer<QueueOrder> Comparer = Comparer<QueueOrder>.Create(static (left, right) =>
        {
            var order = right.ScopeTarget.CompareTo(left.ScopeTarget);
            if (order == 0)
                order = right.HandedDelegate.CompareTo(left.HandedDelegate);
            if (order == 0)
                order = left.Rank.CompareTo(right.Rank);
            if (order == 0)
                order = string.CompareOrdinal(left.MemberId, right.MemberId);
            return order != 0 ? order : string.CompareOrdinal(left.Key, right.Key);
        });
    }

    /// <summary>Whether dependencies or changed views still call for another generation round within the budget.</summary>
    internal bool HasWork => Spent < _runLimit && NextRound().Count != 0;

    /// <summary>A run's generation with the server's limits, the system clock, and the model generator and assembly ranks over this process's
    /// install.</summary>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/generated/</c> the run reads and writes.</param>
    /// <param name="cancellationToken">The run's token: its own cancellation or deadline stops generation and the cancellation propagates.</param>
    internal RunGeneration(string repositoryRoot, CancellationToken cancellationToken)
        : this(repositoryRoot, cancellationToken, RUN_LIMIT, MEMBER_LIMIT, TimeProvider.System, DefaultGenerator(),
               new AssemblyRanks(ImplementationAssemblies.ForThisProcess()).Of)
    {
    }

    /// <summary>A run's generation with limits, a clock, a generator and assembly ranks of the caller's choosing.</summary>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/generated/</c> the run reads and writes.</param>
    /// <param name="cancellationToken">The run's token: its own cancellation or deadline stops generation and the cancellation propagates.</param>
    /// <param name="runLimit">The time the run may spend inside generations.</param>
    /// <param name="memberLimit">The CPU time one member's thread may spend inside its generation (<see cref="MemberLimit"/>).</param>
    /// <param name="time">The clock both limits are measured on, and the timer that cuts a member; a clock other than the system clock stands
    /// for a member's CPU time too.</param>
    /// <param name="generate">The generator call: a member's request, the run's raw dependency answers and the token that cuts the generation.</param>
    /// <param name="rank">The rank of a request's implementation assembly (<see cref="AssemblyRanks.Of"/>); called under the round's lock.</param>
    /// <param name="workers">The threads that drain a round's queue (<see cref="WorkersOf"/>); one, the caller's, unless the run records.</param>
    /// <param name="prepare">Obtains a generation over prepared code before the member's clocks and a query of whether its current code
    /// permits storage, or null for the supplied generator alone.</param>
    internal RunGeneration(string repositoryRoot, CancellationToken cancellationToken, TimeSpan runLimit, TimeSpan memberLimit, TimeProvider time,
                           Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> generate,
                           Func<GenerationRequest, CancellationToken, int> rank, int workers = 1,
                           Func<GenerationRequest, CancellationToken, (Func<bool> RunOnly, Func<DependencyAnswers, CancellationToken, GeneratedAnswer> Generate)>? prepare = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _repositoryRoot = repositoryRoot;
        _runToken = cancellationToken;
        _runLimit = runLimit;
        _memberLimit = memberLimit;
        _time = time;
        _generate = generate;
        _prepare = prepare;
        _rank = rank;
        _workers = workers;
    }

    /// <summary>The time spent inside generations so far: the budget's measure, and the run's <c>generation</c> timing.</summary>
    internal TimeSpan Spent
    {
        get
        {
            lock (_gate)
                return _spent;
        }
    }

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

    /// <summary>Generates what a scope's candidates call for, as one round with one queue (ADR 0032). A candidate is generated when
    /// <see cref="GeneratedLayer.Decide"/> says so — no project entry, no built-in entry in range and no stored answer describes it, and its
    /// implementation is on disk — and the run has not answered its target yet. These targets go first, members handed a delegate first
    /// among them. Every other member waits behind them: a dependency that a generation saw without an answer, which joins the queue when that
    /// generation's answer is published, and a member whose view of a published answer changed, except cycles. Within each, members go by
    /// the rank of their assembly, then in ordinal order of declaration id, then of file key. The run's workers drain the queue, and the round
    /// ends when it is empty and no member is in flight. When a member's own limit is not larger than what is left of the run's, the member
    /// runs under its own limit alone, counted in its thread's CPU time (<see cref="MemberLimit"/>), and a cut is <c>timeout</c>; otherwise it
    /// runs under what is left of the run's limit on the clock, and a cut is <c>budget</c>. A member the run's limit has already been spent
    /// before is <c>budget</c> without a generation. Final answers wait for <see cref="EndScope"/>; a stopped repeat keeps its old answer
    /// applied in memory and unkept.</summary>
    /// <param name="models">The scope's models without the generated layer: they say whether a project or a built-in entry describes a member.</param>
    /// <param name="candidates">The scope's candidates, one per reached call.</param>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal void Generate(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        WorkCancellation.Check();
        _runToken.ThrowIfCancellationRequested();
        var targets = Wanted(models, candidates);
        var waiting = NextRound();
        lock (_gate)
        {
            // A round that a cancellation stopped leaves its queue behind; what it still called for is in the answers.
            _queue.Clear();
            _queued.Clear();
            _taken.Clear();
            foreach (var (target, handed) in targets)
            {
                WorkCancellation.Check();
                Queue(target, scopeTarget: true, handedDelegate: handed);
            }
            foreach (var target in waiting.Values)
            {
                WorkCancellation.Check();
                Queue(target, scopeTarget: false, handedDelegate: false);
            }
        }
        if (_workers == 1)
            Drain();
        else
            DrainOnWorkers();
        FinalizeAnswers();
    }

    /// <summary>Generates the queue's members until it is empty: takes the first, generates it and publishes its answer. Generations run
    /// outside the lock, so the loop may run on several threads at once; with one worker <see cref="Generate"/> runs it once, on its caller's
    /// thread. A member whose generation throws stays in flight until the next round.</summary>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    internal void Drain()
    {
        while (TryTake(out var target))
        {
            WorkCancellation.Check();
            Publish(target, GenerateOne(target));
        }
    }

    /// <summary>Drains the queue on the run's worker threads. A worker that finds the queue empty waits while another has a member in flight,
    /// because publishing that member's answer can queue more; all stop when the queue is empty and no member is in flight, or when one of
    /// them throws, and the first exception is thrown again here.</summary>
    /// <exception cref="OperationCanceledException">The run or enclosing work was cancelled.</exception>
    private void DrainOnWorkers()
    {
        Exception? failure = null;
        var threads = Enumerable.Range(1, _workers).Select(worker => new Thread(() =>
        {
            try
            {
                while (TakeOrWait(() => Volatile.Read(ref failure) is not null, out var target))
                {
                    WorkCancellation.Check();
                    Publish(target, GenerateOne(target));
                    lock (_gate)
                        Monitor.PulseAll(_gate);
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                lock (_gate)
                    Monitor.PulseAll(_gate);
            }
        }) { Name = $"generation worker {worker}", IsBackground = true }).ToArray();
        foreach (var thread in threads)
        {
            WorkCancellation.Check();
            thread.Start();
        }
        // bounded: one join per worker; the workers themselves observe cancellation.
        foreach (var thread in threads)
            thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Takes the first member of the queue, waiting while it is empty and another worker has a member in flight.</summary>
    /// <param name="failed">Whether a worker has thrown, which ends every worker.</param>
    /// <param name="target">The member taken.</param>
    /// <returns><c>false</c> when the queue is empty and no member is in flight, or a worker has thrown.</returns>
    private bool TakeOrWait(Func<bool> failed, [NotNullWhen(true)] out GenerationTarget? target)
    {
        lock (_gate)
        {
            while (true)
            {
                WorkCancellation.Check();
                target = null;
                if (failed())
                    return false;
                if (TryTake(out target))
                    return true;
                if (_taken.Count == 0)
                {
                    Monitor.PulseAll(_gate);
                    return false;
                }
                Monitor.Wait(_gate);
            }
        }
    }

    /// <summary>Takes the first member of the queue, under the lock; the member is in flight until its answer is published.</summary>
    /// <param name="target">The member taken.</param>
    /// <returns><c>false</c> when the queue is empty.</returns>
    private bool TryTake([NotNullWhen(true)] out GenerationTarget? target)
    {
        lock (_gate)
        {
            if (!_queue.TryDequeue(out target, out _))
                return false;
            _queued.Remove((target.MemberId, target.Key));
            _taken.Add((target.MemberId, target.Key));
            return true;
        }
    }

    /// <summary>Queues a member at its place, unless it waits in the queue already or is in flight; the caller holds the lock. A member in
    /// flight is checked again when its answer is published.</summary>
    /// <param name="target">The member.</param>
    /// <param name="scopeTarget">Whether it is a target of the scope's last analysis that has no answer yet.</param>
    /// <param name="handedDelegate">Whether such a target is handed a delegate at one of its calls.</param>
    private void Queue(GenerationTarget target, bool scopeTarget, bool handedDelegate)
    {
        var identity = (target.MemberId, target.Key);
        if (!_taken.Contains(identity) && _queued.Add(identity))
            _queue.Enqueue(target, new QueueOrder(scopeTarget, scopeTarget && handedDelegate, _rank(target.Request, _runToken), target.MemberId, target.Key.Text));
    }

    /// <summary>Publishes a generation's answer and queues what it calls for, under the lock: every dependency it saw that has no answer in
    /// the run or its file and whose implementation is on disk, every member that saw this one and now sees it differently, and this member
    /// again when a dependency's answer changed while it was generated — except a member whose views lead back to itself or whose repeat was
    /// stopped. A stopped repeat keeps the earlier answer, applied but never final, and queues nothing.</summary>
    /// <param name="target">The member generated.</param>
    /// <param name="answer">Its answer.</param>
    private void Publish(GenerationTarget target, RunAnswer answer)
    {
        lock (_gate)
        {
            var identity = (target.MemberId, target.Key);
            _taken.Remove(identity);
            if (_answers.TryGetValue(identity, out var earlier) &&
                answer.Answer.Outcome.Kind is GenerationOutcomeKind.Budget or GenerationOutcomeKind.Failed)
            {
                // A stopped retry keeps the previous answer applied, but never makes it final.
                earlier.RetryStopped = true;
                return;
            }

            // The answer's readers are known before it is stored, so a cancellation below leaves only the queue incomplete, and the next
            // round builds that again from the answers.
            // bounded: one pass over the views the generation reported.
            foreach (var view in answer.Views)
            {
                var dependency = (view.Target.MemberId, view.Target.Key);
                if (!_readers.TryGetValue(dependency, out var readers))
                    _readers.Add(dependency, readers = []);
                readers.Add(identity);
            }
            _answers[identity] = answer;
            foreach (var view in answer.Views)
            {
                WorkCancellation.Check();
                if (view.Target is GenerationTarget next && !_answers.ContainsKey((next.MemberId, next.Key)) && DependencyAnswer(next.MemberId, next.Key) is null)
                    Queue(next, scopeTarget: false, handedDelegate: false);
            }
            foreach (var reader in _readers.GetValueOrDefault(identity, []).Append(identity))
            {
                WorkCancellation.Check();
                if (_answers.TryGetValue(reader, out var read) && !read.RetryStopped && read.Views.Any(view => !SameView(view)) && !InCycle(reader))
                    Queue(read.Target, scopeTarget: false, handedDelegate: false);
            }
        }
    }

    /// <summary>The run's answer before applicability, otherwise the current generator version's stored answer, read under the lock.</summary>
    /// <param name="memberId">The dependency's member id.</param>
    /// <param name="key">The dependency's own file key.</param>
    private GeneratedModelEntry? DependencyAnswer(string memberId, GeneratedFileKey key)
    {
        lock (_gate)
        {
            return _answers.TryGetValue((memberId, key), out var answer)
                ? GeneratedModelFiles.EntryOf(answer.Answer, FileOf(key).DisplayPath)
                : FileOf(key).Entries.GetValueOrDefault(memberId);
        }
    }

    /// <summary>The unanswered dependencies and the members whose raw dependency models changed: nothing once a round's queue is empty.</summary>
    private Dictionary<(string MemberId, GeneratedFileKey Key), GenerationTarget> NextRound()
    {
        var wanted = new Dictionary<(string MemberId, GeneratedFileKey Key), GenerationTarget>();
        foreach (var (identity, answer) in _answers)
        {
            WorkCancellation.Check();
            foreach (var view in answer.Views)
            {
                WorkCancellation.Check();
                var dependency = (view.Target.MemberId, view.Target.Key);
                if (view.Target is GenerationTarget target && !_answers.ContainsKey(dependency) &&
                    DependencyAnswer(dependency.MemberId, dependency.Key) is null)
                    wanted[dependency] = target;
            }
            if (!answer.RetryStopped && answer.Views.Any(view => !SameView(view)) && !InCycle(identity))
                wanted[identity] = answer.Target;
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

    /// <summary>The single owner of finality: the least fixed point of kept outcomes on code that is not only for this run, with unchanged,
    /// final dependency answers.</summary>
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
                if (answer.Final || answer.RunOnly || answer.RetryStopped || !GeneratedModelFiles.Keeps(answer.Answer.Outcome) ||
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
        var left = _runLimit - Spent;
        if (left <= TimeSpan.Zero)
            return new RunAnswer(target, Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s was spent before the member was reached"), []);

        using var preparation = WorkCancellation.Enter(_runToken);
        var prepared = _prepare?.Invoke(target.Request, WorkCancellation.Token);
        WorkCancellation.Check();
        var ownLimitBinds = _memberLimit <= left;
        using var member = CancellationTokenSource.CreateLinkedTokenSource(_runToken, WorkCancellation.Token);
        using var ownLimit = ownLimitBinds ? new MemberLimit(member, _memberLimit, _time) : null;
        using var runLimit = ownLimitBinds ? null : _time.CreateTimer(_ => Cut(member), null, left, Timeout.InfiniteTimeSpan);
        var started = _time.GetTimestamp();
        try
        {
            // The compilations the generator adds to the cache belong to this run, and are released with it.
            using var owned = _compilations.Open();
            var generated = prepared is null ? _generate(target.Request, DependencyAnswer, member.Token) : prepared.Value.Generate(DependencyAnswer, member.Token);
            return new RunAnswer(target, Answer(target, generated), generated.DependencyViews, generated.Generation.Code is { } code
                ? code.Kind == LibraryCodeKind.DecompiledForRun : prepared?.RunOnly() ?? false);
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
                    : Refused(target, GenerationOutcome.BUDGET, $"the run's generation budget of {Seconds(_runLimit)} s ended while the member was generated"), [], prepared?.RunOnly() ?? false);
            }

            return new RunAnswer(target, Refused(target, GenerationOutcome.FAILED, $"{error.GetType().Name}: {error.Message}"), [], prepared?.RunOnly() ?? false);
        }
        finally
        {
            var elapsed = _time.GetElapsedTime(started);
            lock (_gate)
                _spent += elapsed;
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
