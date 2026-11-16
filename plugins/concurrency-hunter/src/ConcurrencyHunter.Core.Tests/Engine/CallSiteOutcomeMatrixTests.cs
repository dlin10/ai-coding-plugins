using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteOutcomeMatrixTests
{
    private enum Member { Package, Framework }
    // None includes an executable body whose declaration id cannot name it for admission.
    private enum Body { Executable, None, LibraryDoesNotCompile }
    private enum History { AdmittedDeciding, AdmittedProducerFirstDecidingLast, NeverAdmittedDecidingEarlier, NeverAdmittedFirstDecidingLast }
    private sealed record Cell(Member Member, bool Dispatches, Body Body, bool OverBound, History History, bool DecidesLast);

    private static IEnumerable<Cell> Cells() =>
        from member in Enum.GetValues<Member>()
        from dispatches in new[] { false, true }
        from body in Enum.GetValues<Body>()
        from overBound in new[] { false, true }
        from history in Enum.GetValues<History>()
        from decidesLast in new[] { false, true }
        let cell = new Cell(member, dispatches, body, overBound, history, decidesLast)
        where Included(cell)
        select cell;

    private static bool Admitted(Cell cell) =>
        cell.History is History.AdmittedDeciding or History.AdmittedProducerFirstDecidingLast;

    private static bool HasAdmissibleBody(Cell cell) =>
        cell.Member == Member.Package && !cell.Dispatches && cell.Body == Body.Executable;

    private static bool Included(Cell cell)
    {
        // Framework members, dispatch and unavailable bodies can never have an admitted history.
        if (Admitted(cell) && !HasAdmissibleBody(cell)) return false;
        // The bound refuses a deciding call; an earlier producer admission still takes precedence.
        if (cell.History == History.AdmittedDeciding && cell.OverBound) return false;
        // An eligible call deciding before the last analysis is admitted in the next round.
        return cell.History != History.NeverAdmittedDecidingEarlier || !HasAdmissibleBody(cell) || cell.OverBound;
    }

    private static string Outcome(Cell cell)
    {
        var eligibility = cell.Member == Member.Framework ? "framework" :
            cell.Dispatches || cell.Body != Body.Executable ? "no-body" : null;
        return CallSiteOutcomes.Of(eligibility, Admitted(cell), cell.DecidesLast, cell.OverBound);
    }

    private static string Expected(Cell cell)
    {
        if (cell.Member == Member.Framework) return "framework";
        if (cell.Dispatches || cell.Body != Body.Executable) return "no-body";
        switch (cell.History)
        {
            case History.AdmittedDeciding:
            case History.AdmittedProducerFirstDecidingLast:
                return cell.DecidesLast ? "no-answer" : "answered";
            default:
                return cell.OverBound ? "bound" : "last-round";
        }
    }

    [Fact]
    public void Every_cell_is_exact()
    {
        foreach (var cell in Cells())
            Assert.True(Expected(cell) == Outcome(cell), $"{cell}: expected {Expected(cell)}, got {Outcome(cell)}");
    }

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(100, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Member>(), cells.Select(cell => cell.Member).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Dispatches).Distinct().Order());
        Assert.Equal(Enum.GetValues<Body>(), cells.Select(cell => cell.Body).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.OverBound).Distinct().Order());
        Assert.Equal(Enum.GetValues<History>(), cells.Select(cell => cell.History).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.DecidesLast).Distinct().Order());
        Assert.Equal(48, cells.Count(cell => cell.Member == Member.Framework));
        Assert.Equal(40, cells.Count(cell => cell.Member == Member.Package && !HasAdmissibleBody(cell)));
        Assert.Equal(12, cells.Count(HasAdmissibleBody));
        Assert.All(cells.Where(Admitted), cell => Assert.True(HasAdmissibleBody(cell)));
        Assert.DoesNotContain(cells, cell => cell.History == History.AdmittedDeciding && cell.OverBound);
        Assert.DoesNotContain(cells, cell => cell.History == History.NeverAdmittedDecidingEarlier &&
                                             HasAdmissibleBody(cell) && !cell.OverBound);
    }

    [Fact]
    public void An_admitted_producer_is_never_last_round()
    {
        var cells = Cells().Where(cell => cell.History == History.AdmittedProducerFirstDecidingLast).ToArray();
        Assert.Equal(4, cells.Length);
        Assert.Equal(2, cells.Count(cell => cell.OverBound));
        Assert.Equal(2, cells.Count(cell => cell.DecidesLast));
        foreach (var cell in cells)
        {
            var outcome = Outcome(cell);
            Assert.NotEqual("last-round", outcome);
            Assert.Equal(cell.DecidesLast ? "no-answer" : "answered", outcome);
        }
    }
}
