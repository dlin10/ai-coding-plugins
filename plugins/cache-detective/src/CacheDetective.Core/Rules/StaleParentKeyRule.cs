using CacheDetective.Caching;
using CacheDetective.Graph;

namespace CacheDetective.Rules;

public sealed record StaleParentKeyFinding(Handler Handler, CacheKey Parent, CacheKey Child, Confidence Confidence,
                                          IReadOnlyList<GraphEdge> Chain, double? ParentTtlSeconds, double? ChildTtlSeconds,
                                          IReadOnlyList<string> SearchedProjects)
{
    public const string Rule = "STALE_PARENT_KEY";
}

public sealed class StaleParentKeyRule
{
    public IReadOnlyList<StaleParentKeyFinding> Evaluate(CacheGraph graph)
    {
        var edges = graph.Edges;
        var findings = new List<StaleParentKeyFinding>();
        foreach (var parent in graph.CacheKeys.Where(key => key.Role == "cache"))
        foreach (var dependency in graph.DependsOn(parent).Where(item => item.Target is CacheKey)
                                          .GroupBy(item => { var key = (CacheKey)item.Target; return (key.Store, key.Template); })
                                          .Select(group => group.OrderBy(item => item.Confidence).ThenBy(item => item.Path.Count).First()))
        {
            var child = (CacheKey)dependency.Target;
            if (parent.Ttl is not null && (child.Ttl is null || parent.Ttl <= child.Ttl)) continue;
            foreach (var invalidation in edges.OfType<Invalidates>().Where(edge => CacheKeyCovering.Covers(edge, child, child.TagsAll))
                                               .GroupBy(edge => (((Handler)edge.From).Solution, ((Handler)edge.From).Symbol))
                                               .Select(group => group.First()))
            {
                var handler = (Handler)invalidation.From;
                var reach = Reachability.From(handler, edges, graph);
                if (edges.OfType<Invalidates>().Any(edge => reach.Handlers.ContainsKey((((Handler)edge.From).Solution, ((Handler)edge.From).Symbol)) &&
                                                        CacheKeyCovering.Covers(edge, parent, parent.TagsAll))) continue;
                var confidence = ConfidenceGaps.Touches(graph, new[] { handler }.Concat(dependency.Path.SelectMany(Handlers)), GapScope.Data) ||
                                 ConfidenceGaps.Touches(graph, reach.Handlers.Values.Select(item => item.Handler), GapScope.Coverage)
                                     ? Confidence.Unknown : Weaken(invalidation.Confidence, dependency.Confidence);
                findings.Add(new StaleParentKeyFinding(handler, parent, child, confidence,
                    [invalidation, .. dependency.Path], parent.TtlSeconds, child.TtlSeconds, reach.Projects));
            }
        }
        return findings;
    }
    private static Confidence Weaken(Confidence left, Confidence right) => (Confidence)Math.Max((int)left, (int)right);
    private static IEnumerable<Handler> Handlers(GraphEdge edge)
    {
        if (edge.From is Handler from) yield return from;
        if (edge.To is Handler to) yield return to;
    }
}
