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

/// <summary>An access as one execution runs it on one region. A construction-local access touches the object its enclosing
/// construction builds, before the construction publishes it, and never pairs.</summary>
/// <param name="ExecutionId">The execution running the access.</param>
/// <param name="InstanceId">The method instance making the access.</param>
/// <param name="Access">The access from the instance's summary.</param>
/// <param name="RegionId">The region the access touches.</param>
/// <param name="IsConstructionLocal">Whether the access is construction-local.</param>
public sealed record CollectedAccess(string ExecutionId, string InstanceId, SummaryAccess Access, string RegionId, bool IsConstructionLocal);

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

    /// <summary>Every access an execution runs, per region; startup's are kept like any other, and happens-before orders them before
    /// what runs after startup.</summary>
    public IReadOnlyList<CollectedAccess> Accesses { get; }

    public IReadOnlyDictionary<string, RegionOwnership> Ownership { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }

    /// <summary>The visits accepted by the execution walk across all entries of this scope.</summary>
    public int WalkVisits { get; init; }

    internal IReadOnlyDictionary<(string Execution, string Instance, BodySegment Segment, string? Tail), int> WalkNodeVisits { get; init; } =
        new Dictionary<(string, string, BodySegment, string?), int>();

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
/// Accesses are collected by walking the call edges from each execution's entries, carrying the objects whose constructor
/// chain is running (ADR 0006).
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
        private readonly Dictionary<string, HashSet<string>> _instanceExecutions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DelegateHandoff> _handoffs = heap.DelegateHandoffs.ToDictionary(handoff => handoff.RegionId, StringComparer.Ordinal);
        private readonly Dictionary<string, string> _delegateDisplays = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StartupDelegate[]> _startupDelegates = heap.StartupDelegates.GroupBy(fated => fated.CallerInstance, StringComparer.Ordinal)
                                                                                        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        /// <summary>For each delegate a known call runs by a <c>startup</c> fate, the executions other than startup that make that call,
        /// with its site: each hands the delegate to its unknown call as an unresolved call would.</summary>
        private readonly Dictionary<string, HashSet<(string Execution, string BodyId, int OperationId)>> _fateHandings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _chains = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Execution, string Instance, BodySegment Segment, string? Tail), WalkNode> _walkNodes = [];
        private int _walkPostorder;
        private readonly List<(string Execution, string Instance, ConstructionSets Intervals, BodySegment Segment)> _visitList = [];
        private readonly Dictionary<string, int> _objectIds = new(StringComparer.Ordinal);
        private readonly List<string> _objects = [];
        private Dictionary<string, IReadOnlyList<ExecutionVisit>> _visitsByExecution = new(StringComparer.Ordinal);
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
        private readonly Dictionary<string, HashSet<ExecutionStep>> _steps = new(StringComparer.Ordinal);
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
        private readonly Dictionary<string, List<HashSet<string>>> _returns = new(StringComparer.Ordinal);
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
                while (_pendingEntries.Count != 0)
                {
                    CheckCancellation();
                    var pending = NextEntry();
                    Walk(pending.Execution, pending.Entry, pending.Tail);
                }
                foreach (var site in _subscriptions.ToArray())
                {
                    CheckCancellation();
                    Subscribed(site);
                }
            }
            while (_pendingEntries.Count != 0);

            CollectChains();
            if (_entries.ContainsKey(STARTUP))
                Add(new ExecutionInstance(STARTUP, ExecutionKind.Startup, "host startup", AT_MOST_ONCE, null, null) { TreeRootId = STARTUP });
            CanonicalizeWalk();

            _visitsByExecution = _visitList.GroupBy(visit => visit.Execution, StringComparer.Ordinal)
                                          .ToDictionary(group => group.Key,
                                                        group => (IReadOnlyList<ExecutionVisit>)group.Select(visit => new ExecutionVisit(visit.Instance, visit.Segment))
                                                                                                     .Distinct().ToArray(),
                                                        StringComparer.Ordinal);

            FinishSpawnedExecutions();
            FinishUnknownDelegateCalls();

            // Ownership reads which executions reach a region, which publication does not change; publication in turn asks whether
            // the object handed to an unresolved call is shared (R2).
            var ownership = Ownership(Collect(new HashSet<string>(StringComparer.Ordinal)));
            var published = Published(ownership);
            var accesses = Collect(published);
            var executions = _executions.Values.OrderBy(execution => execution.Id, StringComparer.Ordinal).ToArray();
            var entries = _entries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExecutionEntry>)pair.Value, StringComparer.Ordinal);
            var steps = _steps.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExecutionStep>)pair.Value.ToArray(), StringComparer.Ordinal);
            var order = new HappensBefore(scope, heap, _segments, executions.ToDictionary(execution => execution.Id, StringComparer.Ordinal), entries,
                                          _visitsByExecution, steps, _anchors.ToArray(), _tails, DuringStartup(), cancellationToken);
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
                _instanceExecutions.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
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

        /// <summary>Adds an entry once and walks each distinct tail; a prefix entry names the execution its awaited callees' tails join.</summary>
        /// <param name="execution">The execution entering the body.</param>
        /// <param name="entry">The body segment and construction interval on entry.</param>
        /// <param name="tail">The execution its awaited callees' tails join.</param>
        private void Entry(string execution, ExecutionEntry entry, string? tail = null)
        {
            if (!_entries.TryGetValue(execution, out var entries))
                _entries.Add(execution, entries = []);
            if (!entries.Contains(entry))
                entries.Add(entry);
            if (_entryWalks.Add((execution, entry, tail)))
                _pendingEntries.Enqueue((execution, entry, tail));
        }

        private (string Execution, ExecutionEntry Entry, string? Tail) NextEntry()
        {
            if (walkOrder is null)
                return _pendingEntries.Dequeue();
            var entries = Permute(_pendingEntries).ToArray();
            _pendingEntries.Clear();
            foreach (var entry in entries.Skip(1))
                _pendingEntries.Enqueue(entry);
            return entries[0];
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
            // Visits of one execution, instance and segment differ only by tail, which no reader tells apart: the visits of an
            // execution keep one of them, and Collect sorts what it collects by the key it deduplicates on.
            OrderList(_visitList, _visitList.OrderBy(visit => visit.Execution, StringComparer.Ordinal)
                                           .ThenBy(visit => visit.Instance, StringComparer.Ordinal)
                                           .ThenBy(visit => visit.Segment));
            foreach (var entries in _entries.Values)
                OrderList(entries, entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.IntervalObject, StringComparer.Ordinal)
                                          .ThenBy(entry => entry.InstanceId, StringComparer.Ordinal).ThenBy(entry => entry.Segment));
            foreach (var steps in _steps.Values)
                OrderSet(steps, steps.OrderBy(step => step.Caller, StringComparer.Ordinal).ThenBy(step => step.CallerSegment)
                                     .ThenBy(step => step.OperationId).ThenBy(step => step.Callee, StringComparer.Ordinal)
                                     .ThenBy(step => step.CalleeSegment));
            foreach (var sets in new[] { _instanceExecutions, _chains, _regionExecutions })
            {
                foreach (var set in sets.Values)
                    OrderSet(set, set.Order(StringComparer.Ordinal));
                OrderDictionary(sets);
            }
            foreach (var handings in _fateHandings.Values)
                OrderSet(handings, handings.OrderBy(handing => handing.Execution, StringComparer.Ordinal)
                                          .ThenBy(handing => handing.BodyId, StringComparer.Ordinal).ThenBy(handing => handing.OperationId));
            OrderSet(_anchors, _anchors.OrderBy(anchor => anchor.ExecutionId, StringComparer.Ordinal)
                                      .ThenBy(anchor => anchor.CallerInstance, StringComparer.Ordinal).ThenBy(anchor => anchor.OperationId)
                                      .ThenBy(anchor => anchor.ChildId, StringComparer.Ordinal).ThenBy(anchor => anchor.Kind));
            foreach (var sites in _timerSites.Values)
                OrderList(sites, sites.OrderBy(site => site.CallerInstance, StringComparer.Ordinal).ThenBy(site => site.OperationId));
            OrderDictionary(_entries);
            OrderDictionary(_steps);
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
                        foreach (var execution in (_instanceExecutions.GetValueOrDefault(creator.Id) ?? []).ToArray())
                        {
                            CheckCancellation();
                            Timer(execution, creator, site, operationId);
                        }
                    }
                }
                else
                {
                    foreach (var execution in (_instanceExecutions.GetValueOrDefault(subscriber.Id) ?? []).ToArray())
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
            var tail = Spawned(execution, $"tail:{origin.BodyId}#{origin.OperationId}", spawned.Kind, $"{spawned.Display} after its first await",
                               origin with { IsTail = true }, alwaysRepeated: false);
            if (tail != execution)
                _tails.TryAdd(execution, tail);
            Entry(execution, new ExecutionEntry(calleeId, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Prefix }, tail);
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
            var child = Spawned(parent, $"async:{caller.BodyId}#{operationId}", ExecutionKind.Spawn, $"{api} in {symbol}",
                                new SpawnOrigin(api, symbol, caller.BodyId, operationId, false), alwaysRepeated: false);
            _anchors.Add(new SpawnAnchor(parent, caller.Id, operationId, child, SpawnAnchorKind.AsyncReturn));
            return child;
        }

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
        private IEnumerable<(string Execution, string BodyId, int OperationId)> Handings(string region) =>
            (_handoffs.TryGetValue(region, out var handoff)
                ? handoff.Sites.SelectMany(site => (_instanceExecutions.GetValueOrDefault(site.CallerInstance) ?? [])
                                               .Select(handing => (handing, heap.Instances[site.CallerInstance].BodyId, site.OperationId)))
                : [])
            .Concat(_fateHandings.GetValueOrDefault(region) ?? []);

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
            handings.Add((execution, caller.BodyId, fated.OperationId));
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
                                                                      : _instanceExecutions.GetValueOrDefault(creators[execution.Subject!]) ?? [])
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
            _instanceExecutions.GetValueOrDefault(site.CallerInstance) is { Count: 1 } executions && RunsOnce(executions.First()) &&
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

            var runs = activations.SelectMany(step => (_instanceExecutions.GetValueOrDefault(step.InstanceId) ?? [])
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

        /// <summary>Whether a construction set holds a region; one the walk never met is in no set.</summary>
        /// <param name="set">The construction set.</param>
        /// <param name="object">The region id.</param>
        private bool Holds(ulong[] set, string @object) => _objectIds.TryGetValue(@object, out var id) && ObjectSet.Contains(set, id);

        /// <summary>A set of object ids as bits, a word past its end reading as zero. A set is never changed in place: an operation
        /// that leaves its first operand as it is returns that operand, so a node shares its predecessor's set until they differ.</summary>
        private static class ObjectSet
        {
            internal static readonly ulong[] EMPTY = [];

            internal static ulong[] Of(IEnumerable<int> ids)
            {
                var set = EMPTY;
                foreach (var id in ids)
                {
                    if (id >> 6 >= set.Length)
                        Array.Resize(ref set, (id >> 6) + 1);
                    set[id >> 6] |= 1UL << (id & 63);
                }

                return set;
            }

            internal static bool Contains(ulong[] set, int id) => id >> 6 < set.Length && (set[id >> 6] & (1UL << (id & 63))) != 0;

            internal static IEnumerable<int> Ids(ulong[] set)
            {
                for (var index = 0; index < set.Length; index++)
                {
                    for (var word = set[index]; word != 0; word &= word - 1)
                        yield return (index << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                }
            }

            internal static bool IsSubset(ulong[] subset, ulong[] superset)
            {
                for (var index = 0; index < subset.Length; index++)
                {
                    if ((subset[index] & ~(index < superset.Length ? superset[index] : 0)) != 0)
                        return false;
                }

                return true;
            }

            internal static ulong[] Union(ulong[] first, ulong[] second)
            {
                if (IsSubset(second, first))
                    return first;
                var union = new ulong[Math.Max(first.Length, second.Length)];
                for (var index = 0; index < union.Length; index++)
                    union[index] = (index < first.Length ? first[index] : 0) | (index < second.Length ? second[index] : 0);
                return union;
            }

            internal static ulong[] Intersect(ulong[] first, ulong[] second)
            {
                if (IsSubset(first, second))
                    return first;
                var intersection = new ulong[Math.Min(first.Length, second.Length)];
                for (var index = 0; index < intersection.Length; index++)
                    intersection[index] = first[index] & second[index];
                return intersection;
            }
        }

        private sealed class ConstructionSets(ulong[] mayIn, ulong[] mustIn)
        {
            internal ulong[] MayIn { get; private set; } = mayIn;
            internal ulong[] MustIn { get; private set; } = mustIn;

            internal bool Merge(ConstructionSets incoming)
            {
                if (ObjectSet.IsSubset(incoming.MayIn, MayIn) && ObjectSet.IsSubset(MustIn, incoming.MustIn))
                    return false;
                var may = ObjectSet.Union(MayIn, incoming.MayIn);
                var must = ObjectSet.Intersect(MustIn, incoming.MustIn);
                if (ReferenceEquals(may, MayIn) && ReferenceEquals(must, MustIn))
                    return false;
                MayIn = may;
                MustIn = must;
                return true;
            }
        }

        private sealed class WalkNode(string instance, BodySegment segment, string? tail)
        {
            internal string Instance { get; } = instance;
            internal BodySegment Segment { get; } = segment;
            internal string? Tail { get; } = tail;
            internal List<(WalkNode Node, ulong[] Added)> Successors { get; } = [];
            internal ConstructionSets? Intervals { get; set; }
            internal bool Discovered { get; set; }
            internal bool Pending { get; set; }
            internal int Postorder { get; set; }
            internal int Visits { get; set; }
        }

        /// <summary>Propagates construction sets per (execution, instance, segment, tail). An unset MustIn means every object;
        /// the first incoming set initializes it. Readers ask only whether R is inside on some path and outside on some path.
        /// A reader asking about two objects on the same path would need the per-path sets these two sets replace.</summary>
        /// <param name="execution">The execution being walked.</param>
        /// <param name="entry">The entry and its initial construction interval.</param>
        /// <param name="tail">The async tail associated with a prefix, if any.</param>
        private void Walk(string execution, ExecutionEntry entry, string? tail)
        {
            CheckCancellation();
            var first = Node(entry.InstanceId, entry.Segment, tail);
            if (first is null)
                return;

            // Discover each node once, caching its set-independent effects and edge transfers.
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
                        Spawn(execution, instance, site);
                }

                foreach (var site in _timers.GetValueOrDefault(item.Instance) ?? [])
                {
                    CheckCancellation();
                    if (!_segments.Runs(instance.BodyId, item.Segment, site.OperationId))
                        continue;
                    if (site.Action == IrTimerAction.ElapsedSubscribe)
                        _subscriptions.Add(site);
                    else
                        Timer(execution, instance, site, site.OperationId);
                }

                foreach (var fated in _startupDelegates.GetValueOrDefault(item.Instance) ?? [])
                {
                    CheckCancellation();
                    if (_segments.Runs(instance.BodyId, item.Segment, fated.OperationId))
                        StartupDelegate(execution, instance, fated);
                }

                if (!_instanceExecutions.TryGetValue(item.Instance, out var executions))
                    _instanceExecutions.Add(item.Instance, executions = new HashSet<string>(StringComparer.Ordinal));
                executions.Add(execution);

                foreach (var edge in Permute(_edges.GetValueOrDefault(item.Instance) ?? []))
                {
                    CheckCancellation();
                    if (_segments.Follow(instance, item.Segment, edge) is not { } calleeSegment)
                        continue;

                    var calleeTail = calleeSegment == BodySegment.Prefix ? item.Tail : null;
                    if (_segments.IsAsyncSpawn(item.Instance, edge.OperationId, edge.CalleeInstance))
                    {
                        calleeTail = AsyncChild(execution, instance, edge.OperationId);
                        Entry(calleeTail, new ExecutionEntry(edge.CalleeInstance, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Tail });
                    }
                    else if (calleeSegment == BodySegment.Prefix && calleeTail is not null)
                    {
                        Entry(calleeTail, new ExecutionEntry(edge.CalleeInstance, ExecutionEntryKind.Spawn, null) { Segment = BodySegment.Tail });
                    }
                    else if (calleeSegment == BodySegment.Prefix)
                    {
                        calleeSegment = BodySegment.Whole;
                    }

                    if (!_steps.TryGetValue(execution, out var steps))
                        _steps.Add(execution, steps = []);
                    steps.Add(new ExecutionStep(item.Instance, item.Segment, edge.OperationId, edge.CalleeInstance, calleeSegment));
                    // The transfer adds only this edge's objects, independently of its incoming sets.
                    var added = edge.Reason == WholeProgram.CONSTRUCTION_REASON && _constructed.TryGetValue(edge.CalleeInstance, out var constructed)
                        ? [constructed]
                        : instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } constructor
                            ? constructor.Receivers.SelectMany(value => heap.Resolve(instance.Id, value))
                                         .Where(region => heap.Regions[region].Kind == HeapRegionKind.Allocation).ToArray()
                            : Array.Empty<string>();
                    if (Node(edge.CalleeInstance, calleeSegment, calleeTail) is { } callee)
                        item.Successors.Add((callee, added.Length == 0 ? ObjectSet.EMPTY : ObjectSet.Of(added.Select(ObjectId))));
                }

                discovery.Push((item, true));
                foreach (var successor in item.Successors)
                    discovery.Push((successor.Node, false));
            }

            // Merge on arrival and keep one pending walk per node, in reverse postorder.
            var pending = new PriorityQueue<WalkNode, int>();
            var intervals = entry.IntervalObject is { } interval ? ObjectSet.Of([ObjectId(interval)]) : ObjectSet.EMPTY;
            Merge(first, new ConstructionSets(intervals, intervals));
            while (pending.TryDequeue(out var item, out _))
            {
                CheckCancellation();
                item.Pending = false;
                WalkVisits++;
                item.Visits++;
                var current = item.Intervals!;
                var mayIn = current.MayIn;
                var mustIn = current.MustIn;
                foreach (var (callee, added) in item.Successors)
                {
                    CheckCancellation();
                    var incoming = added.Length == 0
                        ? new ConstructionSets(mayIn, mustIn)
                        : new ConstructionSets(ObjectSet.Union(mayIn, added), ObjectSet.Union(mustIn, added));
                    Merge(callee, incoming);
                }
            }

            WalkNode? Node(string instance, BodySegment segment, string? nodeTail)
            {
                if (!heap.Instances.ContainsKey(instance))
                    return null;
                var key = (execution, instance, segment, nodeTail);
                if (!_walkNodes.TryGetValue(key, out var node))
                    _walkNodes.Add(key, node = new WalkNode(instance, segment, nodeTail));
                return node;
            }

            void Merge(WalkNode node, ConstructionSets incoming)
            {
                if (node.Intervals is { } known)
                {
                    if (!known.Merge(incoming))
                        return;
                }
                else
                {
                    node.Intervals = incoming;
                    _visitList.Add((execution, node.Instance, incoming, node.Segment));
                }
                if (!node.Pending)
                {
                    node.Pending = true;
                    pending.Enqueue(node, -node.Postorder);
                }
            }
        }

        /// <summary>Each object's constructor chain: the instances of the walk nodes whose MayIn holds it. A node's MayIn only grows and
        /// every change is walked, so its final MayIn holds every object it was walked with.</summary>
        private void CollectChains()
        {
            var objects = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
            foreach (var node in _walkNodes.Values)
            {
                CheckCancellation();
                if (node.Intervals is { } intervals)
                    objects[node.Instance] = objects.TryGetValue(node.Instance, out var known) ? ObjectSet.Union(known, intervals.MayIn) : intervals.MayIn;
            }

            foreach (var (instance, ids) in objects)
            {
                CheckCancellation();
                foreach (var id in ObjectSet.Ids(ids))
                {
                    if (!_chains.TryGetValue(_objects[id], out var chain))
                        _chains.Add(_objects[id], chain = new HashSet<string>(StringComparer.Ordinal));
                    chain.Add(instance);
                }
            }
        }

        /// <summary>The objects whose construction publishes them: an instance of the chain stores the object, or a delegate capturing
        /// it, into a region that is neither the object nor reachable from it, returns it to a caller outside the chain, or hands a
        /// shared object to an unresolved call.</summary>
        /// <param name="ownership">The ownership.</param>
        private HashSet<string> Published(IReadOnlyDictionary<string, RegionOwnership> ownership)
        {
            var published = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (@object, chain) in _chains)
            {
                CheckCancellation();
                if (!heap.Regions.TryGetValue(@object, out var region) || region.Kind == HeapRegionKind.Static)
                    continue;

                var inside = Closure([@object]);
                foreach (var instanceId in chain)
                {
                    CheckCancellation();
                    var instance = heap.Instances[instanceId];
                    foreach (var (stored, targets) in Stores(instance))
                    {
                        CheckCancellation();
                        var exposes = stored.Contains(@object) ||
                                      stored.Any(value => heap.Regions[value].Kind == HeapRegionKind.Delegate && heap.DelegateCaptures(value).Contains(@object));
                        if (exposes && targets.Any(target => target != @object && !IsInternal(@object, inside, target)))
                        {
                            published.Add(@object);
                        }
                    }

                    if (Returns(instance, @object) && (CallersOf.GetValueOrDefault(instanceId) ?? []).Any(caller => !chain.Contains(caller)))
                    {
                        published.Add(@object);
                    }

                    // Handing a shared object to a call the analysis cannot follow publishes it: the call may keep it anywhere. An object
                    // of one execution it only reads, and leaves where it was (R2, ADR 0006).
                    if (ownership[@object].Kind is OwnershipKind.Escaped or OwnershipKind.Shared or OwnershipKind.Unknown &&
                        UnknownReach.Any(item => item.Call.Instance.Id == instanceId && item.Regions.Contains(@object)))
                        published.Add(@object);
                }
            }

            return published;
        }

        /// <summary>What each store and element store of an instance stores and where, resolved once for every object whose chain
        /// holds the instance, in the order a single object resolved them.</summary>
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

        /// <summary>Whether an instance returns an object. Its returns are resolved in order and only as far as some object asks, so
        /// the heap resolves exactly the values it would resolve if every object asked on its own.</summary>
        /// <param name="instance">The instance of a constructor chain.</param>
        /// <param name="object">The object under construction.</param>
        private bool Returns(MethodInstance instance, string @object)
        {
            if (!_returns.TryGetValue(instance.Id, out var resolved))
                _returns.Add(instance.Id, resolved = []);
            var returns = instance.Summary.Returns;
            for (var index = 0; index < returns.Count; index++)
            {
                CheckCancellation();
                if (index == resolved.Count)
                    resolved.Add(Resolve(instance, returns[index].Values));
                if (resolved[index].Contains(@object))
                    return true;
            }

            return false;
        }

        /// <summary>The callers of each instance over the heap's call edges.</summary>
        private Dictionary<string, string[]> CallersOf =>
            _callersOf ??= heap.Edges.GroupBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                               .ToDictionary(group => group.Key, group => group.Select(edge => edge.CallerInstance).ToArray(), StringComparer.Ordinal);

        /// <summary>A target reachable only through the object under construction: an object or delegate reachable from it that
        /// shared storage does not also reach, or a container object the container resolved for it, whose context names it as the
        /// resolver. A singleton or a root-scope object is shared with everything else, so it is never internal.</summary>
        /// <param name="object">The object under construction.</param>
        /// <param name="inside">The regions reachable from the object.</param>
        /// <param name="target">The region a store puts the object into.</param>
        private bool IsInternal(string @object, IReadOnlySet<string> inside, string target) =>
            heap.Regions[target] is var region && !SharedReach(@object).Contains(target) &&
            (inside.Contains(target) && region.Kind is HeapRegionKind.Allocation or HeapRegionKind.Delegate ||
             region.Kind == HeapRegionKind.Di && region.Context.StartsWith($"{@object}|", StringComparison.Ordinal));

        /// <summary>Every region shared storage reaches: static storage, singletons and root-scope objects, and what they hold. The
        /// object under construction is left out as a root, since what it holds is what this asks about.</summary>
        /// <param name="object">The object under construction.</param>
        private HashSet<string> SharedReach(string @object) =>
            _sharedReach.TryGetValue(@object, out var reach)
                ? reach
                : _sharedReach[@object] = Closure(SharedRoots().Where(root => root != @object));

        /// <summary>The unresolved calls with unknown effects and the regions each reaches (R1): a construction that hands them its
        /// shared object publishes it (R2). A locator has no unknown effect.</summary>
        private IReadOnlyList<(UnknownCall Call, IReadOnlySet<string> Regions)> UnknownReach
        {
            get
            {
                if (_unknownReach is not null)
                    return _unknownReach;
                var reach = new UnknownCalls.Reach(scope, heap);
                return _unknownReach = UnknownCalls.Of(scope, heap).Where(call => !call.IsLocator)
                                                   .Select(call => (call, reach.Of(call).Regions))
                                                   .Where(item => item.Regions.Count != 0)
                                                   .ToArray();
            }
        }

        private IReadOnlyList<(UnknownCall Call, IReadOnlySet<string> Regions)>? _unknownReach;

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

        private IEnumerable<(string Field, string Target)> Edges(string region) =>
            heap.FieldsOf(region).SelectMany(field => heap.PointsTo(region, field).Select(target => (field, target)))
                .Concat(heap.Regions[region].Kind == HeapRegionKind.Delegate
                            ? heap.DelegateCaptures(region).Select(target => ("capture", target))
                            : []);

        private IEnumerable<string> Successors(string region) => Edges(region).Select(edge => edge.Target);

        private IReadOnlyList<CollectedAccess> Collect(IReadOnlySet<string> published)
        {
            var accesses = new List<CollectedAccess>();
            var seen = new HashSet<(string, string, int, string, bool)>();
            foreach (var (execution, instanceId, intervals, segment) in _visitList)
            {
                CheckCancellation();
                var instance = heap.Instances[instanceId];
                foreach (var access in instance.Summary.Accesses.Where(access => _segments.Runs(instance.BodyId, segment, access.OperationId)))
                {
                    CheckCancellation();
                    var regions = access.Field.IsStatic
                        ? new HashSet<string> { heap.StaticRegionOf(instanceId, access.Field) }
                        : Resolve(instance, access.Bases);
                    foreach (var region in regions)
                    {
                        CheckCancellation();
                        if (Holds(intervals.MayIn, region) && !published.Contains(region) &&
                            seen.Add((execution, instanceId, access.OperationId, region, true)))
                            accesses.Add(new CollectedAccess(execution, instanceId, access, region, true));
                        if ((!Holds(intervals.MustIn, region) || published.Contains(region)) &&
                            seen.Add((execution, instanceId, access.OperationId, region, false)))
                            accesses.Add(new CollectedAccess(execution, instanceId, access, region, false));
                    }
                }
            }

            return accesses.OrderBy(access => access.ExecutionId, StringComparer.Ordinal).ThenBy(access => access.InstanceId, StringComparer.Ordinal)
                           .ThenBy(access => access.Access.OperationId).ThenBy(access => access.RegionId, StringComparer.Ordinal)
                           .ThenBy(access => access.IsConstructionLocal).ToArray();
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
                                           .ToDictionary(group => group.Key, group => Attributed(group.Select(access => access.ExecutionId)),
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
            var collected = FirstSpan(accesses.Where(access => access.Access.Kind == SummaryAccessKind.Store && access.RegionId == source &&
                                                              FieldSlot.Key(access.Access.Field) == field &&
                                                              Resolve(heap.Instances[access.InstanceId], access.Access.Values).Contains(target))
                                              .Select(access => access.Access.Provenance.Span));
            if (collected is not null)
                return collected;

            var spans = new List<Analysis.SourceSpan>();
            foreach (var instance in heap.Instances.Values)
            {
                CheckCancellation();
                var stores = instance.Summary.Stores
                                     .Where(store => FieldSlot.Key(store.Field) == field && Resolve(instance, store.Values).Contains(target) &&
                                                     Targets(instance, store).Contains(source))
                                     .Select(store => store.OperationId)
                                     .Concat(instance.Summary.Elements
                                                     .Where(element => field == element.Slot && element.Kind == ElementOperationKind.Store &&
                                                                       Resolve(instance, element.Values).Contains(target) &&
                                                                       Resolve(instance, element.Arrays).Contains(source))
                                                     .Select(element => element.OperationId));
                spans.AddRange(stores.Select(operation => Span(instance.BodyId, operation)).OfType<Analysis.SourceSpan>());
            }

            return FirstSpan(spans);
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

        /// <summary>The visits of each instance: the executions it runs in, each with the segment of its body it runs there.</summary>
        private Dictionary<string, (string Execution, BodySegment Segment)[]> VisitsByInstance =>
            _visitsByInstance ??= _visitList.GroupBy(visit => visit.Instance, StringComparer.Ordinal)
                                            .ToDictionary(group => group.Key, group => group.Select(visit => (visit.Execution, visit.Segment)).Distinct().ToArray(),
                                                          StringComparer.Ordinal);

        private Dictionary<string, (string Execution, BodySegment Segment)[]>? _visitsByInstance;

        private string Describe(IEnumerable<string> executions) =>
            string.Join(", ", executions.Order(StringComparer.Ordinal)
                                        .Select(id => _executions.TryGetValue(id, out var execution) ? execution.Display : id));

        /// <summary>The executions that create a region. An allocation or a delegate is created by the executions that run its site: an
        /// async body's prefix runs in its caller's execution and its tail, after the first await, in the tail's, so an object the tail
        /// creates is not created where the prefix runs.</summary>
        /// <param name="region">The region.</param>
        private HashSet<string> CreationExecutions(HeapRegion region) => region.Kind switch
        {
            HeapRegionKind.Allocation or HeapRegionKind.Delegate =>
                heap.Instances.Values.Where(instance => instance.BodyId == region.SiteBodyId && instance.Context == region.Context)
                    .SelectMany(instance => (VisitsByInstance.GetValueOrDefault(instance.Id) ?? [])
                                .Where(visit => region.SiteOperationId is not { } site || _segments.Runs(instance.BodyId, visit.Segment, site))
                                .Select(visit => visit.Execution))
                    .ToHashSet(StringComparer.Ordinal),
            HeapRegionKind.Receiver => [region.Context],
            _ => (_regionExecutions.GetValueOrDefault(region.Identity) ?? [])
                 .Concat((heap.LocatorCreators.GetValueOrDefault(region.Identity) ?? new HashSet<string>())
                         .SelectMany(instance => _instanceExecutions.GetValueOrDefault(instance) ?? []))
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
                var creators = heap.Instances.Values.Where(instance => instance.BodyId == region.SiteBodyId && instance.Context == region.Context).ToArray();
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
