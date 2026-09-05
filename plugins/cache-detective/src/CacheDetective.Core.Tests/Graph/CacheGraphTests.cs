using CacheDetective.Graph;
using CacheDetective.Events;
using CacheDetective.Caching;
using Xunit;

namespace CacheDetective.Tests.Graph;

public sealed class CacheGraphTests
{
    [Fact]
    public void Derived_structures_are_reused_until_a_graph_mutation()
    {
        var graph = new CacheGraph();
        var publisher = new Handler("App", "Publisher.Run", "controller", "publisher.cs", 1);
        var consumer = new Handler("Other", "Consumer.Run", "consumer", "consumer.cs", 1);
        var @event = new Event("Contracts.Changed");
        var key = new CacheKey("changed", "memory", null, [], "cache");
        var table = new Table("dbo.Changed", "shop");
        graph.AddEdge(new Publishes(publisher, @event, Confidence.Confirmed, []));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed, []));
        graph.AddEdge(new Caches(publisher, key, Confidence.Confirmed, []));
        graph.AddEdge(new Reads(publisher, table, Confidence.Confirmed, []));

        var firstEdges = graph.Edges;
        var firstHops = graph.EventHops();
        var firstGaps = EventGaps.Derive(graph);
        var firstDependencies = graph.DependsOn(key);

        Assert.Same(firstEdges, graph.Edges);
        Assert.Same(firstHops, graph.EventHops());
        Assert.Same(firstGaps, EventGaps.Derive(graph));
        Assert.Same(firstDependencies, graph.DependsOn(key));

        graph.AddUnresolved(UnresolvedKind.Call, publisher, new Evidence("publisher.cs", 2), "unknown", "unknown");

        Assert.NotSame(firstEdges, graph.Edges);
        Assert.NotSame(firstHops, graph.EventHops());
        Assert.NotSame(firstGaps, EventGaps.Derive(graph));
        Assert.NotSame(firstDependencies, graph.DependsOn(key));
    }

    [Fact]
    public void TablesAreDeduplicatedAcrossSolutions()
    {
        var graph = new CacheGraph();

        graph.AddTable("one", new Table("dbo.Products", "shop"));
        graph.AddTable("two", new Table("dbo.Products", "shop"));

        Assert.Single(graph.Tables);
    }

    [Fact]
    public void SameTemplateInDifferentStoresRemainsDistinct()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory"));
        graph.AddCacheKey("one", Key("product:{id}", "redis"));

        Assert.Equal(2, graph.CacheKeys.Count);
    }

    [Fact]
    public void NoTtlWinsWhenSitesAreMerged()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory", TimeSpan.FromSeconds(30)));
        graph.AddCacheKey("one", Key("product:{id}", "memory"));

        Assert.Null(Assert.Single(graph.CacheKeys).Ttl);
    }

    [Fact]
    public void TagsTrackIntersectionAndUnionAcrossSites()
    {
        var graph = new CacheGraph();

        graph.AddCacheKey("one", Key("product:{id}", "memory", tags: ["products"]));
        graph.AddCacheKey("one", Key("product:{id}", "memory"));

        var key = Assert.Single(graph.CacheKeys);
        Assert.Empty(key.TagsAll);
        Assert.Equal(new[] { "products" }, key.TagsAny);
    }

    [Fact]
    public void ReindexReplacesContributionsFromTheSolution()
    {
        var graph = new CacheGraph();
        graph.AddTable("one", new Table("dbo.Old"));
        graph.AddTable("two", new Table("dbo.Shared"));

        var replacement = new CacheGraph();
        replacement.AddTable("one", new Table("dbo.New"));
        replacement.AddTable("one", new Table("dbo.Shared"));

        graph.ReplaceSolution("one", replacement);

        Assert.Equal(2, graph.Tables.Count);
        Assert.DoesNotContain(graph.Tables, table => table.Name == "dbo.Old");
        Assert.Contains(graph.Tables, table => table.Name == "dbo.New");
        Assert.Single(graph.Tables, table => table.Name == "dbo.Shared");
    }

    [Fact]
    public void EventHop_is_confirmed_on_one_full_name()
    {
        var graph = new CacheGraph();
        var publish = new Publishes(Handler("A", "Publish", "A.API"), new Event("Contracts.Changed"), Confidence.Confirmed);
        var consume = new Consumes(new Event("Contracts.Changed"), Handler("B", "Consume", "B.API"), Confidence.Confirmed);
        graph.AddEdge(publish);
        graph.AddEdge(consume);

        Assert.True(graph.TryEventHop(publish, consume, out var hop));
        Assert.Equal(Confidence.Confirmed, hop.Confidence);
        Assert.Null(hop.Reason);
        Assert.Single(graph.EventHops());
    }

    [Fact]
    public void EventHop_is_likely_with_a_reason_across_services_on_a_short_name()
    {
        var graph = new CacheGraph();
        var publish = new Publishes(Handler("A", "Publish", "A.API"), new Event("A.Contracts.Changed"), Confidence.Confirmed);
        var consume = new Consumes(new Event("B.Contracts.Changed"), Handler("B", "Consume", "B.API"), Confidence.Confirmed);
        graph.AddEdge(publish);
        graph.AddEdge(consume);

        Assert.True(graph.TryEventHop(publish, consume, out var hop));
        Assert.Equal(Confidence.Likely, hop.Confidence);
        Assert.Equal("contract duplicated across services: A.Contracts.Changed vs B.Contracts.Changed", hop.Reason);
    }

    [Fact]
    public void EventHop_does_not_exist_for_different_full_names_in_one_service()
    {
        var graph = new CacheGraph();
        var publish = new Publishes(Handler("A", "Publish", "Shared.API"), new Event("A.Contracts.Changed"), Confidence.Confirmed);
        var consume = new Consumes(new Event("B.Contracts.Changed"), Handler("B", "Consume", "Shared.API"), Confidence.Confirmed);
        graph.AddEdge(publish);
        graph.AddEdge(consume);

        Assert.False(graph.TryEventHop(publish, consume, out _));
        Assert.Empty(graph.EventHops());
    }

    [Fact]
    public void One_project_through_two_solutions_is_one_service()
    {
        var first = Handler("A.slnx", "Publish", "Shared.API");
        var second = Handler("B.slnx", "Consume", "Shared.API");

        Assert.Equal(first.ServiceId(), second.ServiceId());
    }

    [Fact]
    public void EventHop_never_raises_a_likely_consumes()
    {
        var graph = new CacheGraph();
        var publish = new Publishes(Handler("A", "Publish", "A.API"), new Event("Contracts.Changed"), Confidence.Confirmed);
        var consume = new Consumes(new Event("Contracts.Changed"), Handler("B", "Consume", "B.API"), Confidence.Likely);
        graph.AddEdge(publish);
        graph.AddEdge(consume);

        Assert.True(graph.TryEventHop(publish, consume, out var hop));
        Assert.Equal(Confidence.Likely, hop.Confidence);
        Assert.Equal(Confidence.Likely, Assert.Single(graph.StoredEdges.OfType<Consumes>()).Confidence);
    }

    [Fact]
    public void Handlers_are_equal_on_solution_and_symbol_only()
    {
        var first = new Handler("Shop.slnx", "Catalog.Get", "controller", "one.cs", 1) { Project = "Catalog.API" };
        var second = new Handler("Shop.slnx", "Catalog.Get", "worker", "two.cs", 2)
        {
            Project = "Other.API",
            Routes = [new HandlerRoute("http", "GET", "items")]
        };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void ExternalSources_of_two_services_are_different_vertices_even_with_null_client()
    {
        var graph = new CacheGraph();
        var first = new ExternalSource("http", "GET", "items", null, "A.API");
        var second = new ExternalSource("http", "GET", "items", null, "B.API");
        graph.AddEdge(new Reads(Handler("A", "Read", "A.API"), first, Confidence.Confirmed));
        graph.AddEdge(new Reads(Handler("B", "Read", "B.API"), second, Confidence.Confirmed));

        Assert.Equal(2, graph.ExternalSources.Count);
    }

    [Fact]
    public void ReplaceSolution_of_the_consumer_replaces_its_consumes()
    {
        var graph = new CacheGraph();
        var publisher = Handler("Publisher.slnx", "Publish", "Publisher.API");
        var oldConsumer = Handler("Consumer.slnx", "Old", "Consumer.API");
        graph.AddEdge(new Publishes(publisher, new Event("Contracts.Changed"), Confidence.Confirmed));
        graph.AddEdge(new Consumes(new Event("Contracts.Changed"), oldConsumer, Confidence.Confirmed));

        var replacement = new CacheGraph();
        var newConsumer = Handler("Consumer.slnx", "New", "Consumer.API");
        replacement.AddEdge(new Consumes(new Event("Contracts.OtherChanged"), newConsumer, Confidence.Confirmed));
        graph.ReplaceSolution("Consumer.slnx", replacement);

        Assert.DoesNotContain(graph.StoredEdges.OfType<Consumes>(), edge => ((Handler)edge.To).Symbol == "Old");
        Assert.Contains(graph.StoredEdges.OfType<Consumes>(), edge => ((Handler)edge.To).Symbol == "New");
        Assert.Empty(graph.EventHops());
    }

    [Fact]
    public void Replace_remaps_unresolved_ids_and_everything_that_points_at_them()
    {
        var graph = new CacheGraph();
        graph.AddUnresolved(UnresolvedKind.Call, "Other.slnx", new Evidence("other.cs", 1), "other", "other");
        var replacement = new CacheGraph();
        var handler = Handler("App.slnx", "Reader", "App.API");
        replacement.AddHandler(handler);
        var source = new ExternalSource("http", "GET", "{?}", null, handler.Solution);
        var unresolved = replacement.AddUnresolvedExternal(UnresolvedKind.Call, handler, new Evidence("app.cs", 2), "url", "unknown", source);
        replacement.AddPendingCacheOperation(new PendingCacheOperation(unresolved.Id, handler, "memory", CacheSemantic.Set, null, [], false, []));
        replacement.MarkEventSite(unresolved.Id, EventSiteRole.Publish);
        var role = replacement.AddUnresolved(UnresolvedKind.Role, handler, new Evidence("app.cs", 3), "key", "blocked");
        replacement.AddRoleBlockers(role.Id, "key", "memory", [unresolved.Id]);

        graph.ReplaceSolution("App.slnx", replacement);

        var remapped = Assert.Single(graph.Unresolved, item => item.Kind == UnresolvedKind.Call && item.Solution == "App.slnx");
        Assert.NotEqual(unresolved.Id, remapped.Id);
        Assert.True(graph.TryGetExternalSource(remapped.Id, out var remappedSource));
        Assert.Equal(source, remappedSource);
        Assert.True(graph.TryGetPendingCacheOperation(remapped.Id, out var operation));
        Assert.Equal(remapped.Id, operation.UnresolvedId);
        Assert.True(graph.TryGetEventSiteRole(remapped.Id, out var eventRole));
        Assert.Equal(EventSiteRole.Publish, eventRole);
        Assert.NotEmpty(graph.RoleRowsBlockedBy(remapped.Id));
    }

    [Fact]
    public void Replace_reclassifies_a_role_row_whose_blocker_was_annotated()
    {
        var graph = new CacheGraph();
        var site = new Evidence("app.cs", 2);
        graph.AddAnnotation(new Annotation(1, 1, UnresolvedKind.Sql, "App.slnx", site, "sql", "{}", null));
        var replacement = new CacheGraph();
        var handler = Handler("App.slnx", "Cache", "App.API");
        replacement.AddEdge(new Caches(handler, new CacheKey("key", "memory", null, [], null), Confidence.Confirmed));
        var blocker = replacement.AddUnresolved(UnresolvedKind.Sql, handler, site, "sql", "unknown");
        var role = replacement.AddUnresolved(UnresolvedKind.Role, handler, new Evidence("app.cs", 3), "key", "blocked");
        replacement.AddRoleBlockers(role.Id, "key", "memory", [blocker.Id]);

        graph.ReplaceSolution("App.slnx", replacement);

        Assert.Equal("store", Assert.Single(graph.CacheKeys).Role);
        Assert.DoesNotContain(graph.Unresolved, item => item.Kind == UnresolvedKind.Role);
    }

    [Fact]
    public void Annotation_vertices_and_edges_survive_ReplaceSolution_and_an_edge_drops_when_its_handler_vanishes()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.slnx", "Cache", "App.API");
        var key = new CacheKey("key", "memory", null, [], "cache");
        graph.AddHandler(handler);
        graph.AddAnnotationEdge(new Caches(handler, key, Confidence.Likely) { AnnotationId = 1 });
        graph.AddAnnotation(new Annotation(1, 1, UnresolvedKind.Key, "App.slnx", new Evidence("app.cs", 1), "key", "{}", null));

        graph.ReplaceSolution("App.slnx", new CacheGraph());

        Assert.Single(graph.Annotations);
        Assert.Single(graph.CacheKeys);
        Assert.DoesNotContain(graph.StoredEdges, edge => edge.AnnotationId == 1);
    }

    [Fact]
    public void An_annotation_cache_operation_drops_when_its_handler_vanishes()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.slnx", "Cache", "App.API");
        graph.AddHandler(handler);
        graph.AddAnnotationCacheOperation(new CacheOperation(handler, new CacheKey("key", "memory", null, [], null), CacheSemantic.Set, false, []));

        graph.ReplaceSolution("App.slnx", new CacheGraph());

        Assert.Empty(graph.CacheOperations);
    }

    [Fact]
    public void A_reindexed_unresolved_matching_an_annotation_is_not_re_added()
    {
        var graph = new CacheGraph();
        var site = new Evidence("app.cs", 2);
        graph.AddAnnotation(new Annotation(1, 1, UnresolvedKind.Sql, "App.slnx", site, "sql", "{}", null));
        var replacement = new CacheGraph();
        replacement.AddUnresolved(UnresolvedKind.Sql, "App.slnx", site, "sql", "unknown");

        graph.ReplaceSolution("App.slnx", replacement);

        Assert.Empty(graph.Unresolved);
    }

    [Fact]
    public void A_same_site_unresolved_in_another_solution_is_not_suppressed()
    {
        var graph = new CacheGraph();
        var site = new Evidence("app.cs", 2);
        graph.AddAnnotation(new Annotation(1, 1, UnresolvedKind.Sql, "App.slnx", site, "sql", "{}", null));
        var replacement = new CacheGraph();
        replacement.AddUnresolved(UnresolvedKind.Sql, "Other.slnx", site, "sql", "unknown");

        graph.ReplaceSolution("Other.slnx", replacement);

        Assert.Single(graph.Unresolved);
    }

    private static CacheKey Key(string template, string store, TimeSpan? ttl = null,
                                IEnumerable<string>? tags = null) =>
        new(template, store, ttl, tags, role: null);

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project };
}
