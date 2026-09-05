using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class StaleParentKeyTests
{
    [Fact]
    public void A_parent_ttl_longer_than_the_child_is_reported()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out _, out _, out _);

        Assert.Single(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void A_parent_without_ttl_is_reported()
    {
        var graph = Scenario(null, TimeSpan.FromSeconds(30), out _, out _, out _);

        Assert.Single(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void A_child_without_ttl_is_not_reported_when_parent_has_one()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), null, out _, out _, out _);

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(30, 120)]
    public void A_parent_with_a_shorter_or_equal_ttl_is_not_reported(int parentSeconds, int childSeconds)
    {
        var graph = Scenario(TimeSpan.FromSeconds(parentSeconds), TimeSpan.FromSeconds(childSeconds), out _, out _, out _);

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void The_invalidating_handler_is_the_subject_and_projects_are_reported()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out _, out _, out var invalidator);
        var searched = Handler("Search", "Reachable", "Search.API");
        graph.AddEdge(new Calls(invalidator, searched, Confidence.Confirmed));

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(graph));

        Assert.Equal(invalidator, finding.Handler);
        Assert.Contains("Writer.API", finding.SearchedProjects);
        Assert.Contains("Search.API", finding.SearchedProjects);
    }

    [Fact]
    public void Invalidating_the_parent_from_the_same_handler_covers_the_child_invalidation()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out var parent, out _, out var invalidator);
        graph.AddEdge(new Invalidates(invalidator, parent, Confidence.Confirmed));

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void Invalidating_the_parent_through_calls_covers_the_child_invalidation()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out var parent, out _, out var invalidator);
        var coverer = Handler("Coverer", "InvalidateParent", "Coverer.API");
        graph.AddEdge(new Calls(invalidator, coverer, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(coverer, parent, Confidence.Confirmed));

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void Invalidating_the_parent_through_an_event_covers_the_child_invalidation()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out var parent, out _, out var invalidator);
        var coverer = Handler("Coverer", "InvalidateParent", "Coverer.API");
        var @event = new Event("Contracts.ChildChanged");
        graph.AddEdge(new Publishes(invalidator, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, coverer, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(coverer, parent, Confidence.Confirmed));

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void An_unresolved_call_on_the_invalidating_handler_makes_the_finding_unknown()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out _, out _, out var invalidator);
        graph.AddUnresolved(UnresolvedKind.Call, invalidator, new Evidence("Writer.cs", 7), "call", "unknown");

        Assert.Equal(Confidence.Unknown, Assert.Single(new StaleParentKeyRule().Evaluate(graph)).Confidence);
    }

    [Fact]
    public void An_unresolved_key_on_a_reachable_handler_makes_the_finding_unknown()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out _, out _, out var invalidator);
        var reachable = Handler("Coverer", "Reachable", "Coverer.API");
        graph.AddEdge(new Calls(invalidator, reachable, Confidence.Confirmed));
        graph.AddUnresolved(UnresolvedKind.Key, reachable, new Evidence("Coverer.cs", 3), "key", "unknown");

        Assert.Equal(Confidence.Unknown, Assert.Single(new StaleParentKeyRule().Evaluate(graph)).Confidence);
    }

    [Fact]
    public void Two_paths_from_parent_to_child_produce_one_finding()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), out var parent, out var child, out _);
        var secondCache = Handler("Cache", "SecondParentCache", "Cache.API");
        graph.AddEdge(new Caches(secondCache, parent, Confidence.Confirmed));
        graph.AddEdge(new Reads(secondCache, child, Confidence.Confirmed));

        Assert.Single(new StaleParentKeyRule().Evaluate(graph));
    }

    private static CacheGraph Scenario(TimeSpan? parentTtl, TimeSpan? childTtl,
                                       out CacheKey parent, out CacheKey child, out Handler invalidator)
    {
        var graph = new CacheGraph();
        parent = new CacheKey("parent:{id}", "memory", parentTtl, [], "cache");
        child = new CacheKey("child:{id}", "memory", childTtl, [], "cache");
        var parentCache = Handler("Cache", "GetParent", "Cache.API");
        var childCache = Handler("Cache", "GetChild", "Cache.API");
        invalidator = Handler("Writer", "InvalidateChild", "Writer.API");
        graph.AddEdge(new Caches(parentCache, parent, Confidence.Confirmed));
        graph.AddEdge(new Reads(parentCache, child, Confidence.Confirmed));
        graph.AddEdge(new Caches(childCache, child, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(invalidator, child, Confidence.Confirmed));
        return graph;
    }

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project };
}
