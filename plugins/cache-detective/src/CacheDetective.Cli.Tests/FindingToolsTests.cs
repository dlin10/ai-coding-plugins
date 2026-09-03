using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests;

public sealed class FindingToolsTests
{
    [Fact]
    public void FindingTools_filters_suppression_and_keeps_ids_stable()
    {
        using var fixture = new FindingFixture();
        var catalog = new FindingCatalog();

        var visible = FindingQueries.FindUnguardedWrites(fixture.Graph, fixture.Budgets, catalog,
            "confirmed", "dbo.Products", "Catalog", false, new PageArguments());
        var includingSuppressed = FindingQueries.FindUnguardedWrites(fixture.Graph, fixture.Budgets, catalog,
            "confirmed", "dbo.Products", "Catalog", true, new PageArguments());
        var repeated = FindingQueries.FindUnguardedWrites(fixture.Graph, fixture.Budgets, catalog,
            "confirmed", "dbo.Products", "Catalog", false, new PageArguments());
        var likely = FindingQueries.FindUnguardedWrites(fixture.Graph, fixture.Budgets, catalog,
            "likely", "dbo.Orders", null, false, new PageArguments());
        var json = JsonSerializer.Serialize(visible,
            CacheDetectiveJsonContext.Default.FindingEnvelope);
        using var document = JsonDocument.Parse(json);

        var unsuppressed = Assert.Single(visible.Items);
        Assert.Equal(1, visible.Suppressed);
        Assert.Equal(1, document.RootElement.GetProperty("suppressed").GetInt32());
        Assert.Equal(2, includingSuppressed.Total);
        Assert.Equal(1, includingSuppressed.Suppressed);
        var suppressed = Assert.Single(includingSuppressed.Items, item => item.Suppressed);
        Assert.Equal(30, suppressed.Ttl);
        Assert.Equal(60, suppressed.Budget);
        Assert.Equal(unsuppressed.Id, Assert.Single(repeated.Items).Id);
        Assert.Equal("likely", Assert.Single(likely.Items).Confidence);
    }

    [Fact]
    public void FindingTools_find_issues_covers_all_rules_and_pages_past_first()
    {
        using var fixture = new FindingFixture();
        var catalog = new FindingCatalog();

        var all = FindingQueries.FindIssues(fixture.Graph, fixture.Budgets, catalog,
            null, null, true, new PageArguments { Page = 1, PageSize = 50 });
        var patterns = FindingQueries.FindIssues(fixture.Graph, fixture.Budgets, catalog,
            "pattern_mismatch", null, false, new PageArguments());
        var first = FindingQueries.FindIssues(fixture.Graph, fixture.Budgets, catalog,
            null, null, true, new PageArguments { Page = 1, PageSize = 1 });
        var second = FindingQueries.FindIssues(fixture.Graph, fixture.Budgets, catalog,
            null, null, true, new PageArguments { Page = 2, PageSize = 1 });

        Assert.Contains(all.Items, item => item.Rule == "UNGUARDED_WRITE");
        Assert.Contains(all.Items, item => item.Rule == "ORPHAN_INVALIDATION");
        Assert.Contains(all.Items, item => item.Rule == "PATTERN_MISMATCH");
        var pattern = Assert.Single(patterns.Items);
        Assert.Equal("product:{id}", pattern.ExpectedTemplate);
        Assert.Equal(1, pattern.Distance);
        Assert.True(first.Pages > 1);
        Assert.NotEqual(Assert.Single(first.Items).Id, Assert.Single(second.Items).Id);
        Assert.Equal(1, first.Suppressed);
        Assert.Equal(1, second.Suppressed);
    }

    [Fact]
    public void FindingTools_get_unresolved_includes_ten_lines_on_each_side()
    {
        using var fixture = new FindingFixture();
        fixture.Graph.AddUnresolved(UnresolvedKind.Sql, "Catalog", fixture.SourcePath, 25,
            "FromSqlRaw(sql)", "SQL parsing is out of scope for this phase.");

        var result = FindingQueries.GetUnresolved(fixture.Graph, fixture.Root, "sql", new PageArguments());
        var item = Assert.Single(result.Items);

        Assert.Equal("sql", item.Kind);
        Assert.Equal("FromSqlRaw(sql)", item.Snippet);
        Assert.Equal(21, item.Context.Count);
        Assert.Equal(15, item.Context[0].Line);
        Assert.Equal(35, item.Context[^1].Line);
    }

    [Fact]
    public void FindingTools_get_evidence_pages_a_finding_chain()
    {
        using var fixture = new FindingFixture();
        var catalog = new FindingCatalog();
        var findings = FindingQueries.FindUnguardedWrites(fixture.Graph, fixture.Budgets, catalog,
            "confirmed", "dbo.Products", "Catalog", false, new PageArguments());
        var findingId = Assert.Single(findings.Items).Id;

        var first = FindingQueries.GetEvidence(fixture.Graph, fixture.Budgets, fixture.Root, catalog,
            findingId, new PageArguments { Page = 1, PageSize = 2 });
        var second = FindingQueries.GetEvidence(fixture.Graph, fixture.Budgets, fixture.Root, catalog,
            findingId, new PageArguments { Page = 2, PageSize = 2 });

        Assert.Equal(findingId, first.FindingId);
        Assert.True(first.Fragments.Pages > 1);
        Assert.Equal(2, first.Fragments.Items.Count);
        Assert.Equal(2, second.Fragments.Items.Count);
        Assert.NotEqual(first.Fragments.Items[0].Order, second.Fragments.Items[0].Order);
        Assert.All(first.Fragments.Items, fragment => Assert.NotEmpty(fragment.Context));
    }

    private sealed class FindingFixture : IDisposable
    {
        public FindingFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"cache-detective-findings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "Handlers.cs");
            File.WriteAllLines(SourcePath, Enumerable.Range(1, 50).Select(line => $"// line {line}"));
            Graph = BuildGraph(SourcePath);
        }

        public string Root { get; }
        public string SourcePath { get; }
        public CacheGraph Graph { get; }
        public IReadOnlyDictionary<string, double> Budgets { get; } =
            new Dictionary<string, double> { ["dbo.Products"] = 60 };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static CacheGraph BuildGraph(string sourcePath)
        {
            var graph = new CacheGraph();
            var products = new Table("dbo.Products", "default");
            var orders = new Table("dbo.Orders", "default");
            var writeProducts = Handler("Products.Put", sourcePath, 2);
            var writeOrders = Handler("Orders.Put", sourcePath, 3);
            var cacheProduct = Handler("Products.Get", sourcePath, 10);
            var middle = Handler("Products.Load", sourcePath, 11);
            var reader = Handler("Products.Query", sourcePath, 12);
            var cacheShort = Handler("Products.GetShort", sourcePath, 13);
            var cacheOrder = Handler("Orders.Get", sourcePath, 14);
            var invalidator = Handler("Products.Invalidate", sourcePath, 15);
            var productKey = new CacheKey("product:{id}", "memory", null, [], "cache");
            var shortKey = new CacheKey("product-short:{id}", "memory", TimeSpan.FromSeconds(30), [], "cache");
            var orderKey = new CacheKey("order:{id}", "memory", null, [], "cache");
            var typo = new CacheKey("producx:{id}", "memory", null, [], null);

            graph.AddEdge(new Writes(writeProducts, products, Confidence.Confirmed, [At(sourcePath, 2)]));
            graph.AddEdge(new Writes(writeOrders, orders, Confidence.Confirmed, [At(sourcePath, 3)]));
            graph.AddEdge(new Caches(cacheProduct, productKey, Confidence.Confirmed, [At(sourcePath, 10)]));
            graph.AddEdge(new Calls(cacheProduct, middle, Confidence.Confirmed, [At(sourcePath, 11)]));
            graph.AddEdge(new Calls(middle, reader, Confidence.Confirmed, [At(sourcePath, 12)]));
            graph.AddEdge(new Reads(reader, products, Confidence.Confirmed, [At(sourcePath, 13)]));
            graph.AddEdge(new Caches(cacheShort, shortKey, Confidence.Confirmed, [At(sourcePath, 14)]));
            graph.AddEdge(new Reads(cacheShort, products, Confidence.Confirmed, [At(sourcePath, 15)]));
            graph.AddEdge(new Caches(cacheOrder, orderKey, Confidence.Confirmed, [At(sourcePath, 16)]));
            graph.AddEdge(new Reads(cacheOrder, orders, Confidence.Likely, [At(sourcePath, 17)]));
            graph.AddEdge(new Invalidates(invalidator, typo, Confidence.Confirmed,
                [At(sourcePath, 18)], CacheSemantic.Remove));
            return graph;
        }

        private static Handler Handler(string symbol, string file, int line) =>
            new("Catalog", symbol, "controller", file, line);

        private static Evidence At(string file, int line) => new(file, line);
    }
}
