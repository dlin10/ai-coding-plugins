using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

public sealed class ServiceJoinsTests
{
    [Fact]
    public void Maps_a_client_to_one_project_by_known_tail()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1) { Project = "Shop.API" };
        var endpoint = new Handler("Catalog.slnx", "Catalog.Items()", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/{v}/catalog/items")] };
        var source = new ExternalSource("http", "GET", "{base}/c/api/v1/catalog/items", "ICatalogService", reader.ServiceId());
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        graph.AddHandler(endpoint);
        graph.SetServiceMap(new Dictionary<string, string> { ["icatalogservice"] = "Catalog.API" });

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);
        Assert.Equal(Confidence.Confirmed, join.Confidence);
        Assert.Equal("services", join.Level);
        Assert.Equal(endpoint, join.To);
    }

    [Fact]
    public void Emits_a_gap_when_an_explicit_scope_has_no_endpoint()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1);
        var source = new ExternalSource("http", "GET", "{?}/items", "catalog", reader.ServiceId());
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = "Catalog.API" });

        var gap = Assert.Single(ServiceJoins.Derive(graph).Gaps);
        Assert.Contains("No endpoint", gap.Unresolved.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_a_known_tail_at_the_end_of_a_longer_route_only()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1);
        var endpoint = new Handler("Catalog.slnx", "Catalog.Items()", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/{v}/catalog/items/{id}")] };
        var source = new ExternalSource("http", "GET", "catalog/items/{id}", "catalog", reader.ServiceId());
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        graph.AddHandler(endpoint);
        graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = "Catalog.API" });

        Assert.Single(ServiceJoins.Derive(graph).Serves);

        graph = new CacheGraph();
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        graph.AddHandler(endpoint with { Routes = [new HandlerRoute("http", "GET", "api/items/{id}/detail")] });
        graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = "Catalog.API" });
        Assert.Single(ServiceJoins.Derive(graph).Gaps);
    }

    [Fact]
    public void Matches_gateway_and_short_tails_but_not_a_final_parameter_or_placeholders_only()
    {
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1);
        var endpoint = new Handler("Catalog.slnx", "Catalog.Items()", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/{v}/catalog/items")] };

        AssertJoin("{base}/c/api/{v}/catalog/items", endpoint, true);
        AssertJoin("items", endpoint, true);
        AssertJoin("items", endpoint with { Routes = [new HandlerRoute("http", "GET", "api/{v}/catalog/items/{id}")] }, false);
        AssertJoin("{base}/{id}", endpoint, false);

        void AssertJoin(string template, Handler target, bool expected)
        {
            var graph = new CacheGraph();
            graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", template, "catalog", reader.ServiceId()), Confidence.Confirmed, []));
            graph.AddHandler(target);
            graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = "Catalog.API" });
            Assert.Equal(expected, ServiceJoins.Derive(graph).Serves.Count == 1);
        }
    }

    [Fact]
    public void Emits_a_service_join_gap_for_every_reader_of_a_source()
    {
        var graph = new CacheGraph();
        var first = new Handler("One.slnx", "Reader.Run()", "controller", "one.cs", 1);
        var second = new Handler("Two.slnx", "Reader.Run()", "controller", "two.cs", 1);
        var source = new ExternalSource("http", "GET", "items", "catalog", "Shared");
        graph.AddEdge(new Reads(first, source, Confidence.Confirmed, [new Evidence("one.cs", 2)]));
        graph.AddEdge(new Reads(second, source, Confidence.Confirmed, [new Evidence("two.cs", 2)]));
        graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = "Catalog.API" });

        var gaps = ServiceJoins.Derive(graph).Gaps;

        Assert.Equal(2, gaps.Count);
        Assert.NotEqual(gaps[0].Unresolved.Id, gaps[1].Unresolved.Id);
    }

    [Fact]
    public void Emits_a_gap_when_a_client_name_selects_a_service_but_the_tail_is_unknown()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1) { Project = "Shop.API" };
        var endpoint = new Handler("Catalog.slnx", "Catalog.Items()", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/catalog/items")] };
        var source = new ExternalSource("http", "GET", "{?}", "ICatalogService", reader.ServiceId());
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        graph.AddHandler(endpoint);

        var gap = Assert.Single(ServiceJoins.Derive(graph).Gaps);

        Assert.Equal(UnresolvedKind.Call, gap.Unresolved.Kind);
        Assert.Equal("No endpoint in 'Catalog.API' matches GET {?} — name the target.", gap.Unresolved.Reason);
    }

    [Fact]
    public void Leaves_a_client_without_a_matching_service_as_an_external_leaf()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Run()", "controller", "reader.cs", 1);
        var source = new ExternalSource("http", "GET", "{?}", "IWeatherClient", reader.ServiceId());
        graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));

        Assert.Empty(ServiceJoins.Derive(graph).Gaps);
    }

    [Theory]
    [InlineData("Shop.slnx")]
    [InlineData("Shop")]
    public void Maps_a_client_to_a_solution_name(string mappedService)
    {
        var graph = new CacheGraph();
        var reader = new Handler("Reader.slnx", "Reader.Get", "controller", "reader.cs", 1) { Project = "Reader.API" };
        var endpoint = new Handler("Shop.slnx", "Catalog.Get", "controller", "catalog.cs", 1)
        { Routes = [new HandlerRoute("http", "GET", "api/catalog/items")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "{?}/catalog/items", "catalog", reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(endpoint);
        graph.SetServiceMap(new Dictionary<string, string> { ["catalog"] = mappedService });

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);

        Assert.Equal(Confidence.Confirmed, join.Confidence);
        Assert.Equal("services", join.Level);
        Assert.Equal(endpoint, join.To);
    }

    [Fact]
    public void Matches_an_unmapped_client_name_at_likely_confidence()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1) { Project = "Shop.API" };
        var endpoint = new Handler("Catalog.slnx", "Catalog.Get", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/catalog/items")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "{?}/catalog/items", "ICatalogService", reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(endpoint);

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);

        Assert.Equal(Confidence.Likely, join.Confidence);
        Assert.Equal("client_name", join.Level);
    }

    [Fact]
    public void Matches_only_a_full_workspace_route_at_level_three()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1);
        var endpoint = new Handler("Catalog.slnx", "Catalog.Get", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "api/catalog/items")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "api/catalog/items", null, reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(endpoint);

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);
        Assert.Equal("route", join.Level);
        Assert.Equal(Confidence.Likely, join.Confidence);

        graph = new CacheGraph();
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "{?}/catalog/items", null, reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(endpoint);
        Assert.Empty(ServiceJoins.Derive(graph).Serves);
    }

    [Fact]
    public void Reports_every_workspace_candidate_when_more_than_one_endpoint_matches()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1);
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "items", null, reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(new Handler("A.slnx", "A.Get", "controller", "a.cs", 1) { Routes = [new HandlerRoute("http", "GET", "items")] });
        graph.AddHandler(new Handler("B.slnx", "B.Get", "controller", "b.cs", 1) { Routes = [new HandlerRoute("http", "GET", "items")] });

        var gap = Assert.Single(ServiceJoins.Derive(graph).Gaps);

        Assert.Equal(UnresolvedKind.Call, gap.Unresolved.Kind);
        Assert.Contains("handler:A.slnx/A.Get", gap.Unresolved.Reason, StringComparison.Ordinal);
        Assert.Contains("handler:B.slnx/B.Get", gap.Unresolved.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_grpc_service_and_method_at_confirmed_grpc_level()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1);
        var endpoint = new Handler("Basket.slnx", "Basket.Get", "grpc", "basket.cs", 1)
        { Project = "Basket.API", Routes = [new HandlerRoute("grpc", "*", "Basket/GetBasket")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("grpc", "*", "Basket/GetBasket", "Basket", reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(endpoint);

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);

        Assert.Equal(Confidence.Confirmed, join.Confidence);
        Assert.Equal("grpc", join.Level);
        Assert.Equal(endpoint, join.To);
    }

    [Fact]
    public void An_ambiguous_client_scope_falls_back_to_a_workspace_route()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1);
        var expected = new Handler("CatalogApi.slnx", "Catalog.Get", "controller", "catalog.cs", 1)
        { Project = "Catalog.API", Routes = [new HandlerRoute("http", "GET", "items")] };
        var other = new Handler("CatalogService.slnx", "Other.Get", "controller", "other.cs", 1)
        { Project = "Catalog.Service", Routes = [new HandlerRoute("http", "GET", "other")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "items", "ICatalogService", reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(expected);
        graph.AddHandler(other);

        var join = Assert.Single(ServiceJoins.Derive(graph).Serves);

        Assert.Equal(expected, join.To);
        Assert.Equal("route", join.Level);
    }

    [Fact]
    public void Ordering_signalr_hub_does_not_collide_with_ordering_api()
    {
        var graph = new CacheGraph();
        var reader = new Handler("Shop.slnx", "Reader.Get", "controller", "reader.cs", 1);
        var signalr = new Handler("Signalr.slnx", "Signalr.Get", "controller", "signalr.cs", 1)
        { Project = "Ordering.SignalrHub", Routes = [new HandlerRoute("http", "GET", "items")] };
        var api = new Handler("Api.slnx", "Api.Get", "controller", "api.cs", 1)
        { Project = "Ordering.API", Routes = [new HandlerRoute("http", "GET", "items")] };
        graph.AddEdge(new Reads(reader, new ExternalSource("http", "GET", "items", "Ordering.SignalrHub", reader.ServiceId()), Confidence.Confirmed));
        graph.AddHandler(signalr);
        graph.AddHandler(api);

        Assert.Equal(signalr, Assert.Single(ServiceJoins.Derive(graph).Serves).To);
    }
}
