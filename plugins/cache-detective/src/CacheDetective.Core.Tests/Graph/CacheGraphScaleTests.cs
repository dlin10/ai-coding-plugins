using System.Diagnostics;
using CacheDetective.Caching;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

/// <summary>
/// The graph at the size a real solution has, and the two things that break at that size: a lookup that
/// scans, and a materialisation that runs again for every question asked of it. Everything here is either
/// a budget the graph must stay inside, or a fact about the indexes that keeps it there.
/// <para>The role tests live here too, because the role is the one vertex fact the indexes do <em>not</em>
/// hold: it is stamped with the solution that classified it, and two solutions that classify one key
/// differently are reported rather than reconciled — see <c>docs/adr/0005</c>.</para>
/// </summary>
public sealed class CacheGraphScaleTests
{
    /// <summary>What every timed case is allowed. It is not a benchmark: it is the line between linear
    /// and quadratic at this size, and the quadratic version misses it by minutes.</summary>
    private const double BUDGET_SECONDS = 20;


    [Fact]
    public void Reading_the_key_a_solution_just_added_does_not_throw()
    {
        var graph = new CacheGraph();

        var added = graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(30), [], "cache"));

        Assert.Equal("k:1", added.Template);
        Assert.Equal(TimeSpan.FromSeconds(30), added.Ttl);
    }

    [Fact]
    public void Reading_the_key_an_observation_just_added_does_not_throw()
    {
        var graph = new CacheGraph();

        var added = graph.AddCacheKeyObservation("app", new CacheKey("k:1", "redis", null, [], null));

        Assert.Equal("redis", added.Store);
    }

    [Fact]
    public void Reading_the_table_just_added_does_not_throw()
    {
        var graph = new CacheGraph();

        var added = graph.AddTable("app", new Table("dbo.Products", "shop"));

        Assert.Equal("dbo.Products", added.Name);
    }

    [Fact]
    public void Reading_the_procedure_just_added_does_not_throw()
    {
        var graph = new CacheGraph();

        var added = graph.AddStoredProcedure("shop", new StoredProcedure("dbo.Recalculate", "shop"));

        Assert.Equal("dbo.Recalculate", added.Name);
    }

    [Fact]
    public void Reading_the_handler_just_added_does_not_throw()
    {
        var graph = new CacheGraph();

        var added = graph.AddHandler(new Handler("app", "App.Controller.Get", "http", "C.cs", 1) { Project = "Web" });

        Assert.Equal("Web", added.Project);
    }

    [Fact]
    public void Reading_an_edge_with_two_new_endpoints_does_not_throw()
    {
        var graph = new CacheGraph();
        var handler = new Handler("app", "App.Controller.Save", "http", "C.cs", 1);

        var added = graph.AddEdge(new Writes(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));

        Assert.Equal(handler, Assert.IsType<Handler>(added.From));
        Assert.Equal("dbo.Products", Assert.IsType<Table>(added.To).Name);
    }

    [Fact]
    public void Finding_a_vertex_does_not_rebuild_the_lists()
    {
        var graph = new CacheGraph();
        _ = graph.CacheKeys;
        var before = graph.VertexRebuilds;

        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, [], null));
        graph.AddTable("app", new Table("dbo.Products", "shop"));

        Assert.Equal(before, graph.VertexRebuilds);
    }

    [Fact]
    public void Reading_the_lists_twice_rebuilds_them_once()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, [], null));

        _ = graph.CacheKeys;
        _ = graph.CacheKeys;
        _ = graph.Tables;

        Assert.Equal(1, graph.VertexRebuilds);
    }

    [Fact]
    public void A_change_rebuilds_the_lists_exactly_once()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, [], null));
        _ = graph.CacheKeys;

        graph.AddCacheKey("app", new CacheKey("k:2", "memory", null, [], null));
        _ = graph.CacheKeys;
        _ = graph.CacheKeys;

        Assert.Equal(2, graph.VertexRebuilds);
    }

    [Fact]
    public void A_graph_of_two_thousand_handlers_and_keys_builds_within_the_budget()
    {
        var elapsed = Measure(() => BuildLarge(2000));

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"Building the graph took {elapsed:F0} ms.");
    }

    [Fact]
    public void Materialising_the_stored_edges_stays_within_the_budget()
    {
        var graph = BuildLarge(2000);

        var elapsed = Measure(() => Assert.Equal(6000, graph.StoredEdges.Count));

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"Materialising the edges took {elapsed:F0} ms.");
    }

    [Fact]
    public void Classifying_two_thousand_keys_stays_within_the_budget()
    {
        var graph = BuildLarge(2000);

        var elapsed = Measure(() => new CacheRoleClassifier().Classify(graph, "big"));

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"Classifying took {elapsed:F0} ms.");
        Assert.All(graph.CacheKeys, key => Assert.Equal("cache", key.Role));
    }

    /// <summary>
    /// Doubling the graph must not quadruple the work. The work is counted, not timed: dividing one
    /// wall-clock reading of a few tens of milliseconds by another measures the JIT, the collector and
    /// whatever else is running beside it, and this assertion failed in the gate on a run that passed
    /// locally minutes earlier.
    /// </summary>
    [Fact]
    public void Doubling_the_graph_less_than_quadruples_the_classification()
    {
        var single = ClassificationWork(BuildLarge(1000));
        var doubled = ClassificationWork(BuildLarge(2000));

        Assert.True(doubled <= 2 * single + SLACK,
                    $"1000 keys visited {single} handlers and 2000 visited {doubled}.");
    }

    /// <summary>
    /// Two thousand handlers each carrying an unresolved call. The budget is kept as a floor, but the
    /// assertion that matters is the work: at this size a lookup that scans every row for every handler
    /// costs four million row visits, which twenty seconds would not reliably catch on a fast machine.
    /// </summary>
    [Fact]
    public void Two_thousand_handlers_with_two_thousand_unresolved_calls_classify_within_the_budget()
    {
        var graph = BuildUnresolvedCalls(2000);

        var scans = UnresolvedLookupWork(graph);

        Assert.True(scans <= 4 * 2000 + SLACK, $"Classifying scanned {scans} unresolved rows for 2000 handlers.");
        Assert.All(graph.CacheKeys, key => Assert.Equal("unknown", key.Role));
    }

    /// <summary>
    /// The work the by-handler index of unresolved rows exists to bound. Counting reachability visits said
    /// nothing here: those are linear for this fixture whatever the lookup does, so the test named after
    /// the index was not guarding it. What grows quadratically without the index is the number of rows the
    /// lookups look at, and that is what is counted.
    /// </summary>
    [Fact]
    public void Doubling_the_unresolved_calls_less_than_quadruples_the_classification()
    {
        var single = UnresolvedLookupWork(BuildUnresolvedCalls(1000));
        var doubled = UnresolvedLookupWork(BuildUnresolvedCalls(2000));

        Assert.True(doubled <= 2 * single + SLACK,
                    $"1000 blocked keys scanned {single} unresolved rows and 2000 scanned {doubled}.");
    }

    private static long UnresolvedLookupWork(CacheGraph graph)
    {
        var before = graph.UnresolvedRowScans;
        new CacheRoleClassifier().Classify(graph, "big", new CacheRoleIndex(graph));
        return graph.UnresolvedRowScans - before;
    }

    /// <summary>
    /// One handler that caches N keys and reaches N handlers. Every key used to copy that handler's whole
    /// reachable set into a dictionary of its own, which is Θ(N²) however well the walk itself was
    /// memoised; the two questions a key actually asks are now memoised per handler, so the reachable set
    /// is walked once whatever the key count.
    /// </summary>
    [Fact]
    public void One_handler_caching_many_keys_walks_its_reachable_set_once()
    {
        var single = ClassificationWalks(BuildSharedReachability(500));
        var doubled = ClassificationWalks(BuildSharedReachability(1000));

        Assert.True(doubled <= 2 * single + SLACK,
                    $"500 keys took {single} reachability walks and 1000 took {doubled}.");
    }

    /// <summary>
    /// Hundreds of unresolved calls owned by one handler, removed in one batch. Removing them one at a
    /// time scanned that handler's list per id; sweeping each owner once makes the work grow with the
    /// rows rather than with their square.
    /// </summary>
    [Fact]
    public void Removing_many_unresolved_rows_of_one_handler_sweeps_its_list_once()
    {
        var single = RemovalWork(500);
        var doubled = RemovalWork(1000);

        Assert.True(doubled <= 2 * single + SLACK,
                    $"500 rows scanned {single} owner entries and 1000 scanned {doubled}.");
    }

    /// <summary>The slack that keeps a linear assertion from failing on a constant: the counters carry a
    /// fixed overhead that does not double with the input.</summary>
    private const long SLACK = 64;

    private static long ClassificationWork(CacheGraph graph)
    {
        var index = new CacheRoleIndex(graph);
        new CacheRoleClassifier().Classify(graph, "big", index);
        return index.ReachabilityVisits;
    }

    private static long ClassificationWalks(CacheGraph graph)
    {
        var index = new CacheRoleIndex(graph);
        new CacheRoleClassifier().Classify(graph, "big", index);
        return index.ReachabilityWalks;
    }

    /// <summary>
    /// N caching handlers that all call one helper, which calls N handlers that each read a table. The
    /// graph is linear in N, and so is the answer every caching handler needs — but memoising each
    /// handler's whole reachable set gave every one of the N callers its own set of N+2 entries, so both
    /// the walking and the memory were Θ(N²) on a shape that says nothing new after the first caller.
    /// The one-key-many-handlers test could not see it: it has a single caching handler.
    /// </summary>
    [Fact]
    public void Many_handlers_sharing_one_helper_walk_the_helper_once()
    {
        var single = ClassificationWork(BuildSharedHelper(200));
        var doubled = ClassificationWork(BuildSharedHelper(400));

        Assert.True(single > 0, "the counter recorded no work at all, so this assertion would hold vacuously");
        Assert.True(doubled <= 2 * single + SLACK,
                    $"200 callers took {single} reachability visits and 400 took {doubled}.");
    }

    /// <summary>N caching handlers, one shared helper, and N handlers below it that each touch data.</summary>
    private static CacheGraph BuildSharedHelper(int count)
    {
        var graph = new CacheGraph();
        var helper = new Handler("big", "App.Helper", "method", "App.cs", 1);
        for (var index = 0; index < count; index++)
        {
            var caching = new Handler("big", $"App.Get{index}", "http", "App.cs", index + 2);
            graph.AddEdge(new Caches(caching, new CacheKey($"item:{index}:{{id}}", "redis", null, [], null), Confidence.Confirmed));
            graph.AddEdge(new Calls(caching, helper, Confidence.Confirmed));

            var child = new Handler("big", $"App.Child{index}", "method", "App.cs", index + 2);
            graph.AddEdge(new Calls(helper, child, Confidence.Confirmed));
            graph.AddEdge(new Reads(child, new Table($"dbo.Table{index}", "shop"), Confidence.Confirmed));
        }

        return graph;
    }

    /// <summary>One caching handler, <paramref name="count"/> keys on it, and a chain of
    /// <paramref name="count"/> handlers it can reach.</summary>
    private static CacheGraph BuildSharedReachability(int count)
    {
        var graph = new CacheGraph();
        var caching = new Handler("big", "App.Get", "http", "App.cs", 1);
        for (var index = 0; index < count; index++)
            graph.AddEdge(new Caches(caching, new CacheKey($"item:{index}:{{id}}", "redis", null, [], null), Confidence.Confirmed));

        var previous = caching;
        for (var index = 0; index < count; index++)
        {
            var next = new Handler("big", $"App.Step{index}", "method", "App.cs", index + 2);
            graph.AddEdge(new Calls(previous, next, Confidence.Confirmed));
            previous = next;
        }

        graph.AddEdge(new Reads(previous, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        return graph;
    }

    private static long RemovalWork(int rows)
    {
        var workspace = new CacheGraph();
        var replacement = new CacheGraph();
        var owner = new Handler("big", "App.Owner", "http", "App.cs", 1);
        replacement.AddEdge(new Caches(owner, new CacheKey("owner:{id}", "redis", null, [], null), Confidence.Confirmed));
        for (var index = 0; index < rows; index++)
            replacement.AddUnresolved(UnresolvedKind.Call, owner, "App.cs", index + 1, $"Call{index}()", "unknown call");

        workspace.ReplaceSolution("big", replacement);
        var before = workspace.UnresolvedOwnerScans;
        workspace.ReplaceSolution("big", new CacheGraph());
        return workspace.UnresolvedOwnerScans - before;
    }

    [Fact]
    public void A_second_indexing_run_of_the_same_solution_stays_within_the_budget()
    {
        var workspace = new CacheGraph();
        workspace.ReplaceSolution("big", BuildLarge(2000));

        var elapsed = Measure(() => workspace.ReplaceSolution("big", BuildLarge(2000)));

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"The second run took {elapsed:F0} ms.");
        Assert.Equal(2000, workspace.Handlers.Count);
    }

    [Fact]
    public void Building_the_graph_rebuilds_the_lists_no_times()
    {
        var small = BuildLarge(1000);
        var large = BuildLarge(2000);

        Assert.Equal(0, small.VertexRebuilds);
        Assert.Equal(0, large.VertexRebuilds);
    }

    [Fact]
    public void Re_indexing_does_not_grow_quadratically_in_the_unresolved_rows()
    {
        var single = ReplaceWork(2000);
        var doubled = ReplaceWork(4000);

        Assert.True(doubled <= 2 * single + SLACK,
                    $"2000 rows scanned {single} owner entries and 4000 scanned {doubled}.");
    }

    /// <summary>The owner-list work of replacing a solution whose rows are spread over its handlers.</summary>
    private static long ReplaceWork(int rows)
    {
        var workspace = new CacheGraph();
        workspace.ReplaceSolution("big", BuildUnresolvedCalls(rows));
        var before = workspace.UnresolvedOwnerScans;
        workspace.ReplaceSolution("big", BuildUnresolvedCalls(rows));
        return workspace.UnresolvedOwnerScans - before;
    }

    [Fact]
    public void Re_indexing_removes_every_unresolved_row_of_the_solution_in_one_pass()
    {
        var workspace = new CacheGraph();
        workspace.ReplaceSolution("big", BuildUnresolvedCalls(4000));
        Assert.Equal(4000, workspace.Unresolved.Count);

        var elapsed = Measure(() => workspace.ReplaceSolution("big", new CacheGraph()));

        Assert.Empty(workspace.Unresolved);
        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"Removing 4000 rows took {elapsed:F0} ms.");
    }

    [Fact]
    public void A_set_site_after_an_observation_displaces_the_observation()
    {
        var graph = new CacheGraph();
        graph.AddCacheKeyObservation("app", new CacheKey("k:1", "memory", null, [], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(60), [], null));

        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(graph.CacheKeys).Ttl);
    }

    [Fact]
    public void A_site_without_a_ttl_makes_the_ttl_null_for_good()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(60), [], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, [], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(120), [], null));

        Assert.Null(Assert.Single(graph.CacheKeys).Ttl);
    }

    [Fact]
    public void Tags_merge_by_intersection_and_by_union()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, ["a", "b"], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", null, ["b", "c"], null));

        var key = Assert.Single(graph.CacheKeys);

        Assert.Equal(["b"], key.TagsAll.Order(StringComparer.Ordinal));
        Assert.Equal(["a", "b", "c"], key.TagsAny.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TtlAgreed_is_false_when_the_sites_disagree()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(30), [], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(60), [], null));

        Assert.False(Assert.Single(graph.CacheKeys).TtlAgreed);
    }

    [Fact]
    public void TtlAgreed_is_true_when_the_sites_agree()
    {
        var graph = new CacheGraph();
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(30), [], null));
        graph.AddCacheKey("app", new CacheKey("k:1", "memory", TimeSpan.FromSeconds(30), [], null));

        Assert.True(Assert.Single(graph.CacheKeys).TtlAgreed);
    }

    [Fact]
    public void Reading_a_vertex_the_graph_does_not_hold_throws_and_names_it()
    {
        var graph = new CacheGraph();
        var handler = graph.AddHandler(new Handler("app", "App.Get", "http", "C.cs", 1));
        graph.AddCacheOperation(new CacheOperation(handler, new CacheKey("k:missing", "memory", null, [], null),
                                                   CacheSemantic.Get, false, [new Evidence("C.cs", 1)]));

        var error = Assert.Throws<InvalidOperationException>(() => graph.CacheOperations);

        Assert.Contains("k:missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_indexing_removes_a_vertex_of_the_previous_run()
    {
        var workspace = new CacheGraph();
        var first = new CacheGraph();
        first.AddTable("app", new Table("dbo.Gone", "shop"));
        first.AddTable("app", new Table("dbo.Kept", "shop"));
        workspace.ReplaceSolution("app", first);

        var second = new CacheGraph();
        second.AddTable("app", new Table("dbo.Kept", "shop"));
        workspace.ReplaceSolution("app", second);

        Assert.Equal("dbo.Kept", Assert.Single(workspace.Tables).Name);
    }

    [Fact]
    public void Re_indexing_leaves_no_ttl_or_tag_merged_with_the_removed_solution()
    {
        var workspace = new CacheGraph();
        workspace.ReplaceSolution("one", KeyGraph("one", TimeSpan.FromSeconds(30), ["x"]));
        workspace.ReplaceSolution("two", KeyGraph("two", TimeSpan.FromSeconds(90), ["y"]));
        Assert.Equal(TimeSpan.FromSeconds(90), Assert.Single(workspace.CacheKeys).Ttl);

        workspace.ReplaceSolution("two", new CacheGraph());

        var key = Assert.Single(workspace.CacheKeys);
        Assert.Equal(TimeSpan.FromSeconds(30), key.Ttl);
        Assert.Equal(["x"], key.TagsAny.Order(StringComparer.Ordinal));
        Assert.Equal(["x"], key.TagsAll.Order(StringComparer.Ordinal));
    }

    /// <summary>The role row arrives with the replacement and is answered against the merged graph: the
    /// handler that resolves it exists only after the bulk mutation, and the annotation edge that reaches
    /// the reading handler only comes alive once that handler is there.</summary>
    [Fact]
    public void The_classification_inside_a_replace_sees_the_new_vertices()
    {
        var workspace = new CacheGraph();
        var reader = new Handler("other", "Other.Read", "method", "O.cs", 1);
        workspace.AddEdge(new Reads(reader, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        var caching = new Handler("app", "App.Cache", "http", "A.cs", 1);
        workspace.AddAnnotationEdge(new Calls(caching, reader, Confidence.Likely) { AnnotationId = 1 });

        var replacement = new CacheGraph();
        replacement.AddEdge(new Caches(caching, new CacheKey("k:1", "memory", null, [], null), Confidence.Confirmed));
        replacement.AddUnresolved(UnresolvedKind.Call, caching, "A.cs", 2, "Unknown()", "unknown target");
        new CacheRoleClassifier().Classify(replacement, "app");
        Assert.Contains(replacement.Unresolved, item => item.Kind == UnresolvedKind.Role);

        workspace.ReplaceSolution("app", replacement);

        Assert.Equal("cache", Assert.Single(workspace.CacheKeys).Role);
        Assert.DoesNotContain(workspace.Unresolved, item => item.Kind == UnresolvedKind.Role);
    }

    [Fact]
    public void The_classified_role_reaches_the_key()
    {
        var graph = CachingGraph("app");

        new CacheRoleClassifier().ClassifyKey(graph, graph.CacheKeys.Single());

        Assert.Equal("cache", Assert.Single(graph.CacheKeys).Role);
    }

    [Fact]
    public void Classifying_a_key_does_not_change_the_graph_version()
    {
        var graph = CachingGraph("app");
        var before = graph.VersionToken;

        new CacheRoleClassifier().ClassifyKey(graph, graph.CacheKeys.Single());

        Assert.Equal(before, graph.VersionToken);
    }

    [Fact]
    public void An_override_set_after_a_materialisation_is_read_back()
    {
        var graph = CachingGraph("app");
        Assert.Null(Assert.Single(graph.CacheKeys).Role);

        graph.SetCacheKeyRoleOverride("k:1", "memory", "store");

        Assert.Equal("store", Assert.Single(graph.CacheKeys).Role);
    }

    [Fact]
    public void An_override_beats_the_classified_role()
    {
        var graph = CachingGraph("app");
        new CacheRoleClassifier().ClassifyKey(graph, graph.CacheKeys.Single());

        graph.SetCacheKeyRoleOverride("k:1", "memory", "store");

        Assert.Equal("store", Assert.Single(graph.CacheKeys).Role);
    }

    [Fact]
    public void Replacing_a_solution_carries_its_classified_roles_over()
    {
        var replacement = CachingGraph("app");
        new CacheRoleClassifier().Classify(replacement, "app");
        var workspace = new CacheGraph();

        workspace.ReplaceSolution("app", replacement);

        Assert.Equal("cache", Assert.Single(workspace.CacheKeys).Role);
    }

    [Fact]
    public void The_role_of_a_key_that_left_every_site_is_dropped()
    {
        var workspace = new CacheGraph();
        var classified = CachingGraph("app");
        new CacheRoleClassifier().Classify(classified, "app");
        workspace.ReplaceSolution("app", classified);

        workspace.ReplaceSolution("app", new CacheGraph());
        Assert.Empty(workspace.CacheKeys);
        workspace.AddCacheKey("later", new CacheKey("k:1", "memory", null, [], null));

        Assert.Null(Assert.Single(workspace.CacheKeys).Role);
    }

    [Fact]
    public void Solutions_that_agree_give_that_role()
    {
        var workspace = new CacheGraph();

        workspace.ReplaceSolution("one", Classified(CachingGraph("one"), "one"));
        workspace.ReplaceSolution("two", Classified(CachingGraph("two"), "two"));

        Assert.Equal("cache", Assert.Single(workspace.CacheKeys).Role);
        Assert.DoesNotContain(workspace.Unresolved, item => item.Kind == UnresolvedKind.Role);
    }

    [Fact]
    public void Solutions_that_disagree_give_unknown_and_a_role_row()
    {
        var workspace = new CacheGraph();

        workspace.ReplaceSolution("one", Classified(CachingGraph("one"), "one"));
        workspace.ReplaceSolution("two", Classified(StoringGraph("two"), "two"));

        Assert.Equal("unknown", Assert.Single(workspace.CacheKeys).Role);
        var row = Assert.Single(workspace.Unresolved, item => item.Kind == UnresolvedKind.Role);
        Assert.Equal("k:1", row.Snippet);
        Assert.Contains("disagree", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_replacement_orders_give_the_same_role()
    {
        var forwards = new CacheGraph();
        forwards.ReplaceSolution("one", Classified(CachingGraph("one"), "one"));
        forwards.ReplaceSolution("two", Classified(StoringGraph("two"), "two"));

        var backwards = new CacheGraph();
        backwards.ReplaceSolution("two", Classified(StoringGraph("two"), "two"));
        backwards.ReplaceSolution("one", Classified(CachingGraph("one"), "one"));

        Assert.Equal(Assert.Single(forwards.CacheKeys).Role, Assert.Single(backwards.CacheKeys).Role);
        Assert.Equal("unknown", Assert.Single(backwards.CacheKeys).Role);
    }

    [Fact]
    public void Dropping_one_of_two_disagreeing_solutions_restores_the_other()
    {
        var workspace = new CacheGraph();
        workspace.ReplaceSolution("one", Classified(CachingGraph("one"), "one"));
        workspace.ReplaceSolution("two", Classified(StoringGraph("two"), "two"));
        Assert.Equal("unknown", Assert.Single(workspace.CacheKeys).Role);

        workspace.ReplaceSolution("two", new CacheGraph());

        Assert.Equal("cache", Assert.Single(workspace.CacheKeys).Role);
        Assert.DoesNotContain(workspace.Unresolved, item => item.Kind == UnresolvedKind.Role);
    }

    private static CacheGraph Classified(CacheGraph graph, string solution)
    {
        new CacheRoleClassifier().Classify(graph, solution);
        return graph;
    }

    /// <summary>One key, cached by a handler that reads a table: a cache by the rule's own definition.</summary>
    private static CacheGraph CachingGraph(string solution)
    {
        var graph = new CacheGraph();
        var handler = new Handler(solution, $"{solution}.Get", "http", "C.cs", 1);
        graph.AddEdge(new Caches(handler, new CacheKey("k:1", "memory", null, [], null), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        return graph;
    }

    /// <summary>The same key, cached by a handler that reaches no data at all and nothing unresolved: a
    /// store by the rule's own definition, and the disagreement the graph has to report.</summary>
    private static CacheGraph StoringGraph(string solution)
    {
        var graph = new CacheGraph();
        var handler = new Handler(solution, $"{solution}.Put", "http", "C.cs", 1);
        graph.AddEdge(new Caches(handler, new CacheKey("k:1", "memory", null, [], null), Confidence.Confirmed));
        return graph;
    }

    private static CacheGraph KeyGraph(string solution, TimeSpan ttl, string[] tags)
    {
        var graph = new CacheGraph();
        graph.AddCacheKey(solution, new CacheKey("k:1", "memory", ttl, tags, null));
        return graph;
    }

    /// <summary><paramref name="size"/> handlers, <paramref name="size"/> keys and three times as many
    /// edges, each key cached by a handler that reads its own table.</summary>
    private static CacheGraph BuildLarge(int size)
    {
        var graph = new CacheGraph();
        for (var index = 0; index < size; index++)
        {
            var handler = new Handler("big", $"Big.Handler{index}", "http", "C.cs", index + 1) { Project = "Big" };
            var table = new Table($"dbo.T{index}", "shop");
            graph.AddEdge(new Writes(handler, table, Confidence.Confirmed));
            graph.AddEdge(new Reads(handler, table, Confidence.Confirmed));
            graph.AddEdge(new Caches(handler, new CacheKey($"k:{index}", "memory", null, [], null), Confidence.Confirmed));
        }

        return graph;
    }

    /// <summary>The shape that is quadratic without an unresolved-by-handler index: every key reaches no
    /// data, so every one of them asks the graph which rows block its own handler.</summary>
    private static CacheGraph BuildUnresolvedCalls(int size)
    {
        var graph = new CacheGraph();
        for (var index = 0; index < size; index++)
        {
            var handler = new Handler("big", $"Big.Handler{index}", "http", "C.cs", index + 1) { Project = "Big" };
            graph.AddEdge(new Caches(handler, new CacheKey($"k:{index}", "memory", null, [], null), Confidence.Confirmed));
            graph.AddUnresolved(UnresolvedKind.Call, handler, "C.cs", index + 1, "Unknown()", "unknown target");
        }

        return graph;
    }

    private static double Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
