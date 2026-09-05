using CacheDetective.Graph;
using System.Runtime.CompilerServices;

namespace CacheDetective.Rules;

public sealed record ReachedHandler(Handler Handler, IReadOnlyList<GraphEdge> Path, Confidence Confidence, bool ViaEvent);
public sealed record ReachabilityResult(IReadOnlyDictionary<(string Solution, string Symbol), ReachedHandler> Handlers,
                                       IReadOnlySet<(string Solution, string Symbol)> CallOnly,
                                       IReadOnlyList<EventHop> PublishedHops, IReadOnlyList<string> Projects);

public static class Reachability
{
    public static ReachabilityResult From(Handler start, IReadOnlyList<GraphEdge> edges, CacheGraph graph)
    {
        var cache = Caches.GetValue(graph, _ => new Cache());
        if (cache.Version != graph.Version)
        {
            cache.Version = graph.Version;
            cache.Results.Clear();
        }

        var id = Id(start);
        if (!cache.Results.TryGetValue(id, out var result))
        {
            result = Build(start, edges, graph);
            cache.Results[id] = result;
        }
        return result;
    }

    private static ReachabilityResult Build(Handler start, IReadOnlyList<GraphEdge> edges, CacheGraph graph)
    {
        var calls = edges.OfType<Calls>().Where(edge => edge.From is Handler && edge.To is Handler).ToArray();
        var callOnly = new HashSet<(string, string)>();
        var callQueue = new Queue<Handler>(); callQueue.Enqueue(start);
        while (callQueue.TryDequeue(out var current))
        {
            if (!callOnly.Add(Id(current))) continue;
            foreach (var call in calls.Where(call => Id((Handler)call.From) == Id(current))) callQueue.Enqueue((Handler)call.To);
        }

        var reached = new Dictionary<(string, string), ReachedHandler>();
        var pending = new PriorityQueue<ReachedHandler, (int Confidence, int Length)>();
        pending.Enqueue(new ReachedHandler(start, [], Confidence.Confirmed, false), (0, 0));
        while (pending.TryDequeue(out var current, out _))
        {
            var id = Id(current.Handler);
            if (reached.TryGetValue(id, out var known) && !Better(current, known)) continue;
            reached[id] = current;
            foreach (var call in calls.Where(call => Id((Handler)call.From) == id)) Add((Handler)call.To, [.. current.Path, call], Weaken(current.Confidence, call.Confidence), current.ViaEvent);
            foreach (var publish in edges.OfType<Publishes>().Where(edge => edge.From is Handler from && Id(from) == id))
            foreach (var hop in graph.GetEventHops(publish))
            {
                var consume = hop.Consume;
                Add((Handler)consume.To, [.. current.Path, publish, consume with { Confidence = hop.Confidence, Reason = hop.Reason }],
                    Weaken(current.Confidence, hop.Confidence), true);
            }
        }

        var published = edges.OfType<Publishes>().Where(edge => edge.From is Handler handler && callOnly.Contains(Id(handler)))
                             .SelectMany(graph.GetEventHops).ToArray();
        return new ReachabilityResult(reached, callOnly, published,
            reached.Values.Select(item => item.Handler.Project ?? item.Handler.Solution).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());

        void Add(Handler handler, IReadOnlyList<GraphEdge> path, Confidence confidence, bool viaEvent)
        {
            var candidate = new ReachedHandler(handler, path, confidence, viaEvent);
            if (!reached.TryGetValue(Id(handler), out var known) || Better(candidate, known))
                pending.Enqueue(candidate, ((int)confidence, path.Count));
        }
    }
    private static bool Better(ReachedHandler left, ReachedHandler right) => left.Confidence < right.Confidence || left.Confidence == right.Confidence && left.Path.Count < right.Path.Count;
    private static Confidence Weaken(Confidence left, Confidence right) => (Confidence)Math.Max((int)left, (int)right);
    private static (string, string) Id(Handler handler) => (handler.Solution, handler.Symbol);

    private static readonly ConditionalWeakTable<CacheGraph, Cache> Caches = [];
    private sealed class Cache
    {
        public int Version = -1;
        public Dictionary<(string Solution, string Symbol), ReachabilityResult> Results { get; } = [];
    }
}
