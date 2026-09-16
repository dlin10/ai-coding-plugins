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
    public const string STARTUP_CONSTRUCTION_ACCESS = "startup-construction-access";
    public const string MERGED_CONTEXT = "merged-context";
    public const string WILDCARD_ACCESS = "wildcard-access";
}

/// <summary>What a scope's analysis covered, over what the heap reaches only; <see cref="LoweredNotReached"/> and
/// <see cref="OutsideLoweredSet"/> are inventory, not counted against coverage.</summary>
public sealed record InterproceduralCoverage(string ScopeId, IReadOnlyDictionary<string, int> Counters,
                                             IReadOnlyList<(string Callee, int Count)> TopOpaqueCallees, IReadOnlyList<string> LoweredNotReached,
                                             IReadOnlyList<UnreachedMember> OutsideLoweredSet);

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
        foreach (var execution in input.Executions.Executions)
        {
            if (input.Executions.Entries.TryGetValue(execution.Id, out var entries))
                accesses.AddRange(new ExecutionCollector(input, execution, entries).Collect());
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
        var reachedRegions = heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Di)
                                 .Select(region => (region.Display, region.TypeKey))
                                 .ToHashSet();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoverageCounters.REACHABLE_BODIES] = heap.ReachableBodies.Count,
            [CoverageCounters.SCC_BUDGET_EXCEEDED] = heap.Counters.GetValueOrDefault(HeapCounters.SCC_BUDGET_EXCEEDED),
            [CoverageCounters.OPAQUE_CALL] = opaque.Length,
            // The delegates handed to opaque calls, not the calls: one creation passed to two calls is one delegate.
            [CoverageCounters.DELEGATE_TO_OPAQUE] = opaque.SelectMany(item => item.Call.Delegates.Select(delegateValue => (item.BodyId, delegateValue)))
                                                          .Distinct()
                                                          .Count(),
            [CoverageCounters.ELEMENT_OPERATION] = summaries.Sum(summary => summary.Elements.Count),
            [CoverageCounters.UNANALYSED_REGISTRATION] = input.Scope.Reachable.UnanalysedRegistrations
                                                              .Count(registration => reachedRegions.Contains((registration.RegionId,
                                                                                                             registration.Registration.ImplementationTypeKey))),
            [CoverageCounters.NO_RECEIVER_OBJECT] = heap.Counters.GetValueOrDefault(HeapCounters.NO_RECEIVER_OBJECT),
            [CoverageCounters.STARTUP_CONSTRUCTION_ACCESS] = input.Executions.Counters.GetValueOrDefault(ExecutionCounters.STARTUP_CONSTRUCTION_ACCESS),
            [CoverageCounters.MERGED_CONTEXT] = heap.Counters.GetValueOrDefault(HeapCounters.MERGED_CONTEXT),
            [CoverageCounters.WILDCARD_ACCESS] = accesses.Where(access => access.Resource.IsWildcard)
                                                         .Select(access => (access.BodyId, access.OperationId)).Distinct().Count()
        };
        var topCallees = opaque.GroupBy(item => item.Call.Callee, StringComparer.Ordinal)
                               .Select(group => (Callee: group.Key, Count: group.Count()))
                               .OrderByDescending(item => item.Count)
                               .ThenBy(item => item.Callee, StringComparer.Ordinal)
                               .Take(TOP_CALLEES)
                               .ToArray();
        return new InterproceduralCoverage(input.Scope.ScopeId, counters, topCallees, heap.LoweredNotReached, input.Scope.Reachable.Unreached);
    }

    private sealed record State(string Instance, string? Interval);

    private sealed record PathNode(State State, PathNode? Parent, CallEdge? Edge);

    private sealed record LockInfo(string Display, string? SingleObjectId, SourceSpan Acquisition);

    private sealed class ExecutionCollector(InterproceduralInput input, ExecutionInstance execution, IReadOnlyList<ExecutionEntry> entries)
    {
        private readonly HeapSolution _heap = input.Heap;
        private readonly Dictionary<string, List<CallEdge>> _edgesByCaller = input.Heap.Edges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                                                                                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly List<(ExecutionEntry Entry, PathNode Node)> _visits = [];
        private readonly Dictionary<string, (ExecutionEntry Entry, PathNode Node)> _firstPaths = new(StringComparer.Ordinal);
        private readonly HashSet<CallEdge> _executionEdges = [];
        private readonly Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>>> _lockStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LockInfo> _locks = new(StringComparer.Ordinal);
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

        /// <summary>Breadth-first over the call edges from one entry; a state is an instance with its construction interval, visited
        /// once per entry. A constructor call on an allocation opens that allocation's interval in place of the current one.</summary>
        private void Visit(ExecutionEntry entry)
        {
            var start = new State(entry.InstanceId, entry.IntervalObject);
            var visited = new HashSet<State> { start };
            var pending = new Queue<PathNode>([new PathNode(start, null, null)]);
            while (pending.TryDequeue(out var node))
            {
                if (!_heap.Instances.TryGetValue(node.State.Instance, out var instance))
                    continue;
                _visits.Add((entry, node));
                _firstPaths.TryAdd(node.State.Instance, (entry, node));

                foreach (var edge in OrderedEdges(instance))
                {
                    _executionEdges.Add(edge);
                    var interval = node.State.Interval;
                    if (instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } &&
                        _heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                    {
                        var created = callee.Receivers.Where(region => _heap.Regions[region].Kind == HeapRegionKind.Allocation)
                                            .Order(StringComparer.Ordinal).ToArray();
                        if (created.Length != 0 && (interval is null || !created.Contains(interval)))
                            interval = created[0];
                    }

                    var next = new State(edge.CalleeInstance, interval);
                    if (visited.Add(next))
                        pending.Enqueue(new PathNode(next, node, edge));
                }
            }
        }

        /// <summary>A caller's edges in order of call source (path, line, column), then callee method id, callee context and reason.</summary>
        private IEnumerable<CallEdge> OrderedEdges(MethodInstance instance) =>
            (_edgesByCaller.GetValueOrDefault(instance.Id) ?? [])
            .Select(edge => (Edge: edge, Source: Provenance(instance.BodyId, edge.OperationId)?.Span))
            .OrderBy(item => item.Source?.Path ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Source?.StartLine ?? 0)
            .ThenBy(item => item.Source?.StartColumn ?? 0)
            .ThenBy(item => _heap.Instances.GetValueOrDefault(item.Edge.CalleeInstance)?.BodyId ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Edge.CalleeInstance, StringComparer.Ordinal)
            .ThenBy(item => item.Edge.Reason, StringComparer.Ordinal)
            .Select(item => item.Edge);

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

        /// <summary>The locks each instance must hold on entry: none for an execution's entries, and otherwise the intersection of the
        /// states at every incoming call site, iterated from the top until nothing changes.</summary>
        private void SolveLocks()
        {
            var entryInstances = entries.Select(entry => entry.InstanceId).ToHashSet(StringComparer.Ordinal);
            var entryLocks = new Dictionary<string, Dictionary<string, HeldLock>>(StringComparer.Ordinal);
            foreach (var instance in entryInstances)
                entryLocks[instance] = new Dictionary<string, HeldLock>(StringComparer.Ordinal);

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var instanceId in VisitedInstances())
                {
                    if (entryLocks.TryGetValue(instanceId, out var entry) && input.Scope.Reachable.Bodies.TryGetValue(_heap.Instances[instanceId].BodyId, out var body))
                        _lockStates[instanceId] = MustHeldLocks.Compute(body, operation => Effect(_heap.Instances[instanceId], operation), entry);
                }

                var incoming = new Dictionary<string, Dictionary<string, HeldLock>>(StringComparer.Ordinal);
                foreach (var edge in _executionEdges)
                {
                    if (entryInstances.Contains(edge.CalleeInstance) || !_lockStates.TryGetValue(edge.CallerInstance, out var states) ||
                        !states.TryGetValue(edge.OperationId, out var atCall))
                    {
                        continue;
                    }

                    incoming[edge.CalleeInstance] = incoming.TryGetValue(edge.CalleeInstance, out var previous)
                        ? previous.Where(pair => atCall.ContainsKey(pair.Key))
                                  .ToDictionary(pair => pair.Key, pair => pair.Value with { Depth = 1 }, StringComparer.Ordinal)
                        : atCall.ToDictionary(pair => pair.Key, pair => pair.Value with { Depth = 1 }, StringComparer.Ordinal);
                }

                foreach (var (instanceId, locks) in incoming)
                {
                    if (!entryLocks.TryGetValue(instanceId, out var current) || !current.Keys.ToHashSet().SetEquals(locks.Keys))
                    {
                        entryLocks[instanceId] = locks;
                        changed = true;
                    }
                }
            }
        }

        /// <summary>A lock's identity is the set of regions its value points to; every lock is released by value, so a release of a
        /// lock not held as such clears every held lock it may be.</summary>
        private LockEffect Effect(MethodInstance instance, IrOperation operation)
        {
            if (operation is not (IrAcquireOperation or IrReleaseOperation) ||
                instance.Summary.Locks.FirstOrDefault(candidate => candidate.OperationId == operation.Id) is not { } transfer)
            {
                return LockEffect.None;
            }

            var regions = transfer.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct().Order(StringComparer.Ordinal).ToArray();
            var key = regions.Length != 0 ? "locks:" + string.Join(",", regions) : $"unresolved:{instance.Id}:{transfer.Origin}";
            if (transfer.IsAcquire && !_locks.ContainsKey(key))
            {
                var single = regions.Length == 1 && input.Executions.IsSingleObject(regions[0]) ? $"{input.Scope.ScopeId}|{regions[0]}" : null;
                var display = regions.Length == 0
                    ? $"lock on an unresolved object at {transfer.Provenance.Span.Path}:{transfer.Provenance.Span.StartLine} (identity unknown, not one object per process)"
                    : string.Join(", ", regions.Select(region => _heap.Regions[region].Display)) + (single is null ? " (not one object per process)" : "");
                _locks[key] = new LockInfo(display, single, transfer.Provenance.Span);
            }

            return new LockEffect(transfer.IsAcquire ? LockEffectKind.Acquire : LockEffectKind.Release,
                                  new LockObject(key, key, null, IdentityUnknown: true));
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
            foreach (var instanceId in VisitedInstances())
            {
                var instance = _heap.Instances[instanceId];
                foreach (var store in instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store))
                {
                    var loads = Loads(instance, store.Dependencies);
                    foreach (var resource in Resources(instance, store))
                    {
                        foreach (var (loadInstanceId, loadOperation) in loads.OrderBy(load => load.Instance, StringComparer.Ordinal).ThenBy(load => load.Operation))
                        {
                            var loadInstance = _heap.Instances[loadInstanceId];
                            if (loadInstance.Summary.Accesses.FirstOrDefault(access => access.OperationId == loadOperation && access.Kind == SummaryAccessKind.Load)
                                    is not { } load ||
                                !Resources(loadInstance, load).Any(candidate => candidate.Identity == resource.Identity))
                            {
                                continue;
                            }

                            var key = (instanceId, store.OperationId, resource.Identity);
                            if (!feeding.TryGetValue(key, out var list))
                                feeding.Add(key, list = []);
                            if (!list.Contains((loadInstanceId, loadOperation)))
                                list.Add((loadInstanceId, loadOperation));
                            dropped.Add((loadInstance.Id, loadOperation, resource.Identity));
                        }
                    }
                }
            }

            var accesses = new List<Access>();
            var seen = new HashSet<(string, int, string, AccessOperation, bool)>();
            foreach (var (entry, node) in _visits)
            {
                var instance = _heap.Instances[node.State.Instance];
                foreach (var access in instance.Summary.Accesses)
                {
                    foreach (var resource in Resources(instance, access))
                    {
                        if (access.Kind == SummaryAccessKind.Load && dropped.Contains((instance.Id, access.OperationId, resource.Identity)))
                            continue;

                        var sources = feeding.GetValueOrDefault((instance.Id, access.OperationId, resource.Identity));
                        var operation = access.Kind == SummaryAccessKind.Load ? AccessOperation.Read
                            : sources is { Count: > 0 } ? AccessOperation.ReadModifyWrite
                            : AccessOperation.Write;
                        var regionId = resource.RegionId!;
                        var local = node.State.Interval == regionId && !input.Executions.PublishedObjects.Contains(regionId);
                        if (!seen.Add((instance.BodyId, access.OperationId, resource.Identity, operation, local)))
                            continue;

                        var held = HeldLocks(instance.Id, access.OperationId);
                        var region = _heap.Regions[regionId];
                        var ownership = input.Executions.Ownership.GetValueOrDefault(regionId);
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
                            Uncertainties(instance, region))
                        {
                            ExecutionId = execution.Id,
                            Ownership = ownership?.Kind ?? OwnershipKind.Unknown,
                            OwnershipEvidence = ownership?.Evidence ?? [],
                            IsConstructionLocal = local,
                            ReadSources = (sources ?? []).Select(ReadSourceOf).ToArray(),
                            BodyId = instance.BodyId,
                            OperationId = access.OperationId,
                            InstanceId = instance.Id
                        });
                    }
                }
            }

            return accesses;
        }

        private ReadSource ReadSourceOf((string Instance, int Operation) load)
        {
            var instance = _heap.Instances[load.Instance];
            var access = instance.Summary.Accesses.First(candidate => candidate.OperationId == load.Operation);
            var (entry, node) = _firstPaths[load.Instance];
            var held = HeldLocks(instance.Id, access.OperationId);
            return new ReadSource(Symbol(instance), access.Provenance.Span,
                                  CodeFlow(entry, node, held, $"read {access.Field.ContainingType}.{access.Field.Name}", access.Provenance.Span));
        }

        private IReadOnlyList<LockInfo> HeldLocks(string instanceId, int operationId) =>
            _lockStates.TryGetValue(instanceId, out var states) && states.TryGetValue(operationId, out var held)
                ? held.Keys.Select(key => _locks.GetValueOrDefault(key)).OfType<LockInfo>().ToArray()
                : [];

        private IReadOnlyList<CodeFlowStep> CodeFlow(ExecutionEntry entry, PathNode node, IReadOnlyList<LockInfo> held, string accessText, SourceSpan accessSource)
        {
            var steps = new List<CodeFlowStep>();
            var entryInstance = _heap.Instances[entry.InstanceId];
            steps.Add(execution.RootId is not null
                ? new CodeFlowStep("root", $"{input.Scope.Roots.First(candidate => candidate.StableRootId == execution.RootId).Entry.Display} starts",
                                   input.Scope.Roots.First(candidate => candidate.StableRootId == execution.RootId).Entry.Source)
                : new CodeFlowStep("root", $"{execution.Display} starts", BodySource(entryInstance.BodyId) ?? accessSource));
            if (entry.Kind != ExecutionEntryKind.Root)
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

        /// <summary>The access's root: the root of a root execution, or the construction or type-initializer execution described as one.</summary>
        private AccessRoot Root()
        {
            if (execution.RootId is not null && input.Scope.Roots.FirstOrDefault(root => root.StableRootId == execution.RootId) is { } root)
            {
                return new AccessRoot(root.StableRootId, root.Entry.Symbol, root.Entry.Display, root.ProviderId, root.RootKind, root.InvocationPolicy,
                                      input.Scope.ScopeId, root.InstanceBindings.ReceiverType, root.InstanceBindings.ReceiverTypeKey);
            }

            return new AccessRoot(execution.Id, execution.Display, execution.Display, CONSTRUCTION_PROVIDER,
                                  execution.Kind == ExecutionKind.TypeInitializer ? "type-initializer" : "construction", execution.Policy,
                                  input.Scope.ScopeId);
        }

        private string Symbol(MethodInstance instance) =>
            input.Scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;

        private string MethodSymbol(string bodyId) =>
            input.Scope.Reachable.Bodies.TryGetValue(bodyId, out var body) ? body.MethodSymbol : bodyId;

        /// <summary>A merged context of the accessing instance, or of the region's creation, and an unresolved registration that may
        /// change a container object's binding.</summary>
        private IReadOnlyList<string> Uncertainties(MethodInstance instance, HeapRegion region)
        {
            var uncertainties = new List<string>();
            if (instance.IsMerged)
                uncertainties.Add(ConflictFindings.MergedContextUncertainty(MethodSymbol(instance.BodyId)));
            else if (region.IsMerged || region.IsOpen)
                uncertainties.Add(ConflictFindings.MergedContextUncertainty(region.SiteBodyId is { } site ? MethodSymbol(site) : MethodSymbol(instance.BodyId)));
            if (region.Kind == HeapRegionKind.Di)
            {
                uncertainties.AddRange(input.Scope.DiIndex.UnresolvedRegistrations.Select(unresolved =>
                    $"An unresolved registration at {unresolved.Source.Path}:{unresolved.Source.StartLine} may change this binding."));
            }

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
            if (access.Field.IsStatic)
                return [Resource(_heap.StaticRegionOf(instance.Id, access.Field), access.Field, false)];

            var resources = new List<AccessResource>();
            foreach (var @base in access.Bases)
            {
                var (value, wildcard) = @base is PathValue { IsWildcard: true } path ? (path.Base, true) : (@base, false);
                foreach (var region in _heap.Resolve(instance.Id, value).Order(StringComparer.Ordinal))
                {
                    var resource = Resource(region, access.Field, wildcard);
                    if (!resources.Any(candidate => candidate.Identity == resource.Identity))
                        resources.Add(resource);
                }
            }

            return resources;
        }

        private AccessResource Resource(string regionId, IrFieldRef field, bool wildcard)
        {
            var region = _heap.Regions[regionId];
            if (wildcard)
            {
                return new AccessResource(DeclaringAssembly(region.TypeKey) ?? field.Assembly, input.Scope.ScopeId, region.Display, [PathValue.WILDCARD],
                                          new MemberKey(PathValue.WILDCARD, PathValue.WILDCARD, IrFieldKind.Field), regionId, true);
            }

            return new AccessResource(field.Assembly, input.Scope.ScopeId, region.Display, [field.Name],
                                      new MemberKey(field.ContainingType, field.Name, field.Kind,
                                                    field.ContainingTypeIdentity == field.ContainingType ? null : field.ContainingTypeIdentity),
                                      regionId);
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
/// Pairs accesses on one resource of one scope that may run at the same time: never read/read, never construction-local, never on a
/// thread-confined region, and only across executions that overlap. A wildcard access also meets every access on its region, and an
/// access on an open generic region every access with its path and member on each closed region of its definition.
/// </summary>
public static class InterproceduralPairing
{
    public const string SKIP_READ_READ = "read-read";
    public const string SKIP_NO_OVERLAP = "no-overlap";
    public const string SKIP_CONFINED = "confined";

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
                : first.Resource.Scope != second.Resource.Scope || !executions.Overlaps(first.ExecutionId, second.ExecutionId) ||
                  first.ExecutionId == second.ExecutionId && IsHostedInstanceTransient(first) ? SKIP_NO_OVERLAP
                : IsConfined(first) || IsConfined(second) ? SKIP_CONFINED
                : null;
            if (skip is not null)
            {
                skips[skip] = skips.GetValueOrDefault(skip) + 1;
                return;
            }

            if (first.HeldProtectionIds.Intersect(second.HeldProtectionIds, StringComparer.Ordinal).Any())
            {
                suppressed++;
                return;
            }

            var protection = (first.HeldProtection.Count != 0, second.HeldProtection.Count != 0) switch
            {
                (false, false) => PairProtection.UNPROTECTED,
                (true, true) => PairProtection.DIFFERENT_IDENTITY,
                _ => PairProtection.PARTIAL
            };
            pairs.Add(new AccessPair(first, second, protection) { Resource = reported, Uncertainties = uncertainties });
        }

        // A transient resolved for a hosted service is one object per hosted instance: an execution overlapping itself because the
        // instance count is unknown runs its two sides on different instances, so on different transients.
        bool IsHostedInstanceTransient(Access access) =>
            heap.Regions[access.Resource.RegionId!] is { Kind: HeapRegionKind.Di } region &&
            region.Context.StartsWith($"di|{DiIndex.HOSTED_SERVICE_KEY}|", StringComparison.Ordinal);

        bool IsConfined(Access access) =>
            executions.Ownership.TryGetValue(access.Resource.RegionId!, out var ownership) && ownership.Kind == OwnershipKind.ThreadConfined;

        foreach (var group in candidates.GroupBy(access => access.Resource.Identity, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            for (var first = 0; first < members.Length; first++)
            {
                for (var second = first; second < members.Length; second++)
                    Consider(members[first], members[second], members[first].Resource, []);
            }
        }

        foreach (var wildcard in candidates.Where(access => access.Resource.IsWildcard))
        {
            foreach (var other in candidates.Where(access => !access.Resource.IsWildcard && access.Resource.RegionId == wildcard.Resource.RegionId &&
                                                             access.Resource.Scope == wildcard.Resource.Scope))
            {
                Consider(wildcard, other, wildcard.Resource, []);
            }
        }

        foreach (var open in candidates.Where(access => heap.Regions[access.Resource.RegionId!].IsOpen))
        {
            var group = heap.Regions[open.Resource.RegionId!].Group;
            foreach (var closed in candidates.Where(access => access.Resource.Scope == open.Resource.Scope && heap.Regions[access.Resource.RegionId!] is { IsOpen: false } region &&
                                                              region.Group == group && access.Resource.Member.Identity == open.Resource.Member.Identity &&
                                                              access.Resource.AccessPath.SequenceEqual(open.Resource.AccessPath)))
            {
                Consider(open, closed, closed.Resource, open.Uncertainties);
            }
        }

        return new PairAnalysis(pairs, counted, suppressed, skips);
    }
}
