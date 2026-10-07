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

/// <summary>
/// The happens-before graph of ADR 0008 over one scope's executions, queried per pair of accesses. Inside an execution a point precedes
/// another when no valid interprocedural path leads back from the second to the first; a join precedes what cannot be reached from the
/// execution's start around it. Between executions the edges are spawns, proven joins, <c>Parallel</c> returns, continuations, the tails
/// of async work and the end of startup; a path counts only when every inter-execution edge on it holds for every instance it
/// connects. Each execution's graph is built once, when a query first needs it.
/// </summary>
internal sealed class HappensBefore
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
    private readonly HashSet<string> _asyncHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _composite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _continuations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flow> _plainFlows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<JoinAnchor>> _joins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Execution, int Join)>> _joinedBy = new(StringComparer.Ordinal);
    private readonly HashSet<(string BodyId, int OperationId)> _unprovenJoins = [];
    private readonly HashSet<(string Instance, int Operation)> _proving = [];
    private readonly HashSet<(string Task, int Depth)> _provingTasks = [];
    private readonly Dictionary<string, bool> _quietEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _enumerated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _executionReach = new(StringComparer.Ordinal);
    private bool _joinsReady;
    private CancellationToken _constructionToken;

    internal HappensBefore(ScopeProgram scope, HeapSolution heap, AsyncSegments segments, IReadOnlyDictionary<string, ExecutionInstance> executions,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> entries,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>> visits,
                           IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>> steps, IReadOnlyList<SpawnAnchor> anchors,
                           IReadOnlyDictionary<string, string> tails, IReadOnlySet<string> duringStartup, CancellationToken cancellationToken)
    {
        _constructionToken = cancellationToken;
        _scope = scope;
        _heap = heap;
        _segments = segments;
        _executions = executions;
        _entries = entries;
        _visits = visits;
        _steps = steps;
        _tails = tails;
        _duringStartup = duringStartup;
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
                if (anchor.Kind == SpawnAnchorKind.AsyncReturn)
                    _asyncHandles.Add(handle);
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

    private void CheckCancellation() => _constructionToken.ThrowIfCancellationRequested();

    internal IReadOnlyList<OperationSite> UnprovenJoins
    {
        get
        {
            EnsureJoins();
            return _unprovenJoins.Select(join => new OperationSite(join.BodyId, join.OperationId))
                                 .OrderBy(join => join.BodyId, StringComparer.Ordinal)
                                 .ThenBy(join => join.OperationId)
                                 .ToArray();
        }
    }

    internal bool Ordered(Access first, Access second) => Path(first, second) || Path(second, first);

    /// <summary>Whether every path of an execution to an access passes a join operation: only then could that join, proven, order the
    /// access after what it waits for.</summary>
    /// <param name="execution">The execution whose paths are checked.</param>
    /// <param name="joinInstance">The instance making the join.</param>
    /// <param name="joinOperation">The join operation.</param>
    /// <param name="access">The access the paths lead to.</param>
    internal bool JoinDominates(string execution, string joinInstance, int joinOperation, Access access) =>
        FlowOf(execution).Dominates(new PointKey(joinInstance, joinOperation, false), new PointKey(access.InstanceId, access.OperationId, false));

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

    private bool Once(string execution) =>
        _executions.TryGetValue(execution, out var instance) && instance.Policy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized };

    /// <summary>A task whose completion the plan gives no order for: a continuation of anything but one proven spawn's handle, or the task a
    /// non-async <c>Task.Run</c> delegate returned. A join on it proves nothing.</summary>
    /// <param name="child">The child.</param>
    private bool IsComposite(string child)
    {
        CheckCancellation();
        if (_composite.TryGetValue(child, out var cached))
            return cached;
        _composite[child] = false;
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
                        Get(_continuations, antecedent!).Add(child);
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

        _composite[child] = composite;
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
    /// it starts the joined work (from its own start, when the work comes from elsewhere) to its end, exceptional paths included.</summary>
    /// <param name="joining">The joining.</param>
    /// <param name="join">The join.</param>
    /// <param name="joined">The joined.</param>
    private bool BoundsEnd(string joining, JoinAnchor join, string joined)
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

        return FlowOf(joining).AlwaysFollows(from, [join.Point]);
    }

    /// <summary>The anchors whose spawn edge holds for every instance of the child: the spawning execution runs at most once, and a timer's
    /// creation site runs once in it.</summary>
    /// <param name="execution">The spawning execution.</param>
    private IEnumerable<SpawnAnchor> TrustedAnchors(string execution) =>
        Once(execution)
            ? (_anchors.GetValueOrDefault(execution) ?? []).Where(anchor => anchor.Kind != SpawnAnchorKind.Timer || _executions[anchor.ChildId].SpawnedOnce)
            : [];

    private static PointKey AnchorPoint(SpawnAnchor anchor) =>
        new(anchor.CallerInstance, anchor.OperationId, anchor.Kind == SpawnAnchorKind.AsyncReturn);

    /// <summary>What follows an execution's end: its trusted joins, its trusted continuations, the tail of its async work and, for startup,
    /// the start of every execution that cannot start before startup ends.</summary>
    /// <param name="execution">The execution.</param>
    private IEnumerable<EventNode> EndEdges(string execution)
    {
        EnsureJoins();
        foreach (var (joining, join) in _joinedBy.GetValueOrDefault(execution) ?? [])
        {
            CheckCancellation();
            yield return new EventNode(EventKind.Join, joining, join, execution);
        }
        foreach (var continuation in _continuations.GetValueOrDefault(execution) ?? [])
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
        if (_executionReach.TryGetValue(start, out var cached))
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

        _executionReach.Add(start, reach);
        return reach;
    }

    // ---- joins ----

    private sealed record JoinAnchor(int Index, PointKey Point, IReadOnlyList<string> Targets, bool ThrowsOnlyAfterCompletion);

    private IReadOnlyList<JoinAnchor> Joins(string execution)
    {
        EnsureJoins();
        return _joins.GetValueOrDefault(execution) ?? [];
    }

    /// <summary>Proves the joins of every execution once: the handles each waits for, the unproven ones counted per operation, and the
    /// executions each proven join follows.</summary>
    private void EnsureJoins()
    {
        if (_joinsReady)
            return;
        _joinsReady = true;

        var candidates = new List<Candidate>();
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
                        _unprovenJoins.Add((instance.BodyId, operationId));
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
                        _unprovenJoins.Add((instance.BodyId, join.OperationId));
                    if (targets.Count != 0)
                        Wait(candidates, resumed, execution, visit, instance, join.OperationId, targets, join.ThrowsOnlyAfterCompletion);
                }
            }

            AtResumptions(candidates, execution, resumed);
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

        AtCallSites(candidates);
        foreach (var group in candidates.GroupBy(candidate => (candidate.Execution, candidate.Point)))
        {
            CheckCancellation();
            var joins = _joins.TryGetValue(group.Key.Execution, out var existing) ? (List<JoinAnchor>)existing : [];
            var targets = group.SelectMany(candidate => candidate.Targets).Distinct(StringComparer.Ordinal).ToArray();
            var join = new JoinAnchor(joins.Count, group.Key.Point, targets, group.Any(candidate => candidate.ThrowsAfter));
            joins.Add(join);
            _joins[group.Key.Execution] = joins;
            foreach (var target in targets.Where(target => target != group.Key.Execution))
            {
                CheckCancellation();
                Get(_joinedBy, target).Add((group.Key.Execution, join.Index));
            }
        }
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
    /// ones any of them does. The call keeps its exceptional edge unless every wait behind it throws after the work completed, since one
    /// that may throw earlier leaves the work unfinished on that path. A hoisted wait is a wait of its own site, so calls of calls follow.
    /// </summary>
    /// <param name="candidates">The candidates.</param>
    private void AtCallSites(List<Candidate> candidates)
    {
        var hoisted = new Dictionary<(string Execution, string Caller, int Operation), Candidate>();
        var changed = true;
        while (changed)
        {
            CheckCancellation();
            changed = false;
            foreach (var execution in _steps.Keys.Order(StringComparer.Ordinal))
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
                            if (!flow.AlwaysWaits(step.Callee, step.CalleeSegment, alternatives.Select(candidate => candidate.Point).ToArray()))
                                continue;
                            waited.Add(target);
                            throwsAfter &= alternatives.All(candidate => candidate.ThrowsAfter);
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

        candidates.AddRange(hoisted.Values);
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
                    if (ProveHandle(execution, memberInstance, member, join.OperationId) is { } target)
                        targets.Add(target);
                    else
                        proven = false;
                }

                continue;
            }

            if (ProveHandle(execution, instance, handle, join.OperationId) is { } single)
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
    private List<int> HandleWaits(string execution, MethodInstance instance, SummaryTimer timer)
    {
        if (timer.WaitHandle is not { UnknownSources.Count: 0 } handle || Regions(instance, handle) is not [var region] || !IsQuietEvent(region))
            return [];

        var waits = new List<(int OperationId, PointKey Point)>();
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
                    waits.Add((join.OperationId, new PointKey(waiter.Id, join.OperationId, false)));
                }
            }
        }

        return waits.Count != 0 &&
               PlainFlow(execution).AlwaysFollows(new PointKey(instance.Id, timer.OperationId, false), waits.Select(wait => wait.Point).ToArray())
            ? waits.Select(wait => wait.OperationId).ToList()
            : [];
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
        var regions = handle.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
        if (regions.Length != 1 || HandleExecution(execution, regions[0]) is not { } target ||
            !SourcesExcused(execution, instance, handle, regions[0], joinOperation))
        {
            return null;
        }

        return _executions[target].SpawnedOnce && !IsComposite(target) ? target : null;
    }

    /// <summary>Whether every unknown source of a value that resolves to one region is one that cannot be another object: a field read
    /// before its first write, when the field holds only that region and a store of it precedes the operation on every path, or an
    /// async call's result, when the region is that call's handle and every call the value comes from returns that same handle. A value
    /// that is a task's completion is excused only where no task its await reads may complete with an object the heap cannot name.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    /// <param name="operation">The operation it is judged at.</param>
    private bool SourcesExcused(string execution, MethodInstance instance, SummaryValue value, string region, int operation) =>
        !value.Completions.Any(completion => _heap.UnfollowedCompletions.Contains((instance.Id, completion))) &&
        value.UnknownSources.All(source => source switch
        {
            UnknownSource.FieldBeforeWrite => FieldStoreDominates(execution, instance, value, region, operation),
            UnknownSource.SourceCall => _asyncHandles.Contains(region) && CallsReturn(execution, instance, value, region),
            UnknownSource.Parameter => BoundToRegion(instance, value, region),
            _ => false
        });

    /// <summary>Whether every parameter the value comes from binds, in this instance, to that one region and nothing else. A summary
    /// cannot name what a parameter holds, which is why it records the parameter as an unknown source; the instance the call site
    /// made can, so a handle handed to a callee is as proven there as one it read from a field (R4).</summary>
    /// <param name="instance">The instance whose parameter bindings are read.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    private static bool BoundToRegion(MethodInstance instance, SummaryValue value, string region)
    {
        var parameters = value.Values.OfType<ParameterValue>().ToArray();
        return parameters.Length != 0 &&
               parameters.All(parameter => instance.Parameters.TryGetValue(parameter.Ordinal, out var bound) &&
                                           bound.Count == 1 && bound.Contains(region));
    }

    /// <summary>Whether every call a value comes from returns that region and nothing else; a call whose result the heap does not follow
    /// there, or whose callee may return an object the heap cannot name, may return any task. A call the value is the completion of gives
    /// what its result's tasks complete with, followed as many times as the value consumed it (<see cref="HeapSolution.Gives"/>).</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="region">The one region it resolves to.</param>
    private bool CallsReturn(string execution, MethodInstance instance, SummaryValue value, string region) =>
        value.SourceCalls.Count != 0 &&
        value.SourceCalls.All(call => !_heap.UnfollowedCallResults.Contains((instance.Id, call)) &&
                                      value.Depths(call).All(depth => _heap.Gives(instance.Id, call, depth).SetEquals([region]) &&
                                                                      ReturnsExcused(execution, instance, call, region, depth)));

    /// <summary>Whether every body the call may run gives that handle with an origin it can account for itself: the regions of a call
    /// result name the objects the returned values point to, not the null or the field before its first write they may also be. An async
    /// body gives its call the task the call makes, which its returns complete; so its returns account for the completion of its task,
    /// one consumption fewer, never for the task itself. A non-async body's returns are what the call gives, consumed as often.</summary>
    /// <param name="execution">The execution proving the value.</param>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The call's operation.</param>
    /// <param name="region">The one region the value resolves to.</param>
    /// <param name="depth">How many times the value consumed the call's task.</param>
    private bool ReturnsExcused(string execution, MethodInstance instance, int call, string region, int depth)
    {
        if (!_proving.Add((instance.Id, call)))
            return false;

        try
        {
            var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call)
                                     .Select(edge => _heap.Instances.GetValueOrDefault(edge.CalleeInstance))
                                     .OfType<MethodInstance>()
                                     .ToArray();
            return callees.Length != 0 &&
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
        var paths = handle.Values.OfType<PathValue>().ToArray();
        if (paths.Length == 0 || paths.Any(path => path.Segments.Count != 1 || PathValue.IsStorage(path.Segments[0]) || path.Segments[0] == PathValue.WILDCARD))
            return false;

        var join = new PointKey(instance.Id, joinOperation, false);
        foreach (var path in paths)
        {
            CheckCancellation();
            var slot = path.Segments[0];
            var bases = _heap.Resolve(instance.Id, path.Base).ToHashSet(StringComparer.Ordinal);
            if (bases.Count == 0 || bases.Any(@base => !_heap.PointsTo(@base, slot).SetEquals([region])))
                return false;

            var stores = new List<PointKey>();
            foreach (var visit in _visits.GetValueOrDefault(execution) ?? [])
            {
                CheckCancellation();
                if (!_heap.Instances.TryGetValue(visit.InstanceId, out var writer))
                    continue;
                foreach (var store in writer.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store && FieldSlot.Key(access.Field) == slot))
                {
                    CheckCancellation();
                    if (_segments.Runs(writer.BodyId, visit.Segment, store.OperationId) &&
                        store.Bases.SelectMany(value => _heap.Resolve(writer.Id, value)).Any(bases.Contains))
                    {
                        stores.Add(new PointKey(writer.Id, store.OperationId, false));
                    }
                }
            }

            var flow = PlainFlow(execution);
            if (!stores.Any(store => flow.Dominates(store, join)))
                return false;
        }

        return true;
    }

    // ---- intra-execution order ----

    private Flow FlowOf(string execution)
    {
        if (_flows.TryGetValue(execution, out var flow))
            return flow;
        var throwsAfter = Joins(execution).Where(join => join.ThrowsOnlyAfterCompletion).Select(join => (join.Point.Instance, ThrowingOperation(join.Point)))
                                          .ToHashSet();
        flow = NewFlow(execution, throwsAfter);
        _flows.Add(execution, flow);
        return flow;
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
            _plainFlows.Add(execution, flow = NewFlow(execution, new HashSet<(string, int)>()));
        return flow;
    }

    private Flow NewFlow(string execution, IReadOnlySet<(string Instance, int Operation)> throwsAfter) =>
        new(_scope, _heap, _segments, _entries.GetValueOrDefault(execution) ?? [], _visits.GetValueOrDefault(execution) ?? [],
            _steps.GetValueOrDefault(execution) ?? [], throwsAfter, CheckCancellation);

    private bool Precedes(string execution, PointKey first, PointKey second) => FlowOf(execution).Precedes(first, second);

    /// <summary>Whether the execution has waited for a target at a point: every path to it passes one of the joins that wait for that
    /// target, which need not be the same one on every path.</summary>
    /// <param name="execution">The waiting execution.</param>
    /// <param name="joined">The target execution waited for.</param>
    /// <param name="point">The point the paths lead to.</param>
    private bool Dominates(string execution, string joined, PointKey point) =>
        FlowOf(execution).Dominates(Joins(execution).Where(join => join.Targets.Contains(joined, StringComparer.Ordinal))
                                                   .Select(join => join.Point)
                                                   .ToArray(),
                                    point);

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
    /// handler of its block, except a join that throws only after completion; a call descends into its callees and returns only to its
    /// own site, with a summary edge when a callee can complete. A prefix leaves its body at its first await.
    /// </summary>
    internal sealed class Flow
    {
        private readonly List<int>[] _plain;
        private readonly int[] _pass;
        private readonly Dictionary<int, int[]> _calls = [];
        private readonly List<int>[] _callers;
        private readonly int[] _entry;
        private readonly int[] _exit;
        private readonly int[] _exitVisit;
        private readonly Dictionary<(string Instance, int Operation), List<int>> _operationNodes = [];
        private readonly Dictionary<(string Instance, BodySegment Segment), int> _visitIndex = [];
        private readonly HashSet<int> _unresolved = [];
        private readonly HashSet<int> _escapes = [];
        private readonly List<int> _starts = [];
        private readonly Dictionary<string, HashSet<int>> _completing = new(StringComparer.Ordinal);
        private readonly Action? _checkCancellation;

        private void CheckCancellation() => _checkCancellation?.Invoke();

        private readonly Dictionary<string, bool[]> _reach = new(StringComparer.Ordinal);

        internal Flow(ScopeProgram scope, HeapSolution heap, AsyncSegments segments, IReadOnlyList<ExecutionEntry> entries,
                      IReadOnlyList<ExecutionVisit> visits, IReadOnlyList<ExecutionStep> steps,
                      IReadOnlySet<(string Instance, int Operation)> throwsAfter, Action? checkCancellation = null)
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

            var callees = steps.GroupBy(step => (step.Caller, step.CallerSegment, step.OperationId))
                          .ToDictionary(group => group.Key,
                                        group => group.Select(step => _visitIndex.TryGetValue((step.Callee, step.CalleeSegment), out var callee) ? callee : -1)
                                                      .Where(callee => callee >= 0).Distinct().ToArray());

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
                var waitParts = WaitParts(body);
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

                        if (MayThrow(operation) && !throwsAfter.Contains((visit.InstanceId, operation.Id)))
                        {
                            foreach (var handler in exceptional[block])
                            {
                                CheckCancellation();
                                _plain[node].Add(Node(handler, 0));
                            }
                            // With no handler in this body the exception leaves it here, unless the operation is part of a wait.
                            if (exceptional[block].Count == 0 && !waitParts.Contains(operation.Id))
                                _escapes.Add(node);
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
        }

        /// <summary>The operations a wait is made of: the call each join marks and the operations computing the handles it waits on. An
        /// exception out of one of them is not a path that skipped the wait — it either comes from the wait itself, after the work it waited
        /// for completed, or from reading a handle whose identity the join has already been proven to hold.</summary>
        /// <param name="body">The body.</param>
        private HashSet<int> WaitParts(IrBody body)
        {
            var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            var joins = operations.OfType<IrJoinOperation>().ToArray();
            if (joins.Length == 0)
                return [];

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

            var parts = new HashSet<int>();
            foreach (var join in joins)
            {
                CheckCancellation();
                parts.Add(join.CallOperationId);
                foreach (var value in join.HandleValues)
                {
                    CheckCancellation();
                    parts.UnionWith(definers.GetValueOrDefault(value) ?? []);
                }
            }

            return parts;
        }

        /// <summary>Whether an operation can throw where it stands: calls, awaits, field and element reads and writes, conversions,
        /// arithmetic, locks and operations the lowering does not model. Assignments, phis, comparisons, allocations (their constructor is
        /// a call) and the BCL marks that follow a call cannot.</summary>
        /// <param name="operation">The operation.</param>
        private static bool MayThrow(IrOperation operation) => operation is IrCallOperation or IrAwaitOperation or IrLoadFieldOperation or
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

        internal bool Dominates(PointKey join, PointKey point) => Dominates([join], point);

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
        /// execution's start for none) to the execution's end passes one of the targets.</summary>
        /// <param name="from">The point the paths start after, or null for the execution's start.</param>
        /// <param name="targets">The points one of which every path must pass.</param>
        internal bool AlwaysFollows(PointKey? from, IReadOnlyCollection<PointKey> targets)
        {
            var fromNodes = from is not { } point ? _starts.ToArray()
                : point.AfterCall ? Nodes(point).ToArray()
                : Nodes(point).Where(node => _pass[node] >= 0).Select(node => _pass[node]).ToArray();
            var targetNodes = targets.SelectMany(Nodes).ToHashSet();
            if (fromNodes.Length == 0 || targetNodes.Count == 0)
                return false;
            var key = $"after:{from?.ToString() ?? "start"}>{string.Join(";", targets.Select(target => target.ToString()).Order(StringComparer.Ordinal))}";
            var reach = Reach(key, fromNodes, ascend: true, targetNodes);
            return !_starts.Any(start => reach[start + 1]);
        }

        private List<int> Nodes(PointKey point)
        {
            if (point.Operation < 0)
                return _starts;
            var nodes = _operationNodes.GetValueOrDefault((point.Instance, point.Operation)) ?? [];
            return point.AfterCall ? nodes.Select(node => node + 1).ToList() : nodes;
        }

        /// <summary>Whether every path of one visit, from its entry to its exit or out of it with an exception, passes one of the points:
        /// waiting for one thing at several places is one wait, so the alternatives are cut together.</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="segment">The segment.</param>
        /// <param name="points">The points.</param>
        internal bool AlwaysWaits(string instance, BodySegment segment, IReadOnlyCollection<PointKey> points)
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
                if (node == _exit[visit] || _escapes.Contains(node))
                    return false;
                if (!seen.Add(node))
                    continue;
                if (_pass[node] >= 0)
                    pending.Push(_pass[node]);
                foreach (var next in _plain[node])
                {
                    CheckCancellation();
                    pending.Push(next);
                }
                if (_calls.ContainsKey(node))
                    pending.Push(node + 1);
            }

            return true;
        }

        /// <summary>The nodes valid paths reach from the starts without passing a cut operation: a descent into a callee returns only through
        /// the summary edge of its own site, and an ascent from a start's own body returns to any site calling it.</summary>
        /// <param name="key">The key.</param>
        /// <param name="starts">The starts.</param>
        /// <param name="ascend">The ascend.</param>
        /// <param name="cut">The cut.</param>
        private bool[] Reach(string key, IEnumerable<int> starts, bool ascend, IReadOnlySet<int> cut)
        {
            if (_reach.TryGetValue(key, out var cached))
                return cached;

            var completing = Completing(cut);
            var reached = new bool[_pass.Length];
            var seen = new HashSet<(int, bool)>();
            var pending = new Stack<(int Node, bool Ascend)>(starts.Select(start => (start, ascend)));
            while (pending.TryPop(out var item))
            {
                CheckCancellation();
                if (!seen.Add(item))
                    continue;
                var (node, up) = item;
                reached[node] = true;
                if (_pass[node] >= 0 && !cut.Contains(node))
                    pending.Push((_pass[node], up));
                foreach (var next in _plain[node])
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
                }

                if (up && _exitVisit[node] is var visit and >= 0)
                {
                    foreach (var site in _callers[visit])
                    {
                        CheckCancellation();
                        pending.Push((site + 1, true));
                    }
                }
            }

            _reach.Add(key, reached);
            return reached;
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
                foreach (var next in _plain[node])
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
