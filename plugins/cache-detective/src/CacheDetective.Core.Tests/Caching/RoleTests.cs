using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Caching;

public sealed class RoleTests
{
    [Fact]
    public async Task ClassifiesStoreAndCacheSignalsInOrder()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Roles.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        AssertRole(graph, "session:user", "memory", "store");
        AssertRole(graph, "computed:value", "memory", "store");
        AssertRole(graph, "hits:global", "redis", "store");
        AssertRole(graph, "expiry:key", "redis", "store");
        AssertRole(graph, "conditional:key", "redis", "store");
        AssertRole(graph, "catalog:rows", "memory", "cache");
    }

    [Fact]
    public async Task LeavesAnIncompleteReadPathUnknown()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Roles.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        AssertRole(graph, "unknown:reader", "memory", "unknown");
        var unresolved = Assert.Single(graph.Unresolved, item => item.Kind == UnresolvedKind.Role);
        Assert.Equal("unknown:reader", unresolved.Snippet);
        Assert.Contains("call", unresolved.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No implementation found", unresolved.Reason, StringComparison.Ordinal);
    }

    private static void AssertRole(CacheGraph graph, string template, string store, string role)
    {
        var key = Assert.Single(graph.CacheKeys,
            candidate => candidate.Template == template && candidate.Store == store);
        Assert.Equal(role, key.Role);
    }
}
