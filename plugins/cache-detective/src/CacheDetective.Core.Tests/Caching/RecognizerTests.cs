using CacheDetective.Caching;
using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Caching;

public sealed class RecognizerTests
{
    [Fact]
    public async Task RecognizesStoresSemanticsTtlsTagsAndConditionalSets()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Recognizers.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        AssertOperation(graph, "memory", CacheSemantic.Set, "memory-key", 30);
        AssertOperation(graph, "memory", CacheSemantic.Get, "memory-key");
        AssertOperation(graph, "memory", CacheSemantic.Remove, "memory-key");
        AssertOperation(graph, "distributed", CacheSemantic.Set, "distributed-key", 45);
        AssertOperation(graph, "distributed", CacheSemantic.Get, "distributed-key");
        AssertOperation(graph, "distributed", CacheSemantic.Remove, "distributed-key");

        var hybrid = AssertOperation(graph, "hybrid", CacheSemantic.Set, "hybrid-key", 60);
        Assert.Equal(new[] { "tag-a" }, hybrid.Key.TagsAll);
        Assert.Equal(new[] { "tag-a" }, hybrid.Key.TagsAny);
        AssertOperation(graph, "hybrid", CacheSemantic.RemoveByTag, "tag-a");

        var conditional = AssertOperation(graph, "redis", CacheSemantic.Set, "redis-key", 90);
        Assert.True(conditional.IsConditionalSet);
        AssertOperation(graph, "redis", CacheSemantic.Get, "redis-key");
        AssertOperation(graph, "redis", CacheSemantic.Set, "hash-key");
        AssertOperation(graph, "redis", CacheSemantic.Get, "hash-key");
        AssertOperation(graph, "redis", CacheSemantic.Increment, "counter-key");
        AssertOperation(graph, "redis", CacheSemantic.Expire, "redis-key");
        AssertOperation(graph, "redis", CacheSemantic.Remove, "redis-key");

        Assert.Contains(graph.Edges, edge => edge is Caches);
        Assert.Contains(graph.Edges, edge => edge is Reads { To: CacheKey });
        Assert.Contains(graph.Edges, edge => edge is Invalidates);
    }

    [Fact]
    public async Task RecordsUnknownCacheTypeAndOutputCacheAttributeOnceEach()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Recognizers.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var unresolved = graph.Unresolved.Where(item => item.Kind == UnresolvedKind.CacheApi).ToArray();
        Assert.Equal(2, unresolved.Length);
        Assert.Single(unresolved, item => item.Reason.Contains("MysteryCache", StringComparison.Ordinal));
        Assert.Single(unresolved, item => item.Reason.Contains("OutputCacheAttribute", StringComparison.Ordinal));
    }

    private static CacheOperation AssertOperation(CacheGraph graph, string store, CacheSemantic semantic,
                                                  string template, double? ttlSeconds = null)
    {
        var operation = Assert.Single(graph.CacheOperations,
            candidate => candidate.Key.Store == store && candidate.Semantic == semantic &&
                         candidate.Key.Template == template);
        if (ttlSeconds is not null)
        {
            Assert.Equal(ttlSeconds, operation.Key.TtlSeconds);
        }

        return operation;
    }
}
