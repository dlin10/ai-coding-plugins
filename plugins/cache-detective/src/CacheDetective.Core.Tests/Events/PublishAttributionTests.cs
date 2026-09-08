using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Events;

/// <summary>A publish belongs to the caller that named the event type, not to the body that happens to
/// contain the call. A shared helper taking the event as a parameter publishes nothing of its own and
/// stays a link on the chain. Recovery is per caller, and a branch that produced nothing is recorded
/// rather than lost. See <c>docs/adr/0017</c>.</summary>
public sealed class PublishAttributionTests
{
    [Fact]
    public async Task Two_callers_of_one_helper_each_keep_their_own_event()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("PlacedController.Place", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("OrderPlaced", StringComparison.Ordinal));
        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("ShippedController.Ship", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("OrderShipped", StringComparison.Ordinal));
    }

    /// <summary>The helper is not a publisher. It stays on the chain through its <c>Calls</c> edge.</summary>
    [Fact]
    public async Task The_helper_itself_publishes_nothing()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Publishes(graph), edge => Publisher(edge).Contains("EventPublisher.Send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_helper_is_still_a_link_on_the_chain()
    {
        var graph = await IndexAsync();

        Assert.Contains(graph.Edges.OfType<Calls>(), edge => edge.To is Handler handler &&
                                                             handler.Symbol.Contains("EventPublisher.Send", StringComparison.Ordinal));
    }

    /// <summary>Today's behaviour, and it must not change: a method that constructs its own event names
    /// it itself.</summary>
    [Fact]
    public async Task A_method_that_constructs_its_own_event_keeps_its_edge()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("OwnController.Publish", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("OwnEvent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_two_hop_chain_attributes_to_the_method_that_named_the_type()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("DeepController.Deep", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("DeepEvent", StringComparison.Ordinal));
        Assert.DoesNotContain(Publishes(graph), edge => Publisher(edge).Contains("MiddleHop.Forward", StringComparison.Ordinal));
    }

    /// <summary>The caller is four calls from an entry point, so the walk reaches it long after the helper
    /// was expanded. Deciding attribution mid-walk would have called it unreachable.</summary>
    [Fact]
    public async Task A_caller_the_walk_reaches_late_is_still_attributed()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("LateStepThree.Three", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("LateEvent", StringComparison.Ordinal));
    }

    /// <summary>No entry point reaches it, so it gets a row and not a vertex: a chain head nothing reaches
    /// is a finding addressed to nobody.</summary>
    [Fact]
    public async Task A_caller_with_no_handler_vertex_produces_a_row_and_no_edge()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Publishes(graph), edge => EventName(edge).EndsWith("UnreachableEvent", StringComparison.Ordinal));
        Assert.Contains(EventRows(graph), row => row.Reason.Contains("UnreachablePublisher.Publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreachable_caller_gets_no_handler_vertex()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(graph.Handlers, handler => handler.Symbol.Contains("UnreachablePublisher", StringComparison.Ordinal));
    }

    /// <summary>One publish site, one edge and one row: the direct caller resolves while the six-hop chain
    /// runs out. Both outcomes belong to the site, and neither hides the other.</summary>
    [Fact]
    public async Task One_caller_resolves_while_another_exhausts_the_recovery_depth()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("MixedDirectController.Direct", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("MixedDirectEvent", StringComparison.Ordinal));
        Assert.Contains(EventRows(graph), row => row.Reason.Contains("reached its limit of 5 hops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_exhausted_branch_yields_no_edge_for_its_event()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Publishes(graph), edge => EventName(edge).EndsWith("ExhaustedEvent", StringComparison.Ordinal));
    }

    /// <summary>The same mixed outcome from the other kind of failure: one caller names its event, another
    /// passes something that names nothing.</summary>
    [Fact]
    public async Task One_caller_resolves_while_another_names_nothing()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("PlacedController.Place", StringComparison.Ordinal));
        Assert.Contains(EventRows(graph), row => row.Reason.Contains("NamelessController.Nameless", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_that_names_nothing_yields_no_edge_for_it()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Publishes(graph), edge => Publisher(edge).Contains("NamelessController.Nameless", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conditional_argument_names_both_types_on_the_same_caller()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("BranchController.Branch", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("LeftEvent", StringComparison.Ordinal));
        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("BranchController.Branch", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("RightEvent", StringComparison.Ordinal));
    }

    /// <summary>The same mixed outcome at the publish site itself, where there is no caller to recover from.
    /// One arm of the conditional names a type and the other names nothing; the site's own fallback row is
    /// skipped because something did resolve, so without a row of its own the unresolved arm vanishes.
    /// </summary>
    [Fact]
    public async Task A_branch_that_names_nothing_is_recorded_even_at_the_publish_site()
    {
        var graph = await IndexAsync();

        Assert.Contains(Publishes(graph), edge => Publisher(edge).Contains("MixedBranchController.Branch", StringComparison.Ordinal) &&
                                                  EventName(edge).EndsWith("KnownBranchEvent", StringComparison.Ordinal));
        Assert.Contains(EventRows(graph), row => row.Reason.Contains("MixedBranchController.Branch", StringComparison.Ordinal) &&
                                                 row.Reason.Contains("names no concrete event type", StringComparison.Ordinal));
    }

    /// <summary>And the arm that named nothing produces no edge of its own — the row is the whole of what is
    /// known about it.</summary>
    [Fact]
    public async Task The_unnamed_branch_at_the_publish_site_yields_no_edge()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Publishes(graph), edge => EventName(edge).EndsWith("OtherBranchEvent", StringComparison.Ordinal));
        Assert.DoesNotContain(Publishes(graph), edge => EventName(edge).EndsWith("BranchBase", StringComparison.Ordinal));
    }

    /// <summary>One unresolved arm, one row. Holding the depth-zero branches apart from the recovered ones
    /// must not make a site say the same thing twice, and the site's own generic "not statically known" row
    /// must not appear beside the specific one.</summary>
    [Fact]
    public async Task The_unnamed_branch_records_exactly_one_row()
    {
        var graph = await IndexAsync();

        Assert.Single(EventRows(graph), row => row.Reason.Contains("MixedBranchController.Branch", StringComparison.Ordinal));
        Assert.DoesNotContain(EventRows(graph), row => row.Snippet.Contains("KnownBranchEvent", StringComparison.Ordinal) &&
                                                       row.Reason.Contains("Event type not statically known", StringComparison.Ordinal));
    }

    /// <summary>Attribution moves where the edge starts, not whether the hop can be followed.</summary>
    [Fact]
    public async Task A_cross_service_chain_still_reaches_its_consumer()
    {
        var graph = await IndexAsync();

        var hop = Assert.Single(graph.EventHops(), item => ((Event)item.Publish.To).Name.EndsWith("CrossServiceEvent", StringComparison.Ordinal));
        Assert.Contains("CrossServiceController.Raise", ((Handler)hop.Publish.From).Symbol, StringComparison.Ordinal);
        Assert.Contains("CrossServiceConsumer.Handle", ((Handler)hop.Consume.To).Symbol, StringComparison.Ordinal);
    }

    /// <summary>Every publish edge starts at a handler the graph actually holds. A resolved attribution
    /// that invented its own vertex would still satisfy every assertion above.</summary>
    [Fact]
    public async Task Every_publisher_is_a_handler_the_walk_reached()
    {
        var graph = await IndexAsync();
        var handlers = graph.Handlers.Select(handler => handler.Symbol).ToHashSet(StringComparer.Ordinal);

        Assert.All(Publishes(graph), edge => Assert.Contains(((Handler)edge.From).Symbol, handlers));
    }

    private static async Task<CacheGraph> IndexAsync()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/PublishAttribution.cs");
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }

    private static Publishes[] Publishes(CacheGraph graph) => [.. graph.Edges.OfType<Publishes>()];

    private static Unresolved[] EventRows(CacheGraph graph) =>
        [.. graph.Unresolved.Where(item => item.Kind == UnresolvedKind.Event)];

    private static string Publisher(Publishes edge) => ((Handler)edge.From).Symbol;

    private static string EventName(Publishes edge) => ((Event)edge.To).Name;
}
