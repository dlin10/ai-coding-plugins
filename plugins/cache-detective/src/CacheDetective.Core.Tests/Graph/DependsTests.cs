using CacheDetective.Caching;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

public sealed class DependsTests
{
    [Fact]
    public void CoversExactShapePrefixAndSelectedTagSetWithinOneStore()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("first", new CacheKey("product:{productId}", "memory", null,
            ["catalog", "featured"], null));
        graph.AddCacheKey("second", new CacheKey("product:{productId}", "memory", null,
            ["featured"], null));
        var cached = Assert.Single(graph.CacheKeys);
        var sameShape = new CacheKey("product:{id}", "memory", null, [], null);
        var prefix = new CacheKey("product:*", "memory", null, [], null);
        var otherStore = new CacheKey("product:*", "redis", null, [], null);
        var tag = new CacheKey("catalog", "memory", null, [], null);
        var handler = new Handler("fixture", "Handler.Remove", "method", "fixture.cs", 1);
        var tagRemoval = new Invalidates(handler, tag, Confidence.Confirmed,
            semantic: CacheSemantic.RemoveByTag);

        Assert.True(CacheKeyCovering.Covers(sameShape, cached));
        Assert.True(CacheKeyCovering.Covers(prefix, cached));
        Assert.False(CacheKeyCovering.Covers(otherStore, cached));
        Assert.False(CacheKeyCovering.Covers(tagRemoval, cached, cached.TagsAll));
        Assert.True(CacheKeyCovering.Covers(tagRemoval, cached, cached.TagsAny));
    }

    [Fact]
    public void ExpandsKeyDependenciesCutsCyclesAndWeakensConfidence()
    {
        var graph = new CacheGraph();
        var outer = new CacheKey("outer:{id}", "memory", null, [], "cache");
        var inner = new CacheKey("inner:{id}", "memory", null, [], "cache");
        var table = new Table("dbo.Products", "default");
        var outerWriter = Handler("OuterWriter");
        var reader = Handler("Reader");
        var innerWriter = Handler("InnerWriter");

        graph.AddEdge(new Caches(outerWriter, outer, Confidence.Confirmed));
        graph.AddEdge(new Calls(outerWriter, reader, Confidence.Likely));
        graph.AddEdge(new Reads(reader, inner, Confidence.Confirmed));
        graph.AddEdge(new Caches(innerWriter, inner, Confidence.Confirmed));
        graph.AddEdge(new Reads(innerWriter, table, Confidence.Confirmed));
        graph.AddEdge(new Reads(innerWriter, outer, Confidence.Confirmed));

        var dependencies = graph.DependsOn(outer);

        Assert.Equal(2, dependencies.Count);
        var keyDependency = Assert.Single(dependencies,
            dependency => dependency.Target is CacheKey key && key.Template == "inner:{id}");
        Assert.Equal(Confidence.Likely, keyDependency.Confidence);
        Assert.Collection(keyDependency.Path,
            edge => Assert.IsType<Caches>(edge),
            edge => Assert.IsType<Calls>(edge),
            edge => Assert.IsType<Reads>(edge));

        var tableDependency = Assert.Single(dependencies,
            dependency => dependency.Target is Table candidate && candidate.Name == "dbo.Products");
        Assert.Equal(Confidence.Likely, tableDependency.Confidence);
        Assert.Equal(5, tableDependency.Path.Count);
        Assert.DoesNotContain(dependencies,
            dependency => dependency.Target is CacheKey key && key.Template == "outer:{id}");
    }

    private static Handler Handler(string symbol) =>
        new("fixture", symbol, "method", "fixture.cs", 1);
}
