using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class CrossServiceGapTests
{
    [Fact]
    public void An_invalidation_in_the_event_consumer_covers_the_write()
    {
        var graph = Scenario(out var writer, out var key, out var @event);
        var consumer = Handler("B", "Consumer");
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(consumer, key, Confidence.Confirmed));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void An_uncovered_cross_service_event_is_a_cross_service_gap()
    {
        var graph = Scenario(out var writer, out _, out var @event);
        var consumer = Handler("B", "Consumer", "B.Api");
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(UnguardedWriteFinding.CrossServiceGapRule, finding.RuleName);
        Assert.Collection(finding.EventChain, edge => Assert.IsType<Publishes>(edge), edge => Assert.IsType<Consumes>(edge));
        Assert.Contains("B.Api", finding.SearchedProjects);
    }

    [Fact]
    public void A_duplicated_contract_keeps_the_finding_confirmed_but_marks_the_consume_likely()
    {
        var graph = Scenario(out var writer, out _, out _);
        var publish = new Event("Publisher.Changed");
        var consume = new Event("Consumer.Changed");
        graph.AddEdge(new Publishes(writer, publish, Confidence.Confirmed));
        graph.AddEdge(new Consumes(consume, Handler("B", "Consumer"), Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));
        var edge = Assert.IsType<Consumes>(finding.EventChain[1]);

        Assert.Equal(Confidence.Confirmed, finding.Confidence);
        Assert.Equal(Confidence.Likely, edge.Confidence);
        Assert.Contains("contract duplicated across services", edge.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_contracts_in_one_service_leave_an_unguarded_write()
    {
        var graph = Scenario(out var writer, out _, out _);
        graph.AddEdge(new Publishes(writer, new Event("A.Changed"), Confidence.Confirmed));
        graph.AddEdge(new Consumes(new Event("B.Changed"), Handler("A", "Consumer"), Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(UnguardedWriteFinding.Rule, finding.RuleName);
        Assert.Empty(finding.EventChain);
    }

    [Fact]
    public void An_event_without_consumers_leaves_an_unguarded_write()
    {
        var graph = Scenario(out var writer, out _, out var @event);
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(UnguardedWriteFinding.Rule, finding.RuleName);
        Assert.Empty(finding.EventChain);
    }

    [Fact]
    public void An_unresolved_key_on_the_consumer_makes_the_gap_unknown()
    {
        var graph = Scenario(out var writer, out _, out var @event);
        var consumer = Handler("B", "Consumer");
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed));
        graph.AddUnresolved(UnresolvedKind.Key, consumer, new Evidence("B.cs", 2), "key", "unknown");

        Assert.Equal(Confidence.Unknown, Assert.Single(new UnguardedWriteRule().Evaluate(graph)).Confidence);
    }

    [Fact]
    public void Only_call_only_publishers_contribute_to_the_event_chain()
    {
        var graph = Scenario(out var writer, out _, out var first);
        var consumer = Handler("B", "FirstConsumer");
        graph.AddEdge(new Publishes(writer, first, Confidence.Confirmed));
        graph.AddEdge(new Consumes(first, consumer, Confidence.Confirmed));
        graph.AddEdge(new Publishes(consumer, new Event("Contracts.Second"), Confidence.Confirmed));
        graph.AddEdge(new Consumes(new Event("Contracts.Second"), Handler("C", "SecondConsumer"), Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(2, finding.EventChain.Count);
        Assert.All(finding.EventChain, edge => Assert.False(edge is Publishes { From: var from } && from == consumer));
    }

    [Fact]
    public void A_call_only_handler_reached_by_a_long_call_path_and_short_hop_contributes_its_publish()
    {
        var graph = Scenario(out var writer, out _, out var first);
        var middle = Handler("A", "Middle");
        var target = Handler("B", "Target");
        var final = new Event("Contracts.Final");
        graph.AddEdge(new Calls(writer, middle, Confidence.Confirmed));
        graph.AddEdge(new Calls(middle, target, Confidence.Confirmed));
        graph.AddEdge(new Publishes(writer, first, Confidence.Confirmed));
        graph.AddEdge(new Consumes(first, target, Confidence.Likely));
        graph.AddEdge(new Publishes(target, final, Confidence.Confirmed));
        graph.AddEdge(new Consumes(final, Handler("C", "FinalConsumer"), Confidence.Confirmed));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(UnguardedWriteFinding.CrossServiceGapRule, finding.RuleName);
        Assert.Contains(finding.EventChain, edge => edge is Publishes { From: var from, To: var to } && from == target && to == final);
    }

    private static CacheGraph Scenario(out Handler writer, out CacheKey key, out Event @event)
    {
        var graph = new CacheGraph();
        writer = Handler("A", "Writer");
        var reader = Handler("A", "Reader");
        key = new CacheKey("item", "memory", null, [], "cache");
        var table = new Table("dbo.Items", "shop");
        @event = new Event("Contracts.Changed");
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, table, Confidence.Confirmed));
        return graph;
    }

    private static Handler Handler(string solution, string symbol, string? project = null) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project ?? solution };
}
