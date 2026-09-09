using CacheDetective.Database;
using CacheDetective.Graph;
using Microsoft.Data.SqlClient;
using Xunit;

namespace CacheDetective.Tests.Database;

/// <summary>
/// The catalogue queries against a real server. Everything the fakes cannot answer lives here: whether
/// <c>sys.dm_sql_referenced_entities</c> really separates a trigger's write from its read, whether the
/// script is really idempotent, and whether the indexer really needs no more rights than the README asks
/// for. Skipped, not quietly passed, when <c>CD_TEST_SQL_CONN</c> is unset.
/// </summary>
public sealed class DatabaseIntegrationTests
{
    private const string DATABASE = "shop";

    private const string PURPOSE =
        "Set it to a SQL Server connection string to read a live catalogue. The Linux CI job sets it "
        + "from its mssql service container; the Windows job cannot, because that runner image ships no "
        + "SQL Server engine (see docs/adr/0008).";

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Reads_the_demo_catalogue_under_a_login_with_no_table_access()
    {
        await using var harness = await SqlServerHarness.CreateAsync();
        var script = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", "db", "shop.sql");

        // Applied twice on purpose: this is the idempotency check for the demo script, made against a
        // server rather than taken on trust. A second run must leave the same catalogue.
        await harness.ApplyAsync(script);
        await harness.ApplyAsync(script);
        await AddCallChainAsync(harness);

        var restricted = await harness.GrantCatalogueOnlyLoginAsync();
        await AssertCannotReadUserTablesAsync(harness, restricted);

        await using var connection = await harness.OpenAsync(restricted);
        var result = await new DatabaseIndexer().IndexAsync(connection, DATABASE);
        var graph = result.Graph;

        AssertProcedures(graph);
        AssertTrigger(graph);
        AssertView(graph);
        AssertTransitiveClosure(graph);
        AssertDynamicSqlIsUnresolved(graph);
        Assert.Empty(result.UnresolvableObjects);
    }

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Applying_the_demo_script_twice_leaves_one_of_each_object()
    {
        await using var harness = await SqlServerHarness.CreateAsync();
        var script = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", "db", "shop.sql");

        await harness.ApplyAsync(script);
        var afterFirst = await CountObjectsAsync(harness);
        await harness.ApplyAsync(script);
        var afterSecond = await CountObjectsAsync(harness);

        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(5, afterFirst["U"]);
        Assert.Equal(3, afterFirst["P"]);
        Assert.Equal(1, afterFirst["TR"]);
        Assert.Equal(1, afterFirst["V"]);
    }

    private static void AssertProcedures(CacheGraph graph)
    {
        var write = Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.From is StoredProcedure { Name: "dbo.ApplyDiscount" });
        Assert.Equal("dbo.Discounts", Assert.IsType<Table>(write.To).Name);
        Assert.Equal(Confidence.Confirmed, write.Confidence);
        // The catalogue never says which operation a write was, so all three events, still confirmed.
        Assert.Equal(3, write.Events.Count);
        Assert.Equal(DATABASE, Assert.IsType<Table>(write.To).Database);

        Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.From is StoredProcedure { Name: "dbo.ApplyLoyaltyDiscount" } &&
                    edge.To is Table { Name: "dbo.Prices" });
    }

    /// <summary>The trigger's host table and events come from <c>sys.triggers</c>; what its body writes
    /// comes only from <c>sys.dm_sql_referenced_entities</c>, which is the whole reason that function is
    /// applied to triggers and not just to procedures.</summary>
    private static void AssertTrigger(CacheGraph graph)
    {
        var trigger = Assert.Single(graph.Triggers);
        Assert.Equal("dbo.trg_Discounts_Audit", trigger.Name);
        Assert.Equal("dbo.Discounts", trigger.Table);
        Assert.Equal([WriteEvent.Insert, WriteEvent.Update], trigger.Events.Order());

        var fires = Assert.Single(graph.Edges.OfType<Fires>());
        Assert.Equal("dbo.Discounts", Assert.IsType<Table>(fires.From).Name);

        Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.From is Trigger && edge.To is Table { Name: "dbo.PriceHistory" });
        Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.From is Trigger && edge.To is Table { Name: "dbo.Prices" });
    }

    private static void AssertView(CacheGraph graph)
    {
        var view = Assert.Single(graph.Views);
        Assert.Equal("dbo.vw_ProductCard", view.Name);
        Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.From is View && edge.To is Table { Name: "dbo.Products" });
        Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.From is View && edge.To is Table { Name: "dbo.PriceHistory" });
        Assert.DoesNotContain(graph.Edges.OfType<Writes>(), edge => edge.From is View);
    }

    private static void AssertTransitiveClosure(CacheGraph graph)
    {
        Assert.Single(graph.Edges.OfType<Calls>(),
            edge => edge.From is StoredProcedure { Name: "dbo.TestOuter" } &&
                    edge.To is StoredProcedure { Name: "dbo.TestMiddle" });
        Assert.Single(graph.Edges.OfType<Calls>(),
            edge => edge.From is StoredProcedure { Name: "dbo.TestMiddle" } &&
                    edge.To is StoredProcedure { Name: "dbo.TestInner" });
        Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.From is StoredProcedure { Name: "dbo.TestInner" } &&
                    edge.To is Table { Name: "dbo.Inventory" });
    }

    private static void AssertDynamicSqlIsUnresolved(CacheGraph graph)
    {
        var unresolved = Assert.Single(graph.Unresolved,
            item => item.Site.ObjectName == "dbo.RebuildReport");
        Assert.Equal(UnresolvedKind.Sql, unresolved.Kind);
        Assert.Equal(DATABASE, unresolved.Site.Database);
        Assert.Null(unresolved.Site.File);
        Assert.Contains("dynamic SQL", unresolved.Reason, StringComparison.Ordinal);
    }

    /// <summary>A procedure chain the demo stand has no use for, created here so the live run exercises
    /// the transitive closure the fakes cover in <see cref="DatabaseIndexerTests"/>.</summary>
    private static async Task AddCallChainAsync(SqlServerHarness harness)
    {
        // One command per procedure, and not one batch with EXEC(N'CREATE PROCEDURE …') wrappers.
        // CREATE PROCEDURE has no terminator: everything after AS belongs to the body until the batch
        // ends, so wrapped CREATEs are swallowed into the first procedure and the other two are never
        // created — leaving no procedure-to-procedure call for this test to find, which is exactly how
        // it failed. Innermost first, so each callee exists when its caller is compiled.
        string[] procedures =
        [
            """
            CREATE PROCEDURE dbo.TestInner AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE dbo.Inventory SET OnHand = OnHand + 1;
            END;
            """,
            "CREATE PROCEDURE dbo.TestMiddle AS BEGIN SET NOCOUNT ON; EXEC dbo.TestInner; END;",
            "CREATE PROCEDURE dbo.TestOuter AS BEGIN SET NOCOUNT ON; EXEC dbo.TestMiddle; END;"
        ];

        await using var connection = await harness.OpenAsync();
        foreach (var procedure in procedures)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = procedure;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Proves the login really is what the test claims: without this, an indexer that quietly
    /// read a user table would look fine.</summary>
    private static async Task AssertCannotReadUserTablesAsync(SqlServerHarness harness, string restricted)
    {
        await using var connection = await harness.OpenAsync(restricted);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (1) Id FROM dbo.Products;";

        var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());

        Assert.Contains("permission", denied.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, int>> CountObjectsAsync(SqlServerHarness harness)
    {
        await using var connection = await harness.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RTRIM(o.type), COUNT_BIG(*)
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'P', 'TR', 'V')
            GROUP BY o.type;
            """;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["U"] = 0, ["P"] = 0, ["TR"] = 0, ["V"] = 0
        };
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        }

        return counts;
    }

}
