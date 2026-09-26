using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>What kind of call a semantic gap is (TD-034): its callee in <c>System.Reflection</c> or <c>System.Activator</c>, an operation
/// on a <c>dynamic</c> value, a dispatch without a receiver object, a locator the DI model could not resolve, or any other opaque call.</summary>
public static class SemanticGapKinds
{
    public const string REFLECTION = "reflection";
    public const string DYNAMIC = "dynamic";
    public const string UNRESOLVED_DISPATCH = "unresolved-dispatch";
    public const string MODEL_GAP = "model-gap";
    public const string UNKNOWN_LIBRARY = "unknown-library";
}

/// <summary>One semantic gap of a process scope: a callee the analysis could not reduce, with its materiality (TD-039a) — the roots whose
/// executions reach its call sites, the regions its unknown effects and delegates reach, and the call sites — and those sites.</summary>
public sealed record SemanticGap(string Callee, string Kind, int Roots, int Regions, int CallSites)
{
    public IReadOnlyList<OperationSite> Sites { get; init; } = [];
}

/// <summary>
/// The semantic gaps of one scope (R4). An unresolved call — an opaque call the library table does not describe, a dispatch with no
/// receiver object, an operation on a <c>dynamic</c> value, a locator nothing resolves — is a gap where what it may touch is not owned:
/// its unknown effect reaches a mutable region, it is handed a delegate, or its result is written into a field or a cell of a region
/// that is not owned. A call a recognizer of phases 1-4 models is not unresolved. Only the calls of bodies the heap reached count, and
/// one callee is one gap.
/// </summary>
public static class SemanticGaps
{
    /// <summary>The scope's gaps. Which calls are gaps is known before the accesses are collected; what the unknown executions of
    /// their delegates touch, deep reads and unknown effects included, is known only after, so <paramref name="accesses"/>, once
    /// collected, count those regions (R4).</summary>
    public static IReadOnlyList<SemanticGap> Find(InterproceduralInput input, IReadOnlyList<Access>? accesses = null)
    {
        var heap = input.Heap;
        var reach = new UnknownCalls.Reach(input.Scope, heap);
        var gaps = new Dictionary<string, (string Kind, HashSet<OperationSite> Sites, HashSet<string> Instances, HashSet<string> Regions)>(StringComparer.Ordinal);
        var results = heap.Instances.Values.ToDictionary(instance => instance.Id, instance => instance.Summary.ResultStores.ToLookup(store => store.SourceOperationId),
                                                         StringComparer.Ordinal);
        var executionRegions = (accesses?.Select(access => (access.ExecutionId, RegionId: access.Resource.CollectionId ?? access.Resource.RegionId!)) ??
                                input.Executions.Accesses.Select(access => (access.ExecutionId, access.RegionId)))
                               .GroupBy(access => access.ExecutionId, StringComparer.Ordinal)
                               .ToDictionary(group => group.Key, group => group.Select(access => access.RegionId).ToHashSet(StringComparer.Ordinal),
                                             StringComparer.Ordinal);
        foreach (var call in UnknownCalls.Of(input.Scope, heap))
        {
            // An object of one execution handed to the call is only read by it and is no gap; only what is shared counts (R2, R4). What
            // the unknown execution of a delegate it is handed touches is the call's reach too (R3).
            var (reached, takesDelegate) = reach.Of(call);
            var run = reach.Delegates(call).SelectMany(region => executionRegions.GetValueOrDefault(ExecutionModel.UnknownDelegateCallId(region)) ?? []);
            var regions = reached.Concat(run).Where(region => IsShared(input, region)).Distinct(StringComparer.Ordinal).ToArray();
            if (regions.Length == 0 && !takesDelegate && !FeedsShared(input, call, results[call.Instance.Id][call.OperationId]))
                continue;
            if (!gaps.TryGetValue(call.Callee, out var gap))
                gaps.Add(call.Callee, gap = (call.Kind, [], new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)));
            gap.Sites.Add(new OperationSite(call.Instance.BodyId, call.OperationId));
            gap.Instances.Add(call.Instance.Id);
            gap.Regions.UnionWith(regions);
        }

        var rootsReaching = RootsReaching(heap);
        return gaps.Select(pair => new SemanticGap(pair.Key, pair.Value.Kind,
                                                   pair.Value.Instances.SelectMany(instance => rootsReaching.GetValueOrDefault(instance) ?? [])
                                                       .Distinct(StringComparer.Ordinal).Count(),
                                                   pair.Value.Regions.Count, pair.Value.Sites.Count)
                   {
                       Sites = pair.Value.Sites.OrderBy(site => site.BodyId, StringComparer.Ordinal).ThenBy(site => site.OperationId).ToArray()
                   })
                   .OrderByDescending(gap => gap.Roots)
                   .ThenByDescending(gap => gap.Regions)
                   .ThenByDescending(gap => gap.CallSites)
                   .ThenBy(gap => gap.Callee, StringComparer.Ordinal)
                   .ToArray();
    }

    /// <summary>Whether the call writes its result into a shared region: one that is neither owned nor confined to one execution.</summary>
    private static bool FeedsShared(InterproceduralInput input, UnknownCall call, IEnumerable<SummaryResultStore> results) =>
        results.SelectMany(store => store.StaticField is { } field
                               ? [input.Heap.StaticRegionOf(call.Instance.Id, field)]
                               : store.Targets.SelectMany(value => input.Heap.Resolve(call.Instance.Id, value)))
               .Any(region => IsShared(input, region));

    private static bool IsShared(InterproceduralInput input, string region) =>
        input.Executions.Ownership.GetValueOrDefault(region)?.Kind is OwnershipKind.Escaped or OwnershipKind.Shared or OwnershipKind.Unknown;

    /// <summary>For every instance, the roots whose executions reach it: over the call edges, the constructions and type initializers
    /// they trigger, the work their spawns and timers start, and the delegates they hand to unresolved calls.</summary>
    private static Dictionary<string, HashSet<string>> RootsReaching(HeapSolution heap)
    {
        var next = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Edge(string from, string to)
        {
            if (!next.TryGetValue(from, out var targets))
                next.Add(from, targets = new HashSet<string>(StringComparer.Ordinal));
            targets.Add(to);
        }

        foreach (var edge in heap.ExecutionEdges)
            Edge(edge.CallerInstance, edge.CalleeInstance);
        var constructors = heap.Constructions.ToDictionary(construction => construction.RegionId, construction => construction.ConstructorInstances,
                                                           StringComparer.Ordinal);
        foreach (var trigger in heap.RegionTriggers)
        {
            foreach (var constructor in constructors.GetValueOrDefault(trigger.RegionId) ?? [])
                Edge(trigger.InstanceId, constructor);
        }

        foreach (var initializer in heap.TypeInitializers)
        {
            foreach (var trigger in initializer.TriggeringInstances)
                Edge(trigger, initializer.InstanceId);
        }

        foreach (var spawn in heap.Spawns)
        {
            foreach (var callee in spawn.Callees)
                Edge(spawn.CallerInstance, callee.InstanceId);
        }

        foreach (var timer in heap.TimerCallbacks)
        {
            foreach (var callee in timer.Callees)
                Edge(timer.CallerInstance, callee);
        }

        // The unknown call of a delegate runs where it was handed over as far as its roots go (R3, R4).
        foreach (var handoff in heap.DelegateHandoffs)
        {
            foreach (var site in handoff.Sites)
            {
                foreach (var callee in handoff.Callees)
                    Edge(site.CallerInstance, callee);
            }
        }

        var roots = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (rootId, entry) in heap.RootInstances)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { entry };
            var pending = new Queue<string>([entry]);
            while (pending.TryDequeue(out var instance))
            {
                if (!roots.TryGetValue(instance, out var reaching))
                    roots.Add(instance, reaching = new HashSet<string>(StringComparer.Ordinal));
                reaching.Add(rootId);
                foreach (var target in next.GetValueOrDefault(instance) ?? [])
                {
                    if (visited.Add(target))
                        pending.Enqueue(target);
                }
            }
        }

        return roots;
    }
}
