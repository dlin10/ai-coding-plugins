using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The exceptional exit of an <c>await</c> of a task that may leave with an exception before it joins the child it started
/// (R2), over every form of the waiter, every way the task may leave, every form of the task's join and every reader of the child's
/// write: the fault of a task comes after the task completed, not after the work it started, so a write after the waiter's handler, in
/// the next pass or above the task keeps its pair with the child's write exactly where the task may leave before its join — as for the
/// blocking waits — and a normal exit of the task and a task that does not leave read as they always have. The task may leave by the
/// exception of an earlier <c>await</c> as well (R3): one it does not catch skips the join, one it catches goes on to it, and one for
/// the child itself comes after the child completed. An <c>await</c> of a group listing the child is the last of these only after the
/// calls that build the group, which may throw, and those leave the child running as a call of the program does.</summary>
public sealed class AwaitLeavingWorkMatrixTests
{
    public enum Waiter { Await, AwaitAfterYield, AwaitInWork, Wait, GetResult }

    public enum Leave
    {
        None, Throw, Helper, EarlyReturn, CaughtThenJoin, EarlierWaitThrows,
        EarlierAwaitThrows, EarlierAwaitCaught, EarlierAwaitOfTheChild, EarlierWhenAllWithTheChild
    }

    public enum InnerJoin { Wait, Await }

    public enum Reader { AfterCatch, AboveMiddle, NextPass, AfterUncaught }

    /// <summary>A program call that faults after it yields, which an <c>await</c> of the task it returns throws.</summary>
    internal const string FAIL_ASYNC = """
        private static async Task FailAsync()
        {
            await Task.Yield();
            Fail();
        }

        """;

    /// <summary>One combination of the axes: how the waiter waits, how the awaited task may leave, how it joins its child and which
    /// write reads the child's.</summary>
    /// <param name="Waiter">How the waiter waits for the outer task.</param>
    /// <param name="Leave">How the outer task may leave before its join of the child.</param>
    /// <param name="Join">How the outer task joins the child.</param>
    /// <param name="Reader">Which write is paired with the child's, and under which handler.</param>
    public sealed record Cell(Waiter Waiter, Leave Leave, InnerJoin Join, Reader Reader)
    {
        public override string ToString() => $"{Waiter}/{Leave}/{Join}/{Reader}";
    }

    /// <summary>A cell whose answer is wider than its expectation — the pair stays where the requirements remove it — with the reason
    /// the analysis cannot be exact there without what the run does not build.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Reason">Why the analysis keeps the pair.</param>
    private sealed record Wider(Cell Cell, string Reason);

    /// <summary>The cells wider than their expectation, each with its reason; a cell that names no cell expecting no pair fails
    /// <see cref="Every_wider_cell_names_a_cell_that_expects_no_pair"/>. None is: every cell answers as the requirements say.</summary>
    private static readonly Wider[] WiderCells = [];

    public static IEnumerable<Cell> AllCells() =>
        from waiter in Enum.GetValues<Waiter>()
        from leave in Enum.GetValues<Leave>()
        from inner in Enum.GetValues<InnerJoin>()
        from reader in Enum.GetValues<Reader>()
        select new Cell(waiter, leave, inner, reader);

    public static TheoryData<Waiter, Leave, InnerJoin, Reader> Cells()
    {
        var cells = new TheoryData<Waiter, Leave, InnerJoin, Reader>();
        foreach (var cell in AllCells())
            cells.Add(cell.Waiter, cell.Leave, cell.Join, cell.Reader);
        return cells;
    }

    [Fact]
    public void Cells_cover_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(400, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Waiter>(), waiter => Assert.Contains(cells, cell => cell.Waiter == waiter));
        Assert.All(Enum.GetValues<Leave>(), leave => Assert.Contains(cells, cell => cell.Leave == leave));
        Assert.All(Enum.GetValues<InnerJoin>(), join => Assert.Contains(cells, cell => cell.Join == join));
        Assert.All(Enum.GetValues<Reader>(), reader => Assert.Contains(cells, cell => cell.Reader == reader));
        Assert.Contains(cells, cell => !Expected(cell));
        Assert.Contains(cells, Expected);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        var cells = AllCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.All(WiderCells, wider =>
        {
            Assert.Contains(wider.Cell.ToString(), cells);
            Assert.False(Expected(wider.Cell), $"{wider.Cell} expects a pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Reason));
        });
    }

    /// <summary>No cell is narrower than its expectation; a cell outside <see cref="WiderCells"/> answers exactly, and one inside it
    /// keeps the pair it is listed for.</summary>
    /// <param name="waiter">How the waiter waits for the outer task.</param>
    /// <param name="leave">How the outer task may leave before its join of the child.</param>
    /// <param name="join">How the outer task joins the child.</param>
    /// <param name="reader">Which write is paired with the child's, and under which handler.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Waiter waiter, Leave leave, InnerJoin join, Reader reader)
    {
        var cell = new Cell(waiter, leave, join, reader);
        var pair = Paired(cell);

        if (Expected(cell))
            Assert.True(pair, $"{cell}: the pair the requirements keep is gone.");
        else if (WiderCells.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.False(pair, $"{cell}: the pair the requirements remove is still there.");
    }

    /// <summary>Whether the requirements keep the pair of the child's write and the write the reader pairs it with.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell)
    {
        // R2: a normal exit of the awaited task reads as it always has — the task joined its child unless it returned before the join —
        // and an exception that leaves the waiter is no path to the write after it.
        if (cell.Reader == Reader.AfterUncaught)
            return cell.Leave == Leave.EarlyReturn;
        // R2: the exceptional exit of a task that may leave before its join of the child carries no completion of the child, whatever
        // the waiter's form and the task's join are, so the pair with a write past the handler stays. R3: the exception of an earlier
        // await of something else, which no handler of the task catches, skips the join like that of an earlier blocking wait. A group
        // listing the child is built by calls between the child's start and its join — the Task.Run that starts the group's other task
        // and the Task.WhenAll — each of which may throw, so the task may leave with the child running as it does through a program call.
        if (cell.Leave is Leave.Throw or Leave.Helper or Leave.EarlyReturn or Leave.EarlierWaitThrows or Leave.EarlierAwaitThrows
                       or Leave.EarlierWhenAllWithTheChild)
        {
            return true;
        }
        // R2: a task that joins its child on every path, an exception caught before the join included, leaves nothing running. R3: the
        // exception of an earlier await that the task catches goes on to the join, and that of an earlier await of the child itself
        // comes after the child completed, so the join it skips was no more than a second wait for it.
        return false;
    }

    /// <summary>Whether the child's write and the write the reader pairs it with form a pair in the cell's program.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Paired(Cell cell)
    {
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(Body(cell), BlockingWaitAssumptionMatrixTests.FAIL + FAIL_ASYNC));

        return BlockingWaitAssumptionMatrixTests.Pairs(run, "First", cell.Reader == Reader.NextPass ? "First" : "After");
    }

    /// <summary>The body of the worker's <c>ExecuteAsync</c>: the reader's code, run by the waiter as the cell says.</summary>
    /// <param name="cell">The cell.</param>
    private static string Body(Cell cell)
    {
        var read = Read(cell);
        return cell.Waiter switch
        {
            Waiter.AwaitAfterYield => $"await Task.Yield(); {read}",
            Waiter.AwaitInWork => $"await Task.Run(async () => {{ {read} }});",
            _ => read
        };
    }

    /// <summary>The reader's code: the awaited task, the waiter's wait for it under the reader's handler, and the write after it.</summary>
    /// <param name="cell">The cell.</param>
    private static string Read(Cell cell)
    {
        var task = Outer(cell.Leave, cell.Join);
        var caught = $"try {{ {WaitFor(cell.Waiter, "outer")} }} catch (Exception) {{ }}";
        var async = cell.Waiter is Waiter.Wait or Waiter.GetResult ? "" : "async ";
        return cell.Reader switch
        {
            Reader.AfterCatch => $"var outer = {task}; {caught} After();",
            Reader.AboveMiddle => $"var outer = Task.Run({async}() => {{ var middle = {task}; {WaitFor(cell.Waiter, "middle")} return 1; }}); {caught} After();",
            Reader.NextPass => $"for (var pass = 0; pass < 2; pass++) {{ var outer = {task}; {caught} }}",
            _ => $"var outer = {task}; {WaitFor(cell.Waiter, "outer")} After();"
        };
    }

    /// <summary>The waiter's wait for a task.</summary>
    /// <param name="waiter">How the waiter waits.</param>
    /// <param name="task">The name of the task.</param>
    private static string WaitFor(Waiter waiter, string task) => waiter switch
    {
        Waiter.Wait => $"{task}.Wait();",
        Waiter.GetResult => $"{task}.GetAwaiter().GetResult();",
        _ => $"await {task};"
    };

    /// <summary>A <c>Task.Run</c> whose task starts a child writing in <c>First</c>, may leave as given, then joins the child as given
    /// and completes with a value.</summary>
    /// <param name="leave">How the task may leave before its join of the child.</param>
    /// <param name="join">How the task joins the child.</param>
    private static string Outer(Leave leave, InnerJoin join)
    {
        var before = leave switch
        {
            Leave.Throw => "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();",
            Leave.Helper => "Fail();",
            Leave.EarlyReturn => "if (Environment.TickCount % 2 == 0) return 0;",
            Leave.CaughtThenJoin => "try { Fail(); } catch (Exception) { }",
            Leave.EarlierWaitThrows => "Task.Run(() => Fail()).Wait();",
            Leave.EarlierAwaitThrows => "await FailAsync();",
            Leave.EarlierAwaitCaught => "try { await FailAsync(); } catch (Exception) { }",
            Leave.EarlierAwaitOfTheChild => "await inner;",
            Leave.EarlierWhenAllWithTheChild => "await Task.WhenAll(inner, Task.Run(() => 2));",
            _ => ""
        };
        var wait = join == InnerJoin.Wait ? "inner.Wait();" : "await inner;";
        var async = join == InnerJoin.Await || leave is Leave.EarlierAwaitThrows or Leave.EarlierAwaitCaught or Leave.EarlierAwaitOfTheChild
                                                       or Leave.EarlierWhenAllWithTheChild
            ? "async "
            : "";
        return $"Task.Run({async}() => {{ var inner = Task.Run(() => {{ First(); return 1; }}); {before} {wait} return 1; }})";
    }
}
