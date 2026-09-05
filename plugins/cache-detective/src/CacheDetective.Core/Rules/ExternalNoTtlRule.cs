using CacheDetective.Graph;

namespace CacheDetective.Rules;

public sealed record ExternalNoTtlFinding(Handler Handler, CacheKey Key, ExternalSource Source, Confidence Confidence,
                                          IReadOnlyList<GraphEdge> Chain, double? TtlSeconds, double BudgetSeconds, bool Suppressed)
{
    public const string Rule = "EXTERNAL_NO_TTL";
}

public sealed class ExternalNoTtlRule
{
    public IReadOnlyList<ExternalNoTtlFinding> Evaluate(CacheGraph graph)
    {
        var findings = new List<ExternalNoTtlFinding>();
        foreach (var key in graph.CacheKeys.Where(key => key.Role == "cache"))
        {
            foreach (var dependency in graph.DependsOn(key).Where(item => item.Target is ExternalSource)
                                             .GroupBy(item => item.Target).Select(group => group.OrderBy(item => item.Confidence).ThenBy(item => item.Path.Count).First()))
            {
                var handler = dependency.Path.OfType<Caches>().Select(edge => edge.From as Handler).FirstOrDefault(item => item is not null);
                if (handler is null) continue;
                var budget = StalenessBudget.DefaultSeconds;
                var confidence = ConfidenceGaps.Touches(graph, (ExternalSource)dependency.Target) ||
                                 ConfidenceGaps.Touches(graph, dependency.Path.SelectMany(Handlers), GapScope.Data)
                                     ? Confidence.Unknown : dependency.Confidence;
                findings.Add(new ExternalNoTtlFinding(handler, key, (ExternalSource)dependency.Target, confidence,
                    dependency.Path, key.TtlSeconds, budget, key.TtlSeconds is not null && key.TtlSeconds <= budget));
            }
        }
        return findings;
    }

    private static IEnumerable<Handler> Handlers(GraphEdge edge)
    {
        if (edge.From is Handler from) yield return from;
        if (edge.To is Handler to) yield return to;
    }
}
