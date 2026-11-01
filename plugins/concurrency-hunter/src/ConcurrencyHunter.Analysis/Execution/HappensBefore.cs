using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Execution;

/// <summary>A body segment an execution runs.</summary>
/// <param name="InstanceId">The method instance visited.</param>
/// <param name="Segment">The segment of its body the execution runs.</param>
internal sealed record ExecutionVisit(string InstanceId, BodySegment Segment);

/// <summary>A call edge an execution follows, with the segments of caller and callee it runs.</summary>
/// <param name="Caller">The calling instance.</param>
/// <param name="CallerSegment">The segment of the caller's body the call is in.</param>
/// <param name="OperationId">The call operation.</param>
/// <param name="Callee">The called instance.</param>
/// <param name="CalleeSegment">The segment of the callee's body the execution runs.</param>
internal sealed record ExecutionStep(string Caller, BodySegment CallerSegment, int OperationId, string Callee, BodySegment CalleeSegment);

internal enum SpawnAnchorKind
{
    Spawn,
    Timer,
    AsyncReturn
}

/// <summary>Where an execution starts a child: at a spawn or timer operation, or, for an async call, at the synthetic point where the
/// call returns.</summary>
/// <param name="ExecutionId">The execution that starts the child.</param>
/// <param name="CallerInstance">The instance making the spawn, timer or async call.</param>
/// <param name="OperationId">The operation that starts the child.</param>
/// <param name="ChildId">The child execution started.</param>
/// <param name="Kind">Whether the anchor is a spawn, a timer or an async call's return.</param>
internal sealed record SpawnAnchor(string ExecutionId, string CallerInstance, int OperationId, string ChildId, SpawnAnchorKind Kind);

/// <summary>The assumptions of ADR 0030 that reading the exceptional exit of a blocking wait for a task as that task's completion rests
/// on (R5): the thread context of the waiting code, or of the scheduler a waited task takes from it; the thread-pool entry of waiting
/// code that runs as thread-pool work; the observers, which every such reading rests on.</summary>
[Flags]
internal enum OrderingAssumption
{
    None = 0,
    ThreadContext = 1,
    ThreadPoolEntry = 2,
    Observers = 4
}

/// <summary>
/// The happens-before graph of ADR 0008 over one scope's executions, queried per pair of accesses. Inside an execution a point precedes
/// another when no valid interprocedural path leads back from the second to the first; a join precedes what cannot be reached from the
/// execution's start around it. Between executions the edges are spawns, proven joins, <c>Parallel</c> returns, continuations, the tails
/// of async work and the end of startup; a path counts only when every inter-execution edge on it holds for every instance it
/// connects. In the context of a <c>TreeConfined</c> region's creator, that creator runs once, and every condition an edge puts on an
/// execution running or being started once is read per run of it (ADR 0020). Each execution's graph is built once per context, when a
/// query first needs it.
/// </summary>
internal sealed partial class HappensBefore
{
    private readonly ScopeProgram _scope;
    private readonly HeapSolution _heap;
    private readonly AsyncSegments _segments;
    private readonly IReadOnlyDictionary<string, ExecutionInstance> _executions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> _entries;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>> _visits;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>> _steps;
    private readonly IReadOnlyDictionary<string, string> _tails;
    private readonly IReadOnlySet<string> _duringStartup;
    private readonly Dictionary<string, List<SpawnAnchor>> _anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpawnAnchor> _anchorOfChild = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Caller, int Operation), SpawnSite> _spawnSites;
    private readonly Dictionary<string, List<string>> _handles = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<ExecutionModel.AdmittedCall> _admittedCalls;

    /// <summary>The admitted calls whose record also orders every run's continuation: the only ones joins, anchors and call tasks read.</summary>
    private readonly IReadOnlyList<ExecutionModel.AdmittedCall> _awaitedCalls;

    /// <summary>The admitted calls, sequential and sequential-awaited alike, whose next run begins only after this one returns: the
    /// pass completion proof reads their repetition as a boundary between passes.</summary>
    private readonly HashSet<(string Instance, int Operation)> _sequentialRuns;
    private readonly Dictionary<string, Flow> _plainFlows = new(StringComparer.Ordinal);
    private readonly HashSet<(string Instance, int Operation)> _proving = [];
    private readonly HashSet<(string Instance, int Operation)> _provingBindings = [];
    private readonly HashSet<(string Task, int Depth)> _provingTasks = [];
    private readonly Dictionary<string, bool> _quietEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _enumerated = new(StringComparer.Ordinal);
    private readonly Context _global = new(null);
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.Ordinal);

    /// <summary>The context the query in progress reads its edges in: the whole scope, or one run of a creator.</summary>
    private Context _context;
    private CancellationToken _constructionToken;
    private PassCompletionProof? _passCompletion;
    private ThreadContextFacts? _contextFacts;
    private Dictionary<string, List<SpawnSite>>? _spawnsOfHandle;
    private readonly IReadOnlyList<SpawnAnchor> _anchorList;

    /// <summary>The assumption sets whose blocking waits for a task this graph reads as ordering nothing on their exceptional exit, as
    /// the cases of R4; null for the analysis's own graph.</summary>
    private readonly IReadOnlySet<OrderingAssumption>? _withheld;

    /// <summary>The assumption sets of the readings of an exceptional exit as completion that the queries so far made, each once.</summary>
    private readonly HashSet<OrderingAssumption> _assumptions = [];

    /// <summary>The graphs that withhold some assumption sets, by the sets withheld, made the first time a pair asks for one.</summary>
    private readonly Dictionary<string, HappensBefore> _replays = new(StringComparer.Ordinal);

    /// <summary>Every assumption set a reading can rest on: the waiting code's entry with or without the waited task's thread scheduler.</summary>
    private static readonly OrderingAssumption[] AssumptionSets =
    [
        OrderingAssumption.ThreadContext | OrderingAssumption.Observers,
        OrderingAssumption.ThreadPoolEntry | OrderingAssumption.Observers,
        OrderingAssumption.ThreadPoolEntry | OrderingAssumption.ThreadContext | OrderingAssumption.Observers
    ];

    /// <summary>What the edges of one context decide and cache: the whole scope (no creator), or one run of the creator of a
    /// <c>TreeConfined</c> region, where every condition an edge puts on an execution running or being started once is read per run of
    /// that creator (ADR 0020).</summary>
    /// <param name="creator">The creator, or null for the whole scope.</param>
    private sealed class Context(string? creator)
    {
        internal string? Creator { get; } = creator;
        internal Dictionary<string, bool> Composite { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<string>> Continuations { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Flow> Flows { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, IReadOnlyList<JoinAnchor>> Joins { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<(string Execution, int Join)>> JoinedBy { get; } = new(StringComparer.Ordinal);
        internal HashSet<(string BodyId, int OperationId)> UnprovenJoins { get; } = [];
        internal Dictionary<string, HashSet<string>> ExecutionReach { get; } = new(StringComparer.Ordinal);
        internal bool JoinsReady { get; set; }
    }

    internal HappensBefore(ScopeProgram scope, HeapSolution heap, AsyncSegments segments, IReadOnlyDictionary<string, ExecutionInstance> executions,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> entries,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>> visits,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>> steps, IReadOnlyList<SpawnAnchor> anchors,
                           IReadOnlyDictionary<string, string> tails, IReadOnlySet<string> duringStartup,
                           IReadOnlyList<ExecutionModel.AdmittedCall> admittedCalls, CancellationToken cancellationToken,
                           IReadOnlySet<OrderingAssumption>? withheld = null)
    {
        _constructionToken = cancellationToken;
        _withheld = withheld;
        _anchorList = anchors;
        _context = _global;
        _scope = scope;
        _heap = heap;
        _segments = segments;
        _executions = executions;
        _entries = entries;
        _visits = visits;
        _steps = steps;
        _tails = tails;
        _duringStartup = duringStartup;
        _admittedCalls = admittedCalls;
        _awaitedCalls = admittedCalls.Where(call => call.Relation == RunRelation.SequentialAwaited).ToArray();
        _sequentialRuns = admittedCalls.Select(call => (call.Instance, call.Operation)).ToHashSet();
        _spawnSites = heap.Spawns.ToDictionary(site => (site.CallerInstance, site.OperationId));
        var asyncSites = heap.AsyncSpawns.ToDictionary(site => (site.CallerInstance, site.OperationId));
        var timerSites = heap.TimerCallbacks.ToDictionary(site => (site.CallerInstance, site.OperationId));
        foreach (var anchor in anchors.OrderBy(anchor => anchor.ExecutionId, StringComparer.Ordinal).ThenBy(anchor => anchor.CallerInstance, StringComparer.Ordinal)
                                      .ThenBy(anchor => anchor.OperationId))
        {
            CheckCancellation();
            if (!_executions.ContainsKey(anchor.ExecutionId) || !_executions.ContainsKey(anchor.ChildId))
                continue;
            Get(_anchors, anchor.ExecutionId).Add(anchor);
            _anchorOfChild.TryAdd(anchor.ChildId, anchor);
            IEnumerable<string> handles = anchor.Kind switch
            {
                SpawnAnchorKind.Spawn => _spawnSites.GetValueOrDefault((anchor.CallerInstance, anchor.OperationId))?.Handles ?? new HashSet<string>(),
                SpawnAnchorKind.AsyncReturn => asyncSites.GetValueOrDefault((anchor.CallerInstance, anchor.OperationId))?.Handle is { } handle ? [handle] : [],
                _ => timerSites.GetValueOrDefault((anchor.CallerInstance, anchor.OperationId))?.Timers ?? new HashSet<string>()
            };
            foreach (var handle in handles)
            {
                CheckCancellation();
                AddHandle(handle, anchor.ChildId);
                if (_heap.Tails.TryGetValue(handle, out var tail) && _tails.TryGetValue(anchor.ChildId, out var tailExecution) &&
                    AllWorkAsync(anchor))
                {
                    AddHandle(tail, tailExecution);
                }
            }
        }

        foreach (var child in _executions.Values.Where(execution => execution.ParentId is not null).Select(execution => execution.Id))
        {
            CheckCancellation();
            IsComposite(child);
        }

        SpawnSites = _anchors.Values.SelectMany(list => list)
                             .Where(anchor => anchor.Kind != SpawnAnchorKind.Timer && _executions[anchor.ChildId].Origin is not null)
                             .Select(anchor => new SpawnSiteCoverage(new OperationSite(_executions[anchor.ChildId].Origin!.BodyId, anchor.OperationId),
                                                                     _executions[anchor.ChildId].Origin!.Api))
                             .Distinct()
                             .OrderBy(site => site.Api, StringComparer.Ordinal)
                             .ThenBy(site => site.Site.BodyId, StringComparer.Ordinal)
                             .ThenBy(site => site.Site.OperationId)
                             .ToArray();
        _constructionToken = CancellationToken.None;
    }

    /// <summary>The distinct spawn and async call sites, each with the API it calls.</summary>
    internal IReadOnlyList<SpawnSiteCoverage> SpawnSites { get; }

    private void CheckCancellation()
    {
        WorkCancellation.Check();
        _constructionToken.ThrowIfCancellationRequested();
    }

    /// <summary>The joins whose handle identity is not proven, counted for the whole scope whatever context a query read.</summary>
    internal IReadOnlyList<OperationSite> UnprovenJoins => In(null, () =>
    {
        EnsureJoins();
        return _context.UnprovenJoins.Select(join => new OperationSite(join.BodyId, join.OperationId))
                       .OrderBy(join => join.BodyId, StringComparer.Ordinal)
                       .ThenBy(join => join.OperationId)
                       .ToArray();
    });

    /// <summary>Whether a happens-before path, or the completion proof of the queried passes, orders two accesses, in the whole scope,
    /// or with a creator given, in one run of it: the creator runs once there, and every condition an edge puts on an execution running
    /// or being started once is read per run of it (ADR 0020).</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other access.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal bool Ordered(Access first, Access second, string? creator = null) => OrderedBy(first, second, creator) is not null;

    /// <summary>What <see cref="Ordered"/> answers, by the branch that orders the pair: a path, else the completion proof of the queried
    /// passes, which a path's answer leaves unasked; null when neither orders it.</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other access.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal TimingBranch? OrderedBy(Access first, Access second, string? creator = null) => In(creator, () =>
        first.ExecutionId != second.ExecutionId && (Path(first, second) || Path(second, first)) ? TimingBranch.HappensBeforePath
        : (_passCompletion ??= new PassCompletionProof(this)).Ordered(first, second, creator) ? TimingBranch.PassCompletion
        : (TimingBranch?)null);

    /// <summary>Whether the path that ordered a pair runs from its first access to its second, on a diagnostic request after
    /// <see cref="OrderedBy"/> answered <see cref="TimingBranch.HappensBeforePath"/>: the direction that decision tried first.</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other access.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal bool PathBefore(Access first, Access second, string? creator) => In(creator, () => Path(first, second));

    /// <summary>The completion proof of the queried passes for one pair, on a diagnostic request: a fresh proof with its own trace, so
    /// neither the analysis's cached proofs nor its answers change. It answers what the analysis's own proof answered for the pair.</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other access.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal PassCompletionTrace TracePassCompletion(Access first, Access second, string? creator) => In(creator, () =>
    {
        var frames = new List<PassFrameEvidence>();
        var steps = new List<PassStepEvidence>();
        var models = new List<ModelRecordEvidence>();
        var proven = new PassCompletionProof(this) { Trace = frames, Steps = steps, Models = models }.Ordered(first, second, creator);
        return new PassCompletionTrace(proven, frames, steps, models);
    });

    /// <summary>Whether a join of a pass's parent, were its handle the pass's own, would order the pass's self-pair: the handle may carry
    /// the pass's own through a call the analysis cannot see into, so that call's semantics decides the order of the passes (R9).</summary>
    /// <param name="instance">The instance making the join.</param>
    /// <param name="operation">The join operation.</param>
    /// <param name="first">One access of the pass's work.</param>
    /// <param name="second">The other access of the same work.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal bool PassJoinWouldOrder(string instance, int operation, Access first, Access second, string? creator) => In(creator, () =>
        (_passCompletion ??= new PassCompletionProof(this)).JoinWouldOrder(instance, operation, first, second, creator));

    /// <summary>The assumptions a pair <see cref="OrderedBy"/> orders is ordered only under (R5): none when the same decision, with the
    /// exceptional exit of every blocking wait for a task ordering nothing as in the cases of R4, still orders it. Otherwise the union of
    /// the assumption sets of the readings the decision needed — each one without which it does not order the pair — and, where those
    /// alone do not order it, of each other reading that orders the pair together with them alone, every other reading withheld: a proof
    /// that cannot do without one reading and has alternatives for the rest rests on the alternatives too. Where nothing qualifies, every
    /// reading taken.</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other access.</param>
    /// <param name="creator">The creator of the <c>TreeConfined</c> region the two are on, or null.</param>
    internal OrderingAssumption Assumed(Access first, Access second, string? creator)
    {
        // Every reading a decision made was taken when it was first computed, so a scope that took none has nothing to withhold.
        if (_assumptions.Count == 0 || Withholding(AssumptionSets).OrderedBy(first, second, creator) is not null)
            return OrderingAssumption.None;
        var taken = AssumptionSets.Where(_assumptions.Contains).ToArray();
        var needed = taken.Where(set => Withholding([set]).OrderedBy(first, second, creator) is null).ToArray();
        // Readings the needed ones order the pair without are no part of its proof.
        var alone = needed.Length != 0 && Withholding(AssumptionSets.Where(other => !needed.Contains(other)).ToArray())
                                              .OrderedBy(first, second, creator) is not null;
        var completing = alone ? [] : taken.Except(needed).Where(set => Withholding(AssumptionSets.Where(other => other != set && !needed.Contains(other)).ToArray())
                                                                            .OrderedBy(first, second, creator) is not null);
        var read = needed.Concat(completing).ToArray();
        return (read.Length != 0 ? read : taken).Aggregate(OrderingAssumption.None, (union, set) => union | set);
    }

    /// <summary>The graph of the same scope whose blocking waits for a task, read under one of the given assumption sets, order nothing
    /// on their exceptional exit.</summary>
    /// <param name="withheld">The assumption sets withheld.</param>
    private HappensBefore Withholding(IReadOnlyList<OrderingAssumption> withheld)
    {
        var key = string.Join(",", withheld.Order());
        if (!_replays.TryGetValue(key, out var replay))
            _replays.Add(key, replay = new HappensBefore(_scope, _heap, _segments, _executions, _entries, _visits, _steps, _anchorList, _tails,
                                                         _duringStartup, _admittedCalls, CancellationToken.None, withheld.ToHashSet()));
        return replay;
    }

    /// <summary>Runs a query in the context of a creator, or of the whole scope, and restores the context it was asked in.</summary>
    /// <typeparam name="T">What the query answers.</typeparam>
    /// <param name="creator">The creator, or null for the whole scope.</param>
    /// <param name="query">The query.</param>
    private T In<T>(string? creator, Func<T> query)
    {
        WorkCancellation.Check();
        var outer = _context;
        _context = ContextOf(creator);
        try
        {
            return query();
        }
        finally
        {
            _context = outer;
        }
    }

    /// <summary>The context of a creator, made the first time it is asked for: which continuations hold there is known only once every
    /// child's task has been judged in it, as the constructor judges them for the whole scope.</summary>
    /// <param name="creator">The creator, or null for the whole scope.</param>
    private Context ContextOf(string? creator)
    {
        if (creator is null)
            return _global;
        if (_contexts.TryGetValue(creator, out var context))
            return context;

        context = new Context(creator);
        _contexts.Add(creator, context);
        var outer = _context;
        _context = context;
        try
        {
            foreach (var child in _executions.Values.Where(execution => execution.ParentId is not null).Select(execution => execution.Id))
            {
                CheckCancellation();
                IsComposite(child);
            }
        }
        catch
        {
            _contexts.Remove(creator);
            throw;
        }
        finally
        {
            _context = outer;
        }

        return context;
    }

    /// <summary>Whether every path of an execution to an access passes a join operation: only then could that join, proven, order the
    /// access after what it waits for.</summary>
    /// <param name="execution">The execution whose paths are checked.</param>
    /// <param name="joinInstance">The instance making the join.</param>
    /// <param name="joinOperation">The join operation.</param>
    /// <param name="access">The access the paths lead to.</param>
    internal bool JoinDominates(string execution, string joinInstance, int joinOperation, Access access)
    {
        WorkCancellation.Check();
        return FlowOf(execution).Dominates(new PointKey(joinInstance, joinOperation, false), new PointKey(access.InstanceId, access.OperationId, false));
    }

    /// <summary>Whether every work callee of a spawn anchor's site has an async body.</summary>
    /// <param name="anchor">The anchor whose spawn site is checked.</param>
    private bool AllWorkAsync(SpawnAnchor anchor) =>
        _spawnSites.TryGetValue((anchor.CallerInstance, anchor.OperationId), out var site) &&
        site.Callees.Where(callee => callee.Role == SpawnRole.Work)
            .All(callee => _heap.Instances.TryGetValue(callee.InstanceId, out var instance) && _segments.IsAsync(instance.BodyId));

    private void AddHandle(string region, string execution)
    {
        var list = Get(_handles, region);
        if (!list.Contains(execution))
            list.Add(execution);
    }

    /// <summary>Whether an execution runs at most once: in the whole scope by its policy, and in one run of a creator also when it runs
    /// once per run of that creator.</summary>
    /// <param name="execution">The execution.</param>
    private bool Once(string execution)
    {
        CheckCancellation();
        return _executions.TryGetValue(execution, out var instance) && instance.Policy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized } ||
            _context.Creator is { } creator && CreatorRuns.Once(_executions, execution, creator);
    }

    /// <summary>Whether an execution is started once, so its handle names one run: in the whole scope by
    /// <see cref="ExecutionInstance.SpawnedOnce"/>, and in one run of a creator per run of that creator.</summary>
    /// <param name="execution">The execution.</param>
    private bool SpawnedOnce(string execution) =>
        _context.Creator is { } creator ? CreatorRuns.StartedOnce(_executions, execution, creator) : _executions[execution].SpawnedOnce;

    /// <summary>A task whose completion the plan gives no order for: a continuation of anything but one proven spawn's handle, or the task a
    /// non-async <c>Task.Run</c> delegate returned. A join on it proves nothing.</summary>
    /// <param name="child">The child.</param>
    private bool IsComposite(string child)
    {
        CheckCancellation();
        if (_context.Composite.TryGetValue(child, out var cached))
            return cached;
        _context.Composite[child] = false;
        var composite = false;
        // A tail runs the rest of work whose completion the plan gives no order for, so its own completion has none either.
        if (_executions.TryGetValue(child, out var tail) && tail.Origin is { IsTail: true } && tail.ParentId is { } parent)
            composite = IsComposite(parent);
        if (!composite && _anchorOfChild.TryGetValue(child, out var anchor) && anchor.Kind == SpawnAnchorKind.Spawn &&
            _spawnSites.TryGetValue((anchor.CallerInstance, anchor.OperationId), out var site))
        {
            switch (site.Kind)
            {
                case IrSpawnKind.ContinueWith:
                {
                    var caller = _heap.Instances[anchor.CallerInstance];
                    var receiver = caller.Summary.Spawns.FirstOrDefault(spawn => spawn.OperationId == anchor.OperationId)?.Antecedent;
                    var antecedent = receiver is null ? null : ProveHandle(anchor.ExecutionId, caller, receiver, anchor.OperationId);
                    composite = antecedent is null;
                    if (!composite && Once(anchor.ExecutionId))
                        Get(_context.Continuations, antecedent!).Add(child);
                    break;
                }
                case IrSpawnKind.TaskRun:
                    composite = site.Callees.Any(callee => _heap.Instances.TryGetValue(callee.InstanceId, out var instance) &&
                                                           _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) &&
                                                           !body.IsAsync && IsTaskType(body.ReturnType));
                    break;
                case IrSpawnKind.Unrecognized:
                    composite = true;
                    break;
            }
        }

        _context.Composite[child] = composite;
        return composite;
    }

    private static bool IsTaskType(string type) =>
        type is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" ||
        type.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
        type.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal);

    /// <summary>The one execution of the joining execution's instance tree a handle region names, or null.</summary>
    /// <param name="joining">The joining execution.</param>
    /// <param name="region">The handle region.</param>
    private string? HandleExecution(string joining, string region)
    {
        if (!_handles.TryGetValue(region, out var candidates) || !_executions.TryGetValue(joining, out var execution))
            return null;
        var inTree = candidates.Where(candidate => _executions[candidate].TreeRootId == execution.TreeRootId).ToArray();
        return inTree.Length == 1 ? inTree[0] : null;
    }

    // ---- inter-execution search ----

    private enum EventKind
    {
        Point,
        Start,
        End,
        Join
    }

    /// <summary>An event of the search; a join node remembers the execution whose end reached it.</summary>
    /// <param name="Kind">The kind of event.</param>
    /// <param name="Execution">The execution the event belongs to.</param>
    /// <param name="Join">The index of the join among the execution's joins, or -1 for other events.</param>
    /// <param name="Joined">The execution whose end reached a join node; null for other events.</param>
    private sealed record EventNode(EventKind Kind, string Execution, int Join, string? Joined = null);

    private bool Path(Access from, Access to)
    {
        if (!ExecutionReach(from.ExecutionId).Contains(to.ExecutionId))
            return false;

        var source = new PointKey(from.InstanceId, from.OperationId, false);
        var target = new PointKey(to.InstanceId, to.OperationId, false);
        var seen = new HashSet<EventNode>();
        var pending = new Queue<EventNode>([new EventNode(EventKind.Point, from.ExecutionId, -1)]);
        while (pending.TryDequeue(out var node))
        {
            CheckCancellation();
            if (!seen.Add(node))
                continue;

            var execution = node.Execution;
            switch (node.Kind)
            {
                case EventKind.Point:
                    foreach (var anchor in IsEnumerated(execution, from.InstanceId) ? Enumerable.Empty<SpawnAnchor>() : TrustedAnchors(execution))
                    {
                        CheckCancellation();
                        if (Precedes(execution, source, AnchorPoint(anchor)))
                            pending.Enqueue(new EventNode(EventKind.Start, anchor.ChildId, -1));
                    }

                    pending.Enqueue(new EventNode(EventKind.End, execution, -1));
                    break;
                case EventKind.Start:
                    if (execution == to.ExecutionId)
                        return true;
                    foreach (var anchor in TrustedAnchors(execution))
                    {
                        CheckCancellation();
                        pending.Enqueue(new EventNode(EventKind.Start, anchor.ChildId, -1));
                    }
                    pending.Enqueue(new EventNode(EventKind.End, execution, -1));
                    break;
                case EventKind.End:
                    foreach (var next in EndEdges(execution))
                    {
                        CheckCancellation();
                        pending.Enqueue(next);
                    }
                    break;
                case EventKind.Join:
                {
                    var join = Joins(execution)[node.Join];
                    if (execution == to.ExecutionId && !IsEnumerated(execution, to.InstanceId) && Dominates(execution, node.Joined!, target))
                        return true;
                    foreach (var anchor in TrustedAnchors(execution))
                    {
                        CheckCancellation();
                        if (Dominates(execution, node.Joined!, AnchorPoint(anchor)))
                            pending.Enqueue(new EventNode(EventKind.Start, anchor.ChildId, -1));
                    }

                    if (BoundsEnd(execution, join, node.Joined!))
                        pending.Enqueue(new EventNode(EventKind.End, execution, -1));
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>Whether an instance runs inside an async iterator the execution calls: the iterator's body runs when it is enumerated,
    /// not when it is called, so its points are ordered with none of the execution's spawns and joins.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="instance">The instance.</param>
    private bool IsEnumerated(string execution, string instance)
    {
        if (!_enumerated.TryGetValue(execution, out var enumerated))
        {
            enumerated = new HashSet<string>(StringComparer.Ordinal);
            var steps = _steps.GetValueOrDefault(execution) ?? [];
            var pending = new Stack<string>(steps.Select(step => step.Callee).Where(IsAsyncIterator));
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                if (!enumerated.Add(current))
                    continue;
                foreach (var step in steps.Where(step => step.Caller == current))
                {
                    CheckCancellation();
                    pending.Push(step.Callee);
                }
            }

            _enumerated.Add(execution, enumerated);
        }

        return enumerated.Contains(instance);
    }

    private bool IsAsyncIterator(string instance) =>
        _heap.Instances.TryGetValue(instance, out var state) && _scope.Reachable.Bodies.TryGetValue(state.BodyId, out var body) && body.IsAsyncIterator;

    /// <summary>Whether a joined execution ends before the joining one: the join runs on every path of the joining execution from where
    /// it starts the joined work (from its own start, when the work comes from elsewhere) to its normal end. An exception that leaves the
    /// joining execution is no path to that end here, so the end carries the joined work's completion only on a normal return; a blocking
    /// wait for the joining execution reads its exceptional exit as that completion only where no exception leaves around the join
    /// (<see cref="LeavesWorkRunning"/>).</summary>
    /// <param name="joining">The joining.</param>
    /// <param name="join">The join.</param>
    /// <param name="joined">The joined.</param>
    private bool BoundsEnd(string joining, JoinAnchor join, string joined) => FlowOf(joining).AlwaysFollows(JoinedFrom(joining, joined), [join.Point]);

    /// <summary>Where the joining execution starts the joined work: the anchor of the joined execution's ancestor the joining one spawns,
    /// or null, for its own start, when the work comes from elsewhere.</summary>
    /// <param name="joining">The joining.</param>
    /// <param name="joined">The joined.</param>
    private PointKey? JoinedFrom(string joining, string joined)
    {
        PointKey? from = null;
        for (var current = joined; _executions.TryGetValue(current, out var execution) && execution.ParentId is { } parent; current = parent)
        {
            CheckCancellation();
            if (parent != joining)
                continue;
            if (_anchorOfChild.TryGetValue(current, out var anchor) && anchor.ExecutionId == joining)
                from = AnchorPoint(anchor);
            break;
        }

        return from;
    }

    /// <summary>The anchors whose spawn edge holds for every instance of the child: the spawning execution runs at most once, and a timer's
    /// creation site runs once in it.</summary>
    /// <param name="execution">The spawning execution.</param>
    private IEnumerable<SpawnAnchor> TrustedAnchors(string execution) =>
        Once(execution)
            ? (_anchors.GetValueOrDefault(execution) ?? []).Where(anchor => anchor.Kind != SpawnAnchorKind.Timer || SpawnedOnce(anchor.ChildId))
            : [];

    /// <summary>A spawn's position in its parent. An admitted sequential-awaited call starts its continuations before the call's
    /// repeated delegate prefixes have all returned, so no path orders a later run's prefix after an earlier run's continuation; only the
    /// completion proof of the queried passes does, where the call's task is awaited at once in a body entered through one async call
    /// whose pass completes before its next start. Any other async return, including a sequential-awaited call outside the admission,
    /// starts its continuation after the call returns, so a path already orders it after the synchronous part of every run.</summary>
    /// <param name="anchor">The spawn anchor.</param>
    private PointKey AnchorPoint(SpawnAnchor anchor) =>
        new(anchor.CallerInstance, anchor.OperationId, anchor.Kind == SpawnAnchorKind.AsyncReturn &&
            !_awaitedCalls.Any(call => call.Instance == anchor.CallerInstance && call.Operation == anchor.OperationId && call.Execution == anchor.ExecutionId));

    /// <summary>What follows an execution's end: its trusted joins, its trusted continuations, the tail of its async work and, for startup,
    /// the start of every execution that cannot start before startup ends.</summary>
    /// <param name="execution">The execution.</param>
    private IEnumerable<EventNode> EndEdges(string execution)
    {
        EnsureJoins();
        foreach (var (joining, join) in _context.JoinedBy.GetValueOrDefault(execution) ?? [])
        {
            CheckCancellation();
            yield return new EventNode(EventKind.Join, joining, join, execution);
        }
        foreach (var continuation in _context.Continuations.GetValueOrDefault(execution) ?? [])
        {
            CheckCancellation();
            yield return new EventNode(EventKind.Start, continuation, -1);
        }
        if (_tails.TryGetValue(execution, out var tail) && Once(execution))
            yield return new EventNode(EventKind.Start, tail, -1);
        if (execution == ExecutionModel.STARTUP)
        {
            foreach (var other in _executions.Values.Where(other => !_duringStartup.Contains(other.Id)))
            {
                CheckCancellation();
                yield return new EventNode(EventKind.Start, other.Id, -1);
            }
        }
    }

    /// <summary>The executions an execution's trusted edges lead to, transitively: a cheap filter before the point-level search.</summary>
    /// <param name="start">The start.</param>
    private HashSet<string> ExecutionReach(string start)
    {
        if (_context.ExecutionReach.TryGetValue(start, out var cached))
            return cached;

        var reach = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([start]);
        while (pending.TryPop(out var execution))
        {
            CheckCancellation();
            var next = TrustedAnchors(execution).Select(anchor => anchor.ChildId)
                                                .Concat(EndEdges(execution).Select(edge => edge.Execution));
            foreach (var target in next)
            {
                CheckCancellation();
                if (reach.Add(target))
                    pending.Push(target);
            }
        }

        _context.ExecutionReach.Add(start, reach);
        return reach;
    }

    // ---- joins ----

    private sealed record JoinAnchor(int Index, PointKey Point, IReadOnlyList<string> Targets, bool ThrowsOnlyAfterCompletion);

    private IReadOnlyList<JoinAnchor> Joins(string execution)
    {
        EnsureJoins();
        return _context.Joins.GetValueOrDefault(execution) ?? [];
    }

    /// <summary>Proves the joins of every execution once: the handles each waits for, the unproven ones counted per operation, and the
    /// executions each proven join follows.</summary>
    private void EnsureJoins()
    {
        if (_context.JoinsReady)
            return;
        _context.JoinsReady = true;

        try
        {
            var candidates = new List<Candidate>();
            var blocking = new HashSet<(string Execution, PointKey Point)>();
            foreach (var execution in _executions.Keys.Order(StringComparer.Ordinal))
            {
                CheckCancellation();
                var resumed = new Dictionary<(string Instance, int Operation), IReadOnlyList<string>>();
                foreach (var visit in _visits.GetValueOrDefault(execution) ?? [])
                {
                    CheckCancellation();
                    if (!_heap.Instances.TryGetValue(visit.InstanceId, out var instance))
                        continue;
                    // A timer wait is the await or WaitOne the pattern ends with: its own join says nothing, since what it waits for is a
                    // ValueTask or an event, so the timer's identity decides it and the operation is counted once, here.
                    var timerWaits = TimerWaits(execution, visit, instance).ToArray();
                    foreach (var (operationId, target, throwsAfter) in timerWaits)
                    {
                        CheckCancellation();
                        if (target is null)
                            _context.UnprovenJoins.Add((instance.BodyId, operationId));
                        else
                            Wait(candidates, resumed, execution, visit, instance, operationId, [target], throwsAfter);
                    }

                    foreach (var join in instance.Summary.Joins)
                    {
                        CheckCancellation();
                        if (!_segments.Runs(instance.BodyId, visit.Segment, join.OperationId) ||
                            timerWaits.Any(wait => wait.OperationId == join.OperationId))
                        {
                            continue;
                        }

                        var (targets, proven) = Prove(execution, instance, join);
                        if (!proven)
                            _context.UnprovenJoins.Add((instance.BodyId, join.OperationId));
                        if (targets.Count == 0)
                            continue;
                        var throwsAfter = ThrowsAfterCompletion(execution, instance.Id, join, proven);
                        if (throwsAfter && join.Kind != SummaryJoinKind.Await)
                            blocking.Add((execution, new PointKey(instance.Id, join.OperationId, false)));
                        Wait(candidates, resumed, execution, visit, instance, join.OperationId, targets, throwsAfter);
                    }
                }

                AtResumptions(candidates, execution, resumed);
                foreach (var call in _awaitedCalls.Where(call => call.Execution == execution && call.Task is null))
                {
                    CheckCancellation();
                    candidates.Add(new Candidate(execution, new PointKey(call.Instance, call.Operation, false), call.Continuations, true));
                }
                if (Once(execution))
                {
                    foreach (var anchor in _anchors.GetValueOrDefault(execution) ?? [])
                    {
                        CheckCancellation();
                        if (anchor.Kind == SpawnAnchorKind.Spawn &&
                            _spawnSites.TryGetValue((anchor.CallerInstance, anchor.OperationId), out var site) &&
                            site.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach)
                        {
                            candidates.Add(new Candidate(execution, new PointKey(anchor.CallerInstance, site.CallOperationId, false), [anchor.ChildId], true));
                        }
                    }
                }
            }

            Anchor(candidates);
            if (WithoutRunningWork(candidates, blocking) is { } completing)
            {
                _context.Joins.Clear();
                _context.JoinedBy.Clear();
                _context.Flows.Clear();
                Anchor(completing);
            }
        }
        catch
        {
            _context.JoinsReady = false;
            _context.Joins.Clear();
            _context.JoinedBy.Clear();
            _context.UnprovenJoins.Clear();
            _context.Flows.Clear();
            throw;
        }
    }

    /// <summary>Makes the join anchors of the waits given and of the call sites they are hoisted to, one per point.</summary>
    /// <param name="waits">The waits, before hoisting.</param>
    private void Anchor(IReadOnlyList<Candidate> waits)
    {
        var candidates = waits.ToList();
        AtCallSites(candidates);
        foreach (var group in candidates.GroupBy(candidate => (candidate.Execution, candidate.Point)))
        {
            CheckCancellation();
            var joins = _context.Joins.TryGetValue(group.Key.Execution, out var existing) ? (List<JoinAnchor>)existing : [];
            var targets = group.SelectMany(candidate => candidate.Targets).Distinct(StringComparer.Ordinal).ToArray();
            // One point read as several waits throws only after completion where every reading of it says so.
            var join = new JoinAnchor(joins.Count, group.Key.Point, targets, group.All(candidate => candidate.ThrowsAfter));
            joins.Add(join);
            _context.Joins[group.Key.Execution] = joins;
            foreach (var target in targets.Where(target => target != group.Key.Execution))
            {
                CheckCancellation();
                Get(_context.JoinedBy, target).Add((group.Key.Execution, join.Index));
            }
        }
    }

    /// <summary>The waits with a blocking wait's exceptional exit read as the completion of its tasks only where it is also the completion
    /// of the work their ends carry: the fault of a task comes after the task completed, not after the work it started, so a blocking wait
    /// for a task that may end with an exception while such work runs (<see cref="LeavesWorkRunning"/>) throws before that work completes.
    /// An <c>await</c> is read as it always has been. Null when no wait changes.</summary>
    /// <param name="waits">The waits, before hoisting, read from the anchors already made.</param>
    /// <param name="blocking">The blocking waits for tasks whose exceptional exit is read as their tasks' completion, by execution and
    /// point.</param>
    private List<Candidate>? WithoutRunningWork(IReadOnlyList<Candidate> waits, IReadOnlySet<(string Execution, PointKey Point)> blocking)
    {
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        var changed = false;
        var result = new List<Candidate>(waits.Count);
        foreach (var wait in waits)
        {
            CheckCancellation();
            var running = wait.ThrowsAfter && blocking.Contains((wait.Execution, wait.Point)) &&
                          wait.Targets.Any(target => target != wait.Execution && LeavesWorkRunning(target, known));
            result.Add(running ? wait with { ThrowsAfter = false } : wait);
            changed |= running;
        }

        return changed ? result : null;
    }

    /// <summary>Whether an execution may end with an exception while work whose completion its end carries still runs: a join of the
    /// execution bounds its end on every normal path (<see cref="BoundsEnd"/>) but an exception leaves the execution around it, or the
    /// joined execution's own end carries such work in turn. An execution reached again through its own joins is taken to leave work
    /// running.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="known">The answers already found, by execution.</param>
    private bool LeavesWorkRunning(string execution, Dictionary<string, bool> known)
    {
        CheckCancellation();
        if (known.TryGetValue(execution, out var leaves))
            return leaves;
        known.Add(execution, true);
        foreach (var join in Joins(execution))
        {
            CheckCancellation();
            foreach (var joined in join.Targets.Where(target => target != execution))
            {
                CheckCancellation();
                var from = JoinedFrom(execution, joined);
                var flow = FlowOf(execution);
                if (flow.AlwaysFollows(from, [join.Point]) &&
                    (!flow.AlwaysFollows(from, [join.Point], exceptionalExits: true) || LeavesWorkRunning(joined, known)))
                {
                    leaves = true;
                    break;
                }
            }

            if (leaves)
                break;
        }

        known[execution] = leaves;
        return leaves;
    }

    private sealed record Candidate(string Execution, PointKey Point, IReadOnlyList<string> Targets, bool ThrowsAfter);

    /// <summary>Where a wait orders what it waits for: at the operation itself, or, for the <c>await</c> a prefix stops at, at the start of
    /// the tails that continue after it, since the prefix has no point past that <c>await</c>; those are kept until every such
    /// <c>await</c> is known.</summary>
    /// <param name="candidates">The join candidates the wait is added to when it orders at the operation.</param>
    /// <param name="resumed">The targets kept per prefix <c>await</c>, to be ordered at the start of its tails.</param>
    /// <param name="execution">The waiting execution.</param>
    /// <param name="visit">The visit the wait runs in.</param>
    /// <param name="instance">The instance making the wait.</param>
    /// <param name="operationId">The wait operation.</param>
    /// <param name="targets">The executions it waits for.</param>
    /// <param name="throwsAfter">Whether the wait throws only after the work completed.</param>
    private void Wait(List<Candidate> candidates, Dictionary<(string Instance, int Operation), IReadOnlyList<string>> resumed, string execution,
                      ExecutionVisit visit, MethodInstance instance, int operationId, IReadOnlyList<string> targets, bool throwsAfter)
    {
        if (visit.Segment != BodySegment.Prefix || !IsAwait(instance.BodyId, operationId))
        {
            candidates.Add(new Candidate(execution, new PointKey(instance.Id, operationId, false), targets, throwsAfter));
            return;
        }

        resumed[(instance.Id, operationId)] = resumed.TryGetValue((instance.Id, operationId), out var known)
            ? [.. known, .. targets.Where(target => !known.Contains(target))]
            : targets;
    }

    /// <summary>What a tail may rely on at its start: it resumes after one of the <c>await</c>s the prefixes it continues stopped at, and
    /// which of them is not known, so only the work every one of those suspensions waits for has completed by then.</summary>
    /// <param name="candidates">The candidates.</param>
    /// <param name="execution">The execution.</param>
    /// <param name="resumed">The resumed.</param>
    private void AtResumptions(List<Candidate> candidates, string execution,
                               IReadOnlyDictionary<(string Instance, int Operation), IReadOnlyList<string>> resumed)
    {
        foreach (var tail in _executions.Values.Where(other => other.ParentId == execution))
        {
            CheckCancellation();
            var entries = (_entries.GetValueOrDefault(tail.Id) ?? []).Where(entry => entry.Segment == BodySegment.Tail).ToArray();
            var continued = entries.Select(entry => entry.InstanceId).ToHashSet(StringComparer.Ordinal);
            var targets = (IReadOnlyList<string>?)null;
            foreach (var entry in entries)
            {
                CheckCancellation();
                if (!_heap.Instances.TryGetValue(entry.InstanceId, out var instance))
                    continue;

                foreach (var operationId in Suspensions(execution, instance, continued))
                {
                    CheckCancellation();
                    var waited = resumed.GetValueOrDefault((instance.Id, operationId)) ?? [];
                    targets = targets is null ? waited : targets.Where(waited.Contains).ToArray();
                }
            }

            if (targets is { Count: > 0 })
                candidates.Add(new Candidate(tail.Id, PointKey.Entry, targets, true));
        }
    }

    /// <summary>The <c>await</c>s of a prefix that suspend on work of their own: awaiting a call whose callee continues in the same tail is
    /// that callee's suspension seen from outside, and says nothing its own <c>await</c> does not.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="continued">The continued.</param>
    private IReadOnlyList<int> Suspensions(string execution, MethodInstance instance, IReadOnlySet<string> continued)
    {
        var awaits = PrefixAwaits(instance);
        if (awaits.Count == 0 || !_scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
            return awaits;

        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var sources = operations.OfType<IrAssignOperation>().ToDictionary(assign => assign.TargetValue, assign => assign.SourceValue);
        var calls = operations.OfType<IrCallOperation>().Where(call => call.ResultValue is not null)
                              .ToDictionary(call => call.ResultValue!.Value, call => call.Id);
        var continuing = (_steps.GetValueOrDefault(execution) ?? [])
                         .Where(step => step.Caller == instance.Id && continued.Contains(step.Callee))
                         .Select(step => step.OperationId)
                         .ToHashSet();

        var suspensions = new List<int>();
        foreach (var operationId in awaits)
        {
            CheckCancellation();
            var awaited = operations.OfType<IrAwaitOperation>().First(operation => operation.Id == operationId);
            var value = awaited.TaskValue ?? awaited.AwaitableValue;
            while (sources.TryGetValue(value, out var source))
            {
                CheckCancellation();
                value = source;
            }
            if (!calls.TryGetValue(value, out var call) || !continuing.Contains(call))
                suspensions.Add(operationId);
        }

        return suspensions;
    }

    /// <summary>The <c>await</c> operations a body's prefix may stop at.</summary>
    /// <param name="instance">The instance whose body is read.</param>
    private IReadOnlyList<int> PrefixAwaits(MethodInstance instance) =>
        _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body)
            ? body.Blocks.SelectMany(block => block.Operations)
                  .OfType<IrAwaitOperation>()
                  .Where(operation => _segments.Runs(instance.BodyId, BodySegment.Prefix, operation.Id))
                  .Select(operation => operation.Id)
                  .ToArray()
            : [];

    private bool IsAwait(string bodyId, int operationId) =>
        _scope.Reachable.Bodies.TryGetValue(bodyId, out var body) &&
        body.Blocks.SelectMany(block => block.Operations).Any(operation => operation.Id == operationId && operation is IrAwaitOperation);

    /// <summary>A call is the wait its callees make, but only where the call cannot go around it: every callee it may dispatch to has to
    /// wait on every path from its entry to any exit, exceptional ones included, so the targets are the ones all of them wait for, not the
    /// ones any of them does. The call keeps its exceptional edge unless every wait behind it throws after the work completed and no
    /// exception leaves a callee before its wait — not even from computing the handle it waits on, nor from an earlier wait for something
    /// else, which skips this one — since either leaves the work unfinished on that path. A hoisted wait is a wait of its own site, so
    /// calls of calls follow.
    /// </summary>
    /// <param name="candidates">The candidates.</param>
    private void AtCallSites(List<Candidate> candidates) => candidates.AddRange(Hoisted(candidates, _steps.Keys));

    /// <summary>The waits <see cref="AtCallSites"/> hoists from the candidates to the sites calling them, in the executions given.</summary>
    /// <param name="candidates">The waits.</param>
    /// <param name="executions">The executions whose call sites are read.</param>
    private IReadOnlyList<Candidate> Hoisted(IReadOnlyList<Candidate> candidates, IEnumerable<string> executions)
    {
        var hoisted = new Dictionary<(string Execution, string Caller, int Operation), Candidate>();
        var read = executions.Where(_steps.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        var changed = true;
        while (changed)
        {
            CheckCancellation();
            changed = false;
            foreach (var execution in read)
            {
                CheckCancellation();
                var flow = PlainFlow(execution);
                var waits = candidates.Concat(hoisted.Values)
                                      .Where(candidate => candidate.Execution == execution && candidate.Point.Operation >= 0 &&
                                                          !candidate.Point.AfterCall)
                                      .GroupBy(candidate => candidate.Point.Instance, StringComparer.Ordinal)
                                      .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var site in _steps[execution].GroupBy(step => (step.Caller, step.OperationId)))
                {
                    CheckCancellation();
                    if (_heap.UnresolvedCallTargets.Contains((site.Key.Caller, site.Key.OperationId)))
                        continue;

                    var targets = (IReadOnlyList<string>?)null;
                    var throwsAfter = true;
                    foreach (var step in site)
                    {
                        CheckCancellation();
                        var made = waits.GetValueOrDefault(step.Callee) ?? [];
                        var waited = new List<string>();
                        foreach (var target in made.SelectMany(candidate => candidate.Targets).Distinct(StringComparer.Ordinal))
                        {
                            CheckCancellation();
                            var alternatives = made.Where(candidate => candidate.Targets.Contains(target)).ToArray();
                            var points = alternatives.Select(candidate => candidate.Point).ToArray();
                            if (!flow.AlwaysWaits(step.Callee, step.CalleeSegment, points, exceptional: false))
                                continue;
                            waited.Add(target);
                            throwsAfter &= alternatives.All(candidate => candidate.ThrowsAfter) &&
                                           flow.AlwaysWaits(step.Callee, step.CalleeSegment, points, exceptional: true);
                        }

                        targets = targets is null ? waited : targets.Where(waited.Contains).ToArray();
                    }

                    if (targets is null || targets.Count == 0)
                        continue;

                    var key = (execution, site.Key.Caller, site.Key.OperationId);
                    if (hoisted.TryGetValue(key, out var known) && known.ThrowsAfter == throwsAfter &&
                        known.Targets.SequenceEqual(targets, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    hoisted[key] = new Candidate(execution, new PointKey(site.Key.Caller, site.Key.OperationId, false), targets, throwsAfter);
                    changed = true;
                }
            }
        }

        return hoisted.Values.ToArray();
    }

    /// <summary>Whether an exception out of a wait comes only after every task it waits for completed, so that a path through a handler
    /// after it is ordered as its normal return is (R3, R4; ADR 0030). An await resumes only after its task completed, whatever the thread
    /// context. A blocking wait does when its form does — <c>Wait()</c>, the <c>Result</c> or <c>GetResult()</c> of a <c>Task</c> or a
    /// <c>Task&lt;T&gt;</c>, <c>Task.WaitAll(tasks)</c> — and, under the thread-context assumption, when every task it waits for is
    /// proven, no reachable call of the process scope installs another context and no task it waits for runs on a scheduler the program
    /// names: a task not proven may run on any. Every other wait may throw before its work completes. A blocking wait so read rests on
    /// the assumptions <see cref="Assumption"/> gives, which a graph that withholds them reads as ordering nothing (R5).</summary>
    /// <param name="execution">The execution making the wait.</param>
    /// <param name="instanceId">The instance making the wait.</param>
    /// <param name="join">The wait.</param>
    /// <param name="proven">Whether every task the wait waits for is proven.</param>
    private bool ThrowsAfterCompletion(string execution, string instanceId, SummaryJoin join, bool proven)
    {
        CheckCancellation();
        return ThrowsAfterCompletion(execution, join, instanceId, join.Handles, proven);
    }

    /// <summary>What <see cref="ThrowsAfterCompletion(string, string, SummaryJoin, bool)"/> answers for a wait, with the tasks it waits
    /// for read from the handles given: its own, or for a wait on a gap's result, the handle the gap presumably carries.</summary>
    /// <param name="execution">The execution making the wait.</param>
    /// <param name="join">The wait.</param>
    /// <param name="handleInstance">The instance expressing the handles.</param>
    /// <param name="handles">The handles whose tasks the wait waits for.</param>
    /// <param name="proven">Whether every task the wait waits for is proven.</param>
    private bool ThrowsAfterCompletion(string execution, SummaryJoin join, string handleInstance, IEnumerable<SummaryValue> handles, bool proven)
    {
        if (!join.ThrowsOnlyAfterCompletion || join.Kind == SummaryJoinKind.Await)
            return join.ThrowsOnlyAfterCompletion;
        _contextFacts ??= ThreadContextFacts.Of(_scope);
        var waited = WaitedTasks(handleInstance, handles);
        if (!proven || _contextFacts.InstallsContext ||
            !waited.All(task => (SpawnsOfHandle().GetValueOrDefault(task) ?? []).All(site => _contextFacts.Scheduling(site) != TaskScheduling.Program)))
            return false;
        var assumption = Assumption(execution, waited);
        if (_withheld?.Contains(assumption) == true)
            return false;
        _assumptions.Add(assumption);
        return true;
    }

    /// <summary>The assumptions reading a blocking wait's exceptional exit as its tasks' completion rests on (R5, D-0018): the thread-pool
    /// entry when the waiting execution runs as thread-pool work, else its thread context; the thread context as well when a waited task
    /// takes the scheduler of the thread that starts it; and always the observers.</summary>
    /// <param name="execution">The execution making the wait.</param>
    /// <param name="waited">The tasks the wait waits for.</param>
    private OrderingAssumption Assumption(string execution, IReadOnlySet<string> waited)
    {
        var assumption = (OnThreadPool(execution) ? OrderingAssumption.ThreadPoolEntry : OrderingAssumption.ThreadContext) | OrderingAssumption.Observers;
        return waited.Any(task => (SpawnsOfHandle().GetValueOrDefault(task) ?? []).Any(site => _contextFacts!.Scheduling(site) == TaskScheduling.Thread))
            ? assumption | OrderingAssumption.ThreadContext
            : assumption;
    }

    /// <summary>Whether an execution runs as thread-pool work (D-0018): the work of a <c>Task.Run</c>, a <c>QueueUserWorkItem</c> or an
    /// <c>UnsafeQueueUserWorkItem</c>, of a <c>StartNew</c> whose scheduler argument is a read of <c>TaskScheduler.Default</c>, a
    /// <c>Parallel</c> body, a timer's callback, or a request of a <c>controller-action</c>, <c>minimal-api</c> or <c>grpc-method</c> root.
    /// The tail of async work runs where the execution whose prefix it continues does; every other execution runs on its thread.</summary>
    /// <param name="execution">The execution.</param>
    private bool OnThreadPool(string execution)
    {
        for (var current = execution; _executions.TryGetValue(current, out var instance);)
        {
            CheckCancellation();
            var anchor = _anchorOfChild.GetValueOrDefault(current);
            if (instance.Origin is { IsTail: true } || anchor?.Kind == SpawnAnchorKind.AsyncReturn)
            {
                if (instance.ParentId is not { } parent)
                    return false;
                current = parent;
                continue;
            }
            if (instance.Kind == ExecutionKind.TimerCallback)
                return true;
            if (instance.Kind == ExecutionKind.Root)
                return _scope.Roots.FirstOrDefault(root => root.StableRootId == instance.RootId)?.RootKind is "controller-action" or "minimal-api" or "grpc-method";
            return anchor is { Kind: SpawnAnchorKind.Spawn } && _spawnSites.TryGetValue((anchor.CallerInstance, anchor.OperationId), out var site) &&
                   (site.Kind is IrSpawnKind.TaskRun or IrSpawnKind.QueueUserWorkItem or IrSpawnKind.UnsafeQueueUserWorkItem or IrSpawnKind.ParallelFor
                                 or IrSpawnKind.ParallelForEach or IrSpawnKind.ParallelForEachAsync ||
                    site.Kind == IrSpawnKind.StartNew && site.SchedulerArgument == IrSchedulerArgument.Default);
        }

        return false;
    }

    /// <summary>The task regions a wait waits for: those its handles point to, and the members of each group among them.</summary>
    /// <param name="instanceId">The instance expressing the handles.</param>
    /// <param name="handles">The handles of the wait.</param>
    private HashSet<string> WaitedTasks(string instanceId, IEnumerable<SummaryValue> handles)
    {
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(handles.SelectMany(handle => handle.Values).SelectMany(value => _heap.Resolve(instanceId, value)));
        while (pending.TryPop(out var task))
        {
            CheckCancellation();
            if (!tasks.Add(task) || !_heap.TaskGroups.TryGetValue(task, out var group))
                continue;
            foreach (var member in group.Members)
            {
                CheckCancellation();
                pending.Push(member);
            }
        }

        return tasks;
    }

    /// <summary>The spawns whose handle is each region.</summary>
    private Dictionary<string, List<SpawnSite>> SpawnsOfHandle()
    {
        if (_spawnsOfHandle is not null)
            return _spawnsOfHandle;
        var spawns = new Dictionary<string, List<SpawnSite>>(StringComparer.Ordinal);
        foreach (var site in _heap.Spawns)
        {
            CheckCancellation();
            foreach (var handle in site.Handles)
            {
                CheckCancellation();
                Get(spawns, handle).Add(site);
            }
        }

        return _spawnsOfHandle = spawns;
    }

    /// <summary>The executions a join waits for with proven identity, and whether every handle it waits for is proven (R8: a handle the
    /// analysis cannot name is not proven, whether or not it names a spawn beside it).</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="join">The join.</param>
    private (List<string> Targets, bool Proven) Prove(string execution, MethodInstance instance, SummaryJoin join)
    {
        CheckCancellation();
        var targets = new List<string>();
        var proven = join.HandlesKnown;
        foreach (var handle in join.Handles)
        {
            CheckCancellation();
            var regions = handle.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
            if (regions.Length == 1 && _heap.TaskGroups.TryGetValue(regions[0], out var group))
            {
                var members = GroupMembers(execution, regions[0]);
                if (!group.MembersKnown || members is null || !SourcesExcused(execution, instance, handle, regions[0], join.OperationId))
                {
                    proven = false;
                    continue;
                }

                foreach (var (memberInstance, member) in members)
                {
                    CheckCancellation();
                    if (ProveCallTask(execution, memberInstance, member, join.OperationId) is { } continuations)
                        targets.AddRange(continuations);
                    else if (ProveHandle(execution, memberInstance, member, join.OperationId) is { } target)
                        targets.Add(target);
                    else
                        proven = false;
                }

                continue;
            }

            if (ProveCallTask(execution, instance, handle, join.OperationId) is { } runs)
                targets.AddRange(runs);
            else if (ProveHandle(execution, instance, handle, join.OperationId) is { } single)
                targets.Add(single);
            else
                proven = false;
        }

        return (targets, proven);
    }

    /// <summary>
    /// The waits for every callback of a timer that a visit runs: the point after <c>await timer.DisposeAsync()</c>, and each
    /// <c>WaitOne()</c> that on every path follows <c>timer.Dispose(handle)</c> on a handle only that call signals (see
    /// <see cref="IsQuietEvent"/>). The target is the callback execution the wait waits for, or null when the timer's identity is not
    /// proven and the wait orders nothing.
    /// </summary>
    /// <param name="execution">The execution.</param>
    /// <param name="visit">The visit.</param>
    /// <param name="instance">The instance.</param>
    private IEnumerable<(int OperationId, string? Target, bool ThrowsAfter)> TimerWaits(string execution, ExecutionVisit visit, MethodInstance instance)
    {
        foreach (var timer in instance.Summary.Timers)
        {
            CheckCancellation();
            if (timer.Action is not (IrTimerAction.DisposeAsync or IrTimerAction.DisposeWaitHandle) ||
                !_segments.Runs(instance.BodyId, visit.Segment, timer.OperationId))
            {
                continue;
            }

            var waits = timer.Action == IrTimerAction.DisposeAsync ? DisposeAwaits(visit, instance, timer) : HandleWaits(execution, instance, timer);
            if (waits.Count == 0)
                continue;

            var target = ProveHandle(execution, instance, timer.Timer, timer.OperationId);
            foreach (var wait in waits)
            {
                CheckCancellation();
                yield return (wait, target, timer.Action == IrTimerAction.DisposeAsync);
            }
        }
    }

    /// <summary>The await of a <c>DisposeAsync()</c> result in the same visit, directly, through assignments, or through the
    /// <c>ConfigureAwait</c> the await keeps its task behind.</summary>
    /// <param name="visit">The visit whose segment the await must run in.</param>
    /// <param name="instance">The instance making the dispose.</param>
    /// <param name="timer">The <c>DisposeAsync()</c> timer step.</param>
    private List<int> DisposeAwaits(ExecutionVisit visit, MethodInstance instance, SummaryTimer timer)
    {
        var body = _scope.Reachable.Bodies[instance.BodyId];
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var result = operations.OfType<IrTimerOperation>().Single(operation => operation.Id == timer.OperationId).ResultValue;
        var sources = operations.OfType<IrAssignOperation>().ToDictionary(assign => assign.TargetValue, assign => assign.SourceValue);
        return operations.OfType<IrAwaitOperation>()
                         .Where(await => _segments.Runs(instance.BodyId, visit.Segment, await.Id) &&
                                         Origin(await.TaskValue ?? await.AwaitableValue, sources) == result)
                         .Select(await => await.Id)
                         .ToList();
    }

    private int Origin(int value, IReadOnlyDictionary<int, int> sources)
    {
        for (var steps = 0; steps < sources.Count && sources.TryGetValue(value, out var source); steps++)
        {
            CheckCancellation();
            value = source;
        }
        return value;
    }

    /// <summary>The <c>WaitOne()</c> calls of the execution on the one quiet handle <c>timer.Dispose(handle)</c> passes, when every path after
    /// the dispose reaches one of them.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="timer">The timer.</param>
    private List<int> HandleWaits(string execution, MethodInstance instance, SummaryTimer timer) =>
        HandleWaitPoints(execution, instance, timer).Select(point => point.Operation).ToList();

    /// <summary>The points of the <c>WaitOne()</c> calls <see cref="HandleWaits"/> finds, in the instance that makes each.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="timer">The timer.</param>
    private List<PointKey> HandleWaitPoints(string execution, MethodInstance instance, SummaryTimer timer)
    {
        if (timer.WaitHandle is not { UnknownSources.Count: 0 } handle || Regions(instance, handle) is not [var region] || !IsQuietEvent(region))
            return [];

        var waits = new List<PointKey>();
        foreach (var visit in _visits.GetValueOrDefault(execution) ?? [])
        {
            CheckCancellation();
            if (!_heap.Instances.TryGetValue(visit.InstanceId, out var waiter))
                continue;
            foreach (var join in waiter.Summary.Joins.Where(join => join.Kind == SummaryJoinKind.WaitOne))
            {
                CheckCancellation();
                if (_segments.Runs(waiter.BodyId, visit.Segment, join.OperationId) &&
                    join.Handles is [{ UnknownSources.Count: 0 } waited] && Regions(waiter, waited) is [var other] && other == region)
                {
                    waits.Add(new PointKey(waiter.Id, join.OperationId, false));
                }
            }
        }

        return waits.Count != 0 && PlainFlow(execution).AlwaysFollows(new PointKey(instance.Id, timer.OperationId, false), waits) ? waits : [];
    }

    private string[] Regions(MethodInstance instance, SummaryValue value) =>
        value.Values.SelectMany(item => _heap.Resolve(instance.Id, item)).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A handle only a timer's <c>Dispose(handle)</c> can signal: a <c>new ManualResetEvent(false)</c> or <c>new AutoResetEvent(false)</c>
    /// that no call, store, capture, return or spawn receives except its constructor, <c>WaitOne()</c> calls and
    /// <c>Dispose(handle)</c> calls on one and the same timer.
    /// </summary>
    /// <param name="region">The handle's region.</param>
    private bool IsQuietEvent(string region)
    {
        if (_quietEvents.TryGetValue(region, out var cached))
            return cached;
        var quiet = QuietEvent(region);
        _quietEvents.Add(region, quiet);
        return quiet;
    }

    private bool QuietEvent(string region)
    {
        if (_heap.Regions[region] is not { Kind: HeapRegionKind.Allocation, SiteBodyId: { } siteBody, SiteOperationId: { } siteOperation } ||
            !_scope.Reachable.Bodies.TryGetValue(siteBody, out var body))
        {
            return false;
        }

        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        if (operations.OfType<IrAllocateOperation>().FirstOrDefault(operation => operation.Id == siteOperation) is not
            { AllocatedType: "System.Threading.ManualResetEvent" or "System.Threading.AutoResetEvent" } allocation)
        {
            return false;
        }

        var constructor = operations.OfType<IrCallOperation>().FirstOrDefault(call => call.ReceiverValue == allocation.ResultValue);
        if (constructor is not { ArgumentValues: [var initial] } ||
            body.Values.FirstOrDefault(value => value.Id == initial) is not { Kind: IrValueKind.Constant, Name: "False" })
        {
            return false;
        }

        var disposed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in _heap.Instances.Values)
        {
            CheckCancellation();
            var summary = instance.Summary;
            bool Hits(IEnumerable<AbstractValue> values) => values.Any(value => _heap.Resolve(instance.Id, value).Contains(region));
            bool HitsAny(IEnumerable<SummaryValue?> values) => Hits(values.OfType<SummaryValue>().SelectMany(value => value.Values));

            if (summary.Calls.Any(call => Hits(call.Receivers) || Hits(call.Arguments.SelectMany(argument => argument.Values))) ||
                summary.Stores.Any(store => Hits(store.Values)) || summary.Elements.Any(element => Hits(element.Values)) ||
                summary.Returns.Any(transfer => Hits(transfer.Values)) || summary.CapturedStores.Any(store => Hits(store.Values)) ||
                summary.RefParameters.Any(parameter => Hits(parameter.Values)) ||
                summary.Delegates.Any(transfer => Hits(transfer.Delegate.CapturedValues.Values.SelectMany(values => values))) ||
                summary.Spawns.Any(spawn => HitsAny([spawn.State, .. spawn.Work])) ||
                summary.Timers.Any(timer => HitsAny([timer.State, timer.Callback])) ||
                summary.Joins.Any(join => join.Kind != SummaryJoinKind.WaitOne && HitsAny(join.Handles)))
            {
                return false;
            }

            foreach (var call in summary.OpaqueCalls.Where(call => Hits(call.Receivers) || Hits(call.Arguments.SelectMany(argument => argument.Values))))
            {
                CheckCancellation();
                if (TimerDisposeWithHandle(instance, call.OperationId) is { } dispose)
                {
                    if (dispose.Timer.UnknownSources.Any(source => source is not UnknownSource.Null))
                        return false;
                    disposed.UnionWith(dispose.Timer.Values.SelectMany(value => _heap.Resolve(instance.Id, value)));
                    continue;
                }

                var allowed = instance.BodyId == siteBody && call.OperationId == constructor.Id ||
                              summary.Joins.Any(join => join.Kind == SummaryJoinKind.WaitOne && join.CallOperationId == call.OperationId);
                if (!allowed)
                    return false;
            }
        }

        // Every dispose into the handle must be of one timer: with two, the handle may be set when only one of them has finished.
        return disposed.Count == 1;
    }

    /// <summary>The dispose event of a call that is <c>System.Threading.Timer.Dispose(WaitHandle)</c>: the lowering follows exactly that
    /// call with it.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="callOperationId">The call operation.</param>
    private SummaryTimer? TimerDisposeWithHandle(MethodInstance instance, int callOperationId)
    {
        if (!_scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
            return null;
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var index = Array.FindIndex(operations, operation => operation.Id == callOperationId);
        return index >= 0 && index + 1 < operations.Length &&
               operations[index] is IrCallOperation call && operations[index + 1] is IrTimerOperation { Action: IrTimerAction.DisposeWaitHandle } dispose &&
               dispose.TimerValue == call.ReceiverValue && call.ArgumentValues.Contains(dispose.WaitHandleValue!.Value)
            ? instance.Summary.Timers.FirstOrDefault(timer => timer.OperationId == dispose.Id)
            : null;
    }

    /// <summary>The listed tasks of a <c>WhenAll</c> result region, with the instance that lists them, when the execution runs that call.</summary>
    /// <param name="execution">The execution.</param>
    /// <param name="group">The group.</param>
    private IReadOnlyList<(MethodInstance Instance, SummaryValue Task)>? GroupMembers(string execution, string group)
    {
        var region = _heap.Regions[group];
        foreach (var visit in _visits.GetValueOrDefault(execution) ?? [])
        {
            CheckCancellation();
            if (!_heap.Instances.TryGetValue(visit.InstanceId, out var instance) || instance.BodyId != region.SiteBodyId)
                continue;
            if (instance.Summary.WhenAlls.FirstOrDefault(whenAll => whenAll.CallOperationId == region.SiteOperationId) is { } whenAll &&
                _heap.Resolve(instance.Id, new CallResultValue(whenAll.CallOperationId)).Contains(group))
            {
                return whenAll.Tasks.Select(task => (instance, task)).ToArray();
            }
        }

        return null;
    }

    /// <summary>The execution a handle value names with proven identity: it points to exactly one region, that region is one spawn's
    /// handle (or tail) in this instance tree, the spawn runs once in a parent that runs once, the task is no composite, and the value
    /// comes from nowhere else. A field read counts when the field holds only that handle and a store of it precedes the join on every
    /// path; a call result counts when it is an async call's handle.</summary>
    /// <param name="execution">The joining execution.</param>
    /// <param name="instance">The instance expressing the handle value.</param>
    /// <param name="handle">The handle value.</param>
    /// <param name="joinOperation">The operation the handle is judged at.</param>
    private string? ProveHandle(string execution, MethodInstance instance, SummaryValue handle, int joinOperation)
    {
        var target = ProveHandleSource(execution, instance, handle, joinOperation);
        return target is not null && SpawnedOnce(target) && !IsComposite(target) ? target : null;
    }

    /// <summary>The execution a handle value names with proven identity, whether or not it is started once: it points to exactly one
    /// region, that region is one spawn's handle (or tail) in this instance tree, and the value comes from nowhere else. The completion
    /// proof of a pass asks this and proves for itself that the handle is the pass's own.</summary>
    /// <param name="execution">The joining execution.</param>
    /// <param name="instance">The instance expressing the handle value.</param>
    /// <param name="handle">The handle value.</param>
    /// <param name="joinOperation">The operation the handle is judged at.</param>
    private string? ProveHandleSource(string execution, MethodInstance instance, SummaryValue handle, int joinOperation)
    {
        var regions = handle.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
        if (regions.Length != 1 || HandleExecution(execution, regions[0]) is not { } target ||
            !SourcesExcused(execution, instance, handle, regions[0], joinOperation))
        {
            return null;
        }

        return target;
    }

    /// <summary>The async call of a non-async body whose task every return of that body gives back, each return proven as a join's
    /// handle is (<see cref="ProveHandleSource"/>): the body then runs as that call, which it makes synchronously in the execution
    /// running it. Null where some return is another task, a task from elsewhere or the task of another call.</summary>
    /// <param name="execution">The execution running the body.</param>
    /// <param name="callee">The body's instance.</param>
    internal (string Instance, int Operation)? ReturnedAsyncCall(string execution, string callee)
    {
        if (!_heap.Instances.TryGetValue(callee, out var instance) || !_scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ||
            body.IsAsync || instance.Summary.Returns.Count == 0)
        {
            return null;
        }
        int? operation = null;
        foreach (var returned in instance.Summary.Returns)
        {
            CheckCancellation();
            var value = new SummaryValue(returned.Values, returned.UnknownSources)
            {
                SourceCalls = returned.SourceCalls,
                Completions = returned.Completions,
                CompletedCalls = returned.CompletedCalls
            };
            if (ProveHandleSource(execution, instance, value, returned.OperationId) is not { } target ||
                !_anchorOfChild.TryGetValue(target, out var anchor) || anchor is not { Kind: SpawnAnchorKind.AsyncReturn } ||
                anchor.ExecutionId != execution || anchor.CallerInstance != instance.Id || operation is { } other && other != anchor.OperationId)
            {
                return null;
            }
            operation = anchor.OperationId;
        }
        return (instance.Id, operation!.Value);
    }

    /// <summary>Every continuation a curated call's own task waits for, with the same source proof as an async call's handle.
    /// Unlike a spawn handle it names all runs of the delegate, whose continuation execution may be repeated.</summary>
    /// <param name="execution">The waiting execution.</param>
    /// <param name="instance">The instance expressing the handle.</param>
    /// <param name="handle">The handle value.</param>
    /// <param name="operation">The wait operation.</param>
    private IReadOnlyList<string>? ProveCallTask(string execution, MethodInstance instance, SummaryValue handle, int operation)
    {
        if (_awaitedCalls.Count == 0)
            return null;
        var regions = handle.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
        if (regions is not [var region])
            return null;
        var calls = _awaitedCalls.Where(call => call.Task == region).ToArray();
        if (calls.Length == 0 || _heap.CallTasks.Any(producer => producer.Value == region &&
            !calls.Any(call => call.Instance == producer.Key.Instance && call.Operation == producer.Key.Operation)))
        {
            return null;
        }
        return SourcesExcused(execution, instance, handle, region, operation)
            ? calls.SelectMany(call => call.Continuations).Distinct(StringComparer.Ordinal).ToArray()
            : null;
    }

    /// <summary>Whether every unknown source of a value that resolves to one region is one that cannot be another object: a field read
    /// before its first write, when the field holds only that region and a store of it precedes the operation on every path, or an
    /// async, curated or ordinary call's result, when every call the value comes from provably returns that task. A value
    /// that is a task's completion is excused only where no task its await reads may complete with an object the heap cannot name.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    /// <param name="operation">The operation it is judged at.</param>
    /// <param name="allowNull">Whether a boundary may also carry null, which names no other task.</param>
    private bool SourcesExcused(string execution, MethodInstance instance, SummaryValue value, string region, int operation, bool allowNull = false) =>
        !value.Completions.Any(completion => _heap.UnfollowedCompletions.Contains((instance.Id, completion))) &&
        value.UnknownSources.All(source => source switch
        {
            UnknownSource.FieldBeforeWrite => FieldStoreDominates(execution, instance, value, region, operation),
            UnknownSource.SourceCall => (_handles.ContainsKey(region) || _heap.CallTasks.Values.Contains(region)) &&
                                        CallsReturn(execution, instance, value, region),
            UnknownSource.Parameter => BoundToRegion(execution, instance, value, region),
            UnknownSource.Null => allowNull,
            _ => false
        });

    /// <summary>Whether every parameter binds to that one region, and every argument of every binding call has no unexcused source.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance whose parameter bindings are read.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    private bool BoundToRegion(string execution, MethodInstance instance, SummaryValue value, string region)
    {
        var parameters = value.Values.OfType<ParameterValue>().ToArray();
        var bindings = _heap.Edges.Where(edge => edge.CalleeInstance == instance.Id).ToArray();
        return parameters.Length != 0 && bindings.Length != 0 && parameters.All(parameter =>
            instance.Parameters.TryGetValue(parameter.Ordinal, out var bound) && bound.Count == 1 && bound.Contains(region) &&
            bindings.All(edge => _heap.Instances.TryGetValue(edge.CallerInstance, out var caller) &&
                caller.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId)?.Arguments
                    .FirstOrDefault(argument => argument.ParameterOrdinal == parameter.Ordinal) is { } argument &&
                BindingExcused(execution, caller, new SummaryValue(argument.Values, argument.UnknownSources)
                {
                    SourceCalls = argument.SourceCalls,
                    Completions = argument.Completions,
                    CompletedCalls = argument.CompletedCalls
                }, region, edge.OperationId)));
    }

    /// <summary>Whether a value crossing a parameter or field boundary can name only that region or null, with every source excused.
    /// The boundary's operation guards recursive parameter and field bindings.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the crossing value.</param>
    /// <param name="value">The value crossing the boundary.</param>
    /// <param name="region">The only task region allowed.</param>
    /// <param name="operation">The binding call or field store.</param>
    private bool BindingExcused(string execution, MethodInstance instance, SummaryValue value, string region, int operation)
    {
        if (!_provingBindings.Add((instance.Id, operation)))
            return false;
        try
        {
            return value.Values.SelectMany(item => _heap.Resolve(instance.Id, item)).All(item => item == region) &&
                   SourcesExcused(execution, instance, value, region, operation, allowNull: true);
        }
        finally
        {
            _provingBindings.Remove((instance.Id, operation));
        }
    }

    /// <summary>Whether every call a value comes from returns that region and nothing else. Every callee's return origins must account
    /// for the region, including arguments behind a parameter whose summary marks the result as unfollowed. An unresolved target or
    /// an unaccounted return origin may return any task. A call the value is the completion of gives
    /// what its result's tasks complete with, followed as many times as the value consumed it (<see cref="HeapSolution.Gives"/>).</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    private bool CallsReturn(string execution, MethodInstance instance, SummaryValue value, string region) =>
        value.SourceCalls.Count != 0 &&
        value.SourceCalls.All(call => value.Depths(call).All(depth => _heap.Gives(instance.Id, call, depth).SetEquals([region]) &&
                                                                      ReturnsExcused(execution, instance, call, region, depth)));

    /// <summary>Whether every body the call may run gives that handle with an origin it can account for itself: the regions of a call
    /// result name the objects the returned values point to, not the null or the field before its first write they may also be. An async
    /// body or a curated sequential-awaited call gives its call the task it makes. An async body's returns complete that task;
    /// so its returns account for the completion of its task,
    /// one consumption fewer, never for the task itself. A non-async body's returns are what the call gives, consumed as often.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The call's operation.</param>
    /// <param name="region">The one region the value resolves to.</param>
    /// <param name="depth">How many times the value consumed the call's task.</param>
    private bool ReturnsExcused(string execution, MethodInstance instance, int call, string region, int depth)
    {
        if (depth == 0 && _heap.CallTasks.TryGetValue((instance.Id, call), out var task) && task == region)
            return true;
        if (!_proving.Add((instance.Id, call)))
            return false;

        try
        {
            var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call)
                                     .Select(edge => _heap.Instances.GetValueOrDefault(edge.CalleeInstance))
                                     .OfType<MethodInstance>()
                                     .ToArray();
            return !_heap.UnresolvedCallTargets.Contains((instance.Id, call)) && callees.Length != 0 &&
                   callees.All(callee => (_scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) &&
                                          body is { IsAsync: true, IsAsyncIterator: false }) switch
                   {
                       true when depth == 0 => true,
                       true => ReturnsExcusedAt(execution, callee, region, depth - 1),
                       false => ReturnsExcusedAt(execution, callee, region, depth)
                   });
        }
        finally
        {
            _proving.Remove((instance.Id, call));
        }
    }

    /// <summary>Whether every value a body returns, consumed <paramref name="depth"/> times, is that handle with an origin it can account
    /// for itself (<see cref="ExcusedAt"/>).</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="callee">The body's instance.</param>
    /// <param name="region">The one region the value resolves to.</param>
    /// <param name="depth">How many times the returned values are consumed.</param>
    private bool ReturnsExcusedAt(string execution, MethodInstance callee, string region, int depth) =>
        callee.Summary.Returns.All(returned => ExcusedAt(execution, callee,
                                                         new SummaryValue(returned.Values, returned.UnknownSources)
                                                         {
                                                             SourceCalls = returned.SourceCalls,
                                                             Completions = returned.Completions,
                                                             CompletedCalls = returned.CompletedCalls
                                                         },
                                                         region, returned.OperationId, depth));

    /// <summary>Whether a value, consumed <paramref name="depth"/> times, gives that handle with an origin it can account for itself.
    /// Unconsumed, its own unknown sources must be excused (<see cref="SourcesExcused"/>). Consumed, the tasks it names must be all it
    /// may be — no unknown source but a call whose result the heap follows, no completion of an unfollowed task —, and what each of them
    /// completes with, consumed once fewer, must be excused in turn (<see cref="TaskExcused"/>).</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region the value resolves to once consumed.</param>
    /// <param name="operation">The operation it is judged at.</param>
    /// <param name="depth">How many times the value is consumed.</param>
    private bool ExcusedAt(string execution, MethodInstance instance, SummaryValue value, string region, int operation, int depth)
    {
        if (depth == 0)
            return SourcesExcused(execution, instance, value, region, operation);
        if (value.Completions.Any(completion => _heap.UnfollowedCompletions.Contains((instance.Id, completion))) ||
            value.UnknownSources.Any(source => source != UnknownSource.SourceCall) ||
            value.SourceCalls.Any(call => _heap.UnfollowedCallResults.Contains((instance.Id, call))))
        {
            return false;
        }

        var tasks = value.Values.SelectMany(item => _heap.Resolve(instance.Id, item)).Distinct(StringComparer.Ordinal).ToArray();
        return tasks.Length != 0 && tasks.All(task => TaskExcused(execution, task, region, depth));
    }

    /// <summary>Whether what a task region completes with, consumed once fewer than <paramref name="depth"/>, is that handle with an
    /// origin it can account for itself: the heap follows its completion, and every producer of it the heap names is an async body's
    /// returns or a task member completing it with values (<see cref="TaskCompleterKind.Returns"/>,
    /// <see cref="TaskCompleterKind.TaskOperation"/>), each of them excused in turn.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="task">The task region.</param>
    /// <param name="region">The one region the value resolves to once consumed.</param>
    /// <param name="depth">How many times the task is consumed.</param>
    private bool TaskExcused(string execution, string task, string region, int depth)
    {
        if (_heap.UnfollowedTaskCompletions.Contains(task) || _heap.TaskCompleters.GetValueOrDefault(task) is not { Count: > 0 } completers ||
            !_provingTasks.Add((task, depth)))
        {
            return false;
        }

        try
        {
            return completers.All(completer => _heap.Instances.GetValueOrDefault(completer.Instance) is { } producer && completer.Kind switch
            {
                TaskCompleterKind.Returns => ReturnsExcusedAt(execution, producer, region, depth - 1),
                TaskCompleterKind.TaskOperation =>
                    producer.Summary.TaskOperations.FirstOrDefault(operation => operation.OperationId == completer.Operation) is
                        { Kind: IrTaskKind.Completed } completing &&
                    completing.Values.All(value => ExcusedAt(execution, producer, value, region, completing.OperationId, depth - 1)),
                _ => false
            });
        }
        finally
        {
            _provingTasks.Remove((task, depth));
        }
    }

    private bool FieldStoreDominates(string execution, MethodInstance instance, SummaryValue handle, string region, int joinOperation)
    {
        // All writes must preserve the handle, and the waiting execution must store it on every path.
        var paths = handle.Values.OfType<PathValue>().ToArray();
        if (paths.Length == 0 || paths.Any(path => path.Segments.Count == 0 ||
            path.Segments.Any(segment => PathValue.IsStorage(segment) || segment == PathValue.WILDCARD)))
            return false;

        var join = new PointKey(instance.Id, joinOperation, false);
        foreach (var path in paths)
        {
            CheckCancellation();
            var slot = path.Segments[^1];
            var receiver = path.Segments.Count == 1 ? path.Base : new PathValue(path.Base, path.Segments.Take(path.Segments.Count - 1).ToArray());
            var bases = _heap.Resolve(instance.Id, receiver).ToHashSet(StringComparer.Ordinal);
            if (bases.Count == 0 || bases.Any(@base => !_heap.PointsTo(@base, slot).SetEquals([region])))
                return false;

            var stores = new List<PointKey>();
            foreach (var writer in _heap.Instances.Values)
            {
                CheckCancellation();
                foreach (var store in FieldStores(writer, bases, slot))
                {
                    CheckCancellation();
                    if (store.Value is null || !BindingExcused(execution, writer, store.Value, region, store.Operation))
                        return false;
                    if (store.Value.Values.SelectMany(value => _heap.Resolve(writer.Id, value)).Contains(region) &&
                        (_visits.GetValueOrDefault(execution) ?? []).Any(visit => visit.InstanceId == writer.Id &&
                            _segments.Runs(writer.BodyId, visit.Segment, store.Operation)))
                    {
                        stores.Add(new PointKey(writer.Id, store.Operation, false));
                    }
                }
            }

            var flow = PlainFlow(execution);
            if (!flow.Dominates(stores, join))
                return false;
        }

        return true;
    }

    /// <summary>Every direct or reference store into a field of the named receivers, with the stored value's full origin.</summary>
    /// <param name="writer">The writing instance.</param>
    /// <param name="bases">The receivers of the field read.</param>
    /// <param name="slot">The field's slot.</param>
    private IEnumerable<(int Operation, SummaryValue? Value)> FieldStores(MethodInstance writer, IReadOnlySet<string> bases, string slot)
    {
        foreach (var store in writer.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store && FieldSlot.Key(access.Field) == slot))
        {
            CheckCancellation();
            if (store.Bases.SelectMany(value => _heap.Resolve(writer.Id, value)).Any(bases.Contains))
                yield return (store.OperationId, store.StoredValue);
        }
        foreach (var store in writer.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store))
        {
            CheckCancellation();
            if (store.Targets.SelectMany(target => _heap.ReferenceLocations(writer.Id, target)).Any(location => location.Slot == slot && bases.Contains(location.Region)))
                yield return (store.OperationId, store.StoredValue);
        }
    }

    // ---- intra-execution order ----

    private Flow FlowOf(string execution)
    {
        if (_context.Flows.TryGetValue(execution, out var flow))
            return flow;
        var throwsAfter = ThrowingAfter(Joins(execution).Where(join => join.ThrowsOnlyAfterCompletion).Select(join => (join.Point, join.Targets)).ToArray());
        flow = NewFlow(execution, throwsAfter);
        _context.Flows.Add(execution, flow);
        return flow;
    }

    /// <summary>The operations, by instance, whose exception comes only after the waits given completed, each with the points of the
    /// waits it comes after: its own, and every other such wait for no more than it waits for. A path asking about one of those tasks
    /// stops at any of them, and the exception came after that task completed; a path asking about another task does not.</summary>
    /// <param name="waits">The waits whose exception comes only after their tasks completed, by point and targets.</param>
    private Dictionary<(string Instance, int Operation), IReadOnlyCollection<PointKey>> ThrowingAfter(
        IReadOnlyList<(PointKey Point, IReadOnlyList<string> Targets)> waits)
    {
        var throwing = new Dictionary<(string Instance, int Operation), IReadOnlyCollection<PointKey>>();
        foreach (var wait in waits)
        {
            CheckCancellation();
            var key = (wait.Point.Instance, ThrowingOperation(wait.Point));
            var within = waits.Where(other => other.Targets.All(wait.Targets.Contains)).Select(other => other.Point);
            throwing[key] = throwing.TryGetValue(key, out var known) ? [.. known.Union(within)] : [.. within.Distinct()];
        }

        return throwing;
    }

    /// <summary>The operation a join's exceptions leave from: the call of a joining BCL call, which is what may throw, and not the join
    /// that marks it; an await, or a call site a wait is hoisted to, throws from the point itself.</summary>
    /// <param name="point">The join's point.</param>
    private int ThrowingOperation(PointKey point) =>
        _heap.Instances.TryGetValue(point.Instance, out var instance) &&
        instance.Summary.Joins.FirstOrDefault(join => join.OperationId == point.Operation) is { CallOperationId: int call }
            ? call
            : point.Operation;

    private Flow PlainFlow(string execution)
    {
        if (!_plainFlows.TryGetValue(execution, out var flow))
            _plainFlows.Add(execution, flow = NewFlow(execution, new Dictionary<(string, int), IReadOnlyCollection<PointKey>>(), readByForm: true));
        return flow;
    }

    private Flow NewFlow(string execution, IReadOnlyDictionary<(string Instance, int Operation), IReadOnlyCollection<PointKey>> throwsAfter,
                         bool readByForm = false) =>
        new(_scope, _heap, _segments, _entries.GetValueOrDefault(execution) ?? [], _visits.GetValueOrDefault(execution) ?? [],
            _steps.GetValueOrDefault(execution) ?? [], throwsAfter, CheckCancellation, EarlyTaskWaits(execution), _sequentialRuns, readByForm);

    /// <summary>The blocking waits for tasks of an execution's visits whose form throws only after their tasks complete but whose
    /// environment does not let them (<see cref="ThrowsAfterCompletion"/>), by instance and join operation: the handles' proof is each
    /// join's own, so only the form and the environment decide here. Every other blocking wait is read as its form says.</summary>
    /// <param name="execution">The execution.</param>
    private HashSet<(string Instance, int Operation)> EarlyTaskWaits(string execution)
    {
        var early = new HashSet<(string Instance, int Operation)>();
        foreach (var instanceId in (_visits.GetValueOrDefault(execution) ?? []).Select(visit => visit.InstanceId).Distinct(StringComparer.Ordinal))
        {
            CheckCancellation();
            if (!_heap.Instances.TryGetValue(instanceId, out var instance))
                continue;
            foreach (var join in instance.Summary.Joins.Where(join => join.Kind != SummaryJoinKind.Await && join.ThrowsOnlyAfterCompletion))
            {
                CheckCancellation();
                if (!ThrowsAfterCompletion(execution, instanceId, join, proven: true))
                    early.Add((instanceId, join.OperationId));
            }
        }

        return early;
    }

    private bool Precedes(string execution, PointKey first, PointKey second)
    {
        CheckCancellation();
        return FlowOf(execution).Precedes(first, second);
    }

    /// <summary>Whether the execution has waited for a target at a point: every path to it passes one of the joins that wait for that
    /// target, which need not be the same one on every path.</summary>
    /// <param name="execution">The waiting execution.</param>
    /// <param name="joined">The target execution waited for.</param>
    /// <param name="point">The point the paths lead to.</param>
    private bool Dominates(string execution, string joined, PointKey point)
    {
        CheckCancellation();
        return FlowOf(execution).Dominates(Joins(execution).Where(join => join.Targets.Contains(joined, StringComparer.Ordinal))
                                                       .Select(join => join.Point)
                                                       .ToArray(),
                                        point);
    }

    private static List<TValue> Get<TValue>(Dictionary<string, List<TValue>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
            map.Add(key, list = []);
        return list;
    }

    /// <summary>A point of an execution: the position before an operation of an instance, or, for an async call, the position after it.</summary>
    /// <param name="Instance">The instance the operation belongs to.</param>
    /// <param name="Operation">The operation, or -1 for the execution's start.</param>
    /// <param name="AfterCall">Whether the point is after the operation rather than before it.</param>
    internal readonly record struct PointKey(string Instance, int Operation, bool AfterCall)
    {
        /// <summary>The point every other point of the execution follows: where it starts.</summary>
        internal static PointKey Entry { get; } = new("", -1, false);
    }

    /// <summary>
    /// One execution's interprocedural control flow over the body segments it runs: a node before each operation and at each block's
    /// end, plus an entry and an exit per segment. Passing an operation is an edge; an operation that may throw has an edge to each
    /// handler of its block, except an await that throws only after completion and the operations that belong to a wait. The exception of
    /// a wait that throws only after completion comes after that wait (<see cref="WaitExit"/>): a path through it has waited for what the
    /// wait waits for and for nothing else; that of any other wait comes before it. A call descends into its callees and returns only to its own site, with a summary edge when a callee can complete. A call
    /// that runs a delegate 0..n times (SPEC TD-034a) may run it again from where it stands. A prefix leaves its body at its first await.
    /// </summary>
    internal sealed class Flow
    {
        /// <summary>The kind of the unsupported operation a collection expression lowers to.</summary>
        private const string COLLECTION_EXPRESSION = "CollectionExpression";

        /// <summary>The prefix of the type a conversion to a <c>ReadOnlySpan&lt;T&gt;</c> gives.</summary>
        private const string READ_ONLY_SPAN = "System.ReadOnlySpan<";

        private readonly List<int>[] _plain;
        private readonly int[] _pass;
        private readonly Dictionary<int, int[]> _calls = [];
        private readonly List<int>[] _callers;
        private readonly int[] _entry;
        private readonly int[] _exit;
        private readonly int[] _exitVisit;
        private readonly int[] _visitOf;
        private readonly Dictionary<(string Instance, int Operation), List<int>> _operationNodes = [];
        private readonly Dictionary<(string Instance, BodySegment Segment), int> _visitIndex = [];
        private readonly HashSet<int> _unresolved = [];
        private readonly HashSet<int> _escapes = [];

        /// <summary>The escapes of operations computing a handle a wait waits on: an exception there leaves before the wait, and the body
        /// has then not returned either.</summary>
        private readonly HashSet<int> _producing = [];

        /// <summary>The exceptional steps of the operations a wait throws at, by node (<see cref="WaitExit"/>).</summary>
        private readonly Dictionary<int, WaitExit> _waitExits = [];

        /// <summary>The calls that may run a delegate again before they return: a known call's <c>invoke-now</c> delegate, an
        /// enumeration's <c>iterator</c> delegate, the delegate of a member of the collection table
        /// (<see cref="HeapSolution.RepeatedRuns"/>).</summary>
        private readonly HashSet<int> _repeating = [];

        /// <summary>The repeating calls whose record runs the delegate one run at a time: the next run begins only after this one returns,
        /// so none overlaps a run in progress.</summary>
        private readonly HashSet<int> _sequential = [];

        /// <summary>Where an exception that leaves a visit goes on, by the visit and whether it reaches the execution's exit, as
        /// <see cref="Thrown"/> works it out.</summary>
        private readonly Dictionary<(int Visit, bool ExceptionalExits), (int Node, bool Ascend, int[] Gates)[]> _thrown = [];
        private readonly List<int> _starts = [];
        private readonly Dictionary<string, HashSet<int>> _completing = new(StringComparer.Ordinal);
        private readonly Action? _checkCancellation;

        private void CheckCancellation()
        {
            if (_checkCancellation is not null)
                _checkCancellation();
            else
                WorkCancellation.Check();
        }

        private readonly Dictionary<string, bool[]> _reach = new(StringComparer.Ordinal);

        /// <summary>Builds the flow of one execution.</summary>
        /// <param name="scope">The process scope whose bodies the visits run.</param>
        /// <param name="heap">The solved heap.</param>
        /// <param name="segments">The segments of async bodies.</param>
        /// <param name="entries">Where the execution starts.</param>
        /// <param name="visits">The body segments the execution runs.</param>
        /// <param name="steps">The call edges the execution follows.</param>
        /// <param name="throwsAfter">The operations, by instance, whose exception comes only after the work they wait for completed, each
        /// with the points of the waits it comes after (<see cref="WaitExit"/>).</param>
        /// <param name="checkCancellation">Checks the cancellation of the work building the flow, or null for the ambient one.</param>
        /// <param name="earlyWaits">The task waits, by instance and join operation, whose exception may come before their tasks complete
        /// although their form says otherwise; none when null.</param>
        /// <param name="sequentialRuns">The calls, by instance and operation, whose record runs the delegate one run at a time; none when
        /// null.</param>
        /// <param name="readByForm">Whether a blocking wait whose form throws only after completion and which the early waits do not hold
        /// throws after the wait although the operations given do not list it: the flow is built before the joins are proven, and a query
        /// asking about such a wait reads the proof from the wait itself.</param>
        internal Flow(ScopeProgram scope, HeapSolution heap, AsyncSegments segments, IReadOnlyList<ExecutionEntry> entries,
                      IReadOnlyList<ExecutionVisit> visits, IReadOnlyList<ExecutionStep> steps,
                      IReadOnlyDictionary<(string Instance, int Operation), IReadOnlyCollection<PointKey>> throwsAfter,
                      Action? checkCancellation = null, IReadOnlySet<(string Instance, int Operation)>? earlyWaits = null,
                      IReadOnlySet<(string Instance, int Operation)>? sequentialRuns = null, bool readByForm = false)
        {
            _checkCancellation = checkCancellation;
            var bodies = visits.Select(visit => heap.Instances.TryGetValue(visit.InstanceId, out var instance) &&
                                                scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body)
                                           ? body
                                           : null)
                               .ToArray();
            var offsets = new int[visits.Count];
            var blockStarts = new int[visits.Count][];
            var total = 0;
            for (var index = 0; index < visits.Count; index++)
            {
                CheckCancellation();
                offsets[index] = total;
                var blocks = bodies[index]?.Blocks ?? [];
                blockStarts[index] = new int[blocks.Count];
                var size = 0;
                for (var block = 0; block < blocks.Count; block++)
                {
                    CheckCancellation();
                    blockStarts[index][block] = size;
                    size += blocks[block].Operations.Count + 1;
                }

                total += size + 2;
            }

            _plain = Enumerable.Range(0, total).Select(_ => new List<int>()).ToArray();
            _pass = Enumerable.Repeat(-1, total).ToArray();
            _exitVisit = Enumerable.Repeat(-1, total).ToArray();
            _callers = Enumerable.Range(0, visits.Count).Select(_ => new List<int>()).ToArray();
            _entry = new int[visits.Count];
            _exit = new int[visits.Count];
            for (var index = 0; index < visits.Count; index++)
            {
                CheckCancellation();
                _visitIndex[(visits[index].InstanceId, visits[index].Segment)] = index;
                var size = (bodies[index]?.Blocks ?? []).Sum(block => block.Operations.Count + 1);
                _entry[index] = offsets[index] + size;
                _exit[index] = _entry[index] + 1;
                _exitVisit[_exit[index]] = index;
            }

            _visitOf = new int[total];
            for (var index = 0; index < visits.Count; index++)
            {
                CheckCancellation();
                Array.Fill(_visitOf, index, offsets[index], _exit[index] + 1 - offsets[index]);
            }

            var callees = steps.GroupBy(step => (step.Caller, step.CallerSegment, step.OperationId))
                          .ToDictionary(group => group.Key,
                                        group => group.Select(step => _visitIndex.TryGetValue((step.Callee, step.CalleeSegment), out var callee) ? callee : -1)
                                                      .Where(callee => callee >= 0).Distinct().ToArray());
            var calleesAt = steps.ToLookup(step => (step.Caller, step.OperationId), step => step.Callee);
            var repeated = steps.Where(step => heap.RepeatedRuns.Contains((step.Caller, step.OperationId, step.Callee)))
                                .Select(step => (step.Caller, step.CallerSegment, step.OperationId))
                                .ToHashSet();
            var after = new List<(WaitExit Exit, IReadOnlyCollection<PointKey> Waited)>();

            for (var index = 0; index < visits.Count; index++)
            {
                CheckCancellation();
                var visit = visits[index];
                var body = bodies[index];
                if (body is null || body.Blocks.Count == 0)
                {
                    _plain[_entry[index]].Add(_exit[index]);
                    continue;
                }

                int Node(int block, int position) => offsets[index] + blockStarts[index][block] + position;

                var normal = new List<int>[body.Blocks.Count];
                var exceptional = new List<int>[body.Blocks.Count];
                for (var block = 0; block < body.Blocks.Count; block++)
                {
                    CheckCancellation();
                    (normal[block], exceptional[block]) = ([], []);
                }
                foreach (var block in body.Blocks)
                {
                    CheckCancellation();
                    foreach (var predecessor in block.FlowPredecessors)
                    {
                        CheckCancellation();
                        (predecessor.EdgeKind == IrEdgeKind.Exceptional ? exceptional : normal)[predecessor.BlockOrdinal].Add(block.Ordinal);
                    }
                }

                var instanceBody = heap.Instances[visit.InstanceId].BodyId;
                Func<IrCallOperation, bool> faults = call => FaultsItsTask(scope, heap, body, visit.InstanceId, call, calleesAt[(visit.InstanceId, call.Id)]);
                var waits = WaitParts(body, join => earlyWaits?.Contains((visit.InstanceId, join.Id)) != true, faults);
                var listings = WaitParts(body, join => throwsAfter.ContainsKey((visit.InstanceId, join.CallOperationId)), faults).Parts;
                var reads = readByForm
                    ? body.Blocks.SelectMany(block => block.Operations).OfType<IrJoinOperation>()
                          .Where(join => join.ThrowsOnlyAfterCompletion && earlyWaits?.Contains((visit.InstanceId, join.Id)) != true)
                          .Select(join => join.CallOperationId)
                          .ToHashSet()
                    : [];
                var nodeOf = new Dictionary<int, int>();
                for (var block = 0; block < body.Blocks.Count; block++)
                {
                    CheckCancellation();
                    var operations = body.Blocks[block].Operations;
                    for (var position = 0; position < operations.Count; position++)
                    {
                        CheckCancellation();
                        nodeOf[operations[position].Id] = Node(block, position);
                    }
                }
                var marks = body.Blocks.SelectMany(block => block.Operations).OfType<IrJoinOperation>()
                                .ToLookup(join => join.CallOperationId, join => nodeOf[join.Id]);
                for (var block = 0; block < body.Blocks.Count; block++)
                {
                    CheckCancellation();
                    var operations = body.Blocks[block].Operations;
                    for (var position = 0; position < operations.Count; position++)
                    {
                        CheckCancellation();
                        var operation = operations[position];
                        var node = Node(block, position);
                        if (callees.TryGetValue((visit.InstanceId, visit.Segment, operation.Id), out var called) && called.Length != 0)
                        {
                            _calls[node] = called;
                            foreach (var callee in called)
                            {
                                CheckCancellation();
                                _callers[callee].Add(node);
                            }
                            // A dispatch whose receiver the heap could not follow may run a body it does not have, which returns
                            // without passing anything the called bodies pass.
                            if (heap.UnresolvedCallTargets.Contains((visit.InstanceId, operation.Id)))
                                _unresolved.Add(node);
                            if (repeated.Contains((visit.InstanceId, visit.Segment, operation.Id)))
                            {
                                _repeating.Add(node);
                                if (sequentialRuns?.Contains((visit.InstanceId, operation.Id)) == true)
                                    _sequential.Add(node);
                            }
                        }
                        else if (visit.Segment == BodySegment.Prefix && operation is IrAwaitOperation)
                        {
                            _pass[node] = _exit[index];
                        }
                        else
                        {
                            _pass[node] = Node(block, position + 1);
                        }

                        if (segments.Runs(instanceBody, visit.Segment, operation.Id))
                        {
                            if (!_operationNodes.TryGetValue((visit.InstanceId, operation.Id), out var nodes))
                                _operationNodes.Add((visit.InstanceId, operation.Id), nodes = []);
                            nodes.Add(node);
                        }

                        var afterWait = throwsAfter.TryGetValue((visit.InstanceId, operation.Id), out var waited);
                        if (MayThrow(operation) && !(afterWait && operation is IrAwaitOperation))
                        {
                            // The exception of a wait read as throwing only after completion comes after that wait: its steps are taken
                            // only by a path that does not stop at the wait. Any other wait may throw before its work completes.
                            var exit = afterWait || reads.Contains(operation.Id)
                                ? _waitExits[node] = new WaitExit([node, .. marks[operation.Id]])
                                : null;
                            if (afterWait)
                                after.Add((exit!, waited!));
                            if (afterWait || !listings.Contains(operation.Id))
                            {
                                foreach (var handler in exceptional[block])
                                {
                                    CheckCancellation();
                                    (afterWait ? exit!.Handlers : _plain[node]).Add(Node(handler, 0));
                                }
                            }
                            // With no handler in this body the exception leaves it here, unless the operation is part of a wait; that of
                            // a wait not so read leaves before the wait, like that of a producer.
                            if (exceptional[block].Count == 0 && exit is not null)
                            {
                                exit.Escapes = true;
                            }
                            else if (exceptional[block].Count == 0 && (!waits.Parts.Contains(operation.Id) || waits.Calls.Contains(operation.Id)))
                            {
                                _escapes.Add(node);
                                if (waits.Producers.Contains(operation.Id) || waits.Calls.Contains(operation.Id))
                                    _producing.Add(node);
                            }
                        }
                    }

                    var end = Node(block, operations.Count);
                    foreach (var successor in normal[block])
                    {
                        CheckCancellation();
                        _plain[end].Add(Node(successor, 0));
                    }
                    if (body.Blocks[block].Kind == IrBlockKind.Exit)
                        _plain[end].Add(_exit[index]);
                }

                if (visit.Segment == BodySegment.Tail)
                {
                    for (var block = 0; block < body.Blocks.Count; block++)
                    {
                        CheckCancellation();
                        var operations = body.Blocks[block].Operations;
                        for (var position = 0; position < operations.Count; position++)
                        {
                            CheckCancellation();
                            if (operations[position] is IrAwaitOperation && segments.Runs(instanceBody, BodySegment.Prefix, operations[position].Id))
                                _plain[_entry[index]].Add(Node(block, position + 1));
                        }
                    }
                }
                else
                {
                    _plain[_entry[index]].Add(Node(0, 0));
                }
            }

            foreach (var entry in entries)
            {
                CheckCancellation();
                if (_visitIndex.TryGetValue((entry.InstanceId, entry.Segment), out var index))
                    _starts.Add(_entry[index]);
            }

            foreach (var (exit, waited) in after)
            {
                CheckCancellation();
                exit.Gates = [.. exit.Gates.Concat(waited.SelectMany(Nodes)).Distinct().Order()];
            }
        }

        /// <summary>The operations of a body that belong to its waits, which throw only as the wait does: the call each join marks; the
        /// operations building what it is handed — the array or span made for the call, its initializer's stores, its implicit conversion to a
        /// <c>ReadOnlySpan&lt;T&gt;</c>, the reads and the
        /// identity and implicit reference conversions of the tasks it lists; the calls of the wait's form that give the same task
        /// (<c>GetAwaiter()</c>, <c>ConfigureAwait(bool)</c>) or the task of a <c>Task.WhenAll</c> that lists its tasks, which its
        /// <c>Wait()</c> waits on, with that group's own listing; and a call whose exception faults the task it returns
        /// (<see cref="FaultsItsTask"/>), which the wait throws once that task completed. An exception out of one of them is not a path
        /// that skipped the wait. Any other operation computing a handle or an argument — a program call, a field or element read, a later
        /// store into the array, an explicit conversion, an unboxing or a user-defined one, <c>ConfigureAwait(ConfigureAwaitOptions)</c>
        /// — may throw before the wait, whichever task the value it gives is proven to be; those that define a handle the join waits on
        /// are its producers.</summary>
        /// <param name="body">The body.</param>
        /// <param name="counts">Whether a join is such a wait.</param>
        /// <param name="faults">Whether a call's exception faults the task it returns instead of leaving the call.</param>
        private WaitOperations WaitParts(IrBody body, Func<IrJoinOperation, bool> counts, Func<IrCallOperation, bool> faults)
        {
            var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            var joins = operations.OfType<IrJoinOperation>().Where(counts).ToArray();
            if (joins.Length == 0)
                return new WaitOperations([], [], []);

            var byId = operations.ToDictionary(operation => operation.Id);
            var types = body.Values.ToDictionary(value => value.Id, value => value.Type);
            var definers = Definers(operations);
            var stores = operations.OfType<IrStoreElementOperation>().ToLookup(store => store.ReceiverValue);
            var same = operations.OfType<IrTaskOperation>().Where(task => task is { Kind: IrTaskKind.Same, ResultValue: not null, TaskValue: not null })
                                 .ToLookup(task => (task.CallOperationId, task.ResultValue!.Value), task => task.TaskValue!.Value);
            var groups = operations.OfType<IrWhenAllOperation>().ToLookup(group => group.ResultValue);
            var parts = new HashSet<int>();
            var producers = new HashSet<int>();
            foreach (var join in joins)
            {
                CheckCancellation();
                parts.Add(join.CallOperationId);
                producers.UnionWith(join.HandleValues.SelectMany(value => definers.GetValueOrDefault(value) ?? []));
                var pending = new Stack<int>(join.HandleValues);
                // The array or span a call lists its tasks in is made for the call, written in it.
                var listings = new HashSet<int>();
                if (join is { Kind: IrJoinKind.WaitAll, HandlesKnown: true } && byId.GetValueOrDefault(join.CallOperationId) is IrCallOperation waitAll)
                    Listing(waitAll.ArgumentValues);
                var seen = new HashSet<int>();
                while (pending.TryPop(out var value))
                {
                    CheckCancellation();
                    if (!seen.Add(value))
                        continue;
                    // Only the initializer's stores, at a constant index into the array it makes of the element type, cannot throw.
                    foreach (var store in stores[value].Where(store => store.Provenance.Transformation == "array-initializer"))
                    {
                        CheckCancellation();
                        parts.Add(store.Id);
                        pending.Push(store.Value);
                    }
                    foreach (var definer in (definers.GetValueOrDefault(value) ?? []).Select(id => byId[id]))
                    {
                        CheckCancellation();
                        switch (definer)
                        {
                            case IrCallOperation call when same[(call.Id, value)].ToArray() is { Length: > 0 } tasks:
                                // ConfigureAwait(ConfigureAwaitOptions) throws on options its task does not take.
                                if (call.ArgumentValues.All(argument => tasks.Contains(argument) || types.GetValueOrDefault(argument) == "bool"))
                                    parts.Add(call.Id);
                                Follow(tasks);
                                break;
                            case IrCallOperation call when groups[value].ToArray() is { Length: > 0 } listed:
                                // A group that does not list its tasks may be handed a null one, which the call throws on.
                                if (listed.All(group => group.TasksKnown))
                                {
                                    parts.Add(call.Id);
                                    Listing(call.ArgumentValues);
                                }
                                Follow(listed.SelectMany(group => group.TaskValues));
                                break;
                            case IrCallOperation call when faults(call):
                                parts.Add(call.Id);
                                break;
                            case IrCallOperation:
                                break;
                            // An explicit reference conversion, an unboxing or a user-defined conversion may throw; the implicit one of an
                            // array or a collection expression to a ReadOnlySpan, which takes a covariant array, does not.
                            case IrConvertOperation convert when convert.ConversionKind == IrConversionKind.Identity ||
                                                                 convert is { ConversionKind: IrConversionKind.Reference, IsImplicit: true } ||
                                                                 convert is { ConversionKind: IrConversionKind.Other, IsImplicit: true } &&
                                                                 convert.Type.StartsWith(READ_ONLY_SPAN, StringComparison.Ordinal):
                                parts.Add(convert.Id);
                                if (listings.Contains(value))
                                    listings.Add(convert.OperandValue);
                                pending.Push(convert.OperandValue);
                                break;
                            // Only the array or collection the call lists its tasks in is made for it; any other operation giving the
                            // listing, an explicit conversion of it included, answers as it does for a handle.
                            case IrAllocateOperation or IrUnknownOperation { OperationKind: COLLECTION_EXPRESSION } when listings.Contains(value):
                                parts.Add(definer.Id);
                                Follow(definer.Operands);
                                break;
                            case var other when !MayThrow(other):
                                Follow(other.Operands);
                                break;
                        }
                    }
                }

                void Follow(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        CheckCancellation();
                        pending.Push(value);
                    }
                }

                void Listing(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        CheckCancellation();
                        listings.Add(value);
                        pending.Push(value);
                    }
                }
            }

            producers.ExceptWith(parts);
            return new WaitOperations(parts, producers, joins.Select(join => join.CallOperationId).ToHashSet());
        }

        /// <summary>The operations that belong to a body's waits, and those that compute a handle one of them waits on without
        /// belonging to it.</summary>
        /// <param name="Parts">The operations that throw only as the wait does.</param>
        /// <param name="Producers">The operations defining a handle a wait waits on that may throw before it.</param>
        /// <param name="Calls">The calls the waits mark, where each wait throws.</param>
        private sealed record WaitOperations(HashSet<int> Parts, HashSet<int> Producers, HashSet<int> Calls);

        /// <summary>The exceptional steps of an operation a wait throws at, which come after that wait and only after it: a path that
        /// stops at the wait — whose cut holds one of the gates — has not taken them, and any other path has, without waiting for
        /// anything the wait waits for. An exception the wait throws after the work completed thus orders that work alone, never the work
        /// of a later wait it skips.</summary>
        /// <param name="gates">The nodes of the wait: the operation's own and those of the joins marking it.</param>
        private sealed class WaitExit(int[] gates)
        {
            /// <summary>The nodes of the waits the exception comes after: the wait's own and, for one that throws only after completion,
            /// those of the waits for no more than it waits for, any of which a path asking about one of its tasks stops at.</summary>
            internal int[] Gates { get; set; } = gates;

            /// <summary>The handlers of its block the exception goes to, when it throws only after completion; an exception that may come
            /// earlier keeps its plain edges to them.</summary>
            internal List<int> Handlers { get; } = [];

            /// <summary>Whether the exception leaves the body here, its block having no handler.</summary>
            internal bool Escapes { get; set; }
        }

        /// <summary>Whether a path that stops at none of the cut's nodes passes a step that comes after the waits given.</summary>
        /// <param name="gates">The nodes of the waits.</param>
        /// <param name="cut">The cut.</param>
        private static bool Open(int[] gates, IReadOnlySet<int> cut) => !gates.Any(cut.Contains);

        /// <summary>Whether a call's exception faults the task it returns instead of leaving the call: every body it may run is async and
        /// returns a task, whose builder catches what the body throws, and nothing before the body runs can throw — the call has no
        /// receiver, or its receiver is the calling body's own, which is never null.</summary>
        /// <param name="scope">The process scope whose bodies the calls run.</param>
        /// <param name="heap">The solved heap.</param>
        /// <param name="body">The calling body.</param>
        /// <param name="instance">The calling instance.</param>
        /// <param name="call">The call.</param>
        /// <param name="callees">The instances the call runs in the execution.</param>
        private bool FaultsItsTask(ScopeProgram scope, HeapSolution heap, IrBody body, string instance, IrCallOperation call,
                                   IEnumerable<string> callees)
        {
            if (heap.UnresolvedCallTargets.Contains((instance, call.Id)))
                return false;
            if (call.CallKind is not (IrCallKind.Static or IrCallKind.LocalFunction) &&
                (call.CallKind is IrCallKind.Delegate or IrCallKind.Constructor || call.ReceiverValue is not int receiver ||
                 body.Values.FirstOrDefault(value => value.Id == receiver) is not { Kind: IrValueKind.Receiver }))
            {
                return false;
            }

            var any = false;
            foreach (var callee in callees)
            {
                CheckCancellation();
                if (!heap.Instances.TryGetValue(callee, out var called) || !scope.Reachable.Bodies.TryGetValue(called.BodyId, out var run) ||
                    run is not { IsAsync: true, IsAsyncIterator: false } || run.ReturnType == "void")
                {
                    return false;
                }
                any = true;
            }

            return any;
        }

        /// <summary>The operations defining each value of a body.</summary>
        /// <param name="operations">The body's operations.</param>
        private Dictionary<int, List<int>> Definers(IReadOnlyList<IrOperation> operations)
        {
            var definers = new Dictionary<int, List<int>>();
            foreach (var operation in operations)
            {
                CheckCancellation();
                foreach (var value in operation.DefinedValues)
                {
                    CheckCancellation();
                    if (!definers.TryGetValue(value, out var ids))
                        definers.Add(value, ids = []);
                    ids.Add(operation.Id);
                }
            }

            return definers;
        }

        /// <summary>Whether an operation can throw where it stands: calls, awaits, field and element reads and writes, conversions,
        /// arithmetic, locks and operations the lowering does not model. Assignments, phis, comparisons, allocations (their constructor is
        /// a call) and the BCL marks that follow a call cannot.</summary>
        /// <param name="operation">The operation.</param>
        internal static bool MayThrow(IrOperation operation) => operation is IrCallOperation or IrAwaitOperation or IrLoadFieldOperation or
            IrStoreFieldOperation or IrLoadElementOperation or IrStoreElementOperation or IrConvertOperation or IrComputeOperation or
            IrAcquireOperation or IrReleaseOperation or IrUnknownOperation or IrAtomicOperation;

        internal bool Precedes(PointKey first, PointKey second)
        {
            var firstNodes = Nodes(first);
            var secondNodes = Nodes(second);
            if (firstNodes.Count == 0 || secondNodes.Count == 0)
                return false;
            var reach = Reach($"from:{second}", secondNodes, ascend: true, new HashSet<int>());
            return !firstNodes.Any(node => reach[node]);
        }

        /// <summary>Whether a valid path leads from one of the points, past its operation, to one of them again: one run of the execution
        /// passes the operation more than once, around a loop, through a recursion, by a second call of a body that holds it or by a call
        /// that runs a delegate again. The path follows <see cref="Reach"/>'s rules, from where the operation leads.</summary>
        /// <param name="points">The points of one operation, one per instance of its body.</param>
        internal bool Repeats(IReadOnlyCollection<PointKey> points)
        {
            var targets = points.SelectMany(Nodes).ToHashSet();
            if (targets.Count == 0)
                return false;
            var none = new HashSet<int>();
            var completing = Completing(none);
            var starts = new Stack<(int Node, bool Ascend)>();
            foreach (var target in targets)
            {
                CheckCancellation();
                Expand(target, true, none, completing, starts);
            }
            var reach = Reach($"repeats:{string.Join(";", targets.Order())}", starts, none);
            return targets.Any(target => reach[target]);
        }

        internal bool Dominates(PointKey join, PointKey point)
        {
            CheckCancellation();
            return Dominates([join], point);
        }

        /// <summary>Whether no path from the execution's start reaches the point around every one of the cut points.</summary>
        /// <param name="cuts">The cut points.</param>
        /// <param name="point">The point the paths lead to.</param>
        internal bool Dominates(IReadOnlyCollection<PointKey> cuts, PointKey point)
        {
            var cutNodes = cuts.SelectMany(Nodes).ToHashSet();
            var pointNodes = Nodes(point);
            if (cutNodes.Count == 0 || pointNodes.Count == 0)
                return false;
            // Every point of an execution follows its start.
            if (cuts.Any(cut => cut.Operation < 0))
                return true;
            var reach = Reach($"around:{string.Join(";", cuts.Select(cut => cut.ToString()).Order(StringComparer.Ordinal))}", _starts,
                              ascend: false, cutNodes);
            return !pointNodes.Any(node => reach[node]);
        }

        /// <summary>Whether every path from just after an operation (from the point itself for a point after a call, from the
        /// execution's start for none) to the execution's end passes one of the targets: to its normal end, or with exceptional exits,
        /// also out of it with an exception no handler catches.</summary>
        /// <param name="from">The point the paths start after, or null for the execution's start.</param>
        /// <param name="targets">The points one of which every path must pass.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its end.</param>
        internal bool AlwaysFollows(PointKey? from, IReadOnlyCollection<PointKey> targets, bool exceptionalExits = false)
        {
            var fromNodes = from is not { } point ? _starts.ToArray()
                : point.AfterCall ? Nodes(point).ToArray()
                : Nodes(point).Where(node => _pass[node] >= 0).Select(node => _pass[node]).ToArray();
            var targetNodes = targets.SelectMany(Nodes).ToHashSet();
            if (fromNodes.Length == 0 || targetNodes.Count == 0)
                return false;
            var key = $"after:{from?.ToString() ?? "start"}>{string.Join(";", targets.Select(target => target.ToString()).Order(StringComparer.Ordinal))}" +
                      (exceptionalExits ? ">exceptional" : "");
            var reach = Reach(key, fromNodes, ascend: true, targetNodes, exceptionalExits);
            return !_starts.Any(start => reach[start + 1]);
        }

        /// <summary>Whether every path from just after a pass's start (from the point itself for a point after a call) to that start
        /// again passes one of the pass's completions; a run that a call running a delegate again around the start begins next starts
        /// before this one passes anything, so a path reaches the start through it too.</summary>
        /// <param name="start">The pass's start.</param>
        /// <param name="completions">The points that complete the pass's work.</param>
        internal bool BeforeNext(PointKey start, IReadOnlyCollection<PointKey> completions) =>
            BeforeDestination(start, start with { AfterCall = false }, completions, overlappingRuns: true);

        /// <summary>Whether every path from just after a pass's start (from the point itself for a point after a call) to a destination
        /// passes one of the pass's completions. With overlapping runs, a run that a call running a delegate again around the start begins
        /// next starts before this one passes anything: what that run reaches is reached without a completion of this pass.</summary>
        /// <param name="start">The pass's start.</param>
        /// <param name="destination">The point the paths lead to.</param>
        /// <param name="completions">The points that complete the pass's work.</param>
        /// <param name="overlappingRuns">Whether the runs a call around the start may begin before this one completes count.</param>
        internal bool BeforeDestination(PointKey start, PointKey destination, IReadOnlyCollection<PointKey> completions, bool overlappingRuns)
        {
            var targets = Nodes(destination);
            var cuts = completions.SelectMany(Nodes).ToHashSet();
            if (targets.Count == 0 || cuts.Count == 0 || PassBegins(start, cuts) is not { } begins)
                return false;
            var reach = Reach($"pass:{start}>{string.Join(";", cuts.Order())}", begins, cuts);
            if (targets.Any(node => reach[node]))
                return false;
            if (!overlappingRuns)
                return true;
            var runs = OverlappingRuns(Nodes(start));
            if (runs.Count == 0)
                return true;
            var overlapping = Reach($"overlapping:{string.Join(";", runs.Order())}", runs, ascend: false, new HashSet<int>());
            return !targets.Any(node => overlapping[node]);
        }

        /// <summary>Whether every path from just after a pass's start (from the point itself for a point after a call) to the
        /// execution's end passes one of the pass's completions: to its normal end, or with exceptional exits, also out of it with an
        /// exception no handler catches.</summary>
        /// <param name="start">The pass's start.</param>
        /// <param name="completions">The points that complete the pass's work.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its end.</param>
        internal bool BeforeReturn(PointKey start, IReadOnlyCollection<PointKey> completions, bool exceptionalExits)
        {
            var cuts = completions.SelectMany(Nodes).ToHashSet();
            if (cuts.Count == 0 || PassBegins(start, cuts) is not { } begins)
                return false;
            var reach = Reach($"return:{start}>{exceptionalExits}>{string.Join(";", cuts.Order())}", begins, cuts, exceptionalExits);
            return !_starts.Any(node => reach[node + 1]);
        }

        /// <summary>Where the paths of a pass begin: just after its start's operation, or at the point itself for a point after a call. A
        /// start that is itself one of the pass's completions — a call whose return completes the work it starts — has completed the pass
        /// once it returns, so its paths are only the steps that leave it without returning: to a handler, out of its body, into a run it
        /// begins again. Null when the start has no node to begin from.</summary>
        /// <param name="start">The pass's start.</param>
        /// <param name="cuts">The nodes of the pass's completions.</param>
        private (int Node, bool Ascend)[]? PassBegins(PointKey start, HashSet<int> cuts)
        {
            var nodes = Nodes(start);
            if (start.AfterCall || nodes.Count == 0 || !nodes.All(cuts.Contains))
            {
                var begins = start.AfterCall ? nodes.ToArray() : nodes.Where(node => _pass[node] >= 0).Select(node => _pass[node]).ToArray();
                return begins.Length == 0 ? null : begins.Select(node => (node, true)).ToArray();
            }
            var completing = Completing(cuts);
            var steps = new Stack<(int Node, bool Ascend)>();
            foreach (var node in nodes)
            {
                CheckCancellation();
                Expand(node, true, cuts, completing, steps);
            }
            return steps.ToArray();
        }

        /// <summary>The entries of the runs a call that runs a delegate again may begin while a run holding one of the nodes is still in
        /// progress: every such call on a chain of calling sites above the nodes' bodies, except one whose record runs the delegate one run
        /// at a time, whose next run begins only after this one returns.</summary>
        /// <param name="nodes">The nodes.</param>
        private List<int> OverlappingRuns(IEnumerable<int> nodes)
        {
            var runs = new List<int>();
            var seen = new HashSet<int>();
            var pending = new Stack<int>(nodes.Select(node => _visitOf[node]));
            while (pending.TryPop(out var visit))
            {
                CheckCancellation();
                if (!seen.Add(visit))
                    continue;
                foreach (var site in _callers[visit])
                {
                    CheckCancellation();
                    if (_repeating.Contains(site) && !_sequential.Contains(site))
                        runs.AddRange(_calls[site].Select(callee => _entry[callee]));
                    pending.Push(_visitOf[site]);
                }
            }

            return runs.Distinct().ToList();
        }

        private List<int> Nodes(PointKey point)
        {
            if (point.Operation < 0)
                return _starts;
            var nodes = _operationNodes.GetValueOrDefault((point.Instance, point.Operation)) ?? [];
            return point.AfterCall ? nodes.Select(node => node + 1).ToList() : nodes;
        }

        /// <summary>Whether every path of one visit, from its entry to its exit or out of it with an exception, passes one of the points:
        /// waiting for one thing at several places is one wait, so the alternatives are cut together. An exception computing a handle a
        /// wait waits on leaves before that wait, but without returning, and so does the exception of an earlier wait for something else,
        /// which skips the later one (<see cref="WaitExit"/>): they count only where the exceptional exits are asked for.</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="segment">The segment.</param>
        /// <param name="points">The points.</param>
        /// <param name="exceptional">Whether every exception leaving the visit has to come after one of the points, as it must for the
        /// visit's exception to come only after the work completed.</param>
        internal bool AlwaysWaits(string instance, BodySegment segment, IReadOnlyCollection<PointKey> points, bool exceptional)
        {
            if (!_visitIndex.TryGetValue((instance, segment), out var visit))
                return false;
            var cut = points.SelectMany(Nodes).ToHashSet();
            if (cut.Count == 0)
                return false;

            var seen = new HashSet<int>();
            var pending = new Stack<int>([_entry[visit]]);
            while (pending.TryPop(out var node))
            {
                CheckCancellation();
                if (cut.Contains(node))
                    continue;
                var exit = _waitExits.GetValueOrDefault(node) is { } wait && Open(wait.Gates, cut) ? wait : null;
                if (node == _exit[visit] || _escapes.Contains(node) && (exceptional || !_producing.Contains(node)) ||
                    exceptional && exit is { Escapes: true })
                {
                    return false;
                }
                if (!seen.Add(node))
                    continue;
                if (_pass[node] >= 0)
                    pending.Push(_pass[node]);
                foreach (var next in _plain[node].Concat(exit?.Handlers ?? []))
                {
                    CheckCancellation();
                    pending.Push(next);
                }
                if (_calls.ContainsKey(node))
                    pending.Push(node + 1);
            }

            return true;
        }

        /// <summary>The nodes valid paths reach from the starts without passing a cut operation, by <see cref="Expand"/>'s steps.</summary>
        /// <param name="key">The key.</param>
        /// <param name="starts">The starts.</param>
        /// <param name="ascend">Whether a path may leave the body of a start through a site calling it.</param>
        /// <param name="cut">The cut.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its exit.</param>
        private bool[] Reach(string key, IEnumerable<int> starts, bool ascend, IReadOnlySet<int> cut, bool exceptionalExits = false)
        {
            CheckCancellation();
            return Reach(key, starts.Select(start => (start, ascend)), cut, exceptionalExits);
        }

        /// <summary>The nodes valid paths reach from the starts, each start saying whether a path may leave its body through a site calling
        /// it, without passing a cut operation, by <see cref="Expand"/>'s steps.</summary>
        /// <param name="key">The key the answer is kept under; it names the exceptional exits a query asks for.</param>
        /// <param name="starts">The starts.</param>
        /// <param name="cut">The cut.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its exit.</param>
        private bool[] Reach(string key, IEnumerable<(int Node, bool Ascend)> starts, IReadOnlySet<int> cut, bool exceptionalExits = false)
        {
            if (_reach.TryGetValue(key, out var cached))
                return cached;

            var completing = Completing(cut);
            var reached = new bool[_pass.Length];
            var seen = new HashSet<(int, bool)>();
            var pending = new Stack<(int Node, bool Ascend)>(starts);
            while (pending.TryPop(out var item))
            {
                CheckCancellation();
                if (!seen.Add(item))
                    continue;
                reached[item.Node] = true;
                Expand(item.Node, item.Ascend, cut, completing, pending, exceptionalExits);
            }

            _reach.Add(key, reached);
            return reached;
        }

        /// <summary>The steps a valid path takes from a node: passing its operation unless the cut holds it, to a handler of its block —
        /// after a wait only where the cut does not hold that wait (<see cref="WaitExit"/>) —, into a callee and, through the summary edge
        /// of its own site, past the call when a callee can complete, and into the call's delegate again where the call may run it again.
        /// A path that leaves a body it may leave returns to any site calling it, and to that site again where it may run the body again;
        /// an exception that leaves such a body goes on as <see cref="Thrown"/> says.</summary>
        /// <param name="node">The node.</param>
        /// <param name="up">Whether the path may leave the node's body through a site calling it.</param>
        /// <param name="cut">The operations the path may not pass.</param>
        /// <param name="completing">The segments whose exit their entry reaches without passing the cut.</param>
        /// <param name="pending">Receives the steps.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its exit.</param>
        private void Expand(int node, bool up, IReadOnlySet<int> cut, HashSet<int> completing, Stack<(int Node, bool Ascend)> pending,
                            bool exceptionalExits = false)
        {
            if (_pass[node] >= 0 && !cut.Contains(node))
                pending.Push((_pass[node], up));
            var exit = _waitExits.GetValueOrDefault(node) is { } wait && Open(wait.Gates, cut) ? wait : null;
            foreach (var next in _plain[node].Concat(exit?.Handlers ?? []))
            {
                CheckCancellation();
                pending.Push((next, up));
            }
            if (_calls.TryGetValue(node, out var called))
            {
                foreach (var callee in called)
                {
                    CheckCancellation();
                    pending.Push((_entry[callee], false));
                }
                if (!cut.Contains(node) && (_unresolved.Contains(node) || called.Any(completing.Contains)))
                    pending.Push((node + 1, up));
                if (_repeating.Contains(node))
                    pending.Push((node, up));
            }

            if (!up)
                return;
            if (_exitVisit[node] is var visit and >= 0)
            {
                foreach (var site in _callers[visit])
                {
                    CheckCancellation();
                    pending.Push((site + 1, true));
                    if (_repeating.Contains(site))
                        pending.Push((site, true));
                }
            }
            if (_escapes.Contains(node) || exit is { Escapes: true })
            {
                foreach (var (next, ascend, gates) in Thrown(_visitOf[node], exceptionalExits))
                {
                    CheckCancellation();
                    if (Open(gates, cut))
                        pending.Push((next, ascend));
                }
            }
        }

        /// <summary>Where an exception that leaves a visit goes on: to the handlers of every site calling the visit, to the site again where
        /// its call may run the visit again, and from a site whose block has no handler on out of that site's visit in turn. With exceptional
        /// exits, an exception that leaves a visit the execution starts in reaches that visit's exit. A step past a site whose call is a
        /// wait comes after that wait (<see cref="WaitExit"/>): each step carries the nodes of the waits it comes after, and a path takes it
        /// only where its cut holds none of them.</summary>
        /// <param name="visit">The visit the exception leaves.</param>
        /// <param name="exceptionalExits">Whether an exception that leaves the execution reaches its exit.</param>
        private (int Node, bool Ascend, int[] Gates)[] Thrown(int visit, bool exceptionalExits)
        {
            if (_thrown.TryGetValue((visit, exceptionalExits), out var cached))
                return cached;

            var targets = new List<(int Node, bool Ascend, int[] Gates)>();
            var left = new HashSet<(int Visit, string Gates)> { (visit, "") };
            var pending = new Stack<(int Visit, int[] Gates)>([(visit, [])]);
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                if (exceptionalExits && _starts.Contains(_entry[current.Visit]))
                    targets.Add((_exit[current.Visit], true, current.Gates));
                foreach (var site in _callers[current.Visit])
                {
                    CheckCancellation();
                    var wait = _waitExits.GetValueOrDefault(site);
                    int[] past = wait is null ? current.Gates : [.. current.Gates.Union(wait.Gates).Order()];
                    foreach (var handler in _plain[site])
                    {
                        CheckCancellation();
                        targets.Add((handler, true, current.Gates));
                    }
                    foreach (var handler in wait?.Handlers ?? [])
                    {
                        CheckCancellation();
                        targets.Add((handler, true, past));
                    }
                    if (_repeating.Contains(site))
                        targets.Add((site, true, current.Gates));
                    var gates = _escapes.Contains(site) ? current.Gates : wait is { Escapes: true } ? past : null;
                    if (gates is not null && left.Add((_visitOf[site], string.Join(",", gates))))
                        pending.Push((_visitOf[site], gates));
                }
            }

            cached = targets.DistinctBy(target => (target.Node, target.Ascend, string.Join(",", target.Gates))).ToArray();
            _thrown.Add((visit, exceptionalExits), cached);
            return cached;
        }

        /// <summary>The segments whose exit their entry reaches without passing a cut operation, with callees summarized.</summary>
        /// <param name="cut">The cut.</param>
        private HashSet<int> Completing(IReadOnlySet<int> cut)
        {
            CheckCancellation();
            var key = string.Join(",", cut.Order());
            if (_completing.TryGetValue(key, out var cached))
                return cached;

            var completing = new HashSet<int>();
            var changed = true;
            while (changed)
            {
                CheckCancellation();
                changed = false;
                for (var visit = 0; visit < _entry.Length; visit++)
                {
                    CheckCancellation();
                    if (completing.Contains(visit) || !Completes(visit, cut, completing))
                        continue;
                    completing.Add(visit);
                    changed = true;
                }
            }

            _completing.Add(key, completing);
            return completing;
        }

        private bool Completes(int visit, IReadOnlySet<int> cut, HashSet<int> completing)
        {
            CheckCancellation();
            var seen = new HashSet<int>();
            var pending = new Stack<int>([_entry[visit]]);
            while (pending.TryPop(out var node))
            {
                CheckCancellation();
                if (node == _exit[visit])
                    return true;
                if (!seen.Add(node))
                    continue;
                if (_pass[node] >= 0 && !cut.Contains(node))
                    pending.Push(_pass[node]);
                var exit = _waitExits.GetValueOrDefault(node) is { } wait && Open(wait.Gates, cut) ? wait : null;
                foreach (var next in _plain[node].Concat(exit?.Handlers ?? []))
                {
                    CheckCancellation();
                    pending.Push(next);
                }
                if (_calls.TryGetValue(node, out var called) && !cut.Contains(node) && called.Any(completing.Contains))
                    pending.Push(node + 1);
            }

            return false;
        }
    }
}
