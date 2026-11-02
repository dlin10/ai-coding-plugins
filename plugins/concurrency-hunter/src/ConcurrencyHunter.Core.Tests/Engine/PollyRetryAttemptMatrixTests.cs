using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The attempts of a Polly 7 retry under the built-in records (R4, R1), against the real <c>Polly.dll</c>: an attempt that
/// joins its work on every path does not overlap that work with the work of the next attempt, and one that may leave before its join —
/// directly, through a call or by the exception of an earlier <c>await</c> — leaves the pair of its work with the next attempt. The
/// records are chosen by the static type of the receiver, so the work of an attempt is ordered with the code after the call only under
/// the keyed receiver, and only where the attempt joins its work on every path; a policy declared as its base class or as an interface
/// keeps that pair whatever the attempt does.</summary>
public sealed class PollyRetryAttemptMatrixTests
{
    public enum Record { Sync, Async }

    public enum Receiver { Keyed, Base, Interface }

    public enum Attempt { JoinsChild, ThrowBeforeJoin, HelperBeforeJoin, CaughtThenJoin, EarlierAwaitThrows, EarlierAwaitCaught }

    public enum ChildJoin { Wait, Await }

    public enum Reader { NextAttempt, AfterCall }

    /// <summary>One combination of the axes: which record's call runs the attempts, the declared type of the policy, what the attempt does
    /// before it joins its work, how it joins and which write is paired with the work of the attempt.</summary>
    /// <param name="Record">The call that runs the attempts: the synchronous <c>Execute</c> or the asynchronous <c>ExecuteAsync</c>.</param>
    /// <param name="Receiver">The type the policy field is declared as.</param>
    /// <param name="Attempt">What the attempt does between starting its work and joining it.</param>
    /// <param name="Join">How the attempt joins its work.</param>
    /// <param name="Reader">The write paired with the work of the attempt: that of the next attempt or the one right after the call.</param>
    public sealed record Cell(Record Record, Receiver Receiver, Attempt Attempt, ChildJoin Join, Reader Reader)
    {
        public override string ToString() => $"{Record}/{Receiver}/{Attempt}/{Join}/{Reader}";
    }

    /// <summary>A cell whose answer is wider than its expectation — the pair stays where the requirements remove it — with the reason
    /// the analysis cannot be exact there without what the run does not build.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Reason">Why the analysis keeps the pair.</param>
    private sealed record Wider(Cell Cell, string Reason);

    /// <summary>The cells wider than their expectation, each with its reason; a cell that names no cell expecting no pair fails
    /// <see cref="Every_wider_cell_names_a_cell_that_expects_no_pair"/>.</summary>
    private static readonly Wider[] WiderCells =
    [
        .. AllCells().Where(cell => cell.Receiver == Receiver.Keyed && cell.Attempt == Attempt.EarlierAwaitCaught)
                     .Select(cell => new Wider(cell, "The attempt's join stands after an await that is no join of the pass, and the pass proof reads " +
                                                     "every such await as a way out of the attempt, even one caught in a try; the analysis has no " +
                                                     "fact that the await returns to the attempt and goes on to its join."))
    ];

    public static IEnumerable<Cell> AllCells() =>
        from call in Enum.GetValues<Record>()
        from receiver in Enum.GetValues<Receiver>()
        from attempt in Enum.GetValues<Attempt>()
        from wait in Enum.GetValues<ChildJoin>()
        from reader in Enum.GetValues<Reader>()
        select new Cell(call, receiver, attempt, wait, reader) into cell
        where Allowed(cell)
        select cell;

    public static TheoryData<Record, Receiver, Attempt, ChildJoin, Reader> Cells()
    {
        var cells = new TheoryData<Record, Receiver, Attempt, ChildJoin, Reader>();
        foreach (var cell in AllCells())
            cells.Add(cell.Record, cell.Receiver, cell.Attempt, cell.Join, cell.Reader);
        return cells;
    }

    /// <summary>Whether the program of a combination can be written: the delegate of the synchronous call is an <see cref="Action"/>,
    /// which cannot wait with <c>await</c>.</summary>
    /// <param name="cell">The combination.</param>
    private static bool Allowed(Cell cell) =>
        cell.Record == Record.Async ||
        cell.Attempt is not (Attempt.EarlierAwaitThrows or Attempt.EarlierAwaitCaught) && cell.Join == ChildJoin.Wait;

    [Fact]
    public void Cells_cover_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(96, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Record>(), record => Assert.Contains(cells, cell => cell.Record == record));
        Assert.All(Enum.GetValues<Receiver>(), receiver => Assert.Contains(cells, cell => cell.Receiver == receiver));
        Assert.All(Enum.GetValues<Attempt>(), attempt => Assert.Contains(cells, cell => cell.Attempt == attempt));
        Assert.All(Enum.GetValues<ChildJoin>(), join => Assert.Contains(cells, cell => cell.Join == join));
        Assert.All(Enum.GetValues<Reader>(), reader => Assert.Contains(cells, cell => cell.Reader == reader));
        Assert.Contains(cells, cell => !Expected(cell));
        Assert.Contains(cells, Expected);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        var cells = AllCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(4, WiderCells.Length);
        Assert.All(WiderCells, wider =>
        {
            Assert.Contains(wider.Cell.ToString(), cells);
            Assert.False(Expected(wider.Cell), $"{wider.Cell} expects a pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Reason));
        });
    }

    /// <summary>No cell is narrower than its expectation; a cell outside <see cref="WiderCells"/> answers exactly, and one inside it
    /// keeps the pair it is listed for.</summary>
    /// <param name="record">The call that runs the attempts.</param>
    /// <param name="receiver">The type the policy field is declared as.</param>
    /// <param name="attempt">What the attempt does before it joins its work.</param>
    /// <param name="join">How the attempt joins its work.</param>
    /// <param name="reader">The write paired with the work of the attempt.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Record record, Receiver receiver, Attempt attempt, ChildJoin join, Reader reader)
    {
        var cell = new Cell(record, receiver, attempt, join, reader);
        var pair = Paired(cell);

        if (Expected(cell))
            Assert.True(pair, $"{cell}: the pair the requirements keep is gone.");
        else if (WiderCells.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.False(pair, $"{cell}: the pair the requirements remove is still there.");
    }

    /// <summary>The exceptional exit of the <c>await</c> of the retry call's task — read directly and through <c>Task.WhenAll</c> —
    /// keeps the pair of a child an attempt left running with a write past the waiter's handler (R2), and an attempt that joins its
    /// child leaves none. The call is made once in a worker that runs once, so the admission of the record holds as it stands.</summary>
    /// <param name="shape">How the waiter awaits the call: <c>direct</c> or <c>when-all</c>.</param>
    /// <param name="leaves">Whether the attempt may throw before it awaits its child.</param>
    [Theory]
    [InlineData("direct", true)]
    [InlineData("direct", false)]
    [InlineData("when-all", true)]
    [InlineData("when-all", false)]
    public void Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(string shape, bool leaves)
    {
        var attempt = "async () => { var child = Task.Run(() => { First(); return 1; }); " +
                      (leaves ? "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); " : "") + "await child; }";
        var call = shape == "direct" ? $"await _policy.ExecuteAsync({attempt});" : $"await Task.WhenAll(_policy.ExecuteAsync({attempt}));";
        var run = Run($"try {{ {call} }} catch (Exception) {{ }} After();", "AsyncRetryPolicy _policy = Policy.Handle<InvalidOperationException>().RetryAsync(2);");

        Assert.True(leaves == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{shape}: expected the pair {(leaves ? "kept" : "removed")}.");
    }

    /// <summary>Whether the requirements keep the pair of the work of one attempt and the write the cell's reader pairs it with.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell)
    {
        // R4: an attempt that may throw before its join, directly, through a call or by the exception of an earlier await it does not
        // catch, leaves its work running into the next attempt and past the call.
        var leaves = cell.Attempt is Attempt.ThrowBeforeJoin or Attempt.HelperBeforeJoin or Attempt.EarlierAwaitThrows;
        // R1, R4: the delegate of an opaque call, handed from one place of an execution that runs once and passes that place once, goes
        // one at a time (CONTEXT, Unknown execution), so the receiver changes nothing for the next attempt: an attempt that joins its work
        // on every path — an exception caught before the join, or one of an earlier await it catches, included — does not overlap it.
        if (cell.Reader == Reader.NextAttempt)
            return leaves;
        // R1, R4: only the record orders the work of an attempt with the code after the call, and the record is chosen by the static
        // type of the receiver, so a policy declared as its base class or an interface takes none.
        return cell.Receiver != Receiver.Keyed || leaves;
    }

    /// <summary>Whether the work of one attempt and the write the cell's reader pairs it with form a pair in the cell's program.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Paired(Cell cell)
    {
        var field = (cell.Record, cell.Receiver) switch
        {
            (Record.Sync, Receiver.Keyed) => "RetryPolicy _policy = Policy.Handle<InvalidOperationException>().Retry(2);",
            (Record.Sync, Receiver.Base) => "Policy _policy = Policy.Handle<InvalidOperationException>().Retry(2);",
            (Record.Sync, _) => "ISyncPolicy _policy = Policy.Handle<InvalidOperationException>().Retry(2);",
            (_, Receiver.Keyed) => "AsyncRetryPolicy _policy = Policy.Handle<InvalidOperationException>().RetryAsync(2);",
            (_, Receiver.Base) => "AsyncPolicy _policy = Policy.Handle<InvalidOperationException>().RetryAsync(2);",
            _ => "IAsyncPolicy _policy = Policy.Handle<InvalidOperationException>().RetryAsync(2);"
        };

        return BlockingWaitAssumptionMatrixTests.Pairs(Run(Body(cell), field), "First", cell.Reader == Reader.NextAttempt ? "First" : "After");
    }

    /// <summary>The body of the worker's <c>ExecuteAsync</c>: the retry call whose delegate is the attempt, and the reader's write after
    /// the call when the cell asks for it.</summary>
    /// <param name="cell">The cell.</param>
    private static string Body(Cell cell)
    {
        var before = cell.Attempt switch
        {
            Attempt.ThrowBeforeJoin => "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();",
            Attempt.HelperBeforeJoin => "Fail();",
            Attempt.CaughtThenJoin => "try { Fail(); } catch (Exception) { }",
            Attempt.EarlierAwaitThrows => "await FailAsync();",
            Attempt.EarlierAwaitCaught => "try { await FailAsync(); } catch (Exception) { }",
            _ => ""
        };
        var join = cell.Join == ChildJoin.Wait ? "child.Wait();" : "await child;";
        var attempt = $"var child = Task.Run(() => {{ First(); return 1; }}); {before} {join}";
        var call = cell.Record == Record.Sync
            ? $"_policy.Execute(() => {{ {attempt} }});"
            : $"await _policy.ExecuteAsync(async () => {{ {attempt} await Task.CompletedTask; }});";

        return cell.Reader == Reader.AfterCall ? $"{call} After();" : call;
    }

    /// <summary>Analyses the once-launched worker whose <c>ExecuteAsync</c> runs <paramref name="body"/> and whose policy field is
    /// <paramref name="field"/>, compiled against the real <c>Polly.dll</c>.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    /// <param name="field">The type, name and initializer of the policy field, which the worker declares private and read-only.</param>
    private static EngineRun Run(string body, string field)
    {
        var source = Usings + "using Polly;\nusing Polly.Retry;\n" +
                     BlockingWaitAssumptionMatrixTests.Worker(body, $"private readonly {field}\n" + BlockingWaitAssumptionMatrixTests.FAIL +
                                                                    AwaitLeavingWorkMatrixTests.FAIL_ASYNC);
        var options = new FixtureOptions { MetadataReferences = LibraryPackages.BesideStubs };

        return AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", source)), "scope:Fixture");
    }
}
