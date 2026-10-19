using PlanForge.Advisors;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks every R14 invalidation input against each judgment and inclusive sizing boundary.</summary>
public sealed class EvidenceInvalidationMatrixTests
{
    /// <summary>The independently enumerated input-change axis.</summary>
    private enum Changed
    {
        /// <summary>All inputs remain identical.</summary>
        Nothing,
        /// <summary>One shared requirement changes.</summary>
        Requirement,
        /// <summary>One shared exclusion changes.</summary>
        Exclusion,
        /// <summary>The shared Change-point list changes.</summary>
        ChangePoints,
        /// <summary>The primary candidate's exact text changes.</summary>
        Candidate,
        /// <summary>An unrelated unit's text changes.</summary>
        OtherUnit,
        /// <summary>Only the primary item's text changes.</summary>
        ItemText,
        /// <summary>Only the primary item's current locator window changes.</summary>
        ItemWindow,
        /// <summary>The requested model changes.</summary>
        RequestedModel,
        /// <summary>The rubric version changes.</summary>
        RubricVersion,
        /// <summary>The selection version changes.</summary>
        SelectionVersion,
        /// <summary>A neighbouring judgment's unit text changes.</summary>
        PackedNeighbourChanged,
        /// <summary>A neighbouring judgment disappears.</summary>
        PackedNeighbourRemoved,
        /// <summary>A new judgment joins the request.</summary>
        PackedNeighbourAdded,
        /// <summary>An unrelated unit is inserted before the candidate.</summary>
        OtherInsertedBefore,
        /// <summary>An unrelated unit before the candidate is removed.</summary>
        OtherRemovedBefore,
        /// <summary>Two unrelated units before the candidate exchange positions.</summary>
        OthersSwappedBefore,
        /// <summary>The candidate moves while its exact text stays unchanged.</summary>
        CandidateMoved
    }

    /// <summary>The judgment axis, including both sides of each size limit.</summary>
    private enum Judgment
    {
        /// <summary>The primary item's relevance judgment.</summary>
        Relevance,
        /// <summary>The primary item's candidate pair.</summary>
        Pair,
        /// <summary>Another item's pair for the same candidate.</summary>
        OtherItemPair,
        /// <summary>A pair at the inclusive total-size limit.</summary>
        TotalAtLimit,
        /// <summary>A pair one byte beyond the total-size limit.</summary>
        TotalOneByteOver,
        /// <summary>A pair at the inclusive state-plus-question limit.</summary>
        StateAtLimit,
        /// <summary>A pair one byte beyond the state-plus-question limit.</summary>
        StateOneByteOver
    }

    /// <summary>The exact plan prefix whose requirement and exclusion participate in every key's shared state.</summary>
    private const string PREFIX = "## Requirements\n1. **R1.** Preserve contract.\n**Out of scope.** Preserve extras.\n";
    /// <summary>A non-candidate unit that can move without changing shared state.</summary>
    private const string ALPHA = "## Alpha\nUnrelated prose alpha.\n";
    /// <summary>A second non-candidate unit for the swap axis.</summary>
    private const string BETA = "## Beta\nUnrelated prose beta.\n";
    /// <summary>The exact candidate whose text stays unchanged through every positional axis.</summary>
    private const string CANDIDATE = "## Candidate\nConsumerCache handles consumers.\n";
    /// <summary>A candidate packed beside the primary pair to exercise request independence.</summary>
    private const string NEIGHBOUR = "## Neighbour\nConsumerCache handles neighbours.\n";
    /// <summary>A newly packed candidate with no effect on the old pair's key.</summary>
    private const string ADDED = "## Added\nConsumerCache handles additions.\n";

    /// <summary>Literal R14 re-ask expectations, in judgment-axis order; these do not inspect generated keys.</summary>
    private static readonly IReadOnlyDictionary<Changed, bool[]> Reasked = new Dictionary<Changed, bool[]>
    {
        [Changed.Nothing]                = [false, false, false, false, false, false, false],
        [Changed.Requirement]            = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.Exclusion]              = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.ChangePoints]           = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.Candidate]              = [false, true,  true,  true,  true,  true,  true ],
        [Changed.OtherUnit]              = [false, false, false, false, false, false, false],
        [Changed.ItemText]               = [true,  true,  false, true,  true,  true,  true ],
        [Changed.ItemWindow]             = [true,  true,  false, true,  true,  true,  true ],
        [Changed.RequestedModel]         = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.RubricVersion]          = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.SelectionVersion]       = [true,  true,  true,  true,  true,  true,  true ],
        [Changed.PackedNeighbourChanged] = [false, false, false, false, false, false, false],
        [Changed.PackedNeighbourRemoved] = [false, false, false, false, false, false, false],
        [Changed.PackedNeighbourAdded]   = [false, false, false, false, false, false, false],
        [Changed.OtherInsertedBefore]    = [false, false, false, false, false, false, false],
        [Changed.OtherRemovedBefore]     = [false, false, false, false, false, false, false],
        [Changed.OthersSwappedBefore]     = [false, false, false, false, false, false, false],
        [Changed.CandidateMoved]         = [false, false, false, false, false, false, false]
    };

    /// <summary>One cell of the full Cartesian product.</summary>
    /// <param name="Changed">The changed key input or unrelated context.</param>
    /// <param name="Judgment">The judgment whose provenance and outcome are checked.</param>
    private sealed record Cell(Changed Changed, Judgment Judgment);

    /// <summary>Every change occurs against every judgment, with no excluded combinations.</summary>
    private static readonly Cell[] Cells = (from changed in Enum.GetValues<Changed>()
                                           from judgment in Enum.GetValues<Judgment>()
                                           select new Cell(changed, judgment)).ToArray();

    /// <summary>All cells exercise real partitioning, selection, rubric packing, validation and disk reuse.</summary>
    [Fact]
    public async Task Every_cell_gets_its_expected_answer()
    {
        foreach (var cell in Cells)
        {
            using var run = new EvidenceStoreTestRun();
            var before = Inputs(cell, false);
            var after = Inputs(cell, true);
            CheckPositionalInvariants(cell, before, after);
            var size = run.Advisor.Measure(new(EvidenceRubric.State(before.Shared, EvidenceRubric.Item(before.Item)), before.Primary.Questions));
            run.Advisor.Limits = Limits(cell.Judgment, size);
            var revisedSize = run.Advisor.Measure(new(EvidenceRubric.State(after.Shared, EvidenceRubric.Item(after.Item)), after.Primary.Questions));
            // Every mutation has equal byte length in the key's body inputs. Positional mutations leave those inputs identical.
            Assert.Equal(size, revisedSize);
            var first = await run.CallAsync(before.Shared, before.Item, before.Primary, before.Neighbours);
            Assert.False(first.CacheHit);
            var expectedReason = cell.Judgment is Judgment.TotalOneByteOver or Judgment.StateOneByteOver ? "too_large" : null;
            AssertOutcome(first.Stored!, expectedReason);
            var requests = run.Advisor.Requests;
            if (cell.Changed == Changed.RequestedModel) run.Advisor.RequestedModel = "model-2";
            var next = await run.CallAsync(after.Shared, after.Item, after.Primary, after.Neighbours,
                cell.Changed == Changed.RubricVersion ? "2" : "1", cell.Changed == Changed.SelectionVersion ? "2" : "1");
            var reasked = Reasked[cell.Changed][(int)cell.Judgment];
            Assert.True(next.CacheHit == !reasked, $"{cell}: expected {(reasked ? "re-evaluation" : "stored outcome")}");
            Assert.True((first.Key != next.Key) == reasked, cell.ToString());
            AssertOutcome(next.Stored!, expectedReason);
            foreach (var (name, outcome) in first.Stored!.Questions)
            {
                var current = next.Stored!.Questions[name];
                Assert.Equal(outcome.Answer?.Choice, current.Answer?.Choice);
                Assert.Equal(outcome.Answer?.Probabilities, current.Answer?.Probabilities);
                Assert.Equal(outcome.Answer?.Confidence, current.Answer?.Confidence);
            }
            if (reasked && expectedReason is null) Assert.True(run.Advisor.Requests > requests, cell.ToString());
            if (expectedReason is not null && after.Neighbours.Count == 0) Assert.Equal(requests, run.Advisor.Requests);
            if (!reasked && after.Neighbours.Count == 0) Assert.Equal(requests, run.Advisor.Requests);
        }
    }

    /// <summary>Both axes occur completely and exactly once per Cartesian cell.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(Enum.GetValues<Changed>(), Cells.Select(cell => cell.Changed).Distinct());
        Assert.Equal(Enum.GetValues<Judgment>(), Cells.Select(cell => cell.Judgment).Distinct());
        Assert.Equal(Enum.GetValues<Changed>(), Reasked.Keys);
        Assert.All(Reasked.Values, row => Assert.Equal(Enum.GetValues<Judgment>().Length, row.Length));
        Assert.Equal(18 * 7, Cells.Length);
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
    }

    /// <summary>The pipeline inputs obtained from an actual plan, not synthetic outline ids.</summary>
    /// <param name="Units">The complete plan partition.</param>
    /// <param name="Shared">The exact shared requirements, exclusions and Change points.</param>
    /// <param name="Item">The primary or comparison item.</param>
    /// <param name="Primary">The selected judgment.</param>
    /// <param name="Neighbours">Other selected pairs packed into the request when they fit.</param>
    private sealed record Input(IReadOnlyList<PlanUnit> Units, EvidenceSharedState Shared, EvidenceItem Item,
                                EvidenceJudgment Primary, IReadOnlyList<EvidenceJudgment> Neighbours);

    /// <summary>Applies one isolated mutation, preserving body lengths for the four boundary judgments.</summary>
    /// <param name="cell">The independently labelled matrix cell.</param>
    /// <param name="revised">Whether to apply the change.</param>
    private static Input Inputs(Cell cell, bool revised)
    {
        var prefix = PREFIX;
        var blocks = new List<string> { ALPHA, BETA, CANDIDATE, NEIGHBOUR };
        string[] changePoints = ["ConsumerCache"];
        var item = EvidenceStoreTestRun.Item();
        if (revised)
        {
            switch (cell.Changed)
            {
                case Changed.Requirement: prefix = prefix.Replace("Preserve contract", "Maintain contract", StringComparison.Ordinal); break;
                case Changed.Exclusion: prefix = prefix.Replace("extras", "others", StringComparison.Ordinal); break;
                case Changed.ChangePoints: changePoints = ["ConsumerGraph"]; break;
                case Changed.Candidate: blocks[2] = CANDIDATE.Replace("handles", "updates", StringComparison.Ordinal); break;
                case Changed.OtherUnit: blocks[0] = ALPHA.Replace("alpha", "gamma", StringComparison.Ordinal); break;
                case Changed.ItemText: item = item with { Text = item.Text.Replace("consumers", "listeners", StringComparison.Ordinal) }; break;
                case Changed.ItemWindow: item = item with { CurrentCheck = new(true, "fedcba9876543210", null) }; break;
                case Changed.PackedNeighbourChanged: blocks[3] = NEIGHBOUR.Replace("handles", "updates", StringComparison.Ordinal); break;
                case Changed.PackedNeighbourRemoved: blocks.RemoveAt(3); break;
                case Changed.PackedNeighbourAdded: blocks.Add(ADDED); break;
                case Changed.OtherInsertedBefore: blocks.Insert(0, "## Inserted\nUnrelated inserted prose.\n"); break;
                case Changed.OtherRemovedBefore: blocks.RemoveAt(0); break;
                case Changed.OthersSwappedBefore: (blocks[0], blocks[1]) = (blocks[1], blocks[0]); break;
                case Changed.CandidateMoved: blocks.RemoveAt(2); blocks.Insert(0, CANDIDATE); break;
            }
        }
        if (cell.Judgment == Judgment.OtherItemPair)
            item = EvidenceStoreTestRun.Item() with { Id = "A2.confirmedFacts.1", Text = "ConsumerCache requires verification" };
        var units = PlanUnits.Of(prefix + string.Concat(blocks));
        var shared = EvidenceRubric.SharedState(units, changePoints);
        var candidates = CandidateSelection.Select(item, units, changePoints);
        var judgments = EvidenceRubric.Judgments(item, candidates);
        var primary = cell.Judgment == Judgment.Relevance ? judgments[0]
            : judgments.Single(judgment => judgment.Unit?.Text.StartsWith("## Candidate\n", StringComparison.Ordinal) == true);
        var neighbours = cell.Changed is Changed.PackedNeighbourChanged or Changed.PackedNeighbourRemoved or Changed.PackedNeighbourAdded
            ? judgments.Where(judgment => judgment.Unit?.Text.StartsWith("## Neighbour\n", StringComparison.Ordinal) == true
                || judgment.Unit?.Text.StartsWith("## Added\n", StringComparison.Ordinal) == true).ToArray()
            : [];
        return new(units, shared, item, primary, neighbours);
    }

    /// <summary>Sets one independently measured inclusive limit, leaving all other bounds unrestrictive.</summary>
    /// <param name="judgment">The size-boundary axis value.</param>
    /// <param name="size">The baseline primary judgment's exact byte sizes.</param>
    private static AdvisorLimits Limits(Judgment judgment, AdviceSize size) => new("bytes",
        judgment switch { Judgment.TotalAtLimit => size.Total, Judgment.TotalOneByteOver => size.Total - 1, _ => int.MaxValue },
        judgment switch { Judgment.StateAtLimit => size.StatePlusLargestQuestion, Judgment.StateOneByteOver => size.StatePlusLargestQuestion - 1, _ => int.MaxValue },
        64, 255, 1, TimeSpan.FromSeconds(30));

    /// <summary>Checks that every boundary preserves its original valid answers or persistent too_large reason.</summary>
    /// <param name="stored">The primary outcome from disk.</param>
    /// <param name="reason">The independently expected withheld reason, or null for an answer.</param>
    private static void AssertOutcome(StoredEvidenceJudgment stored, string? reason)
    {
        Assert.All(stored.Questions.Values, outcome =>
        {
            Assert.Equal(reason, outcome.Reason);
            if (reason is null) Assert.NotNull(outcome.Answer);
            else Assert.Null(outcome.Answer);
        });
    }

    /// <summary>Guards the positional axes against accidentally changing a state or candidate text.</summary>
    /// <param name="cell">The matrix cell identifying any positional mutation.</param>
    /// <param name="before">The original plan's inputs.</param>
    /// <param name="after">The revised plan's inputs.</param>
    private static void CheckPositionalInvariants(Cell cell, Input before, Input after)
    {
        if (cell.Changed is not (Changed.OtherInsertedBefore or Changed.OtherRemovedBefore or Changed.OthersSwappedBefore or Changed.CandidateMoved)) return;
        Assert.Equal(EvidenceRubric.Canonical(before.Shared), EvidenceRubric.Canonical(after.Shared));
        var first = before.Units.Single(unit => unit.Text.StartsWith("## Candidate\n", StringComparison.Ordinal));
        var next = after.Units.Single(unit => unit.Text.StartsWith("## Candidate\n", StringComparison.Ordinal));
        Assert.Equal(first.Text, next.Text);
        if (cell.Changed != Changed.OthersSwappedBefore) Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(EvidenceRubric.CanonicalUnit(EvidenceRubric.Judgments(before.Item, [first])[1]),
            EvidenceRubric.CanonicalUnit(EvidenceRubric.Judgments(after.Item, [next])[1]));
    }
}
