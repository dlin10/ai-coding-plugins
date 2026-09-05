using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class ExternalNoTtlTests
{
    [Fact]
    public void A_cache_of_an_external_source_without_ttl_is_reported()
    {
        var graph = Scenario(null, out _, out _, out _);

        var finding = Assert.Single(new ExternalNoTtlRule().Evaluate(graph));

        Assert.Null(finding.TtlSeconds);
        Assert.False(finding.Suppressed);
    }

    [Fact]
    public void A_ttl_within_the_budget_is_reported_as_suppressed()
    {
        var graph = Scenario(TimeSpan.FromSeconds(30), out _, out _, out _);

        Assert.True(Assert.Single(new ExternalNoTtlRule().Evaluate(graph)).Suppressed);
    }

    [Fact]
    public void A_ttl_above_the_budget_is_not_suppressed()
    {
        var graph = Scenario(TimeSpan.FromSeconds(120), out _, out _, out _);

        Assert.False(Assert.Single(new ExternalNoTtlRule().Evaluate(graph)).Suppressed);
    }

    [Fact]
    public void Two_paths_to_one_external_source_produce_one_finding()
    {
        var graph = Scenario(null, out var key, out var source, out _);
        var second = Handler("Reader", "SecondReader", "Reader.API");
        graph.AddEdge(new Caches(second, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(second, source, Confidence.Confirmed));

        Assert.Single(new ExternalNoTtlRule().Evaluate(graph));
    }

    [Fact]
    public void An_external_source_linked_to_an_unresolved_call_is_unknown()
    {
        var graph = Scenario(null, out _, out var source, out var reader);
        graph.AddUnresolvedExternal(UnresolvedKind.Call, reader, new Evidence("Reader.cs", 7), "url",
            "HTTP URL has no literal segment: name the endpoint it reaches.", source);

        Assert.Equal(Confidence.Unknown, Assert.Single(new ExternalNoTtlRule().Evaluate(graph)).Confidence);
    }

    [Fact]
    public void An_external_source_with_a_service_join_gap_is_unknown()
    {
        var graph = Scenario(null, out _, out _, out _);
        var reader = Assert.Single(graph.Handlers);
        var source = Assert.Single(graph.ExternalSources);
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "items", "other", source.Owner), Confidence.Confirmed));
        graph.AddHandler(Handler("Catalog", "GetItems", "Catalog.API", [new HandlerRoute("http", "GET", "items")]));
        graph.AddHandler(Handler("Orders", "GetItems", "Orders.API", [new HandlerRoute("http", "GET", "items")]));

        var findings = new ExternalNoTtlRule().Evaluate(graph);

        Assert.Contains(findings, finding => finding.Source.ClientName == "other" && finding.Confidence == Confidence.Unknown);
    }

    [Fact]
    public void A_served_external_source_is_not_an_external_leaf_finding()
    {
        var graph = Scenario(null, out _, out var source, out _);
        var target = Handler("Catalog", "GetItems", "Catalog.API");
        graph.AddEdge(new Serves(source, target, Confidence.Likely, [], "client_name"));
        graph.AddEdge(new Reads(target, new Table("dbo.Items", "shop"), Confidence.Confirmed));

        Assert.Empty(new ExternalNoTtlRule().Evaluate(graph));
    }

    private static CacheGraph Scenario(TimeSpan? ttl, out CacheKey key, out ExternalSource source, out Handler reader)
    {
        var graph = new CacheGraph();
        key = new CacheKey("weather:today", "memory", ttl, [], "cache");
        reader = Handler("Reader", "GetWeather", "Reader.API");
        source = new ExternalSource("http", "GET", "items", "catalog", reader.ServiceId());
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed));
        return graph;
    }

    private static Handler Handler(string solution, string symbol, string project, IReadOnlyList<HandlerRoute>? routes = null) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project, Routes = routes ?? [] };
}
