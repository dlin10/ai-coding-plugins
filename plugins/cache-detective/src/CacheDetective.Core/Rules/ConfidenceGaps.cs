using CacheDetective.Events;
using CacheDetective.Graph;

namespace CacheDetective.Rules;

public enum GapScope { Data, Coverage }

public static class ConfidenceGaps
{
    public static bool Touches(CacheGraph graph, IEnumerable<Handler> handlers, GapScope scope)
    {
        var kinds = scope == GapScope.Data
            ? new[] { UnresolvedKind.Call, UnresolvedKind.Sql }
            : new[] { UnresolvedKind.Call, UnresolvedKind.Sql, UnresolvedKind.Key, UnresolvedKind.CacheApi, UnresolvedKind.Event, UnresolvedKind.EventApi };
        var ids = handlers.Select(handler => (handler.Solution, handler.Symbol)).ToHashSet();
        if (graph.GetUnresolvedForHandlers(handlers, kinds).Count > 0 ||
            ProcedureGaps.Derive(graph).Any(gap => gap.Caller is Handler handler && ids.Contains((handler.Solution, handler.Symbol)))) return true;
        return scope == GapScope.Coverage && EventGaps.Derive(graph).Any(gap => ids.Contains((gap.Publisher.Solution, gap.Publisher.Symbol)));
    }

    public static bool Touches(CacheGraph graph, ExternalSource source) =>
        graph.Unresolved.Any(item => graph.TryGetExternalSource(item.Id, out var candidate) && candidate == source) ||
        ServiceJoins.Derive(graph).Gaps.Any(gap => gap.Source == source);
}
