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
    TypeInitializer
}

/// <summary>One execution of the scope: a root with its invocation policy, a lazily resolved construction, or a type
/// initializer that does not run at startup. <see cref="Subject"/> is the region or closed type a construction builds.</summary>
public sealed record ExecutionInstance(string Id, ExecutionKind Kind, string Display, InvocationPolicy Policy, string? RootId, string? Subject)
{
    public bool OverlapsItself =>
        Policy is { Multiplicity: Multiplicity.Repeated, SelfOverlap: SelfOverlap.MayOverlap } ||
        Policy.Multiplicity == Multiplicity.Unknown || Policy.SelfOverlap == SelfOverlap.Unknown;
}

public enum ExecutionEntryKind
{
    Root,
    Construction,
    TypeInitializer
}

/// <summary>Where an execution starts: its root entry, a constructor chain it runs (starting inside the interval of the object
/// under construction), or a type initializer (inside its closed type's static region).</summary>
public sealed record ExecutionEntry(string InstanceId, ExecutionEntryKind Kind, string? IntervalObject);

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
public sealed record CollectedAccess(string ExecutionId, string InstanceId, SummaryAccess Access, string RegionId, bool IsConstructionLocal);

public static class ExecutionCounters
{
    public const string STARTUP_CONSTRUCTION_ACCESS = "startup-construction-access";
}

public sealed class ExecutionAnalysis
{
    private readonly IReadOnlyDictionary<string, ExecutionInstance> _executions;
    private readonly IReadOnlySet<string> _singleObjects;

    internal ExecutionAnalysis(IReadOnlyList<ExecutionInstance> executions, IReadOnlyList<CollectedAccess> accesses,
                               IReadOnlyDictionary<string, RegionOwnership> ownership, IReadOnlyDictionary<string, int> counters,
                               IReadOnlySet<string> singleObjects, IReadOnlySet<string> publishedObjects,
                               IReadOnlyDictionary<string, IReadOnlySet<string>> instanceExecutions,
                               IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>> entries)
    {
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

    /// <summary>Every access an execution runs, per region; accesses of startup constructions are dropped and counted.</summary>
    public IReadOnlyList<CollectedAccess> Accesses { get; }

    public IReadOnlyDictionary<string, RegionOwnership> Ownership { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }

    /// <summary>The objects under construction that their construction publishes.</summary>
    public IReadOnlySet<string> PublishedObjects { get; }

    /// <summary>The executions each instance runs in; <see cref="ExecutionModel.STARTUP"/> marks startup.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> InstanceExecutions { get; }

    public ExecutionInstance Execution(string id) => _executions[id];

    /// <summary>Two executions of one scope overlap; an execution overlaps itself only when its policy says so.</summary>
    public bool Overlaps(string first, string second) => first != second || _executions[first].OverlapsItself;

    /// <summary>Whether a region is provably one object per process, as a lock identity.</summary>
    public bool IsSingleObject(string regionId) => _singleObjects.Contains(regionId);
}

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

    public static ExecutionAnalysis Build(ScopeProgram scope, HeapSolution heap) => new Builder(scope, heap).Build();

    private sealed class Builder(ScopeProgram scope, HeapSolution heap)
    {
        private readonly Dictionary<string, ExecutionInstance> _executions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _regionExecutions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CallEdge>> _edges = heap.Edges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                                                                        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _instanceExecutions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _chains = new(StringComparer.Ordinal);
        private readonly HashSet<(string Execution, string Instance, string Intervals)> _visits = [];
        private readonly List<(string Execution, string Instance, HashSet<string> Intervals)> _visitList = [];
        private readonly Dictionary<string, List<ExecutionEntry>> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _sharedReach = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _constructed = heap.Constructions.SelectMany(construction => construction.ConstructorInstances
                                                                                         .Select(instance => (Instance: instance, construction.RegionId)))
                                                                       .GroupBy(item => item.Instance, StringComparer.Ordinal)
                                                                       .ToDictionary(group => group.Key, group => group.First().RegionId, StringComparer.Ordinal);

        internal ExecutionAnalysis Build()
        {
            foreach (var root in scope.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
                Add(new ExecutionInstance(RootExecution(root.StableRootId), ExecutionKind.Root, root.Entry.Display, root.InvocationPolicy, root.StableRootId, null));

            AssignRegionExecutions();
            var startupTypeInitializers = StartupTypeInitializers();

            foreach (var (rootId, instanceId) in heap.RootInstances.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Walk(RootExecution(rootId), instanceId, []);
                Entry(RootExecution(rootId), new ExecutionEntry(instanceId, ExecutionEntryKind.Root, null));
            }

            foreach (var construction in heap.Constructions.OrderBy(construction => heap.Regions[construction.RegionId].Kind != HeapRegionKind.Receiver)
                                                           .ThenBy(construction => construction.RegionId, StringComparer.Ordinal))
            {
                foreach (var execution in _regionExecutions.GetValueOrDefault(construction.RegionId) ?? [])
                {
                    foreach (var instance in construction.ConstructorInstances)
                    {
                        Walk(execution, instance, [construction.RegionId]);
                        Entry(execution, new ExecutionEntry(instance, ExecutionEntryKind.Construction, construction.RegionId));
                    }
                }
            }

            foreach (var initializer in heap.TypeInitializers.Where(initializer => heap.Instances.ContainsKey(initializer.InstanceId)))
            {
                var execution = startupTypeInitializers.Contains(initializer.TypeKey)
                    ? STARTUP
                    : Add(new ExecutionInstance($"type-initializer:{initializer.TypeKey}", ExecutionKind.TypeInitializer,
                                                $"type initializer of {WholeProgram.DisplayType(initializer.TypeKey)}", AT_MOST_ONCE, null,
                                                initializer.TypeKey));
                Walk(execution, initializer.InstanceId, [$"static:{initializer.TypeKey}"]);
                Entry(execution, new ExecutionEntry(initializer.InstanceId, ExecutionEntryKind.TypeInitializer, $"static:{initializer.TypeKey}"));
            }

            var published = Published();
            var (accesses, startupAccesses) = Collect(published);
            var ownership = Ownership(accesses);
            return new ExecutionAnalysis(
                _executions.Values.OrderBy(execution => execution.Id, StringComparer.Ordinal).ToArray(),
                accesses,
                ownership,
                new Dictionary<string, int>(StringComparer.Ordinal) { [ExecutionCounters.STARTUP_CONSTRUCTION_ACCESS] = startupAccesses },
                SingleObjects(),
                published,
                _instanceExecutions.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
                _entries.Where(pair => pair.Key != STARTUP)
                        .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExecutionEntry>)pair.Value, StringComparer.Ordinal));
        }

        private void Entry(string execution, ExecutionEntry entry)
        {
            if (!_entries.TryGetValue(execution, out var entries))
                _entries.Add(execution, entries = []);
            entries.Add(entry);
        }

        private static readonly InvocationPolicy AT_MOST_ONCE = new(Multiplicity.AtMostOnce, SelfOverlap.Serialized, "");

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
                children[construction.RegionId] = construction.ConstructorInstances
                    .SelectMany(instance => heap.Instances[instance].Parameters.Values.SelectMany(values => values))
                    .Where(region => heap.Regions[region].Kind == HeapRegionKind.Di)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var startup = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(heap.Regions.Values.Where(IsHosted).Select(region => region.Identity).Concat(heap.StartupRegions));
            while (pending.TryPop(out var region))
            {
                if (!startup.Add(region))
                    continue;
                foreach (var child in children.GetValueOrDefault(region) ?? [])
                    pending.Push(child);
            }

            foreach (var region in heap.Regions.Values.Where(region => region.Kind is HeapRegionKind.Di or HeapRegionKind.Receiver or HeapRegionKind.Container))
            {
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
                foreach (var region in heap.Instances[instanceId].Parameters.Values.SelectMany(values => values))
                {
                    if (heap.Regions[region] is { Kind: HeapRegionKind.Di } di && !IsContainerWide(di) && !startup.Contains(region))
                        Executions(region).Add(RootExecution(rootId));
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (parent, childRegions) in children)
                {
                    foreach (var child in childRegions.Where(child => !IsContainerWide(heap.Regions[child]) && !startup.Contains(child)))
                    {
                        foreach (var execution in Executions(parent).ToArray())
                            changed |= Executions(child).Add(execution);
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
                Reach(construction.ConstructorInstances, instances);

            var startupTypeInitializers = new HashSet<string>(StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var initializer in heap.TypeInitializers)
                {
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
            var pending = new Stack<string>(starts);
            while (pending.TryPop(out var instance))
            {
                if (!reached.Add(instance))
                    continue;
                foreach (var edge in _edges.GetValueOrDefault(instance) ?? [])
                    pending.Push(edge.CalleeInstance);
            }
        }

        /// <summary>Visits every instance an execution reaches from one entry, with the objects whose constructor chain is running: a
        /// constructor call on a new allocation starts that object's interval for everything the call reaches.</summary>
        private void Walk(string execution, string entry, HashSet<string> intervals)
        {
            var pending = new Stack<(string Instance, HashSet<string> Intervals)>([(entry, intervals)]);
            while (pending.TryPop(out var item))
            {
                var key = string.Join(",", item.Intervals.Order(StringComparer.Ordinal));
                if (!heap.Instances.TryGetValue(item.Instance, out var instance) || !_visits.Add((execution, item.Instance, key)))
                    continue;

                _visitList.Add((execution, item.Instance, item.Intervals));
                if (!_instanceExecutions.TryGetValue(item.Instance, out var executions))
                    _instanceExecutions.Add(item.Instance, executions = new HashSet<string>(StringComparer.Ordinal));
                executions.Add(execution);
                foreach (var @object in item.Intervals)
                {
                    if (!_chains.TryGetValue(@object, out var chain))
                        _chains.Add(@object, chain = new HashSet<string>(StringComparer.Ordinal));
                    chain.Add(item.Instance);
                }

                foreach (var edge in _edges.GetValueOrDefault(item.Instance) ?? [])
                {
                    var calleeIntervals = item.Intervals;
                    if (edge.Reason == WholeProgram.CONSTRUCTION_REASON && _constructed.TryGetValue(edge.CalleeInstance, out var constructed) &&
                        !item.Intervals.Contains(constructed))
                    {
                        calleeIntervals = [.. item.Intervals, constructed];
                    }
                    else if (instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } constructor)
                    {
                        var created = constructor.Receivers.SelectMany(value => heap.Resolve(instance.Id, value))
                                          .Where(region => heap.Regions[region].Kind == HeapRegionKind.Allocation && !item.Intervals.Contains(region))
                                          .ToArray();
                        if (created.Length != 0)
                            calleeIntervals = [.. item.Intervals, .. created];
                    }

                    pending.Push((edge.CalleeInstance, calleeIntervals));
                }
            }
        }

        /// <summary>The objects whose construction publishes them: an instance of the chain stores the object, or a delegate capturing
        /// it, into a region that is neither the object nor reachable from it, or returns it to a caller outside the chain.</summary>
        private HashSet<string> Published()
        {
            var published = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (@object, chain) in _chains)
            {
                if (!heap.Regions.TryGetValue(@object, out var region) || region.Kind == HeapRegionKind.Static)
                    continue;

                var inside = Closure([@object]);
                foreach (var instanceId in chain)
                {
                    var instance = heap.Instances[instanceId];
                    var summary = instance.Summary;
                    var stores = summary.Stores.Select(store => (Values: store.Values,
                                                                  Targets: store.Field.IsStatic
                                                                      ? new HashSet<string> { heap.StaticRegionOf(instanceId, store.Field) }
                                                                      : Resolve(instance, store.Bases)))
                                        .Concat(summary.Elements.Where(element => element.Kind == ElementOperationKind.Store)
                                                       .Select(element => (Values: element.Values, Targets: Resolve(instance, element.Arrays))));
                    foreach (var (values, targets) in stores)
                    {
                        var stored = Resolve(instance, values);
                        var exposes = stored.Contains(@object) ||
                                      stored.Any(value => heap.Regions[value].Kind == HeapRegionKind.Delegate && heap.DelegateCaptures(value).Contains(@object));
                        if (exposes && targets.Any(target => target != @object && !IsInternal(@object, inside, target)))
                        {
                            published.Add(@object);
                        }
                    }

                    if (summary.Returns.Any(@return => Resolve(instance, @return.Values).Contains(@object)) &&
                        heap.Edges.Any(edge => edge.CalleeInstance == instanceId && !chain.Contains(edge.CallerInstance)))
                    {
                        published.Add(@object);
                    }
                }
            }

            return published;
        }

        /// <summary>A target reachable only through the object under construction: an object or delegate reachable from it that
        /// shared storage does not also reach, or a container object the container resolved for it, whose context names it as the
        /// resolver. A singleton or a root-scope object is shared with everything else, so it is never internal.</summary>
        private bool IsInternal(string @object, IReadOnlySet<string> inside, string target) =>
            heap.Regions[target] is var region && !SharedReach(@object).Contains(target) &&
            (inside.Contains(target) && region.Kind is HeapRegionKind.Allocation or HeapRegionKind.Delegate ||
             region.Kind == HeapRegionKind.Di && region.Context.StartsWith($"{@object}|", StringComparison.Ordinal));

        /// <summary>Every region shared storage reaches: static storage, singletons and root-scope objects, and what they hold. The
        /// object under construction is left out as a root, since what it holds is what this asks about.</summary>
        private HashSet<string> SharedReach(string @object) =>
            _sharedReach.TryGetValue(@object, out var reach)
                ? reach
                : _sharedReach[@object] = Closure(SharedRoots().Where(root => root != @object));

        private IEnumerable<string> SharedRoots() =>
            heap.Regions.Values.Where(region => !region.IsMerged && (region.Kind == HeapRegionKind.Static || IsContainerWide(region)))
                .Select(region => region.Identity);

        private HashSet<string> Resolve(MethodInstance instance, IEnumerable<AbstractValue> values) =>
            values.SelectMany(value => heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);

        /// <summary>Every region reachable from the starts through fields, array elements and delegate captures.</summary>
        private HashSet<string> Closure(IEnumerable<string> starts)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(starts);
            while (pending.TryPop(out var region))
            {
                foreach (var next in Successors(region))
                {
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

        private (IReadOnlyList<CollectedAccess> Accesses, int StartupAccesses) Collect(IReadOnlySet<string> published)
        {
            var accesses = new List<CollectedAccess>();
            var seen = new HashSet<(string, string, int, string, bool)>();
            var startup = new HashSet<(string, int, string)>();
            foreach (var (execution, instanceId, intervals) in _visitList)
            {
                var instance = heap.Instances[instanceId];
                foreach (var access in instance.Summary.Accesses)
                {
                    var regions = access.Field.IsStatic
                        ? new HashSet<string> { heap.StaticRegionOf(instanceId, access.Field) }
                        : Resolve(instance, access.Bases);
                    foreach (var region in regions)
                    {
                        if (execution == STARTUP)
                        {
                            // One source operation counts once per scope, whatever context or call site instantiated it.
                            startup.Add((instance.BodyId, access.OperationId, region));
                            continue;
                        }

                        var local = intervals.Contains(region) && !published.Contains(region);
                        if (seen.Add((execution, instanceId, access.OperationId, region, local)))
                            accesses.Add(new CollectedAccess(execution, instanceId, access, region, local));
                    }
                }
            }

            return (accesses, startup.Count);
        }

        private Dictionary<string, RegionOwnership> Ownership(IReadOnlyList<CollectedAccess> accesses)
        {
            var accessExecutions = accesses.GroupBy(access => access.RegionId, StringComparer.Ordinal)
                                           .ToDictionary(group => group.Key, group => group.Select(access => access.ExecutionId).ToHashSet(StringComparer.Ordinal),
                                                         StringComparer.Ordinal);

            var sharedRoots = SharedRoots().ToArray();
            // Each escaped region keeps the chain of hops from the shared root it is reached through, outermost first.
            var escapes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var pending = new Queue<string>(sharedRoots);
            var visited = new HashSet<string>(sharedRoots, StringComparer.Ordinal);
            while (pending.TryDequeue(out var region))
            {
                foreach (var (field, target) in Edges(region))
                {
                    if (!visited.Add(target))
                        continue;
                    escapes[target] = [.. escapes.GetValueOrDefault(region) ?? [], EscapeEvidence(accesses, region, field, target)];
                    pending.Enqueue(target);
                }
            }

            var ownership = new Dictionary<string, RegionOwnership>(StringComparer.Ordinal);
            foreach (var region in heap.Regions.Values)
            {
                var reached = accessExecutions.GetValueOrDefault(region.Identity);
                ownership[region.Identity] = region switch
                {
                    { IsMerged: true } => new RegionOwnership(OwnershipKind.Unknown, [$"{region.Display} has a merged context."]),
                    { Kind: HeapRegionKind.Static } => new RegionOwnership(OwnershipKind.Shared, [$"{region.Display} is static storage."]),
                    _ when IsContainerWide(region) =>
                        new RegionOwnership(OwnershipKind.Shared, [$"{region.Display} is one container object for the whole scope ({region.Context})."]),
                    _ when escapes.TryGetValue(region.Identity, out var chain) => new RegionOwnership(OwnershipKind.Escaped, chain),
                    _ when reached is not null && reached.Except(CreationExecutions(region)).FirstOrDefault() is { } other =>
                        new RegionOwnership(OwnershipKind.Escaped,
                                            [$"{region.Display} is created in {Describe(CreationExecutions(region))} and reached from {Describe([other])}."]),
                    _ when reached is not null => new RegionOwnership(OwnershipKind.ThreadConfined, [$"{region.Display} is reached only in {Describe(reached)}."]),
                    _ => new RegionOwnership(OwnershipKind.Owned, [$"No access reaches {region.Display}."])
                };
            }

            return ownership;
        }

        /// <summary>One hop of an escape chain, with the source location that made it: the delegate creation of a capture, or the
        /// store that put the target there.</summary>
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
        private Analysis.SourceSpan? StoreSpan(IReadOnlyList<CollectedAccess> accesses, string source, string field, string target)
        {
            var collected = accesses.FirstOrDefault(access => access.Access.Kind == SummaryAccessKind.Store && access.RegionId == source &&
                                                              FieldSlot.Key(access.Access.Field) == field &&
                                                              Resolve(heap.Instances[access.InstanceId], access.Access.Values).Contains(target));
            if (collected is not null)
                return collected.Access.Provenance.Span;

            foreach (var instance in heap.Instances.Values)
            {
                var stores = instance.Summary.Stores
                                     .Where(store => FieldSlot.Key(store.Field) == field && Resolve(instance, store.Values).Contains(target) &&
                                                     Targets(instance, store).Contains(source))
                                     .Select(store => store.OperationId)
                                     .Concat(instance.Summary.Elements
                                                     .Where(element => field == PathValue.ELEMENT && element.Kind == ElementOperationKind.Store &&
                                                                       Resolve(instance, element.Values).Contains(target) &&
                                                                       Resolve(instance, element.Arrays).Contains(source))
                                                     .Select(element => element.OperationId));
                if (stores.Select(operation => Span(instance.BodyId, operation)).OfType<Analysis.SourceSpan>().FirstOrDefault() is { } span)
                    return span;
            }

            return null;
        }

        private HashSet<string> Targets(MethodInstance instance, StoreTransfer store) =>
            store.Field.IsStatic
                ? [heap.StaticRegionOf(instance.Id, store.Field)]
                : Resolve(instance, store.Bases);

        private Analysis.SourceSpan? Span(string? bodyId, int? operationId) =>
            bodyId is not null && operationId is { } operation && scope.Reachable.Bodies.TryGetValue(bodyId, out var body)
                ? body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(item => item.Id == operation)?.Provenance.Span
                : null;

        private string Describe(IEnumerable<string> executions) =>
            string.Join(", ", executions.Order(StringComparer.Ordinal)
                                        .Select(id => _executions.TryGetValue(id, out var execution) ? execution.Display : id));

        private HashSet<string> CreationExecutions(HeapRegion region) => region.Kind switch
        {
            HeapRegionKind.Allocation or HeapRegionKind.Delegate =>
                heap.Instances.Values.Where(instance => instance.BodyId == region.SiteBodyId && instance.Context == region.Context)
                    .SelectMany(instance => _instanceExecutions.GetValueOrDefault(instance.Id) ?? [])
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
                var definition = scope.Program.Decompose(region.TypeKey!).DefinitionKey;
                foreach (var field in heap.FieldsOf(region.Identity))
                {
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
                if (resolved && !region.MayOverlapItself)
                    single.Add(region.Identity);
            }

            var once = OnceInstances();
            foreach (var region in heap.Regions.Values.Where(region => region is { Kind: HeapRegionKind.Allocation, IsMerged: false, SiteBodyId: not null }))
            {
                var creators = heap.Instances.Values.Where(instance => instance.BodyId == region.SiteBodyId && instance.Context == region.Context).ToArray();
                if (creators.Length != 0 && creators.All(instance => once.Contains(instance.Id)) &&
                    scope.Reachable.Bodies.TryGetValue(region.SiteBodyId!, out var body) && !InCycle(body, region.SiteOperationId!.Value))
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
                var executions = _regionExecutions.GetValueOrDefault(construction.RegionId) ?? [];
                if (!executions.Contains(STARTUP) && !executions.Any(id => id.StartsWith("construction:", StringComparison.Ordinal)))
                    continue;

                var pending = new Stack<string>(construction.ConstructorInstances);
                while (pending.TryPop(out var instanceId))
                {
                    if (!once.Add(instanceId))
                        continue;
                    foreach (var edge in _edges.GetValueOrDefault(instanceId) ?? [])
                    {
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
                if (heap.RootInstances.TryGetValue(root.StableRootId, out var instance))
                    once.Add(instance);
            }

            return once;
        }

        private static bool InCycle(IrBody body, int operationId)
        {
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
                if (current == block.Ordinal)
                    return true;
                if (!seen.Add(current))
                    continue;
                foreach (var next in successors.GetValueOrDefault(current) ?? [])
                    pending.Push(next);
            }

            return false;
        }
    }
}
