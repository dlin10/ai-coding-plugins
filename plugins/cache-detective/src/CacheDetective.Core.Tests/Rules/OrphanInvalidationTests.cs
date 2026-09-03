using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class OrphanInvalidationTests
{
    [Fact]
    public void OrphanWithoutANearNeighbourReportsOnlyTheOrphan()
    {
        var graph = new CacheGraph();
        AddCached(graph, "live:{id}", "memory");
        AddInvalidation(graph, "totally:dead", "memory");

        var result = new OrphanInvalidationRule().Evaluate(graph);

        Assert.Single(result.Orphans);
        Assert.Empty(result.PatternMismatches);
        Assert.Equal("ORPHAN_INVALIDATION", OrphanInvalidationFinding.Rule);
    }

    [Theory]
    [InlineData("product:{id}", "prodvct:{productId}", 1)]
    [InlineData("order:{id}", "odrer:{otherId}", 2)]
    public void PatternMismatchRefinesOneAndTwoCharacterTypos(string cachedTemplate,
                                                               string invalidationTemplate,
                                                               int expectedDistance)
    {
        var graph = new CacheGraph();
        AddCached(graph, cachedTemplate, "memory");
        AddInvalidation(graph, invalidationTemplate, "memory");

        var result = new OrphanInvalidationRule().Evaluate(graph);

        var orphan = Assert.Single(result.Orphans);
        var mismatch = Assert.Single(result.PatternMismatches);
        Assert.Same(orphan.Invalidation, mismatch.Invalidation);
        Assert.Equal(cachedTemplate, mismatch.CachedKey.Template);
        Assert.Equal(expectedDistance, mismatch.Distance);
        Assert.Equal("PATTERN_MISMATCH", PatternMismatchFinding.Rule);
    }

    [Fact]
    public void PatternMismatchIgnoresThreeCharacterAndCrossStoreDifferences()
    {
        var threeCharacters = new CacheGraph();
        AddCached(threeCharacters, "abc:{id}", "memory");
        AddInvalidation(threeCharacters, "xyz:{id}", "memory");
        var crossStore = new CacheGraph();
        AddCached(crossStore, "product:{id}", "memory");
        AddInvalidation(crossStore, "prodvct:{id}", "redis");

        var distantResult = new OrphanInvalidationRule().Evaluate(threeCharacters);
        var crossStoreResult = new OrphanInvalidationRule().Evaluate(crossStore);

        Assert.Single(distantResult.Orphans);
        Assert.Empty(distantResult.PatternMismatches);
        Assert.Single(crossStoreResult.Orphans);
        Assert.Empty(crossStoreResult.PatternMismatches);
    }

    [Fact]
    public void OrphanDetectionAcceptsPlaceholderShapesAndCorrectPrefixes()
    {
        var placeholder = new CacheGraph();
        AddCached(placeholder, "product:{id}", "memory");
        AddInvalidation(placeholder, "product:{productId}", "memory");
        var prefix = new CacheGraph();
        AddCached(prefix, "product:{id}", "memory");
        AddInvalidation(prefix, "product:*", "memory", CacheSemantic.RemoveByPrefix);

        var placeholderResult = new OrphanInvalidationRule().Evaluate(placeholder);
        var prefixResult = new OrphanInvalidationRule().Evaluate(prefix);

        Assert.Empty(placeholderResult.Orphans);
        Assert.Empty(placeholderResult.PatternMismatches);
        Assert.Empty(prefixResult.Orphans);
        Assert.Empty(prefixResult.PatternMismatches);
    }

    [Fact]
    public void OrphanDetectionUsesTagsAnyForMergedCacheSites()
    {
        var graph = new CacheGraph();
        var first = Handler("FirstCache");
        var second = Handler("SecondCache");
        graph.AddEdge(new Caches(first,
            new CacheKey("product:{id}", "hybrid", null, ["catalog"], "cache"),
            Confidence.Confirmed));
        graph.AddEdge(new Caches(second,
            new CacheKey("product:{id}", "hybrid", null, [], "cache"),
            Confidence.Confirmed));
        AddInvalidation(graph, "catalog", "hybrid", CacheSemantic.RemoveByTag);

        var result = new OrphanInvalidationRule().Evaluate(graph);

        Assert.Empty(result.Orphans);
        Assert.Empty(result.PatternMismatches);
    }

    private static void AddCached(CacheGraph graph, string template, string store) =>
        graph.AddEdge(new Caches(Handler($"Cache:{template}:{store}"),
            new CacheKey(template, store, null, [], "cache"), Confidence.Confirmed));

    private static void AddInvalidation(CacheGraph graph, string template, string store,
                                        CacheSemantic semantic = CacheSemantic.Remove) =>
        graph.AddEdge(new Invalidates(Handler($"Remove:{template}:{store}"),
            new CacheKey(template, store, null, [], null), Confidence.Confirmed,
            semantic: semantic));

    private static Handler Handler(string symbol) =>
        new("fixture", symbol, "method", "fixture.cs", 1);
}
