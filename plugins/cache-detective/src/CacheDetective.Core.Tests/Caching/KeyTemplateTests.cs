using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Caching;

public sealed class KeyTemplateTests
{
    [Fact]
    public async Task FoldsSupportedKeyFormsAndDeduplicatesTemplates()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/KeyTemplates.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");
        var templates = graph.CacheKeys.Where(key => key.Store == "memory")
                             .Select(key => key.Template)
                             .ToArray();

        Assert.Contains("literal:key", templates);
        Assert.Contains("constant:key", templates);
        Assert.Contains("readonly:key", templates);
        Assert.Contains("product:{id}", templates);
        Assert.Contains("order:{orderId}", templates);
        Assert.Contains("tenant:{tenantId}", templates);
        Assert.True(templates.Contains("user:{userId}"), string.Join(Environment.NewLine, templates));
        Assert.Contains("cart:{cartId}", templates);
        Assert.Contains("local:{region}", templates);
        Assert.Contains("property:{RequestRegion}", templates);
        Assert.Contains("unknown:{?}", templates);
        Assert.Contains("five:{id}", templates);
        Assert.Contains("builder:{id}", templates);
        Assert.Single(graph.CacheKeys, key => key.Store == "memory" && key.Template == "product:{id}");
    }

    [Fact]
    public async Task RecordsFullyDynamicAndOverLimitKeysAsUnresolved()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/KeyTemplates.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");
        var unresolved = graph.Unresolved.Where(item => item.Kind == UnresolvedKind.Key).ToArray();

        Assert.True(unresolved.Length == 2, string.Join(Environment.NewLine,
            unresolved.Select(item => $"{item.Snippet}: {item.Reason}")));
        Assert.Contains(unresolved, item => item.Snippet == "dynamicKey");
        Assert.Contains(unresolved, item => item.Snippet == "Six1(id)" &&
                                            item.Reason.Contains("hop limit", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.CacheKeys, key => key.Template.Contains("six", StringComparison.Ordinal));
    }
}
