using CacheDetective.Database;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Tests.Database;
using CacheDetective.Workspaces;
using Xunit;
using Xunit.Abstractions;

namespace CacheDetective.Tests;

/// <summary>
/// The whole tool over the demo stand: the solution loaded through MSBuild, the catalogue read from a
/// live server, and every one of the seven planted cases checked for its own outcome — a finding, an
/// edge and an <c>unresolved</c> row being three different things.
/// <para>This test lives here because loading solutions is the CLI's job (<c>docs/adr/0002</c>), and it
/// is a <em>local</em> gate: no CI job has both MSBuild and a SQL Server. The Windows job has MSBuild and
/// no engine; the Linux job an engine and no <c>CacheDetective.Cli</c>. That is the price named in
/// <c>docs/adr/0008</c>.</para>
/// </summary>
public sealed class DemoStandEndToEndTests(ITestOutputHelper output)
{
    private const string PURPOSE =
        "The demo-stand run is a local gate: it needs MSBuild and a SQL Server in one process, and no CI "
        + "job has both (see docs/adr/0008). Set it to a SQL Server connection string and run this test "
        + "on a developer machine.";

    private const string SOLUTION_NAME = "Shop.slnx";
    private const string DATABASE = "shop";

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Reports_every_planted_case_with_its_own_outcome()
    {
        var solutionPath = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", "Shop.slnx");
        var scriptPath = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", "db", "shop.sql");

        await using var harness = await SqlServerHarness.CreateAsync();
        await harness.ApplyAsync(scriptPath);

        var graph = new CacheGraph();

        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            foreach (var diagnostic in loaded.Diagnostics)
            {
                output.WriteLine($"{diagnostic.Kind}: {diagnostic.Message}");
            }

            var code = await new CallGraphIndexer().IndexAsync(loaded.Solution, SOLUTION_NAME);
            graph.ReplaceSolution(SOLUTION_NAME, code);
        }

        await using var connection = await harness.OpenAsync();
        var catalogue = await new DatabaseIndexer().IndexAsync(connection, DATABASE);
        graph.ReplaceDatabase(DATABASE, catalogue.Graph);

        var findings = new UnguardedWriteRule().Evaluate(graph).ToArray();
        var reported = findings.Where(finding => !finding.Suppressed).ToArray();
        var gaps = ProcedureGaps.Derive(graph);
        foreach (var finding in findings)
        {
            output.WriteLine($"{finding.Confidence} {finding.Table.Name} <- {finding.Handler.Symbol}" +
                             $" (suppressed: {finding.Suppressed})");
        }

        AssertCaseA(reported);
        AssertCaseB(graph);
        AssertCaseC(graph);
        AssertCaseD(graph);
        AssertCaseE(gaps);
        AssertCaseF(reported, graph);
        AssertCaseG(findings, reported);
    }

    /// <summary>Case A: procedure writes Discounts, trigger writes PriceHistory, view reads it, and the
    /// cached key has no TTL and no invalidation. One confirmed finding, subjected to the handler at the
    /// head of the chain rather than to the trigger that performed the write.</summary>
    private static void AssertCaseA(IReadOnlyList<UnguardedWriteFinding> reported)
    {
        var finding = Assert.Single(reported, candidate => candidate.Table.Name == "dbo.PriceHistory");

        Assert.Equal("product:{id}", finding.Key.Template);
        Assert.Equal(Confidence.Confirmed, finding.Confidence);
        Assert.Contains("ApplyDiscount", finding.Handler.Symbol, StringComparison.Ordinal);
        Assert.IsType<Trigger>(finding.Write.From);
        // The chain runs handler -> procedure -> table -> trigger -> table -> view -> key.
        Assert.Contains(finding.Chain, edge => edge is Calls { To: StoredProcedure });
        Assert.Contains(finding.Chain, edge => edge is Fires);
        Assert.Contains(finding.Chain, edge => edge is Reads { From: View });
    }

    /// <summary>Case B: a concatenated value. An edge, not a finding — the substituted parameter lands in
    /// a value position, which cannot change which table the statement touches.</summary>
    private static void AssertCaseB(CacheGraph graph)
    {
        var read = Assert.Single(graph.Edges.OfType<Reads>(),
            edge => edge.To is Table { Name: "dbo.Prices" } &&
                    edge.From is Handler handler &&
                    handler.Symbol.Contains("GetPrices", StringComparison.Ordinal));

        Assert.Equal(Confidence.Confirmed, read.Confidence);
    }

    /// <summary>Case C: the unknown fragment lands where a table name goes. An unresolved row naming the
    /// position — and the parser did not fail, which is why the reason says so and not otherwise.</summary>
    private static void AssertCaseC(CacheGraph graph)
    {
        var unresolved = Assert.Single(graph.Unresolved,
            item => item.Kind == UnresolvedKind.Sql &&
                    item.Snippet.Contains("FROM {table}", StringComparison.Ordinal));

        Assert.Contains("unknown table name", unresolved.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be parsed", unresolved.Reason, StringComparison.Ordinal);
        Assert.EndsWith("DiscountsController.cs", unresolved.File!, StringComparison.Ordinal);
    }

    /// <summary>Case D: dynamic SQL inside a procedure. An unresolved row from the catalogue half,
    /// carrying a database object name rather than a file and a line.</summary>
    private static void AssertCaseD(CacheGraph graph)
    {
        var unresolved = Assert.Single(graph.Unresolved,
            item => item.Site.ObjectName == "dbo.RebuildReport");

        Assert.Equal(UnresolvedKind.Sql, unresolved.Kind);
        Assert.Equal(DATABASE, unresolved.Site.Database);
        Assert.Null(unresolved.File);
        Assert.Contains("dynamic SQL", unresolved.Reason, StringComparison.Ordinal);
    }

    /// <summary>Case E: a procedure the code names and the catalogue does not hold. The second of the two
    /// reasons a procedure vertex is a dead end, derived on query from the graph as it stands now.</summary>
    private static void AssertCaseE(IReadOnlyList<ProcedureGap> gaps)
    {
        var gap = Assert.Single(gaps, candidate => candidate.Procedure == "dbo.RecalculateTax");

        Assert.Equal(UnresolvedKind.Sql, gap.Unresolved.Kind);
        Assert.Contains("dbo.RecalculateTax", gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.Contains(DATABASE, gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.Contains("RecalculateTax", gap.Caller!.Symbol, StringComparison.Ordinal);
    }

    /// <summary>Case F: a hidden write whose head handler invalidates the key itself. No finding — and
    /// because the write is hidden inside the procedure, this is what tests the anchor.</summary>
    private static void AssertCaseF(IReadOnlyList<UnguardedWriteFinding> reported, CacheGraph graph)
    {
        Assert.DoesNotContain(reported, finding => finding.Table.Name == "dbo.Prices");
        // The write really is there, and really is hidden: the absence is the invalidation's work.
        Assert.Single(graph.Edges.OfType<Writes>(),
            edge => edge.From is StoredProcedure { Name: "dbo.ApplyLoyaltyDiscount" } &&
                    edge.To is Table { Name: "dbo.Prices" });
        Assert.Contains(graph.Edges.OfType<Invalidates>(),
            edge => ((CacheKey)edge.To).Template == "price:{id}");
    }

    /// <summary>Case G: written, never invalidated, but the TTL fits the budget. The finding exists and
    /// is suppressed, which is not the same outcome as case F's absence.</summary>
    private static void AssertCaseG(IReadOnlyList<UnguardedWriteFinding> findings,
                                    IReadOnlyList<UnguardedWriteFinding> reported)
    {
        var suppressed = Assert.Single(findings, finding => finding.Table.Name == "dbo.Inventory");

        Assert.True(suppressed.Suppressed);
        Assert.Equal(30, suppressed.TtlSeconds);
        Assert.Equal(60, suppressed.BudgetSeconds);
        Assert.DoesNotContain(reported, finding => finding.Table.Name == "dbo.Inventory");
    }
}
