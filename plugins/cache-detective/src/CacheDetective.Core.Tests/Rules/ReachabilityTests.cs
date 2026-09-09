using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class ReachabilityTests
{
    [Fact]
    public void A_confirmed_call_path_replaces_a_shorter_likely_event_path_for_descendants()
    {
        var graph = new CacheGraph();
        var start = Handler("A", "Start");
        var middle = Handler("A", "Middle");
        var target = Handler("B", "Target");
        var child = Handler("B", "Child");
        var @event = new Event("Contracts.Changed");
        graph.AddEdge(new Calls(start, middle, Confidence.Confirmed));
        graph.AddEdge(new Calls(middle, target, Confidence.Confirmed));
        graph.AddEdge(new Calls(target, child, Confidence.Confirmed));
        graph.AddEdge(new Publishes(start, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, target, Confidence.Likely));

        var reach = Reachability.From(start, graph.Edges, graph);

        var reachedTarget = reach.Handlers[(target.Solution, target.Symbol)];
        var reachedChild = reach.Handlers[(child.Solution, child.Symbol)];
        Assert.Equal(Confidence.Confirmed, reachedTarget.Confidence);
        Assert.False(reachedTarget.ViaEvent);
        Assert.Equal(Confidence.Confirmed, reachedChild.Confidence);
        Assert.False(reachedChild.ViaEvent);
        Assert.Equal(2, reachedTarget.Path.Count);
        Assert.Equal(3, reachedChild.Path.Count);
    }

    [Fact]
    public void A_handler_reached_through_calls_is_in_call_only_and_its_publish_is_reported()
    {
        var graph = new CacheGraph();
        var start = Handler("A", "Start");
        var middle = Handler("A", "Middle");
        var target = Handler("B", "Target");
        var @event = new Event("Contracts.First");
        var second = new Event("Contracts.Second");
        graph.AddEdge(new Calls(start, middle, Confidence.Confirmed));
        graph.AddEdge(new Calls(middle, target, Confidence.Confirmed));
        graph.AddEdge(new Publishes(start, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, target, Confidence.Likely));
        graph.AddEdge(new Publishes(target, second, Confidence.Confirmed));
        graph.AddEdge(new Consumes(second, Handler("C", "Consumer"), Confidence.Confirmed));

        var reach = Reachability.From(start, graph.Edges, graph);

        Assert.Contains((target.Solution, target.Symbol), reach.CallOnly);
        Assert.Contains(reach.PublishedHops, hop => hop.Publish.From == target && hop.Publish.To == second);
    }

    private static Handler Handler(string solution, string symbol) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = solution };
}
