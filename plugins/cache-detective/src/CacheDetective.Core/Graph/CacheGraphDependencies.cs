namespace CacheDetective.Graph;

public sealed record KeyDependency(GraphVertex Target, Confidence Confidence,
                                   IReadOnlyList<GraphEdge> Path);

public static class CacheGraphDependencies
{
    public static IReadOnlyList<KeyDependency> DependsOn(this CacheGraph graph, CacheKey key)
    {
        var edges = graph.Edges.ToArray();
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
                WalkHandler(handler, nextPath, nextConfidence,
                    new HashSet<(string Solution, string Symbol)> { GetHandlerId(handler) }, activeKeys);
            }
        }

        void WalkHandler(Handler handler, IReadOnlyList<GraphEdge> path, Confidence confidence,
                         ISet<(string Solution, string Symbol)> activeHandlers,
                         ISet<(string Template, string Store)> activeKeys)
        {
            foreach (var read in edges.OfType<Reads>().Where(edge => IsHandler(edge.From, handler)))
            {
                var nextPath = Append(path, read);
                var nextConfidence = Weaken(confidence, read.Confidence);
                if (read.To is Table table)
                {
                    dependencies.Add(new KeyDependency(table, nextConfidence, nextPath));
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

            foreach (var call in edges.OfType<Calls>().Where(edge => IsHandler(edge.From, handler)))
            {
                var target = (Handler)call.To;
                var targetId = GetHandlerId(target);
                if (!activeHandlers.Add(targetId))
                    continue;

                WalkHandler(target, Append(path, call), Weaken(confidence, call.Confidence),
                    activeHandlers, activeKeys);
                activeHandlers.Remove(targetId);
            }
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

    private static bool IsHandler(GraphVertex candidate, Handler handler) =>
        candidate is Handler candidateHandler && GetHandlerId(candidateHandler) == GetHandlerId(handler);

    private static (string Template, string Store) GetKeyId(CacheKey key) =>
        (key.Template, key.Store);

    private static (string Solution, string Symbol) GetHandlerId(Handler handler) =>
        (handler.Solution, handler.Symbol);
}
