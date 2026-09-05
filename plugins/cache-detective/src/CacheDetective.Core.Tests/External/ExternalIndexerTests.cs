using CacheDetective.External;
using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.External;

public sealed class ExternalIndexerTests
{
    [Theory]
    [InlineData("{?}/catalog/items?page={page}", "{?}/catalog/items")]
    [InlineData("/Items/", "items")]
    [InlineData("https://api.weather.invalid/today", "today")]
    [InlineData("api/v1/catalog/items/{id:int}", "api/{v}/catalog/items/{id}")]
    [InlineData("{?}items", "{?}/items")]
    [InlineData("items{?}", "items{?}")]
    public void Normalizes_paths(string raw, string expected) => Assert.Equal(expected, PathTemplates.Normalize(raw));

    [Fact]
    public async Task Indexes_http_grpc_refit_and_routes()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/External.cs");
        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Contains(graph.ExternalSources, source => source is { Kind: "http", Method: "GET", Template: "{baseuri}/catalog/items" });
        Assert.Single(graph.ExternalSources, source => source is { Kind: "http", Method: "POST", Template: "{baseuri}/orders" });
        Assert.Contains(graph.ExternalSources, source => source is { ClientName: "ICatalogService", Template: "catalog" });
        Assert.Contains(graph.ExternalSources, source => source is { ClientName: "catalog", Template: "items" });
        Assert.Contains(graph.ExternalSources, source => source is { ClientName: "IProducts", Method: "GET", Template: "products/{id}" });
        Assert.Contains(graph.ExternalSources, source => source is { Kind: "grpc", Template: "Basket/GetBasketById" });
        var grpcSource = Assert.Single(graph.ExternalSources, source => source is { Kind: "grpc", Template: "Basket/GetBasketById" });
        var grpcJoin = Assert.Single(graph.Edges.OfType<Serves>(), edge => edge.From == grpcSource);
        Assert.Equal(Confidence.Confirmed, grpcJoin.Confidence);
        Assert.Equal("grpc", grpcJoin.Level);
        Assert.Contains("BasketServer.GetBasketById", ((Handler)grpcJoin.To).Symbol, StringComparison.Ordinal);

        var unknown = Assert.Single(graph.Unresolved, item => item.Reason.StartsWith("HTTP URL has no literal", StringComparison.Ordinal));
        Assert.True(graph.TryGetExternalSource(unknown.Id, out var source));
        Assert.Equal("{url}", source.Template);

        var controller = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("GetItems", StringComparison.Ordinal));
        Assert.Contains(controller.Routes, route => route is { Method: "GET", Template: "api/{v}/catalog/items/{id}" });
        var ping = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("Ping", StringComparison.Ordinal));
        Assert.Contains(ping.Routes, route => route is { Method: "*", Template: "api/{v}/catalog/ping" });
        var cardTypes = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("CardTypes", StringComparison.Ordinal));
        Assert.Equal([new HandlerRoute("http", "GET", "api/{v}/catalog/cardtypes")], cardTypes.Routes);
        var prefix = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("PrefixGet", StringComparison.Ordinal));
        Assert.Equal([new HandlerRoute("http", "GET", "api/{v}/catalog")], prefix.Routes);
        var verbed = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("VerbedGet", StringComparison.Ordinal));
        Assert.Equal([new HandlerRoute("http", "GET", "api/{v}/catalog/verbed")], verbed.Routes);
        var grpc = Assert.Single(graph.Handlers, handler => handler.Symbol.Contains("BasketServer.GetBasketById", StringComparison.Ordinal));
        Assert.Contains(grpc.Routes, route => route is { Kind: "grpc", Template: "Basket/GetBasketById" });
    }

    [Fact]
    public async Task Folds_an_http_url_through_a_local_and_helper_method()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/External.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Contains(graph.ExternalSources, source => source is { Kind: "http", Method: "GET", Template: "{baseuri}/catalog/items" });
    }

    [Fact]
    public void External_read_makes_a_reachable_cache_key_a_cache()
    {
        var graph = new CacheGraph();
        var handler = new Handler("solution", "Handler.Run()", "controller", "source.cs", 1);
        var key = new CacheKey("products", "memory", null, [], null);
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed, []));
        graph.AddEdge(new Reads(handler, new ExternalSource("http", "GET", "products", null, handler.ServiceId()), Confidence.Confirmed, []));

        new CacheRoleClassifier().Classify(graph, "solution");

        Assert.Equal("cache", Assert.Single(graph.CacheKeys).Role);
    }

    [Fact]
    public async Task External_sources_keep_the_calling_project_and_typed_or_bare_client_identity()
    {
        var graph = await new CallGraphIndexer().IndexAsync(await FixtureSolution.CreateAsync("SourceFiles/External.cs"), "fixture");

        var typed = Assert.Single(graph.ExternalSources, source => source is { ClientName: "ICatalogService", Template: "catalog" });
        Assert.Equal("Fixture", typed.Owner);
        Assert.Contains(graph.ExternalSources, source => source is { Kind: "http", ClientName: null, Owner: "Fixture" });
    }

    [Fact]
    public async Task A_url_without_a_literal_segment_is_an_external_call_unresolved()
    {
        var graph = await new CallGraphIndexer().IndexAsync(await FixtureSolution.CreateAsync("SourceFiles/External.cs"), "fixture");

        var unresolved = Assert.Single(graph.Unresolved, item => item.Kind == UnresolvedKind.Call &&
                                                          item.Reason.Contains("name the endpoint", StringComparison.Ordinal));

        Assert.True(graph.TryGetExternalSource(unresolved.Id, out var source));
        Assert.Equal("{url}", source.Template);
    }
}
