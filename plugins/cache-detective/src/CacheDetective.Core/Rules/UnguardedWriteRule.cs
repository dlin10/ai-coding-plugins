using CacheDetective.Graph;

namespace CacheDetective.Rules;

public sealed record UnguardedWriteFinding(Writes Write, Table Table, CacheKey Key,
                                          Confidence Confidence, IReadOnlyList<GraphEdge> Chain,
                                          double? TtlSeconds, double BudgetSeconds, bool Suppressed)
{
    public const string Rule = "UNGUARDED_WRITE";
}

public sealed class UnguardedWriteRule
{
    public IReadOnlyList<UnguardedWriteFinding> Evaluate(
        CacheGraph graph, IReadOnlyDictionary<string, double>? budgets = null)
    {
        var edges = graph.Edges.ToArray();
        var findings = new List<UnguardedWriteFinding>();
        var cacheKeys = graph.CacheKeys.Where(key => key.Role == "cache").ToArray();

        foreach (var write in edges.OfType<Writes>())
        {
            var table = (Table)write.To;
            var invalidationSearch = FindReachableHandlers((Handler)write.From, edges);

            foreach (var key in cacheKeys)
            {
                var dependencies = graph.DependsOn(key)
                    .Where(dependency => dependency.Target is Table dependencyTable &&
                                         dependencyTable.Name == table.Name)
                    .ToArray();
                if (dependencies.Length == 0 || HasCoveringInvalidation(key, invalidationSearch.Handlers, edges))
                    continue;

                var budgetSeconds = StalenessBudget.GetSeconds(table.Name, budgets);
                var ttlSeconds = key.TtlSeconds;
                var suppressed = ttlSeconds is not null && ttlSeconds.Value <= budgetSeconds;
                var dependency = dependencies.OrderBy(candidate => candidate.Confidence)
                    .ThenBy(candidate => candidate.Path.Count)
                    .First();
                var chain = new GraphEdge[dependency.Path.Count + 1];
                chain[0] = write;
                for (var index = 0; index < dependency.Path.Count; index++)
                    chain[index + 1] = dependency.Path[dependency.Path.Count - index - 1];

                var confidence = Weaken(write.Confidence, dependency.Confidence);
                var chainHandlers = GetHandlers(chain).Concat(invalidationSearch.Handlers.Values);
                if (graph.GetUnresolvedForHandlers(chainHandlers, UnresolvedKind.Call,
                        UnresolvedKind.Sql).Count > 0)
                {
                    confidence = Confidence.Unknown;
                }

                findings.Add(new UnguardedWriteFinding(write, table, key, confidence, chain,
                    ttlSeconds, budgetSeconds, suppressed));
            }
        }

        return findings;
    }

    private static bool HasCoveringInvalidation(
        CacheKey key, IReadOnlyDictionary<(string Solution, string Symbol), Handler> reachableHandlers,
        IEnumerable<GraphEdge> edges) =>
        edges.OfType<Invalidates>().Any(invalidation =>
            reachableHandlers.ContainsKey(GetHandlerId((Handler)invalidation.From)) &&
            CacheKeyCovering.Covers(invalidation, key, key.TagsAll));

    private static Reachability FindReachableHandlers(Handler start, IReadOnlyList<GraphEdge> edges)
    {
        var calls = edges.OfType<Calls>()
            .GroupBy(call => GetHandlerId((Handler)call.From))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var handlers = new Dictionary<(string Solution, string Symbol), Handler>();
        var pending = new Queue<Handler>();
        pending.Enqueue(start);

        while (pending.TryDequeue(out var handler))
        {
            var id = GetHandlerId(handler);
            if (!handlers.TryAdd(id, handler))
                continue;

            if (!calls.TryGetValue(id, out var outgoing))
                continue;

            foreach (var call in outgoing)
                pending.Enqueue((Handler)call.To);
        }

        return new Reachability(handlers);
    }

    private static IEnumerable<Handler> GetHandlers(IEnumerable<GraphEdge> chain)
    {
        foreach (var edge in chain)
        {
            yield return (Handler)edge.From;
            if (edge is Calls call)
                yield return (Handler)call.To;
        }
    }

    private static Confidence Weaken(Confidence current, Confidence candidate) =>
        (Confidence)Math.Max((int)current, (int)candidate);

    private static (string Solution, string Symbol) GetHandlerId(Handler handler) =>
        (handler.Solution, handler.Symbol);

    private sealed record Reachability(
        IReadOnlyDictionary<(string Solution, string Symbol), Handler> Handlers);
}
