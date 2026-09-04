using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests;

public sealed class TraceToolsTests
{
    [Fact]
    public void TraceTools_trace_key_returns_shape_and_disambiguates_store()
    {
        var graph = BuildGraph();

        var ambiguous = Assert.Throws<InvalidOperationException>(() =>
            TraceQueries.TraceKey(graph, "product:{id}", null, new PageArguments()));
        var trace = TraceQueries.TraceKey(graph, "product:{id}", "memory", new PageArguments());
        var json = JsonSerializer.Serialize(trace, CacheDetectiveJsonContext.Default.TraceKeyResult);
        using var document = JsonDocument.Parse(json);

        Assert.Contains("multiple stores", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("memory", document.RootElement.GetProperty("store").GetString());
        Assert.Equal("cache", document.RootElement.GetProperty("role").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("cachedBy").GetProperty("total").GetInt32());
        Assert.Contains(trace.Dependencies.Items, dependency => dependency.Type == "Table" &&
            dependency.Name == "dbo.Products" && dependency.Path.Count > 0);
        Assert.Contains(trace.Dependencies.Items, dependency => dependency.Type == "CacheKey" &&
            dependency.Name == "price:{id}");
        Assert.Single(trace.InvalidatedBy.Items);

        var redis = TraceQueries.TraceKey(graph, "product:{id}", "redis", new PageArguments());
        Assert.Equal("redis", redis.Store);
        Assert.Equal("store", redis.Role);
    }

    [Fact]
    public void TraceTools_trace_table_returns_readers_writers_and_dependent_keys()
    {
        var trace = TraceQueries.TraceTable(BuildGraph(), "dbo.Products", new PageArguments());
        var json = JsonSerializer.Serialize(trace, CacheDetectiveJsonContext.Default.TraceTableResult);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("dbo.Products", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("default", document.RootElement.GetProperty("database").GetString());
        Assert.Single(trace.ReadBy.Items);
        Assert.Single(trace.WrittenBy.Items);
        var key = Assert.Single(trace.DependentKeys.Items);
        Assert.Equal("product:{id}", key.Template);
        Assert.Equal("memory", key.Store);
        Assert.NotEmpty(key.Path);
    }

    [Fact]
    public void TraceTools_export_graph_uses_section_4_3_shape()
    {
        var graph = BuildGraph();
        graph.AddUnresolved(UnresolvedKind.Sql, "Catalog", "Query.cs", 12, "FromSqlRaw(sql)",
            "SQL parsing is out of scope for this phase.");

        var export = TraceQueries.ExportGraph(graph, null, null);
        var json = JsonSerializer.Serialize(export, CacheDetectiveJsonContext.Default.GraphExport);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("workspace").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("nodes").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("edges").ValueKind);
        Assert.Equal("u:1", root.GetProperty("unresolved")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("annotations").ValueKind);
        Assert.Contains(export.Nodes, node => node.Id == "key:memory/product:{id}");
        Assert.Contains(export.Edges, edge => edge.Type == "caches" && edge.Evidence.Count > 0);
    }

    [Fact]
    public void TraceTools_trace_table_names_the_database_objects_that_touch_it()
    {
        var graph = BuildDatabaseGraph();

        var trace = TraceQueries.TraceTable(graph, "dbo.Products", new PageArguments());
        var json = JsonSerializer.Serialize(trace, CacheDetectiveJsonContext.Default.TraceTableResult);
        using var document = JsonDocument.Parse(json);

        var procedure = Assert.Single(trace.ReadBy.Items, item => item.Type == "procedure");
        Assert.Equal("dbo.ApplyDiscount", procedure.Name);
        Assert.Equal("shop", procedure.Database);
        Assert.Null(procedure.File);
        Assert.Equal(["shop.dbo.ApplyDiscount"], procedure.Evidence);

        var view = Assert.Single(trace.ReadBy.Items, item => item.Type == "view");
        Assert.Equal("dbo.vw_ProductCard", view.Name);
        Assert.Equal(["shop.dbo.vw_ProductCard"], view.Evidence);

        var writer = Assert.Single(trace.WrittenBy.Items);
        Assert.Equal("procedure", writer.Type);
        Assert.Equal("dbo.ApplyDiscount", writer.Name);

        var trigger = Assert.Single(trace.Triggers.Items);
        Assert.Equal("dbo.trg_Products_Audit", trigger.Name);
        Assert.Equal("dbo.Products", trigger.Table);
        Assert.Equal("shop", trigger.Database);
        Assert.Equal(["insert", "update"], trigger.Events);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("triggers").ValueKind);
    }

    [Fact]
    public void TraceTools_export_graph_carries_the_database_vertices_and_the_fires_edge()
    {
        var export = TraceQueries.ExportGraph(BuildDatabaseGraph(), null, null);

        Assert.Contains(export.Nodes, node => node.Id == "procedure:dbo.ApplyDiscount" &&
                                              node.Type == "StoredProcedure" && node.Database == "shop");
        Assert.Contains(export.Nodes, node => node.Id == "view:dbo.vw_ProductCard" && node.Type == "View");
        var trigger = Assert.Single(export.Nodes, node => node.Type == "Trigger");
        Assert.Equal("trigger:dbo.trg_Products_Audit", trigger.Id);
        Assert.Equal("dbo.Products", trigger.Table);
        Assert.Equal(["insert", "update"], trigger.Events);
        Assert.Single(export.Edges, edge => edge.Type == "fires" &&
                                            edge.From == "table:dbo.Products" &&
                                            edge.To == "trigger:dbo.trg_Products_Audit");
    }

    private static CacheGraph BuildDatabaseGraph()
    {
        var graph = new CacheGraph();
        var products = new Table("dbo", "Products", "shop");
        var procedure = new StoredProcedure("dbo", "ApplyDiscount", "shop");
        var view = new View("dbo", "vw_ProductCard", "shop");
        var trigger = new Trigger("dbo", "trg_Products_Audit", products.Name,
            [WriteEvent.Insert, WriteEvent.Update], "shop");

        graph.AddEdge(new Reads(procedure, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.ApplyDiscount", "shop")]));
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.ApplyDiscount", "shop")], [WriteEvent.Update]));
        graph.AddEdge(new Reads(view, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.vw_ProductCard", "shop")]));
        graph.AddEdge(new Fires(products, trigger, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.trg_Products_Audit", "shop")]));
        return graph;
    }

    private static CacheGraph BuildGraph()
    {
        var graph = new CacheGraph();
        var getProduct = new Handler("Catalog", "Products.Get", "controller", "Products.cs", 10);
        var getPrice = new Handler("Catalog", "Prices.Get", "controller", "Prices.cs", 20);
        var redisWriter = new Handler("Catalog", "Session.Store", "method", "Session.cs", 30);
        var updateProduct = new Handler("Catalog", "Products.Put", "controller", "Products.cs", 40);
        var invalidateProduct = new Handler("Catalog", "Products.Invalidate", "method", "Products.cs", 50);
        var products = new Table("dbo.Products", "default");
        var discounts = new Table("dbo.Discounts", "default");
        var memoryProduct = new CacheKey("product:{id}", "memory", TimeSpan.FromMinutes(5), [], "cache");
        var redisProduct = new CacheKey("product:{id}", "redis", null, [], "store");
        var price = new CacheKey("price:{id}", "memory", TimeSpan.FromMinutes(1), [], "cache");

        graph.AddEdge(new Caches(getProduct, memoryProduct, Confidence.Confirmed,
            [new Evidence("Products.cs", 14)]));
        graph.AddEdge(new Reads(getProduct, products, Confidence.Confirmed,
            [new Evidence("Products.cs", 12)]));
        graph.AddEdge(new Reads(getProduct, price, Confidence.Confirmed,
            [new Evidence("Products.cs", 13)]));
        graph.AddEdge(new Caches(getPrice, price, Confidence.Confirmed,
            [new Evidence("Prices.cs", 24)]));
        graph.AddEdge(new Reads(getPrice, discounts, Confidence.Likely,
            [new Evidence("Prices.cs", 22)]));
        graph.AddEdge(new Caches(redisWriter, redisProduct, Confidence.Confirmed,
            [new Evidence("Session.cs", 31)]));
        graph.AddEdge(new Writes(updateProduct, products, Confidence.Confirmed,
            [new Evidence("Products.cs", 44)]));
        graph.AddEdge(new Invalidates(invalidateProduct, memoryProduct, Confidence.Confirmed,
            [new Evidence("Products.cs", 52)], CacheSemantic.Remove));
        return graph;
    }
}
