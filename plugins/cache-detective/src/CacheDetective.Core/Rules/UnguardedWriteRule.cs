using CacheDetective.Graph;

namespace CacheDetective.Rules;

/// <summary><see cref="Handler"/> is the subject: the handler at the head of the chain, named by the rule
/// rather than inferred from <see cref="Write"/>, whose source is a procedure or a trigger when the write
/// is hidden.</summary>
public sealed record UnguardedWriteFinding(Handler Handler, Writes Write, Table Table, CacheKey Key,
                                          Confidence Confidence, IReadOnlyList<GraphEdge> Chain,
                                          double? TtlSeconds, double BudgetSeconds, bool Suppressed)
{
    public const string Rule = "UNGUARDED_WRITE";
}

public sealed class UnguardedWriteRule
{
    /// <summary>The depth of the code call graph, so every walk in the project stops the same way.</summary>
    private const int MAXIMUM_DEPTH = 12;

    public IReadOnlyList<UnguardedWriteFinding> Evaluate(CacheGraph graph, IReadOnlyDictionary<string, double>? budgets = null)
    {
        var edges = graph.Edges.ToArray();
        var findings = new List<UnguardedWriteFinding>();
        var cacheKeys = graph.CacheKeys.Where(key => key.Role == "cache").ToArray();
        var dependencies = cacheKeys.ToDictionary(key => (key.Template, key.Store), key => graph.DependsOn(key).ToArray());
        // A procedure whose dependencies the graph does not know weakens every finding whose chain runs
        // through the handler that calls it, exactly as a stored unresolved of kind Sql does — which is
        // why the derived rows carry that kind. Derived here, at the point of use, and never stored: see
        // ProcedureGaps.
        var gapHandlers = ProcedureGaps.Derive(graph).Select(gap => gap.Caller).OfType<Handler>().Select(GetHandlerId).ToHashSet();

        foreach (var write in edges.OfType<Writes>())
        {
            var table = (Table)write.To;

            // A procedure the indexed code never calls, and a trigger no reachable write fires, have no
            // handler at the head of their chain and produce no finding at all: there is nobody to fix it.
            foreach (var head in FindHeads(write, edges))
            {
                // The anchor. Both the search for a covering invalidation and the handler set that lowers
                // confidence hang off the handler at the head of the chain, never off the writer: the
                // writer of a hidden write is a procedure or a trigger, and anchoring there would turn the
                // commonest correct pattern — a handler calls a procedure that writes, and invalidates the
                // key itself — into a false finding.
                var invalidationSearch = FindReachableHandlers(head.Handler, edges);

                foreach (var key in cacheKeys)
                {
                    var matches = dependencies[(key.Template, key.Store)]
                                 .Where(dependency => dependency.Target is Table dependencyTable && dependencyTable.Name == table.Name)
                                 .ToArray();
                    if (matches.Length == 0 || HasCoveringInvalidation(key, invalidationSearch.Handlers, edges))
                    {
                        continue;
                    }

                    var budgetSeconds = StalenessBudget.GetSeconds(table.Name, budgets);
                    var ttlSeconds = key.TtlSeconds;
                    var suppressed = ttlSeconds is not null && ttlSeconds.Value <= budgetSeconds;
                    var dependency = matches.OrderBy(candidate => candidate.Confidence).ThenBy(candidate => candidate.Path.Count).First();
                    var chain = BuildChain(head.Path, write, dependency.Path);

                    var confidence = Weaken(write.Confidence, dependency.Confidence);
                    var chainHandlers = GetHandlers(chain).Concat(invalidationSearch.Handlers.Values).ToArray();
                    if (graph.GetUnresolvedForHandlers(chainHandlers, UnresolvedKind.Call, UnresolvedKind.Sql).Count > 0 ||
                        chainHandlers.Any(handler => gapHandlers.Contains(GetHandlerId(handler))))
                    {
                        confidence = Confidence.Unknown;
                    }

                    findings.Add(new UnguardedWriteFinding(head.Handler, write, table, key, confidence, chain, ttlSeconds, budgetSeconds, suppressed));
                }
            }
        }

        return findings;
    }

    /// <summary>The chain reads top to bottom: how the handler reached the write, the write itself, then
    /// the path from the table up to the key.</summary>
    private static GraphEdge[] BuildChain(IReadOnlyList<GraphEdge> head, Writes write, IReadOnlyList<GraphEdge> dependency)
    {
        var chain = new GraphEdge[head.Count + 1 + dependency.Count];
        for (var index = 0; index < head.Count; index++)
            chain[index] = head[index];
        chain[head.Count] = write;
        for (var index = 0; index < dependency.Count; index++)
            chain[head.Count + 1 + index] = dependency[dependency.Count - index - 1];
        return chain;
    }

    /// <summary>The handlers at the head of the chains that reach one write, with the path from each. A
    /// handler's own write has an empty path; a procedure's write is reached through the calls into it; a
    /// trigger's write is reached through a write to its table that its events answer.</summary>
    private static IReadOnlyList<WriteChain> FindHeads(Writes write, IReadOnlyList<GraphEdge> edges) =>
        FindHeads(write, edges, 0, new HashSet<Writes>(ReferenceEqualityComparer.Instance));

    private static IReadOnlyList<WriteChain> FindHeads(Writes write, IReadOnlyList<GraphEdge> edges, int depth, HashSet<Writes> active)
    {
        if (depth > MAXIMUM_DEPTH || !active.Add(write))
        {
            return [];
        }

        try
        {
            switch (write.From)
            {
                case Handler handler:
                    return [new WriteChain(handler, [])];
                case StoredProcedure procedure:
                    return FindCallers(procedure, edges);
                case Trigger trigger:
                    var chains = new List<WriteChain>();
                    foreach (var (fired, fires) in FindFiringWrites(trigger, edges))
                    {
                        foreach (var chain in FindHeads(fired, edges, depth + 1, active))
                        {
                            chains.Add(new WriteChain(chain.Handler, [.. chain.Path, fired, fires]));
                        }
                    }

                    return Shortest(chains);
                default:
                    return [];
            }
        }
        finally
        {
            active.Remove(write);
        }
    }

    /// <summary>Walks the calls into a procedure backwards to the nearest handler above it, which is the
    /// handler that would carry the invalidation — as in phase 1, where a write was attributed to the
    /// handler performing it and not to its callers.</summary>
    private static IReadOnlyList<WriteChain> FindCallers(StoredProcedure procedure, IReadOnlyList<GraphEdge> edges)
    {
        var chains = new List<WriteChain>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { procedure.Name };
        var pending = new Queue<(GraphVertex Target, IReadOnlyList<GraphEdge> Path, int Depth)>();
        pending.Enqueue((procedure, [], 0));

        while (pending.TryDequeue(out var current))
        {
            foreach (var call in edges.OfType<Calls>().Where(edge => IsSame(edge.To, current.Target)))
            {
                IReadOnlyList<GraphEdge> path = [call, .. current.Path];
                if (call.From is Handler handler)
                {
                    chains.Add(new WriteChain(handler, path));
                }
                else if (call.From is StoredProcedure caller && current.Depth < MAXIMUM_DEPTH && visited.Add(caller.Name))
                {
                    pending.Enqueue((caller, path, current.Depth + 1));
                }
            }
        }

        return Shortest(chains);
    }

    private static IReadOnlyList<WriteChain> Shortest(IReadOnlyList<WriteChain> chains) =>
        chains.GroupBy(chain => (chain.Handler.Solution, chain.Handler.Symbol)).Select(group => group.OrderBy(chain => chain.Path.Count).First()).ToArray();

    private static IEnumerable<(Writes Write, Fires Fires)> FindFiringWrites(Trigger trigger, IReadOnlyList<GraphEdge> edges)
    {
        var fires = edges.OfType<Fires>().FirstOrDefault(edge => edge.To is Trigger candidate && candidate.Name == trigger.Name);
        if (fires is null)
        {
            yield break;
        }

        foreach (var write in edges.OfType<Writes>())
        {
            if (((Table)write.To).Name == trigger.Table && Activates(write.Events, trigger.Events))
            {
                yield return (write, fires);
            }
        }
    }

    /// <summary>A trigger joins a chain only where the write's events meet its own: a trigger declared
    /// <c>FOR DELETE</c> is not reached by an insert, and <c>TRUNCATE</c> reaches no trigger at all. A
    /// write whose events could not be inferred is taken to match every trigger.</summary>
    private static bool Activates(IReadOnlySet<WriteEvent> writeEvents, IReadOnlySet<WriteEvent> triggerEvents) =>
        writeEvents.Count == 0 || writeEvents.Any(triggerEvents.Contains);

    private static bool HasCoveringInvalidation(CacheKey key, IReadOnlyDictionary<(string Solution, string Symbol), Handler> reachableHandlers,
                                                IEnumerable<GraphEdge> edges) =>
        edges.OfType<Invalidates>()
             .Any(invalidation => reachableHandlers.ContainsKey(GetHandlerId((Handler)invalidation.From)) &&
                                  CacheKeyCovering.Covers(invalidation, key, key.TagsAll));

    private static Reachability FindReachableHandlers(Handler start, IReadOnlyList<GraphEdge> edges)
    {
        var calls = edges.OfType<Calls>()
                         .Where(call => call.From is Handler && call.To is Handler)
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
            if (edge.From is Handler from)
                yield return from;

            if (edge is Calls { To: Handler to })
                yield return to;
        }
    }

    private static bool IsSame(GraphVertex left, GraphVertex right) => (left, right) switch
                                                                       {
                                                                           (StoredProcedure first, StoredProcedure second) => first.Name == second.Name,
                                                                           (Handler first, Handler second) => first.Solution == second.Solution &&
                                                                               first.Symbol == second.Symbol,
                                                                           _ => false
                                                                       };

    private static Confidence Weaken(Confidence current, Confidence candidate) => (Confidence)Math.Max((int)current, (int)candidate);

    private static (string Solution, string Symbol) GetHandlerId(Handler handler) => (handler.Solution, handler.Symbol);

    private sealed record WriteChain(Handler Handler, IReadOnlyList<GraphEdge> Path);

    private sealed record Reachability(IReadOnlyDictionary<(string Solution, string Symbol), Handler> Handlers);
}
