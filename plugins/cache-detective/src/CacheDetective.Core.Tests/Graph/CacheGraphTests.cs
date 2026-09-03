using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

public sealed class CacheGraphTests
{
    [Fact]
    public void TablesAreDeduplicatedAcrossSolutions()
    {
        var graph = new CacheGraph();

        graph.AddTable("one", new Table("dbo.Products", "shop"));
        graph.AddTable("two", new Table("dbo.Products", "shop"));

        Assert.Single(graph.Tables);
    }

    [Fact]
    public void SameTemplateInDifferentStoresRemainsDistinct()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory"));
        graph.AddCacheKey("one", Key("product:{id}", "redis"));

        Assert.Equal(2, graph.CacheKeys.Count);
    }

    [Fact]
    public void NoTtlWinsWhenSitesAreMerged()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory", TimeSpan.FromSeconds(30)));
        graph.AddCacheKey("one", Key("product:{id}", "memory"));

        Assert.Null(Assert.Single(graph.CacheKeys).Ttl);
    }

    [Fact]
    public void TagsTrackIntersectionAndUnionAcrossSites()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory", tags: ["products"]));
        graph.AddCacheKey("one", Key("product:{id}", "memory"));

        var key = Assert.Single(graph.CacheKeys);
        Assert.Empty(key.TagsAll);
        Assert.Equal(new[] { "products" }, key.TagsAny);
    }

    [Fact]
    public void ReindexReplacesContributionsFromTheSolution()
    {
        var graph = new CacheGraph();
        graph.AddTable("one", new Table("dbo.Old"));
        graph.AddTable("two", new Table("dbo.Shared"));

        var replacement = new CacheGraph();
        replacement.AddTable("one", new Table("dbo.New"));
        replacement.AddTable("one", new Table("dbo.Shared"));

        graph.ReplaceSolution("one", replacement);

        Assert.Equal(2, graph.Tables.Count);
        Assert.DoesNotContain(graph.Tables, table => table.Name == "dbo.Old");
        Assert.Contains(graph.Tables, table => table.Name == "dbo.New");
        Assert.Single(graph.Tables, table => table.Name == "dbo.Shared");
    }

    private static CacheKey Key(string template, string store, TimeSpan? ttl = null,
                                IEnumerable<string>? tags = null) =>
        new(template, store, ttl, tags, role: null);
}
