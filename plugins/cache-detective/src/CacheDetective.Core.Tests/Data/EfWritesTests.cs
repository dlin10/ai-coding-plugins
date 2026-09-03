using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Data;

public sealed class EfWritesTests
{
    [Fact]
    public async Task DetectsTrackedEntityWritePatterns()
    {
        var graph = await IndexAsync();

        AssertWrite(graph, "Add()", "dbo.AddRows", Confidence.Confirmed);
        AssertWrite(graph, "AddRange()", "dbo.AddRangeRows", Confidence.Confirmed);
        AssertWrite(graph, "Update()", "dbo.UpdateRows", Confidence.Confirmed);
        AssertWrite(graph, "UpdateRange()", "dbo.UpdateRangeRows", Confidence.Confirmed);
        AssertWrite(graph, "Remove()", "dbo.RemoveRows", Confidence.Confirmed);
        AssertWrite(graph, "RemoveRange()", "dbo.RemoveRangeRows", Confidence.Confirmed);
        AssertWrite(graph, "AttachThenAssign()", "dbo.AttachRows", Confidence.Confirmed);
        AssertWrite(graph, "AssignQueriedEntity()", "dbo.AssignedRows", Confidence.Confirmed);
        AssertWrite(graph, "AssignFoundEntity()", "dbo.FindRows", Confidence.Confirmed);
        AssertWrite(graph, "SetEntryState()", "dbo.EntryRows", Confidence.Confirmed);
        AssertWrite(graph, "SaveChangesAsyncWrite()", "dbo.AsyncSaveRows", Confidence.Confirmed);
    }

    [Fact]
    public async Task DetectsImmediateBulkWritesWithoutSaveChanges()
    {
        var graph = await IndexAsync();

        AssertWrite(graph, "ExecuteUpdateOnly()", "dbo.ExecuteUpdateRows", Confidence.Confirmed);
        AssertWrite(graph, "ExecuteUpdateAsyncOnly()", "dbo.ExecuteUpdateAsyncRows", Confidence.Confirmed);
        AssertWrite(graph, "ExecuteDeleteOnly()", "dbo.ExecuteDeleteRows", Confidence.Confirmed);
        AssertWrite(graph, "ExecuteDeleteAsyncOnly()", "dbo.ExecuteDeleteAsyncRows", Confidence.Confirmed);
    }

    [Fact]
    public async Task ResolvesNavigationWritesAndLikelyFallback()
    {
        var graph = await IndexAsync();

        AssertWrite(graph, "AddNavigationChild()", "dbo.OrderLines", Confidence.Confirmed);
        AssertWrite(graph, "LikelyMutation()", "dbo.LikelyRows", Confidence.Likely);
    }

    [Fact]
    public async Task PropagatesMutationAndSaveFactsThroughCalls()
    {
        var graph = await IndexAsync();

        AssertWrite(graph, "ReachesMutationAndSave()", "dbo.HelperRows", Confidence.Confirmed);
        AssertWrite(graph, "MutationThenReachedSave()", "dbo.CrossMethodRows", Confidence.Confirmed);
    }

    private static async Task<CacheGraph> IndexAsync()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/EfWrites.cs");
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }

    private static void AssertWrite(CacheGraph graph, string handlerName, string tableName,
                                    Confidence confidence)
    {
        var edge = Assert.Single(graph.Edges.OfType<Writes>(), candidate =>
            ((Handler)candidate.From).Symbol.Contains(handlerName, StringComparison.Ordinal) &&
            ((Table)candidate.To).Name == tableName);
        Assert.Equal(confidence, edge.Confidence);
        Assert.Equal("default", ((Table)edge.To).Database);
        Assert.NotEmpty(edge.Evidence);
    }
}
