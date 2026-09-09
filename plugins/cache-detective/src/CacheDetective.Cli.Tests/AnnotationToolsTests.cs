using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using Xunit;

namespace CacheDetective.Tests;

public sealed class AnnotationToolsTests
{
    [Fact]
    public async Task Annotating_a_key_creates_a_likely_edge_and_records_the_annotation()
    {
        var session = new WorkspaceSession();
        var handler = Handler("Shop.slnx", "Reader.Get()");
        session.Graph.AddHandler(handler);
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.Key, handler, new Evidence("reader.cs", 2), "id", "unknown");
        session.Graph.AddPendingCacheOperation(new PendingCacheOperation(unresolved.Id, handler, "memory", CacheSemantic.Set,
            TimeSpan.FromSeconds(30), [], false, [new Evidence("reader.cs", 2)]));

        var result = await Annotate(session, unresolved, "{ \"template\": \"item:{id}\" }", "known key");

        AssertAnnotation(session, unresolved, result, "key", "known key");
        AssertLikelyAnnotated<Caches>(session, result.AnnotationId);
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Annotating_sql_creates_likely_edges_and_records_the_annotation()
    {
        var session = new WorkspaceSession();
        var handler = Handler("Shop.slnx", "Writer.Save()");
        session.Graph.AddHandler(handler);
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.Sql, handler, new Evidence("writer.cs", 2), "sql", "unknown");

        var result = await Annotate(session, unresolved, "{ \"reads\": [\"Products\"], \"writes\": [\"Orders\"], \"procs\": [\"Apply\"] }", "known sql");

        AssertAnnotation(session, unresolved, result, "sql", "known sql");
        Assert.All(session.Graph.Edges.Where(edge => edge.AnnotationId == result.AnnotationId), AssertLikely);
        Assert.Contains(session.Graph.Edges, edge => edge is Reads { To: Table { Name: "dbo.Products" } });
        Assert.Contains(session.Graph.Edges, edge => edge is Writes { To: Table { Name: "dbo.Orders" } });
        Assert.Contains(session.Graph.Edges, edge => edge is Calls { To: StoredProcedure { Name: "dbo.Apply" } });
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Annotating_a_call_creates_a_likely_call_and_unknown_handler_refusal_lists_nearest_handlers()
    {
        var session = new WorkspaceSession();
        var caller = Handler("Shop.slnx", "Reader.Call()");
        var target = Handler("Shop.slnx", "Catalog.Items()");
        session.Graph.AddHandler(caller);
        session.Graph.AddHandler(target);
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.Call, caller, new Evidence("reader.cs", 2), "call", "unknown");

        var malformed = await Assert.ThrowsAsync<ArgumentException>(() => Annotate(session, unresolved,
            "{ \"target\": \"handler:Shop.slnx/Catalog.Missing()\" }", null));
        Assert.Contains("Nearest handlers", malformed.Message, StringComparison.Ordinal);
        Assert.Contains(unresolved, session.Graph.Unresolved);
        Assert.DoesNotContain(session.Graph.Edges, edge => edge.AnnotationId is not null);

        var result = await Annotate(session, unresolved, "{ \"target\": \"handler:Shop.slnx/Catalog.Items()\" }", "known call");

        AssertAnnotation(session, unresolved, result, "call", "known call");
        AssertLikelyAnnotated<Calls>(session, result.AnnotationId);
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Annotating_an_event_creates_a_likely_publish_and_rejects_an_empty_event_list()
    {
        var session = new WorkspaceSession();
        var publisher = Handler("Shop.slnx", "Publisher.Run()");
        session.Graph.AddHandler(publisher);
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.Event, publisher, new Evidence("publisher.cs", 2), "event", "unknown");
        session.Graph.MarkEventSite(unresolved.Id, EventSiteRole.Publish);

        var malformed = await Assert.ThrowsAsync<ArgumentException>(() => Annotate(session, unresolved, "{ \"events\": [] }", null));
        Assert.Contains("resolution for kind 'event'", malformed.Message, StringComparison.Ordinal);
        Assert.Contains(unresolved, session.Graph.Unresolved);

        var result = await Annotate(session, unresolved, "{ \"events\": [\"Contracts.Changed\"] }", "known event");

        AssertAnnotation(session, unresolved, result, "event", "known event");
        AssertLikelyAnnotated<Publishes>(session, result.AnnotationId);
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Annotating_a_role_removes_the_row_and_records_the_annotation()
    {
        var session = new WorkspaceSession();
        session.Graph.AddCacheKey("Shop.slnx", new CacheKey("items", "memory", null, [], null));
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.Role, "Shop.slnx", new Evidence("reader.cs", 2), "items", "unknown");

        var result = await Annotate(session, unresolved, "{ \"role\": \"cache\", \"store\": \"memory\" }", "known role");

        AssertAnnotation(session, unresolved, result, "role", "known role");
        Assert.Equal("cache", Assert.Single(session.Graph.CacheKeys).Role);
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Annotating_a_service_join_gap_creates_a_likely_serves_edge_and_removes_the_gap()
    {
        var session = new WorkspaceSession();
        var reader = Handler("Shop.slnx", "Reader.Run()", "Shop.API");
        var target = Handler("Catalog.slnx", "Catalog.Items()", "Catalog.API",
            [new HandlerRoute("http", "GET", "api/catalog/items")]);
        var source = new ExternalSource("http", "GET", "{?}", "ICatalogService", reader.ServiceId());
        session.Graph.AddEdge(new Reads(reader, source, Confidence.Confirmed, [new Evidence("reader.cs", 2)]));
        session.Graph.AddHandler(target);
        var gap = Assert.Single(ServiceJoins.Derive(session.Graph).Gaps);

        var result = await Annotate(session, gap.Unresolved, $$"""{ "target": "handler:Catalog.slnx/{{target.Symbol}}" }""", "known endpoint");

        AssertAnnotation(session, gap.Unresolved, result, "call", "known endpoint");
        AssertLikelyAnnotated<Serves>(session, result.AnnotationId);
        Assert.Empty(ServiceJoins.Derive(session.Graph).Gaps);
    }

    [Fact]
    public async Task Event_gap_handler_annotations_validate_all_handlers_before_adding_any_edge()
    {
        var session = new WorkspaceSession();
        var publisher = Handler("Shop.slnx", "Publisher.Run()");
        var consumer = Handler("Shop.slnx", "Consumer.Handle()");
        session.Graph.AddHandler(publisher);
        session.Graph.AddHandler(consumer);
        session.Graph.AddEdge(new Publishes(publisher, new Event("Contracts.Changed"), Confidence.Confirmed));
        var gap = Assert.Single(CacheDetective.Events.EventGaps.Derive(session.Graph));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Annotate(session, gap.Unresolved,
            "{ \"handlers\": [\"handler:Shop.slnx/Consumer.Handle()\", \"handler:Shop.slnx/Missing.Handle()\"] }", null));

        Assert.Contains("Unknown handler", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Graph.Edges, edge => edge is Consumes);
        Assert.Contains(CacheDetective.Events.EventGaps.Derive(session.Graph), item => item.Unresolved.Id == gap.Unresolved.Id);
    }

    [Fact]
    public async Task Cache_api_annotation_reindexes_with_a_likely_edge_and_is_not_persisted_to_a_new_session()
    {
        using var repository = await TestRepository.CreateAsync();
        var session = await repository.CreateIndexedSessionAsync();
        var unresolved = Assert.Single(session.Graph.Unresolved, item => item.Kind == UnresolvedKind.CacheApi);

        var result = await Annotate(session, unresolved,
            "{ \"type\": \"MysteryCache\", \"store\": \"memory\", \"methods\": [{ \"name\": \"Set\", \"semantic\": \"set\", \"key_arg\": 0 }] }", "known cache api");

        Assert.NotNull(result.Reindexed);
        AssertAnnotation(session, unresolved, result, "cache_api", "known cache api");
        AssertLikelyAnnotated<Caches>(session, result.AnnotationId);
        AssertGone(session, unresolved);

        var second = await repository.CreateIndexedSessionAsync();
        Assert.Empty(second.Graph.Annotations);
        Assert.Contains(second.Graph.Unresolved, item => item.Kind == UnresolvedKind.CacheApi);
    }

    [Fact]
    public async Task Event_api_annotation_reindexes_with_a_likely_edge_and_rejects_an_unknown_bus_shape()
    {
        using var repository = await TestRepository.CreateAsync();
        var session = await repository.CreateIndexedSessionAsync();
        var unresolved = Assert.Single(session.Graph.Unresolved, item => item.Kind == UnresolvedKind.EventApi);

        var malformed = await Assert.ThrowsAsync<ArgumentException>(() => Annotate(session, unresolved, "{ \"publisher\": 1 }", null));
        Assert.Contains("resolution for kind 'event_api'", malformed.Message, StringComparison.Ordinal);
        Assert.Contains(unresolved, session.Graph.Unresolved);

        var result = await Annotate(session, unresolved,
            "{ \"publisher\": \"MysteryBus\", \"consumer\": \"IConsumer\" }", "known event api");

        Assert.NotNull(result.Reindexed);
        AssertAnnotation(session, unresolved, result, "event_api", "known event api");
        AssertLikelyAnnotated<Publishes>(session, result.AnnotationId);
        AssertGone(session, unresolved);
    }

    [Fact]
    public async Task Failed_recognizer_reindex_does_not_keep_the_cache_recognizer()
    {
        using var repository = await TestRepository.CreateAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        var unresolved = session.Graph.AddUnresolved(UnresolvedKind.CacheApi, "missing.csproj", new Evidence("missing.cs", 1), "cache",
            "Unknown cache API type MysteryCache.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Annotate(session, unresolved,
            "{ \"type\": \"MysteryCache\", \"store\": \"memory\", \"methods\": [{ \"name\": \"Set\", \"semantic\": \"set\", \"key_arg\": 0 }] }", null));
        var indexed = await session.IndexSolutionAsync("App.csproj");

        Assert.True(indexed.Succeeded, indexed.Error);
        Assert.Empty(session.Graph.Edges.OfType<Caches>());
        Assert.Contains(session.Graph.Unresolved, item => item.Kind == UnresolvedKind.CacheApi);
    }

    [Theory]
    [InlineData(UnresolvedKind.Key, "{ \"bad\": true }")]
    [InlineData(UnresolvedKind.Sql, "{ \"reads\": [] }")]
    [InlineData(UnresolvedKind.Role, "{ \"role\": \"bad\" }")]
    [InlineData(UnresolvedKind.CacheApi, "{ \"type\": \"MysteryCache\" }")]
    public async Task Malformed_resolutions_are_refused_without_removing_the_row(UnresolvedKind kind, string json)
    {
        var session = new WorkspaceSession();
        var handler = Handler("Shop.slnx", "Handler.Run()");
        session.Graph.AddHandler(handler);
        if (kind == UnresolvedKind.Role)
            session.Graph.AddCacheKey(handler.Solution, new CacheKey("role-key", "memory", null, [], null));
        var unresolved = session.Graph.AddUnresolved(kind, handler, new Evidence("handler.cs", 2), kind == UnresolvedKind.Role ? "role-key" : "value", "unknown");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Annotate(session, unresolved, json, null));

        Assert.Contains($"resolution for kind '{FindingQueries.KindName(kind)}'", error.Message, StringComparison.Ordinal);
        Assert.Contains(unresolved, session.Graph.Unresolved);
        Assert.Empty(session.Graph.Annotations);
    }

    private static async Task<AnnotateResult> Annotate(WorkspaceSession session, Unresolved unresolved, string json, string? note)
    {
        using var resolution = JsonDocument.Parse(json);
        return await session.AnnotateAsync($"u:{unresolved.Id}", resolution.RootElement, note);
    }

    private static void AssertAnnotation(WorkspaceSession session, Unresolved unresolved, AnnotateResult result, string kind, string? note)
    {
        var annotation = Assert.Single(TraceQueries.ExportGraph(session.Graph, null, null).Annotations);
        Assert.Equal(result.AnnotationId, annotation.Id);
        Assert.Equal(unresolved.Id, annotation.UnresolvedId);
        Assert.Equal(kind, annotation.Kind);
        Assert.Equal(note, annotation.Note);
        Assert.Equal(result.Kind, annotation.Kind);
        Assert.False(string.IsNullOrWhiteSpace(annotation.Resolution.GetRawText()));
    }

    private static void AssertLikelyAnnotated<TEdge>(WorkspaceSession session, int annotationId) where TEdge : GraphEdge
    {
        var edge = Assert.Single(session.Graph.Edges.OfType<TEdge>(), candidate => candidate.AnnotationId == annotationId);
        AssertLikely(edge);
    }

    private static void AssertLikely(GraphEdge edge)
    {
        Assert.Equal(Confidence.Likely, edge.Confidence);
        Assert.NotNull(edge.AnnotationId);
    }

    private static void AssertGone(WorkspaceSession session, Unresolved unresolved) =>
        Assert.DoesNotContain(FindingQueries.GetUnresolved(session.Graph, null, null, new PageArguments { PageSize = 100 }).Items,
            item => item.Id == $"u:{unresolved.Id}");

    private static Handler Handler(string solution, string symbol, string? project = null, IReadOnlyList<HandlerRoute>? routes = null) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project ?? solution, Routes = routes ?? [] };

    private sealed class TestRepository : IDisposable
    {
        private TestRepository(string path) => Path = path;
        public string Path { get; }

        public static async Task<TestRepository> CreateAsync()
        {
            var repository = new TestRepository(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-annotation-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(repository.Path);
            await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "Controller.cs"), """
                public class ControllerBase { }
                public sealed class MysteryCache { public void Set(string key) { } }
                public sealed class MysteryBus { public void Publish(Changed changed) { } }
                public sealed class Changed { }
                public sealed class DemoController : ControllerBase
                {
                    public void Get()
                    {
                        new MysteryCache().Set("item:1");
                        new MysteryBus().Publish(new Changed());
                    }
                }
                """);
            return repository;
        }

        public async Task<WorkspaceSession> CreateIndexedSessionAsync()
        {
            var session = new WorkspaceSession();
            await session.InitializeAsync(Path, ["App.csproj"], null);
            var indexed = await session.IndexSolutionAsync("App.csproj");
            Assert.True(indexed.Succeeded, indexed.Error);
            return session;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
