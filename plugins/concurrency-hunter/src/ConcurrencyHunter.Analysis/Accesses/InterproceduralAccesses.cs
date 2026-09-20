using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Accesses;

public static class CoverageCounters
{
    public const string REACHABLE_BODIES = "reachable-bodies";
    public const string SCC_BUDGET_EXCEEDED = "scc-budget-exceeded";
    public const string OPAQUE_CALL = "opaque-call";
    public const string DELEGATE_TO_OPAQUE = "delegate-to-opaque";
    public const string ELEMENT_OPERATION = "element-operation";
    public const string UNANALYSED_REGISTRATION = "unanalysed-registration";
    public const string NO_RECEIVER_OBJECT = "no-receiver-object";
    public const string MERGED_CONTEXT = "merged-context";
    public const string WILDCARD_ACCESS = "wildcard-access";
    public const string UNRESOLVED_LOCATOR = "unresolved-locator";
}

/// <summary>What the execution model ordered and started, which the report shows on lines of their own: the distinct spawn sites
/// (all APIs), timer creation sites per kind, and join operations without proven identity.</summary>
public static class OrderingCounters
{
    public const string SPAWN_SITES = "spawn-sites";
    public const string UNPROVEN_JOINS = "unproven-joins";
    public const string TIMERS_DISABLED = "timers-disabled";
    public const string TIMERS_ONE_SHOT = "timers-one-shot";
    public const string TIMERS_PERIODIC = "timers-periodic";

    /// <summary>The timer counters, widest last: a creation site several scopes reach is counted in the widest kind any of them gives it.</summary>
    public static IReadOnlyList<string> TIMER_KINDS { get; } = [TIMERS_DISABLED, TIMERS_ONE_SHOT, TIMERS_PERIODIC];
}

/// <summary>One operation of one body: what makes a site the same site across scopes, so that a library site two executable scopes
/// reach is one site, not two.</summary>
public sealed record OperationSite(string BodyId, int OperationId);

/// <summary>A spawn or async call site with the API it calls.</summary>
public sealed record SpawnSiteCoverage(OperationSite Site, string Api);

/// <summary>A timer creation site with the counter (<see cref="OrderingCounters.TIMER_KINDS"/>) of the widest kind it runs with.</summary>
public sealed record TimerSiteCoverage(OperationSite Site, string Counter);

/// <summary>What a scope's analysis covered, over what the heap reaches only; <see cref="LoweredNotReached"/> and
/// <see cref="OutsideLoweredSet"/> are inventory, not counted against coverage.</summary>
public sealed record InterproceduralCoverage(string ScopeId, IReadOnlyDictionary<string, int> Counters,
                                             IReadOnlyList<(string Callee, int Count)> TopOpaqueCallees, IReadOnlyList<string> LoweredNotReached,
                                             IReadOnlyList<UnreachedMember> OutsideLoweredSet)
{
    /// <summary>The <see cref="OrderingCounters"/>, apart from the generic counters.</summary>
    public IReadOnlyDictionary<string, int> Ordering { get; init; } = new Dictionary<string, int>();

    /// <summary>The sites behind the ordering counters, by source, so that the report counts a site two scopes share once.</summary>
    public IReadOnlyList<SpawnSiteCoverage> SpawnSites { get; init; } = [];

    public IReadOnlyList<TimerSiteCoverage> TimerSites { get; init; } = [];
    public IReadOnlyList<OperationSite> UnprovenJoins { get; init; } = [];
}

public sealed record InterproceduralCollection(IReadOnlyList<Access> Accesses, InterproceduralCoverage Coverage);

public sealed record InterproceduralInput(ScopeProgram Scope, HeapSolution Heap, ExecutionAnalysis Executions);

/// <summary>
/// Collects every access each execution runs, breadth-first from its entries over the heap's call edges, with the construction
/// interval each instance runs in, the locks it must hold (a must-dataflow over its incoming edges), and the loads its stores
/// depend on across calls and capture cells, so a lost update is one read-modify-write access (R10).
/// </summary>
public static class InterproceduralAccesses
{
    private const int TOP_CALLEES = 20;
    private const string CONSTRUCTION_PROVIDER = "construction";

    public static InterproceduralCollection Collect(InterproceduralInput input)
    {
        var accesses = new List<Access>();
        var graph = new WalkGraph(input);
        var discoveries = Discoveries(input, graph);
        foreach (var execution in input.Executions.Executions)
        {
            if (input.Executions.Entries.TryGetValue(execution.Id, out var entries))
                accesses.AddRange(new ExecutionCollector(input, execution, entries, graph, discoveries).Collect());
        }

        return new InterproceduralCollection(accesses, Coverage(input, accesses));
    }

    private static InterproceduralCoverage Coverage(InterproceduralInput input, IReadOnlyList<Access> accesses)
    {
        var heap = input.Heap;
        var summaries = heap.Instances.Values.GroupBy(instance => instance.BodyId, StringComparer.Ordinal)
                            .Select(group => group.First().Summary)
                            .ToArray();
        var opaque = summaries.SelectMany(summary => summary.OpaqueCalls.Select(call => (summary.BodyId, Call: call))).ToArray();
        var reachedMembers = input.Scope.Reachable.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var memberOf = input.Scope.Program.Methods.SelectMany(method => method.NestedBodyIds.Select(nested => (Nested: nested, method.MethodId)))
                            .GroupBy(pair => pair.Nested, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.First().MethodId, StringComparer.Ordinal);
        // A supported factory registration's delegate runs as its region's construction, so it is not handed to an opaque call.
        var factorySites = input.Scope.DiIndex.Registrations
                                .Where(registration => registration is { IsSupported: true, Form: DiRegistrationForm.Factory, BodyId: not null, OperationId: not null })
                                .Select(registration => (registration.BodyId!, registration.OperationId!.Value))
                                .ToHashSet();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoverageCounters.REACHABLE_BODIES] = heap.ReachableBodies.Count,
            [CoverageCounters.SCC_BUDGET_EXCEEDED] = heap.Counters.GetValueOrDefault(HeapCounters.SCC_BUDGET_EXCEEDED),
            [CoverageCounters.OPAQUE_CALL] = opaque.Length,
            // The delegates handed to opaque calls, not the calls: one creation passed to two calls is one delegate.
            [CoverageCounters.DELEGATE_TO_OPAQUE] = opaque.Where(item => !factorySites.Contains((memberOf.GetValueOrDefault(item.BodyId) ?? item.BodyId, item.Call.OperationId)))
                                                          .SelectMany(item => item.Call.Delegates.Select(delegateValue => (item.BodyId, delegateValue)))
                                                          .Distinct()
                                                          .Count(),
            [CoverageCounters.ELEMENT_OPERATION] = summaries.Sum(summary => summary.Elements.Count),
            // Unsupported registrations whose holding member the reachable set reaches.
            [CoverageCounters.UNANALYSED_REGISTRATION] = input.Scope.DiIndex.Registrations
                                                              .Count(registration => !registration.IsSupported && registration.BodyId is { } member &&
                                                                                     reachedMembers.Contains(member)),
            [CoverageCounters.UNRESOLVED_LOCATOR] = heap.UnresolvedLocators.Count,
            [CoverageCounters.NO_RECEIVER_OBJECT] = heap.Counters.GetValueOrDefault(HeapCounters.NO_RECEIVER_OBJECT),
            [CoverageCounters.MERGED_CONTEXT] = heap.Counters.GetValueOrDefault(HeapCounters.MERGED_CONTEXT),
            [CoverageCounters.WILDCARD_ACCESS] = accesses.Where(access => access.Resource.IsWildcard)
                                                         .Select(access => (access.BodyId, access.OperationId)).Distinct().Count()
        };
        var ordering = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [OrderingCounters.SPAWN_SITES] = input.Executions.SpawnSites.Count,
            [OrderingCounters.UNPROVEN_JOINS] = input.Executions.UnprovenJoins.Count,
            [OrderingCounters.TIMERS_DISABLED] = input.Executions.Counters.GetValueOrDefault(OrderingCounters.TIMERS_DISABLED),
            [OrderingCounters.TIMERS_ONE_SHOT] = input.Executions.Counters.GetValueOrDefault(OrderingCounters.TIMERS_ONE_SHOT),
            [OrderingCounters.TIMERS_PERIODIC] = input.Executions.Counters.GetValueOrDefault(OrderingCounters.TIMERS_PERIODIC)
        };
        var topCallees = opaque.GroupBy(item => item.Call.Callee, StringComparer.Ordinal)
                               .Select(group => (Callee: group.Key, Count: group.Count()))
                               .OrderByDescending(item => item.Count)
                               .ThenBy(item => item.Callee, StringComparer.Ordinal)
                               .Take(TOP_CALLEES)
                               .ToArray();
        return new InterproceduralCoverage(input.Scope.ScopeId, counters, topCallees, heap.LoweredNotReached, input.Scope.Reachable.Unreached)
        {
            Ordering = ordering,
            SpawnSites = input.Executions.SpawnSites,
            TimerSites = input.Executions.TimerSites,
            UnprovenJoins = input.Executions.UnprovenJoins
        };
    }

    private sealed record State(string Instance, string? Interval, BodySegment Segment);

    private sealed record PathNode(State State, PathNode? Parent, CallEdge? Edge);

    /// <summary><see cref="Capacity"/> is the count the lock object was constructed with, when it is a constant.</summary>
    private sealed record LockInfo(string Display, string? SingleObjectId, SourceSpan Acquisition, int? Capacity);

    /// <summary>A lock held at an access: what the lock object is, plus the mechanism, the mode and the acquisition site the state
    /// holds it from. The site tells one lock section from the next, which is what protection across a whole span needs (R2).</summary>
    private sealed record HeldProtectionInfo(string Display, string? SingleObjectId, SourceSpan Acquisition,
                                             IrSynchronizationPrimitive Primitive, IrLockMode Mode, string Site, bool IsExclusive)
    {
        /// <summary>The lock object as the must-hold analysis keys it, which is what asking whether the holding ever broke between
        /// two operations needs.</summary>
        public string Key { get; init; } = "";
    }

    /// <summary>What a body does to primitives it does not both enter and leave: <see cref="Opened"/> are the entries it hands to its
    /// caller, <see cref="Closed"/> the exits it makes of a scope its caller opened (ADR 0009).</summary>
    private sealed record LiftedLocks(IReadOnlyList<HeldLock> Opened, IReadOnlyList<HeldLock> Closed)
    {
        internal static LiftedLocks None { get; } = new([], []);
    }

    /// <summary>An edge the walk follows from an instance: a call edge, or a construction edge from the operation that triggered a
    /// construction to one of its instances; <see cref="Constructed"/> is the object or type the construction builds.</summary>
    private sealed record Step(int OperationId, CallEdge Edge, string? Constructed);

    /// <summary>The shortest, then smallest, path from a root's entry to an instance: the instances along it, root first.</summary>
    private sealed record Discovery(string RootId, IReadOnlyList<string> Instances);

    /// <summary>The edges of the walk (R5): a body's calls and construction triggers in operation order, the entry (operation
    /// <c>-1</c>) first, and a call's callee instances or a trigger's construction instances in ascending instance id.</summary>
    private sealed class WalkGraph(InterproceduralInput input)
    {
        private readonly HeapSolution _heap = input.Heap;
        private readonly Dictionary<string, List<CallEdge>> _edgesByCaller = input.Heap.Edges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                                                                                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly Dictionary<string, RegionTrigger[]> _triggers = input.Heap.RegionTriggers.GroupBy(trigger => trigger.InstanceId, StringComparer.Ordinal)
                                                                            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        private readonly Dictionary<string, RegionConstruction> _constructions = input.Heap.Constructions.ToDictionary(construction => construction.RegionId,
                                                                                                                       StringComparer.Ordinal);
        private readonly Dictionary<string, string> _constructed = input.Heap.Constructions
                                                                        .SelectMany(construction => construction.ConstructorInstances
                                                                                        .Select(instance => (Instance: instance, construction.RegionId)))
                                                                        .GroupBy(item => item.Instance, StringComparer.Ordinal)
                                                                        .ToDictionary(group => group.Key, group => group.First().RegionId, StringComparer.Ordinal);
        private readonly Dictionary<string, TypeInitializerConstruction[]> _initializers =
            input.Heap.TypeInitializers.Where(initializer => input.Heap.Instances.ContainsKey(initializer.InstanceId))
                 .SelectMany(initializer => initializer.TriggeringInstances.Select(trigger => (Trigger: trigger, Initializer: initializer)))
                 .GroupBy(item => item.Trigger, StringComparer.Ordinal)
                 .ToDictionary(group => group.Key, group => group.Select(item => item.Initializer).ToArray(), StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Step>> _steps = new(StringComparer.Ordinal);

        internal IReadOnlyList<Step> Of(MethodInstance instance)
        {
            if (_steps.TryGetValue(instance.Id, out var cached))
                return cached;

            var steps = new List<Step>();
            foreach (var edge in _edgesByCaller.GetValueOrDefault(instance.Id) ?? [])
            {
                steps.Add(new Step(edge.OperationId, edge,
                                   edge.Reason == WholeProgram.CONSTRUCTION_REASON ? _constructed.GetValueOrDefault(edge.CalleeInstance) : null));
            }

            foreach (var trigger in _triggers.GetValueOrDefault(instance.Id) ?? [])
            {
                foreach (var constructor in _constructions.GetValueOrDefault(trigger.RegionId)?.ConstructorInstances ?? [])
                {
                    steps.Add(new Step(trigger.OperationId, new CallEdge(instance.Id, trigger.OperationId, constructor, WholeProgram.CONSTRUCTION_REASON),
                                       trigger.RegionId));
                }
            }

            foreach (var initializer in _initializers.GetValueOrDefault(instance.Id) ?? [])
            {
                var operation = FirstUse(instance, initializer.TypeKey);
                steps.Add(new Step(operation, new CallEdge(instance.Id, operation, initializer.InstanceId, WholeProgram.CONSTRUCTION_REASON),
                                   $"static:{initializer.TypeKey}"));
            }

            var ordered = steps.GroupBy(step => (step.OperationId, step.Edge.CalleeInstance))
                               .Select(group => group.OrderBy(step => step.Constructed is null ? 1 : 0).First())
                               .OrderBy(step => step.OperationId)
                               .ThenBy(step => step.Edge.CalleeInstance, StringComparer.Ordinal)
                               .ToArray();
            _steps.Add(instance.Id, ordered);
            return ordered;
        }

        /// <summary>The first operation of an instance's body that uses a type's static member or constructor.</summary>
        private int FirstUse(MethodInstance instance, string typeKey)
        {
            var summary = instance.Summary;
            var uses = summary.Accesses.Where(access => access.Field.IsStatic && _heap.StaticRegionOf(instance.Id, access.Field) == $"static:{typeKey}")
                              .Select(access => access.OperationId)
                              .Concat(summary.Calls.Where(call => call.Kind is IrCallKind.Static or IrCallKind.Constructor &&
                                                                  ContainingType(instance, call.TargetContainingTypeKey, call.Target) == typeKey)
                                             .Select(call => call.OperationId))
                              .Concat(summary.Delegates.Where(created => ContainingType(instance, created.TargetContainingTypeKey, created.Delegate.Target) == typeKey)
                                             .Select(created => created.OperationId));
            return uses.DefaultIfEmpty(-1).Min();
        }

        private string? ContainingType(MethodInstance instance, string? containingTypeKey, string target) =>
            containingTypeKey is not null
                ? ProgramIndex.Substitute(containingTypeKey, instance.Substitution)
                : input.Scope.Program.Method(target)?.ContainingTypeKey;
    }

    /// <summary>For every entry instance of a rootless execution (a construction), each root whose walk reaches it, over every edge and
    /// whatever execution the instance runs in (startup aside), with that root's shortest path, then the smallest sequence of
    /// (operation, instance); the roots in id order.</summary>
    private static Dictionary<string, List<Discovery>> Discoveries(InterproceduralInput input, WalkGraph graph)
    {
        var targets = input.Executions.Executions.Where(execution => execution.Kind is ExecutionKind.LazyConstruction or ExecutionKind.TypeInitializer)
                           .SelectMany(execution => input.Executions.Entries.GetValueOrDefault(execution.Id) ?? [])
                           .Where(entry => entry.Kind != ExecutionEntryKind.Root)
                           .Select(entry => entry.InstanceId)
                           .ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, List<Discovery>>(StringComparer.Ordinal);
        foreach (var (rootId, entry) in input.Heap.RootInstances.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var reached = new Dictionary<string, Discovery>(StringComparer.Ordinal) { [entry] = new Discovery(rootId, [entry]) };
            var pending = new Queue<string>([entry]);
            while (pending.TryDequeue(out var instanceId))
            {
                if (!input.Heap.Instances.TryGetValue(instanceId, out var instance))
                    continue;
                var discovery = reached[instanceId];
                foreach (var step in graph.Of(instance))
                {
                    var callee = step.Edge.CalleeInstance;
                    if (reached.ContainsKey(callee) || IsStartupOnly(input, callee))
                        continue;
                    reached.Add(callee, new Discovery(rootId, [.. discovery.Instances, callee]));
                    pending.Enqueue(callee);
                }
            }

            foreach (var (instanceId, discovery) in reached)
            {
                if (!targets.Contains(instanceId))
                    continue;
                if (!found.TryGetValue(instanceId, out var list))
                    found.Add(instanceId, list = []);
                list.Add(discovery);
            }
        }

        return found;
    }

    private static bool IsStartupOnly(InterproceduralInput input, string instanceId) =>
        input.Executions.InstanceExecutions.TryGetValue(instanceId, out var executions) && executions.Count == 1 && executions.Contains(ExecutionModel.STARTUP);

    private sealed class ExecutionCollector(InterproceduralInput input, ExecutionInstance execution, IReadOnlyList<ExecutionEntry> entries,
                                            WalkGraph graph, IReadOnlyDictionary<string, List<Discovery>> discoveries)
    {
        private readonly HeapSolution _heap = input.Heap;
        private readonly List<(ExecutionEntry Entry, PathNode Node)> _visits = [];
        private readonly Dictionary<string, (ExecutionEntry Entry, PathNode Node)> _firstPaths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (ExecutionEntry Entry, PathNode Node)> _bodyPaths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<(IReadOnlyList<string> Symbols, AccessRoot Root)>> _callPaths = new(StringComparer.Ordinal);
        private readonly HashSet<CallEdge> _executionEdges = [];
        private readonly Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>>> _lockStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LockInfo> _locks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LiftedLocks> _lifted = new(StringComparer.Ordinal);
        private HashSet<string>? _writtenOutsideConstruction;
        private Dictionary<string, int>? _iterationParameters;
        private readonly HashSet<string> _liftingInProgress = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, IrProvenance>> _provenance = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<(string Instance, int Operation)>> _returnLoads = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Instance, int Ordinal), HashSet<(string Instance, int Operation)>> _parameterLoads = [];
        private readonly Dictionary<(string Instance, int Ordinal), HashSet<(string Instance, int Operation)>> _refParameterLoads = [];
        private readonly Dictionary<(string Owner, string Key), HashSet<(string Instance, int Operation)>> _cellLoads = [];

        internal IReadOnlyList<Access> Collect()
        {
            foreach (var entry in entries)
                Visit(entry);
            SolveLocks();
            SolveDependencies();
            return Emit();
        }

        /// <summary>Breadth-first over the walk's edges from one entry; a state is an instance with its construction interval, visited
        /// once per entry. A construction edge into a construction this execution runs opens the constructed object's interval, one into
        /// a construction of another execution is not followed, and a constructor call on an allocation opens that allocation's
        /// interval in place of the current one. The first visit of an instance, and of a body, keeps its discovery path.</summary>
        private void Visit(ExecutionEntry entry)
        {
            var start = new State(entry.InstanceId, entry.IntervalObject, entry.Segment);
            var visited = new HashSet<State> { start };
            var pending = new Queue<PathNode>([new PathNode(start, null, null)]);
            while (pending.TryDequeue(out var node))
            {
                if (!_heap.Instances.TryGetValue(node.State.Instance, out var instance))
                    continue;
                _visits.Add((entry, node));
                _firstPaths.TryAdd(node.State.Instance, (entry, node));
                _bodyPaths.TryAdd(instance.BodyId, (entry, node));

                foreach (var step in graph.Of(instance))
                {
                    var edge = step.Edge;
                    var interval = node.State.Interval;
                    if (input.Executions.Follow(instance, node.State.Segment, edge) is not { } segment)
                        continue;
                    if (step.Constructed is not null)
                    {
                        if (!input.Executions.InstanceExecutions.TryGetValue(edge.CalleeInstance, out var executions) ||
                            !executions.Contains(execution.Id))
                        {
                            continue;
                        }

                        interval = step.Constructed;
                    }
                    else if (instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } &&
                             _heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                    {
                        var created = callee.Receivers.Where(region => _heap.Regions[region].Kind == HeapRegionKind.Allocation)
                                            .Order(StringComparer.Ordinal).ToArray();
                        if (created.Length != 0 && (interval is null || !created.Contains(interval)))
                            interval = created[0];
                    }

                    _executionEdges.Add(edge);
                    var next = new State(edge.CalleeInstance, interval, segment);
                    if (visited.Add(next))
                        pending.Enqueue(new PathNode(next, node, edge));
                }
            }
        }

        /// <summary>The member symbols of a body's discovery path in this execution, consecutive duplicates (a lambda in its member)
        /// folded; a construction that is an execution of its own has one path for each root whose walk triggers it, starting at
        /// that root.</summary>
        private IReadOnlyList<(IReadOnlyList<string> Symbols, AccessRoot Root)> CallPaths(string bodyId)
        {
            if (_callPaths.TryGetValue(bodyId, out var cached))
                return cached;

            var (entry, node) = _bodyPaths[bodyId];
            var instances = new List<string>();
            for (var current = node; current is not null; current = current.Parent)
                instances.Add(current.State.Instance);
            instances.Reverse();

            var paths = new List<(IReadOnlyList<string> Symbols, AccessRoot Root)>();
            if (input.Executions.CallPathPrefixes.TryGetValue(execution.Id, out var prefix))
                paths.Add(([.. prefix, .. Symbols(instances)], Root()));
            else if (execution.RootId is null && entry.Kind != ExecutionEntryKind.Root && discoveries.TryGetValue(entry.InstanceId, out var found))
            {
                foreach (var discovery in found)
                {
                    if (RootOf(discovery.RootId) is { } root)
                        paths.Add((Symbols([.. discovery.Instances.Take(discovery.Instances.Count - 1), .. instances]), root));
                }
            }

            if (paths.Count == 0)
                paths.Add((Symbols(instances), Root()));
            _callPaths.Add(bodyId, paths);
            return paths;
        }

        private List<string> Symbols(IEnumerable<string> instances)
        {
            var symbols = new List<string>();
            foreach (var symbol in instances.Select(instance => Symbol(_heap.Instances[instance])))
            {
                if (symbols.Count == 0 || symbols[^1] != symbol)
                    symbols.Add(symbol);
            }

            return symbols;
        }

        private IrProvenance? Provenance(string bodyId, int operationId)
        {
            if (!_provenance.TryGetValue(bodyId, out var operations))
            {
                operations = input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                    ? body.Blocks.SelectMany(block => block.Operations).ToDictionary(operation => operation.Id, operation => operation.Provenance)
                    : [];
                _provenance.Add(bodyId, operations);
            }

            return operations.GetValueOrDefault(operationId);
        }

        private IEnumerable<string> VisitedInstances() => _firstPaths.Keys;

        /// <summary>The locks each instance must hold on entry: none for an execution's entries, and otherwise the join of the
        /// states at every incoming call site, iterated from the top until nothing changes.</summary>
        private void SolveLocks()
        {
            var entryInstances = entries.Select(entry => entry.InstanceId).ToHashSet(StringComparer.Ordinal);
            var entryLocks = new Dictionary<string, Dictionary<string, HeldLock>>(StringComparer.Ordinal);
            foreach (var instance in entryInstances)
                entryLocks[instance] = new Dictionary<string, HeldLock>(StringComparer.Ordinal);

            var changed = true;
            var rounds = 0;
            var bounded = false;
            while (changed)
            {
                changed = false;
                foreach (var instanceId in VisitedInstances())
                {
                    if (entryLocks.TryGetValue(instanceId, out var entry) && input.Scope.Reachable.Bodies.TryGetValue(_heap.Instances[instanceId].BodyId, out var body))
                        _lockStates[instanceId] = MustHeldLocks.Compute(body, operation => Effect(_heap.Instances[instanceId], operation), entry).Operations;
                }

                if (bounded)
                    break;

                var incoming = new Dictionary<string, Dictionary<string, HeldLock>>(StringComparer.Ordinal);
                foreach (var edge in _executionEdges)
                {
                    if (entryInstances.Contains(edge.CalleeInstance) || !_lockStates.TryGetValue(edge.CallerInstance, out var states) ||
                        !states.TryGetValue(edge.OperationId, out var atCall))
                    {
                        continue;
                    }

                    // Entering at the top of a body is depth one whatever the caller's nesting; everything else about the holding
                    // is weakened to what every incoming call site has, exactly as the paths inside one body are joined.
                    var atEntry = atCall.ToDictionary(pair => pair.Key, pair => pair.Value with { Depth = 1 }, StringComparer.Ordinal);
                    incoming[edge.CalleeInstance] = MustHeldLocks.Join(incoming.GetValueOrDefault(edge.CalleeInstance), atEntry)!;
                }

                foreach (var (instanceId, locks) in incoming)
                {
                    if (!entryLocks.TryGetValue(instanceId, out var current) || !Same(current, locks))
                    {
                        entryLocks[instanceId] = locks;
                        changed = true;
                    }
                }

                // Each round weakens what an entry holds, so the states descend and this is a bound and not a policy; reaching it
                // would mean a state that does not settle, and a body whose entry nothing settles holds nothing.
                if (++rounds <= (VisitedInstances().Count() + 1) * ROUNDS_PER_INSTANCE)
                    continue;

                foreach (var instanceId in entryLocks.Keys.ToArray().Where(instanceId => !entryInstances.Contains(instanceId)))
                    entryLocks[instanceId] = new Dictionary<string, HeldLock>(StringComparer.Ordinal);
                (bounded, changed) = (true, true);
            }
        }

        /// <summary>A lock's identity is the set of regions its value points to; every lock is released by value, so a release of a
        /// lock not held as such clears every held lock it may be. A call carries what its callee leaves open or closes on an object
        /// somebody else opened, which is what makes a wrapper transparent (ADR 0009).</summary>
        private LockEffect Effect(MethodInstance instance, IrOperation operation)
        {
            if (operation is IrCallOperation call)
                return Lifted(instance, call);

            if (operation is not (IrAcquireOperation or IrReleaseOperation) ||
                instance.Summary.Locks.FirstOrDefault(candidate => candidate.OperationId == operation.Id) is not { } transfer)
            {
                return LockEffect.None;
            }

            var regions = transfer.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct().Order(StringComparer.Ordinal).ToArray();
            // Two mechanisms on one object are independent, so the kind is part of what a release matches (TD-083).
            var key = $"{transfer.Primitive}:" +
                      (regions.Length != 0 ? "locks:" + string.Join(",", regions) : $"unresolved:{instance.Id}:{transfer.Origin}");
            if (transfer.IsAcquire && !_locks.ContainsKey(key))
            {
                var single = regions.Length == 1 && input.Executions.IsSingleObject(regions[0]) ? $"{input.Scope.ScopeId}|{regions[0]}" : null;
                var display = regions.Length == 0
                    ? $"lock on an unresolved object at {transfer.Provenance.Span.Path}:{transfer.Provenance.Span.StartLine} (identity unknown, not one object per process)"
                    : string.Join(", ", regions.Select(region => _heap.Regions[region].Display)) + (single is null ? " (not one object per process)" : "");
                _locks[key] = new LockInfo(display, single, transfer.Provenance.Span, Capacity(regions));
            }

            return new LockEffect(transfer.IsAcquire ? LockEffectKind.Acquire : LockEffectKind.Release,
                                  new LockObject(key, key, null, IdentityUnknown: true)
                                  {
                                      Primitive = transfer.Primitive,
                                      Mode = transfer.Mode,
                                      Site = $"{instance.Id}:{operation.Id}",
                                      NamesObject = regions.Length != 0
                                  })
            {
                Permits = transfer.Permits
            };
        }

        /// <summary>What a call does to a primitive its callee does not enter and leave itself: entering one it leaves open is an
        /// entry here, and leaving one it never entered — the disposal of a scope its caller opened — is a release here. Only one
        /// such primitive is carried, and only when every callee the call may reach agrees on it (ADR 0009).</summary>
        private LockEffect Lifted(MethodInstance instance, IrCallOperation call)
        {
            var callees = _executionEdges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.Id)
                                         .Select(edge => edge.CalleeInstance)
                                         .Distinct(StringComparer.Ordinal)
                                         .ToArray();
            if (callees.Length == 0)
                return LockEffect.None;

            var lifted = callees.Select(LiftedOf).ToArray();
            if (Single(lifted.Select(item => item.Opened)) is { } opened && CanExclude(opened))
                return new LockEffect(LockEffectKind.Acquire, opened.Lock with { Site = $"{instance.Id}:{call.Id}", IsLifted = true });
            if (Single(lifted.Select(item => item.Closed)) is { } closed && CanExclude(closed))
                return new LockEffect(LockEffectKind.Release, closed.Lock) { Permits = closed.IsPaired ? 1 : null };

            return LockEffect.None;

            // The one lock the call carries. A must-effect is what every callee does, so a callee that carries nothing out of
            // itself takes the proof away: a call that may run an implementation which acquires nothing holds nothing after it,
            // whatever its siblings acquire. Among the callees that do carry something, every one that names an object at all has
            // to name the same one; a context that named none carries the effect without saying which object it is on, and it is
            // no evidence either way about the object its siblings named.
            static HeldLock? Single(IEnumerable<IReadOnlyList<HeldLock>> perCallee)
            {
                var carried = perCallee.ToArray();
                if (carried.Length == 0 || carried.Any(locks => locks.Count == 0))
                    return null;

                var named = carried.SelectMany(locks => locks).Where(held => held.Lock.NamesObject).ToArray();
                return named.Length != 0 && named.Select(held => held.Lock.Key).Distinct(StringComparer.Ordinal).Count() == 1
                    ? named[0]
                    : null;
            }
        }

        /// <summary>Whether an entry can exclude anyone at all, which is what a call carries out of its callee: a
        /// <c>SemaphoreSlim</c> whose capacity nothing proves to be one gives the caller nothing, exactly as a type the analysis
        /// does not model gives it nothing (ADR 0009).</summary>
        private bool CanExclude(HeldLock held) =>
            held.Lock.Primitive != IrSynchronizationPrimitive.SemaphoreSlim ||
            (_locks.TryGetValue(held.Lock.Key, out var info) && info.Capacity == 1);

        /// <summary>What an instance leaves open and what it closes without having opened it, read from its own body alone.</summary>
        private LiftedLocks LiftedOf(string instanceId)
        {
            if (_lifted.TryGetValue(instanceId, out var known))
                return known;
            if (!_liftingInProgress.Add(instanceId))
                return LiftedLocks.None;

            var lifted = LiftedLocks.None;
            if (input.Scope.Reachable.Bodies.TryGetValue(_heap.Instances[instanceId].BodyId, out var body))
            {
                var state = MustHeldLocks.Compute(body, operation => Effect(_heap.Instances[instanceId], operation));
                var candidates = body.Blocks.SelectMany(block => block.Operations)
                                     .Select(operation => (operation, Effect: Effect(_heap.Instances[instanceId], operation)))
                                     .Where(item => item.Effect is { Kind: LockEffectKind.Release, Lock: { } } &&
                                                    state.Operations.TryGetValue(item.operation.Id, out var held) &&
                                                    !held.ContainsKey(item.Effect.Lock!.Key))
                                     // An exit that gives back more permits than an entry takes is carried as such, so that the
                                     // caller whose scope it closes counts what came back and not merely that something did (R3).
                                     .Select(item => new HeldLock(1, item.Effect.Lock!, item.operation.Id) { IsPaired = item.Effect.Permits == 1 })
                                     .DistinctBy(held => held.Lock.Key)
                                     .ToArray();
                // A release somewhere in the body is a release on some path. What the caller's scope needs is a release on every
                // path, so each candidate is re-solved from a state that already holds it: it is carried back only where the body
                // ends holding it no longer, which is what a `Dispose` with a conditional release fails (R3, R4, ADR 0009).
                var closed = candidates.Where(held => MustHeldLocks.ReleasesOnEveryPath(
                                                          body, operation => Effect(_heap.Instances[instanceId], operation), held.Lock.Key))
                                       .ToArray();
                lifted = new LiftedLocks(state.AtExit.Values.ToArray(), closed);
            }

            _liftingInProgress.Remove(instanceId);
            _lifted[instanceId] = lifted;
            return lifted;
        }

        /// <summary>The loads each return value, parameter, capture cell and store depends on, as sets that only grow over the
        /// execution's instances and edges, iterated until none changes.</summary>
        private void SolveDependencies()
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var instanceId in VisitedInstances())
                {
                    var instance = _heap.Instances[instanceId];
                    var summary = instance.Summary;
                    foreach (var @return in summary.Returns)
                        changed |= Grow(Get(_returnLoads, instanceId), Loads(instance, @return.Dependencies));
                    foreach (var parameter in summary.RefParameters)
                        changed |= Grow(Get(_refParameterLoads, (instanceId, parameter.Ordinal)), Loads(instance, parameter.Dependencies));
                    foreach (var store in summary.CapturedStores)
                    {
                        foreach (var owner in instance.CellOwners)
                            changed |= Grow(Get(_cellLoads, (owner, store.SymbolKey)), Loads(instance, store.Dependencies));
                    }

                    foreach (var variable in summary.Variables)
                    {
                        foreach (var owner in instance.CellOwners)
                            changed |= Grow(Get(_cellLoads, (owner, variable.SymbolKey)), Loads(instance, variable.Dependencies));
                    }

                    foreach (var call in summary.Calls)
                    {
                        foreach (var edge in _executionEdges.Where(edge => edge.CallerInstance == instanceId && edge.OperationId == call.OperationId))
                        {
                            foreach (var argument in call.Arguments)
                                changed |= Grow(Get(_parameterLoads, (edge.CalleeInstance, argument.ParameterOrdinal)), Loads(instance, argument.Dependencies));
                        }
                    }
                }
            }
        }

        private HashSet<(string Instance, int Operation)> Loads(MethodInstance instance, IEnumerable<ValueDependency> dependencies)
        {
            var loads = new HashSet<(string, int)>();
            foreach (var dependency in dependencies)
            {
                switch (dependency)
                {
                    case LoadDependency load:
                        loads.Add((instance.Id, load.OperationId));
                        break;
                    case ParameterDependency parameter:
                        loads.UnionWith(_parameterLoads.GetValueOrDefault((instance.Id, parameter.Ordinal)) ?? []);
                        break;
                    case CapturedDependency captured:
                        foreach (var owner in instance.CellOwners)
                            loads.UnionWith(_cellLoads.GetValueOrDefault((owner, captured.SymbolKey)) ?? []);
                        break;
                    case CallDependency call:
                        foreach (var edge in _executionEdges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId))
                            loads.UnionWith(_returnLoads.GetValueOrDefault(edge.CalleeInstance) ?? []);
                        break;
                    case RefResultDependency result:
                        foreach (var edge in _executionEdges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == result.OperationId))
                            loads.UnionWith(_refParameterLoads.GetValueOrDefault((edge.CalleeInstance, result.Ordinal)) ?? []);
                        break;
                }
            }

            return loads;
        }

        private IReadOnlyList<Access> Emit()
        {
            var feeding = new Dictionary<(string Instance, int Operation, string Resource), List<(string Instance, int Operation)>>();
            // Only the load of the instance that feeds the write is folded into it; another instance of that body still reads.
            var dropped = new HashSet<(string Instance, int Operation, string Resource)>();
            // The stores whose compare-and-swap checks the cell against what the value it writes was read from. That, and not
            // the name of the member called, is what makes the sequence around it as atomic as the call itself (R1).
            var verified = new HashSet<(string Instance, int Operation, string Resource)>();
            foreach (var instanceId in VisitedInstances())
            {
                var instance = _heap.Instances[instanceId];
                foreach (var store in instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store))
                {
                    var loads = Loads(instance, store.Dependencies);
                    // The comparand has to be the value those reads produced, not a value computed from them: a swap that checks
                    // `old + 2` checks a number nobody ever observed in the cell (R1).
                    var observed = store.ComparandLoad is { } checkedLoad &&
                                   loads.Count != 0 &&
                                   loads.All(load => load.Operation == checkedLoad &&
                                                     string.Equals(load.Instance, instanceId, StringComparison.Ordinal));
                    foreach (var resource in Resources(instance, store))
                    {
                        if (observed)
                            verified.Add((instanceId, store.OperationId, resource.Identity));

                        foreach (var (loadInstanceId, loadOperation) in loads.OrderBy(load => load.Instance, StringComparer.Ordinal).ThenBy(load => load.Operation))
                        {
                            var loadInstance = _heap.Instances[loadInstanceId];
                            // A compound operation reaches over the whole collection: its check may read the structure while its
                            // change writes a cell, so the two meet on the collection rather than on one resource (ADR 0010).
                            var reads = loadInstance.Summary.Accesses
                                                    .Where(access => access.OperationId == loadOperation && access.Kind == SummaryAccessKind.Load)
                                                    .SelectMany(load => Resources(loadInstance, load))
                                                    .Where(candidate => candidate.Identity == resource.Identity ||
                                                                        store.IsCompound && candidate.StructuralIdentity == resource.StructuralIdentity)
                                                    .ToArray();
                            if (reads.Length == 0)
                                continue;

                            var key = (instanceId, store.OperationId, resource.Identity);
                            if (!feeding.TryGetValue(key, out var list))
                                feeding.Add(key, list = []);
                            if (!list.Contains((loadInstanceId, loadOperation)))
                                list.Add((loadInstanceId, loadOperation));
                            // Only a read of the very resource the change is on is folded into it; a check of another resource
                            // stays an access of its own.
                            if (reads.Any(candidate => candidate.Identity == resource.Identity))
                                dropped.Add((loadInstance.Id, loadOperation, resource.Identity));
                        }
                    }
                }
            }

            var accesses = new List<Access>();
            var seen = new HashSet<(string, int, string, AccessOperation, bool, string, string)>();
            foreach (var (entry, node) in _visits)
            {
                var instance = _heap.Instances[node.State.Instance];
                foreach (var access in instance.Summary.Accesses.Where(access => input.Executions.Runs(instance.BodyId, node.State.Segment, access.OperationId)))
                {
                    foreach (var resource in Resources(instance, access))
                    {
                        if (access.Kind == SummaryAccessKind.Load && dropped.Contains((instance.Id, access.OperationId, resource.Identity)))
                            continue;

                        var sources = feeding.GetValueOrDefault((instance.Id, access.OperationId, resource.Identity));
                        var operation = Operation(access, sources is { Count: > 0 },
                                                  verified.Contains((instance.Id, access.OperationId, resource.Identity)));
                        var conditions = Conditions(instance, access, node);
                        var regionId = resource.RegionId!;
                        var local = node.State.Interval == regionId && !input.Executions.PublishedObjects.Contains(regionId);
                        // Contexts of one body that hold the same protection give one access; a context holding less stays, so the pair
                        // an occurrence keeps can be the least protected one (R5).
                        var held = HeldOverSpan(instance.Id, access.OperationId, operation, sources);
                        // Which object is held is not the whole protection: one context may hold it for reading and another for
                        // writing, or hold it over a suspension that keeps nobody out, and those are not one access (R5).
                        var heldKey = string.Join("|", held.Select(info => $"{info.Display}/{info.Primitive}/{info.Mode}/{info.IsExclusive}")
                                                           .Order(StringComparer.Ordinal));
                        var region = _heap.Regions[regionId];
                        var ownership = input.Executions.Ownership.GetValueOrDefault(regionId);
                        // A construction triggered by several roots gives one access per root, so each root pair is an occurrence (R5).
                        foreach (var (callPath, pathRoot) in CallPaths(instance.BodyId))
                        {
                            if (!seen.Add((instance.BodyId, access.OperationId, resource.Identity, operation, local, heldKey, pathRoot.RootId)))
                                continue;

                            accesses.Add(new Access(
                                resource,
                                operation,
                                Root(),
                                Symbol(instance),
                                access.Provenance.Span,
                                held.Select(info => info.Display).Order(StringComparer.Ordinal).ToArray(),
                                held.Select(info => info.SingleObjectId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                                BindingEvidenceOf(region),
                                CodeFlow(entry, node, held, $"{operation.ToWireName()} {access.Field.ContainingType}.{access.Field.Name}", access.Provenance.Span),
                                Uncertainties(instance, region, access, resource, conditions))
                            {
                                // One object can be held by more than one mechanism at once, and the two are independent, so the
                                // holdings of an object are a list and never one entry that the last mechanism wins (TD-083).
                                HeldProtections = held.Where(info => info.SingleObjectId is not null)
                                                      .GroupBy(info => info.SingleObjectId!, StringComparer.Ordinal)
                                                      .ToDictionary(group => group.Key,
                                                                    group => (IReadOnlyList<HeldProtection>)group
                                                                             .Select(info => new HeldProtection(info.Primitive, info.Mode, info.IsExclusive))
                                                                             .Distinct()
                                                                             .ToArray(),
                                                                    StringComparer.Ordinal),
                                ExecutionId = execution.Id,
                                Ownership = ownership?.Kind ?? OwnershipKind.Unknown,
                                OwnershipEvidence = ownership?.Evidence ?? [],
                                IsConstructionLocal = local,
                                ReadSources = (sources ?? []).Select(ReadSourceOf).ToArray(),
                                BodyId = instance.BodyId,
                                OperationId = access.OperationId,
                                InstanceId = instance.Id,
                                CallPath = callPath,
                                SpawnSites = input.Executions.SpawnSitesOf(execution.Id, callPath),
                                PathRoot = pathRoot,
                                Conditions = conditions,
                                IsIterationIndexed = access.SelectorParameter is { } ordinal &&
                                                     IterationParameters.GetValueOrDefault(instance.BodyId, -1) == ordinal &&
                                                     (access.SelectorWidth ?? ITERATION_WIDTH) >= ITERATION_WIDTH,
                                // Values of independent executions are independent unknowns, whatever they are called in the
                                // body they come from (TD-092).
                                SelectorTerm = access.SelectorTerm is { } term ? Bind(term, instance.Id) : null
                            });
                        }
                    }
                }
            }

            return accesses;
        }

        /// <summary>What an access does to its cell. An atomic mark decides it (TD-082), with two exceptions: a read-modify-write is
        /// only atomic when one operation performs the whole of it, so a store fed by loads stays an ordinary read-modify-write
        /// however atomic the operation that performs it is, and a change decided by an earlier read of the same collection is a
        /// compound operation however atomic each of its steps is (ADR 0010). What a single call does atomically and what a
        /// sequence of them does are two questions: <c>Interlocked.Exchange</c> given a value an earlier read produced writes
        /// atomically and still loses every update made between that read and it. A compare-and-swap is the exception that shows
        /// the rule — it is handed the value that read saw and writes only where the cell still holds it, so the sequence is as
        /// atomic as the call — but only where it verifies that read, which is a fact about the comparand it was handed and never
        /// about the member's name. A compare-and-swap given another read of the cell as its comparand checks a value nobody
        /// built anything from, and the sequence loses updates like any other (R1).</summary>
        private static AccessOperation Operation(SummaryAccess access, bool isReadModifyWrite, bool verifiesItsRead) =>
            (access.Atomic, access.Kind) switch
        {
            _ when access.IsCompound => AccessOperation.CompoundOperation,
            (IrAtomicEffect.Read, _) => AccessOperation.AtomicRead,
            (IrAtomicEffect.CompareAndSwap, _) when !isReadModifyWrite || verifiesItsRead => AccessOperation.AtomicReadModifyWrite,
            (IrAtomicEffect.ReadModifyWrite, _) when !isReadModifyWrite => AccessOperation.AtomicReadModifyWrite,
            (IrAtomicEffect.Write, _) when !isReadModifyWrite => AccessOperation.AtomicWrite,
            (_, SummaryAccessKind.Load) => AccessOperation.Read,
            _ => isReadModifyWrite ? AccessOperation.ReadModifyWrite : AccessOperation.Write
        };

        private ReadSource ReadSourceOf((string Instance, int Operation) load)
        {
            var instance = _heap.Instances[load.Instance];
            var access = instance.Summary.Accesses.First(candidate => candidate.OperationId == load.Operation);
            var (entry, node) = _firstPaths[load.Instance];
            var held = HeldLocks(instance.Id, access.OperationId);
            return new ReadSource(Symbol(instance), access.Provenance.Span,
                                  CodeFlow(entry, node, held, $"read {access.Field.ContainingType}.{access.Field.Name}", access.Provenance.Span));
        }

        /// <summary>What protects an operation that spans a read and the write depending on it: only a lock section that covers the
        /// whole span, so a lock around the write alone protects nothing, and two sections, one around the read and one around the
        /// write, protect nothing either (R2). Every other operation is protected by what is held where it stands.</summary>
        private IReadOnlyList<HeldProtectionInfo> HeldOverSpan(string instanceId, int operationId, AccessOperation operation,
                                                               IReadOnlyList<(string Instance, int Operation)>? sources)
        {
            var held = HeldLocks(instanceId, operationId);
            if (operation is not (AccessOperation.ReadModifyWrite or AccessOperation.CompoundOperation) || sources is not { Count: > 0 })
                return held;

            return held.Where(protection => sources.All(source => Spans(protection, instanceId, operationId, source))).ToArray();
        }

        /// <summary>Whether one holding covers the span from a read to the write that depends on it: the same section at both ends,
        /// and, where both ends stand in one body, a holding that was never let go in between. A section is named by the acquisition
        /// it comes from, and one acquisition inside a loop is a new section on every iteration, so the name alone would call a read
        /// of one iteration and a write of the next one protected by one section (R2).</summary>
        private bool Spans(HeldProtectionInfo protection, string instanceId, int operationId, (string Instance, int Operation) source)
        {
            if (!HeldLocks(source.Instance, source.Operation).Any(other => other.Site == protection.Site))
                return false;

            var instance = _heap.Instances[instanceId];
            return !string.Equals(source.Instance, instanceId, StringComparison.Ordinal) ||
                   !input.Scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ||
                   MustHeldLocks.HoldsBetween(body, operation => Effect(instance, operation), protection.Key,
                                              source.Operation, operationId);
        }

        private IReadOnlyList<HeldProtectionInfo> HeldLocks(string instanceId, int operationId) =>
            _lockStates.TryGetValue(instanceId, out var states) && states.TryGetValue(operationId, out var held)
                // A scope a call opened is protection only where the exit is proven too: nothing else says it ever closes.
                ? held.Values.Where(lockHeld => !lockHeld.Lock.IsLifted || lockHeld.IsReleasedOnAllPaths)
                      .Select(lockHeld => _locks.TryGetValue(lockHeld.Lock.Key, out var info)
                                         ? new HeldProtectionInfo(info.Display, info.SingleObjectId, info.Acquisition,
                                                                  lockHeld.Lock.Primitive, lockHeld.Lock.Mode, lockHeld.Lock.Site,
                                                                  Excludes(lockHeld, info)) { Key = lockHeld.Lock.Key }
                                         : null)
                      .OfType<HeldProtectionInfo>()
                      .ToArray()
                : [];

        /// <summary>Whether one holding excludes anyone at all. A primitive owned by the thread that took it holds nothing over a
        /// suspension point, because the continuation may resume on another thread; a <c>SemaphoreSlim</c> is a mutex only at a
        /// capacity proven to be one, released on every path, and given back one permit for one (TD-083).</summary>
        private static bool Excludes(HeldLock held, LockInfo info) => held.Lock.Primitive switch
        {
            IrSynchronizationPrimitive.SemaphoreSlim => info.Capacity == 1 && held.IsReleasedOnAllPaths && held.IsPaired,
            _ => !held.CrossesSuspension
        };

        /// <summary>The predicates that hold where an access runs, with each subject named as far as it is proven (TD-090). A
        /// field read gets one canonical identity across executions only when everything is proven at once: its region is one
        /// object per process, the field cannot change after construction, nothing writes it outside the construction of that
        /// region, and the read happens once that construction is over. Anything less is this execution's own value, which no
        /// other execution shares, so it can never take a pair away.</summary>
        private IReadOnlyList<PathPredicate> Conditions(MethodInstance instance, SummaryAccess access, PathNode node)
        {
            var conditions = new List<PathPredicate>(Predicates(instance, access.Conditions, node));
            // What reaches the access is part of the condition it runs under (R8). A call site's guards are added only while the
            // path to the access is the one way there: where several calls reach an instance, the access runs under one of their
            // conditions and under no one of them for certain, and the weaker fact is that nothing is known.
            for (var step = node; step is { Edge: { } edge, Parent: { } caller }; step = caller)
            {
                if (_executionEdges.Count(candidate => string.Equals(candidate.CalleeInstance, step.State.Instance, StringComparison.Ordinal)) != 1)
                    break;
                if (_heap.Instances.TryGetValue(edge.CallerInstance, out var callerInstance) &&
                    callerInstance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { } transfer)
                {
                    conditions.AddRange(Predicates(callerInstance, transfer.Conditions, caller));
                }
            }

            return conditions;
        }

        /// <summary>The predicates of one body, with each subject named as the execution names it.</summary>
        private IReadOnlyList<PathPredicate> Predicates(MethodInstance instance, IReadOnlyList<SummaryPredicate> predicates, PathNode node)
        {
            var conditions = new List<PathPredicate>();
            foreach (var condition in predicates)
            {
                var subject = condition switch
                {
                    { Relation: PathRelation.Unsupported } => "",
                    { SubjectLoad: { } load } => Canonical(instance, load, node) ?? Local(instance, $"{instance.BodyId}#{load}"),
                    // The same name a cell's expression gets, so that a bound on an index constrains the cell it names.
                    { SubjectValue: { } value } => Local(instance, $"{instance.BodyId}:{value}"),
                    _ => ""
                };
                conditions.Add(new PathPredicate(subject, condition.Relation, condition.Value, condition.Text)
                {
                    Width = condition.Width,
                    Signed = condition.Signed
                });
            }

            return conditions;
        }

        /// <summary>
        /// The term with every value in it named by the execution it belongs to, so that one body read by two executions states
        /// two unknowns and never one (TD-092). The name is the one a predicate over that same value gets, which is what ties a
        /// guard to the cell it decides: without it the solver is handed <c>i &lt; 5</c>, <c>j &gt;= 5</c> and <c>i == j</c> over
        /// three unrelated unknowns and can prove nothing, although the three together are unsatisfiable.
        /// </summary>
        private static ValueTerm Bind(ValueTerm term, string instanceId) => term switch
        {
            VariableTerm variable => variable with { Identity = Local(instanceId, variable.Identity) },
            SumTerm sum => new SumTerm(Bind(sum.Left, instanceId), Bind(sum.Right, instanceId)),
            ConvertTerm convert => convert with { Operand = Bind(convert.Operand, instanceId) },
            _ => term
        };

        /// <summary>What one execution's own value is called, wherever the query names it.</summary>
        private static string Local(MethodInstance instance, string value) => Local(instance.Id, value);

        private static string Local(string instanceId, string value) => $"local|{instanceId}|{value}";

        /// <summary>The identity of the value a field load reads, when every execution that reads it reads the same one.</summary>
        private string? Canonical(MethodInstance instance, int loadOperationId, PathNode node)
        {
            if (instance.Summary.Accesses.FirstOrDefault(candidate => candidate.OperationId == loadOperationId &&
                                                                      candidate.Kind == SummaryAccessKind.Load) is not { } load ||
                !load.Field.IsReadOnly || WrittenOutsideConstruction.Contains(FieldSlot.Key(load.Field)))
            {
                return null;
            }

            var resources = Resources(instance, load);
            if (resources.Count != 1 || resources[0].RegionId is not { } regionId)
                return null;
            // A read while the object is still being built sees a field the construction has not finished writing.
            if (node.State.Interval == regionId)
                return null;

            return _heap.Regions[regionId].Kind == HeapRegionKind.Static || input.Executions.IsSingleObject(regionId)
                ? $"{PathPredicate.CANONICAL}{resources[0].Identity}"
                : null;
        }

        /// <summary>The field slots something writes outside a constructor, which are the ones a reader cannot take for
        /// settled however the field is declared.</summary>
        private HashSet<string> WrittenOutsideConstruction =>
            _writtenOutsideConstruction ??= _heap.Instances.Values
                .SelectMany(instance => instance.Summary.Accesses
                                                .Where(access => access.Kind == SummaryAccessKind.Store && !IsConstructor(instance.BodyId))
                                                .Select(access => FieldSlot.Key(access.Field)))
                .ToHashSet(StringComparer.Ordinal);

        private static bool IsConstructor(string bodyId) =>
            bodyId.Contains(".#ctor", StringComparison.Ordinal) || bodyId.Contains(".#cctor", StringComparison.Ordinal);

        /// <summary>How many bits an iteration number of a parallel loop needs to stay one number: <c>Parallel.For</c> counts
        /// with an <c>int</c>, and the indexed <c>ForEach</c> numbers the elements of a source whose count is one, so an index
        /// that keeps 32 bits keeps every iteration apart and a narrower one folds them onto each other (TD-068).</summary>
        private const int ITERATION_WIDTH = 32;

        /// <summary>How many rounds each visited instance is allowed before the entry states are taken as unsettled: one round can
        /// weaken a holding by its mode, its suspension, its proof of release and its site, and one more carries the weakening on
        /// to the next instance.</summary>
        private const int ROUNDS_PER_INSTANCE = 8;

        /// <summary>Whether two entry states hold the same locks with the same properties: a holding that changed only by its mode
        /// or by losing its proof of release still has to travel to everything the instance calls.</summary>
        private static bool Same(Dictionary<string, HeldLock> left, Dictionary<string, HeldLock> right) =>
            left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var other) && other == pair.Value);

        /// <summary>The bodies a parallel loop runs, with the parameter it binds the iteration number to: the counter of
        /// <c>Parallel.For</c> and the index of the indexed <c>Parallel.ForEach</c> overload. The element the plain overload
        /// passes is not one, because the source it comes from may hold the same element twice (TD-068).</summary>
        private Dictionary<string, int> IterationParameters =>
            _iterationParameters ??= _heap.Instances.Values
                .SelectMany(instance => instance.Summary.Spawns)
                .Where(spawn => spawn.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach)
                .SelectMany(spawn => spawn.Work.SelectMany(work => work.Values).OfType<DelegateCreationValue>()
                                          .Select(work => (work.Target, Ordinal: IterationParameter(spawn.Kind, work.Target))))
                .Where(work => work.Ordinal is not null)
                .GroupBy(work => work.Target, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Ordinal!.Value, StringComparer.Ordinal);

        private int? IterationParameter(IrSpawnKind kind, string bodyId)
        {
            if (!input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                return null;
            var ordinal = kind == IrSpawnKind.ParallelFor ? 0 : 2;
            return body.Parameters.FirstOrDefault(parameter => parameter.Ordinal == ordinal) is { } parameter &&
                   parameter.Type is "int" or "long" or "System.Int32" or "System.Int64"
                ? ordinal
                : null;
        }

        /// <summary>How the collection a field holds compares the keys of its cells: what each object the field points to was
        /// constructed with, taken at its weakest. A keyed collection this scope never constructs may compare keys any way at all,
        /// so nothing about its keys is proven (ADR 0010).</summary>
        private IrKeyEquality Equality(string regionId, IrFieldRef field)
        {
            if (!field.Type.Contains("Dictionary<", StringComparison.Ordinal))
                return IrKeyEquality.Value;

            var equalities = _heap.PointsTo(regionId, FieldSlot.Key(field))
                                  .Select(target => _heap.Regions.TryGetValue(target, out var region) ? Allocation(region)?.KeyEquality : null)
                                  .ToArray();
            return equalities.Length != 0 && equalities.All(equality => equality is not null)
                ? equalities.Max(equality => equality!.Value)
                : IrKeyEquality.Unknown;
        }

        /// <summary>The <c>new</c> a region was created by, when the body that holds it is at hand.</summary>
        private IrAllocateOperation? Allocation(HeapRegion region) =>
            region is { SiteBodyId: { } bodyId, SiteOperationId: { } operationId } && input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                ? body.Blocks.SelectMany(block => block.Operations).OfType<IrAllocateOperation>().FirstOrDefault(allocate => allocate.Id == operationId)
                : null;

        /// <summary>The count the one object a lock resolves to was constructed with, read from its allocation site.</summary>
        private int? Capacity(IReadOnlyList<string> regions) =>
            regions.Count == 1 && _heap.Regions.TryGetValue(regions[0], out var region) &&
            region is { SiteBodyId: { } bodyId, SiteOperationId: { } operationId } &&
            input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                ? body.Blocks.SelectMany(block => block.Operations).OfType<IrAllocateOperation>()
                      .FirstOrDefault(allocate => allocate.Id == operationId)?.SynchronizationCapacity
                : null;

        private IReadOnlyList<CodeFlowStep> CodeFlow(ExecutionEntry entry, PathNode node, IReadOnlyList<HeldProtectionInfo> held, string accessText,
                                                     SourceSpan accessSource)
        {
            var steps = new List<CodeFlowStep>();
            var entryInstance = _heap.Instances[entry.InstanceId];
            steps.Add(execution.RootId is not null
                ? new CodeFlowStep("root", $"{input.Scope.Roots.First(candidate => candidate.StableRootId == execution.RootId).Entry.Display} starts",
                                   input.Scope.Roots.First(candidate => candidate.StableRootId == execution.RootId).Entry.Source)
                : new CodeFlowStep("root", $"{execution.Display} starts", BodySource(entryInstance.BodyId) ?? accessSource));
            if (entry.Kind is ExecutionEntryKind.Construction or ExecutionEntryKind.TypeInitializer)
            {
                var subject = entry.IntervalObject is { } interval && _heap.Regions.TryGetValue(interval, out var region) ? region.Display : entryInstance.BodyId;
                steps.Add(new CodeFlowStep("construction", $"constructs {subject}", BodySource(entryInstance.BodyId) ?? accessSource));
            }

            var path = new List<PathNode>();
            for (var current = node; current.Edge is not null; current = current.Parent!)
                path.Add(current);
            path.Reverse();
            foreach (var step in path)
            {
                var caller = _heap.Instances[step.Edge!.CallerInstance];
                var callee = _heap.Instances[step.Edge.CalleeInstance];
                var calleeSymbol = input.Scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var calleeBody) ? calleeBody.MethodSymbol : callee.BodyId;
                if (step.Edge.Reason == WholeProgram.CONSTRUCTION_REASON && step.State.Interval is { } constructed)
                {
                    var subject = _heap.Regions.TryGetValue(constructed, out var constructedRegion) ? constructedRegion.Display : constructed;
                    steps.Add(new CodeFlowStep("construction", $"constructs {subject} in {calleeSymbol}",
                                               Provenance(caller.BodyId, step.Edge.OperationId)?.Span ?? BodySource(callee.BodyId) ?? accessSource));
                    continue;
                }

                var receiver = callee.Receivers.Order(StringComparer.Ordinal).Select(region => _heap.Regions[region].Display).FirstOrDefault();
                steps.Add(new CodeFlowStep("call", receiver is null ? $"calls {calleeSymbol}" : $"calls {calleeSymbol} on {receiver}",
                                           Provenance(caller.BodyId, step.Edge.OperationId)?.Span ?? accessSource));
            }

            steps.AddRange(held.OrderBy(info => info.Acquisition.Path, StringComparer.Ordinal).ThenBy(info => info.Acquisition.StartLine)
                               .Select(info => new CodeFlowStep("acquire", $"acquires {info.Display}", info.Acquisition)));
            steps.Add(new CodeFlowStep("access", accessText, accessSource));
            return steps;
        }

        private SourceSpan? BodySource(string bodyId) =>
            input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                ? body.Blocks.SelectMany(block => block.Operations).FirstOrDefault()?.Provenance.Span
                : null;

        /// <summary>The access's root: the root of its execution tree; a construction or type-initializer execution described as one; or
        /// <c>startup</c> for a tree startup starts.</summary>
        private AccessRoot Root()
        {
            var top = execution.TreeRootId;
            if (top == ExecutionModel.STARTUP)
                return new AccessRoot(ExecutionModel.STARTUP, ExecutionModel.STARTUP, ExecutionModel.STARTUP, CONSTRUCTION_PROVIDER, ExecutionModel.STARTUP,
                                      new InvocationPolicy(Multiplicity.AtMostOnce, SelfOverlap.Serialized, ""), input.Scope.ScopeId);
            if (RootOf(top) is { } root)
                return root;

            var described = input.Executions.Executions.FirstOrDefault(candidate => candidate.Id == top) ?? execution;
            return new AccessRoot(described.Id, described.Display, described.Display, CONSTRUCTION_PROVIDER,
                                  described.Kind == ExecutionKind.TypeInitializer ? "type-initializer" : "construction", described.Policy,
                                  input.Scope.ScopeId);
        }

        private AccessRoot? RootOf(string rootId) =>
            input.Scope.Roots.FirstOrDefault(root => root.StableRootId == rootId) is { } root
                ? new AccessRoot(root.StableRootId, root.Entry.Symbol, root.Entry.Display, root.ProviderId, root.RootKind, root.InvocationPolicy,
                                 input.Scope.ScopeId, root.InstanceBindings.ReceiverType, root.InstanceBindings.ReceiverTypeKey)
                : null;

        private string Symbol(MethodInstance instance) =>
            input.Scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;

        private string MethodSymbol(string bodyId) =>
            input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body) ? body.MethodSymbol : bodyId;

        /// <summary>A merged context of the accessing instance, or of the region's creation, and an unresolved registration that may
        /// change a container object's binding.</summary>
        private IReadOnlyList<string> Uncertainties(MethodInstance instance, HeapRegion region, SummaryAccess access, AccessResource resource,
                                                    IReadOnlyList<PathPredicate> conditions)
        {
            var uncertainties = new List<string>();
            // A guard the analysis cannot read is abstracted away rather than dropped, and says so on the finding (TD-095).
            uncertainties.AddRange(conditions.Where(condition => !condition.IsSupported)
                                             .Select(condition => PathConditions.UnsupportedUncertainty(condition.Text))
                                             .Distinct(StringComparer.Ordinal));
            // A comparer the analysis cannot read leaves the cell unknown; the candidate stays, with the reason recorded.
            if (access.Selector is not null && access.Selector != ElementSelector.Unknown && resource.Selector == ElementSelector.Unknown)
                uncertainties.Add(ConflictFindings.KEY_EQUALITY_UNCERTAINTY);
            if (instance.IsMerged)
                uncertainties.Add(ConflictFindings.MergedContextUncertainty(MethodSymbol(instance.BodyId)));
            else if (region.IsMerged || region.IsOpen)
                uncertainties.Add(ConflictFindings.MergedContextUncertainty(region.SiteBodyId is { } site ? MethodSymbol(site) : MethodSymbol(instance.BodyId)));
            if (region.Kind == HeapRegionKind.Di)
            {
                uncertainties.AddRange(input.Scope.DiIndex.UnresolvedRegistrations.Select(unresolved =>
                    $"An unresolved registration at {unresolved.Source.Path}:{unresolved.Source.StartLine} may change this binding."));
            }

            uncertainties.AddRange(_heap.RegionUncertainties.GetValueOrDefault(region.Identity) ?? []);
            return uncertainties;
        }

        /// <summary>What binds a container object's region: the injected members that hold its service, then its registrations.</summary>
        private IReadOnlyList<BindingEvidence> BindingEvidenceOf(HeapRegion region)
        {
            if (region.Kind != HeapRegionKind.Di)
                return [];
            var serviceKey = region.Identity.Split('|')[1];
            var members = input.Scope.InjectionBindings
                               .SelectMany(type => type.Bindings)
                               .Where(binding => binding.Resolution.Binding is { } bound && bound.ServiceTypeKey == serviceKey &&
                                                 bound.ImplementationTypeKey == region.TypeKey)
                               .SelectMany(binding => binding.Evidence);
            var registrations = input.Scope.DiIndex.Registrations
                                     .Where(registration => registration.IsSupported && registration.ImplementationTypeKey == region.TypeKey &&
                                                            (registration.ServiceTypeKey == serviceKey ||
                                                             serviceKey == DiIndex.HOSTED_SERVICE_KEY && registration.IsHostedService))
                                     .Select(registration => new BindingEvidence("registration",
                                                                                 $"{registration.Method} registers {registration.ServiceType ?? registration.ImplementationType}",
                                                                                 registration.Source));
            return members.Concat(registrations).Distinct().ToArray();
        }

        /// <summary>The resources an access touches: a static field's storage, or the field of each region its base points to, or the
        /// wildcard resource of each region a wildcard path starts from.</summary>
        private IReadOnlyList<AccessResource> Resources(MethodInstance instance, SummaryAccess access)
        {
            var resources = new List<AccessResource>();

            void Add(string regionId, bool wildcard)
            {
                foreach (var collection in CollectionsOf(regionId, access))
                {
                    var resource = Resource(regionId, access.Field, wildcard, access.Selector, collection);
                    if (!resources.Any(candidate => candidate.Identity == resource.Identity))
                        resources.Add(resource);
                }
            }

            if (access.Field.IsStatic)
            {
                Add(_heap.StaticRegionOf(instance.Id, access.Field), false);
                return resources;
            }

            foreach (var @base in access.Bases)
            {
                var (value, wildcard) = @base is PathValue { IsWildcard: true } path ? (path.Base, true) : (@base, false);
                foreach (var region in _heap.Resolve(instance.Id, value).Order(StringComparer.Ordinal))
                    Add(region, wildcard);
            }

            return resources;
        }

        /// <summary>
        /// The collections an access on a collection may work on: what the field of that object holds, which is the collection
        /// itself and not the name it was reached by. It is read from the field's slot and not from the values of this one
        /// access, so every access of that field on that object answers it alike. A field that may hold several collections is
        /// an access on each of them, exactly as a base that may point to several objects is an access on each: an ambiguity is
        /// not a proof that two accesses touch different collections, and naming such a field after itself would leave it unable
        /// to meet a field that names one of them (ADR 0010). A collection the analysis cannot name at all leaves the field as
        /// the only identity there is.
        /// </summary>
        private IReadOnlyList<string?> CollectionsOf(string regionId, SummaryAccess access)
        {
            if (!access.IsOnCollection)
                return [null];

            var targets = _heap.PointsTo(regionId, FieldSlot.Key(access.Field));
            return targets.Count == 0 ? [null] : targets.Order(StringComparer.Ordinal).Select(target => (string?)target).ToArray();
        }

        private AccessResource Resource(string regionId, IrFieldRef field, bool wildcard, ElementSelector? selector,
                                        string? collectionId = null)
        {
            var region = _heap.Regions[regionId];
            selector = selector?.UnderEquality(Equality(regionId, field));
            if (wildcard)
            {
                return new AccessResource(DeclaringAssembly(region.TypeKey) ?? field.Assembly, input.Scope.ScopeId, region.Display, [PathValue.WILDCARD],
                                          new MemberKey(PathValue.WILDCARD, PathValue.WILDCARD, IrFieldKind.Field), regionId, true)
                {
                    RegionKey = RegionKey(region)
                };
            }

            // A cell is the collection's own path with the cell appended, so the collection and its cells read as one family.
            return new AccessResource(field.Assembly, input.Scope.ScopeId, region.Display,
                                      selector is null ? [field.Name] : [field.Name, selector.Text],
                                      new MemberKey(field.ContainingType, field.Name, field.Kind,
                                                    field.ContainingTypeIdentity == field.ContainingType ? null : field.ContainingTypeIdentity),
                                      regionId)
            {
                RegionKey = RegionKey(region),
                Selector = selector,
                CollectionId = collectionId
            };
        }

        /// <summary>A region's context-free identity (R6): a registration's keys, lifetime and number; an allocation's or delegate
        /// creation's body and site ordinal; a static's declaring type; a receiver's type. Other kinds have context-free identities.</summary>
        private string RegionKey(HeapRegion region)
        {
            switch (region.Kind)
            {
                case HeapRegionKind.Di:
                {
                    var parts = region.Identity.Split('|');
                    var registration = $"{parts[0]}|{parts[1]}|{parts[2]}";
                    return parts[2].Contains('#', StringComparison.Ordinal) ? registration : $"{registration}#1";
                }
                case HeapRegionKind.Allocation when region.SiteBodyId is { } body:
                    return $"alloc|{body}#{region.TypeKey}#{SiteOrdinal(body, region.SiteOperationId, operation => operation is IrAllocateOperation)}";
                case HeapRegionKind.Delegate when region.SiteBodyId is { } body:
                    return $"delegate|{body}#{SiteOrdinal(body, region.SiteOperationId, operation => operation is IrCreateDelegateOperation)}";
                case HeapRegionKind.Static:
                    return $"static|{region.TypeKey}";
                case HeapRegionKind.Receiver:
                    return $"receiver|{region.TypeKey}";
                default:
                    return region.Identity;
            }
        }

        /// <summary>The 1-based position of a site among its body's sites of the same kind in source order, or the operation id
        /// (prefixed) for a site that is no such operation.</summary>
        private string SiteOrdinal(string bodyId, int? operationId, Func<IrOperation, bool> isSite)
        {
            if (!input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                return $"op{operationId}";
            var sites = body.Blocks.SelectMany(block => block.Operations).Where(isSite)
                            .OrderBy(operation => operation.Provenance.Span.StartLine)
                            .ThenBy(operation => operation.Provenance.Span.StartColumn)
                            .ThenBy(operation => operation.Id)
                            .Select(operation => operation.Id)
                            .ToList();
            var index = operationId is { } id ? sites.IndexOf(id) : -1;
            return index < 0 ? $"op{operationId}" : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string? DeclaringAssembly(string? typeKey)
        {
            if (typeKey is null)
                return null;
            var colon = typeKey.IndexOf(':');
            var bracket = typeKey.IndexOf('<');
            return colon > 0 && (bracket < 0 || colon < bracket) ? typeKey[..colon] : null;
        }

        private static HashSet<(string, int)> Get<TKey>(Dictionary<TKey, HashSet<(string, int)>> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
                map.Add(key, set = []);
            return set;
        }

        private static bool Grow(HashSet<(string, int)> target, IEnumerable<(string, int)> values)
        {
            var changed = false;
            foreach (var value in values)
                changed |= target.Add(value);
            return changed;
        }
    }
}

/// <summary>
/// Pairs accesses on one resource of one scope that may run at the same time: never read/read, never a pair neither of whose sides
/// is a conflicting operation (TD-072), never construction-local, never on a
/// thread-confined region, only across executions that overlap, and never
/// where the happens-before graph orders the two accesses. The candidates come from <see cref="CandidateIndex"/>, each unordered
/// pair once.
/// </summary>
public static class InterproceduralPairing
{
    public const string SKIP_READ_READ = "read-read";
    public const string SKIP_NO_CONFLICTING_OPERATION = "no-conflicting-operation";
    public const string SKIP_NO_OVERLAP = "no-overlap";
    public const string SKIP_CONFINED = "confined";
    public const string SKIP_ORDERED = "ordered";
    public const string SKIP_UNSATISFIABLE_PATH = "unsatisfiable-path";
    public const string SKIP_DISJOINT_ITERATION = "disjoint-iteration";

    public static PairAnalysis Pair(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap)
    {
        var candidates = accesses.Where(access => !access.IsConstructionLocal).ToArray();
        var pairs = new List<AccessPair>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var counted = 0;
        var suppressed = 0;

        void Consider(Access first, Access second, AccessResource reported, IReadOnlyList<string> uncertainties)
        {
            counted++;
            var skip = first.Operation == AccessOperation.Read && second.Operation == AccessOperation.Read ? SKIP_READ_READ
                : !first.Operation.Conflicts() && !second.Operation.Conflicts() ? SKIP_NO_CONFLICTING_OPERATION
                : first.Resource.Scope != second.Resource.Scope || !executions.Overlaps(first.ExecutionId, second.ExecutionId) ||
                  first.ExecutionId == second.ExecutionId && IsHostedInstanceTransient(first) ? SKIP_NO_OVERLAP
                : executions.Ordered(first, second) ? SKIP_ORDERED
                : IsConfined(first) || IsConfined(second) ? SKIP_CONFINED
                : PathConditions.Contradict(first.Conditions, second.Conditions) ? SKIP_UNSATISFIABLE_PATH
                : IsDisjointIteration(first, second) ? SKIP_DISJOINT_ITERATION
                : null;
            if (skip is not null)
            {
                skips[skip] = skips.GetValueOrDefault(skip) + 1;
                return;
            }

            var protection = PairProtection.Of(first, second);
            if (protection == PairProtection.SUFFICIENT)
            {
                suppressed++;
                return;
            }

            pairs.Add(new AccessPair(first, second, protection) { Resource = reported, Uncertainties = uncertainties });
        }

        // A transient resolved for a hosted service is one object per hosted instance: an execution overlapping itself because the
        // instance count is unknown runs its two sides on different instances, so on different transients.
        bool IsHostedInstanceTransient(Access access) =>
            heap.Regions[access.Resource.RegionId!] is { Kind: HeapRegionKind.Di } region &&
            region.Context.StartsWith($"di|{DiIndex.HOSTED_SERVICE_KEY}|", StringComparison.Ordinal);

        bool IsConfined(Access access) =>
            executions.Ownership.TryGetValue(access.Resource.RegionId!, out var ownership) && ownership.Kind == OwnershipKind.ThreadConfined;

        var index = CandidateIndex.Build(candidates, heap);
        foreach (var candidate in index.OrderedPairs())
            Consider(candidate.First, candidate.Second, candidate.Reported, candidate.Uncertainties);

        return new PairAnalysis(pairs, counted, suppressed, skips)
        {
            CartesianBound = (int)((long)candidates.Length * (candidates.Length + 1) / 2),
            Buckets = index.Buckets,
            LargestBucket = index.LargestBucket
        };
    }

    /// <summary>Whether the two accesses are two iterations of one run of a parallel loop, each naming the cell its own
    /// iteration number names. Two runs of a loop prove nothing, so the two must be one execution of one body: a loop that
    /// joins before it returns can only overlap itself within one run (TD-068).</summary>
    internal static bool IsDisjointIteration(Access first, Access second) =>
        first.IsIterationIndexed && second.IsIterationIndexed &&
        string.Equals(first.ExecutionId, second.ExecutionId, StringComparison.Ordinal) &&
        string.Equals(first.BodyId, second.BodyId, StringComparison.Ordinal);
}

internal enum PairEnumeration
{
    Resource,
    Wildcard,
    OpenRegion
}

/// <summary>An unordered pair of accesses to compare, the resource it is reported on, and the enumeration that produced it.</summary>
internal sealed record CandidatePair(Access First, Access Second, AccessResource Reported, IReadOnlyList<string> Uncertainties,
                                     PairEnumeration Enumeration);

/// <summary>
/// The buckets of one scope's non-construction-local accesses (R4): a resource bucket per resource identity, and a wildcard bucket per
/// scope and region, never a resource bucket. Each unordered pair that may race comes from exactly one enumeration: within a resource
/// bucket; within a wildcard bucket and between it and its region's resource buckets; or between an open region's bucket and a closed
/// region of its group, two resource buckets with one path and member, or either region's wildcard bucket against every bucket of
/// the other.
/// </summary>
internal sealed class CandidateIndex
{
    private sealed class RegionBuckets(string scope, string regionId)
    {
        internal string Scope { get; } = scope;
        internal string RegionId { get; } = regionId;
        internal List<List<Access>> Resources { get; } = [];
        internal List<Access> Wildcard { get; } = [];
    }

    private readonly SortedDictionary<string, List<Access>> _resources = new(StringComparer.Ordinal);

    /// <summary>The cell buckets of one collection, by that collection's identity: a cell meets the cells it may be, and no more
    /// (TD-075).</summary>
    private readonly SortedDictionary<string, List<List<Access>>> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scope, string RegionId), RegionBuckets> _regions = [];
    private readonly List<RegionBuckets> _regionOrder = [];
    private readonly Dictionary<Access, int> _positions = new(ReferenceEqualityComparer.Instance);
    private readonly HeapSolution _heap;

    private CandidateIndex(HeapSolution heap) => _heap = heap;

    public int Buckets => _resources.Count + _regionOrder.Count(region => region.Wildcard.Count != 0);

    public int LargestBucket => _resources.Values.Select(bucket => bucket.Count)
                                          .Concat(_regionOrder.Select(region => region.Wildcard.Count))
                                          .DefaultIfEmpty(0)
                                          .Max();

    public static CandidateIndex Build(IReadOnlyList<Access> accesses, HeapSolution heap)
    {
        var index = new CandidateIndex(heap);
        foreach (var access in accesses)
        {
            index._positions.TryAdd(access, index._positions.Count);
            var region = index.Region(access.Resource.Scope, access.Resource.RegionId!);
            if (access.Resource.IsWildcard)
            {
                region.Wildcard.Add(access);
                continue;
            }

            if (!index._resources.TryGetValue(access.Resource.Identity, out var bucket))
            {
                index._resources.Add(access.Resource.Identity, bucket = []);
                region.Resources.Add(bucket);
                if (access.Resource.Selector is not null)
                {
                    if (!index._cells.TryGetValue(access.Resource.StructuralIdentity, out var cells))
                        index._cells.Add(access.Resource.StructuralIdentity, cells = []);
                    cells.Add(bucket);
                }
            }

            bucket.Add(access);
        }

        return index;
    }

    private RegionBuckets Region(string scope, string regionId)
    {
        if (!_regions.TryGetValue((scope, regionId), out var region))
        {
            _regions.Add((scope, regionId), region = new RegionBuckets(scope, regionId));
            _regionOrder.Add(region);
        }

        return region;
    }

    /// <summary>The pairs in the order of an all-pairs loop over the accesses: by the earlier access, then the later one. Findings keep
    /// the first of two pairs with one identity, so this order makes them what the plain loop gives.</summary>
    public IEnumerable<CandidatePair> OrderedPairs() =>
        Pairs().Select(pair => (Pair: pair, First: _positions[pair.First], Second: _positions[pair.Second]))
               .OrderBy(item => Math.Min(item.First, item.Second))
               .ThenBy(item => Math.Max(item.First, item.Second))
               .Select(item => item.Pair);

    public IEnumerable<CandidatePair> Pairs()
    {
        foreach (var bucket in _resources.Values)
        {
            foreach (var pair in Within(bucket, PairEnumeration.Resource))
                yield return pair;
        }

        // Two cells of one collection meet only where their selectors may name one cell; proven-distinct cells never do, which is
        // what keeps this short of the all-pairs loop (TD-075).
        foreach (var cells in _cells.Values)
        {
            for (var first = 0; first < cells.Count; first++)
            {
                for (var second = first + 1; second < cells.Count; second++)
                {
                    if (!cells[first][0].Resource.Selector!.MayOverlap(cells[second][0].Resource.Selector!))
                        continue;
                    foreach (var pair in Across(cells[first], cells[second], (item, other) => ReportedCell(item.Resource, other.Resource),
                                                _ => [], PairEnumeration.Resource))
                    {
                        yield return pair;
                    }
                }
            }
        }

        foreach (var region in _regionOrder.Where(region => region.Wildcard.Count != 0))
        {
            foreach (var pair in Within(region.Wildcard, PairEnumeration.Wildcard))
                yield return pair;
            foreach (var bucket in region.Resources)
            {
                foreach (var pair in Across(region.Wildcard, bucket, (first, _) => first.Resource, _ => [], PairEnumeration.Wildcard))
                    yield return pair;
            }
        }

        foreach (var open in _regionOrder.Where(region => _heap.Regions[region.RegionId].IsOpen))
        {
            var group = _heap.Regions[open.RegionId].Group;
            foreach (var closed in _regionOrder.Where(region => region.Scope == open.Scope && _heap.Regions[region.RegionId] is { IsOpen: false } heapRegion &&
                                                                heapRegion.Group == group))
            {
                foreach (var pair in OpenAndClosed(open, closed))
                    yield return pair;
            }
        }
    }

    /// <summary>Which of two cells a pair between them is reported on: the cell that is proven, since that is what a reader can
    /// act on, and the first by text where that does not decide it. The choice does not depend on which side is which.</summary>
    internal static AccessResource ReportedCell(AccessResource first, AccessResource second) =>
        (first.Selector!.IsProven, second.Selector!.IsProven) switch
        {
            (false, true) => second,
            (true, false) => first,
            _ => string.CompareOrdinal(second.Selector.Text, first.Selector.Text) < 0 ? second : first
        };

    private static IEnumerable<CandidatePair> OpenAndClosed(RegionBuckets open, RegionBuckets closed)
    {
        IEnumerable<CandidatePair> Pair(IReadOnlyList<Access> openBucket, IReadOnlyList<Access> closedBucket) =>
            Across(openBucket, closedBucket, (_, second) => second.Resource, first => first.Uncertainties, PairEnumeration.OpenRegion);

        foreach (var openBucket in open.Resources)
        {
            var key = openBucket[0].Resource;
            foreach (var closedBucket in closed.Resources.Where(bucket => bucket[0].Resource.Member.Identity == key.Member.Identity &&
                                                                          bucket[0].Resource.AccessPath.SequenceEqual(key.AccessPath)))
            {
                foreach (var pair in Pair(openBucket, closedBucket))
                    yield return pair;
            }

            foreach (var pair in Pair(openBucket, closed.Wildcard))
                yield return pair;
        }

        foreach (var closedBucket in closed.Resources.Append(closed.Wildcard))
        {
            foreach (var pair in Pair(open.Wildcard, closedBucket))
                yield return pair;
        }
    }

    private static IEnumerable<CandidatePair> Within(IReadOnlyList<Access> bucket, PairEnumeration enumeration)
    {
        for (var first = 0; first < bucket.Count; first++)
        {
            for (var second = first; second < bucket.Count; second++)
                yield return new CandidatePair(bucket[first], bucket[second], bucket[first].Resource, [], enumeration);
        }
    }

    /// <summary>Every pair of an access of the first bucket with an access of the second, the first access first.</summary>
    private static IEnumerable<CandidatePair> Across(IReadOnlyList<Access> firsts, IReadOnlyList<Access> seconds, Func<Access, Access, AccessResource> reported,
                                                     Func<Access, IReadOnlyList<string>> uncertainties, PairEnumeration enumeration)
    {
        foreach (var first in firsts)
        {
            foreach (var second in seconds)
                yield return new CandidatePair(first, second, reported(first, second),
                                               uncertainties(first), enumeration);
        }
    }
}