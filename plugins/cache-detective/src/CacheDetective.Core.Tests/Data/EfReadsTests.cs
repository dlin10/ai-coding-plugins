using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Data;

public sealed class EfReadsTests
{
    [Fact]
    public async Task ResolvesAttributeFluentConfigurationAndConventionMappings()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/EfReads.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        AssertTable(graph, "catalog.attribute_rows");
        AssertTable(graph, "inventory.fluent_rows");
        AssertTable(graph, "custom.configured_rows");
        AssertTable(graph, "dbo.ConventionRows");
        AssertTable(graph, "dbo.SharedRows");
        Assert.DoesNotContain(graph.Tables, table => table.Name == "ignored.ignored_by_attribute");

        var reads = graph.Edges.OfType<Reads>().Where(edge => edge.To is Table).ToArray();
        Assert.Contains(reads, edge => ((Table)edge.To).Name == "catalog.attribute_rows");
        Assert.Contains(reads, edge => ((Table)edge.To).Name == "inventory.fluent_rows");
        Assert.Contains(reads, edge => ((Table)edge.To).Name == "custom.configured_rows");
        Assert.Contains(reads, edge => ((Table)edge.To).Name == "dbo.ConventionRows");
        Assert.All(reads, edge => Assert.Equal(Confidence.Confirmed, edge.Confidence));
    }

    [Fact]
    public async Task DeduplicatesTheSameTableAcrossSolutions()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/EfReads.cs");
        var first = await new CallGraphIndexer().IndexAsync(solution, "first");
        var second = await new CallGraphIndexer().IndexAsync(solution, "second");
        var workspace = new CacheGraph();

        workspace.ReplaceSolution("first", first);
        workspace.ReplaceSolution("second", second);

        Assert.Single(workspace.Tables, table => table.Name == "dbo.SharedRows");
        var reads = workspace.Edges.OfType<Reads>()
            .Where(edge => edge.To is Table { Name: "dbo.SharedRows" })
            .ToArray();
        Assert.Contains(reads, edge => ((Handler)edge.From).Solution == "first");
        Assert.Contains(reads, edge => ((Handler)edge.From).Solution == "second");
    }

    private static void AssertTable(CacheGraph graph, string name)
    {
        var table = Assert.Single(graph.Tables, table => table.Name == name);
        Assert.Equal("default", table.Database);
    }
}
