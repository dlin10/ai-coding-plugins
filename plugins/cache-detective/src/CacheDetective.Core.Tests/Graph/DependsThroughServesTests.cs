using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

public sealed class DependsThroughServesTests
{
    [Fact]
    public void A_serves_edge_reaches_the_target_handlers_table_at_its_confidence()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("digest:{id}", "memory", null, [], "cache");
        var source = new ExternalSource("http", "GET", "items", "catalog", "Reader.API");
        var reader = Handler("Reader", "GetDigest", "Reader.API");
        var target = Handler("Catalog", "GetItems", "Catalog.API");
        var table = new Table("dbo.Items", "shop");
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed));
        graph.AddEdge(new Serves(source, target, Confidence.Likely, [], "client_name"));
        graph.AddEdge(new Reads(target, table, Confidence.Confirmed));

        var dependency = Assert.Single(graph.DependsOn(key), item => item.Target == table);

        Assert.Equal(Confidence.Likely, dependency.Confidence);
        Assert.Contains(dependency.Path, edge => edge is Serves { From: var from, To: var to } && from == source && to == target);
    }

    [Fact]
    public void Without_a_serves_edge_the_external_source_is_a_leaf()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("digest:{id}", "memory", null, [], "cache");
        var source = new ExternalSource("http", "GET", "items", "catalog", "Reader.API");
        var reader = Handler("Reader", "GetDigest", "Reader.API");
        var target = Handler("Catalog", "GetItems", "Catalog.API");
        var table = new Table("dbo.Items", "shop");
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed));
        graph.AddEdge(new Reads(target, table, Confidence.Confirmed));

        var dependencies = graph.DependsOn(key);

        Assert.Contains(dependencies, item => item.Target == source);
        Assert.DoesNotContain(dependencies, item => item.Target == table);
    }

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project };
}
