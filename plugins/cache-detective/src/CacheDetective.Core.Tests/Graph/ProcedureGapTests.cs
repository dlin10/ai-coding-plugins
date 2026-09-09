using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Graph;

/// <summary>The two reasons a stored-procedure vertex can be a dead end are derived on query, so that the
/// answer follows the graph's present state and not the order the two halves were indexed in.</summary>
public sealed class ProcedureGapTests
{
    private const string DATABASE = "shop";

    [Fact]
    public void Says_the_database_is_not_indexed_when_the_graph_holds_no_catalogue()
    {
        var graph = BuildCodeHalf();

        var gap = Assert.Single(ProcedureGaps.Derive(graph));

        Assert.Equal("dbo.ApplyDiscount", gap.Procedure);
        Assert.Equal(UnresolvedKind.Sql, gap.Unresolved.Kind);
        Assert.Contains("no database is indexed", gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.Equal("Prices.cs", gap.Unresolved.File);
        Assert.Equal(44, gap.Unresolved.Line);
        Assert.Equal("fixture", gap.Unresolved.Solution);
    }

    [Fact]
    public void Names_the_procedure_and_the_database_once_the_catalogue_lacks_it()
    {
        foreach (var graph in BothIndexingOrders(catalogueHoldsTheProcedure: false))
        {
            var gap = Assert.Single(ProcedureGaps.Derive(graph));

            Assert.Equal("dbo.ApplyDiscount", gap.Procedure);
            Assert.Equal(UnresolvedKind.Sql, gap.Unresolved.Kind);
            Assert.Contains("dbo.ApplyDiscount", gap.Unresolved.Reason, StringComparison.Ordinal);
            Assert.Contains(DATABASE, gap.Unresolved.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("not indexed", gap.Unresolved.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_indexed_but_empty_catalogue_gives_reason_b()
    {
        var graph = BuildCodeHalf();
        var catalogue = new CacheGraph();
        catalogue.AddIndexedDatabase(DATABASE);
        graph.ReplaceDatabase(DATABASE, catalogue);

        var gap = Assert.Single(ProcedureGaps.Derive(graph));

        Assert.Contains("dbo.ApplyDiscount", gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.Contains(DATABASE, gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no database is indexed", gap.Unresolved.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_nothing_once_the_catalogue_answers_for_the_procedure()
    {
        foreach (var graph in BothIndexingOrders(catalogueHoldsTheProcedure: true))
        {
            Assert.Empty(ProcedureGaps.Derive(graph));
        }
    }

    /// <summary>The pure-dynamic-SQL shape: the catalogue lists the procedure and it references nothing
    /// statically, so it has no edges. Calling it absent from the catalogue would be false — and the same
    /// report carries the indexer's dynamic-SQL row naming that very procedure.</summary>
    [Fact]
    public void Says_nothing_about_a_procedure_the_catalogue_holds_that_references_nothing()
    {
        foreach (var graph in BothIndexingOrders(catalogueHoldsTheProcedure: true, withEdges: false))
        {
            Assert.Contains(graph.StoredProcedures,
                procedure => procedure.Name == "dbo.ApplyDiscount" && procedure.Database == DATABASE);
            Assert.DoesNotContain(graph.Edges, edge => edge.From is StoredProcedure
            {
                Name: "dbo.ApplyDiscount"
            });

            Assert.Empty(ProcedureGaps.Derive(graph));
        }
    }

    [Fact]
    public void Keeps_a_derived_id_when_later_indexing_stores_more_rows()
    {
        var graph = BuildCodeHalf();

        var first = Assert.Single(ProcedureGaps.Derive(graph)).Unresolved.Id;
        var stored = graph.AddUnresolved(UnresolvedKind.Key, "fixture", "Prices.cs", 9, "cacheKey",
            "no literal segment");
        var second = Assert.Single(ProcedureGaps.Derive(graph)).Unresolved.Id;

        Assert.Equal(first, second);
        Assert.NotEqual(first, stored.Id);
    }

    [Fact]
    public void Numbers_derived_rows_after_the_stored_ones()
    {
        var graph = BuildCodeHalf();
        graph.AddUnresolved(UnresolvedKind.Key, "fixture", "Prices.cs", 9, "cacheKey", "no literal segment");

        var gap = Assert.Single(ProcedureGaps.Derive(graph));

        Assert.Equal(1, Assert.Single(graph.Unresolved).Id);
        Assert.Equal(2, gap.Unresolved.Id);
    }

    [Fact]
    public void Weakens_a_finding_reached_through_a_handler_that_calls_an_unknown_procedure()
    {
        var withGap = BuildCodeHalf();
        AddUnguardedWrite(withGap);
        var withoutGap = BuildCodeHalf(callTheProcedure: false);
        AddUnguardedWrite(withoutGap);

        Assert.Equal(Confidence.Unknown,
            Assert.Single(new UnguardedWriteRule().Evaluate(withGap)).Confidence);
        Assert.Equal(Confidence.Confirmed,
            Assert.Single(new UnguardedWriteRule().Evaluate(withoutGap)).Confidence);
    }

    /// <summary>The same graph assembled code-first and catalogue-first. Re-indexing goes through
    /// <see cref="CacheGraph.ReplaceDatabase"/>, as the tool does.</summary>
    private static IEnumerable<CacheGraph> BothIndexingOrders(bool catalogueHoldsTheProcedure,
                                                              bool withEdges = true)
    {
        var codeFirst = BuildCodeHalf();
        codeFirst.ReplaceDatabase(DATABASE, BuildCatalogueHalf(catalogueHoldsTheProcedure, withEdges));
        yield return codeFirst;

        var catalogueFirst = new CacheGraph();
        catalogueFirst.ReplaceDatabase(DATABASE,
            BuildCatalogueHalf(catalogueHoldsTheProcedure, withEdges));
        foreach (var edge in BuildCodeHalf().Edges)
        {
            catalogueFirst.AddEdge(edge);
        }

        yield return catalogueFirst;
    }

    private static CacheGraph BuildCodeHalf(bool callTheProcedure = true)
    {
        var graph = new CacheGraph();
        var reader = Handler("Products.Get");
        graph.AddEdge(new Caches(reader, new CacheKey("product:{id}", "memory", null, [], "cache"),
            Confidence.Confirmed, [new Evidence("Products.cs", 14)]));
        graph.AddEdge(new Reads(reader, new Table("dbo", "Products", DATABASE), Confidence.Confirmed,
            [new Evidence("Products.cs", 12)]));
        if (callTheProcedure)
        {
            // The code half never knows the database: a two-part name and no third argument.
            graph.AddEdge(new Calls(reader, new StoredProcedure("dbo.ApplyDiscount"),
                Confidence.Confirmed, [new Evidence("Prices.cs", 44)]));
        }

        return graph;
    }

    private static CacheGraph BuildCatalogueHalf(bool holdsTheProcedure, bool withEdges)
    {
        var graph = new CacheGraph();
        var audit = new StoredProcedure("dbo", "WriteAudit", DATABASE);
        graph.AddEdge(new Writes(audit, new Table("dbo", "Audit", DATABASE), Confidence.Confirmed,
            [Evidence.InDatabase("dbo.WriteAudit", DATABASE)]));
        if (!holdsTheProcedure)
        {
            return graph;
        }

        var procedure = new StoredProcedure("dbo", "ApplyDiscount", DATABASE);
        if (withEdges)
        {
            graph.AddEdge(new Writes(procedure, new Table("dbo", "Prices", DATABASE),
                Confidence.Confirmed, [Evidence.InDatabase("dbo.ApplyDiscount", DATABASE)]));
        }
        else
        {
            // Listed by the catalogue and referencing nothing statically, as the indexer records it.
            graph.AddStoredProcedure(DATABASE, procedure);
        }

        return graph;
    }

    private static void AddUnguardedWrite(CacheGraph graph) =>
        graph.AddEdge(new Writes(Handler("Products.Put"), new Table("dbo", "Products", DATABASE),
            Confidence.Confirmed, [new Evidence("Products.cs", 44)]));

    private static Handler Handler(string symbol) => new("fixture", symbol, "controller", "fixture.cs", 1);
}
