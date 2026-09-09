using System.Globalization;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>The shape a finding reaches a reader in. The report is assembled from what
/// <c>find_issues</c> and <c>get_evidence</c> return, so this renders one finding the way
/// <c>skills/scan/report-template.md</c> prescribes and pins the result whole: a reordered chain, a
/// reversed arrow, a lost <c>file:line</c> or a lost TTL or budget line changes the string and fails
/// here, rather than in a reader's issue tracker.</summary>
public sealed class ReportContractTests
{
    private const string DATABASE = "shop";
    private const string SOLUTION = "Shop.slnx";
    private const string FILE = "Catalog/ProductsController.cs";

    [Fact]
    public void Unguarded_write_renders_the_whole_finding()
    {
        var rendered = Render(BuildGraph(TimeSpan.FromSeconds(300)));

        Assert.Equal(string.Join('\n',
            "UNGUARDED_WRITE — confirmed — Shop.slnx",
            "TTL: 300s",
            "Budget: 60s",
            "1 writes Catalog/ProductsController.cs:44 handler:Shop.slnx/Catalog.ProductsController.Rename(int, string) -> table:dbo.Products",
            "2 reads Catalog/ProductsController.cs:22 handler:Shop.slnx/Catalog.ProductsController.Get(int) -> table:dbo.Products",
            "3 caches Catalog/ProductsController.cs:24 handler:Shop.slnx/Catalog.ProductsController.Get(int) -> key:memory/product:{id}"),
            rendered);
    }

    /// <summary>A key with no TTL still renders a TTL line. Dropping the line rather than saying "none"
    /// is how a reader stops being able to tell an unbounded key from one they never asked about.
    /// </summary>
    [Fact]
    public void A_key_without_a_ttl_still_renders_a_ttl_line()
    {
        var rendered = Render(BuildGraph(null));

        Assert.Contains("TTL: none\nBudget: 60s\n", rendered, StringComparison.Ordinal);
    }

    /// <summary>A database object carries no file and no line, so its chain line carries its own name
    /// instead. Neither form may be left off: a line with no site at all is a line a reader cannot act
    /// on.</summary>
    [Fact]
    public void A_hidden_write_renders_the_procedure_by_name_where_a_file_would_be()
    {
        var graph = BuildGraph(TimeSpan.FromSeconds(300), throughProcedure: true);

        var rendered = Render(graph);

        Assert.Equal(string.Join('\n',
            "UNGUARDED_WRITE — confirmed — Shop.slnx",
            "TTL: 300s",
            "Budget: 60s",
            "1 calls Catalog/ProductsController.cs:44 handler:Shop.slnx/Catalog.ProductsController.Rename(int, string) -> procedure:dbo.RenameProduct",
            "2 writes shop.dbo.RenameProduct procedure:dbo.RenameProduct -> table:dbo.Products",
            "3 reads Catalog/ProductsController.cs:22 handler:Shop.slnx/Catalog.ProductsController.Get(int) -> table:dbo.Products",
            "4 caches Catalog/ProductsController.cs:24 handler:Shop.slnx/Catalog.ProductsController.Get(int) -> key:memory/product:{id}"),
            rendered);
    }

    private static string Render(CacheGraph graph)
    {
        var budgets = new Dictionary<string, double> { ["dbo.Products"] = 60 };
        var catalog = new FindingCatalog();
        var snapshot = Assert.Single(catalog.GetAll(graph, budgets));
        var evidence = FindingQueries.GetEvidence(graph, budgets, null, catalog, snapshot.Item.Id, new PageArguments());

        var item = snapshot.Item;
        var lines = new List<string>
        {
            $"{item.Rule} — {item.Confidence} — {item.Solution}",
            $"TTL: {Seconds(item.Ttl)}",
            $"Budget: {Seconds(item.Budget)}"
        };
        lines.AddRange(evidence.Fragments.Items.Select(fragment =>
            $"{fragment.Order} {fragment.Edge} {Site(fragment)} {fragment.From} -> {fragment.To}"));
        return string.Join('\n', lines);
    }

    private static string Seconds(double? value) =>
        value is null ? "none" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}s";

    private static string Site(FindingEvidenceFragment fragment) => fragment.File is not null
        ? $"{fragment.File}:{fragment.Line}"
        : fragment.Database is null ? fragment.ObjectName! : $"{fragment.Database}.{fragment.ObjectName}";

    /// <summary>One handler caches a key over one table; another writes that table and invalidates
    /// nothing — directly, or through a procedure when <paramref name="throughProcedure"/> is set.
    /// </summary>
    private static CacheGraph BuildGraph(TimeSpan? ttl, bool throughProcedure = false)
    {
        var graph = new CacheGraph();
        var products = new Table("dbo", "Products", DATABASE);
        var reader = new Handler(SOLUTION, "Catalog.ProductsController.Get(int)", "controller", FILE, 20);
        var writer = new Handler(SOLUTION, "Catalog.ProductsController.Rename(int, string)", "controller", FILE, 40);

        graph.AddEdge(new Caches(reader, new CacheKey("product:{id}", "memory", ttl, [], "cache"),
            Confidence.Confirmed, [new Evidence(FILE, 24)]));
        graph.AddEdge(new Reads(reader, products, Confidence.Confirmed, [new Evidence(FILE, 22)]));

        if (throughProcedure)
        {
            var procedure = new StoredProcedure("dbo.RenameProduct");
            graph.AddEdge(new Calls(writer, procedure, Confidence.Confirmed, [new Evidence(FILE, 44)]));
            graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed,
                [Evidence.InDatabase("dbo.RenameProduct", DATABASE)], [WriteEvent.Update]));
        }
        else
        {
            graph.AddEdge(new Writes(writer, products, Confidence.Confirmed, [new Evidence(FILE, 44)],
                [WriteEvent.Update]));
        }

        return graph;
    }
}
