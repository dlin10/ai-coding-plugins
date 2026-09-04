using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Data;

public sealed class SqlAnalysisTests
{
    [Fact]
    public async Task ReadsTheTableOutOfAConcatenatedQuery()
    {
        var graph = await IndexAsync();

        var read = Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.To is Table { Name: "dbo.Products" } &&
                    InHandler(edge, "ConcatenatedQuery"));
        Assert.Equal(Confidence.Confirmed, read.Confidence);
    }

    [Fact]
    public async Task SendsAnUnknownTableNameToUnresolvedWithoutFailingTheParser()
    {
        var graph = await IndexAsync();

        var unresolved = Assert.Single(SqlUnresolved(graph),
            item => item.Snippet.Contains("FROM {table}", StringComparison.Ordinal));
        Assert.Contains("unknown table name", unresolved.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be parsed", unresolved.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Edges.OfType<Reads>(),
            edge => InHandler(edge, "UnknownTableName"));
    }

    [Fact]
    public async Task SendsAFailedParseToUnresolvedWithTheParserMessage()
    {
        var graph = await IndexAsync();

        var unresolved = Assert.Single(SqlUnresolved(graph),
            item => item.Snippet.Contains("FROM {schema}.Products", StringComparison.Ordinal));
        Assert.StartsWith("The SQL could not be parsed: ", unresolved.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Edges.OfType<Reads>(),
            edge => InHandler(edge, "UnknownSchemaName"));
    }

    [Fact]
    public async Task CallsTheProcedureNamedInABatchAndWritesTheUpdatedTable()
    {
        var graph = await IndexAsync();

        Assert.Single(graph.Edges.OfType<Calls>(),
            edge => edge.To is StoredProcedure { Name: "dbo.ApplyDiscount" } &&
                    InHandler(edge, "BatchWithProcedure"));
        var write = Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.To is Table { Name: "dbo.Prices" });
        Assert.Equal(Confidence.Confirmed, write.Confidence);
        Assert.Equal([WriteEvent.Update], write.Events);
    }

    [Fact]
    public async Task CallsTheProcedureNamedByADeclaredCommandType()
    {
        var graph = await IndexAsync();

        var call = Assert.Single(graph.Edges.OfType<Calls>(),
            edge => edge.To is StoredProcedure { Name: "dbo.ApplyDiscount" } &&
                    InHandler(edge, "DeclaredProcedureCommand"));
        Assert.Equal(Confidence.Confirmed, call.Confidence);
        Assert.DoesNotContain(SqlUnresolved(graph),
            item => item.Snippet.Contains("CommandType.StoredProcedure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepsTheCacheRoleWhenTheOnlyDataAccessIsAProcedureCall()
    {
        var graph = await IndexAsync();

        var key = Assert.Single(graph.CacheKeys, candidate => candidate.Template == "pricing:hot");
        Assert.Equal("cache", key.Role);
        Assert.Single(graph.Edges.OfType<Calls>(),
            edge => edge.To is StoredProcedure { Name: "dbo.RefreshPrices" });
    }

    private static async Task<CacheGraph> IndexAsync()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/SqlAnalysis.cs");
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }

    private static IEnumerable<Unresolved> SqlUnresolved(CacheGraph graph) =>
        graph.Unresolved.Where(item => item.Kind == UnresolvedKind.Sql);

    /// <summary>The fixture's handlers are named <c>SqlAnalysisController.&lt;method&gt;(&lt;parameters&gt;)</c>.</summary>
    private static bool InHandler(GraphEdge edge, string method) =>
        edge.From is Handler handler &&
        handler.Symbol.Contains($".{method}(", StringComparison.Ordinal);
}
