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

        Assert.Equal(3, root.GetProperty("version").GetInt32());
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

    [Fact]
    public void TraceTools_export_graph_carries_phase_three_events_sources_hops_and_annotations()
    {
        var graph = new CacheGraph();
        var publisher = Handler("Shop.slnx", "Catalog.Publish", "Catalog.API");
        var consumer = Handler("Notify.slnx", "Notifications.Handle", "Notifications.API");
        var foreignPublisher = Handler("Other.slnx", "Other.Publish", "Other.API");
        var foreignConsumer = Handler("Else.slnx", "Else.Handle", "Else.API");
        var published = new Event("Catalog.Contracts.ProductChanged");
        var consumed = new Event("Notifications.Contracts.ProductChanged");
        var source = new ExternalSource("http", "GET", "products", "ICatalogClient", "Catalog.API");

        graph.AddEdge(new Publishes(publisher, published, Confidence.Confirmed, [new Evidence("Catalog.cs", 10)]));
        graph.AddEdge(new Consumes(consumed, consumer, Confidence.Confirmed, [new Evidence("Notifications.cs", 20)]));
        graph.AddEdge(new Reads(publisher, source, Confidence.Confirmed, [new Evidence("Catalog.cs", 11)]));
        graph.AddEdge(new Serves(source, consumer, Confidence.Likely, [new Evidence("Catalog.cs", 12)], "client_name"));
        graph.AddEdge(new Publishes(foreignPublisher, new Event("Other.Contracts.Changed"), Confidence.Confirmed,
            [new Evidence("Other.cs", 30)]));
        graph.AddEdge(new Consumes(new Event("Else.Contracts.Changed"), foreignConsumer, Confidence.Confirmed,
            [new Evidence("Else.cs", 40)]));
        graph.AddAnnotation(new Annotation(7, 42, UnresolvedKind.Key, "Shop.slnx", new Evidence("Catalog.cs", 13),
            "product:{id}", "{\"template\":\"product:{id}\"}", "named by reviewer"));

        var export = TraceQueries.ExportGraph(graph, null, null);
        var filtered = TraceQueries.ExportGraph(graph, null, "Catalog.Contracts.ProductChanged");

        var eventNodes = export.Nodes.Where(node => node.Type == "Event" && node.Name == "ProductChanged").ToArray();
        Assert.Equal(2, eventNodes.Length);
        Assert.Contains(eventNodes, node => node.Id == "event:Catalog.Contracts.ProductChanged");
        Assert.Contains(eventNodes, node => node.Id == "event:Notifications.Contracts.ProductChanged");
        var external = Assert.Single(export.Nodes, node => node.Type == "ExternalSource");
        Assert.Equal("Catalog.API", external.Owner);
        Assert.Equal("ICatalogClient", external.ClientName);
        Assert.Contains(export.Edges, edge => edge.Type == "serves" && edge.Level == "client_name");
        Assert.Contains(export.Edges, edge => edge.Type == "consumes" && edge.Confidence == "confirmed" && edge.Reason is null);
        var hop = Assert.Single(export.EventHops, item => item.PublishedEvent == "event:Catalog.Contracts.ProductChanged");
        Assert.Equal("likely", hop.Confidence);
        Assert.Contains("contract duplicated across services", hop.Reason, StringComparison.Ordinal);
        var annotation = Assert.Single(export.Annotations);
        Assert.Equal(7, annotation.Id);
        Assert.Equal(42, annotation.UnresolvedId);
        Assert.Equal("key", annotation.Kind);
        Assert.Equal("named by reviewer", annotation.Note);
        Assert.Single(filtered.EventHops);
        Assert.DoesNotContain(filtered.EventHops, item => item.Publisher == "handler:Other.slnx/Other.Publish");
    }

    [Fact]
    public void TraceTools_trace_key_reports_serves_paths_best_invalidation_route_and_projects()
    {
        var graph = new CacheGraph();
        var firstCache = Handler("A.slnx", "First.Get", "A.API");
        var secondCache = Handler("B.slnx", "Second.Get", "B.API");
        var reader = Handler("Catalog.slnx", "Products.Read", "Catalog.API");
        var callAndEvent = Handler("A.slnx", "Invalidate.Best", "A.API");
        var eventOnly = Handler("B.slnx", "Invalidate.Event", "B.API");
        var unreachable = Handler("C.slnx", "Invalidate.None", "C.API");
        var key = new CacheKey("product:{id}", "memory", null, [], "cache");
        var products = new Table("dbo.Products", "shop");
        var source = new ExternalSource("http", "GET", "products", "ICatalogClient", "A.API");
        var changed = new Event("Contracts.Changed");
        var eventOnlyChanged = new Event("Contracts.EventOnlyChanged");

        graph.AddEdge(new Caches(firstCache, key, Confidence.Confirmed, [new Evidence("A.cs", 1)]));
        graph.AddEdge(new Caches(secondCache, key, Confidence.Confirmed, [new Evidence("B.cs", 1)]));
        graph.AddEdge(new Reads(firstCache, source, Confidence.Confirmed, [new Evidence("A.cs", 2)]));
        graph.AddEdge(new Serves(source, reader, Confidence.Likely, [new Evidence("A.cs", 3)], "client_name"));
        graph.AddEdge(new Reads(reader, products, Confidence.Confirmed, [new Evidence("Catalog.cs", 4)]));
        graph.AddEdge(new Calls(firstCache, callAndEvent, Confidence.Confirmed, [new Evidence("A.cs", 5)]));
        graph.AddEdge(new Publishes(secondCache, changed, Confidence.Confirmed, [new Evidence("B.cs", 5)]));
        graph.AddEdge(new Consumes(changed, callAndEvent, Confidence.Likely, [new Evidence("A.cs", 6)]));
        graph.AddEdge(new Publishes(secondCache, eventOnlyChanged, Confidence.Confirmed, [new Evidence("B.cs", 7)]));
        graph.AddEdge(new Consumes(eventOnlyChanged, eventOnly, Confidence.Likely, [new Evidence("B.cs", 8)]));
        graph.AddEdge(new Invalidates(callAndEvent, key, Confidence.Confirmed, [new Evidence("A.cs", 9)], CacheSemantic.Remove));
        graph.AddEdge(new Invalidates(eventOnly, key, Confidence.Confirmed, [new Evidence("B.cs", 9)], CacheSemantic.Remove));
        graph.AddEdge(new Invalidates(unreachable, key, Confidence.Confirmed, [new Evidence("C.cs", 9)], CacheSemantic.Remove));

        var keyTrace = TraceQueries.TraceKey(graph, key.Template, key.Store, new PageArguments());
        var tableTrace = TraceQueries.TraceTable(graph, products.Name, new PageArguments());

        var dependency = Assert.Single(keyTrace.Dependencies.Items, item => item.Type == "Table");
        Assert.Contains(dependency.Path, edge => edge.Type == "serves" && edge.Level == "client_name");
        Assert.Equal("Catalog.API", Assert.Single(tableTrace.ReadBy.Items).Project);
        Assert.Equal(("calls", "confirmed"), keyTrace.InvalidatedBy.Items.Single(item => item.Handler.Symbol == "Invalidate.Best") is { } best
            ? (best.Via, best.Confidence) : throw new InvalidOperationException());
        Assert.Equal(("event", "likely"), keyTrace.InvalidatedBy.Items.Single(item => item.Handler.Symbol == "Invalidate.Event") is { } viaEvent
            ? (viaEvent.Via, viaEvent.Confidence) : throw new InvalidOperationException());
        Assert.Equal("none", keyTrace.InvalidatedBy.Items.Single(item => item.Handler.Symbol == "Invalidate.None").Via);
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

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "controller", $"{project}.cs", 1) { Project = project };

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
