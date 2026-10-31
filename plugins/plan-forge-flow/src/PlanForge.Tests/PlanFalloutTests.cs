using PlanForge.Acts;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The labels a plan-review round carries for the previous round's new findings, and the share of
/// them that is fallout. See docs/adr/0049.
/// </summary>
public sealed class PlanFalloutTests
{
    private static readonly string[] Owed = ["F-0010", "F-0011", "F-0012", "F-0013"];

    /// <summary>Round 1 has no revision behind it, and a run begun earlier has nothing recorded: nothing is owed, so nothing is asked.</summary>
    [Fact]
    public void Nothing_is_checked_while_no_finding_is_owed()
    {
        Assert.Null(PlanFallout.Check(1, null, null));
        Assert.Null(PlanFallout.Check(2, [], new PlanFallout(Own: ["F-0001"])));
    }

    [Fact]
    public void A_call_without_labels_is_refused_naming_every_owed_finding()
    {
        var refusal = Assert.Throws<FalloutLabelsException>(() => PlanFallout.Check(2, Owed, null));

        Assert.Contains("round 2", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("missing: F-0010, F-0011, F-0012, F-0013", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A label set that covers all but one ID is the near-miss a count-only check would let through.</summary>
    [Fact]
    public void Labels_that_leave_an_ID_out_are_refused_naming_only_that_ID()
    {
        var labels = new PlanFallout(Own: ["F-0010"], Sibling: ["F-0011"], Older: ["F-0013"]);

        var refusal = Assert.Throws<FalloutLabelsException>(() => PlanFallout.Check(3, Owed, labels));

        Assert.Contains("missing: F-0012", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not among its new findings", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four labels for four findings still fail when one of the four belongs to another round: the
    /// share would count a finding the round never raised, and leave the one it did raise unlabelled.
    /// </summary>
    [Fact]
    public void A_label_for_a_finding_the_round_did_not_raise_is_refused()
    {
        var labels = new PlanFallout(Own: ["F-0010"], Sibling: ["F-0011"], SameClass: ["F-0099"], Older: ["F-0013"]);

        var refusal = Assert.Throws<FalloutLabelsException>(() => PlanFallout.Check(3, Owed, labels));

        Assert.Contains("missing: F-0012", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("not among its new findings: F-0099", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_under_two_labels_is_refused()
    {
        var labels = new PlanFallout(Own: ["F-0010", "F-0011"], Sibling: ["F-0011"], SameClass: ["F-0012"], Older: ["F-0013"]);

        var refusal = Assert.Throws<FalloutLabelsException>(() => PlanFallout.Check(3, Owed, labels));

        Assert.Contains("under more than one label: F-0011", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_labels_are_counted_by_label()
    {
        var labels = new PlanFallout(Own: ["F-0010", "F-0011"], SameClass: ["F-0012"], Older: ["F-0013"]);

        var tally = PlanFallout.Check(3, Owed, labels);

        Assert.Equal(new FalloutTally(2, 0, 1, 1), tally);
        Assert.Equal(4, tally!.New);
        Assert.Equal(2, tally.Fallout);
    }

    /// <summary>
    /// Own and sibling are fallout; same-class and older are not. Exactly half counts, since "half a
    /// round or more" is the rule the code-review skill states, and a round with nothing new is not
    /// fallout of anything.
    /// </summary>
    /// <param name="own">Findings labelled <c>own</c>.</param>
    /// <param name="sibling">Findings labelled <c>sibling</c>.</param>
    /// <param name="sameClass">Findings labelled <c>sameClass</c>.</param>
    /// <param name="older">Findings labelled <c>older</c>.</param>
    /// <param name="mostly">Whether half the round or more is fallout.</param>
    [Theory]
    [InlineData(1, 0, 2, 0, false)]
    [InlineData(1, 1, 1, 1, true)]
    [InlineData(0, 1, 0, 1, true)]
    [InlineData(2, 1, 0, 2, true)]
    [InlineData(0, 0, 3, 1, false)]
    [InlineData(0, 0, 0, 0, false)]
    public void Half_a_round_or_more_of_own_and_sibling_is_mostly_fallout(int own, int sibling, int sameClass, int older, bool mostly)
    {
        Assert.Equal(mostly, new FalloutTally(own, sibling, sameClass, older).Mostly);
    }

    /// <summary>
    /// The count fell and no blocker is left, which ADR 0041 settled, but the round's findings are
    /// mostly the revisions' own: another round would find more of the same, so the user is asked.
    /// </summary>
    [Fact]
    public void A_falling_count_does_not_continue_a_round_that_is_mostly_fallout()
    {
        ReviewTally[] tallies = [new(6, false), new(4, false)];

        Assert.Null(ReviewCap.Continuation(tallies, new FalloutTally(1, 1, 1, 1)));
        Assert.Equal("6 → 4", ReviewCap.Continuation(tallies, new FalloutTally(1, 0, 1, 2)));
        Assert.Equal("6 → 4", ReviewCap.Continuation(tallies));
    }

    [Fact]
    public void The_flow_line_names_only_the_labels_that_hold_a_finding()
    {
        var labels = new PlanFallout(Own: ["F-0021"], Sibling: ["F-0024", "F-0025"], Older: ["F-0022"]);

        Assert.Equal("Plan review round 3: own F-0021; sibling F-0024, F-0025; older F-0022", labels.Line(3));
        Assert.Equal("own 1, sibling 2, sameClass 0, older 1", labels.Counts());
    }
}
