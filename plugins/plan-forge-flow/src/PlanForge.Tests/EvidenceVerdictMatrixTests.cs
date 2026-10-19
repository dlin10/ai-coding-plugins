using PlanForge.Advisors;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>R12's exact pair cells and every allowed combination of item-precedence axes.</summary>
public sealed class EvidenceVerdictMatrixTests
{
    /// <summary>The six obligation columns of the independently specified R12 pair table.</summary>
    private static readonly string[] OBLIGATIONS =
        ["explicit_obligation", "explicit_exclusion", "mentioned_only", "absent", "insufficient_context", "unchecked"];

    /// <summary>R12's 30 explicit expected pair verdicts, in obligation-column order; no wider cells.</summary>
    private static readonly PairRow[] PAIRS =
    [
        new("supports", ["covered", "excluded", "none", "none", "uncertain", "unchecked"]),
        new("contradicts", ["contradiction", "contradiction", "contradiction", "contradiction", "contradiction", "contradiction"]),
        new("unrelated", ["uncertain", "excluded", "none", "none", "uncertain", "unchecked"]),
        new("insufficient_context", ["uncertain", "uncertain", "uncertain", "uncertain", "uncertain", "unchecked"]),
        new("unchecked", ["unchecked", "unchecked", "unchecked", "unchecked", "unchecked", "unchecked"])
    ];

    /// <summary>The relevance axis, with null representing an implication's absent relevance question.</summary>
    private static readonly string?[] RELEVANCE = ["implies_action", "context_only", "insufficient_context", "unchecked", null];

    /// <summary>The six pair-verdict presence axes, whose power set includes the empty set.</summary>
    private static readonly string[] VERDICTS = ["contradiction", "covered", "excluded", "uncertain", "none", "unchecked"];

    /// <summary>Every one of the 30 exact pair cells returns its written R12 verdict.</summary>
    [Fact]
    public void Every_pair_cell_gets_its_expected_verdict()
    {
        foreach (var row in PAIRS)
        for (var column = 0; column < OBLIGATIONS.Length; column++)
            Assert.Equal(row.Expected[column], EvidenceVerdicts.Pair(Outcome(row.Relationship), Outcome(OBLIGATIONS[column])));
    }

    /// <summary>Every allowed relevance, pair-presence and non-exclusion-candidate cell follows R12.</summary>
    [Fact]
    public void Every_item_cell_gets_its_expected_status()
    {
        foreach (var cell in Cells())
            Assert.Equal(Expected(cell), EvidenceVerdicts.Item(cell.Relevance is null ? null : Outcome(cell.Relevance),
                                                             cell.PairVerdicts, cell.HasNonExclusionCandidate));
    }

    /// <summary>A contradiction beats unchecked obligations, unchecked relevance and every competing pair verdict.</summary>
    [Fact]
    public void A_contradiction_is_never_outranked()
    {
        foreach (var obligation in OBLIGATIONS)
            Assert.Equal("contradiction", EvidenceVerdicts.Pair(Outcome("contradicts"), Outcome(obligation)));
        foreach (var cell in Cells().Where(cell => cell.PairVerdicts.Contains("contradiction")))
            Assert.Equal("contradiction", EvidenceVerdicts.Item(cell.Relevance is null ? null : Outcome(cell.Relevance),
                                                              cell.PairVerdicts, cell.HasNonExclusionCandidate));
    }

    /// <summary>The matrices cover all axis values and subsets, excluding only a candidate with no pair.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(["supports", "contradicts", "unrelated", "insufficient_context", "unchecked"], PAIRS.Select(row => row.Relationship));
        Assert.Equal(["explicit_obligation", "explicit_exclusion", "mentioned_only", "absent", "insufficient_context", "unchecked"], OBLIGATIONS);
        Assert.All(PAIRS, row => Assert.Equal(6, row.Expected.Length));
        Assert.Equal(30, PAIRS.Sum(row => row.Expected.Length));
        var cells = Cells().ToArray();
        Assert.Equal(635, cells.Length);
        Assert.Equal(new string?[] { "implies_action", "context_only", "insufficient_context", "unchecked", null },
            cells.Select(cell => cell.Relevance).Distinct());
        Assert.Equal([false, true], cells.Select(cell => cell.HasNonExclusionCandidate).Distinct());
        Assert.Equal(["contradiction", "covered", "excluded", "uncertain", "none", "unchecked"],
            cells.SelectMany(cell => cell.PairVerdicts).Distinct().OrderBy(verdict => Array.IndexOf(VERDICTS, verdict)));
        foreach (var relevance in RELEVANCE)
        {
            var rows = cells.Where(cell => cell.Relevance == relevance).ToArray();
            Assert.Equal(64, rows.Count(cell => !cell.HasNonExclusionCandidate));
            Assert.Equal(63, rows.Count(cell => cell.HasNonExclusionCandidate));
            Assert.Equal(64, rows.Select(cell => string.Join(",", cell.PairVerdicts)).Distinct().Count());
            Assert.DoesNotContain(rows, cell => cell.HasNonExclusionCandidate && cell.PairVerdicts.Length == 0);
        }
    }

    /// <summary>R12 flags exactly five statuses without source drift, and all statuses with source drift.</summary>
    [Fact]
    public void Every_status_obeys_the_flag_rule()
    {
        (string Status, bool Flag)[] statuses =
        [
            ("contradiction", true), ("unchecked", true), ("context_only", false), ("covered", false),
            ("excluded", false), ("uncertain", true), ("no_candidates", true), ("no_coverage_established", true)
        ];
        foreach (var (status, flag) in statuses)
        {
            Assert.Equal(flag, EvidenceVerdicts.IsFlag(status, false));
            Assert.True(EvidenceVerdicts.IsFlag(status, true));
        }
    }

    /// <summary>Generates every exact item cell before applying the sole impossible-combination filter.</summary>
    private static IEnumerable<ItemCell> Cells()
    {
        foreach (var relevance in RELEVANCE)
        for (var mask = 0; mask < 1 << VERDICTS.Length; mask++)
        foreach (var candidate in new[] { false, true })
        {
            var pairs = VERDICTS.Where((_, bit) => (mask & (1 << bit)) != 0).ToArray();
            var cell = new ItemCell(relevance, pairs, candidate);
            if (Allowed(cell)) yield return cell;
        }
    }

    /// <summary>A non-exclusion candidate necessarily contributes a pair, so only its empty-pair cell is excluded.</summary>
    /// <param name="cell">The proposed exact item cell.</param>
    private static bool Allowed(ItemCell cell) => !cell.HasNonExclusionCandidate || cell.PairVerdicts.Length > 0;

    /// <summary>Computes the independent item expectation with each precedence branch attributed to R12.</summary>
    /// <param name="cell">The exact allowed item cell.</param>
    private static string Expected(ItemCell cell)
    {
        // R12: any contradiction pair wins before every other status.
        if (cell.PairVerdicts.Contains("contradiction")) return "contradiction";
        // R12: unchecked relevance or any unchecked pair wins next.
        if (cell.Relevance == "unchecked" || cell.PairVerdicts.Contains("unchecked")) return "unchecked";
        // R12: context-only relevance wins before any coverage or exclusion.
        if (cell.Relevance == "context_only") return "context_only";
        // R12: any covered pair wins before exclusions and uncertainty.
        if (cell.PairVerdicts.Contains("covered")) return "covered";
        // R12: any excluded pair wins before uncertainty.
        if (cell.PairVerdicts.Contains("excluded")) return "excluded";
        // R12: insufficient relevance context or any uncertain pair makes the item uncertain.
        if (cell.Relevance == "insufficient_context" || cell.PairVerdicts.Contains("uncertain")) return "uncertain";
        // R12: no candidate besides exclusion units means no_candidates.
        if (!cell.HasNonExclusionCandidate) return "no_candidates";
        // R12: otherwise no coverage has been established, also for an implication without relevance.
        return "no_coverage_established";
    }

    /// <summary>Creates a validated choice or a representative unchecked outcome for the matrix axes.</summary>
    /// <param name="value">The validated choice, or unchecked.</param>
    private static EvidenceQuestionOutcome Outcome(string value) =>
        value == "unchecked" ? new(null, "refused") : new(new Answered(value, new Dictionary<string, double> { [value] = 1 }, null), null);

    /// <summary>One relationship row with its six explicitly written expected pair verdicts.</summary>
    /// <param name="Relationship">The relationship choice or unchecked axis value.</param>
    /// <param name="Expected">The verdicts in obligation-column order.</param>
    private sealed record PairRow(string Relationship, string[] Expected);

    /// <summary>One exact combination of the item status rule's three axes.</summary>
    /// <param name="Relevance">The relevance choice, unchecked, or null for an implication.</param>
    /// <param name="PairVerdicts">The subset of pair verdicts present.</param>
    /// <param name="HasNonExclusionCandidate">Whether there is a candidate besides exclusion units.</param>
    private sealed record ItemCell(string? Relevance, string[] PairVerdicts, bool HasNonExclusionCandidate);
}
