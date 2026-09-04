using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Data;

/// <summary>The raw-SQL call sites phase 1 recorded wholesale now split three ways: a text that parses and
/// names tables becomes edges, a text the grammar rejects becomes <c>unresolved</c>, and a site whose
/// command text cannot be found stays <c>unresolved</c> for that reason alone.</summary>
public sealed class UnparsedSqlTests
{
    [Fact]
    public async Task BuildsEdgesFromTheSitesWhoseSqlParses()
    {
        var graph = await IndexAsync();

        var read = Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.To is Table { Name: "dbo.rows" });
        Assert.Equal(Confidence.Confirmed, read.Confidence);
        var write = Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.To is Table { Name: "dbo.rows" });
        Assert.Equal(Confidence.Confirmed, write.Confidence);
        Assert.Equal([WriteEvent.Delete], write.Events);
    }

    [Fact]
    public async Task RecordsOnlyTheSitesThatStayUnresolvedAndWhy()
    {
        var graph = await IndexAsync();

        var unresolved = graph.Unresolved.Where(item => item.Kind == UnresolvedKind.Sql).ToArray();
        Assert.True(unresolved.Length == 3, string.Join(Environment.NewLine,
            unresolved.Select(item => $"{item.Snippet}: {item.Reason}")));

        var rejected = Assert.Single(unresolved,
            item => item.Snippet.Contains("_connection.Execute", StringComparison.Ordinal));
        Assert.StartsWith("The SQL could not be parsed: ", rejected.Reason, StringComparison.Ordinal);

        var textless = Assert.Single(unresolved, item => item.Snippet == "new SqlDataAdapter()");
        Assert.Equal("The SQL command text could not be found.", textless.Reason);

        var procedure = Assert.Single(unresolved,
            item => item.Snippet.Contains("CommandType.StoredProcedure", StringComparison.Ordinal));
        Assert.Contains("unknown procedure name", procedure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesNothingBehindForSqlThatParsesToNoTable()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(graph.Unresolved,
            item => item.Kind == UnresolvedKind.Sql &&
                    (item.Snippet.Contains("Query<SqlEntity>", StringComparison.Ordinal) ||
                     item.Snippet == "new SqlCommand(\"select 1\")"));
    }

    private static async Task<CacheGraph> IndexAsync()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/UnparsedSql.cs");
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }
}
