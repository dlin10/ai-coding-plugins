using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Events;

public sealed class EventIndexerTests
{
    [Fact]
    public async Task Indexes_every_builtin_publisher_and_consumer_form()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var publishes = graph.Edges.OfType<Publishes>().Where(edge => ((Event)edge.To).FullName == "EventFixture.PublishedEvent").ToArray();
        Assert.Equal(7, publishes.Length);
        Assert.All(publishes, edge =>
        {
            Assert.Equal(Confidence.Confirmed, edge.Confidence);
            Assert.Null(edge.AnnotationId);
        });

        var consumes = graph.Edges.OfType<Consumes>().Where(edge => ((Event)edge.From).FullName == "EventFixture.PublishedEvent").ToArray();
        Assert.Equal(4, consumes.Length);
        Assert.Single(consumes, edge => ((Handler)edge.To).Kind == "notification_handler");
        Assert.Single(consumes, edge => ((Handler)edge.To).Kind == "consumer");
        Assert.Equal(2, consumes.Count(edge => ((Handler)edge.To).Kind == "message_handler"));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("MediatRPublisher", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("MediatRMediator", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("MassTransitEndpoint", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("MassTransitBus", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("RebusBus", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("NServiceBusSession", StringComparison.Ordinal));
        Assert.Contains(publishes, edge => ((Handler)edge.From).Symbol.Contains("NServiceBusPipeline", StringComparison.Ordinal));
        Assert.Contains(consumes, edge => ((Handler)edge.To).Symbol.Contains("MediatRConsumer.Handle", StringComparison.Ordinal));
        Assert.Contains(consumes, edge => ((Handler)edge.To).Symbol.Contains("MassTransitConsumer.Consume", StringComparison.Ordinal));
        Assert.Contains(consumes, edge => ((Handler)edge.To).Symbol.Contains("RebusConsumer.Handle", StringComparison.Ordinal));
        Assert.Contains(consumes, edge => ((Handler)edge.To).Symbol.Contains("NServiceBusConsumer.Handle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Carries_configuration_provenance_on_a_new_consumer_form()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");
        var recognizer = new EventRecognizer("custom", ["EventFixture.MyBus"], ["Publish"], 0,
                                             "IMyConsumer", 1, "Consume", "consumer", Confidence.Likely, 7);
        var options = new IndexerOptions(CacheRecognizers.All, EventRecognizers.All.Concat([recognizer]).ToArray());

        var graph = await new CallGraphIndexer(options).IndexAsync(solution, "fixture");

        var publish = Assert.Single(graph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.CustomEvent");
        Assert.Equal(Confidence.Likely, publish.Confidence);
        Assert.Equal(7, publish.AnnotationId);

        var consume = Assert.Single(graph.Edges.OfType<Consumes>(), edge => ((Event)edge.From).FullName == "EventFixture.CustomEvent");
        Assert.Equal(Confidence.Likely, consume.Confidence);
        Assert.Equal(7, consume.AnnotationId);
    }

    [Fact]
    public async Task Derives_event_gaps_and_marks_open_generic_consumers()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var gap = Assert.Single(EventGaps.Derive(graph), item => ((Event)item.Publish.To).FullName == "EventFixture.LonelyEvent");
        Assert.Equal(UnresolvedKind.Event, gap.Unresolved.Kind);
        Assert.Contains("no consumer", gap.Unresolved.Reason, StringComparison.Ordinal);

        var unresolved = Assert.Single(graph.Unresolved, item => item.Reason.Contains("Open generic consumer", StringComparison.Ordinal));
        Assert.True(graph.TryGetEventSiteRole(unresolved.Id, out var role));
        Assert.Equal(EventSiteRole.Consume, role);
    }

    [Fact]
    public async Task Recovers_a_concrete_event_type_from_callers()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Single(graph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.RecoveredEvent");
        Assert.DoesNotContain(graph.Unresolved, item => item.Snippet == "bus.Publish(@event)" &&
                                                   item.Reason.Contains("Event type not statically known", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovers_a_concrete_base_event_through_an_interface_call()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Contains(graph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.InterfaceRecoveredEvent");
        Assert.DoesNotContain(graph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.ConcreteBaseEvent");
    }

    [Fact]
    public async Task Recovers_conditional_and_assigned_local_event_types_without_the_base()
    {
        var graph = await new CallGraphIndexer().IndexAsync(await FixtureSolution.CreateAsync("SourceFiles/Events.cs"), "fixture");
        var names = graph.Edges.OfType<Publishes>().Select(edge => ((Event)edge.To).FullName).ToArray();

        Assert.Equal(2, names.Count(name => name == "EventFixture.BranchLeftEvent"));
        Assert.Equal(2, names.Count(name => name == "EventFixture.BranchRightEvent"));
        Assert.DoesNotContain("EventFixture.BranchBaseEvent", names);
        Assert.Contains(graph.Unresolved, item => item.Kind == UnresolvedKind.Event && item.Snippet.Contains("Property", StringComparison.Ordinal));
        Assert.Contains(names, name => name == "EventFixture.SealedPropertyEvent");
    }

    [Fact]
    public async Task An_event_without_any_consumer_is_an_event_gap_at_its_publish_site()
    {
        var graph = await new CallGraphIndexer().IndexAsync(await FixtureSolution.CreateAsync("SourceFiles/Events.cs"), "fixture");

        var gap = Assert.Single(EventGaps.Derive(graph), item => ((Event)item.Publish.To).FullName == "EventFixture.LonelyEvent");

        Assert.Equal(UnresolvedKind.Event, gap.Unresolved.Kind);
        Assert.Equal(gap.Publish.Evidence.Single().Describe(), gap.Unresolved.Site.Describe());
    }

    [Fact]
    public void A_duplicated_contract_across_services_is_likely_but_is_not_rewritten()
    {
        var graph = new CacheGraph();
        var publish = new Publishes(Handler("A", "Publish", "A.API"), new Event("A.Contracts.Changed"), Confidence.Confirmed);
        var consume = new Consumes(new Event("B.Contracts.Changed"), Handler("B", "Consume", "B.API"), Confidence.Confirmed);
        graph.AddEdge(publish);
        graph.AddEdge(consume);

        var hop = Assert.Single(graph.EventHops());

        Assert.Equal(2, graph.Events.Count);
        Assert.Equal(Confidence.Likely, hop.Confidence);
        Assert.Contains("contract duplicated across services", hop.Reason, StringComparison.Ordinal);
        Assert.Equal(Confidence.Confirmed, Assert.Single(graph.StoredEdges.OfType<Consumes>()).Confidence);
    }

    [Fact]
    public void The_same_short_event_name_inside_one_service_does_not_hop()
    {
        var graph = new CacheGraph();
        graph.AddEdge(new Publishes(Handler("A", "Publish", "Shared.API"), new Event("A.Contracts.Changed"), Confidence.Confirmed));
        graph.AddEdge(new Consumes(new Event("B.Contracts.Changed"), Handler("B", "Consume", "Shared.API"), Confidence.Confirmed));

        Assert.Empty(graph.EventHops());
    }

    [Fact]
    public async Task A_configured_recognizer_is_confirmed_and_an_annotated_recognizer_is_likely()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");
        var configured = new EventRecognizer("configured", ["EventFixture.MyBus"], ["Publish"], 0,
            "IMyConsumer", 1, "Consume", "consumer", Confidence.Confirmed, null);
        var annotated = configured with { Name = "annotated", Confidence = Confidence.Likely, AnnotationId = 7 };

        var confirmedGraph = await new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All.Concat([configured]).ToArray()))
            .IndexAsync(solution, "fixture");
        var likelyGraph = await new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All.Concat([annotated]).ToArray()))
            .IndexAsync(solution, "fixture");

        var confirmed = Assert.Single(confirmedGraph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.CustomEvent");
        Assert.Equal(Confidence.Confirmed, confirmed.Confidence);
        Assert.Null(confirmed.AnnotationId);
        var likely = Assert.Single(likelyGraph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.CustomEvent");
        Assert.Equal(Confidence.Likely, likely.Confidence);
        Assert.Equal(7, likely.AnnotationId);
    }

    [Fact]
    public async Task A_publisher_only_recognizer_produces_publishes_without_a_consumer_form()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Events.cs");
        var recognizer = new EventRecognizer("publisher-only", ["EventFixture.MyBus"], ["Publish"], 0,
            string.Empty, 0, string.Empty, "consumer", Confidence.Confirmed, null);

        var graph = await new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All.Concat([recognizer]).ToArray()))
            .IndexAsync(solution, "fixture");

        Assert.Contains(graph.Edges.OfType<Publishes>(), edge => ((Event)edge.To).FullName == "EventFixture.CustomEvent");
        Assert.DoesNotContain(graph.Edges.OfType<Consumes>(), edge => ((Event)edge.From).FullName == "EventFixture.CustomEvent");
    }

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project };
}
