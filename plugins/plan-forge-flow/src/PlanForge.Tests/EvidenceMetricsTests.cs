using PlanForge.Advisors;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks metric arithmetic on hand-built observations independently of any Advisor or fixture script.</summary>
public sealed class EvidenceMetricsTests
{
    /// <summary>Candidate recall counts labelled pairs, including ones the selector missed.</summary>
    [Fact]
    public void Candidate_recall_counts_selected_labelled_pairs()
    {
        var report = Report([Item("a", candidates: ["T1"])], [Label("a", candidates: ["T1", "T2"])]);
        Assert.Equal(0.5, report.CandidateRecall);
    }

    /// <summary>Only checked labelled obligation questions enter the obligation-recall denominator.</summary>
    [Fact]
    public void Obligation_recall_excludes_unchecked_labelled_pairs()
    {
        var item = Item("a", judgments: [Pair("one", "T1", "supports", "explicit_obligation"),
            Pair("two", "T2", "supports", "mentioned_only"), Pair("three", "T3", "supports", null)]);
        var report = Report([item], [Label("a", obligations: ["T1", "T2", "T3", "T4"])]);
        Assert.Equal(0.5, report.ObligationRecall);
    }

    /// <summary>False coverage is measured against independent labels, not against the total item count.</summary>
    [Fact]
    public void False_coverage_uses_only_items_reported_covered()
    {
        var report = Report([Item("a"), Item("b"), Item("c", "uncertain")],
            [Label("a"), Label("b", "excluded"), Label("c", "covered")]);
        Assert.Equal(0.5, report.FalseCoverage);
    }

    /// <summary>A scripted status never counts as the truth: a fake's covered script cannot hide false coverage.</summary>
    [Fact]
    public void False_coverage_reads_the_truth_not_the_script()
    {
        var report = Report([Item("a")], [Label("a", "excluded", scriptStatus: "covered")]);
        Assert.Equal(1d, report.FalseCoverage);
    }

    /// <summary>Refused questions do not enter either side of the abstention ratio.</summary>
    [Fact]
    public void Abstentions_count_only_valid_checked_questions()
    {
        var report = Report([Item("a", judgments: [Relevance("r", "implies_action"),
            Pair("p", "T1", "insufficient_context", null)])], [Label("a")]);
        Assert.Equal(0.5, report.Abstentions);
    }

    /// <summary>Unchecked share uses every item, including implications.</summary>
    [Fact]
    public void Unchecked_share_counts_items()
    {
        var report = Report([Item("a", "unchecked"), Item("b"), Item("H1", locator: "none")],
            [Label("a"), Label("b"), Label("H1")]);
        Assert.Equal(1d / 3, report.UncheckedShare);
    }

    /// <summary>External evidence and implications are excluded from repository-locator validity.</summary>
    [Fact]
    public void Locator_validity_uses_repository_items_only()
    {
        var report = Report([Item("a", check: new(true, "0123456789abcdef", null)),
            Item("b", check: new(false, null, "missing_file")), Item("c"), Item("H1", locator: "none")],
            [Label("a"), Label("b"), Label("c"), Label("H1")]);
        Assert.Equal(0.5, report.LocatorValidity);
    }

    /// <summary>All empty denominators and latency populations are explicitly undefined.</summary>
    [Fact]
    public void Zero_denominators_are_null()
    {
        var report = Report([], []);
        Assert.Null(report.CandidateRecall);
        Assert.Null(report.ObligationRecall);
        Assert.Null(report.FalseCoverage);
        Assert.Null(report.Abstentions);
        Assert.Null(report.UncheckedShare);
        Assert.Null(report.LocatorValidity);
        Assert.Null(report.LatencyP50);
        Assert.Null(report.LatencyP90);
    }

    /// <summary>Unreported usage never becomes zero tokens or a zero cost.</summary>
    [Fact]
    public void Missing_usage_is_null_tokens_and_cost()
    {
        var report = Report([], [], [new() { Requests = 3 }, new() { Requests = 1 }]);
        Assert.Null(report.InputTokens);
        Assert.Null(report.OutputTokens);
        Assert.Null(report.CostUsd);
    }

    /// <summary>Output tokens do not contribute to the fixed jev-1.13.0 input-only cost.</summary>
    [Fact]
    public void Cost_uses_input_tokens_only_and_preserves_reported_zero()
    {
        var report = Report([], [], [new() { InputTokens = 1_000_000, OutputTokens = 9_000_000 }]);
        Assert.Equal(0.042m, report.CostUsd);
        Assert.Equal(1_000_000L, report.InputTokens);
        Assert.Equal(9_000_000L, report.OutputTokens);
        var zero = Report([], [], [new() { InputTokens = 0 }]);
        Assert.Equal(0m, zero.CostUsd);
        Assert.Null(zero.OutputTokens);
        Assert.Null(Report([], [], [new() { OutputTokens = 100 }]).CostUsd);
    }

    /// <summary>Counts are pooled across plans before division, and usage is pooled across all calls.</summary>
    [Fact]
    public void Two_plans_pool_counts_and_usage()
    {
        var report = EvidenceMetrics.Of([
            new(Map([Item("a", candidates: ["T1"])]), [Label("a", candidates: ["T1"])]),
            new(Map([Item("a")]), [Label("a", candidates: ["T1", "T2", "T3"])])],
            [new() { InputTokens = 100, OutputTokens = 20 }, new() { InputTokens = 300 }, new() { OutputTokens = 40 }]);
        Assert.Equal(0.25, report.CandidateRecall);
        Assert.Equal(400L, report.InputTokens);
        Assert.Equal(60L, report.OutputTokens);
        Assert.Equal(0.0000168m, report.CostUsd);
        Assert.True(report.Complete);
    }

    /// <summary>Pooling raw durations differs from taking a percentile of the calls' own percentiles.</summary>
    [Fact]
    public void Pooled_p90_uses_every_request_duration()
    {
        var first = new EvidenceMapProgress();
        first.AskDurations.AddRange([1, 1, 1, 1, 1, 1, 1, 1, 1, 100]);
        var second = new EvidenceMapProgress();
        second.AskDurations.Add(200);
        var report = Report([], [], [first, second]);
        Assert.Equal(1d, first.LatencyP90);
        Assert.Equal(200d, second.LatencyP90);
        Assert.Equal(1d, report.LatencyP50);
        Assert.Equal(100d, report.LatencyP90);
        Assert.NotEqual(new[] { first.LatencyP90, second.LatencyP90 }.Max(), report.LatencyP90);
    }

    /// <summary>An earlier received judgment is counted once from the last observation when it is cached.</summary>
    [Fact]
    public void Cached_judgments_are_counted_once_from_the_last_call()
    {
        var earlier = new EvidenceMapProgress
        {
            Observation = [Item("a", judgments: [Relevance("old", "insufficient_context")])]
        };
        var last = Item("a", judgments: [Relevance("old", "insufficient_context", fromStore: true),
            Relevance("fresh", "implies_action")]);
        var report = Report([last], [Label("a")], [earlier, new() { Observation = [last] }]);
        Assert.Equal(0.5, report.Abstentions);
    }

    /// <summary>Multiple logical uses of a shared key do not multiply its checked-question outcomes.</summary>
    [Fact]
    public void Shared_judgment_keys_are_counted_once_per_plan()
    {
        var report = Report([Item("a", judgments: [Relevance("shared", "insufficient_context")]),
            Item("b", judgments: [Relevance("shared", "insufficient_context"), Relevance("other", "implies_action")])],
            [Label("a"), Label("b")]);
        Assert.Equal(0.5, report.Abstentions);
    }

    /// <summary>One incomplete last map makes the pooled report incomplete regardless of earlier progress.</summary>
    [Fact]
    public void Completion_comes_from_each_plans_last_result()
    {
        var complete = Map([]);
        var incomplete = complete with { Result = complete.Result with { Complete = false, Remaining = 1 } };
        Assert.False(EvidenceMetrics.Of([new(complete, []), new(incomplete, [])], []).Complete);
    }

    /// <summary>Builds a minimal report input without running the mapper.</summary>
    /// <param name="items">The last observation.</param>
    /// <param name="labels">The independent labels.</param>
    /// <param name="progress">All call progress, if any.</param>
    private static EvidenceMetricsReport Report(EvidenceItemObservation[] items, EvidenceLabel[] labels,
                                                EvidenceMapProgress[]? progress = null) =>
        EvidenceMetrics.Of([new(Map(items), labels)], progress ?? []);

    /// <summary>Builds a complete last-call envelope; metrics read the observation rather than its counts.</summary>
    /// <param name="items">The hand-built observation.</param>
    private static EvidenceMap Map(EvidenceItemObservation[] items) =>
        new(new("test", "model", "model", true, 0, 0, 0, 0, null, new Dictionary<string, int>(), [], [], "unused"), items);

    /// <summary>Builds an independent label with only the axes needed by an arithmetic example.</summary>
    /// <param name="id">The item identity.</param>
    /// <param name="status">The truth status.</param>
    /// <param name="candidates">The labelled candidate units.</param>
    /// <param name="obligations">The truth obligation units.</param>
    /// <param name="scriptStatus">The fake's scripted status, which metrics must never read.</param>
    private static EvidenceLabel Label(string id, string status = "covered", string[]? candidates = null,
                                       string[]? obligations = null, string scriptStatus = "unchecked") =>
        new(id, id, candidates ?? [], new(scriptStatus, null, []), new(status, obligations ?? []));

    /// <summary>Builds an item independently of production selection and verdict code.</summary>
    /// <param name="id">The item identity.</param>
    /// <param name="status">The observed status.</param>
    /// <param name="candidates">The observed candidate ids.</param>
    /// <param name="judgments">The observed question outcomes.</param>
    /// <param name="check">The repository check, or null for external evidence and implications.</param>
    /// <param name="locator">The external or implication locator marker.</param>
    private static EvidenceItemObservation Item(string id, string status = "covered", string[]? candidates = null,
                                                EvidenceJudgmentObservation[]? judgments = null, LocatorCheck? check = null,
                                                string locator = "external") =>
        new(id, status, false, check is null ? locator : "repository", check, candidates ?? [], judgments ?? []);

    /// <summary>Builds one checked relevance outcome with optional cache provenance.</summary>
    /// <param name="key">The shared judgment identity.</param>
    /// <param name="choice">The checked choice.</param>
    /// <param name="fromStore">Whether the last call read this outcome from the store.</param>
    private static EvidenceJudgmentObservation Relevance(string key, string choice, bool fromStore = false) =>
        new(key, "relevance", null, new Dictionary<string, EvidenceQuestionOutcome> { ["relevance"] = Outcome(choice) }, fromStore);

    /// <summary>Builds a pair with valid choices or a refused obligation.</summary>
    /// <param name="key">The shared judgment identity.</param>
    /// <param name="unit">The candidate unit identity.</param>
    /// <param name="relationship">The valid relationship choice.</param>
    /// <param name="obligation">The valid obligation choice, or null for refusal.</param>
    private static EvidenceJudgmentObservation Pair(string key, string unit, string relationship, string? obligation) =>
        new(key, "pair", unit, new Dictionary<string, EvidenceQuestionOutcome>
        {
            ["relationship"] = Outcome(relationship),
            ["obligation"] = obligation is null ? new(null, "refused") : Outcome(obligation)
        }, false);

    /// <summary>Builds a normalized checked answer, without relying on any model.</summary>
    /// <param name="choice">The valid choice for the hand-built outcome.</param>
    private static EvidenceQuestionOutcome Outcome(string choice) =>
        new(new Answered(choice, new Dictionary<string, double> { [choice] = 1 }), null);
}
