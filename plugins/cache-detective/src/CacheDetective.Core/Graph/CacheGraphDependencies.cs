using CacheDetective.Events;

namespace CacheDetective.Graph;

public sealed record KeyDependency(GraphVertex Target, Confidence Confidence,
                                   IReadOnlyList<GraphEdge> Path);

/// <summary>
/// What one walk found and how much of it it saw. <paramref name="Unresolved"/> holds the rows of every
/// branch the walk visited, including branches that produced no dependency at all — an unrecognised call
/// in a handler that reads nothing is exactly the case where the graph's silence means least.
/// <para><paramref name="DepthLimitReached"/> is the only kind of incompleteness there is, and it means a
/// source the walk never reached any other way: going round a cycle also spends the budget, and a source
/// the walk settled anyway was seen with more budget than the stop refused it, so nothing was missed.
/// </para>
/// </summary>
public sealed record KeyDependencyWalk(IReadOnlyList<KeyDependency> Dependencies,
                                       IReadOnlyList<Unresolved> Unresolved,
                                       bool DepthLimitReached);

public static class CacheGraphDependencies
{
    /// <summary>The depth of the code call graph, so every walk in the project stops the same way.</summary>
    private const int MAXIMUM_DEPTH = 12;

    /// <summary>Every kind but <see cref="UnresolvedKind.Role"/>: a role the classifier could not settle
    /// says nothing about what the key reads, and the role is gated on its own before a walk begins.</summary>
    private static readonly UnresolvedKind[] DEPENDENCY_KINDS =
        Enum.GetValues<UnresolvedKind>().Where(kind => kind != UnresolvedKind.Role).ToArray();

    /// <summary>One row per target, at the strongest confidence the graph reaches it by and the shortest
    /// path at that confidence — the row every caller of this selected for itself. See docs/adr/0018.
    /// </summary>
    public static IReadOnlyList<KeyDependency> DependsOn(this CacheGraph graph, CacheKey key)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.GetDependencies(key).Dependencies;
    }

    /// <summary>The same walk, with the account of how complete it was. Verification needs both, because
    /// what the walk could not see is what forbids it to refute anything.</summary>
    public static KeyDependencyWalk WalkDependencies(this CacheGraph graph, CacheKey key)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.GetDependencies(key);
    }

    internal static KeyDependencyWalk Build(CacheGraph graph, CacheKey key)
    {
        var adjacency = graph.GetAdjacency();
        var rootId = GetKeyId(key);
        var visited = new Dictionary<(string Solution, string Symbol), Handler>();
        var visitedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Where the budget ran out, and the strongest confidence each source was actually reached at.
        // Which of the two a stop was is not knowable when it happens — see IsIncomplete.
        var cuts = new HashSet<(string Source, Confidence Confidence)>();
        var settledAt = new Dictionary<string, Confidence>(StringComparer.Ordinal);

        // A search over states, not an enumeration of paths. A state is a source reached at a confidence
        // with a depth already spent, and the only thing worth remembering about one is the shortest way
        // to it: two paths arriving at the same source, at the same confidence, having spent the same
        // depth continue identically from there, so the shorter dominates the longer at every target
        // below it. That bounds the work at three confidences and thirteen depths per source, where
        // walking the paths themselves is an out-degree raised to the depth limit — a quarter of a
        // billion per handler on a real solution, which is where this stopped returning at all.
        var labels = new Dictionary<WalkState, Label>();
        var pending = new PriorityQueue<WalkState, int>();
        var best = new Dictionary<GraphVertex, Candidate>();

        foreach (var caches in adjacency.CachesInto(rootId))
            Relax(new WalkState((Handler)caches.From, caches.Confidence, 0), 1, null, [caches]);

        // Shortest off the queue first, so a state is settled the first time it is popped and the
        // predecessor chain a path is rebuilt from never moves under it afterwards.
        while (pending.TryDequeue(out var state, out var length))
        {
            if (labels[state].Length == length)
                Settle(state, length);
        }

        var dependencies = best.Select(entry => new KeyDependency(entry.Key, entry.Value.Confidence,
                                                                 BuildPath(entry.Value.From, entry.Value.Tail)))
                               .OrderBy(dependency => DependencyId(dependency.Target), StringComparer.Ordinal)
                               .ToArray();

        // Both halves of the walk, not just the code half. A procedure or view the walk descended into can
        // carry unresolved rows of its own — dynamic SQL, or dependencies this login may not read — and
        // those lie on a visited branch exactly as an unrecognised call in a handler does.
        var unresolved = graph.GetUnresolvedForHandlers(visited.Values, DEPENDENCY_KINDS)
                              .Concat(graph.GetUnresolvedForDatabaseObjects(visitedObjects, DEPENDENCY_KINDS))
                              .Concat(DerivedGaps(graph, visited, visitedObjects))
                              .DistinctBy(item => item.Id)
                              .OrderBy(item => item.Id)
                              .ToArray();
        return new KeyDependencyWalk(dependencies, unresolved, cuts.Any(IsIncomplete));

        // The two reasons a walk stops are not the same reason, and which one a stop was is only knowable
        // once the walk is over. Refusing to spend depth thirteen on a source the walk settled anyway —
        // which is what going round a cycle looks like without a path to test against — missed nothing:
        // that visit had strictly more budget than the refused one would have had, and its confidence was
        // no weaker, so everything below it was already seen. A source cut and never otherwise reached is
        // the real thing, and the only thing this reports.
        bool IsIncomplete((string Source, Confidence Confidence) cut) =>
            !settledAt.TryGetValue(cut.Source, out var reached) || reached > cut.Confidence;

        // Reads out of whatever can read: a handler, a procedure it calls, or a view it reads. A handler
        // that calls a procedure depends on what the procedure reads, and one that reads a view depends
        // on the view's tables.
        void Settle(WalkState state, int length)
        {
            // Every branch the walk enters, whether or not it comes back with a dependency: an
            // unrecognised call in a handler that reads nothing is the case the graph is quietest about.
            if (state.Source is Handler handler)
            {
                visited[(handler.Solution, handler.Symbol)] = handler;
            }
            else if (QualifiedName(state.Source) is { } databaseObject)
            {
                visitedObjects.Add(databaseObject);
            }

            var source = CacheGraphAdjacency.SourceId(state.Source);
            if (!settledAt.TryGetValue(source, out var strongest) || state.Confidence < strongest)
                settledAt[source] = state.Confidence;

            foreach (var read in adjacency.ReadsFrom(source))
            {
                var confidence = Weaken(state.Confidence, read.Confidence);
                switch (read.To)
                {
                    // A view of the same name displaces the table: neither half of the graph can tell a
                    // view from a table by name alone, so the read continues into the view's own reads.
                    // With no database indexed the name stays a plain table and the chain ends here,
                    // which is what phase 1 did.
                    case Table table when adjacency.Views.TryGetValue(table.Name, out var view):
                        Descend(state, view, confidence, length + 1, [read]);
                        break;
                    case Table table:
                        Offer(table, confidence, length + 1, state, [read]);
                        break;
                    case ExternalSource external when adjacency.ServesFrom(external) is { Count: > 0 } joins:
                        foreach (var serves in joins)
                            Descend(state, (Handler)serves.To, Weaken(confidence, serves.Confidence), length + 2, [read, serves]);
                        break;
                    case ExternalSource external:
                        Offer(external, confidence, length + 1, state, [read]);
                        break;
                    // A key never depends on itself, and the key the walk started from is the one case
                    // where following the reads would say otherwise.
                    case CacheKey dependency when GetKeyId(dependency) != rootId:
                        Offer(dependency, confidence, length + 1, state, [read]);
                        // What a key depends on is reached through the handlers that fill it, and the
                        // budget starts again there: the limit bounds a chain of calls, not one of keys.
                        foreach (var caches in adjacency.CachesInto(GetKeyId(dependency)))
                            Relax(new WalkState((Handler)caches.From, Weaken(confidence, caches.Confidence), 0),
                                  length + 2, state, [read, caches]);
                        break;
                }
            }

            foreach (var call in adjacency.CallsFrom(source))
            {
                if (call.To is ReadSource target)
                    Descend(state, target, Weaken(state.Confidence, call.Confidence), length + 1, [call]);
            }
        }

        void Descend(WalkState from, ReadSource target, Confidence confidence, int length, GraphEdge[] edges)
        {
            // A cycle needs no rule of its own any more: coming back to a source is never the shorter
            // arrival, so it improves no label and expands nothing, which is all that cutting a cycle
            // ever did. What it does still do is spend depth — a two-method cycle walks round until the
            // budget is gone — so the stop is recorded rather than judged here.
            if (from.Depth >= MAXIMUM_DEPTH)
            {
                cuts.Add((CacheGraphAdjacency.SourceId(target), confidence));
                return;
            }

            Relax(new WalkState(target, confidence, from.Depth + 1), length, from, edges);
        }

        void Relax(WalkState state, int length, WalkState? previous, GraphEdge[] edges)
        {
            if (labels.TryGetValue(state, out var known) && known.Length <= length)
                return;

            labels[state] = new Label(length, previous, edges);
            pending.Enqueue(state, length);
        }

        void Offer(GraphVertex target, Confidence confidence, int length, WalkState from, GraphEdge[] tail)
        {
            if (best.TryGetValue(target, out var known) &&
                (known.Confidence < confidence || known.Confidence == confidence && known.Length <= length))
            {
                return;
            }

            best[target] = new Candidate(confidence, length, from, tail);
        }

        IReadOnlyList<GraphEdge> BuildPath(WalkState from, GraphEdge[] tail)
        {
            var segments = new Stack<GraphEdge[]>();
            segments.Push(tail);
            for (WalkState? state = from; state is { } current;)
            {
                var label = labels[current];
                segments.Push(label.Edges);
                state = label.Previous;
            }

            return segments.SelectMany(edges => edges).ToArray();
        }
    }

    /// <summary>A source reached at a confidence with a depth already spent. Everything else the walk used
    /// to carry — the path, the sources on it, the keys on it — is either rebuilt from the labels or no
    /// longer needed: what a state reaches depends on where it stands and what budget it has left, and on
    /// nothing else about how it got there.</summary>
    private readonly record struct WalkState(ReadSource Source, Confidence Confidence, int Depth);

    /// <summary>The shortest way to a state: the edges of the hop that reached it, and the state before.
    /// Paths are rebuilt from these rather than carried, so the walk costs one array per hop taken rather
    /// than one per hop per path through it.</summary>
    private readonly record struct Label(int Length, WalkState? Previous, GraphEdge[] Edges);

    /// <summary>The best row for one target so far, held as its last hop until the walk ends: a target is
    /// improved on several times and the path is only worth rebuilding for the row that survives.</summary>
    private readonly record struct Candidate(Confidence Confidence, int Length, WalkState From, GraphEdge[] Tail);

    /// <summary>
    /// The gaps <c>get_unresolved</c> derives rather than stores, for the branches this walk visited. They
    /// are not in <see cref="CacheGraph.Unresolved"/> — which reason holds depends on what is indexed now,
    /// so they are computed on query — and a walk that read only the stored rows called a branch complete
    /// that the same graph reports a gap on through the tool.
    /// <para>That mattered because it is what verification refuses to refute over: a handler that reads a
    /// table and, on another branch, calls a procedure whose dependencies are unknown could be refuted by
    /// the table's row agreeing, while <c>get_unresolved</c> was reporting the procedure all along. Any of
    /// the four says the graph did not see everything the value was built from.</para>
    /// </summary>
    private static IEnumerable<Unresolved> DerivedGaps(CacheGraph graph,
                                                       IReadOnlyDictionary<(string Solution, string Symbol), Handler> visited,
                                                       IReadOnlySet<string> visitedObjects)
    {
        bool Visited(Handler? handler) => handler is not null && visited.ContainsKey((handler.Solution, handler.Symbol));

        // A procedure counts through its caller and through itself: the walk descends into the vertex even
        // when it has no outgoing edges, which is the very shape a gap describes.
        foreach (var gap in ProcedureGaps.Derive(graph))
        {
            if (Visited(gap.Caller) || visitedObjects.Contains(gap.Procedure))
                yield return gap.Unresolved;
        }

        foreach (var gap in EventGaps.Derive(graph).Where(item => Visited(item.Publisher)))
            yield return gap.Unresolved;

        foreach (var gap in ServiceJoins.Derive(graph).Gaps.Where(item => Visited(item.Reader)))
            yield return gap.Unresolved;
    }

    private static Confidence Weaken(Confidence current, Confidence edge) =>
        (Confidence)Math.Max((int)current, (int)edge);

    private static (string Template, string Store) GetKeyId(CacheKey key) =>
        (key.Template, key.Store);

    /// <summary>The name the database indexer files an unresolved row under, or <c>null</c> for a source
    /// that is not a catalogue object.</summary>
    private static string? QualifiedName(ReadSource source) => source switch
    {
        StoredProcedure procedure => procedure.Name,
        Trigger trigger => trigger.Name,
        View view => view.Name,
        _ => null
    };

    /// <summary>The identity a dependency is reported under, and the order the rows come back in. It is
    /// the id the trace tools name the same vertex by, so one row per target is one row per id there
    /// too.</summary>
    private static string DependencyId(GraphVertex target) => target switch
    {
        Table table => $"table:{table.Name}",
        CacheKey key => $"key:{key.Store}/{key.Template}",
        ExternalSource source => $"external:{source.Kind}:{source.Owner}:{source.ClientName ?? "-"}:{source.Method} {source.Template}",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };
}
