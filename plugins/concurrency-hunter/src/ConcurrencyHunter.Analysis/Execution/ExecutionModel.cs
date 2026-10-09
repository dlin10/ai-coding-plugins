using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Execution;

public enum ExecutionKind
{
    Root,
    LazyConstruction,
    TypeInitializer,
    Spawn,
    TimerCallback,
    UnknownEnumeration,
    Startup,

    /// <summary>The run of a delegate an unresolved call was handed (R3, ADR 0011): one per delegate region, overlapping everything.</summary>
    UnknownDelegateCall
}

/// <summary>Where a spawned execution starts: the API (<c>Task.Run</c>, <c>async-call</c>, a timer type, ...), the symbol of the member
/// holding the site, and the site. A tail is the part of async work after its first await that its spawn does not wait for; it
/// starts at the synthetic point where the work returns.</summary>
/// <param name="Api">The API that starts the execution.</param>
/// <param name="Symbol">The symbol of the member holding the site.</param>
/// <param name="BodyId">The body holding the site.</param>
/// <param name="OperationId">The site's operation.</param>
/// <param name="IsTail">Whether the execution is the tail of async work.</param>
public sealed record SpawnOrigin(string Api, string Symbol, string BodyId, int OperationId, bool IsTail);

/// <summary>One execution of the scope: a root with its invocation policy, a lazily resolved construction, a type initializer that does
/// not run at startup, or an execution a spawn site or a timer starts inside its <see cref="ParentId"/> (<see cref="ExecutionModel.STARTUP"/>
/// for startup). <see cref="Subject"/> is the region or closed type a construction builds; <see cref="TreeRootId"/> is the root id, or
/// <c>startup</c>, or the construction execution id at the top of the tree.</summary>
/// <param name="Id">The execution's id.</param>
/// <param name="Kind">What kind of execution it is.</param>
/// <param name="Display">The execution's display name.</param>
/// <param name="Policy">How often the execution runs and whether its runs may overlap.</param>
/// <param name="RootId">The root the execution belongs to, or null.</param>
/// <param name="Subject">The region or closed type a construction builds, or null.</param>
public sealed record ExecutionInstance(string Id, ExecutionKind Kind, string Display, InvocationPolicy Policy, string? RootId, string? Subject)
{
    public string? ParentId { get; init; }
    public SpawnOrigin? Origin { get; init; }
    public string TreeRootId { get => field ?? RootId ?? Id; init; }

    /// <summary>A spawned execution whose parent runs at most once and whose site runs at most once per run of the parent, not reached
    /// again through its own chain; a tail has its work's value. Its handle then names one run.</summary>
    public bool SpawnedOnce { get; init; }

    public bool OverlapsItself =>
        Policy is { Multiplicity: Multiplicity.Repeated, SelfOverlap: SelfOverlap.MayOverlap } ||
        Policy.Multiplicity == Multiplicity.Unknown || Policy.SelfOverlap == SelfOverlap.Unknown;
}

public enum ExecutionEntryKind
{
    Root,
    Construction,
    TypeInitializer,
    Spawn,
    UnknownEnumeration,
    UnknownDelegateCall,

    /// <summary>A delegate a known call startup makes runs by its model's <c>startup</c> fate (R3).</summary>
    StartupDelegate
}

/// <summary>Which operations of a body an execution runs: all of them, those an async body runs before its first await, or those it
/// runs after one. An operation reachable both ways is in both.</summary>
public enum BodySegment
{
    Whole,
    Prefix,
    Tail
}

/// <summary>Where an execution starts: its root entry, a constructor chain it runs (starting inside the interval of the object
/// under construction), a type initializer (inside its closed type's static region), or the work of a spawn, in the
/// <see cref="Segment"/> of its body the execution runs.</summary>
/// <param name="InstanceId">The method instance the execution starts in.</param>
/// <param name="Kind">What kind of entry it is.</param>
/// <param name="IntervalObject">The object under construction or static region the entry starts inside, or null.</param>
public sealed record ExecutionEntry(string InstanceId, ExecutionEntryKind Kind, string? IntervalObject)
{
    public BodySegment Segment { get; init; } = BodySegment.Whole;
}

public enum OwnershipKind
{
    Owned,
    ThreadConfined,
    Escaped,
    Shared,
    Unknown
}

public sealed record RegionOwnership(OwnershipKind Kind, IReadOnlyList<string> Evidence);

/// <summary>An access on one region, with the executions that run it so (ADR 0017). A construction-local access touches the object its
/// enclosing construction builds, before the construction publishes it, and never pairs.</summary>
/// <param name="Executions">The executions running the access so.</param>
/// <param name="InstanceId">The method instance making the access.</param>
/// <param name="Access">The access from the instance's summary.</param>
/// <param name="RegionId">The region the access touches.</param>
/// <param name="IsConstructionLocal">Whether the access is construction-local.</param>
public sealed record CollectedAccess(ExecutionSet Executions, string InstanceId, SummaryAccess Access, string RegionId, bool IsConstructionLocal);

/// <summary>An access as one execution runs it on one region.</summary>
/// <param name="ExecutionId">The execution running the access.</param>
/// <param name="InstanceId">The method instance making the access.</param>
/// <param name="Access">The access from the instance's summary.</param>
/// <param name="RegionId">The region the access touches.</param>
/// <param name="IsConstructionLocal">Whether the access is construction-local.</param>
public sealed record ExecutionAccess(string ExecutionId, string InstanceId, SummaryAccess Access, string RegionId, bool IsConstructionLocal);

/// <summary>A node of the walk graph the executions share (ADR 0017): an instance, the segment of its body it runs and the key of the
/// site whose tail its awaited callees' tails join.</summary>
/// <param name="Instance">The instance.</param>
/// <param name="Segment">The segment of its body.</param>
/// <param name="Tail">The key of the site whose tail its awaited callees' tails join, or null.</param>
internal sealed record WalkNodeKey(string Instance, BodySegment Segment, string? Tail);

public sealed class ExecutionAnalysis
{
    private readonly IReadOnlyDictionary<string, ExecutionInstance> _executions;
    private readonly IReadOnlySet<string> _singleObjects;
    private readonly AsyncSegments? _segments;
    private readonly HappensBefore? _order;

    internal ExecutionAnalysis(IReadOnlyList<ExecutionInstance> executions, IReadOnlyList<CollectedAccess> accesses,
                               IReadOnlyDictionary<string, RegionOwnership> ownership, IReadOnlyDictionary<string, int> counters,
                               IReadOnlySet<string> singleObjects, IReadOnlySet<string> publishedObjects,
                               IReadOnlyDictionary<string, IReadOnlySet<string>> instanceExecutions,
                               IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> entries, AsyncSegments? segments = null,
                               IReadOnlyDictionary<string, IReadOnlyList<string>>? callPathPrefixes = null, HappensBefore? order = null)
    {
        _order = order;
        _segments = segments;
        CallPathPrefixes = callPathPrefixes ?? new Dictionary<string, IReadOnlyList<string>>();
        Entries = entries;
        Executions = executions;
        Accesses = accesses;
        Ownership = ownership;
        Counters = counters;
        PublishedObjects = publishedObjects;
        InstanceExecutions = instanceExecutions;
        _executions = executions.ToDictionary(execution => execution.Id, StringComparer.Ordinal);
        _singleObjects = singleObjects;
    }

    public IReadOnlyList<ExecutionInstance> Executions { get; }

    /// <summary>Each execution's entries, in order: a root's entry, then the constructor chains it runs, receiver first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> Entries { get; }

    /// <summary>Every access the executions run, per region, once with the executions that run it, by instance, operation, region and
    /// construction-local; startup's are kept like any other, and happens-before orders them before what runs after startup.</summary>
    public IReadOnlyList<CollectedAccess> Accesses { get; }

    /// <summary>Every collected access once for each execution that runs it: by execution, then in the order of <see cref="Accesses"/>.
    /// It repeats each access per execution, so it is for readers that go through one execution at a time on a small scope.</summary>
    public IEnumerable<ExecutionAccess> ExecutionAccesses()
    {
        foreach (var execution in Accesses.SelectMany(access => access.Executions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            foreach (var access in Accesses)
            {
                if (access.Executions.Contains(execution))
                    yield return new ExecutionAccess(execution, access.InstanceId, access.Access, access.RegionId, access.IsConstructionLocal);
            }
        }
    }

    public IReadOnlyDictionary<string, RegionOwnership> Ownership { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }

    /// <summary>How many times the walk took up a node of the graph the executions share, across all entries of this scope.</summary>
    public int WalkVisits { get; init; }

    internal IReadOnlyDictionary<WalkNodeKey, int> WalkNodeVisits { get; init; } = new Dictionary<WalkNodeKey, int>();

    internal IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>> Visits { get; init; } =
        new Dictionary<string, IReadOnlyList<ExecutionVisit>>(StringComparer.Ordinal);

    /// <summary>The objects under construction that their construction publishes.</summary>
    public IReadOnlySet<string> PublishedObjects { get; }

    /// <summary>The executions each instance runs in; <see cref="ExecutionModel.STARTUP"/> marks startup.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> InstanceExecutions { get; }

    public ExecutionInstance Execution(string id) => _executions[id];

    /// <summary>Two executions of one scope overlap; an execution overlaps itself only when its policy says so.</summary>
    /// <param name="first">The first execution's id.</param>
    /// <param name="second">The second execution's id.</param>
    public bool Overlaps(string first, string second) => first != second || _executions[first].OverlapsItself;

    /// <summary>Whether a happens-before path trusted for every instance it connects orders two accesses of different executions.</summary>
    /// <param name="first">The first access.</param>
    /// <param name="second">The second access.</param>
    public bool Ordered(Access first, Access second) => first.ExecutionId != second.ExecutionId && _order?.Ordered(first, second) == true;

    /// <summary>Whether a join of an instance comes before an access on every path of the access's execution (R6).</summary>
    /// <param name="joinInstance">The instance making the join.</param>
    /// <param name="joinOperation">The join operation.</param>
    /// <param name="access">The access.</param>
    public bool JoinDominates(string joinInstance, int joinOperation, Access access) =>
        _order?.JoinDominates(access.ExecutionId, joinInstance, joinOperation, access) ?? true;

    /// <summary>The distinct spawn and async call sites the executions reach, by source; timers are counted apart.</summary>
    public IReadOnlyList<SpawnSiteCoverage> SpawnSites => _order?.SpawnSites ?? [];

    /// <summary>The distinct join operations on spawned work whose handle identity is not proven in at least one context.</summary>
    public IReadOnlyList<OperationSite> UnprovenJoins => _order?.UnprovenJoins ?? [];

    /// <summary>The timer creation sites the executions reach, each with the counter of the widest kind a context creates it with.</summary>
    public IReadOnlyList<TimerSiteCoverage> TimerSites { get; init; } = [];

    /// <summary>The subscriptions and creations each timer callback execution runs for: what says which timer objects it may run on.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TimerCallbackSite>> TimerCallbackSites { get; init; } =
        new Dictionary<string, IReadOnlyList<TimerCallbackSite>>();

    /// <summary>Whether a region is provably one object per process, as a lock identity.</summary>
    /// <param name="regionId">The region.</param>
    public bool IsSingleObject(string regionId) => _singleObjects.Contains(regionId);

    /// <summary>For each spawned execution, the member symbols from its tree's root to its spawn site, ending with the site's
    /// <c>spawn:</c> or <c>timer-callback:</c> segment; the execution's own path follows them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CallPathPrefixes { get; }

    internal IReadOnlyDictionary<string, IReadOnlyList<SpawnSiteLocation>> SpawnSiteLocations { get; init; } =
        new Dictionary<string, IReadOnlyList<SpawnSiteLocation>>();

    /// <summary>The sites of the segments an access's call path passes: its execution's, when the path starts with that execution's
    /// prefix.</summary>
    /// <param name="executionId">The access's execution.</param>
    /// <param name="callPath">The access's call path.</param>
    public IReadOnlyList<SpawnSiteLocation> SpawnSitesOf(string executionId, IReadOnlyList<string> callPath) =>
        SpawnSiteLocations.TryGetValue(executionId, out var sites) && CallPathPrefixes.TryGetValue(executionId, out var prefix) &&
        callPath.Take(prefix.Count).SequenceEqual(prefix)
            ? sites
            : [];

    /// <summary>Whether a segment of a body runs an operation; a negative operation is the body's entry.</summary>
    /// <param name="bodyId">The body.</param>
    /// <param name="segment">The segment of the body.</param>
    /// <param name="operationId">The operation.</param>
    public bool Runs(string bodyId, BodySegment segment, int operationId) => _segments?.Runs(bodyId, segment, operationId) ?? true;

    /// <summary>The segment of its callee a call edge runs in the caller's execution, or null when that segment of the caller does
    /// not run the call.</summary>
    /// <param name="caller">The calling instance.</param>
    /// <param name="segment">The segment of the caller the execution runs.</param>
    /// <param name="edge">The call edge.</param>
    public BodySegment? Follow(MethodInstance caller, BodySegment segment, CallEdge edge) =>
        _segments is null ? BodySegment.Whole : _segments.Follow(caller, segment, edge);
}

/// <summary>
/// The segments of async bodies and how calls move between them. An operation reachable from the body's entry without passing an
/// await is in its prefix; one reachable from an await is in its tail. An async spawn edge runs its callee's prefix in the caller;
/// inside a prefix, an awaited call into an async body runs that body's prefix too, its tail joining the caller's tail.
/// </summary>
/// <param name="scope">The scope program whose bodies are read.</param>
/// <param name="heap">The solved heap, giving the async spawn sites and instances.</param>
public sealed class AsyncSegments(ScopeProgram scope, HeapSolution heap)
{
    private readonly Dictionary<string, (HashSet<int> Prefix, HashSet<int> Tail)> _bodies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Caller, int Operation), HashSet<string>> _asyncCallees =
        heap.AsyncSpawns.ToDictionary(site => (site.CallerInstance, site.OperationId), site => site.Callees.ToHashSet(StringComparer.Ordinal));

    public bool Runs(string bodyId, BodySegment segment, int operationId) => segment switch
    {
        BodySegment.Whole => true,
        BodySegment.Prefix => operationId < 0 || Of(bodyId).Prefix.Contains(operationId),
        _ => operationId >= 0 && Of(bodyId).Tail.Contains(operationId)
    };

    public bool IsAsyncSpawn(string caller, int operationId, string callee) =>
        _asyncCallees.TryGetValue((caller, operationId), out var callees) && callees.Contains(callee);

    /// <summary>An async body that is not an async iterator: one that starts work its caller may not wait for.</summary>
    /// <param name="bodyId">The body.</param>
    public bool IsAsync(string bodyId) => scope.Reachable.Bodies.TryGetValue(bodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false };

    public BodySegment? Follow(MethodInstance caller, BodySegment segment, CallEdge edge)
    {
        if (!Runs(caller.BodyId, segment, edge.OperationId))
            return null;
        if (edge.Reason == WholeProgram.CONSTRUCTION_REASON)
            return BodySegment.Whole;
        if (IsAsyncSpawn(caller.Id, edge.OperationId, edge.CalleeInstance))
            return BodySegment.Prefix;
        return segment == BodySegment.Prefix && IsAsync(heap.Instances[edge.CalleeInstance].BodyId) &&
               caller.Summary.Calls.Any(call => call.OperationId == edge.OperationId && call.IsAwaitedImmediately)
            ? BodySegment.Prefix
            : BodySegment.Whole;
    }

    /// <summary>A body's prefix and tail operations: a block entered before any await runs its operations up to and including its
    /// first await in the prefix and the rest in the tail; an exceptional edge may leave the block before or after that await.</summary>
    private (HashSet<int> Prefix, HashSet<int> Tail) Of(string bodyId)
    {
        if (_bodies.TryGetValue(bodyId, out var cached))
            return cached;

        var (prefix, tail) = (new HashSet<int>(), new HashSet<int>());
        _bodies.Add(bodyId, (prefix, tail));
        if (!scope.Reachable.Bodies.TryGetValue(bodyId, out var body) || body.Blocks.Count == 0)
            return (prefix, tail);

        var successors = body.Blocks.SelectMany(block => block.FlowPredecessors.Select(predecessor => (predecessor.BlockOrdinal, Successor: block.Ordinal,
                                                                                                         Exceptional: predecessor.EdgeKind == IrEdgeKind.Exceptional)))
                             .GroupBy(edge => edge.BlockOrdinal)
                             .ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<(int Block, bool AfterAwait)>();
        var pending = new Stack<(int Block, bool AfterAwait)>([(0, false)]);
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item))
                continue;

            var afterAwait = item.AfterAwait;
            foreach (var operation in body.Blocks[item.Block].Operations)
            {
                (afterAwait ? tail : prefix).Add(operation.Id);
                if (operation is IrAwaitOperation)
                    afterAwait = true;
            }

            foreach (var edge in successors.GetValueOrDefault(item.Block) ?? [])
            {
                pending.Push((edge.Successor, afterAwait));
                if (edge.Exceptional)
                    pending.Push((edge.Successor, item.AfterAwait));
            }
        }

        return (prefix, tail);
    }
}

internal sealed record ExecutionWalkOrder(bool Reverse = false, int? Seed = null);

/// <summary>
/// Executions, construction intervals, ownership and single-object facts over a solved heap. Roots are executions; a
/// construction runs in the execution its trigger runs in, a lazily resolved singleton and a non-startup type initializer are
/// at-most-once executions of their own, and everything a hosted service's construction reaches at startup is no execution.
/// The executions share one walk graph over the call edges from their entries, each node carrying the executions that reach it;
/// which objects' constructor chains run at a node is worked out per object (ADR 0006, ADR 0017).
/// </summary>
public static class ExecutionModel
{
    public const string STARTUP = "startup";

    public static ExecutionAnalysis Build(ScopeProgram scope, HeapSolution heap, CancellationToken cancellationToken) =>
        Build(scope, heap, cancellationToken, null);

    internal static ExecutionAnalysis Build(ScopeProgram scope, HeapSolution heap, CancellationToken cancellationToken, ExecutionWalkOrder? walkOrder)
    {
        var builder = new Builder(scope, heap, cancellationToken, walkOrder);
        try
        {
            return builder.Build();
        }
        catch (OperationCanceledException error)
        {
            throw new EngineStageCancelledException("executions", new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["walkVisits"] = builder.WalkVisits
            }, cancellationToken, error);
        }
    }

    /// <summary>The id of the unknown execution running a delegate region an unresolved call was handed (R3).</summary>
    /// <param name="delegateRegion">The delegate region.</param>
    public static string UnknownDelegateCallId(string delegateRegion) => $"unknown-delegate-call:{delegateRegion}";

    private sealed class Builder(ScopeProgram scope, HeapSolution heap, CancellationToken cancellationToken, ExecutionWalkOrder? walkOrder)
    {
        internal int WalkVisits { get; private set; }

        private void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();

        private readonly Dictionary<string, ExecutionInstance> _executions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _regionExecutions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CallEdge>> _edges = heap.ExecutionEdges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                                                                        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly ExecutionIds _ids = new();

        /// <summary>The executions each instance runs in, as bits over <see cref="_ids"/>.</summary>
        private readonly Dictionary<string, ulong[]> _instanceExecutions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DelegateHandoff> _handoffs = heap.DelegateHandoffs.ToDictionary(handoff => handoff.RegionId, StringComparer.Ordinal);
        private readonly Dictionary<string, string> _delegateDisplays = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StartupDelegate[]> _startupDelegates = heap.StartupDelegates.GroupBy(fated => fated.CallerInstance, StringComparer.Ordinal)
                                                                                        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        /// <summary>For each delegate a known call runs by a <c>startup</c> fate, the executions other than startup that make that call,
        /// with its site: each hands the delegate to its unknown call as an unresolved call would.</summary>
        private readonly Dictionary<string, HashSet<Handing>> _fateHandings = new(StringComparer.Ordinal);
        /// <summary>For each instance, the objects whose constructor chain holds it, as bits over object numbers.</summary>
        private readonly Dictionary<string, ulong[]> _chains = new(StringComparer.Ordinal);
        private readonly Dictionary<WalkNodeKey, WalkNode> _walkNodes = [];
        private readonly List<WalkNode> _nodeList = [];
        private int _walkPostorder;
        private readonly PriorityQueue<WalkNode, int> _walkQueue = new();

        /// <summary>Every entry the walk took: the execution's number, its node and the number of the object it starts inside, or -1.</summary>
        private readonly List<TakenEntry> _seeds = [];

        /// <summary>The execution each execution's awaited callees' tails join, by the key of the site that starts that tail.</summary>
        private readonly Dictionary<(string Execution, string Key), string> _tailOf = [];
        private readonly Dictionary<string, int> _objectIds = new(StringComparer.Ordinal);
        private readonly List<string> _objects = [];
        private IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>> _visitsByExecution = new Dictionary<string, IReadOnlyList<ExecutionVisit>>();
        private readonly Dictionary<string, List<ExecutionEntry>> _entries = new(StringComparer.Ordinal);
        private readonly Queue<(string Execution, ExecutionEntry Entry, string? Tail)> _pendingEntries = new();
        private readonly HashSet<(string Execution, ExecutionEntry Entry, string? Tail)> _entryWalks = [];
        private readonly Random? _walkRandom = walkOrder?.Seed is { } seed ? new Random(seed) : null;
        private readonly AsyncSegments _segments = new(scope, heap);
        private readonly Dictionary<string, SpawnSite[]> _spawns = heap.Spawns.GroupBy(site => site.CallerInstance, StringComparer.Ordinal)
                                                                       .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        private readonly Dictionary<string, TimerCallbackSite[]> _timers = heap.TimerCallbacks.GroupBy(site => site.CallerInstance, StringComparer.Ordinal)
                                                                               .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        private readonly Dictionary<(string Caller, int Operation), AsyncSpawnSite> _asyncSpawns =
            heap.AsyncSpawns.ToDictionary(site => (site.CallerInstance, site.OperationId));
        private IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>> _steps = new Dictionary<string, IReadOnlyList<ExecutionStep>>();
        private readonly HashSet<SpawnAnchor> _anchors = [];
        private readonly Dictionary<string, string> _tails = new(StringComparer.Ordinal);
        private readonly TimerSteps _timerSteps = new(heap);
        private readonly HashSet<TimerCallbackSite> _subscriptions = [];
        private readonly Dictionary<string, List<TimerCallbackSite>> _timerSites = new(StringComparer.Ordinal);
        private readonly Dictionary<(string BodyId, int OperationId), TimerKind> _timerKinds = [];
        private readonly Dictionary<string, HappensBefore.Flow> _flows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _childKeys = new(StringComparer.Ordinal);
        private readonly List<string> _childOrder = [];
        private readonly HashSet<string> _alwaysRepeated = new(StringComparer.Ordinal);
        private readonly HashSet<string> _recursive = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> _prefixes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<SpawnSiteLocation>> _spawnSites = new(StringComparer.Ordinal);
        private readonly Dictionary<(string BodyId, int Operation), bool> _inCycle = [];
        private readonly Dictionary<string, HashSet<string>> _sharedReach = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (HashSet<string> Stored, HashSet<string> Targets)[]> _stores = new(StringComparer.Ordinal);
        private Dictionary<string, string[]>? _callersOf;
        private readonly Dictionary<string, string> _constructed = heap.Constructions.SelectMany(construction => construction.ConstructorInstances
                                                                                         .Select(instance => (Instance: instance, construction.RegionId)))
                                                                       .GroupBy(item => item.Instance, StringComparer.Ordinal)
                                                                       .ToDictionary(group => group.Key, group => group.First().RegionId, StringComparer.Ordinal);

        internal ExecutionAnalysis Build()
        {
            CheckCancellation();
            foreach (var root in scope.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
            {
                CheckCancellation();
                Add(new ExecutionInstance(RootExecution(root.StableRootId), ExecutionKind.Root, root.Entry.Display, root.InvocationPolicy, root.StableRootId, null));
            }

            foreach (var group in heap.StartupDelegates.Select(fated => (fated.RegionId, Callee: fated.CalleeInstance))
                                      .Concat(heap.DelegateHandoffs.SelectMany(handoff => handoff.Callees.Select(callee => (handoff.RegionId, Callee: callee))))
                                      .GroupBy(item => item.RegionId, StringComparer.Ordinal))
            {
                CheckCancellation();
                _delegateDisplays.Add(group.Key, group.Select(item => DelegateName(item.Callee)).Order(StringComparer.Ordinal).First());
            }
            AssignRegionExecutions();
            var startupTypeInitializers = StartupTypeInitializers();

            foreach (var (rootId, instanceId) in heap.RootInstances.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CheckCancellation();
                Entry(RootExecution(rootId), new ExecutionEntry(instanceId, ExecutionEntryKind.Root, null));
            }

            foreach (var iterator in heap.IteratorObjects.Where(iterator => heap.UnknownIterators.Contains(iterator.RegionId))
                                          .OrderBy(iterator => iterator.RegionId, StringComparer.Ordinal))
            {
                CheckCancellation();
                var body = heap.Instances[iterator.Creation.CalleeInstance].BodyId;
                var display = $"unknown enumeration of {scope.Reachable.Bodies[body].MethodSymbol}";
                var id = Add(new ExecutionInstance($"unknown-enumeration:{iterator.RegionId}", ExecutionKind.UnknownEnumeration,
                                                   display, REPEATED, null, iterator.RegionId));
                Entry(id, new ExecutionEntry(iterator.Creation.CalleeInstance, ExecutionEntryKind.UnknownEnumeration, null));
            }

            // A library sequence that escaped is enumerated by an unknown execution as well: its delegates run there, and so does what
            // enumerating its sources runs (R5).
            foreach (var sequence in heap.LibrarySequences.Values.Where(sequence => !sequence.IsGrouping && heap.UnknownIterators.Contains(sequence.RegionId))
                                         .OrderBy(sequence => sequence.RegionId, StringComparer.Ordinal))
            {
                CheckCancellation();
                var id = Add(new ExecutionInstance($"unknown-enumeration:{sequence.RegionId}", ExecutionKind.UnknownEnumeration,
                                                   $"unknown enumeration of {heap.Regions[sequence.RegionId].Display}", REPEATED, null, sequence.RegionId));
                foreach (var instance in sequence.EnumerationInstances)
                {
                    CheckCancellation();
                    Entry(id, new ExecutionEntry(instance, ExecutionEntryKind.UnknownEnumeration, null));
                }
            }

            // A delegate an unresolved call was handed runs whenever that call likes: one execution per delegate, which nothing orders
            // but the end of startup when only executions after startup hand it over, and which holds no lock on entry (R3, ADR 0011).
            foreach (var handoff in heap.DelegateHandoffs.Where(handoff => handoff.Callees.Count != 0))
            {
                CheckCancellation();
                var id = Add(new ExecutionInstance(UnknownDelegateCallId(handoff.RegionId), ExecutionKind.UnknownDelegateCall,
                                                   $"unknown call of the delegate {DelegateDisplay(handoff.RegionId)}", REPEATED, null, handoff.RegionId));
                foreach (var callee in handoff.Callees)
                {
                    CheckCancellation();
                    Entry(id, new ExecutionEntry(callee, ExecutionEntryKind.UnknownDelegateCall, null));
                }
            }

            foreach (var construction in heap.Constructions.OrderBy(construction => heap.Regions[construction.RegionId].Kind != HeapRegionKind.Receiver)
                                                           .ThenBy(construction => construction.RegionId, StringComparer.Ordinal))
            {
                CheckCancellation();
                foreach (var execution in _regionExecutions.GetValueOrDefault(construction.RegionId) ?? [])
                {
                    CheckCancellation();
                    foreach (var instance in construction.ConstructorInstances)
                    {
                        CheckCancellation();
                        Entry(execution, new ExecutionEntry(instance, ExecutionEntryKind.Construction, construction.RegionId));
                    }
                }
            }

            foreach (var initializer in heap.TypeInitializers.Where(initializer => heap.Instances.ContainsKey(initializer.InstanceId)))
            {
                CheckCancellation();
                var execution = startupTypeInitializers.Contains(initializer.TypeKey)
                    ? STARTUP
                    : Add(new ExecutionInstance($"type-initializer:{initializer.TypeKey}", ExecutionKind.TypeInitializer,
                                                $"type initializer of {WholeProgram.DisplayType(initializer.TypeKey)}", AT_MOST_ONCE, null,
                                                initializer.TypeKey));
                Entry(execution, new ExecutionEntry(initializer.InstanceId, ExecutionEntryKind.TypeInitializer, $"static:{initializer.TypeKey}"));
            }

            do
            {
                CheckCancellation();
                // Every pending entry is taken before the walk passes executions on, so executions that reach a node together arrive
                // there in one visit; the entries the walk makes are taken in the next round.
                while (_pendingEntries.Count != 0)
                {
                    var taken = Permute(_pendingEntries).ToArray();
                    _pendingEntries.Clear();
                    foreach (var (execution, entry, tail) in taken)
                        Seed(execution, entry, tail);
                    while (_walkQueue.TryDequeue(out var node, out _))
                    {
                        CheckCancellation();
                        Process(node);
                    }
                }
                foreach (var site in _subscriptions.ToArray())
                {
                    CheckCancellation();
                    Subscribed(site);
                }
            }
            while (_pendingEntries.Count != 0);

            Constructions();
            if (_entries.ContainsKey(STARTUP))
                Add(new ExecutionInstance(STARTUP, ExecutionKind.Startup, "host startup", AT_MOST_ONCE, null, null) { TreeRootId = STARTUP });
            CanonicalizeWalk();
            ReadExecutionLists();

            FinishSpawnedExecutions();
            FinishUnknownDelegateCalls();

            // Ownership reads which executions reach a region, which publication does not change; publication in turn asks whether
            // the object handed to an unresolved call is shared (R2).
            var ownership = Ownership(Collect(new HashSet<string>(StringComparer.Ordinal)));
            var published = Published(ownership);
            var accesses = Collect(published);
            var executions = _executions.Values.OrderBy(execution => execution.Id, StringComparer.Ordinal).ToArray();
            var entries = _entries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExecutionEntry>)pair.Value, StringComparer.Ordinal);
            var order = new HappensBefore(scope, heap, _segments, executions.ToDictionary(execution => execution.Id, StringComparer.Ordinal), entries,
                                          _visitsByExecution, _steps, _anchors.ToArray(), _tails, DuringStartup(), cancellationToken);
            var timerSites = _timerKinds.Select(pair => new TimerSiteCoverage(new OperationSite(pair.Key.BodyId, pair.Key.OperationId), Counter(pair.Value)))
                                        .OrderBy(site => site.Site.BodyId, StringComparer.Ordinal)
                                        .ThenBy(site => site.Site.OperationId)
                                        .ToArray();
            var counters = OrderingCounters.TIMER_KINDS.ToDictionary(counter => counter,
                                                                     counter => timerSites.Count(site => site.Counter == counter),
                                                                     StringComparer.Ordinal);
            return new ExecutionAnalysis(
                executions,
                accesses,
                ownership,
                counters,
                SingleObjects(),
                published,
                _instanceExecutions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                   .ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)_ids.Set(pair.Value), StringComparer.Ordinal),
                entries,
                _segments,
                _prefixes,
                order)
            {
                WalkVisits = WalkVisits,
                WalkNodeVisits = _walkNodes.ToDictionary(pair => pair.Key, pair => pair.Value.Visits),
                Visits = _visitsByExecution,
                SpawnSiteLocations = _spawnSites,
                TimerSites = timerSites,
                TimerCallbackSites = _timerSites.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TimerCallbackSite>)pair.Value, StringComparer.Ordinal)
            };
        }

        /// <summary>The counter a timer creation site of one kind is counted in.</summary>
        /// <param name="kind">The timer kind.</param>
        private static string Counter(TimerKind kind) => kind switch
        {
            TimerKind.Disabled => OrderingCounters.TIMERS_DISABLED,
            TimerKind.OneShot => OrderingCounters.TIMERS_ONE_SHOT,
            _ => OrderingCounters.TIMERS_PERIODIC
        };

        /// <summary>Adds an entry once and walks each distinct tail; a prefix entry names the site whose tail its awaited callees' tails
        /// join, and <see cref="_tailOf"/> names that tail's execution for the execution entering.</summary>
        /// <param name="execution">The execution entering the body.</param>
        /// <param name="entry">The body segment and construction interval on entry.</param>
        /// <param name="tail">The key of the site whose tail its awaited callees' tails join.</param>
        private void Entry(string execution, ExecutionEntry entry, string? tail = null)
        {
            if (!_entries.TryGetValue(execution, out var entries))
                _entries.Add(execution, entries = []);
            if (!entries.Contains(entry))
                entries.Add(entry);
            if (_entryWalks.Add((execution, entry, tail)))
                _pendingEntries.Enqueue((execution, entry, tail));
        }

        private IEnumerable<T> Permute<T>(IEnumerable<T> items)
        {
            if (walkOrder is null)
                return items;
            var result = items.ToArray();
            if (_walkRandom is not null)
                _walkRandom.Shuffle(result);
            else if (walkOrder.Reverse)
                Array.Reverse(result);
            return result;
        }

        private void CanonicalizeWalk()
        {
            foreach (var entries in _entries.Values)
                OrderList(entries, entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.IntervalObject, StringComparer.Ordinal)
                                          .ThenBy(entry => entry.InstanceId, StringComparer.Ordinal).ThenBy(entry => entry.Segment));
            foreach (var set in _regionExecutions.Values)
                OrderSet(set, set.Order(StringComparer.Ordinal));
            OrderDictionary(_regionExecutions);
            foreach (var handings in _fateHandings.Values)
                OrderSet(handings, handings.OrderBy(handing => handing.Execution, StringComparer.Ordinal)
                                          .ThenBy(handing => handing.BodyId, StringComparer.Ordinal).ThenBy(handing => handing.OperationId));
            OrderSet(_anchors, _anchors.OrderBy(anchor => anchor.ExecutionId, StringComparer.Ordinal)
                                      .ThenBy(anchor => anchor.CallerInstance, StringComparer.Ordinal).ThenBy(anchor => anchor.OperationId)
                                      .ThenBy(anchor => anchor.ChildId, StringComparer.Ordinal).ThenBy(anchor => anchor.Kind));
            foreach (var sites in _timerSites.Values)
                OrderList(sites, sites.OrderBy(site => site.CallerInstance, StringComparer.Ordinal).ThenBy(site => site.OperationId));
            OrderDictionary(_entries);
            OrderDictionary(_fateHandings);
            OrderDictionary(_timerSites);
            OrderDictionary(_executions);
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            int Depth(string id)
            {
                if (depths.TryGetValue(id, out var depth))
                    return depth;
                return depths[id] = _executions[id].ParentId is { } parent && parent != STARTUP ? Depth(parent) + 1 : 0;
            }
            OrderList(_childOrder, _childOrder.OrderBy(Depth).ThenBy(id => id, StringComparer.Ordinal));
        }

        private static void OrderList<T>(List<T> list, IEnumerable<T> ordered)
        {
            var items = ordered.ToArray();
            list.Clear();
            list.AddRange(items);
        }

        private static void OrderSet<T>(HashSet<T> set, IEnumerable<T> ordered)
        {
            var items = ordered.ToArray();
            set.Clear();
            set.UnionWith(items);
        }

        private static void OrderDictionary<T>(Dictionary<string, T> dictionary)
        {
            var items = dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            dictionary.Clear();
            foreach (var (key, value) in items)
                dictionary.Add(key, value);
        }

        private static readonly InvocationPolicy AT_MOST_ONCE = new(Multiplicity.AtMostOnce, SelfOverlap.Serialized, "");
        private static readonly InvocationPolicy REPEATED = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, "");
        private static readonly InvocationPolicy SERIALIZED = new(Multiplicity.Repeated, SelfOverlap.Serialized, "");

        private static string Api(IrSpawnKind kind) => kind switch
        {
            IrSpawnKind.TaskRun => "Task.Run",
            IrSpawnKind.StartNew => "TaskFactory.StartNew",
            IrSpawnKind.ContinueWith => "Task.ContinueWith",
            IrSpawnKind.QueueUserWorkItem => "ThreadPool.QueueUserWorkItem",
            IrSpawnKind.UnsafeQueueUserWorkItem => "ThreadPool.UnsafeQueueUserWorkItem",
            IrSpawnKind.ThreadStart => "Thread.Start",
            IrSpawnKind.ParallelFor => "Parallel.For",
            IrSpawnKind.ParallelForEach => "Parallel.ForEach",
            IrSpawnKind.ParallelForEachAsync => "Parallel.ForEachAsync",
            IrSpawnKind.AsyncCall => "async-call",
            IrSpawnKind.AsyncVoid => "async-void",
            _ => "unrecognized"
        };

        private string Symbol(string bodyId) => scope.Reachable.Bodies.TryGetValue(bodyId, out var body) ? body.OwnerSymbol : bodyId;

        /// <summary>The execution a spawn site of <paramref name="parent"/> starts, created once per parent and site; a site reached again
        /// inside the chain of executions it started reuses that execution, which then overlaps itself.</summary>
        /// <param name="key">The key.</param>
        /// <param name="create">The create.</param>
        /// <param name="alwaysRepeated">The alwaysRepeated.</param>
        /// <param name="parent">The parent.</param>
        private string Child(string parent, string key, Func<string, ExecutionInstance> create, bool alwaysRepeated)
        {
            for (var current = parent; current is not null && current != STARTUP; current = _executions[current].ParentId)
            {
                CheckCancellation();
                if (_childKeys.GetValueOrDefault(current) == key)
                {
                    _recursive.Add(current);
                    return current;
                }
            }

            var id = $"{parent}>{key}";
            if (_executions.TryAdd(id, create(id)))
            {
                _childKeys.Add(id, key);
                _childOrder.Add(id);
                if (alwaysRepeated)
                    _alwaysRepeated.Add(id);
            }

            return id;
        }

        private string Spawned(string parent, string key, ExecutionKind kind, string display, SpawnOrigin origin, bool alwaysRepeated) =>
            Child(parent, key, id => new ExecutionInstance(id, kind, display, REPEATED, null, null) { ParentId = parent, Origin = origin }, alwaysRepeated);

        private void Spawn(string parent, MethodInstance instance, SpawnSite site)
        {
            var api = Api(site.Kind);
            var symbol = Symbol(instance.BodyId);
            var child = Spawned(parent, $"spawn:{instance.BodyId}#{site.OperationId}", ExecutionKind.Spawn, $"{api} in {symbol}",
                                new SpawnOrigin(api, symbol, instance.BodyId, site.OperationId, false),
                                site.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach or IrSpawnKind.ParallelForEachAsync or IrSpawnKind.Unrecognized);
            _anchors.Add(new SpawnAnchor(parent, instance.Id, site.OperationId, child, SpawnAnchorKind.Spawn));
            var awaitsTask = instance.Summary.Spawns.Any(spawn => spawn.OperationId == site.OperationId && spawn.AwaitsWorkTask);
            foreach (var callee in site.Callees)
            {
                CheckCancellation();
                EnterWork(child, callee.InstanceId, awaitsTask);
            }
        }

        /// <summary>Starts the callbacks an <c>Elapsed</c> subscription runs in each execution that created its timer, at the creation site;
        /// a timer whose creation the heap does not locate, or one that may also be a timer of unknown origin, keeps the subscription as a
        /// site of its own, since that timer may have been created in another execution.</summary>
        /// <param name="site">The site.</param>
        private void Subscribed(TimerCallbackSite site)
        {
            var subscriber = heap.Instances[site.CallerInstance];
            foreach (var region in site.TimersKnown ? site.Timers.DefaultIfEmpty("") : site.Timers.Append(""))
            {
                CheckCancellation();
                if (heap.Regions.GetValueOrDefault(region) is { Kind: HeapRegionKind.Allocation, SiteBodyId: { } bodyId, SiteOperationId: { } operationId } timer)
                {
                    foreach (var creator in heap.Instances.Values.Where(instance => instance.BodyId == bodyId && instance.Context == timer.Context))
                    {
                        CheckCancellation();
                        foreach (var execution in ExecutionsOf(creator.Id))
                        {
                            CheckCancellation();
                            Timer(execution, creator, site, operationId);
                        }
                    }
                }
                else
                {
                    foreach (var execution in ExecutionsOf(subscriber.Id))
                    {
                        CheckCancellation();
                        Timer(execution, subscriber, site, site.OperationId);
                    }
                }
            }
        }

        /// <summary>Starts the callback execution of a timer site at the operation that creates its timer (or subscribes to it), unless every
        /// timer the site names is disabled, which is only counted; a site that may run a timer of unknown origin runs its callback.</summary>
        /// <param name="parent">The parent.</param>
        /// <param name="instance">The instance.</param>
        /// <param name="site">The site.</param>
        /// <param name="operationId">The operationId.</param>
        private void Timer(string parent, MethodInstance instance, TimerCallbackSite site, int operationId)
        {
            if (site.TimersKnown && site.Timers.Count != 0 && site.Timers.All(region => site.Action == IrTimerAction.Create
                                                              ? _timerSteps.ThreadingKind(region) == TimerKind.Disabled
                                                              : _timerSteps.Activations(region).Count == 0 && !_timerSteps.MayBeActivatedElsewhere))
            {
                foreach (var region in site.Timers)
                {
                    CheckCancellation();
                    CountTimer(site, region, TimerKind.Disabled);
                }
                return;
            }

            var api = site.Action == IrTimerAction.ElapsedSubscribe ? "System.Timers.Timer" : "System.Threading.Timer";
            var symbol = Symbol(instance.BodyId);
            var child = Spawned(parent, $"timer:{instance.BodyId}#{operationId}", ExecutionKind.TimerCallback, $"{api} callback created in {symbol}",
                                new SpawnOrigin(api, symbol, instance.BodyId, operationId, false), alwaysRepeated: false);
            _anchors.Add(new SpawnAnchor(parent, instance.Id, operationId, child, SpawnAnchorKind.Timer));
            if (!_timerSites.TryGetValue(child, out var sites))
                _timerSites.Add(child, sites = []);
            if (!sites.Contains(site))
                sites.Add(site);
            foreach (var callee in site.Callees)
            {
                CheckCancellation();
                EnterWork(child, callee, awaitsTask: false);
            }
        }

        /// <summary>Enters spawned work: whole, or, for async work its spawn does not wait for, its prefix here and its tail in a child
        /// execution that starts where the work returns.</summary>
        /// <param name="execution">The spawned execution entering the work.</param>
        /// <param name="calleeId">The work's instance.</param>
        /// <param name="awaitsTask">Whether the spawn waits for the work's task, so async work is entered whole.</param>
        private void EnterWork(string execution, string calleeId, bool awaitsTask)
        {
            if (!_segments.IsAsync(heap.Instances[calleeId].BodyId) || awaitsTask)
            {
                Entry(execution, new ExecutionEntry(calleeId, ExecutionEntryKind.Spawn, null));
                return;
            }

            var spawned = _executions[execution];
            var origin = spawned.Origin!;
            var key = $"tail:{origin.BodyId}#{origin.OperationId}";
            var tail = Spawned(execution, key, spawned.Kind, $"{spawned.Display} after its first await", origin with { IsTail = true }, alwaysRepeated: false);
            if (tail != execution)
                _tails.TryAdd(execution, tail);
            _tailOf[(execution, key)] = tail;
            Entry(execution, new ExecutionEntry(calleeId, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Prefix }, key);
            Entry(tail, new ExecutionEntry(calleeId, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Tail });
        }

        /// <summary>The execution an async spawn edge starts: its callee's tail, from the synthetic point where the call returns.</summary>
        /// <param name="parent">The execution making the call.</param>
        /// <param name="caller">The calling instance.</param>
        /// <param name="operationId">The call operation.</param>
        private string AsyncChild(string parent, MethodInstance caller, int operationId)
        {
            var site = _asyncSpawns[(caller.Id, operationId)];
            var api = Api(site.Kind);
            var symbol = Symbol(caller.BodyId);
            var key = AsyncKey(caller.BodyId, operationId);
            var child = Spawned(parent, key, ExecutionKind.Spawn, $"{api} in {symbol}", new SpawnOrigin(api, symbol, caller.BodyId, operationId, false),
                                alwaysRepeated: false);
            _anchors.Add(new SpawnAnchor(parent, caller.Id, operationId, child, SpawnAnchorKind.AsyncReturn));
            _tailOf[(parent, key)] = child;
            return child;
        }

        /// <summary>The key of the execution an async spawn edge starts, below its parent.</summary>
        /// <param name="bodyId">The calling body.</param>
        /// <param name="operationId">The call operation.</param>
        private static string AsyncKey(string bodyId, int operationId) => $"async:{bodyId}#{operationId}";

        /// <summary>Sets each spawned execution's policy, tree root and call-path prefix, parents first. A spawned execution runs at most once
        /// without overlapping itself only when its parent does and its site runs at most once per run of the parent; a tail has its
        /// work's policy; parallel bodies, unrecognized spawns, periodic timer callbacks and recursive spawns overlap themselves, and a
        /// one-shot timer's callback runs at most once only when its timer is created once.</summary>
        private void FinishSpawnedExecutions()
        {
            foreach (var id in _childOrder)
            {
                CheckCancellation();
                var child = _executions[id];
                var parentId = child.ParentId!;
                var parent = parentId == STARTUP ? null : _executions[parentId];
                var origin = child.Origin!;
                var parentOnce = parent is null || parent.Policy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized };
                var once = origin.IsTail ? parent!.SpawnedOnce
                    : !_recursive.Contains(id) && parentOnce && SiteOnce(parentId, origin.BodyId, origin.OperationId) && OriginKnown(id);
                var timerKind = child.Kind == ExecutionKind.TimerCallback && !origin.IsTail ? CallbackKind(id) : (TimerKind?)null;
                var policy = _recursive.Contains(id) || _alwaysRepeated.Contains(id) || timerKind == TimerKind.Periodic ? REPEATED
                    : origin.IsTail ? parent!.Policy
                    : once ? AT_MOST_ONCE
                    : REPEATED;
                var segment = origin.Api.StartsWith("System.", StringComparison.Ordinal)
                    ? $"timer-callback:{origin.Api}@{origin.Symbol}"
                    : $"spawn:{origin.Api}@{origin.Symbol}";
                _prefixes[id] = origin.IsTail
                    ? _prefixes[parentId]
                    : [.. _prefixes.GetValueOrDefault(parentId) ?? [], .. PathSymbols(parentId, origin.BodyId), segment];
                _spawnSites[id] = origin.IsTail
                    ? _spawnSites[parentId]
                    : [.. _spawnSites.GetValueOrDefault(parentId) ?? [], new SpawnSiteLocation(segment, SiteSource(origin))];
                _executions[id] = child with { Policy = policy, TreeRootId = parent?.TreeRootId ?? STARTUP, SpawnedOnce = once };
            }
        }

        /// <summary>Sets the policy of each unknown call of a delegate, once the executions that hand it over have theirs (R3). The call
        /// may run the delegate any number of times; it runs it one call at a time only when the delegate is handed at one site, in the
        /// one execution that runs that site, which runs once and runs the site once per run, as a spawn is. Handed anywhere else, it may
        /// overlap itself.</summary>
        private void FinishUnknownDelegateCalls()
        {
            foreach (var region in HandedRegions())
            {
                CheckCancellation();
                if (!_executions.TryGetValue(UnknownDelegateCallId(region), out var execution))
                    continue;
                var handings = Handings(region).Distinct().ToArray();
                if (handings is [var (handing, bodyId, operationId)] && RunsOnce(handing) && SiteOnce(handing, bodyId, operationId))
                    _executions[execution.Id] = execution with { Policy = SERIALIZED };
            }
        }

        /// <summary>The delegate regions an unknown call of a delegate runs: those handed to unresolved calls or run by a known call's
        /// <c>unknown-execution</c> fate, and those a call outside startup runs by a <c>startup</c> fate.</summary>
        private IEnumerable<string> HandedRegions() => _handoffs.Keys.Concat(_fateHandings.Keys).Distinct(StringComparer.Ordinal);

        /// <summary>The executions that hand a delegate region to its unknown call, each with the site it hands it at.</summary>
        /// <param name="region">The delegate region.</param>
        private IEnumerable<Handing> Handings(string region) =>
            (_handoffs.TryGetValue(region, out var handoff)
                ? handoff.Sites.SelectMany(site => ExecutionsOf(site.CallerInstance)
                                               .Select(handing => new Handing(handing, heap.Instances[site.CallerInstance].BodyId, site.OperationId)))
                : [])
            .Concat(_fateHandings.GetValueOrDefault(region) ?? []);

        /// <summary>An execution handing a delegate to its unknown call, with the site it hands it at.</summary>
        /// <param name="Execution">The execution.</param>
        /// <param name="BodyId">The body of the site.</param>
        /// <param name="OperationId">The operation of the site.</param>
        private sealed record Handing(string Execution, string BodyId, int OperationId);

        /// <summary>How an unknown call of a delegate names what it runs: a lambda, which has no name of its own, by the member it is
        /// written in and its place, anything else by its method.</summary>
        /// <param name="calleeInstance">The delegate's callee in the heap.</param>
        private string DelegateName(string calleeInstance)
        {
            var body = scope.Reachable.Bodies[heap.Instances[calleeInstance].BodyId];
            return body.Kind == IrBodyKind.Lambda
                ? $"lambda in {body.OwnerSymbol}{At(body.Blocks.SelectMany(block => block.Operations).FirstOrDefault()?.Provenance.Span)}"
                : body.MethodSymbol;
        }

        private string DelegateDisplay(string region) => _delegateDisplays[region];

        /// <summary>A delegate a known call runs by a <c>startup</c> fate runs in startup when startup makes the call. Any other
        /// execution making it does so once startup has ended for it, so the delegate runs, for that call, in an unknown execution, as an
        /// <c>unknown-execution</c> fate's does (R3).</summary>
        /// <param name="execution">The execution making the call.</param>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="fated">The delegate and call site the heap resolved.</param>
        private void StartupDelegate(string execution, MethodInstance caller, StartupDelegate fated)
        {
            if (execution == STARTUP)
            {
                Entry(STARTUP, new ExecutionEntry(fated.CalleeInstance, ExecutionEntryKind.StartupDelegate, null));
                return;
            }

            var id = Add(new ExecutionInstance(UnknownDelegateCallId(fated.RegionId), ExecutionKind.UnknownDelegateCall,
                                               $"unknown call of the delegate {DelegateDisplay(fated.RegionId)}", REPEATED, null, fated.RegionId));
            if (!_fateHandings.TryGetValue(fated.RegionId, out var handings))
                _fateHandings.Add(fated.RegionId, handings = []);
            handings.Add(new Handing(execution, caller.BodyId, fated.OperationId));
            Entry(id, new ExecutionEntry(fated.CalleeInstance, ExecutionEntryKind.UnknownDelegateCall, null));
        }

        /// <summary>The executions that may start before startup ends: startup and what it starts, and an unknown execution with what it
        /// starts when one of them hands it its delegate or creates its iterator, since the call may run the delegate, and whoever holds
        /// the iterator may enumerate it, at once. One that no execution hands over or creates may run whenever. Everything else starts
        /// after startup: a root, a construction or type initializer startup does not run, what they start, and an unknown execution
        /// only they hand over or create (issue #123).</summary>
        private HashSet<string> DuringStartup()
        {
            var creators = heap.IteratorObjects.Select(iterator => (iterator.RegionId, Creator: iterator.Creation.CallerInstance))
                               .Concat(heap.LibrarySequences.Values.Where(sequence => !sequence.IsGrouping)
                                           .Select(sequence => (sequence.RegionId, Creator: sequence.CreatorInstance)))
                               .ToDictionary(item => item.RegionId, item => item.Creator, StringComparer.Ordinal);
            var starters = _executions.Values.Where(execution => execution.Kind is ExecutionKind.UnknownDelegateCall or ExecutionKind.UnknownEnumeration)
                                      .ToDictionary(execution => execution.Id,
                                                    execution => (execution.Kind == ExecutionKind.UnknownDelegateCall
                                                                      ? Handings(execution.Subject!).Select(handing => handing.Execution)
                                                                      : ExecutionsOf(creators[execution.Subject!]))
                                                                 .ToHashSet(StringComparer.Ordinal),
                                                    StringComparer.Ordinal);

            var during = _executions.Values.Where(execution => execution.TreeRootId == STARTUP).Select(execution => execution.Id)
                                    .ToHashSet(StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                CheckCancellation();
                changed = false;
                foreach (var execution in _executions.Values.Where(execution => !during.Contains(execution.Id)))
                {
                    CheckCancellation();
                    if (starters.TryGetValue(execution.TreeRootId, out var by) && (by.Count == 0 || by.Overlaps(during)))
                        changed |= during.Add(execution.Id);
                }
            }

            return during;
        }

        /// <summary>Whether every site a callback runs for names the timer it runs on: one that may run a timer of unknown origin may have
        /// been created in another execution, so neither the site it starts at nor the execution it starts in is proven.</summary>
        /// <param name="callback">The callback execution.</param>
        private bool OriginKnown(string callback) => (_timerSites.GetValueOrDefault(callback) ?? []).All(site => site.TimersKnown);

        /// <summary>The broadest kind among the timers a callback execution's sites subscribe to, each counted at its creation site; a site
        /// that may also run a timer of unknown origin counts as periodic, the broadest kind that timer may have.</summary>
        /// <param name="callback">The callback.</param>
        private TimerKind CallbackKind(string callback)
        {
            var kind = TimerKind.Disabled;
            var any = false;
            foreach (var site in _timerSites[callback])
            {
                CheckCancellation();
                foreach (var region in site.Timers)
                {
                    CheckCancellation();
                    var regionKind = !site.TimersKnown ? TimerKind.Periodic
                        : site.Action == IrTimerAction.Create ? _timerSteps.ThreadingKind(region)
                        : SubscribedOnce(site) ? TimersKind(region)
                        : TimerKind.Periodic;
                    CountTimer(site, region, regionKind);
                    kind = regionKind > kind ? regionKind : kind;
                    any = true;
                }
            }

            return any ? kind : TimerKind.Periodic;
        }

        /// <summary>Whether an <c>Elapsed</c> subscription runs once in the one execution that runs it; otherwise the handler may be attached
        /// more than once, and its callbacks overlap as periodic ones do.</summary>
        /// <param name="site">The subscription site.</param>
        private bool SubscribedOnce(TimerCallbackSite site) =>
            ExecutionsOf(site.CallerInstance) is { Count: 1 } executions && RunsOnce(executions.First()) &&
            SiteOnce(executions.First(), heap.Instances[site.CallerInstance].BodyId, site.OperationId);

        /// <summary>
        /// A <c>System.Timers.Timer</c>: disabled without an activation; one-shot only when every <c>AutoReset</c> assignment reaching it
        /// assigns <c>false</c> and one of them precedes its one activation on every path, and that activation runs once in an execution that
        /// runs once and is no callback of this timer; periodic otherwise, as <c>AutoReset</c> defaults to <c>true</c>.
        /// </summary>
        /// <param name="region">The timer's region.</param>
        private TimerKind TimersKind(string region)
        {
            var activations = _timerSteps.Activations(region);
            var resets = _timerSteps.AutoResets(region);
            if (activations.Count == 0 && !_timerSteps.MayBeActivatedElsewhere)
                return TimerKind.Disabled;
            if (resets.Count == 0 || resets.Any(reset => !reset.Off) || _timerSteps.MayBeActivatedElsewhere || _timerSteps.MayBeResetElsewhere)
                return TimerKind.Periodic;

            var runs = activations.SelectMany(step => ExecutionsOf(step.InstanceId)
                                                  .Select(execution => (Execution: execution, step.BodyId, step.OperationId)))
                                  .Distinct()
                                  .ToArray();
            if (runs is not [var (execution, bodyId, operationId)] || !RunsOnce(execution) || !SiteOnce(execution, bodyId, operationId) ||
                IsCallbackOf(execution, region))
            {
                return TimerKind.Periodic;
            }

            var flow = FlowOf(execution);
            var cuts = resets.Select(reset => new HappensBefore.PointKey(reset.Step.InstanceId, reset.Step.OperationId, false)).ToArray();
            return activations.All(step => flow.Dominates(cuts, new HappensBefore.PointKey(step.InstanceId, step.OperationId, false)))
                ? TimerKind.OneShot
                : TimerKind.Periodic;
        }

        private bool RunsOnce(string execution) =>
            execution == STARTUP || _executions[execution].Policy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized };

        /// <summary>Whether an execution is a callback of the timer, or runs inside one.</summary>
        /// <param name="execution">The execution.</param>
        /// <param name="region">The region.</param>
        private bool IsCallbackOf(string execution, string region)
        {
            for (string? current = execution; current is not null && current != STARTUP; current = _executions[current].ParentId)
            {
                CheckCancellation();
                if (_timerSites.TryGetValue(current, out var sites) && sites.Any(site => site.Timers.Contains(region)))
                    return true;
            }

            return false;
        }

        private HappensBefore.Flow FlowOf(string execution)
        {
            if (_flows.TryGetValue(execution, out var flow))
                return flow;
            var visits = _visitsByExecution.GetValueOrDefault(execution) ?? [];
            flow = new HappensBefore.Flow(scope, heap, _segments, _entries.GetValueOrDefault(execution) ?? [], visits,
                                          _steps.GetValueOrDefault(execution)?.ToArray() ?? [], new HashSet<(string, int)>(), CheckCancellation);
            _flows.Add(execution, flow);
            return flow;
        }

        /// <summary>Counts a timer at its creation site, in the broadest kind any of its contexts gives it.</summary>
        /// <param name="site">The callback site, whose own operation counts when the timer's creation is not located.</param>
        /// <param name="region">The timer's region.</param>
        /// <param name="kind">The kind this context gives the timer.</param>
        private void CountTimer(TimerCallbackSite site, string region, TimerKind kind)
        {
            var key = heap.Regions[region] is { SiteBodyId: { } bodyId, SiteOperationId: { } operationId }
                ? (bodyId, operationId)
                : (heap.Instances[site.CallerInstance].BodyId, site.OperationId);
            _timerKinds[key] = _timerKinds.TryGetValue(key, out var known) && known > kind ? known : kind;
        }

        /// <summary>The source of the operation a spawned execution starts at.</summary>
        /// <param name="origin">The spawned execution's origin.</param>
        private Analysis.SourceSpan SiteSource(SpawnOrigin origin) =>
            scope.Reachable.Bodies[origin.BodyId].Blocks.SelectMany(block => block.Operations)
                 .First(operation => operation.Id == origin.OperationId).Provenance.Span;

        /// <summary>Whether a site runs at most once per run of an execution: no loop holds it, and the execution's call graph has at most one
        /// path to its body, none through a recursion or a call inside a loop.</summary>
        /// <param name="execution">The execution.</param>
        /// <param name="bodyId">The bodyId.</param>
        /// <param name="operationId">The operationId.</param>
        private bool SiteOnce(string execution, string bodyId, int operationId)
        {
            if (InCycle(bodyId, operationId))
                return false;

            var steps = _steps.GetValueOrDefault(execution) ?? [];
            var entries = (_entries.GetValueOrDefault(execution) ?? []).Select(entry => entry.InstanceId).ToHashSet(StringComparer.Ordinal);
            var incoming = steps.GroupBy(step => step.Callee, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.Select(step => (step.Caller, step.OperationId)).Distinct().ToArray(),
                                              StringComparer.Ordinal);
            var nodes = entries.Concat(incoming.Keys).ToHashSet(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                CheckCancellation();
                changed = false;
                foreach (var node in nodes)
                {
                    CheckCancellation();
                    var total = entries.Contains(node) ? 1 : 0;
                    foreach (var (caller, operation) in incoming.GetValueOrDefault(node) ?? [])
                    {
                        CheckCancellation();
                        total += counts.GetValueOrDefault(caller) * (InCycle(heap.Instances[caller].BodyId, operation) ? 2 : 1);
                    }
                    total = Math.Min(total, 2);
                    if (total != counts.GetValueOrDefault(node))
                    {
                        counts[node] = total;
                        changed = true;
                    }
                }
            }

            return counts.Where(pair => heap.Instances[pair.Key].BodyId == bodyId).Sum(pair => pair.Value) <= 1;
        }

        /// <summary>The member symbols of the shortest walk of an execution from its entries to an instance of a body, consecutive
        /// duplicates folded; just the body's symbol when the walk does not reach it.</summary>
        /// <param name="execution">The execution.</param>
        /// <param name="bodyId">The bodyId.</param>
        private IReadOnlyList<string> PathSymbols(string execution, string bodyId)
        {
            var steps = (_steps.GetValueOrDefault(execution) ?? []).GroupBy(step => step.Caller, StringComparer.Ordinal)
                                                                    .ToDictionary(group => group.Key,
                                                                                  group => group.OrderBy(step => step.OperationId)
                                                                                                .ThenBy(step => step.Callee, StringComparer.Ordinal)
                                                                                                .Select(step => step.Callee).ToArray(),
                                                                                  StringComparer.Ordinal);
            var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            foreach (var entry in _entries.GetValueOrDefault(execution) ?? [])
            {
                CheckCancellation();
                if (parents.TryAdd(entry.InstanceId, null))
                    pending.Enqueue(entry.InstanceId);
            }

            while (pending.TryDequeue(out var instance))
            {
                CheckCancellation();
                if (heap.Instances[instance].BodyId == bodyId)
                {
                    var path = new List<string>();
                    for (string? current = instance; current is not null; current = parents[current])
                    {
                        CheckCancellation();
                        path.Add(Symbol(heap.Instances[current].BodyId));
                    }
                    path.Reverse();
                    return path.Where((symbol, index) => index == 0 || path[index - 1] != symbol).ToArray();
                }

                foreach (var callee in steps.GetValueOrDefault(instance) ?? [])
                {
                    CheckCancellation();
                    if (parents.TryAdd(callee, instance))
                        pending.Enqueue(callee);
                }
            }

            return [Symbol(bodyId)];
        }

        private static string RootExecution(string rootId) => $"root:{rootId}";

        private string Add(ExecutionInstance execution)
        {
            _executions.TryAdd(execution.Id, execution);
            return execution.Id;
        }

        private bool IsHosted(HeapRegion region) => region.Identity.StartsWith($"di|{DiIndex.HOSTED_SERVICE_KEY}|", StringComparison.Ordinal);

        private static bool IsContainerWide(HeapRegion region) =>
            region.Kind == HeapRegionKind.Di && region.Context is "singleton" or "root-scope";

        /// <summary>Which executions each constructed region's construction runs in: a controller receiver its root, a hosted service and
        /// everything its construction resolves startup, another singleton or root-scope object its own at-most-once execution, a
        /// per-invocation scoped object its root, and a transient its resolver's executions.</summary>
        private void AssignRegionExecutions()
        {
            var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var construction in heap.Constructions)
            {
                CheckCancellation();
                children[construction.RegionId] = construction.ConstructorInstances
                    .SelectMany(instance => heap.Instances[instance].Parameters.Values.SelectMany(values => values))
                    .Where(region => heap.Regions[region].Kind == HeapRegionKind.Di)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var startup = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(heap.Regions.Values.Where(IsHosted).Select(region => region.Identity).Concat(heap.StartupRegions));
            while (pending.TryPop(out var region))
            {
                CheckCancellation();
                if (!startup.Add(region))
                    continue;
                foreach (var child in children.GetValueOrDefault(region) ?? [])
                {
                    CheckCancellation();
                    pending.Push(child);
                }
            }

            foreach (var region in heap.Regions.Values.Where(region => region.Kind is HeapRegionKind.Di or HeapRegionKind.Receiver or HeapRegionKind.Container))
            {
                CheckCancellation();
                var executions = Executions(region.Identity);
                if (startup.Contains(region.Identity))
                    executions.Add(STARTUP);
                else if (region.Kind == HeapRegionKind.Receiver)
                    executions.Add(region.Context);
                else if (IsContainerWide(region))
                    executions.Add(Add(new ExecutionInstance($"construction:{region.Identity}", ExecutionKind.LazyConstruction,
                                                             $"construction of {region.Display}", AT_MOST_ONCE, null, region.Identity)));
                else if (region.Context.StartsWith("invocation:", StringComparison.Ordinal))
                    executions.Add(region.Context["invocation:".Length..]);
            }

            foreach (var (rootId, instanceId) in heap.RootInstances)
            {
                CheckCancellation();
                var instance = heap.Instances[instanceId];
                foreach (var region in instance.Parameters.Values.SelectMany(values => values).Concat(instance.Receivers))
                {
                    CheckCancellation();
                    if (heap.Regions[region] is { Kind: HeapRegionKind.Di } di && !IsContainerWide(di) && !startup.Contains(region))
                        Executions(region).Add(RootExecution(rootId));
                }
            }

            var changed = true;
            while (changed)
            {
                CheckCancellation();
                changed = false;
                foreach (var (parent, childRegions) in children)
                {
                    CheckCancellation();
                    foreach (var child in childRegions.Where(child => !IsContainerWide(heap.Regions[child]) && !startup.Contains(child)))
                    {
                        CheckCancellation();
                        foreach (var execution in Executions(parent).ToArray())
                        {
                            CheckCancellation();
                            changed |= Executions(child).Add(execution);
                        }
                    }
                }
            }
        }

        private HashSet<string> Executions(string region)
        {
            if (!_regionExecutions.TryGetValue(region, out var executions))
                _regionExecutions.Add(region, executions = new HashSet<string>(StringComparer.Ordinal));
            return executions;
        }

        /// <summary>The closed types whose type initializer runs at startup: activated by an instance the startup constructions reach
        /// over call edges, or by a startup construction, transitively through the type initializers they activate.</summary>
        private HashSet<string> StartupTypeInitializers()
        {
            var startupRegions = _regionExecutions.Where(pair => pair.Value.Contains(STARTUP)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var instances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var construction in heap.Constructions.Where(construction => startupRegions.Contains(construction.RegionId)))
            {
                CheckCancellation();
                Reach(construction.ConstructorInstances, instances);
            }

            var startupTypeInitializers = new HashSet<string>(StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                CheckCancellation();
                changed = false;
                foreach (var initializer in heap.TypeInitializers)
                {
                    CheckCancellation();
                    if (startupTypeInitializers.Contains(initializer.TypeKey) ||
                        !initializer.TriggeringInstances.Any(instances.Contains) && !initializer.TriggeringRegions.Any(startupRegions.Contains))
                    {
                        continue;
                    }

                    startupTypeInitializers.Add(initializer.TypeKey);
                    Reach([initializer.InstanceId], instances);
                    changed = true;
                }
            }

            return startupTypeInitializers;
        }

        private void Reach(IEnumerable<string> starts, HashSet<string> reached)
        {
            CheckCancellation();
            var pending = new Stack<string>(starts);
            while (pending.TryPop(out var instance))
            {
                CheckCancellation();
                if (!reached.Add(instance))
                    continue;
                foreach (var edge in _edges.GetValueOrDefault(instance) ?? [])
                {
                    CheckCancellation();
                    pending.Push(edge.CalleeInstance);
                }
            }
        }

        /// <summary>The dense id of an object under construction, given the first time the walk meets it.</summary>
        /// <param name="object">The object's region id.</param>
        private int ObjectId(string @object)
        {
            if (!_objectIds.TryGetValue(@object, out var id))
            {
                id = _objects.Count;
                _objectIds.Add(@object, id);
                _objects.Add(@object);
            }

            return id;
        }

        /// <summary>The executions an instance runs in so far, as a set of its own.</summary>
        /// <param name="instance">The instance.</param>
        private ExecutionSet ExecutionsOf(string instance) => _ids.Set(_instanceExecutions.GetValueOrDefault(instance) ?? Bits.EMPTY);

        /// <summary>A node of the walk graph the executions share (ADR 0017): an instance, the segment of its body it runs and the key of
        /// the site whose tail its awaited callees' tails join. What the node does for an execution that reaches it — the spawns, timers
        /// and startup delegates it starts and the tails it enters — is found once, when the node is discovered.</summary>
        /// <param name="id">The node's place in <see cref="_nodeList"/>.</param>
        /// <param name="instance">The instance.</param>
        /// <param name="segment">The segment of its body.</param>
        /// <param name="tail">The key of the site whose tail its awaited callees' tails join, or null.</param>
        private sealed class WalkNode(int id, string instance, BodySegment segment, string? tail)
        {
            internal int Id { get; } = id;
            internal string Instance { get; } = instance;
            internal BodySegment Segment { get; } = segment;
            internal string? Tail { get; } = tail;

            internal List<WalkEdge> Successors { get; } = [];
            internal List<WalkEdge> Predecessors { get; } = [];
            internal List<ExecutionStep> Steps { get; } = [];
            internal List<SpawnSite> Spawns { get; } = [];
            internal List<TimerCallbackSite> Timers { get; } = [];
            internal List<StartupDelegate> StartupDelegates { get; } = [];

            internal List<TailEntry> TailEntries { get; } = [];

            /// <summary>The executions that reach the node, as bits over execution numbers.</summary>
            internal ulong[] Executions = Bits.EMPTY;

            /// <summary>The executions that reached the node since it last passed them on.</summary>
            internal ulong[] Arrived = Bits.EMPTY;

            /// <summary>The objects whose construction reaches the node, as bits over object numbers.</summary>
            internal ulong[] Objects = Bits.EMPTY;

            /// <summary>The objects that reached the node since it last passed them on.</summary>
            internal ulong[] ObjectsArrived = Bits.EMPTY;

            /// <summary>The executions that reach the node on a path that starts no construction, as bits over execution numbers.</summary>
            internal ulong[] Clean = Bits.EMPTY;

            /// <summary>The clean executions that reached the node since it last passed them on.</summary>
            internal ulong[] CleanArrived = Bits.EMPTY;
            internal bool Discovered { get; set; }
            internal bool Queued { get; set; }
            internal int Postorder { get; set; }
            internal int Visits { get; set; }
        }

        /// <summary>An edge of the walk graph, from one node's side: the node at its other end, and the objects the edge starts to
        /// construct, as bits over object numbers.</summary>
        /// <param name="Node">The node at the other end.</param>
        /// <param name="Added">The objects the edge starts to construct.</param>
        private sealed record WalkEdge(WalkNode Node, ulong[] Added);

        /// <summary>A tail an execution reaching a node enters: an async spawn edge's callee, with the call operation, or an awaited
        /// callee in a prefix, whose tail joins the node's.</summary>
        /// <param name="Callee">The callee instance whose tail is entered.</param>
        /// <param name="AsyncOperation">The call operation of an async spawn edge, or null for an awaited callee in a prefix.</param>
        private sealed record TailEntry(string Callee, int? AsyncOperation);

        /// <summary>An entry the walk took.</summary>
        /// <param name="Execution">The execution's number.</param>
        /// <param name="Node">The node entered.</param>
        /// <param name="Interval">The number of the object the entry starts inside, or -1.</param>
        private sealed record TakenEntry(int Execution, WalkNode Node, int Interval);

        /// <summary>An edge that starts an object's construction.</summary>
        /// <param name="From">The node the edge leaves.</param>
        /// <param name="To">The node the edge enters.</param>
        private sealed record ConstructionEdge(WalkNode From, WalkNode To);

        /// <summary>One object at one node: the executions in which it is under construction there on some path (it is in
        /// <c>MayIn</c>), and those that reach the node on some path outside its construction (it is not in <c>MustIn</c>).</summary>
        /// <param name="Node">The node.</param>
        /// <param name="May">The executions in which the object is in <c>MayIn</c>, as bits.</param>
        /// <param name="NotMust">The executions in which the object is not in <c>MustIn</c>, as bits.</param>
        private sealed record ConstructionAt(WalkNode Node, ulong[] May, ulong[] NotMust);

        /// <summary>What earlier searches for one object learned about a node they passed.</summary>
        /// <param name="Outside">The executions found to reach the node outside the construction.</param>
        /// <param name="Never">The executions found not to.</param>
        private sealed record SearchFacts(ulong[] Outside, ulong[] Never);

        /// <summary>An access a node makes on one region it may touch.</summary>
        /// <param name="Node">The node.</param>
        /// <param name="Access">The access from its instance's summary.</param>
        /// <param name="Region">The region.</param>
        private sealed record NodeAccess(WalkNode Node, SummaryAccess Access, string Region);

        /// <summary>What collected accesses are told apart by.</summary>
        /// <param name="Instance">The instance making the access.</param>
        /// <param name="Operation">The access's operation.</param>
        /// <param name="Region">The region it touches.</param>
        /// <param name="Local">Whether it is construction-local.</param>
        private sealed record AccessKey(string Instance, int Operation, string Region, bool Local);

        /// <summary>A collected access being gathered: the first access of the summary with its key, and the executions found so far.</summary>
        /// <param name="access">The first access of the summary with the key.</param>
        private sealed class AccessRun(SummaryAccess access)
        {
            internal SummaryAccess Access { get; } = access;
            internal ulong[] Executions = Bits.EMPTY;
        }

        /// <summary>A store or element store of an instance into one slot of one region, with the values it may store.</summary>
        /// <param name="Slot">The slot written.</param>
        /// <param name="Region">The region it may be written in.</param>
        /// <param name="Instance">The instance making the store.</param>
        /// <param name="OperationId">The store operation.</param>
        /// <param name="Values">The values it may store.</param>
        private sealed record SlotStore(string Slot, string Region, MethodInstance Instance, int OperationId, HashSet<string> Values);

        /// <summary>The store accesses of one list of collected accesses, by region.</summary>
        /// <param name="Accesses">The list.</param>
        /// <param name="Stores">Its store accesses, by region.</param>
        private sealed record StoreIndex(IReadOnlyList<CollectedAccess> Accesses, ILookup<string, CollectedAccess> Stores);

        private WalkNode? Node(string instance, BodySegment segment, string? tail)
        {
            if (!heap.Instances.ContainsKey(instance))
                return null;
            var key = new WalkNodeKey(instance, segment, tail);
            if (!_walkNodes.TryGetValue(key, out var node))
            {
                _walkNodes.Add(key, node = new WalkNode(_nodeList.Count, instance, segment, tail));
                _nodeList.Add(node);
            }
            return node;
        }

        /// <summary>Takes an entry: its node and what that reaches are discovered, and the execution arrives at the node.</summary>
        /// <param name="execution">The execution entering.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="tail">The key of the site whose tail the entry's awaited callees' tails join.</param>
        private void Seed(string execution, ExecutionEntry entry, string? tail)
        {
            CheckCancellation();
            if (Node(entry.InstanceId, entry.Segment, tail) is not { } node)
                return;
            Discover(node);
            var number = _ids.Number(execution);
            _seeds.Add(new TakenEntry(number, node, entry.IntervalObject is { } interval ? ObjectId(interval) : -1));
            if (Bits.Add(ref node.Executions, number))
            {
                Bits.Add(ref node.Arrived, number);
                Enqueue(node);
            }
        }

        private void Enqueue(WalkNode node)
        {
            if (node.Queued)
                return;
            node.Queued = true;
            _walkQueue.Enqueue(node, -node.Postorder);
        }

        /// <summary>Discovers each node reachable from a first one once, depth first, with what it does for an execution, its steps and
        /// its successors, and gives it its postorder rank: the walk takes up nodes in reverse postorder.</summary>
        /// <param name="first">The node to start from.</param>
        private void Discover(WalkNode first)
        {
            var discovery = new Stack<(WalkNode Node, bool Finish)>([(first, false)]);
            while (discovery.TryPop(out var frame))
            {
                CheckCancellation();
                var item = frame.Node;
                if (frame.Finish)
                {
                    item.Postorder = _walkPostorder++;
                    continue;
                }
                if (item.Discovered)
                    continue;
                item.Discovered = true;
                var instance = heap.Instances[item.Instance];
                foreach (var site in _spawns.GetValueOrDefault(item.Instance) ?? [])
                {
                    CheckCancellation();
                    if (_segments.Runs(instance.BodyId, item.Segment, site.OperationId))
                        item.Spawns.Add(site);
                }

                foreach (var site in _timers.GetValueOrDefault(item.Instance) ?? [])
                {
                    CheckCancellation();
                    if (!_segments.Runs(instance.BodyId, item.Segment, site.OperationId))
                        continue;
                    if (site.Action == IrTimerAction.ElapsedSubscribe)
                        _subscriptions.Add(site);
                    else
                        item.Timers.Add(site);
                }

                foreach (var fated in _startupDelegates.GetValueOrDefault(item.Instance) ?? [])
                {
                    CheckCancellation();
                    if (_segments.Runs(instance.BodyId, item.Segment, fated.OperationId))
                        item.StartupDelegates.Add(fated);
                }

                foreach (var edge in Permute(_edges.GetValueOrDefault(item.Instance) ?? []))
                {
                    CheckCancellation();
                    if (_segments.Follow(instance, item.Segment, edge) is not { } calleeSegment)
                        continue;

                    var calleeTail = calleeSegment == BodySegment.Prefix ? item.Tail : null;
                    if (_segments.IsAsyncSpawn(item.Instance, edge.OperationId, edge.CalleeInstance))
                    {
                        calleeTail = AsyncKey(instance.BodyId, edge.OperationId);
                        item.TailEntries.Add(new TailEntry(edge.CalleeInstance, edge.OperationId));
                    }
                    else if (calleeSegment == BodySegment.Prefix && calleeTail is not null)
                    {
                        item.TailEntries.Add(new TailEntry(edge.CalleeInstance, null));
                    }
                    else if (calleeSegment == BodySegment.Prefix)
                    {
                        calleeSegment = BodySegment.Whole;
                    }

                    item.Steps.Add(new ExecutionStep(item.Instance, item.Segment, edge.OperationId, edge.CalleeInstance, calleeSegment));
                    // The transfer adds only this edge's objects, whatever reaches the edge.
                    var added = edge.Reason == WholeProgram.CONSTRUCTION_REASON && _constructed.TryGetValue(edge.CalleeInstance, out var constructed)
                        ? [constructed]
                        : instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } constructor
                            ? constructor.Receivers.SelectMany(value => heap.Resolve(instance.Id, value))
                                         .Where(region => heap.Regions[region].Kind == HeapRegionKind.Allocation).ToArray()
                            : Array.Empty<string>();
                    if (Node(edge.CalleeInstance, calleeSegment, calleeTail) is { } callee)
                        item.Successors.Add(new WalkEdge(callee, added.Length == 0 ? Bits.EMPTY : Bits.Of(added.Select(ObjectId))));
                }

                discovery.Push((item, true));
                foreach (var successor in item.Successors)
                    discovery.Push((successor.Node, false));
            }
        }

        /// <summary>Takes up a node: for each execution that arrived since it last did, the node starts what it starts and enters the
        /// tails it enters, and then passes those executions on to its successors. Each execution arrives at a node once.</summary>
        /// <param name="node">The node.</param>
        private void Process(WalkNode node)
        {
            node.Queued = false;
            WalkVisits++;
            node.Visits++;
            var arrived = node.Arrived;
            node.Arrived = Bits.EMPTY;
            var instance = heap.Instances[node.Instance];
            var executions = _instanceExecutions.GetValueOrDefault(node.Instance) ?? Bits.EMPTY;
            Bits.UnionWith(ref executions, arrived);
            _instanceExecutions[node.Instance] = executions;
            foreach (var number in Bits.Numbers(arrived))
            {
                CheckCancellation();
                var execution = _ids.Id(number);
                foreach (var site in node.Spawns)
                    Spawn(execution, instance, site);
                foreach (var site in node.Timers)
                    Timer(execution, instance, site, site.OperationId);
                foreach (var fated in node.StartupDelegates)
                    StartupDelegate(execution, instance, fated);
                foreach (var (callee, asyncOperation) in node.TailEntries)
                {
                    var tail = asyncOperation is { } operation ? AsyncChild(execution, instance, operation) : _tailOf[(execution, node.Tail!)];
                    Entry(tail, new ExecutionEntry(callee, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Tail });
                }
            }

            foreach (var (successor, _) in node.Successors)
            {
                CheckCancellation();
                if (Bits.UnionWith(ref successor.Executions, arrived, ref successor.Arrived))
                    Enqueue(successor);
            }
        }

        /// <summary>Which objects' constructions reach each node, and so each object's constructor chain: the instances of the nodes its
        /// construction reaches, from an edge that starts it or an entry inside it. Readers ask only whether an object is under
        /// construction on some path and outside it on some path, which <see cref="ConstructionExecutions"/> answers per execution.</summary>
        private void Constructions()
        {
            var pending = new PriorityQueue<WalkNode, int>();
            void Arrive(WalkNode node, ulong[] objects)
            {
                if (!Bits.UnionWith(ref node.Objects, objects, ref node.ObjectsArrived) || node.Queued)
                    return;
                node.Queued = true;
                pending.Enqueue(node, -node.Postorder);
            }

            foreach (var node in _walkNodes.Values)
            {
                CheckCancellation();
                foreach (var (successor, added) in node.Successors)
                {
                    successor.Predecessors.Add(new WalkEdge(node, added));
                    foreach (var number in Bits.Numbers(added))
                    {
                        if (!_objectEdges.TryGetValue(number, out var edges))
                            _objectEdges.Add(number, edges = []);
                        edges.Add(new ConstructionEdge(node, successor));
                    }
                    Arrive(successor, added);
                }
            }

            foreach (var seed in _seeds)
            {
                CheckCancellation();
                var (execution, node, interval) = seed;
                if (!_seedsAt.TryGetValue(node, out var seeds))
                    _seedsAt.Add(node, seeds = []);
                seeds.Add(seed);
                if (interval < 0)
                    continue;
                if (!_objectSeeds.TryGetValue(interval, out var executions))
                    _objectSeeds.Add(interval, executions = []);
                executions.Add(execution);
                Arrive(node, Bits.Of([interval]));
            }

            while (pending.TryDequeue(out var node, out _))
            {
                CheckCancellation();
                node.Queued = false;
                var arrived = node.ObjectsArrived;
                node.ObjectsArrived = Bits.EMPTY;
                foreach (var (successor, _) in node.Successors)
                    Arrive(successor, arrived);
            }

            // A chain matters only to publication, which never publishes static storage or an object the heap does not hold.
            var publishable = Bits.Of(Enumerable.Range(0, _objects.Count)
                                                .Where(number => heap.Regions.TryGetValue(_objects[number], out var region) && region.Kind != HeapRegionKind.Static));
            foreach (var node in _walkNodes.Values)
            {
                CheckCancellation();
                var objects = _chains.GetValueOrDefault(node.Instance) ?? Bits.EMPTY;
                if (Bits.UnionWith(ref objects, node.Objects, publishable))
                    _chains[node.Instance] = objects;
            }

            // For each object, the executions that may enter the nodes its construction reaches outside it: over an edge from a node it
            // does not reach that does not start it, or by another entry taken at one of those nodes.
            foreach (var node in _walkNodes.Values)
            {
                CheckCancellation();
                foreach (var (predecessor, added) in node.Predecessors)
                {
                    for (var index = 0; index < node.Objects.Length; index++)
                    {
                        var entering = node.Objects[index] & ~(index < predecessor.Objects.Length ? predecessor.Objects[index] : 0) &
                                       ~(index < added.Length ? added[index] : 0);
                        for (; entering != 0; entering &= entering - 1)
                            EnterOutside((index << 6) + System.Numerics.BitOperations.TrailingZeroCount(entering), predecessor.Executions);
                    }
                }

                foreach (var (execution, _, interval) in _seedsAt.GetValueOrDefault(node) ?? [])
                {
                    foreach (var number in Bits.Numbers(node.Objects).Where(number => number != interval))
                        EnterOutside(number, Bits.Of([execution]));
                }
            }

            // The executions that reach each node on a path that starts no construction: for any object, that path avoids its
            // construction, unless the execution's own entry starts inside the object.
            var clean = new Stack<WalkNode>();
            void ArriveClean(WalkNode node, ulong[] executions)
            {
                if (Bits.UnionWith(ref node.Clean, executions, ref node.CleanArrived) && !node.Queued)
                {
                    node.Queued = true;
                    clean.Push(node);
                }
            }

            foreach (var (execution, node, _) in _seeds)
                ArriveClean(node, Bits.Of([execution]));
            while (clean.TryPop(out var node))
            {
                CheckCancellation();
                node.Queued = false;
                var arrived = node.CleanArrived;
                node.CleanArrived = Bits.EMPTY;
                foreach (var (successor, added) in node.Successors)
                {
                    if (Bits.IsEmpty(added))
                        ArriveClean(successor, arrived);
                }
            }

            void EnterOutside(int number, ulong[] executions)
            {
                var set = _outsideStarts.GetValueOrDefault(number) ?? Bits.EMPTY;
                if (Bits.UnionWith(ref set, executions))
                    _outsideStarts[number] = set;
            }
        }

        /// <summary>For each object, by number, the executions that may enter the nodes its construction reaches without starting it.</summary>
        private readonly Dictionary<int, ulong[]> _outsideStarts = [];

        /// <summary>The edges that start each object's construction, by object number.</summary>
        private readonly Dictionary<int, List<ConstructionEdge>> _objectEdges = [];

        /// <summary>The executions of the entries that start inside each object, by object number.</summary>
        private readonly Dictionary<int, List<int>> _objectSeeds = [];

        /// <summary>The entries taken at each node.</summary>
        private readonly Dictionary<WalkNode, List<TakenEntry>> _seedsAt = new(ReferenceEqualityComparer.Instance);

        /// <summary>For one object, at each node asked about, the executions in which the object is under construction there on some
        /// path (it is in <c>MayIn</c>), and those that reach the node on some path outside its construction (it is not in <c>MustIn</c>)
        /// (ADR 0017). Every node the construction reaches is reached from one of the edges and entries that start it, so when all of them
        /// carry the same executions, those are <c>MayIn</c> at every such node; otherwise a search back from the node collects the
        /// starts that reach it. An execution that reaches the node and is not in <c>MayIn</c> there reaches it outside the
        /// construction, and so does one that reaches it on a path that starts no construction from an entry outside the object. The
        /// rest can reach it outside only if they enter the construction's nodes without starting it (<see cref="_outsideStarts"/>); for
        /// those, a search back along the edges that do not start the construction looks for a node the construction does not reach, or
        /// another entry of that execution. Each search stops once it has found every execution it asks about. A search that ends
        /// without finding an execution shows that no node it passed is reached outside the construction by it, and later searches for
        /// the same object stop at those nodes.</summary>
        /// <param name="object">The object's number.</param>
        /// <param name="asked">The nodes asked about, each one its construction reaches.</param>
        /// <returns>For each node asked about, the two sets of executions, as bits.</returns>
        private IEnumerable<ConstructionAt> ConstructionExecutions(int @object, IReadOnlyCollection<WalkNode> asked)
        {
            var starts = (_objectEdges.GetValueOrDefault(@object) ?? []).Select(edge => edge.From.Executions)
                                                                         .Concat((_objectSeeds.GetValueOrDefault(@object) ?? []).Select(execution => Bits.Of([execution])))
                                                                         .ToArray();
            var shared = starts.Length != 0 && starts.All(set => Bits.SameAs(set, starts[0])) ? starts[0] : null;
            var entering = _outsideStarts.GetValueOrDefault(@object) ?? Bits.EMPTY;
            var enteredInside = Bits.Of(_objectSeeds.GetValueOrDefault(@object) ?? []);
            // For nodes earlier searches passed: the executions found to reach them outside the construction, and those found not to.
            var known = new Dictionary<WalkNode, SearchFacts>(ReferenceEqualityComparer.Instance);
            foreach (var node in asked)
            {
                CheckCancellation();
                var may = shared ?? Back(node, node.Executions, inside: true);
                // A clean path avoids the construction, unless the execution entered inside the object.
                var clear = new ulong[node.Executions.Length];
                var wanted = new ulong[node.Executions.Length];
                for (var index = 0; index < clear.Length; index++)
                {
                    clear[index] = (index < node.Clean.Length ? node.Clean[index] : 0) & ~(index < enteredInside.Length ? enteredInside[index] : 0);
                    wanted[index] = (index < may.Length ? may[index] : 0) & (index < entering.Length ? entering[index] : 0) & ~clear[index];
                }

                var outside = Back(node, wanted, inside: false);
                var notMust = (ulong[])node.Executions.Clone();
                for (var index = 0; index < notMust.Length; index++)
                    notMust[index] = notMust[index] & ~((index < may.Length ? may[index] : 0) & ~clear[index]) | (index < outside.Length ? outside[index] : 0);
                yield return new ConstructionAt(node, may, notMust);
            }

            // Searches back from a node among the nodes the construction reaches for the executions asked about: inside, the starts of
            // the construction that reach it; outside, the paths to it that never start the construction.
            ulong[] Back(WalkNode node, ulong[] wanted, bool inside)
            {
                var found = Bits.EMPTY;
                if (Bits.IsEmpty(wanted))
                    return found;
                var marks = _constructionMarks ??= new int[_nodeList.Count];
                var stamp = ++_constructionStamp;
                marks[node.Id] = stamp;
                var passed = new List<WalkNode>();
                var pending = new Queue<WalkNode>([node]);
                var complete = false;
                while (pending.TryDequeue(out var current))
                {
                    CheckCancellation();
                    if (!inside && known.TryGetValue(current, out var earlier))
                    {
                        Bits.UnionWith(ref found, earlier.Outside, wanted);
                        if (Bits.Covers(found, wanted))
                        {
                            complete = true;
                            break;
                        }

                        // No path outside the construction reaches the node for what is still wanted: none goes on from it either.
                        var missing = (ulong[])wanted.Clone();
                        for (var index = 0; index < missing.Length; index++)
                            missing[index] &= ~(index < found.Length ? found[index] : 0);
                        if (Bits.Covers(earlier.Never, missing))
                            continue;
                    }

                    passed.Add(current);
                    foreach (var (execution, _, interval) in _seedsAt.GetValueOrDefault(current) ?? [])
                    {
                        if ((interval == @object) == inside && Bits.Contains(wanted, execution))
                            Bits.Add(ref found, execution);
                    }

                    foreach (var (predecessor, added) in current.Predecessors)
                    {
                        var starting = Bits.Contains(added, @object);
                        var reached = Bits.Contains(predecessor.Objects, @object);
                        if (inside ? starting : !starting && !reached)
                            Bits.UnionWith(ref found, predecessor.Executions, wanted);
                        else if (!starting && reached && marks[predecessor.Id] != stamp)
                        {
                            marks[predecessor.Id] = stamp;
                            pending.Enqueue(predecessor);
                        }
                    }

                    if (Bits.Covers(found, wanted))
                    {
                        complete = true;
                        break;
                    }
                }

                if (!inside)
                {
                    var (outsideBefore, neverBefore) = known.GetValueOrDefault(node) ?? new SearchFacts(Bits.EMPTY, Bits.EMPTY);
                    Bits.UnionWith(ref outsideBefore, found);
                    known[node] = new SearchFacts(outsideBefore, neverBefore);
                    if (!complete)
                    {
                        // The search ran out: what it did not find reaches none of the nodes it passed outside the construction.
                        var never = (ulong[])wanted.Clone();
                        for (var index = 0; index < never.Length; index++)
                            never[index] &= ~(index < found.Length ? found[index] : 0);
                        foreach (var passedNode in passed)
                        {
                            var (outsideOf, neverOf) = known.GetValueOrDefault(passedNode) ?? new SearchFacts(Bits.EMPTY, Bits.EMPTY);
                            Bits.UnionWith(ref neverOf, never);
                            known[passedNode] = new SearchFacts(outsideOf, neverOf);
                        }
                    }
                }

                return found;
            }
        }

        /// <summary>The marks of the searches of <see cref="ConstructionExecutions"/>: a node is marked for the search whose stamp it holds.</summary>
        private int[]? _constructionMarks;
        private int _constructionStamp;

        /// <summary>The objects whose construction publishes them: an instance of the chain stores the object, or a delegate capturing
        /// it, into a region that is neither the object nor reachable from it, returns it to a caller outside the chain, or hands a
        /// shared object to an unresolved call. Each instance is asked only about the objects it stores, returns or hands over whose
        /// chain holds it, so the work grows with what instances do, not with the length of every chain; an unresolved call's reach is
        /// worked out only where a shared object of the chain could be handed to it.</summary>
        /// <param name="ownership">The ownership.</param>
        private HashSet<string> Published(IReadOnlyDictionary<string, RegionOwnership> ownership)
        {
            var published = new HashSet<string>(StringComparer.Ordinal);
            var inside = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var shared = Bits.Of(Enumerable.Range(0, _objects.Count)
                                           .Where(number => ownership.GetValueOrDefault(_objects[number])?.Kind is OwnershipKind.Escaped or OwnershipKind.Shared
                                                                                                                       or OwnershipKind.Unknown));
            bool InChain(string instanceId, string @object) =>
                _chains.TryGetValue(instanceId, out var objects) && _objectIds.TryGetValue(@object, out var number) && Bits.Contains(objects, number);
            foreach (var (instanceId, chain) in _chains)
            {
                CheckCancellation();
                bool Candidate(string @object) =>
                    !published.Contains(@object) && InChain(instanceId, @object) && heap.Regions[@object].Kind != HeapRegionKind.Static;
                var instance = heap.Instances[instanceId];
                foreach (var (stored, targets) in Stores(instance))
                {
                    CheckCancellation();
                    var exposed = stored.Concat(stored.Where(value => heap.Regions[value].Kind == HeapRegionKind.Delegate).SelectMany(heap.DelegateCaptures));
                    foreach (var @object in exposed.Where(Candidate).Distinct(StringComparer.Ordinal).ToArray())
                    {
                        IReadOnlySet<string> Inside() => inside.TryGetValue(@object, out var within) ? within : inside[@object] = Closure([@object]);
                        if (targets.Any(target => target != @object && !IsInternal(@object, Inside, target)))
                            published.Add(@object);
                    }
                }

                var callers = CallersOf.GetValueOrDefault(instanceId) ?? [];
                foreach (var @object in instance.Summary.Returns.SelectMany(returned => Resolve(instance, returned.Values)).Where(Candidate)
                                                .Distinct(StringComparer.Ordinal).ToArray())
                {
                    if (callers.Any(caller => !InChain(caller, @object)))
                        published.Add(@object);
                }

                // Handing a shared object to a call the analysis cannot follow publishes it: the call may keep it anywhere. An object
                // of one execution it only reads, and leaves where it was (R2, ADR 0006).
                var handed = Bits.EMPTY;
                Bits.UnionWith(ref handed, chain, shared);
                var objects = Bits.Numbers(handed).Select(number => _objects[number]).Where(Candidate).ToArray();
                if (objects.Length == 0)
                    continue;
                foreach (var call in UnknownCallsByInstance[instanceId])
                {
                    CheckCancellation();
                    var reached = ReachOf(call);
                    foreach (var @object in objects.Where(reached.Contains))
                        published.Add(@object);
                }
            }

            return published;
        }

        /// <summary>The unresolved calls with unknown effects, by the instance making them (R1); a locator has none.</summary>
        private ILookup<string, UnknownCall> UnknownCallsByInstance =>
            _unknownCalls ??= UnknownCalls.Of(scope, heap).Where(call => !call.IsLocator).ToLookup(call => call.Instance.Id, StringComparer.Ordinal);

        private ILookup<string, UnknownCall>? _unknownCalls;

        /// <summary>The regions an unresolved call's unknown effect and delegates reach, worked out once per call.</summary>
        /// <param name="call">The call.</param>
        private IReadOnlySet<string> ReachOf(UnknownCall call)
        {
            _reach ??= new UnknownCalls.Reach(scope, heap);
            if (!_reached.TryGetValue(call, out var regions))
                _reached.Add(call, regions = _reach.Of(call).Regions);
            return regions;
        }

        private UnknownCalls.Reach? _reach;
        private readonly Dictionary<UnknownCall, IReadOnlySet<string>> _reached = new(ReferenceEqualityComparer.Instance);

        /// <summary>What each store and element store of an instance stores and where, resolved once.</summary>
        /// <param name="instance">The instance of a constructor chain.</param>
        private (HashSet<string> Stored, HashSet<string> Targets)[] Stores(MethodInstance instance)
        {
            if (_stores.TryGetValue(instance.Id, out var stores))
                return stores;
            var summary = instance.Summary;
            stores = summary.Stores.Select(store => (Values: store.Values,
                                                     Targets: store.Field.IsStatic
                                                         ? new HashSet<string> { heap.StaticRegionOf(instance.Id, store.Field) }
                                                         : Resolve(instance, store.Bases)))
                            .Concat(summary.Elements.Where(element => element.Kind == ElementOperationKind.Store)
                                           .Select(element => (Values: element.Values, Targets: Resolve(instance, element.Arrays))))
                            .Select(store => (Stored: Resolve(instance, store.Values), store.Targets))
                            .ToArray();
            _stores.Add(instance.Id, stores);
            return stores;
        }

        /// <summary>The callers of each instance over the heap's call edges.</summary>
        private Dictionary<string, string[]> CallersOf =>
            _callersOf ??= heap.Edges.GroupBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                               .ToDictionary(group => group.Key, group => group.Select(edge => edge.CallerInstance).ToArray(), StringComparer.Ordinal);

        /// <summary>A target reachable only through the object under construction: an object or delegate reachable from it that
        /// shared storage does not also reach, or a container object the container resolved for it, whose context names it as the
        /// resolver. A singleton or a root-scope object is shared with everything else, so it is never internal.</summary>
        /// <param name="object">The object under construction.</param>
        /// <param name="inside">The regions reachable from the object, worked out only when the target could be one of them.</param>
        /// <param name="target">The region a store puts the object into.</param>
        private bool IsInternal(string @object, Func<IReadOnlySet<string>> inside, string target) =>
            heap.Regions[target] is var region && !SharedReach(@object).Contains(target) &&
            (region.Kind is HeapRegionKind.Allocation or HeapRegionKind.Delegate && inside().Contains(target) ||
             region.Kind == HeapRegionKind.Di && region.Context.StartsWith($"{@object}|", StringComparison.Ordinal));

        /// <summary>Every region shared storage reaches: static storage, singletons and root-scope objects, and what they hold. The
        /// object under construction is left out as a root, since what it holds is what this asks about; for an object that is no root,
        /// that leaves the roots as they are, so all such objects share one closure.</summary>
        /// <param name="object">The object under construction.</param>
        private HashSet<string> SharedReach(string @object)
        {
            _sharedRoots ??= SharedRoots().ToHashSet(StringComparer.Ordinal);
            if (!_sharedRoots.Contains(@object))
                return _sharedReachOfAll ??= Closure(_sharedRoots);
            return _sharedReach.TryGetValue(@object, out var reach) ? reach : _sharedReach[@object] = Closure(_sharedRoots.Where(root => root != @object));
        }

        private HashSet<string>? _sharedRoots;
        private HashSet<string>? _sharedReachOfAll;

        private IEnumerable<string> SharedRoots() =>
            heap.Regions.Values.Where(region => !region.IsMerged && (region.Kind == HeapRegionKind.Static || IsContainerWide(region)))
                .Select(region => region.Identity);

        private HashSet<string> Resolve(MethodInstance instance, IEnumerable<AbstractValue> values) =>
            values.SelectMany(value => heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);

        /// <summary>Every region reachable from the starts through fields, array elements and delegate captures.</summary>
        /// <param name="starts">The starts.</param>
        private HashSet<string> Closure(IEnumerable<string> starts)
        {
            CheckCancellation();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(starts);
            while (pending.TryPop(out var region))
            {
                CheckCancellation();
                foreach (var next in Successors(region))
                {
                    CheckCancellation();
                    if (seen.Add(next))
                        pending.Push(next);
                }
            }

            return seen;
        }

        /// <summary>The edges out of a region, asked of the heap once per region: its fields' targets and a delegate's captures.</summary>
        /// <param name="region">The region.</param>
        private RegionEdge[] Edges(string region)
        {
            if (!_regionEdges.TryGetValue(region, out var edges))
                _regionEdges.Add(region, edges = heap.FieldsOf(region).SelectMany(field => heap.PointsTo(region, field).Select(target => new RegionEdge(field, target)))
                                                     .Concat(heap.Regions[region].Kind == HeapRegionKind.Delegate
                                                                 ? heap.DelegateCaptures(region).Select(target => new RegionEdge("capture", target))
                                                                 : [])
                                                     .ToArray());
            return edges;
        }

        private readonly Dictionary<string, RegionEdge[]> _regionEdges = new(StringComparer.Ordinal);

        /// <summary>An edge of the heap out of a region.</summary>
        /// <param name="Field">The field slot, or <c>capture</c> for a delegate capture.</param>
        /// <param name="Target">The region it points to.</param>
        private sealed record RegionEdge(string Field, string Target);

        private IEnumerable<string> Successors(string region) => Edges(region).Select(edge => edge.Target);

        /// <summary>Every access each node makes, on each region it may touch, in the order of its instance's summary.</summary>
        private List<NodeAccess> NodeAccesses
        {
            get
            {
                if (_nodeAccesses is not null)
                    return _nodeAccesses;
                _nodeAccesses = [];
                foreach (var node in _walkNodes.Values)
                {
                    CheckCancellation();
                    var instance = heap.Instances[node.Instance];
                    foreach (var access in instance.Summary.Accesses.Where(access => _segments.Runs(instance.BodyId, node.Segment, access.OperationId)))
                    {
                        CheckCancellation();
                        var regions = access.Field.IsStatic
                            ? new HashSet<string> { heap.StaticRegionOf(instance.Id, access.Field) }
                            : Resolve(instance, access.Bases);
                        foreach (var region in regions)
                            _nodeAccesses.Add(new NodeAccess(node, access, region));
                    }
                }

                return _nodeAccesses;
            }
        }

        private List<NodeAccess>? _nodeAccesses;

        /// <summary>For each node with an access on an object whose construction reaches it, in which executions the object is in
        /// <c>MayIn</c> there and in which it is not in <c>MustIn</c>.</summary>
        private Dictionary<(WalkNode Node, int Object), ConstructionAt> ConstructionStatus
        {
            get
            {
                if (_constructionStatus is not null)
                    return _constructionStatus;
                _constructionStatus = [];
                var asked = new Dictionary<int, HashSet<WalkNode>>();
                foreach (var (node, _, region) in NodeAccesses)
                {
                    if (!_objectIds.TryGetValue(region, out var number) || !Bits.Contains(node.Objects, number))
                        continue;
                    if (!asked.TryGetValue(number, out var nodes))
                        asked.Add(number, nodes = new HashSet<WalkNode>(ReferenceEqualityComparer.Instance));
                    nodes.Add(node);
                }

                foreach (var (number, nodes) in asked)
                {
                    CheckCancellation();
                    foreach (var construction in ConstructionExecutions(number, nodes))
                        _constructionStatus[(construction.Node, number)] = construction;
                }

                return _constructionStatus;
            }
        }

        private Dictionary<(WalkNode Node, int Object), ConstructionAt>? _constructionStatus;

        /// <summary>Every access the executions run on each region, once per instance, operation, region and construction-local, with the
        /// executions that run it so; of two accesses at one operation on one region, the first of the instance's summary stands. An
        /// access on an object its node runs inside the construction of is local in the executions where the object is in <c>MayIn</c> and
        /// not local in those where it is not in <c>MustIn</c>, unless the construction publishes the object; any other access is not
        /// local, in every execution that reaches its node.</summary>
        /// <param name="published">The objects whose construction publishes them.</param>
        private IReadOnlyList<CollectedAccess> Collect(IReadOnlySet<string> published)
        {
            var collected = new Dictionary<AccessKey, AccessRun>();
            void Add(string instance, SummaryAccess access, string region, bool local, ulong[] executions)
            {
                if (Bits.IsEmpty(executions))
                    return;
                var key = new AccessKey(instance, access.OperationId, region, local);
                if (!collected.TryGetValue(key, out var run))
                    collected.Add(key, run = new AccessRun(access));
                Bits.UnionWith(ref run.Executions, executions);
            }

            foreach (var (node, access, region) in NodeAccesses)
            {
                CheckCancellation();
                if (!published.Contains(region) && _objectIds.TryGetValue(region, out var number) && Bits.Contains(node.Objects, number))
                {
                    var (_, may, notMust) = ConstructionStatus[(node, number)];
                    Add(node.Instance, access, region, true, may);
                    Add(node.Instance, access, region, false, notMust);
                }
                else
                {
                    Add(node.Instance, access, region, false, node.Executions);
                }
            }

            return collected.OrderBy(pair => pair.Key.Instance, StringComparer.Ordinal).ThenBy(pair => pair.Key.Operation)
                            .ThenBy(pair => pair.Key.Region, StringComparer.Ordinal).ThenBy(pair => pair.Key.Local)
                            .Select(pair => new CollectedAccess(_ids.Set(pair.Value.Executions), pair.Key.Instance, pair.Value.Access, pair.Key.Region,
                                                                pair.Key.Local))
                            .ToArray();
        }

        private Dictionary<string, RegionOwnership> Ownership(IReadOnlyList<CollectedAccess> accesses)
        {
            // What the unknown execution of a handed delegate touches it touches as the executions that handed it over, so their own
            // objects stay theirs and only what is shared overlaps everything (R3).
            var handedBy = HandedRegions().ToDictionary(
                region => UnknownDelegateCallId(region),
                region => Handings(region).Select(handing => handing.Execution).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
            // A delegate handed over inside the unknown call of another is attributed through it to where the outer one was handed.
            HashSet<string> Attributed(IEnumerable<string> executions)
            {
                var attributed = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var given = executions.ToHashSet(StringComparer.Ordinal);
                var pending = new Stack<string>(given);
                while (pending.TryPop(out var execution))
                {
                    CheckCancellation();
                    if (!visited.Add(execution))
                        continue;
                    if (handedBy.TryGetValue(execution, out var by) && by.Count != 0)
                    {
                        foreach (var handing in by)
                        {
                            CheckCancellation();
                            pending.Push(handing);
                        }
                    }
                    else
                    {
                        attributed.Add(execution);
                    }
                }

                // Delegates that only hand each other over run nowhere else: they stay their own.
                return attributed.Count != 0 ? attributed : given;
            }

            var accessExecutions = accesses.GroupBy(access => access.RegionId, StringComparer.Ordinal)
                                           .ToDictionary(group => group.Key, group => Attributed(Union(group.Select(access => access.Executions))),
                                                         StringComparer.Ordinal);

            var sharedRoots = SharedRoots().ToArray();
            // Each escaped region keeps the chain of hops from the shared root it is reached through, outermost first.
            var escapes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var pending = new Queue<string>(sharedRoots);
            var visited = new HashSet<string>(sharedRoots, StringComparer.Ordinal);
            while (pending.TryDequeue(out var region))
            {
                CheckCancellation();
                foreach (var (field, target) in Edges(region))
                {
                    CheckCancellation();
                    if (!visited.Add(target))
                        continue;
                    escapes[target] = [.. escapes.GetValueOrDefault(region) ?? [], EscapeEvidence(accesses, region, field, target)];
                    pending.Enqueue(target);
                }
            }

            var ownership = new Dictionary<string, RegionOwnership>(StringComparer.Ordinal);
            foreach (var region in heap.Regions.Values)
            {
                CheckCancellation();
                var reached = accessExecutions.GetValueOrDefault(region.Identity);
                ownership[region.Identity] = region switch
                {
                    { IsMerged: true } => new RegionOwnership(OwnershipKind.Unknown, [$"{region.Display} has a merged context."]),
                    { Kind: HeapRegionKind.Static } => new RegionOwnership(OwnershipKind.Shared, [$"{region.Display} is static storage."]),
                    _ when IsContainerWide(region) =>
                        new RegionOwnership(OwnershipKind.Shared, [$"{region.Display} is one container object for the whole scope ({region.Context})."]),
                    _ when escapes.TryGetValue(region.Identity, out var chain) => new RegionOwnership(OwnershipKind.Escaped, chain),
                    _ when reached is not null && reached.Except(Attributed(CreationExecutions(region))).Order(StringComparer.Ordinal).FirstOrDefault() is { } other =>
                        new RegionOwnership(OwnershipKind.Escaped,
                                            [$"{region.Display} is created in {Describe(Attributed(CreationExecutions(region)))} and reached from {Describe([other])}."]),
                    _ when reached is not null => new RegionOwnership(OwnershipKind.ThreadConfined, [$"{region.Display} is reached only in {Describe(reached)}."]),
                    _ => new RegionOwnership(OwnershipKind.Owned, [$"No access reaches {region.Display}."])
                };
            }

            return ownership;
        }

        /// <summary>One hop of an escape chain, with the source location that made it: the delegate creation of a capture, or the
        /// store that put the target there.</summary>
        /// <param name="accesses">The collected accesses, searched first for the store.</param>
        /// <param name="source">The region the hop starts at.</param>
        /// <param name="field">The field slot, or <c>capture</c> for a delegate capture.</param>
        /// <param name="target">The region the hop reaches.</param>
        private string EscapeEvidence(IReadOnlyList<CollectedAccess> accesses, string source, string field, string target)
        {
            if (field == "capture")
            {
                var creation = heap.Regions[source];
                return $"{heap.Regions[target].Display} is captured by {creation.Display}, which a shared region reaches" +
                       $"{At(Span(creation.SiteBodyId, creation.SiteOperationId))}.";
            }

            return $"{heap.Regions[target].Display} is stored into {FieldSlot.Name(field)} of {heap.Regions[source].Display}" +
                   $"{At(StoreSpan(accesses, source, field, target))}.";
        }

        private static string At(Analysis.SourceSpan? span) =>
            span is null ? " (no source location)" : $" at {span.Path}:{span.StartLine}";

        /// <summary>The store that put a target into a field of a region: the collected access where there is one, else any instance's
        /// store or element transfer of that slot.</summary>
        /// <param name="accesses">The accesses.</param>
        /// <param name="source">The source.</param>
        /// <param name="field">The field.</param>
        /// <param name="target">The target.</param>
        private Analysis.SourceSpan? StoreSpan(IReadOnlyList<CollectedAccess> accesses, string source, string field, string target)
        {
            if (_storesByRegion is not { } index || !ReferenceEquals(index.Accesses, accesses))
                _storesByRegion = index = new StoreIndex(accesses, accesses.Where(access => access.Access.Kind == SummaryAccessKind.Store)
                                                                         .ToLookup(access => access.RegionId, StringComparer.Ordinal));
            var collected = FirstSpan(index.Stores[source].Where(access => FieldSlot.Key(access.Access.Field) == field &&
                                                                           Resolve(heap.Instances[access.InstanceId], access.Access.Values).Contains(target))
                                                          .Select(access => access.Access.Provenance.Span));
            if (collected is not null)
                return collected;

            return FirstSpan(SlotStores[(field, source)].Where(store => store.Values.Contains(target))
                                                        .Select(store => Span(store.Instance.BodyId, store.OperationId)));
        }

        /// <summary>Every instance's stores and element stores, by the slot they write and each region they may write it in, with the
        /// values they may store: resolved once for every hop an escape chain asks about.</summary>
        private ILookup<(string Slot, string Region), SlotStore> SlotStores
        {
            get
            {
                if (_slotStores is not null)
                    return _slotStores;
                var stores = new List<SlotStore>();
                foreach (var instance in heap.Instances.Values)
                {
                    CheckCancellation();
                    foreach (var store in instance.Summary.Stores)
                    {
                        var values = Resolve(instance, store.Values);
                        stores.AddRange(Targets(instance, store).Select(region => new SlotStore(FieldSlot.Key(store.Field), region, instance, store.OperationId, values)));
                    }

                    foreach (var element in instance.Summary.Elements.Where(element => element.Kind == ElementOperationKind.Store))
                    {
                        var values = Resolve(instance, element.Values);
                        stores.AddRange(Resolve(instance, element.Arrays).Select(region => new SlotStore(element.Slot, region, instance, element.OperationId, values)));
                    }
                }

                return _slotStores = stores.ToLookup(store => (store.Slot, store.Region));
            }
        }

        private ILookup<(string Slot, string Region), SlotStore>? _slotStores;

        /// <summary>The store accesses of the list <see cref="StoreSpan"/> was last asked about, by region.</summary>
        private StoreIndex? _storesByRegion;

        /// <summary>The union of execution sets.</summary>
        /// <param name="sets">The sets.</param>
        private ExecutionSet Union(IEnumerable<ExecutionSet> sets)
        {
            var bits = Bits.EMPTY;
            foreach (var set in sets)
                Bits.UnionWith(ref bits, set.Words);
            return _ids.Set(bits);
        }

        private static Analysis.SourceSpan? FirstSpan(IEnumerable<Analysis.SourceSpan?> spans) =>
            spans.OfType<Analysis.SourceSpan>().OrderBy(span => span.Path, StringComparer.Ordinal)
                 .ThenBy(span => span.StartLine).ThenBy(span => span.StartColumn).FirstOrDefault();

        private HashSet<string> Targets(MethodInstance instance, StoreTransfer store) =>
            store.Field.IsStatic
                ? [heap.StaticRegionOf(instance.Id, store.Field)]
                : Resolve(instance, store.Bases);

        private Analysis.SourceSpan? Span(string? bodyId, int? operationId) =>
            bodyId is not null && operationId is { } operation && scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                ? body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(item => item.Id == operation)?.Provenance.Span
                : null;

        /// <summary>The nodes of each instance: one for each segment of its body and tail it runs with.</summary>
        private Dictionary<string, WalkNode[]> NodesByInstance =>
            _nodesByInstance ??= _walkNodes.Values.GroupBy(node => node.Instance, StringComparer.Ordinal)
                                           .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        private Dictionary<string, WalkNode[]>? _nodesByInstance;

        /// <summary>Each execution's visits and steps, read out of the shared graph the first time an execution is asked for: a visit is an
        /// instance with a segment of its body, by instance and segment; a step is an edge the execution takes, by caller, its segment,
        /// operation, callee and its segment.</summary>
        private void ReadExecutionLists()
        {
            var nodes = _walkNodes.Values.OrderBy(node => node.Instance, StringComparer.Ordinal).ThenBy(node => node.Segment)
                                  .Select(node => new ReachedNode(new ExecutionVisit(node.Instance, node.Segment), node.Executions, node.Steps))
                                  .ToArray();
            var visiting = Bits.EMPTY;
            var stepping = Bits.EMPTY;
            foreach (var node in nodes)
            {
                CheckCancellation();
                Bits.UnionWith(ref visiting, node.Executions);
                if (node.Steps.Count != 0)
                    Bits.UnionWith(ref stepping, node.Executions);
            }

            _visitsByExecution = VisitLists(nodes, _ids, _ids.Set(visiting).ToArray());
            _steps = StepLists(nodes, _ids, _ids.Set(stepping).ToArray());
        }

        /// <summary>What a node of the walk graph adds to the lists of the executions that reach it.</summary>
        /// <param name="Visit">The node's instance and segment.</param>
        /// <param name="Executions">The executions that reach the node, as bits.</param>
        /// <param name="Steps">The edges the node's executions take from it.</param>
        private sealed record ReachedNode(ExecutionVisit Visit, ulong[] Executions, IReadOnlyList<ExecutionStep> Steps);

        /// <summary>Each execution's visits, read the first time it is asked for. Static, so the lists keep the nodes and the numbering,
        /// and nothing else the builder holds.</summary>
        /// <param name="nodes">The nodes, by instance and segment.</param>
        /// <param name="ids">The numbering of the execution ids.</param>
        /// <param name="keys">The executions with at least one visit, in ordinal order.</param>
        private static ExecutionLists<ExecutionVisit> VisitLists(ReachedNode[] nodes, ExecutionIds ids, IReadOnlyList<string> keys) =>
            new(keys, execution => Reached(nodes, ids, execution).Select(node => node.Visit).Distinct().ToArray());

        /// <summary>Each execution's steps, read the first time it is asked for. Static, so the lists keep the nodes and the numbering,
        /// and nothing else the builder holds.</summary>
        /// <param name="nodes">The nodes, by instance and segment.</param>
        /// <param name="ids">The numbering of the execution ids.</param>
        /// <param name="keys">The executions with at least one step, in ordinal order.</param>
        private static ExecutionLists<ExecutionStep> StepLists(ReachedNode[] nodes, ExecutionIds ids, IReadOnlyList<string> keys) =>
            new(keys, execution => Reached(nodes, ids, execution).SelectMany(node => node.Steps).Distinct()
                                                                 .OrderBy(step => step.Caller, StringComparer.Ordinal).ThenBy(step => step.CallerSegment)
                                                                 .ThenBy(step => step.OperationId).ThenBy(step => step.Callee, StringComparer.Ordinal)
                                                                 .ThenBy(step => step.CalleeSegment).ToArray());

        /// <summary>The nodes an execution reaches.</summary>
        /// <param name="nodes">The nodes.</param>
        /// <param name="ids">The numbering of the execution ids.</param>
        /// <param name="execution">The execution.</param>
        private static IEnumerable<ReachedNode> Reached(ReachedNode[] nodes, ExecutionIds ids, string execution) =>
            ids.TryGetNumber(execution, out var number) ? nodes.Where(node => Bits.Contains(node.Executions, number)) : [];

        private string Describe(IEnumerable<string> executions) =>
            string.Join(", ", executions.Order(StringComparer.Ordinal)
                                        .Select(id => _executions.TryGetValue(id, out var execution) ? execution.Display : id));

        /// <summary>The instances that run a region's site in its context: the ones that create it.</summary>
        /// <param name="region">The region.</param>
        private IEnumerable<MethodInstance> CreatorsOf(HeapRegion region) =>
            (_creators ??= heap.Instances.Values.ToLookup(instance => (instance.BodyId, instance.Context)))[(region.SiteBodyId!, region.Context)];

        private ILookup<(string BodyId, string Context), MethodInstance>? _creators;

        /// <summary>The executions that create a region. An allocation or a delegate is created by the executions that run its site: an
        /// async body's prefix runs in its caller's execution and its tail, after the first await, in the tail's, so an object the tail
        /// creates is not created where the prefix runs.</summary>
        /// <param name="region">The region.</param>
        private HashSet<string> CreationExecutions(HeapRegion region) => region.Kind switch
        {
            HeapRegionKind.Allocation or HeapRegionKind.Delegate =>
                CreatorsOf(region)
                    .SelectMany(instance => (NodesByInstance.GetValueOrDefault(instance.Id) ?? [])
                                .Where(node => region.SiteOperationId is not { } site || _segments.Runs(instance.BodyId, node.Segment, site))
                                .SelectMany(node => _ids.Set(node.Executions)))
                    .ToHashSet(StringComparer.Ordinal),
            HeapRegionKind.Receiver => [region.Context],
            _ => (_regionExecutions.GetValueOrDefault(region.Identity) ?? [])
                 .Concat((heap.LocatorCreators.GetValueOrDefault(region.Identity) ?? new HashSet<string>()).SelectMany(ExecutionsOf))
                 .ToHashSet(StringComparer.Ordinal)
        };

        /// <summary>Lock identities that are one object per process: static readonly fields' objects, singleton and root-scope container
        /// objects while no registration is unresolved, and allocations outside any cycle in a body that runs once.</summary>
        private HashSet<string> SingleObjects()
        {
            var single = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static))
            {
                CheckCancellation();
                var definition = scope.Program.Decompose(region.TypeKey!).DefinitionKey;
                foreach (var field in heap.FieldsOf(region.Identity))
                {
                    CheckCancellation();
                    // The slot key carries the declaring type; the program index names the field by itself.
                    if (scope.Program.Fields.Any(candidate => candidate is { IsStatic: true, IsReadOnly: true } &&
                                                              candidate.ContainingTypeKey == definition &&
                                                              FieldSlot.WithoutTypeArguments(candidate.ContainingTypeKey) == FieldSlot.DeclaringTypeKey(field) &&
                                                              candidate.Name == FieldSlot.Name(field)))
                    {
                        single.UnionWith(heap.PointsTo(region.Identity, field));
                    }
                }
            }

            var resolved = scope.DiIndex.UnresolvedRegistrations.Count == 0;
            foreach (var region in heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Di && IsContainerWide(region) && !region.IsMerged))
            {
                CheckCancellation();
                if (resolved && !region.MayOverlapItself)
                    single.Add(region.Identity);
            }

            var once = OnceInstances();
            foreach (var region in heap.Regions.Values.Where(region => region is { Kind: HeapRegionKind.Allocation, IsMerged: false, SiteBodyId: not null }))
            {
                CheckCancellation();
                var creators = CreatorsOf(region).ToArray();
                if (creators.Length != 0 && creators.All(instance => once.Contains(instance.Id)) &&
                    scope.Reachable.Bodies.ContainsKey(region.SiteBodyId!) && !InCycle(region.SiteBodyId!, region.SiteOperationId!.Value))
                {
                    single.Add(region.Identity);
                }
            }

            return single;
        }

        /// <summary>The instances that run once per process: the constructor chains of startup and lazy constructions, type
        /// initializers, and the entries of roots that run at most once without overlapping themselves.</summary>
        private HashSet<string> OnceInstances()
        {
            var once = new HashSet<string>(StringComparer.Ordinal);
            foreach (var construction in heap.Constructions)
            {
                CheckCancellation();
                var executions = _regionExecutions.GetValueOrDefault(construction.RegionId) ?? [];
                if (!executions.Contains(STARTUP) && !executions.Any(id => id.StartsWith("construction:", StringComparison.Ordinal)))
                    continue;

                var pending = new Stack<string>(construction.ConstructorInstances);
                while (pending.TryPop(out var instanceId))
                {
                    CheckCancellation();
                    if (!once.Add(instanceId))
                        continue;
                    foreach (var edge in _edges.GetValueOrDefault(instanceId) ?? [])
                    {
                        CheckCancellation();
                        if (heap.Instances[edge.CalleeInstance] is { } callee &&
                            scope.Program.Method(callee.BodyId)?.Kind == ProgramMethodKind.Constructor && callee.Receivers.Contains(construction.RegionId))
                        {
                            pending.Push(edge.CalleeInstance);
                        }
                    }
                }
            }

            once.UnionWith(heap.TypeInitializers.Select(initializer => initializer.InstanceId));
            foreach (var root in scope.Roots.Where(root => root.InvocationPolicy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized }))
            {
                CheckCancellation();
                if (heap.RootInstances.TryGetValue(root.StableRootId, out var instance))
                    once.Add(instance);
            }

            return once;
        }

        private bool InCycle(string bodyId, int operationId)
        {
            CheckCancellation();
            if (!_inCycle.TryGetValue((bodyId, operationId), out var inCycle))
            {
                inCycle = scope.Reachable.Bodies.TryGetValue(bodyId, out var body) && InCycle(body, operationId);
                _inCycle.Add((bodyId, operationId), inCycle);
            }

            return inCycle;
        }

        private bool InCycle(IrBody body, int operationId)
        {
            CheckCancellation();
            var block = body.Blocks.FirstOrDefault(candidate => candidate.Operations.Any(operation => operation.Id == operationId));
            if (block is null)
                return false;

            var successors = body.Blocks.SelectMany(candidate => candidate.FlowPredecessors.Select(predecessor => (predecessor.BlockOrdinal, candidate.Ordinal)))
                                 .GroupBy(pair => pair.BlockOrdinal)
                                 .ToDictionary(group => group.Key, group => group.Select(pair => pair.Ordinal).ToArray());
            var seen = new HashSet<int>();
            var pending = new Stack<int>(successors.GetValueOrDefault(block.Ordinal) ?? []);
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                if (current == block.Ordinal)
                    return true;
                if (!seen.Add(current))
                    continue;
                foreach (var next in successors.GetValueOrDefault(current) ?? [])
                {
                    CheckCancellation();
                    pending.Push(next);
                }
            }

            return false;
        }
    }
}
