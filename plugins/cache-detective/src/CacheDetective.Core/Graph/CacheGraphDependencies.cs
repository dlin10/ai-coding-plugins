namespace CacheDetective.Graph;

public sealed record KeyDependency(GraphVertex Target, Confidence Confidence,
                                   IReadOnlyList<GraphEdge> Path);

public static class CacheGraphDependencies
{
    /// <summary>The depth of the code call graph, so every walk in the project stops the same way.</summary>
    private const int MAXIMUM_DEPTH = 12;

    public static IReadOnlyList<KeyDependency> DependsOn(this CacheGraph graph, CacheKey key)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.GetDependencies(key);
    }

    internal static IReadOnlyList<KeyDependency> Build(CacheGraph graph, CacheKey key)
    {
        var edges = graph.Edges.ToArray();
        var views = graph.Views.ToDictionary(view => view.Name, StringComparer.Ordinal);
        var dependencies = new List<KeyDependency>();
        var keyPath = new HashSet<(string Template, string Store)> { GetKeyId(key) };

        ExpandKey(key, [], Confidence.Confirmed, keyPath);
        return dependencies;

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
            var id = GetSourceId(target);
            if (depth >= MAXIMUM_DEPTH || !activeSources.Add(id))
                return;

            WalkSource(target, path, confidence, activeSources, activeKeys, depth + 1);
            activeSources.Remove(id);
        }
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

    private static string GetSourceId(ReadSource source) => source switch
    {
        Handler handler => $"handler:{handler.Solution}/{handler.Symbol}",
        StoredProcedure procedure => $"procedure:{procedure.Name}",
        Trigger trigger => $"trigger:{trigger.Name}",
        View view => $"view:{view.Name}",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
