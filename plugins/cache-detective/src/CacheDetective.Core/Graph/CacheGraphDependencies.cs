using CacheDetective.Events;

namespace CacheDetective.Graph;

public sealed record KeyDependency(GraphVertex Target, Confidence Confidence,
                                   IReadOnlyList<GraphEdge> Path);

/// <summary>
/// What one walk found and how much of it it saw. <paramref name="Unresolved"/> holds the rows of every
/// branch the walk visited, including branches that produced no dependency at all — an unrecognised call
/// in a handler that reads nothing is exactly the case where the graph's silence means least.
/// <para><paramref name="DepthLimitReached"/> is the only kind of incompleteness there is: stopping at a
/// source or a key already on the path is legitimate pruning of a cycle, and says nothing was missed.</para>
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
        var edges = graph.Edges.ToArray();
        var views = graph.Views.ToDictionary(view => view.Name, StringComparer.Ordinal);
        var dependencies = new List<KeyDependency>();
        var visited = new Dictionary<(string Solution, string Symbol), Handler>();
        var visitedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyPath = new HashSet<(string Template, string Store)> { GetKeyId(key) };
        var depthLimitReached = false;

        ExpandKey(key, [], Confidence.Confirmed, keyPath);

        // Both halves of the walk, not just the code half. A procedure or view the walk descended into can
        // carry unresolved rows of its own — dynamic SQL, or dependencies this login may not read — and
        // those lie on a visited branch exactly as an unrecognised call in a handler does.
        var unresolved = graph.GetUnresolvedForHandlers(visited.Values, DEPENDENCY_KINDS)
                              .Concat(graph.GetUnresolvedForDatabaseObjects(visitedObjects, DEPENDENCY_KINDS))
                              .Concat(DerivedGaps(graph, visited, visitedObjects))
                              .DistinctBy(item => item.Id)
                              .OrderBy(item => item.Id)
                              .ToArray();
        return new KeyDependencyWalk(dependencies, unresolved, depthLimitReached);

        void ExpandKey(CacheKey current, IReadOnlyList<GraphEdge> path, Confidence confidence,
                       ISet<(string Template, string Store)> activeKeys)
        {
            foreach (var caches in edges.OfType<Caches>().Where(edge => IsKey(edge.To, current)))
            {
                var nextPath = Append(path, caches);
                var nextConfidence = Weaken(confidence, caches.Confidence);
                var handler = (Handler)caches.From;
                WalkSource(handler, nextPath, nextConfidence,
                    new HashSet<string>(StringComparer.Ordinal) { GetSourceId(handler) }, activeKeys, 0);
            }
        }

        // Walks out of whatever can read: a handler, a procedure it calls, or a view it reads. A handler
        // that calls a procedure depends on what the procedure reads, and one that reads a view depends
        // on the view's tables.
        void WalkSource(ReadSource source, IReadOnlyList<GraphEdge> path, Confidence confidence,
                        ISet<string> activeSources, ISet<(string Template, string Store)> activeKeys,
                        int depth)
        {
            // Every branch the walk enters, whether or not it comes back with a dependency: an
            // unrecognised call in a handler that reads nothing is the case the graph is quietest about.
            if (source is Handler visitedHandler)
            {
                visited[(visitedHandler.Solution, visitedHandler.Symbol)] = visitedHandler;
            }
            else if (QualifiedName(source) is { } databaseObject)
            {
                visitedObjects.Add(databaseObject);
            }

            foreach (var read in edges.OfType<Reads>().Where(edge => IsSource(edge.From, source)))
            {
                var nextPath = Append(path, read);
                var nextConfidence = Weaken(confidence, read.Confidence);
                if (read.To is Table table)
                {
                    // A view of the same name displaces the table: neither half of the graph can tell a
                    // view from a table by name alone, so the read continues into the view's own reads.
                    // With no database indexed the name stays a plain table and the chain ends here,
                    // which is what phase 1 did.
                    if (views.TryGetValue(table.Name, out var view))
                    {
                        Descend(view, nextPath, nextConfidence, activeSources, activeKeys, depth);
                        continue;
                    }

                    dependencies.Add(new KeyDependency(table, nextConfidence, nextPath));
                    continue;
                }

                if (read.To is ExternalSource external)
                {
                    var joins = edges.OfType<Serves>().Where(edge => edge.From is ExternalSource candidate && candidate == external).ToArray();
                    if (joins.Length == 0)
                    {
                        dependencies.Add(new KeyDependency(external, nextConfidence, nextPath));
                        continue;
                    }

                    foreach (var serves in joins)
                        Descend((Handler)serves.To, Append(nextPath, serves), Weaken(nextConfidence, serves.Confidence),
                            activeSources, activeKeys, depth);
                    continue;
                }

                var dependencyKey = (CacheKey)read.To;
                var dependencyId = GetKeyId(dependencyKey);
                if (!activeKeys.Add(dependencyId))
                    continue;

                dependencies.Add(new KeyDependency(dependencyKey, nextConfidence, nextPath));
                ExpandKey(dependencyKey, nextPath, nextConfidence, activeKeys);
                activeKeys.Remove(dependencyId);
            }

            foreach (var call in edges.OfType<Calls>().Where(edge => IsSource(edge.From, source)))
            {
                if (call.To is ReadSource target)
                {
                    Descend(target, Append(path, call), Weaken(confidence, call.Confidence),
                        activeSources, activeKeys, depth);
                }
            }
        }

        void Descend(ReadSource target, IReadOnlyList<GraphEdge> path, Confidence confidence,
                     ISet<string> activeSources, ISet<(string Template, string Store)> activeKeys,
                     int depth)
        {
            // The two reasons to stop are not the same reason. Running out of depth means the walk did
            // not see everything; meeting a source already on the path means the cycle is closed and
            // there was nothing further to see.
            if (depth >= MAXIMUM_DEPTH)
            {
                depthLimitReached = true;
                return;
            }

            var id = GetSourceId(target);
            if (!activeSources.Add(id))
                return;

            WalkSource(target, path, confidence, activeSources, activeKeys, depth + 1);
            activeSources.Remove(id);
        }
    }

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

    private static IReadOnlyList<GraphEdge> Append(IReadOnlyList<GraphEdge> path, GraphEdge edge)
    {
        var result = new GraphEdge[path.Count + 1];
        for (var index = 0; index < path.Count; index++)
            result[index] = path[index];
        result[^1] = edge;
        return result;
    }

    private static Confidence Weaken(Confidence current, Confidence edge) =>
        (Confidence)Math.Max((int)current, (int)edge);

    private static bool IsKey(GraphVertex candidate, CacheKey key) =>
        candidate is CacheKey candidateKey && GetKeyId(candidateKey) == GetKeyId(key);

    private static bool IsSource(GraphVertex candidate, ReadSource source) =>
        candidate is ReadSource candidateSource && GetSourceId(candidateSource) == GetSourceId(source);

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

    private static string GetSourceId(ReadSource source) => source switch
    {
        Handler handler => $"handler:{handler.Solution}/{handler.Symbol}",
        StoredProcedure procedure => $"procedure:{procedure.Name}",
        Trigger trigger => $"trigger:{trigger.Name}",
        View view => $"view:{view.Name}",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
