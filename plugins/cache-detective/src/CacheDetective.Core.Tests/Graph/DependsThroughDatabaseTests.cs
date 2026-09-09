using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

/// <summary>A handler that calls a procedure depends on what the procedure reads; one that reads a view
/// depends on the view's tables.</summary>
public sealed class DependsThroughDatabaseTests
{
    private const string DATABASE = "shop";

    [Fact]
    public void Follows_a_call_into_a_procedure_and_on_into_the_procedures_it_calls()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("product:{id}", "memory", null, [], "cache");
        var handler = Handler("Products.Get");
        var outer = new StoredProcedure("dbo", "GetProductCard", DATABASE);
        var inner = new StoredProcedure("dbo", "GetPrices", DATABASE);
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed));
        graph.AddEdge(new Calls(handler, outer, Confidence.Confirmed));
        graph.AddEdge(new Calls(outer, inner, Confidence.Confirmed));
        graph.AddEdge(new Reads(outer, Table("Products"), Confidence.Confirmed));
        graph.AddEdge(new Reads(inner, Table("PriceHistory"), Confidence.Confirmed));

        var dependencies = graph.DependsOn(key);

        Assert.Equal(2, dependencies.Count);
        Assert.Contains(dependencies, dependency => Named(dependency, "dbo.Products"));
        var nested = Assert.Single(dependencies, dependency => Named(dependency, "dbo.PriceHistory"));
        Assert.Collection(nested.Path,
            edge => Assert.IsType<Caches>(edge),
            edge => Assert.IsType<Calls>(edge),
            edge => Assert.IsType<Calls>(edge),
            edge => Assert.IsType<Reads>(edge));
    }

    [Fact]
    public void Lets_a_view_displace_the_table_of_the_same_name()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("card:{id}", "memory", null, [], "cache");
        var handler = Handler("Products.Card");
        var view = new View("dbo", "vw_ProductCard", DATABASE);
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed));
        // The code half cannot tell a view from a table: EF and Dapper both name it as a table.
        graph.AddEdge(new Reads(handler, Table("vw_ProductCard"), Confidence.Confirmed));
        graph.AddEdge(new Reads(view, Table("PriceHistory"), Confidence.Confirmed));

        var dependency = Assert.Single(graph.DependsOn(key));

        Assert.True(Named(dependency, "dbo.PriceHistory"));
        Assert.Collection(dependency.Path,
            edge => Assert.IsType<Caches>(edge),
            edge => Assert.IsType<Reads>(edge),
            edge => Assert.IsType<Reads>(edge));
    }

    [Fact]
    public void Leaves_the_name_a_plain_table_when_no_view_of_that_name_is_known()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("card:{id}", "memory", null, [], "cache");
        var handler = Handler("Products.Card");
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table("vw_ProductCard"), Confidence.Confirmed));

        var dependency = Assert.Single(graph.DependsOn(key));

        Assert.True(Named(dependency, "dbo.vw_ProductCard"));
    }

    [Fact]
    public void Cuts_a_cycle_between_procedures()
    {
        var graph = new CacheGraph();
        var key = new CacheKey("product:{id}", "memory", null, [], "cache");
        var handler = Handler("Products.Get");
        var left = new StoredProcedure("dbo", "Left", DATABASE);
        var right = new StoredProcedure("dbo", "Right", DATABASE);
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed));
        graph.AddEdge(new Calls(handler, left, Confidence.Confirmed));
        graph.AddEdge(new Calls(left, right, Confidence.Confirmed));
        graph.AddEdge(new Calls(right, left, Confidence.Confirmed));
        graph.AddEdge(new Reads(right, Table("Products"), Confidence.Confirmed));

        var dependency = Assert.Single(graph.DependsOn(key));

        Assert.True(Named(dependency, "dbo.Products"));
    }

    private static bool Named(KeyDependency dependency, string name) =>
        dependency.Target is Table table && table.Name == name;

    private static Table Table(string name) => new("dbo", name, DATABASE);

    private static Handler Handler(string symbol) => new("fixture", symbol, "controller", "fixture.cs", 1);
}
