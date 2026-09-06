using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Verification;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// Two questions, kept apart: whether verification begins at all, and whether what it reads is allowed to
/// refute anything. Folding them into one list threw findings away before a reading was taken, in exactly
/// the cases where a plain disagreement of fields would have been visible.
/// <para>Age answers neither. It is evidence that staleness is possible and numbers for the report, and
/// it never refutes — see <c>docs/adr/0012</c>.</para>
/// </summary>
public sealed class FindingVerifierApplicabilityTests
{
    private const string Template = "product:{id}";

    private static readonly VerifyConfiguration Verify = new();

    [Fact]
    public void A_key_whose_role_is_not_cache_does_not_start()
    {
        var (graph, key) = WithTable(role: "store");

        var applicability = FindingVerifier.Assess(graph, key, Verify, null, Matched());

        Assert.False(applicability.Starts);
        Assert.Contains("role is 'store'", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_outside_the_configured_list_does_not_start()
    {
        var (graph, key) = WithTable(store: "memory");

        var applicability = FindingVerifier.Assess(graph, key, Verify, null, Matched());

        Assert.False(applicability.Starts);
        Assert.Contains("'memory' is not listed in verify.stores", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_depends_on_no_table_does_not_start()
    {
        var graph = new CacheGraph();
        graph.AddEdge(new Caches(Handler("App.Get"), Key(), Confidence.Confirmed));

        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), Verify, null, Matched());

        Assert.False(applicability.Starts);
        Assert.Contains("depends on no table", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_that_disables_INFO_does_not_start()
    {
        var (graph, key) = WithTable();

        var applicability = FindingVerifier.Assess(graph, key, Verify, RedisReader.RefuseReason, Matched());

        Assert.False(applicability.Starts);
        Assert.Equal(RedisReader.RefuseReason, applicability.Reason);
    }

    [Fact]
    public void An_external_source_with_no_serves_runs_but_may_not_refute()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        graph.AddEdge(new Caches(handler, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, new ExternalSource("http", "GET", "rates", null, "App"), Confidence.Confirmed));

        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("external source", applicability.Reason, StringComparison.Ordinal);
        Assert.Single(applicability.Tables);
    }

    [Fact]
    public void An_unresolved_on_the_path_that_came_back_runs_but_may_not_refute()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        graph.AddEdge(new Caches(handler, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table(), Confidence.Confirmed));
        graph.AddUnresolved(UnresolvedKind.Call, handler, "C.cs", 4, "Unknown()", "unknown target");

        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("unresolved", applicability.Reason, StringComparison.Ordinal);
    }

    /// <summary>The branch that returned nothing is the one the graph is quietest about, so its unresolved
    /// rows are the ones most worth carrying out.</summary>
    [Fact]
    public void An_unresolved_on_a_branch_that_produced_no_dependency_runs_but_may_not_refute()
    {
        var graph = new CacheGraph();
        var reader = Handler("App.Get");
        var silent = Handler("App.Warm");
        graph.AddEdge(new Caches(reader, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, Table(), Confidence.Confirmed));
        graph.AddEdge(new Caches(silent, Key(), Confidence.Confirmed));
        graph.AddUnresolved(UnresolvedKind.Call, silent, "C.cs", 9, "Unknown()", "unknown target");

        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("unresolved", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_walk_that_hit_its_depth_limit_runs_but_may_not_refute()
    {
        var graph = new CacheGraph();
        var head = Handler("App.Get");
        graph.AddEdge(new Caches(head, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(head, Table(), Confidence.Confirmed));
        var previous = head;
        for (var index = 0; index < 15; index++)
        {
            var next = Handler($"App.Step{index}");
            graph.AddEdge(new Calls(previous, next, Confidence.Confirmed));
            previous = next;
        }

        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("depth limit", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_through_another_key_runs_but_may_not_refute()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        graph.AddEdge(new Caches(handler, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, new CacheKey("brand:{id}", "redis", null, [], "cache"), Confidence.Confirmed));

        var applicability = FindingVerifier.Assess(graph, Find(graph), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("another cache key", applicability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_match_runs_but_may_not_refute()
    {
        var (graph, key) = WithTable();
        var ambiguous = new KeyMatch(Template, "0badc0de", null, false, "the match is ambiguous");

        var applicability = FindingVerifier.Assess(graph, key, Verify, null, ambiguous);

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("ambiguous", applicability.Reason, StringComparison.Ordinal);
    }

    /// <summary>A source already on the path closes a cycle. Nothing further was there to see, so the walk
    /// is complete and refutation stays allowed.</summary>
    [Fact]
    public void Pruning_a_source_already_on_the_path_is_not_incompleteness()
    {
        var graph = new CacheGraph();
        var first = Handler("App.Get");
        var second = Handler("App.Helper");
        graph.AddEdge(new Caches(first, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(first, Table(), Confidence.Confirmed));
        graph.AddEdge(new Calls(first, second, Confidence.Confirmed));
        graph.AddEdge(new Calls(second, first, Confidence.Confirmed));

        var key = Find(graph);

        Assert.False(graph.WalkDependencies(key).DepthLimitReached);
        Assert.True(FindingVerifier.Assess(graph, key, Verify, null, Matched()).RefutationAllowed);
    }

    [Fact]
    public void Pruning_a_key_already_on_the_path_is_not_incompleteness()
    {
        var graph = new CacheGraph();
        var outer = Handler("App.Get");
        var inner = Handler("App.Inner");
        var other = new CacheKey("brand:{id}", "redis", null, [], "cache");
        graph.AddEdge(new Caches(outer, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(outer, Table(), Confidence.Confirmed));
        graph.AddEdge(new Reads(outer, other, Confidence.Confirmed));
        graph.AddEdge(new Caches(inner, other, Confidence.Confirmed));
        graph.AddEdge(new Reads(inner, Key(), Confidence.Confirmed));

        Assert.False(graph.WalkDependencies(Find(graph)).DepthLimitReached);
    }

    /// <summary>Parent cached at 11:00 from a child cached at 09:00, with the table changed at 10:00. Read
    /// at noon the parent looks an hour old against a table written two hours ago, and it is stale all the
    /// same, because what it holds came through the child.</summary>
    [Fact]
    public void A_parent_built_from_an_older_child_is_not_refuted_by_its_own_age()
    {
        var signal = FindingVerifier.Signal(entryAgeSeconds: 3600, tableAgeSeconds: 7200);

        Assert.NotEqual(VerificationOutcome.Refuted, signal.Outcome);
        Assert.Equal(VerificationOutcome.NotVerifiable, signal.Outcome);
    }

    /// <summary>Set at 09:00 with an hour to live, extended at 09:59, read at 10:00: the entry computes as
    /// sixty seconds old and is really an hour old. A table changed at 09:30 looks older than it.</summary>
    [Fact]
    public void An_entry_whose_deadline_was_extended_is_not_refuted_by_its_age()
    {
        var signal = FindingVerifier.Signal(entryAgeSeconds: 60, tableAgeSeconds: 1800);

        Assert.NotEqual(VerificationOutcome.Refuted, signal.Outcome);
        Assert.Contains("extended", signal.Reason, StringComparison.Ordinal);
    }

    /// <summary>A handler reads a row, the table changes while it is still working, and it then writes the
    /// value it already had. The entry is younger than the table's last write and holds stale data; the
    /// gap is the handler's own duration, which no clock margin bounds.</summary>
    [Fact]
    public void A_handler_that_wrote_an_old_value_after_the_table_changed_is_not_refuted()
    {
        var signal = FindingVerifier.Signal(entryAgeSeconds: 60, tableAgeSeconds: 120);

        Assert.NotEqual(VerificationOutcome.Refuted, signal.Outcome);
        Assert.Equal(VerificationOutcome.NotVerifiable, signal.Outcome);
    }

    [Fact]
    public void The_age_is_the_declared_ttl_less_what_is_left_of_it() =>
        Assert.Equal(240, FindingVerifier.EntryAge(declaredTtlSeconds: 300, remainingTtlSeconds: 60, ttlAgreed: true));

    [Fact]
    public void Sites_that_disagreed_about_the_ttl_give_no_age() =>
        Assert.Null(FindingVerifier.EntryAge(declaredTtlSeconds: 300, remainingTtlSeconds: 60, ttlAgreed: false));

    [Fact]
    public void An_entry_with_no_remaining_ttl_gives_no_age() =>
        Assert.Null(FindingVerifier.EntryAge(declaredTtlSeconds: 300, remainingTtlSeconds: null, ttlAgreed: true));

    /// <summary>Idle time is time since the last read, which any reader resets. It says nothing about when
    /// the value was written and takes no part in the age.</summary>
    [Fact]
    public void The_idle_time_does_not_influence_the_age()
    {
        var key = new CacheKey(Template, "redis", TimeSpan.FromSeconds(300), [], "cache");
        var match = Matched();

        var untouched = FindingVerifier.EntryAge(key, new CacheEntry(match, "{}", 60, 0, null));
        var busy = FindingVerifier.EntryAge(key, new CacheEntry(match, "{}", 60, 9999, null));

        Assert.Equal(240, untouched);
        Assert.Equal(untouched, busy);
    }

    /// <summary>The two readings are brought to one moment by subtraction, not by trusting that they were
    /// simultaneous.</summary>
    [Fact]
    public void A_reading_taken_later_is_reduced_to_the_moment_of_the_first()
    {
        var observed = FindingVerifier.AtObservation(reportedSecondsAgo: 95, secondsAfterFirstRead: 40,
                                                     clockMarginSeconds: VerifyConfiguration.DefaultClockMarginSeconds);

        Assert.Equal(FindingVerifier.Placement.Placed, observed.Placement);
        Assert.Equal(55, observed.SecondsAgo);
    }

    [Fact]
    public void A_gap_wider_than_the_margin_drops_the_age_signal()
    {
        var observed = FindingVerifier.AtObservation(reportedSecondsAgo: 95, secondsAfterFirstRead: 90, clockMarginSeconds: 60);

        Assert.Equal(FindingVerifier.Placement.BeyondMargin, observed.Placement);
        Assert.Null(observed.SecondsAgo);
        Assert.Equal(VerificationOutcome.NotVerifiable,
                     FindingVerifier.Signal(entryAgeSeconds: 240, tableAgeSeconds: null).Outcome);
    }

    /// <summary>
    /// A database asked forty seconds after the cache, answering "five seconds ago". The reduction is
    /// negative, and that is not missing data: the write landed thirty-five seconds <em>after</em> the
    /// cache entry was observed, which is a signal of its own. It used to come back as an unknown age, and
    /// the caller then announced a sixty-second clock margin as exceeded by a forty-second gap.
    /// </summary>
    [Fact]
    public void A_table_written_between_the_two_readings_is_a_signal_and_not_a_gap()
    {
        var observed = FindingVerifier.AtObservation(reportedSecondsAgo: 5, secondsAfterFirstRead: 40, clockMarginSeconds: 60);

        Assert.Equal(FindingVerifier.Placement.WrittenAfterObservation, observed.Placement);
        Assert.Null(observed.SecondsAgo);
        Assert.NotEqual(FindingVerifier.Placement.BeyondMargin, observed.Placement);

        // And on its own it carries the possible, with no entry age to compare against.
        var signal = FindingVerifier.Aggregate(entryAgeSeconds: null,
                                               [new TableAge("dbo.Products", null, null, WrittenAfterObservation: true)]);
        Assert.Equal(VerificationOutcome.Possible, signal.Outcome);
        Assert.Equal(FindingVerifier.WrittenAfterObservationReason, signal.Reason);
    }

    [Fact]
    public void A_table_written_after_the_entry_gives_the_possible_signal()
    {
        var signal = FindingVerifier.Signal(entryAgeSeconds: 240, tableAgeSeconds: 30);

        Assert.Equal(VerificationOutcome.Possible, signal.Outcome);
        Assert.Equal(240, signal.EntryAgeSeconds);
        Assert.Equal(30, signal.TableAgeSeconds);
    }

    /// <summary>
    /// A dependency reached through a stored procedure full of dynamic SQL. The procedure's unresolved row
    /// carries no solution and no owning handler — its site is the object's qualified name — so the walk's
    /// handler-only lookup never saw it, and a chain the graph plainly could not follow was treated as
    /// fully known and allowed to refute.
    /// </summary>
    [Fact]
    public void An_unresolved_row_on_a_visited_procedure_withholds_refutation()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        var procedure = new StoredProcedure("dbo", "RebuildReport", "shop");
        graph.AddEdge(new Caches(handler, Key(), Confidence.Confirmed));
        graph.AddEdge(new Calls(handler, procedure, Confidence.Confirmed));
        graph.AddEdge(new Reads(procedure, Table(), Confidence.Confirmed));
        graph.AddUnresolved(UnresolvedKind.Sql, solution: null, Evidence.InDatabase(procedure.Name, "shop"),
                            "EXEC(@sql)", "The procedure builds its statement at run time.");

        var applicability = FindingVerifier.Assess(graph, Find(graph), Verify, null, Matched());

        Assert.True(applicability.Starts);
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("unresolved", applicability.Reason, StringComparison.Ordinal);
    }
    /// <summary>
    /// A handler that reads a table on one branch and calls a stored procedure on another, with no database
    /// indexed. Nothing is <em>stored</em> as unresolved — which reason a procedure gap carries depends on
    /// what is indexed now, so <c>get_unresolved</c> derives it on query — and the dependency walk read only
    /// the stored rows. So the tool reported the gap while verification called the chain whole and let the
    /// table's row agreeing refute the finding, which is the refutation over an unseen branch R8 forbids.
    /// </summary>
    [Fact]
    public void A_procedure_whose_dependencies_are_unknown_withholds_refutation()
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        graph.AddEdge(new Caches(handler, Key(), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table(), Confidence.Confirmed));
        graph.AddEdge(new Calls(handler, new StoredProcedure("dbo.RecalculatePrices", null), Confidence.Confirmed));
        Assert.Empty(graph.Unresolved);

        var applicability = FindingVerifier.Assess(graph, Find(graph), Verify, null, Matched());

        Assert.True(applicability.Starts, "the readings are still worth taking");
        Assert.False(applicability.RefutationAllowed);
        Assert.Contains("dbo.RecalculatePrices", applicability.Reason, StringComparison.Ordinal);
    }

    private static (CacheGraph Graph, CacheKey Key) WithTable(string store = "redis", string role = "cache")
    {
        var graph = new CacheGraph();
        var handler = Handler("App.Get");
        graph.AddEdge(new Caches(handler, Key(store, role), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, Table(), Confidence.Confirmed));
        return (graph, Find(graph));
    }

    private static CacheKey Find(CacheGraph graph) =>
        graph.CacheKeys.Single(key => key.Template == Template);

    private static CacheKey Key(string store = "redis", string role = "cache") =>
        new(Template, store, null, [], role);

    private static Table Table() => new("dbo.Products", "shop");

    private static Handler Handler(string symbol) => new("app", symbol, "http", "C.cs", 1);

    private static KeyMatch Matched() =>
        new(Template, "0badc0de", new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "42" }, false, null);
}
