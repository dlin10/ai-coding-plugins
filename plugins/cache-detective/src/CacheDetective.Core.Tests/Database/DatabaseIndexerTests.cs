using CacheDetective.Database;
using CacheDetective.Graph;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace CacheDetective.Tests.Database;

public sealed class DatabaseIndexerTests
{
    private const string DATABASE = "shop";

    [Fact]
    public async Task Closes_over_the_procedure_call_graph()
    {
        var catalogue = new FakeCatalogue();
        AddProcedure(catalogue, "ApplyDiscount");
        AddProcedure(catalogue, "RecalculatePrices");
        AddProcedure(catalogue, "WritePriceHistory");
        catalogue.ProcedureCalls.Add(("dbo.ApplyDiscount", "dbo.RecalculatePrices"));
        catalogue.ProcedureCalls.Add(("dbo.RecalculatePrices", "dbo.WritePriceHistory"));
        // A dependency on a table is not a call, and must not become one.
        catalogue.ProcedureCalls.Add(("dbo.ApplyDiscount", "dbo.Products"));
        catalogue.References["dbo.WritePriceHistory"] = [("dbo", "PriceHistory", false, true)];

        var graph = await IndexAsync(catalogue);

        var calls = graph.Edges.OfType<Calls>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains(calls, edge => Named(edge.From, "dbo.ApplyDiscount") &&
                                       Named(edge.To, "dbo.RecalculatePrices"));
        Assert.Contains(calls, edge => Named(edge.From, "dbo.RecalculatePrices") &&
                                       Named(edge.To, "dbo.WritePriceHistory"));
        var write = Assert.Single(graph.Edges.OfType<Writes>());
        Assert.True(Named(write.From, "dbo.WritePriceHistory"));
        Assert.Equal("dbo.PriceHistory", Assert.IsType<Table>(write.To).Name);
    }

    [Fact]
    public async Task Cuts_a_cycle_in_the_procedure_call_graph()
    {
        var catalogue = new FakeCatalogue();
        AddProcedure(catalogue, "Left");
        AddProcedure(catalogue, "Right");
        catalogue.ProcedureCalls.Add(("dbo.Left", "dbo.Right"));
        catalogue.ProcedureCalls.Add(("dbo.Right", "dbo.Left"));
        catalogue.ProcedureCalls.Add(("dbo.Left", "dbo.Left"));

        var graph = await IndexAsync(catalogue);

        var calls = graph.Edges.OfType<Calls>().ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Single(calls, edge => Named(edge.From, "dbo.Left") && Named(edge.To, "dbo.Right"));
        Assert.Single(calls, edge => Named(edge.From, "dbo.Right") && Named(edge.To, "dbo.Left"));
        Assert.Single(calls, edge => Named(edge.From, "dbo.Left") && Named(edge.To, "dbo.Left"));
    }

    /// <summary>The walk stops at the same depth as the code call graph, and loses nothing by it: every
    /// procedure in the catalogue is also a root, so a chain longer than the limit still yields its edges.</summary>
    [Fact]
    public async Task Keeps_every_edge_of_a_chain_longer_than_the_depth_limit()
    {
        var catalogue = new FakeCatalogue();
        for (var index = 0; index < 20; index++)
        {
            AddProcedure(catalogue, $"Step{index:00}");
            if (index > 0)
            {
                catalogue.ProcedureCalls.Add(($"dbo.Step{index - 1:00}", $"dbo.Step{index:00}"));
            }
        }

        var graph = await IndexAsync(catalogue);

        Assert.Equal(19, graph.Edges.OfType<Calls>().Count());
        Assert.Equal(20, graph.StoredProcedures.Count);
    }

    /// <summary>A procedure whose body is all dynamic SQL references nothing statically, so it has no
    /// edges. It still has to enter the graph: otherwise the query layer cannot tell it from a procedure
    /// this database does not hold, and would report it as absent while the indexer's own row names it.</summary>
    [Fact]
    public async Task Records_a_procedure_the_catalogue_listed_even_when_it_references_nothing()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Procedures.Add(("dbo", "RebuildReport",
            "CREATE PROCEDURE dbo.RebuildReport AS\nEXEC sp_executesql @statement;\n"));

        var graph = await IndexAsync(catalogue);

        var procedure = Assert.Single(graph.StoredProcedures);
        Assert.Equal("dbo.RebuildReport", procedure.Name);
        Assert.Equal(DATABASE, procedure.Database);
        Assert.Empty(graph.Edges);
        Assert.Single(graph.Unresolved, item => item.Site.ObjectName == "dbo.RebuildReport");
    }

    [Fact]
    public async Task Records_a_view_that_references_nothing()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Views.Add(("dbo", "vw_Empty"));

        var graph = await IndexAsync(catalogue);

        var view = Assert.Single(graph.Views);
        Assert.Equal("dbo.vw_Empty", view.Name);
        Assert.Equal(DATABASE, view.Database);
        Assert.DoesNotContain(graph.Edges, edge => edge.From is View source && source.Name == view.Name);
    }

    [Fact]
    public async Task A_catalogue_view_and_a_view_met_from_code_are_one_vertex_and_reach_its_tables()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Views.Add(("dbo", "vw_ProductCard"));
        catalogue.References["dbo.vw_ProductCard"] = [("dbo", "Products", true, false)];
        var graph = await IndexAsync(catalogue);
        var handler = new Handler("Catalog", "Products.Get", "controller", "Products.cs", 1);
        var key = new CacheKey("product:{id}", "memory", null, [], "cache");

        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed, [new Evidence("Products.cs", 1)]));
        graph.AddEdge(new Reads(handler, new Table("dbo", "vw_ProductCard", DATABASE), Confidence.Confirmed,
            [new Evidence("Products.cs", 2)]));

        Assert.Single(graph.Views, view => view.Name == "dbo.vw_ProductCard");
        var dependency = Assert.Single(graph.DependsOn(key));
        Assert.Equal("dbo.Products", Assert.IsType<Table>(dependency.Target).Name);
        Assert.Contains(dependency.Path, edge => edge.From is View { Name: "dbo.vw_ProductCard" });
    }

    [Fact]
    public async Task Every_issued_command_touches_only_sys_objects()
    {
        var catalogue = new FakeCatalogue();
        AddProcedure(catalogue, "ApplyDiscount");
        catalogue.Views.Add(("dbo", "vw_ProductCard"));
        catalogue.Triggers.Add(("dbo", "trg_Discounts_Audit", "Discounts", "INSERT"));
        catalogue.References["dbo.ApplyDiscount"] = [("dbo", "Products", true, true)];

        await IndexAsync(catalogue);

        Assert.Equal(6, catalogue.CommandTexts.Distinct(StringComparer.Ordinal).Count());

        var everyName = new List<string?>();
        foreach (var text in catalogue.CommandTexts)
        {
            var parser = new TSql180Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(text), out var errors);
            Assert.Empty(errors);

            var walker = new ObjectNameWalker();
            fragment.Accept(walker);
            // Reading nothing at all is safe, and one command genuinely does that: the permission probe
            // is a bare scalar function with no FROM. What must never happen is naming something outside
            // sys, which is asserted for every command; the vacuity guard moves below, where it belongs.
            Assert.All(walker.Names, name => Assert.Equal("sys", name));
            Assert.Empty(walker.Executions);
            everyName.AddRange(walker.Names);
        }

        Assert.NotEmpty(everyName);
    }

    [Fact]
    public async Task Splits_a_views_reads_from_a_triggers_writes()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Views.Add(("dbo", "vw_ProductCard"));
        catalogue.References["dbo.vw_ProductCard"] = [("dbo", "PriceHistory", true, false)];
        catalogue.Triggers.Add(("dbo", "trg_Discounts_Audit", "Discounts", "INSERT"));
        catalogue.Triggers.Add(("dbo", "trg_Discounts_Audit", "Discounts", "UPDATE"));
        catalogue.References["dbo.trg_Discounts_Audit"] = [("dbo", "PriceHistory", false, true)];

        var graph = await IndexAsync(catalogue);

        var read = Assert.Single(graph.Edges.OfType<Reads>());
        Assert.Equal("dbo.vw_ProductCard", Assert.IsType<View>(read.From).Name);
        Assert.Equal("dbo.PriceHistory", Assert.IsType<Table>(read.To).Name);
        Assert.Equal(Confidence.Confirmed, read.Confidence);

        var write = Assert.Single(graph.Edges.OfType<Writes>());
        Assert.Equal("dbo.trg_Discounts_Audit", Assert.IsType<Trigger>(write.From).Name);
        Assert.Equal(Confidence.Confirmed, write.Confidence);
        Assert.Equal([WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete], write.Events.Order());

        var fires = Assert.Single(graph.Edges.OfType<Fires>());
        Assert.Equal("dbo.Discounts", Assert.IsType<Table>(fires.From).Name);
        var trigger = Assert.IsType<Trigger>(fires.To);
        Assert.Equal("dbo.Discounts", trigger.Table);
        Assert.Equal([WriteEvent.Insert, WriteEvent.Update], trigger.Events.Order());
    }

    [Fact]
    public async Task Records_dynamic_sql_and_an_unresolvable_object_as_unresolved()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Procedures.Add(("dbo", "BuildReport",
            "CREATE PROCEDURE dbo.BuildReport AS\nEXEC sp_executesql @statement = @sql;\n"));
        AddProcedure(catalogue, "NamesAGhost");
        catalogue.Unresolvable.Add("dbo.NamesAGhost");

        var graph = await IndexAsync(catalogue);

        var dynamicSql = Assert.Single(graph.Unresolved,
            item => item.Site.ObjectName == "dbo.BuildReport");
        Assert.Equal(UnresolvedKind.Sql, dynamicSql.Kind);
        Assert.Equal(DATABASE, dynamicSql.Site.Database);
        Assert.Null(dynamicSql.Site.File);
        Assert.Null(dynamicSql.Solution);
        Assert.Contains("dynamic SQL", dynamicSql.Reason, StringComparison.Ordinal);
        Assert.Contains("sp_executesql", dynamicSql.Snippet, StringComparison.Ordinal);

        var ghost = Assert.Single(graph.Unresolved, item => item.Site.ObjectName == "dbo.NamesAGhost");
        Assert.Contains("does not exist", ghost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_so_when_it_cannot_see_the_dependencies_instead_of_reporting_none()
    {
        var catalogue = new FakeCatalogue { CanSeeDependencies = false };
        AddProcedure(catalogue, "Outer");
        AddProcedure(catalogue, "Inner");
        catalogue.ProcedureCalls.Add(("dbo.Outer", "dbo.Inner"));

        var graph = await IndexAsync(catalogue);

        var gap = Assert.Single(graph.Unresolved,
            item => item.Reason.Contains("VIEW DEFINITION", StringComparison.Ordinal));
        Assert.Equal(UnresolvedKind.Sql, gap.Kind);
        Assert.Equal(DATABASE, gap.Site.Database);
        Assert.Null(gap.Site.File);
        Assert.Null(gap.Solution);
    }

    [Fact]
    public async Task Stays_quiet_about_permissions_when_it_can_see_the_dependencies()
    {
        var catalogue = new FakeCatalogue();
        AddProcedure(catalogue, "Outer");

        var graph = await IndexAsync(catalogue);

        Assert.DoesNotContain(graph.Unresolved,
            item => item.Reason.Contains("VIEW DEFINITION", StringComparison.Ordinal));
    }

    private static async Task<CacheGraph> IndexAsync(FakeCatalogue catalogue)
    {
        using var connection = catalogue.Connect();
        var result = await new DatabaseIndexer().IndexAsync(connection, DATABASE);
        return result.Graph;
    }

    private static void AddProcedure(FakeCatalogue catalogue, string name) =>
        catalogue.Procedures.Add(("dbo", name, $"CREATE PROCEDURE dbo.{name} AS SELECT 1;"));

    private static bool Named(GraphVertex vertex, string name) =>
        vertex is StoredProcedure procedure && procedure.Name == name;

    /// <summary>Collects every object a statement reads from — a table, a view, or a table-valued function
    /// — so a test can insist they all live in <c>sys</c>. Data type names are object names too in this
    /// grammar (<c>CAST(x AS int)</c>), which is why this looks at table sources rather than at every
    /// <see cref="SchemaObjectName"/>.</summary>
    private sealed class ObjectNameWalker : TSqlFragmentVisitor
    {
        public List<string?> Names { get; } = [];

        public List<ExecutableProcedureReference> Executions { get; } = [];

        public override void Visit(NamedTableReference node) => Names.Add(Schema(node.SchemaObject));

        public override void Visit(SchemaObjectFunctionTableReference node) =>
            Names.Add(Schema(node.SchemaObject));

        public override void Visit(ExecutableProcedureReference node) => Executions.Add(node);

        private static string? Schema(SchemaObjectName name) => name.SchemaIdentifier?.Value;
    }
}
