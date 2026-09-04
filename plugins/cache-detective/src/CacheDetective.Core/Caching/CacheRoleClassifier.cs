using CacheDetective.Graph;

namespace CacheDetective.Caching;

internal sealed class CacheRoleClassifier
{
    private static readonly string[] STORE_PREFIXES =
    [
        "session:",
        "lock:",
        "idempotency:",
        "ratelimit:",
        "token:"
    ];

    public void Classify(CacheGraph graph, string solutionName)
    {
        var keys = graph.CacheKeys.ToArray();
        var edges = graph.Edges.ToArray();
        var operations = graph.CacheOperations.ToArray();

        foreach (var key in keys)
        {
            if (HasStoreSignal(key, operations, edges))
            {
                graph.SetCacheKeyRole(key.Template, key.Store, "store");
                continue;
            }

            var cachingHandlers = edges.OfType<Caches>().Where(edge => IsKey(edge.To, key)).Select(edge => (Handler)edge.From);
            var reachableHandlers = FindReachableHandlers(cachingHandlers, edges);
            var hasReachableRead = HasReachableDataAccess(edges, reachableHandlers);

            if (hasReachableRead)
            {
                graph.SetCacheKeyRole(key.Template, key.Store, "cache");
                continue;
            }

            var blockers = graph.GetUnresolvedForHandlers(reachableHandlers.Values, UnresolvedKind.Call, UnresolvedKind.Sql);
            if (blockers.Count == 0)
            {
                graph.SetCacheKeyRole(key.Template, key.Store, "store");
                continue;
            }

            graph.SetCacheKeyRole(key.Template, key.Store, "unknown");
            var blocker = blockers[0];
            var reasons =
                string.Join("; ", blockers.Select(item => $"{item.Kind.ToString().ToLowerInvariant()}: {item.Reason}").Distinct(StringComparer.Ordinal));
            graph.AddUnresolved(UnresolvedKind.Role, solutionName, blocker.Site, key.Template,
                                $"Role classification was blocked by incomplete analysis: {reasons}");
        }
    }

    private static bool HasStoreSignal(CacheKey key, IEnumerable<CacheOperation> operations, IEnumerable<GraphEdge> edges)
    {
        if (STORE_PREFIXES.Any(prefix => key.Template.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (operations.Any(operation => IsKey(operation.Key, key) &&
                                        (operation.Semantic is CacheSemantic.Increment or CacheSemantic.Expire or CacheSemantic.Lock ||
                                         operation.IsConditionalSet)))
        {
            return true;
        }

        return edges.OfType<Caches>().Any(edge => IsKey(edge.To, key) && edge.IsConditionalSet);
    }

    /// <summary>A reachable handler touches data when it reads a table, or when it calls a stored
    /// procedure: calling a procedure is reaching for data whether or not the database is indexed, which
    /// is why the role does not have to be recomputed after <c>index_database</c>.</summary>
    private static bool HasReachableDataAccess(IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<(string Solution, string Symbol), Handler> reachableHandlers)
    {
        return edges.OfType<Reads>().Any(edge => IsReachable(edge.From)) ||
               edges.OfType<Calls>().Any(edge => edge.To is StoredProcedure && IsReachable(edge.From));

        bool IsReachable(GraphVertex from) =>
            from is Handler handler && reachableHandlers.ContainsKey(GetHandlerId(handler));
    }

    private static Dictionary<(string Solution, string Symbol), Handler> FindReachableHandlers(IEnumerable<Handler> startingHandlers,
                                                                                               IEnumerable<GraphEdge> edges)
    {
        var calls = edges.OfType<Calls>()
                         .Where(edge => edge.From is Handler && edge.To is Handler)
                         .GroupBy(edge => GetHandlerId((Handler)edge.From))
                         .ToDictionary(group => group.Key, group => group.Select(edge => (Handler)edge.To).ToArray());
        var reachable = new Dictionary<(string Solution, string Symbol), Handler>();
        var pending = new Queue<Handler>(startingHandlers);

        while (pending.TryDequeue(out var handler))
        {
            var id = GetHandlerId(handler);
            if (!reachable.TryAdd(id, handler) || !calls.TryGetValue(id, out var targets))
                continue;

            foreach (var target in targets)
                pending.Enqueue(target);
        }

        return reachable;
    }

    private static bool IsKey(GraphVertex candidate, CacheKey key) =>
        candidate is CacheKey candidateKey && candidateKey.Template == key.Template && candidateKey.Store == key.Store;

    private static (string Solution, string Symbol) GetHandlerId(Handler handler) => (handler.Solution, handler.Symbol);
}
