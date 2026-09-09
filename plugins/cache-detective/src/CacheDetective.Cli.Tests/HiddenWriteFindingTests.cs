using CacheDetective.Graph;
using CacheDetective.Mcp;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>The query layer reads the subject off the finding. It does not walk the chain to work out
/// which handler to report, and it must not cast the writer to one.</summary>
public sealed class HiddenWriteFindingTests
{
    private const string DATABASE = "shop";

    [Fact]
    public void FindIssues_names_the_handler_and_not_the_procedure_that_wrote()
    {
        var graph = BuildGraph();
        var catalog = new FindingCatalog();

        var envelope = FindingQueries.FindIssues(graph, null, catalog, null, null, true,
            new PageArguments());

        var item = Assert.Single(envelope.Items);
        Assert.Equal("UNGUARDED_WRITE", item.Rule);
        Assert.Equal("Catalog", item.Solution);
        Assert.Equal("dbo.Products", item.Table);
        Assert.Equal("product:{id}", item.KeyTemplate);
    }

    [Fact]
    public void GetEvidence_walks_the_hidden_chain_including_the_database_objects()
    {
        var graph = BuildGraph();
        var catalog = new FindingCatalog();
        var snapshot = Assert.Single(catalog.GetAll(graph, null));

        var evidence = FindingQueries.GetEvidence(graph, null, null, catalog, snapshot.Item.Id,
            new PageArguments());

        Assert.Equal(["calls", "writes", "reads", "caches"],
            evidence.Fragments.Items.Select(fragment => fragment.Edge));
        var hidden = Assert.Single(evidence.Fragments.Items, fragment => fragment.Edge == "writes");
        Assert.Equal("procedure:dbo.ApplyDiscount", hidden.From);
        Assert.Equal(DATABASE, hidden.Database);
        Assert.Equal("dbo.ApplyDiscount", hidden.ObjectName);
        Assert.Null(hidden.File);
    }

    [Fact]
    public void GetUnresolved_includes_the_derived_reason_for_an_unknown_procedure()
    {
        var graph = BuildGraph();

        var unresolved = FindingQueries.GetUnresolved(graph, null, null, new PageArguments());

        var derived = Assert.Single(unresolved.Items, item => item.Snippet == "dbo.RefreshCache");
        Assert.Equal("sql", derived.Kind);
        Assert.Contains("not in the catalogue", derived.Reason, StringComparison.Ordinal);
        Assert.Contains(DATABASE, derived.Reason, StringComparison.Ordinal);
        Assert.Equal("Prices.cs", derived.File);
        Assert.Equal(46, derived.Line);
    }

    /// <summary>A handler caches a key over one table; another handler calls a procedure that writes it,
    /// and a second procedure that the catalogue does not hold at all — a dead end in the graph.</summary>
    private static CacheGraph BuildGraph()
    {
        var graph = new CacheGraph();
        var products = new Table("dbo", "Products", DATABASE);
        var reader = new Handler("Catalog", "Products.Get", "controller", "Products.cs", 10);
        var writer = new Handler("Catalog", "Prices.Put", "controller", "Prices.cs", 40);
        var procedure = new StoredProcedure("dbo.ApplyDiscount");
        var indexed = new StoredProcedure("dbo", "WriteAudit", DATABASE);

        graph.AddEdge(new Caches(reader, new CacheKey("product:{id}", "memory", null, [], "cache"),
            Confidence.Confirmed, [new Evidence("Products.cs", 14)]));
        graph.AddEdge(new Reads(reader, products, Confidence.Confirmed, [new Evidence("Products.cs", 12)]));
        graph.AddEdge(new Calls(writer, procedure, Confidence.Confirmed, [new Evidence("Prices.cs", 44)]));
        graph.AddEdge(new Calls(writer, new StoredProcedure("dbo.RefreshCache"), Confidence.Confirmed,
            [new Evidence("Prices.cs", 46)]));
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.ApplyDiscount", DATABASE)], [WriteEvent.Update]));
        graph.AddEdge(new Writes(indexed, new Table("dbo", "Audit", DATABASE), Confidence.Confirmed,
            [Evidence.InDatabase("dbo.WriteAudit", DATABASE)]));
        return graph;
    }
}
