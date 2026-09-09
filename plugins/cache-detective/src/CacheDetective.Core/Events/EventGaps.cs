using CacheDetective.Graph;

namespace CacheDetective.Events;

public sealed record EventGap(Publishes Publish, Handler Publisher, Unresolved Unresolved);

public static class EventGaps
{
    public static IReadOnlyList<EventGap> Derive(CacheGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.GetEventGaps();
    }

    internal static IReadOnlyList<EventGap> Build(CacheGraph graph)
    {
        var gaps = new List<EventGap>();

        foreach (var publish in graph.StoredEdges.OfType<Publishes>())
        {
            if (publish.From is not Handler publisher || publish.To is not Event @event ||
                graph.GetEventHops(publish).Count > 0)
            {
                continue;
            }

            var site = publish.Evidence.FirstOrDefault() ?? new Evidence(publisher.File, publisher.Line);
            var identity = $"event|{publisher.Solution}|{publisher.Symbol}|{site.Describe()}|{@event.FullName}";
            var id = graph.GetDerivedUnresolvedId(identity);
            if (graph.IsEventGapSuppressed(id))
            {
                continue;
            }

            gaps.Add(new EventGap(publish, publisher,
                                  new Unresolved(id, UnresolvedKind.Event, publisher.Solution, site, @event.Name,
                                                 $"Event '{@event.Name}' has no consumer in the workspace. Name its handlers, or say it leaves the workspace.")));
        }

        return gaps;
    }
}
