using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Rules;

/// <summary>Only a certain invalidation grants a suppression. A site whose key folded to several values,
/// or to one standing for several, removes exactly one of them at run time, so counting it as coverage
/// would hide the findings for the rest — and a suppressed finding is invisible where a false one is only
/// noisy. What changes is the modality, never the matching. See <c>docs/adr/0015</c>.</summary>
public sealed class MaySetSuppressionTests
{
    /// <summary>The writer removes <c>product:one</c> and nothing else, so that key is covered. Its
    /// finding for <c>product:two</c> is a real one and stays.</summary>
    [Fact]
    public async Task A_certain_single_valued_removal_still_suppresses()
    {
        var graph = await IndexAsync();

        Assert.DoesNotContain(Findings(graph), finding => Writes(finding, "CertainWriter") && finding.Key.Template == "product:one");
        Assert.Contains(Findings(graph), finding => Writes(finding, "CertainWriter") && finding.Key.Template == "product:two");
    }

    /// <summary>The same site, with the same two keys reachable, covers neither: it removes one of them
    /// at run time and only it knows which.</summary>
    [Fact]
    public async Task A_two_valued_removal_does_not_suppress_and_keeps_both_edges()
    {
        var graph = await IndexAsync();

        Assert.Contains(Findings(graph), finding => Writes(finding, "BranchyWriter") && finding.Key.Template == "product:one");
        Assert.Contains(Findings(graph), finding => Writes(finding, "BranchyWriter") && finding.Key.Template == "product:two");
        var edges = Removals(graph, "BranchyWriter");
        Assert.Equal(2, edges.Length);
        Assert.All(edges, edge => Assert.Equal(InvalidationModality.May, edge.Modality));
    }

    /// <summary>The removal a reader will find in the code is in the chain the report prints, carrying the
    /// reason it did not count. A finding that silently omitted it would send the reader to a `Remove` of
    /// this very key and leave them to conclude the tool is wrong.</summary>
    [Fact]
    public async Task A_two_valued_removal_appears_in_the_chain_with_its_reason()
    {
        var graph = await IndexAsync();

        var finding = Findings(graph).First(item => Writes(item, "BranchyWriter") && item.Key.Template == "product:one");
        var removal = Assert.Single(finding.Chain.OfType<Invalidates>());
        Assert.Equal("product:one", ((CacheKey)removal.To).Template);
        Assert.Equal(InvalidationModality.May, removal.Modality);
        Assert.NotNull(removal.Reason);
        Assert.Contains("does not count as coverage", removal.Reason!, StringComparison.Ordinal);
    }

    /// <summary>A certain removal suppresses, so it never reaches a chain; nothing is added to a finding the
    /// policy does not touch.</summary>
    [Fact]
    public async Task A_finding_with_no_possible_removal_keeps_the_chain_it_had()
    {
        var graph = await IndexAsync();

        var finding = Findings(graph).First(item => Writes(item, "CertainWriter") && item.Key.Template == "product:two");
        Assert.Empty(finding.Chain.OfType<Invalidates>());
    }

    /// <summary>A removal that may fire is not dead code.</summary>
    [Fact]
    public async Task A_two_valued_removal_is_not_an_orphan()
    {
        var graph = await IndexAsync();
        var orphans = new OrphanInvalidationRule().Evaluate(graph).Orphans;

        Assert.DoesNotContain(orphans, orphan => ((Handler)orphan.Invalidation.From).Symbol.Contains("BranchyWriter", StringComparison.Ordinal));
    }

    /// <summary>Nor is it a mismatch against a key it does cover.</summary>
    [Fact]
    public async Task A_two_valued_removal_is_not_a_pattern_mismatch()
    {
        var graph = await IndexAsync();
        var mismatches = new OrphanInvalidationRule().Evaluate(graph).PatternMismatches;

        Assert.DoesNotContain(mismatches, mismatch => ((Handler)mismatch.Invalidation.From).Symbol.Contains("BranchyWriter", StringComparison.Ordinal));
    }

    [Fact]
    public void A_may_removal_of_the_parent_no_longer_suppresses_the_stale_parent()
    {
        var graph = Scenario(out var parent, out var invalidator);
        graph.AddEdge(May(new Invalidates(invalidator, parent, Confidence.Confirmed)));

        Assert.Single(new StaleParentKeyRule().Evaluate(graph));
    }

    /// <summary>And the reader is shown that removal. The suppression was withheld because of it, so a
    /// finding that left it out would send someone to a `Remove` of the parent and leave them to conclude
    /// the tool is wrong — the same requirement `UNGUARDED_WRITE` already meets.</summary>
    [Fact]
    public void The_may_removal_of_the_parent_is_in_the_chain_with_its_reason()
    {
        var graph = Scenario(out var parent, out var invalidator);
        graph.AddEdge(May(new Invalidates(invalidator, parent, Confidence.Confirmed)));

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(graph));
        var removal = Assert.Single(finding.Chain.OfType<Invalidates>(),
                                    edge => ((CacheKey)edge.To).Template == parent.Template);
        Assert.Equal(InvalidationModality.May, removal.Modality);
        Assert.NotNull(removal.Reason);
        Assert.Contains("does not count as coverage", removal.Reason!, StringComparison.Ordinal);
    }

    /// <summary>It goes after the chain that was already there, so nothing reading the chain by position
    /// moves: the child removal the finding starts from is still first.</summary>
    [Fact]
    public void The_parent_removal_is_appended_rather_than_woven_in()
    {
        var graph = Scenario(out var parent, out var invalidator);
        graph.AddEdge(May(new Invalidates(invalidator, parent, Confidence.Confirmed)));

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(graph));
        Assert.Equal("child:{id}", ((CacheKey)((Invalidates)finding.Chain[0]).To).Template);
        Assert.Equal(parent.Template, ((CacheKey)((Invalidates)finding.Chain[^1]).To).Template);
    }

    /// <summary>A parent nothing may remove adds nothing: the chain a finding had before this is the chain
    /// it still has.</summary>
    [Fact]
    public void A_stale_parent_with_no_possible_removal_keeps_the_chain_it_had()
    {
        var graph = Scenario(out var parent, out _);

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(graph));
        Assert.DoesNotContain(finding.Chain.OfType<Invalidates>(),
                              edge => ((CacheKey)edge.To).Template == parent.Template);
    }

    [Fact]
    public void A_must_removal_of_the_parent_still_suppresses_the_stale_parent()
    {
        var graph = Scenario(out var parent, out var invalidator);
        graph.AddEdge(new Invalidates(invalidator, parent, Confidence.Confirmed));

        Assert.Empty(new StaleParentKeyRule().Evaluate(graph));
    }

    /// <summary>The sibling search that finds the child removal keeps accepting a may: a possible removal
    /// of the child is still a possible removal, and refusing it would delete the finding.</summary>
    [Fact]
    public void A_may_removal_of_the_child_still_starts_the_stale_parent()
    {
        var graph = Scenario(out _, out _, childRemovalIsMay: true);

        Assert.Single(new StaleParentKeyRule().Evaluate(graph));
    }

    [Fact]
    public void A_may_edge_is_still_rendered_in_the_chain()
    {
        var graph = Scenario(out _, out _, childRemovalIsMay: true);

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(graph));
        Assert.Contains(finding.Chain, edge => edge is Invalidates { Modality: InvalidationModality.May });
    }

    [Fact]
    public void A_may_removal_reached_across_an_event_does_not_suppress()
    {
        var graph = UnguardedScenario(out var key, out var writer);
        var consumer = Handler("Consumer", "OnChanged", "Consumer.API");
        var @event = new Event("Contracts.Changed");
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed));
        graph.AddEdge(May(new Invalidates(consumer, key, Confidence.Confirmed)));

        Assert.NotEmpty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void A_must_removal_reached_across_an_event_still_suppresses()
    {
        var graph = UnguardedScenario(out var key, out var writer);
        var consumer = Handler("Consumer", "OnChanged", "Consumer.API");
        var @event = new Event("Contracts.Changed");
        graph.AddEdge(new Publishes(writer, @event, Confidence.Confirmed));
        graph.AddEdge(new Consumes(@event, consumer, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(consumer, key, Confidence.Confirmed));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    /// <summary>A run-time choice between two prefixes removes only one of them. The matching is
    /// untouched — the prefix still covers by pattern — but it no longer covers certainly.</summary>
    [Fact]
    public void A_two_valued_prefix_removal_does_not_suppress()
    {
        var graph = UnguardedScenario(out _, out var writer);
        graph.AddEdge(May(new Invalidates(writer, new CacheKey("product:*", "memory", null, [], null),
                                          Confidence.Confirmed, semantic: CacheSemantic.RemoveByPrefix)));

        Assert.NotEmpty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void A_single_valued_prefix_removal_still_suppresses()
    {
        var graph = UnguardedScenario(out _, out var writer);
        graph.AddEdge(new Invalidates(writer, new CacheKey("product:*", "memory", null, [], null),
                                      Confidence.Confirmed, semantic: CacheSemantic.RemoveByPrefix));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void A_two_valued_tag_removal_does_not_suppress()
    {
        var graph = UnguardedScenario(out _, out var writer, tags: ["catalog"]);
        graph.AddEdge(May(new Invalidates(writer, new CacheKey("catalog", "memory", null, [], null),
                                          Confidence.Confirmed, semantic: CacheSemantic.RemoveByTag)));

        Assert.NotEmpty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void A_single_valued_tag_removal_still_suppresses()
    {
        var graph = UnguardedScenario(out _, out var writer, tags: ["catalog"]);
        graph.AddEdge(new Invalidates(writer, new CacheKey("catalog", "memory", null, [], null),
                                      Confidence.Confirmed, semantic: CacheSemantic.RemoveByTag));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    /// <summary>One member nameable, one not: the nameable one becomes an edge, and it may fire.</summary>
    [Fact]
    public async Task A_mixed_removal_produces_a_may_edge()
    {
        var graph = await IndexAsync();

        var edge = Assert.Single(Removals(graph, "MixedWriter"));
        Assert.Equal(InvalidationModality.May, edge.Modality);
        Assert.Contains(Findings(graph), finding => Writes(finding, "MixedWriter"));
    }

    /// <summary>The mark and not the count decides: one element built over a part that outgrew the bound
    /// is a choice wearing the shape of a certainty. Its template <c>product:{?}</c> does cover the
    /// templated key by skeleton, so this is a suppression withheld and not a mismatch.</summary>
    [Fact]
    public async Task A_single_element_removal_over_an_over_cap_part_does_not_suppress()
    {
        var graph = await IndexAsync();

        var edge = Assert.Single(Removals(graph, "OverCapWriter"));
        Assert.Equal(InvalidationModality.May, edge.Modality);
        Assert.True(CacheKeyCovering.Covers(edge, Templated(graph), Templated(graph).TagsAll));
        Assert.Contains(Findings(graph), finding => Writes(finding, "OverCapWriter") && finding.Key.Template == "product:{id}");
    }

    /// <summary>The folder never sees a compound assignment, so it folds to the initialiser alone — a
    /// value the code may never produce. That granted a suppression before; it no longer does.</summary>
    [Fact]
    public async Task A_removal_keyed_by_a_compound_updated_local_does_not_suppress()
    {
        var graph = await IndexAsync();

        var edge = Assert.Single(Removals(graph, "CompoundWriter"));
        Assert.Equal("product:one", ((CacheKey)edge.To).Template);
        Assert.Equal(InvalidationModality.May, edge.Modality);
        Assert.Contains(Findings(graph), finding => Writes(finding, "CompoundWriter") && finding.Key.Template == "product:one");
    }

    /// <summary>The other spelling folds to no literal at all, so it names no key to remove and grants no
    /// coverage to begin with. What this pins is that it still grants none.</summary>
    [Fact]
    public async Task A_removal_keyed_by_a_self_assigned_local_does_not_suppress()
    {
        var graph = await IndexAsync();

        Assert.Empty(Removals(graph, "SelfAssignWriter"));
        Assert.Contains(Findings(graph), finding => Writes(finding, "SelfAssignWriter") && finding.Key.Template == "product:one");
    }

    /// <summary>An annotation says what the key is; it does not say the site had no choice, so the modality
    /// has to survive the trip. This end is the pending operation the fold leaves behind; what
    /// <c>WorkspaceSession.AnnotateAsync</c> then builds from it is asserted by
    /// <c>MaySetAnnotationTests</c> in the CLI tests, which is where the session lives.</summary>
    [Fact]
    public async Task The_pending_operation_for_a_mixed_removal_carries_the_may()
    {
        var graph = await IndexAsync();

        var pending = Assert.Single(graph.PendingCacheOperations,
                                    operation => operation.Handler.Symbol.Contains("MixedWriter", StringComparison.Ordinal));

        Assert.Equal(InvalidationModality.May, pending.Modality);
        Assert.Equal(CacheSemantic.Remove, pending.Semantic);
    }

    /// <summary>A `set` is untouched: an entry may be written under any of its keys, so a branchy cache
    /// site still produces every key and nothing about it turns on modality.</summary>
    [Fact]
    public async Task The_caches_path_is_untouched()
    {
        var graph = await IndexAsync();
        var templates = graph.CacheKeys.Select(key => key.Template).ToArray();

        Assert.Contains("product:one", templates);
        Assert.Contains("product:two", templates);
    }

    /// <summary>The policy reaches the key-object path too. A removal whose key object comes from a local
    /// assigned in two branches may remove either, so it covers neither — and before the fold followed the
    /// local's assignments it named only the initialiser, uncollapsed, and suppressed both findings. See
    /// <c>docs/adr/0016</c>.</summary>
    [Fact]
    public async Task A_branchy_key_object_removal_does_not_suppress()
    {
        var graph = await IndexKeyObjectsAsync();

        var edges = Removals(graph, "ObjectBranchyWriter");
        Assert.Equal(2, edges.Length);
        Assert.All(edges, edge => Assert.Equal(InvalidationModality.May, edge.Modality));
        Assert.Contains(Findings(graph), finding => Writes(finding, "ObjectBranchyWriter") && finding.Key.Template == "product:alpha");
        Assert.Contains(Findings(graph), finding => Writes(finding, "ObjectBranchyWriter") && finding.Key.Template == "product:beta");
    }

    /// <summary>The same branch written as one expression, which used to fold to nothing at all.</summary>
    [Fact]
    public async Task A_conditional_key_object_removal_does_not_suppress()
    {
        var graph = await IndexKeyObjectsAsync();

        var edges = Removals(graph, "ObjectConditionalWriter");
        Assert.Equal(2, edges.Length);
        Assert.All(edges, edge => Assert.Equal(InvalidationModality.May, edge.Modality));
        Assert.Contains(Findings(graph), finding => Writes(finding, "ObjectConditionalWriter") && finding.Key.Template == "product:alpha");
        Assert.Contains(Findings(graph), finding => Writes(finding, "ObjectConditionalWriter") && finding.Key.Template == "product:beta");
    }

    /// <summary>And the case the policy must leave alone: one construction, named certainly.</summary>
    [Fact]
    public async Task A_certain_key_object_removal_still_suppresses()
    {
        var graph = await IndexKeyObjectsAsync();

        var edge = Assert.Single(Removals(graph, "ObjectCertainWriter"));
        Assert.Equal(InvalidationModality.Must, edge.Modality);
        Assert.DoesNotContain(Findings(graph), finding => Writes(finding, "ObjectCertainWriter") && finding.Key.Template == "product:alpha");
        Assert.Contains(Findings(graph), finding => Writes(finding, "ObjectCertainWriter") && finding.Key.Template == "product:beta");
    }

    private static async Task<CacheGraph> IndexAsync()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/MaySets.cs");
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }

    private static async Task<CacheGraph> IndexKeyObjectsAsync()
    {
        var recognizer = new CacheRecognizer("KeyObjectMayFixture.IMayCache", "memory",
                                             [new CacheMethodRecognizer("Set", CacheSemantic.Set, 0),
                                              new CacheMethodRecognizer("Remove", CacheSemantic.Remove, 0)]) with
        {
            KeyObject = new KeyObjectRecognizer("KeyObjectMayFixture.MayCacheKey", 0,
                [new KeyObjectFactory("KeyObjectMayFixture.IMayKeyService", ["PrepareKey"], 0, 1)])
        };
        var solution = await FixtureSolution.CreateAsync("SourceFiles/KeyObjectMaySets.cs");
        return await new CallGraphIndexer(new IndexerOptions([recognizer, .. CacheRecognizers.All], EventRecognizers.All))
            .IndexAsync(solution, "fixture");
    }

    private static UnguardedWriteFinding[] Findings(CacheGraph graph) => [.. new UnguardedWriteRule().Evaluate(graph)];

    private static bool Writes(UnguardedWriteFinding finding, string writer) =>
        finding.Handler.Symbol.Contains(writer, StringComparison.Ordinal);

    private static Invalidates[] Removals(CacheGraph graph, string writer) =>
        [.. graph.Edges.OfType<Invalidates>().Where(edge => ((Handler)edge.From).Symbol.Contains(writer, StringComparison.Ordinal))];

    private static Invalidates May(Invalidates edge) => edge with { Modality = InvalidationModality.May };

    private static CacheKey Templated(CacheGraph graph) => graph.CacheKeys.Single(key => key.Template == "product:{id}");

    /// <summary>One handler caches a key over a table; another writes the table. Without a covering
    /// removal this is an unguarded write.</summary>
    private static CacheGraph UnguardedScenario(out CacheKey key, out Handler writer, string[]? tags = null)
    {
        var graph = new CacheGraph();
        var table = new Table("dbo.Products", "default");
        var reader = Handler("Cache", "GetProduct", "Cache.API");
        writer = Handler("Writer", "UpdateProduct", "Writer.API");
        key = new CacheKey("product:{id}", "memory", TimeSpan.FromSeconds(3600), tags ?? [], "cache");
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, table, Confidence.Confirmed));
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        return graph;
    }

    /// <summary>A parent key that outlives the child it depends on, with the child removed by one
    /// handler — the shape STALE_PARENT_KEY starts from.</summary>
    private static CacheGraph Scenario(out CacheKey parent, out Handler invalidator, bool childRemovalIsMay = false)
    {
        var graph = new CacheGraph();
        parent = new CacheKey("parent:{id}", "memory", TimeSpan.FromSeconds(120), [], "cache");
        var child = new CacheKey("child:{id}", "memory", TimeSpan.FromSeconds(30), [], "cache");
        var parentCache = Handler("Cache", "GetParent", "Cache.API");
        var childCache = Handler("Cache", "GetChild", "Cache.API");
        invalidator = Handler("Writer", "InvalidateChild", "Writer.API");
        graph.AddEdge(new Caches(parentCache, parent, Confidence.Confirmed));
        graph.AddEdge(new Reads(parentCache, child, Confidence.Confirmed));
        graph.AddEdge(new Caches(childCache, child, Confidence.Confirmed));
        var removal = new Invalidates(invalidator, child, Confidence.Confirmed);
        graph.AddEdge(childRemovalIsMay ? May(removal) : removal);
        return graph;
    }

    private static Handler Handler(string solution, string symbol, string project) =>
        new(solution, symbol, "handler", $"{solution}.cs", 1) { Project = project };
}
