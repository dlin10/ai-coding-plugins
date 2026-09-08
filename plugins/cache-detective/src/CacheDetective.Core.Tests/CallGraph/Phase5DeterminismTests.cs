using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.CallGraph;

/// <summary>
/// The two order sensitivities this phase introduced, held down. A fold set's element order reaches
/// vertex creation, and <see cref="Microsoft.CodeAnalysis.FindSymbols.SymbolFinder"/> does not specify
/// the order it returns callers in — which now decides which handler a publish hangs on and in what
/// order rows are recorded.
/// <para>These compare sequences <em>as recorded</em> rather than sorted, which
/// <see cref="CallGraphDeterminismTests"/> deliberately does not: sorting first would hide exactly the
/// drift this guards. <see cref="Unresolved"/> ids are the sharp end, because an id is what an
/// <c>annotate</c> binds to, so an id that moves between two runs binds the annotation to another site.
/// See <c>docs/adr/0014</c>.</para>
/// </summary>
public sealed class Phase5DeterminismTests
{
    [Fact]
    public async Task Indexing_the_publish_fixture_twice_gives_the_same_counts()
    {
        var (first, second) = await IndexTwiceAsync(PUBLISH);

        Assert.Equal(first.Handlers.Count, second.Handlers.Count);
        Assert.Equal(first.Events.Count, second.Events.Count);
        Assert.Equal(first.Edges.Count, second.Edges.Count);
        Assert.Equal(first.CacheOperations.Count, second.CacheOperations.Count);
        Assert.Equal(first.Unresolved.Count, second.Unresolved.Count);
    }

    [Fact]
    public async Task Indexing_the_key_fixture_twice_gives_the_same_counts()
    {
        var (first, second) = await IndexTwiceAsync(KEYS);

        Assert.Equal(first.CacheKeys.Count, second.CacheKeys.Count);
        Assert.Equal(first.Edges.Count, second.Edges.Count);
        Assert.Equal(first.CacheOperations.Count, second.CacheOperations.Count);
        Assert.Equal(first.Unresolved.Count, second.Unresolved.Count);
    }

    /// <summary>The sharp end: not that the same rows exist, but that the same id names the same site.
    /// </summary>
    [Fact]
    public async Task Unresolved_ids_bind_to_the_same_sites()
    {
        var (first, second) = await IndexTwiceAsync(PUBLISH);

        Assert.Equal(Bindings(first), Bindings(second));
    }

    [Fact]
    public async Task Unresolved_ids_bind_to_the_same_sites_for_keys()
    {
        var (first, second) = await IndexTwiceAsync(KEYS);

        Assert.Equal(Bindings(first), Bindings(second));
    }

    /// <summary>A site whose key folds to several values records one edge per value, and the order it
    /// records them in reaches vertex creation.</summary>
    [Fact]
    public async Task A_multi_valued_site_keeps_its_element_order()
    {
        var (first, second) = await IndexTwiceAsync(KEYS);

        var order = Removals(first, "BranchyWriter");
        Assert.Equal(2, order.Length);
        Assert.Equal(order, Removals(second, "BranchyWriter"));
    }

    /// <summary>And that order is the ordinal one the fold promised, not merely a repeatable accident.
    /// </summary>
    [Fact]
    public async Task A_multi_valued_site_is_ordered_ordinally()
    {
        var (first, _) = await IndexTwiceAsync(KEYS);

        var order = Removals(first, "BranchyWriter");
        Assert.Equal(order.OrderBy(template => template, StringComparer.Ordinal), order);
    }

    /// <summary>The helper has several callers, and which handler each publish hangs on must not depend on
    /// the order the caller search happened to return them in.</summary>
    [Fact]
    public async Task Publish_attribution_is_identical_for_a_helper_with_several_callers()
    {
        var (first, second) = await IndexTwiceAsync(PUBLISH);

        var attribution = Attribution(first);
        Assert.True(attribution.Length > 1, "the fixture must exercise a helper with several callers");
        Assert.Equal(attribution, Attribution(second));
    }

    /// <summary>Recorded order, not just membership: a set compared after sorting would pass while the
    /// ids underneath it moved.</summary>
    [Fact]
    public async Task The_recorded_edge_order_is_the_same_twice_running()
    {
        var (first, second) = await IndexTwiceAsync(PUBLISH);

        Assert.Equal(RecordedEdges(first), RecordedEdges(second));
    }

    /// <summary>
    /// Equality between two runs only proves they agree; it does not prove they agree on the order the
    /// design chose, and in one process a caller search may simply answer the same way twice. This asserts
    /// the order itself — publishes are recorded by publisher, then by site — so removing the sort fails
    /// here even when nothing perturbs the search.
    /// </summary>
    [Fact]
    public async Task Publishes_are_recorded_in_the_stable_key_order()
    {
        var (graph, _) = await IndexTwiceAsync(PUBLISH);

        var recorded = Attribution(graph);
        Assert.Equal(recorded.OrderBy(text => text, StringComparer.Ordinal), recorded);
    }

    /// <summary>
    /// The same, for the rows whose ids an <c>annotate</c> binds to. Resolution records two groups in
    /// turn — the publishes whose publisher the walk never reached, then the recovery failures — and each
    /// is ordered by the stable key. The groups are not interleaved, which is itself deterministic; what
    /// would not be is the order inside one of them.
    /// </summary>
    [Fact]
    public async Task Resolved_event_rows_are_recorded_in_the_stable_key_order()
    {
        var (graph, _) = await IndexTwiceAsync(PUBLISH);

        var failures = graph.Unresolved
                            .Where(item => item.Kind == UnresolvedKind.Event && item.Reason != WALK_TIME_REASON &&
                                           !item.Reason.Contains(UNREACHABLE, StringComparison.Ordinal))
                            .OrderBy(item => item.Id)
                            .Select(item => item.Reason)
                            .ToArray();
        Assert.True(failures.Length > 1, "the fixture must produce several recovery-failure rows");
        Assert.Equal(failures.OrderBy(reason => reason, StringComparer.Ordinal), failures);
    }

    /// <summary>What a publish site records when nothing about it could be recovered — ordered by the
    /// walk, not by the resolution key.</summary>
    private const string WALK_TIME_REASON = "Event type not statically known: name its events.";

    private const string UNREACHABLE = "no entry point reaches it";

    /// <summary>
    /// The one that would actually have caught an unsorted caller search. Indexing the same solution
    /// twice in one process need not perturb anything — the caller search may simply answer the same way
    /// both times — so the callers are split across two documents and the documents are added in both
    /// orders, which is the lever <see cref="CallGraphDeterminismTests"/> uses for the walk.
    /// </summary>
    [Fact]
    public async Task Attribution_does_not_depend_on_the_order_the_documents_arrived_in()
    {
        var forwards = await FixtureSolution.CreateAsync(PUBLISH, MORE);
        var backwards = await FixtureSolution.CreateAsync(MORE, PUBLISH);

        var first = await new CallGraphIndexer().IndexAsync(forwards, "fixture");
        var second = await new CallGraphIndexer().IndexAsync(backwards, "fixture");

        Assert.Equal(Attribution(first), Attribution(second));
        Assert.Equal(Bindings(first), Bindings(second));
    }

    private const string PUBLISH = "SourceFiles/PublishAttribution.cs";
    private const string MORE = "SourceFiles/PublishAttributionMore.cs";
    private const string KEYS = "SourceFiles/MaySets.cs";

    private static async Task<(CacheGraph First, CacheGraph Second)> IndexTwiceAsync(string fixture)
    {
        var solution = await FixtureSolution.CreateAsync(fixture);
        var first = await new CallGraphIndexer().IndexAsync(solution, "fixture");
        var second = await new CallGraphIndexer().IndexAsync(solution, "fixture");
        return (first, second);
    }

    /// <summary>Each row's id beside the site it names, in id order.</summary>
    private static string[] Bindings(CacheGraph graph) =>
        [.. graph.Unresolved.OrderBy(item => item.Id)
                 .Select(item => $"{item.Id}|{item.Kind}|{item.File}:{item.Line}|{item.Snippet}|{item.Reason}")];

    /// <summary>Publisher and event as recorded, in the order the edges were added.</summary>
    private static string[] Attribution(CacheGraph graph) =>
        [.. graph.Edges.OfType<Publishes>().Select(edge => $"{((Handler)edge.From).Symbol} -> {((Event)edge.To).Name}")];

    private static string[] RecordedEdges(CacheGraph graph) =>
        [.. graph.Edges.Select(edge => $"{edge.GetType().Name}|{Describe(edge.From)}|{Describe(edge.To)}")];

    private static string[] Removals(CacheGraph graph, string writer) =>
        [.. graph.Edges.OfType<Invalidates>()
                 .Where(edge => ((Handler)edge.From).Symbol.Contains(writer, StringComparison.Ordinal))
                 .Select(edge => ((CacheKey)edge.To).Template)];

    private static string Describe(GraphVertex vertex) => vertex switch
    {
        Handler handler => handler.Symbol,
        CacheKey key => $"{key.Store}/{key.Template}",
        Event @event => @event.Name,
        Table table => table.Name,
        _ => vertex.ToString() ?? string.Empty
    };
}
