using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using Xunit;

namespace CacheDetective.Tests.Data;

public sealed class UnparsedSqlTests
{
    [Fact]
    public async Task RecordsEveryUnsupportedSqlSite()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/UnparsedSql.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var unresolved = graph.Unresolved.Where(item => item.Kind == UnresolvedKind.Sql).ToArray();
        Assert.Equal(7, unresolved.Length);
        Assert.Contains(unresolved, item => item.Snippet.Contains("Query<SqlEntity>", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item.Snippet.Contains("_connection.Execute", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item.Snippet == "new SqlCommand(\"select 1\")");
        Assert.Contains(unresolved, item => item.Snippet == "new SqlDataAdapter()");
        Assert.Contains(unresolved, item => item.Snippet.Contains("FromSqlRaw", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item.Snippet.Contains("ExecuteSqlRaw", StringComparison.Ordinal));
        Assert.Contains(unresolved, item => item.Snippet.Contains("CommandType.StoredProcedure",
            StringComparison.Ordinal));
        Assert.All(unresolved, item =>
            Assert.Contains("SQL parsing is out of scope for this phase", item.Reason,
                StringComparison.Ordinal));
    }
}
