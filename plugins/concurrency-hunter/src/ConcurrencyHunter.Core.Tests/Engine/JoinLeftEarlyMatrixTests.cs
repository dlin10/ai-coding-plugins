using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures for the grid of joins left early (#505): for each mechanism a waiting execution relies on to know that work
/// another one started has ended, every way the work's owner can leave before its own join of that work — a throw, an early return, a
/// join on one branch, cancellation, a timeout, a loop with no normal exit, a join in <c>finally</c> — with the readers that rely on
/// it. A cell is judged against what .NET does at run time, never against the analysis: a cell whose answer is narrower than its
/// expectation is listed with its issue in <see cref="KnownMisses"/>, one whose answer is wider with its reason in
/// <see cref="WiderCells"/>, and every other cell answers exactly. The engine runs on <see cref="EngineFixture.Analyze(string,
/// AnalysisLimits?)"/>, which pairs the collected accesses rather than the folded ones production pairs, so a cell's answer is the
/// order the engine gives the two writes, not the finding a run reports.</summary>
public sealed class JoinLeftEarlyMatrixTests
{
    /// <summary>How the antecedent of a <c>ContinueWith</c> — a <c>Task.Run</c> that starts a child writing in <c>First</c> — may
    /// leave before its join of the child, or how it joins it.</summary>
    public enum Leave
    {
        /// <summary>Joins the child and returns.</summary>
        None,

        /// <summary>May throw before the join.</summary>
        Throw,

        /// <summary>Joins the child, then may throw.</summary>
        ThrowAfterJoin,

        /// <summary>Catches the fault of a call before the join, then joins.</summary>
        CaughtThenJoin,

        /// <summary>May return before the join.</summary>
        EarlyReturn,

        /// <summary>Joins the child on one branch only.</summary>
        ConditionalJoin,

        /// <summary>Calls <c>ThrowIfCancellationRequested</c> on the worker's token before the join.</summary>
        ThrowIfCanceled,

        /// <summary>Waits for the child under the worker's token: <c>Wait(token)</c> or <c>await WaitAsync(token)</c>.</summary>
        CanceledWait,

        /// <summary>Waits for the child with a timeout that returns normally: <c>Wait(100)</c> or
        /// <c>await Task.WhenAny(child, Task.Delay(100))</c>.</summary>
        TimedWait,

        /// <summary>Loops for ever, a call that may throw before the join in each pass: the antecedent never returns normally.</summary>
        LoopWithoutExit,

        /// <summary>Loops for ever, joining the child before the call that may throw.</summary>
        LoopJoinFirst,

        /// <summary>Joins the child in the <c>finally</c> of a <c>try</c> around a call that may throw.</summary>
        JoinInFinally
    }

    /// <summary>What the runtime does with the two writes of a cell.</summary>
    public enum Outcome
    {
        /// <summary>Both writes run and may overlap: the pair is kept.</summary>
        Pair,

        /// <summary>Both writes run, ordered: the pair is removed.</summary>
        NoPair,

        /// <summary>The reader's write never runs: there is no pair, whether or not the analysis collects the write.</summary>
        Unreachable
    }

    /// <summary>How the antecedent joins its child.</summary>
    public enum InnerJoin
    {
        /// <summary>A blocking wait in a synchronous lambda.</summary>
        Wait,

        /// <summary>An <c>await</c> in an async lambda.</summary>
        Await
    }

    /// <summary>The <c>TaskContinuationOptions</c> the continuation is created with.</summary>
    public enum Option
    {
        /// <summary>None: the continuation runs on every completion of the antecedent.</summary>
        Default,

        /// <summary><c>OnlyOnRanToCompletion</c>: the continuation runs only where the antecedent ran to completion, and its task is
        /// canceled, when the antecedent completes, otherwise.</summary>
        OnlyOnRanToCompletion
    }

    /// <summary>Which write is paired with the child's.</summary>
    public enum Reader
    {
        /// <summary>A write in the continuation's delegate.</summary>
        InContinuation,

        /// <summary>A write after an <c>await</c> of the continuation's task in a <c>try</c> whose <c>catch</c> takes every
        /// exception.</summary>
        AfterCaughtAwait,

        /// <summary>A write after an <c>await</c> of the continuation's task with
        /// <c>ConfigureAwaitOptions.SuppressThrowing</c>.</summary>
        AfterSuppressedAwait
    }

    /// <summary>One cell of the <c>ContinueWith</c> row: how the antecedent leaves, how it joins its child, the continuation's
    /// options and the reader.</summary>
    /// <param name="Leave">How the antecedent may leave before its join of the child.</param>
    /// <param name="Join">How the antecedent joins the child.</param>
    /// <param name="Option">The continuation's options.</param>
    /// <param name="Reader">Which write is paired with the child's.</param>
    public sealed record Cell(Leave Leave, InnerJoin Join, Option Option, Reader Reader)
    {
        /// <summary>The cell's name, its axes in order.</summary>
        public override string ToString() => $"{Leave}/{Join}/{Option}/{Reader}";
    }

    /// <summary>A cell whose answer differs from its expectation, with the issue or the reason that names why.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Why">The issue that owns the wrong answer, or why the analysis cannot be exact there.</param>
    private sealed record Listed(Cell Cell, string Why);

    /// <summary>The readers that run after an antecedent ends in any state: the continuation's delegate under the default options, and
    /// the writes after an <c>await</c> of the continuation's task under either option, since that task completes — run or
    /// <c>Canceled</c> — once the antecedent has.</summary>
    private static readonly (Option Option, Reader Reader)[] AnyEndReaders =
    [
        (Option.Default, Reader.InContinuation), (Option.Default, Reader.AfterCaughtAwait), (Option.Default, Reader.AfterSuppressedAwait),
        (Option.OnlyOnRanToCompletion, Reader.AfterCaughtAwait), (Option.OnlyOnRanToCompletion, Reader.AfterSuppressedAwait)
    ];

    /// <summary>The cells whose race the analysis misses: the pair the run keeps is gone. A join reaches the antecedent's end on its
    /// normal return alone, but the end leads to the continuation's start whatever the way the antecedent ended, so a continuation of
    /// work that may fault before its join of the child follows the child on the happens-before path — and so does the task of that
    /// continuation, whatever its options, since it completes only after the antecedent. Listed by hand, not through
    /// <see cref="Expected"/>, which <see cref="Every_listed_cell_names_a_cell_its_list_fits"/> checks against this list.</summary>
    private static readonly Listed[] KnownMisses =
    [
        .. Listing(Leave.Throw, AnyEndReaders, "#502: HappensBefore.BoundsEnd carries the join to the antecedent's end because every " +
                                               "normal return passes it, and HappensBefore.EndEdges leads that end to the " +
                                               "continuation's start whatever the way the antecedent ended, so the path orders the " +
                                               "child first though the fault leaves it running."),
        .. Listing(Leave.ThrowIfCanceled, AnyEndReaders, "#502: as for a throw; a cancellation check before the join ends the " +
                                                         "antecedent around it as a throw does.")
    ];

    /// <summary>The cells whose answer is wider than their expectation: the pair stays where the run removes it, or the reader the
    /// run reaches never runs. Listed by hand.</summary>
    private static readonly Listed[] WiderCells =
    [
        .. Listing(Leave.LoopJoinFirst, AnyEndReaders, "#508: the antecedent joins its child before every fault and has no normal " +
                                                       "return, yet a reader that runs after the fault keeps the pair; the engine code " +
                                                       "that keeps it is not established (HappensBefore.BoundsEnd asks Flow.AlwaysFollows, " +
                                                       "which holds where no normal exit is reachable, so the missing normal return " +
                                                       "alone does not explain it)."),
        .. Listing(Leave.LoopJoinFirst, [(Option.OnlyOnRanToCompletion, Reader.InContinuation)], OPTIONS_IGNORED),
        .. Listing(Leave.LoopWithoutExit, [(Option.OnlyOnRanToCompletion, Reader.InContinuation)], OPTIONS_IGNORED),
        .. Listing(Leave.CanceledWait, [(Option.OnlyOnRanToCompletion, Reader.InContinuation)], TOKEN_WAIT_IGNORED)
    ];

    /// <summary>Why a continuation that never runs keeps its pair: lowering drops the continuation's options.</summary>
    private const string OPTIONS_IGNORED = "IrLowering lowers ContinueWith as a spawn of IrSpawnKind.ContinueWith with its antecedent " +
                                           "and scheduler and reads no TaskContinuationOptions: the antecedent never runs to " +
                                           "completion, so the continuation never runs, yet its write is collected and paired.";

    /// <summary>Why a continuation that runs only after a cancellable wait returned keeps its pair.</summary>
    private const string TOKEN_WAIT_IGNORED = "IrLowering reads no TaskContinuationOptions, and it makes no join of a wait under a " +
                                              "token: Wait(token) is no IrJoinKind.Wait, which it makes of Wait() alone, and " +
                                              "WaitAsync(token) gives an IrTaskKind.CompletionOf task, which carries no join (by rule: " +
                                              "ADR 0030, SPEC TD-067).";

    /// <summary>Every cell of the <c>ContinueWith</c> row.</summary>
    public static IEnumerable<Cell> AllCells() =>
        from leave in Enum.GetValues<Leave>()
        from inner in Enum.GetValues<InnerJoin>()
        from option in Enum.GetValues<Option>()
        from reader in Enum.GetValues<Reader>()
        select new Cell(leave, inner, option, reader);

    /// <summary>The cells of the <c>ContinueWith</c> row as theory data.</summary>
    public static TheoryData<Leave, InnerJoin, Option, Reader> Cells()
    {
        var cells = new TheoryData<Leave, InnerJoin, Option, Reader>();
        foreach (var cell in AllCells())
            cells.Add(cell.Leave, cell.Join, cell.Option, cell.Reader);
        return cells;
    }

    /// <summary>The row's cells are distinct, cover every value of every axis, and hold cells that keep the pair and cells that
    /// remove it.</summary>
    [Fact]
    public void Cells_cover_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(144, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(76, cells.Count(cell => Expected(cell) == Outcome.Pair));
        Assert.Equal(64, cells.Count(cell => Expected(cell) == Outcome.NoPair));
        Assert.Equal(4, cells.Count(cell => Expected(cell) == Outcome.Unreachable));
    }

    /// <summary>Every listed cell is a cell of the row, listed once, with its reason, and the lists hold the counts the run record
    /// gives: 20 known misses, every one a cell the oracle keeps a pair in, and 16 wider cells, every one a cell the oracle removes the
    /// pair from or whose reader never runs. The lists are written by hand, so a change of the oracle that drops a cell shows here.</summary>
    [Fact]
    public void Every_listed_cell_names_a_cell_its_list_fits()
    {
        var cells = AllCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(20, KnownMisses.Length);
        Assert.Equal(10, KnownMisses.Count(miss => miss.Cell.Leave == Leave.Throw));
        Assert.Equal(16, WiderCells.Length);
        Assert.All(KnownMisses, miss => Assert.StartsWith("#502:", miss.Why, StringComparison.Ordinal));
        Assert.Equal(4, WiderCells.Count(wider => wider.Why == OPTIONS_IGNORED));
        Assert.Equal(2, WiderCells.Count(wider => wider.Why == TOKEN_WAIT_IGNORED));
        Assert.Equal(10, WiderCells.Count(wider => wider.Why.StartsWith("#508", StringComparison.Ordinal)));
        Assert.All(KnownMisses, miss =>
        {
            Assert.Contains(miss.Cell.ToString(), cells);
            Assert.True(Expected(miss.Cell) == Outcome.Pair, $"{miss.Cell} expects no pair, so it cannot be a miss.");
            Assert.False(string.IsNullOrWhiteSpace(miss.Why));
        });
        Assert.All(WiderCells, wider =>
        {
            Assert.Contains(wider.Cell.ToString(), cells);
            Assert.True(Expected(wider.Cell) != Outcome.Pair, $"{wider.Cell} expects a pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Why));
        });
        Assert.Equal(KnownMisses.Length + WiderCells.Length,
                     KnownMisses.Concat(WiderCells).Select(listed => listed.Cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(AllCells().Where(cell => Expected(cell) == Outcome.Unreachable),
                   cell => Assert.Contains(WiderCells, wider => wider.Cell == cell && wider.Why == OPTIONS_IGNORED));
    }

    /// <summary>A cell outside the lists answers exactly; a cell in <see cref="KnownMisses"/> misses the pair it is listed for, and
    /// one in <see cref="WiderCells"/> keeps it. The child's write must be collected in every cell; the reader's must be where it can
    /// run, and where it cannot (<see cref="Outcome.Unreachable"/>) the exact answer is no pair, with or without the write.</summary>
    /// <param name="leave">How the antecedent may leave before its join of the child.</param>
    /// <param name="join">How the antecedent joins the child.</param>
    /// <param name="option">The continuation's options.</param>
    /// <param name="reader">Which write is paired with the child's.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Continuation_cells_are_exact_outside_the_lists(Leave leave, InnerJoin join, Option option, Reader reader)
    {
        var cell = new Cell(leave, join, option, reader);
        var pair = Paired(cell);

        if (KnownMisses.Any(miss => miss.Cell == cell))
            Assert.False(pair, $"{cell}: listed as a known miss, but the pair is reported now; take it off the list.");
        else if (WiderCells.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.True((Expected(cell) == Outcome.Pair) == pair, $"{cell}: expected {Expected(cell)}.");
    }

    /// <summary>The status a foreign token's cancellation gives the antecedent decides an <c>OnlyOnFaulted</c> or <c>OnlyOnCanceled</c>
    /// continuation. The antecedent starts a child writing in <c>First</c>, calls <c>ThrowIfCancellationRequested</c> on the worker's
    /// token, then joins the child. In <c>Task.Run(Action)</c> the <c>OperationCanceledException</c> carries a token other than the
    /// task's, so the task ends <c>Faulted</c> (Task cancellation: «the exception's token doesn't match the Task's token … Faulted»); in
    /// an async lambda it ends <c>Canceled</c>, since an async method's builder sets any <c>OperationCanceledException</c> as
    /// cancellation (<c>AsyncTaskMethodBuilderT.SetException</c>, dotnet/runtime release/10.0). A continuation whose option matches
    /// that status runs while the child may still run; the other one never runs. Today the path orders every row: the matching rows
    /// are known misses of #502, and the never-run rows are exact only because no pair is reported.</summary>
    /// <param name="asynchronous">Whether the antecedent is an async lambda, ending <c>Canceled</c>, rather than a synchronous one,
    /// ending <c>Faulted</c>.</param>
    /// <param name="option">The continuation's option: <c>OnlyOnFaulted</c> or <c>OnlyOnCanceled</c>.</param>
    /// <param name="expected">The runtime's outcome.</param>
    [Theory]
    [InlineData(false, "OnlyOnFaulted", Outcome.Pair)]
    [InlineData(false, "OnlyOnCanceled", Outcome.Unreachable)]
    [InlineData(true, "OnlyOnFaulted", Outcome.Unreachable)]
    [InlineData(true, "OnlyOnCanceled", Outcome.Pair)]
    public void Continuation_on_the_status_a_foreign_cancellation_gives(bool asynchronous, string option, Outcome expected)
    {
        var antecedent = asynchronous
            ? "Task.Run(async () => { var inner = Task.Run(() => First()); stoppingToken.ThrowIfCancellationRequested(); await inner; })"
            : "Task.Run(() => { var inner = Task.Run(() => First()); stoppingToken.ThrowIfCancellationRequested(); inner.Wait(); })";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            $"var outer = {antecedent}; outer.ContinueWith(_ => After(), TaskContinuationOptions.{option});"));
        var child = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".First()", StringComparison.Ordinal));
        var write = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.Equal(TimingBranch.HappensBeforePath, BlockingWaitAssumptionMatrixTests.Branch(run, child, write));
        Assert.False(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                     expected == Outcome.Pair ? "#502: the known miss is reported now; expect the pair." : "a never-run continuation is paired.");
    }

    /// <summary>#502 confirmed: the antecedent may throw before its blocking join of the child, the continuation runs on that fault
    /// and writes while the child may still run, and the happens-before path through the antecedent's end orders the two writes;
    /// where the antecedent throws only after the join, the same path orders them rightly.</summary>
    /// <param name="early">Whether the antecedent may throw before its join, rather than after it.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Continuation_of_an_antecedent_faulting_before_its_join_is_ordered_by_the_path(bool early)
    {
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            $"var outer = {Antecedent(early ? Leave.Throw : Leave.ThrowAfterJoin, InnerJoin.Wait)}; outer.ContinueWith(_ => After());",
            BlockingWaitAssumptionMatrixTests.FAIL));
        var child = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".First()", StringComparison.Ordinal));
        var write = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.Equal(TimingBranch.HappensBeforePath, BlockingWaitAssumptionMatrixTests.Branch(run, child, write));
        Assert.Empty(BlockingWaitAssumptionMatrixTests.Assumed(run, child, write));
        Assert.False(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"));
    }

    /// <summary>The antecedent is the task of a helper, at one to three levels: <c>A</c> starts a child writing in <c>First</c> and may
    /// throw before its <c>await</c> of it, or only after; <c>B</c> awaits <c>A</c>'s task and catches its fault; <c>C</c> awaits
    /// <c>B</c>'s task. Leaving early, each completes — faulted, or normally once the fault is caught — while the child may run, and
    /// the continuation then writes; leaving late, the child completed first, and the requirements remove the pair. The analysis keeps
    /// it either way, so the late rows are wider. Why is not established: SPEC TD-065 makes a continuation composite unless its
    /// receiver is proven one task spawned once, but an async call left unawaited is modelled as a spawn (TD-060a) whose handle the
    /// same proof can follow, and a pair of these two writes cannot tell a failed receiver proof from a missing edge between the child
    /// and the antecedent's end; it needs the receiver proof traced for the three shapes.</summary>
    /// <param name="antecedent">The helper whose task the continuation follows: <c>A()</c>, <c>B()</c> or <c>C()</c>.</param>
    /// <param name="early">Whether <c>A</c> may throw before its <c>await</c> of the child, rather than after it.</param>
    [Theory]
    [InlineData("A()", true)]
    [InlineData("A()", false)]
    [InlineData("B()", true)]
    [InlineData("B()", false)]
    [InlineData("C()", true)]
    [InlineData("C()", false)]
    public void Continuation_of_a_helper_s_task_keeps_the_pair(string antecedent, bool early)
    {
        const string THROW = "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();";
        var inner = early ? $"{THROW} await t;" : $"await t; {THROW}";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            $"var outer = {antecedent}; outer.ContinueWith(_ => After());",
            $"private async Task A() {{ var t = Task.Run(() => First()); {inner} }} " +
            "private async Task B() { try { await A(); } catch (InvalidOperationException) { } } " +
            "private async Task C() { await B(); }"));

        Assert.True(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    early ? "the race is missed." : "the late row is ordered now, so it is exact.");
    }

    /// <summary>A continuation created with the worker's token: without <c>LazyCancellation</c>, canceling the token cancels the
    /// continuation's task at once, while its antecedent — a <c>Task.Run</c> writing in <c>First</c> — may still run, so a write after
    /// an <c>await</c> of that task whose cancellation is caught races with the antecedent; with <c>LazyCancellation</c>, or under
    /// <c>CancellationToken.None</c>, the canceled task completes only after the antecedent has. The same holds for an <c>await</c> with
    /// <c>ConfigureAwaitOptions.SuppressThrowing</c>, which goes on past the cancellation without throwing. The rows with the token and
    /// without <c>LazyCancellation</c> are known misses (#507), under both readers: lowering drops the continuation's token and
    /// options, and the path from the antecedent's end through the continuation to the <c>await</c> orders the two writes; their
    /// expected answer is a kept pair.</summary>
    /// <param name="token">Whether the continuation takes the worker's token, rather than <c>CancellationToken.None</c>.</param>
    /// <param name="lazy">Whether the continuation is created with <c>TaskContinuationOptions.LazyCancellation</c>.</param>
    /// <param name="suppressed">Whether the worker awaits the continuation's task with <c>SuppressThrowing</c>, rather than catching
    /// its cancellation.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Await_of_a_continuation_canceled_by_its_own_token_is_read_as_following_the_antecedent(bool token, bool lazy, bool suppressed)
    {
        var options = lazy ? "TaskContinuationOptions.LazyCancellation" : "TaskContinuationOptions.None";
        var wait = suppressed
            ? "await next.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);"
            : "try { await next; } catch (OperationCanceledException) { }";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            "var outer = Task.Run(() => First()); " +
            $"var next = outer.ContinueWith(_ => {{ }}, {(token ? "stoppingToken" : "CancellationToken.None")}, {options}, TaskScheduler.Default); " +
            $"{wait} After();"));
        var child = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".First()", StringComparison.Ordinal));
        var write = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.Equal(TimingBranch.HappensBeforePath, BlockingWaitAssumptionMatrixTests.Branch(run, child, write));
        Assert.False(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                     token && !lazy ? "#507: the known miss is reported now; expect the pair." : "the pair the requirements remove is back.");
    }

    // ---- the task kept in a field and joined later ----

    /// <summary>Where a hosted service joins the task it kept in a field: in the <c>StartAsync</c> that started it, in a helper that
    /// <c>StartAsync</c> awaits, or in <c>StopAsync</c>.</summary>
    public enum Holder
    {
        /// <summary>In the body of <c>StartAsync</c>, after the store.</summary>
        SameBody,

        /// <summary>In an async helper that <c>StartAsync</c> awaits after the store.</summary>
        Helper,

        /// <summary>In <c>StopAsync</c>, under the precondition that startup completed before stop: the host runs it on shutdown after
        /// <c>StartAsync</c> returned, as <c>RunAsync</c> does. The fixture does not enforce it — the two methods are two roots — and
        /// what a stop overlapping the start gives is not established here.</summary>
        StopAsync
    }

    /// <summary>How the holder waits for the kept task and writes in <c>After</c>.</summary>
    public enum FieldWait
    {
        /// <summary><c>await _task; After();</c></summary>
        Await,

        /// <summary><c>_task.Wait(); After();</c></summary>
        Wait,

        /// <summary><c>try { await _task; } catch (Exception) { } After();</c></summary>
        CaughtAwait,

        /// <summary><c>await _task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); After();</c></summary>
        SuppressedAwait,

        /// <summary><c>if (_task is { } pending) await pending; After();</c> — the field is always set here.</summary>
        NullCheckedAwait,

        /// <summary><c>try { await _task; } finally { After(); }</c></summary>
        WriteInFinally,

        /// <summary><c>_task.Wait(100); After();</c></summary>
        TimedWait,

        /// <summary><c>WaitAsync(TimeSpan)</c>, its <c>TimeoutException</c> caught, then <c>After()</c>.</summary>
        WaitAsyncTimeout,

        /// <summary><c>WaitAsync(cancellationToken)</c>, its <c>OperationCanceledException</c> caught, then <c>After()</c>.</summary>
        WaitAsyncToken,

        /// <summary><c>await Task.WhenAny(_task, Task.Delay(Timeout.Infinite, cancellationToken)); After();</c></summary>
        WhenAnyDelay
    }

    /// <summary>What the kept task runs.</summary>
    public enum Kept
    {
        /// <summary>The write in <c>First</c> itself.</summary>
        Plain,

        /// <summary>A child writing in <c>First</c>, started and then left by a throw that may come before the child's join.</summary>
        LeavesChild
    }

    /// <summary>One cell of the row of the task kept in a field.</summary>
    /// <param name="Holder">Where the kept task is joined.</param>
    /// <param name="Wait">How the holder waits for it.</param>
    /// <param name="Kept">What the kept task runs.</param>
    public sealed record FieldCell(Holder Holder, FieldWait Wait, Kept Kept)
    {
        /// <summary>The cell's name, its axes in order.</summary>
        public override string ToString() => $"{Holder}/{Wait}/{Kept}";
    }

    /// <summary>A cell of the field row whose answer differs from its expectation, with the issue or the reason.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Why">The issue that owns the wrong answer, or why the analysis cannot be exact there.</param>
    private sealed record ListedField(FieldCell Cell, string Why);

    /// <summary>The cells of the field row whose race the analysis misses: lowering keeps the receiver of
    /// <c>ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c> as the awaited task and drops the option, so the fault of a task
    /// that leaves its child running continues past the <c>await</c> as a normal return would.</summary>
    private static readonly ListedField[] FieldMisses =
    [
        new(new FieldCell(Holder.SameBody, FieldWait.SuppressedAwait, Kept.LeavesChild), SUPPRESSED),
        new(new FieldCell(Holder.Helper, FieldWait.SuppressedAwait, Kept.LeavesChild), SUPPRESSED)
    ];

    /// <summary>The cells of the field row whose answer is wider than their expectation, listed by hand: in <c>StopAsync</c> the waits
    /// that return only after the task — <c>await</c>, <c>Wait()</c> and the null-checked <c>await</c> over either task, and the waits
    /// whose fault the holder survives over a task that leaves nothing; in the same root the waits under a handler that read the field
    /// inside the <c>try</c>, and the null-checked <c>await</c>.</summary>
    private static readonly ListedField[] FieldWider =
    [
        .. from wait in new[] { FieldWait.Await, FieldWait.Wait, FieldWait.NullCheckedAwait }
           from kept in Enum.GetValues<Kept>()
           select new ListedField(new FieldCell(Holder.StopAsync, wait, kept), TWO_ROOTS),
        .. from wait in new[] { FieldWait.CaughtAwait, FieldWait.SuppressedAwait, FieldWait.WriteInFinally }
           select new ListedField(new FieldCell(Holder.StopAsync, wait, Kept.Plain), TWO_ROOTS),
        .. from holder in new[] { Holder.SameBody, Holder.Helper }
           from wait in new[] { FieldWait.CaughtAwait, FieldWait.WriteInFinally }
           select new ListedField(new FieldCell(holder, wait, Kept.Plain), FIELD_READ),
        .. from holder in new[] { Holder.SameBody, Holder.Helper }
           from kept in Enum.GetValues<Kept>()
           select new ListedField(new FieldCell(holder, FieldWait.NullCheckedAwait, kept), NULL_CHECK)
    ];

    /// <summary>Why a wait under a test of the handle keeps the pair.</summary>
    private const string NULL_CHECK = "the test of a handle just stored gives the write a path around the wait, which only the " +
                                      "handle's nullness rules out; the code that keeps that path was not read (candidate limitation).";

    /// <summary>Why a fault suppressed at an <c>await</c> is missed.</summary>
    private const string SUPPRESSED = "#500: IrLowering lowers ConfigureAwait(…) as an IrTaskKind.Same task of its receiver without " +
                                      "reading the ConfigureAwaitOptions, so the fault of the kept task continues to the write as a " +
                                      "normal return that joined the child.";

    /// <summary>Why a wait in <c>StopAsync</c> orders nothing <c>StartAsync</c> started.</summary>
    private const string TWO_ROOTS = "HappensBefore.HandleExecution looks for the task a handle names only in the waiting execution's " +
                                     "own instance tree (HandlesInTree, ADR 0008), and StartAsync and StopAsync are two roots; ordering " +
                                     "them needs the host's order of the two calls as an assumption (owner's decision).";

    /// <summary>Why a handler around a wait on a handle read from a field keeps the pair.</summary>
    private const string FIELD_READ = "HappensBefore.WaitParts counts a field read computing a handle among the operations that may " +
                                      "throw before the wait, so the handler is reached around it; a copy read before the try is " +
                                      "ordered (see The_field_read_inside_the_try_decides_the_handler_pair).";

    /// <summary>Every cell of the field row.</summary>
    public static IEnumerable<FieldCell> AllFieldCells() =>
        from holder in Enum.GetValues<Holder>()
        from wait in Enum.GetValues<FieldWait>()
        from kept in Enum.GetValues<Kept>()
        select new FieldCell(holder, wait, kept);

    /// <summary>The cells of the field row as theory data.</summary>
    public static TheoryData<Holder, FieldWait, Kept> FieldCells()
    {
        var cells = new TheoryData<Holder, FieldWait, Kept>();
        foreach (var cell in AllFieldCells())
            cells.Add(cell.Holder, cell.Wait, cell.Kept);
        return cells;
    }

    /// <summary>Every listed cell of the field row is a cell of the row, listed once, with its reason: a miss expects the pair, a
    /// wider cell expects none.</summary>
    [Fact]
    public void Every_listed_field_cell_names_a_cell_its_list_fits()
    {
        var cells = AllFieldCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(60, cells.Count);
        Assert.Equal(2, FieldMisses.Length);
        Assert.Equal(17, FieldWider.Length);
        Assert.All(FieldMisses, miss => Assert.Equal(SUPPRESSED, miss.Why));
        Assert.Equal(4, FieldWider.Count(wider => wider.Why == FIELD_READ));
        Assert.Equal(4, FieldWider.Count(wider => wider.Why == NULL_CHECK));
        Assert.Equal(9, FieldWider.Count(wider => wider.Why == TWO_ROOTS));
        Assert.All(FieldMisses, miss => Assert.True(cells.Contains(miss.Cell.ToString()) && FieldExpected(miss.Cell) && miss.Why.Length > 0));
        Assert.All(FieldWider, wider => Assert.True(cells.Contains(wider.Cell.ToString()) && !FieldExpected(wider.Cell) && wider.Why.Length > 0));
        Assert.Equal(FieldMisses.Length + FieldWider.Length,
                     FieldMisses.Concat(FieldWider).Select(listed => listed.Cell.ToString()).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A cell of the field row outside the lists answers exactly; one in <see cref="FieldMisses"/> misses its pair, and one
    /// in <see cref="FieldWider"/> keeps it.</summary>
    /// <param name="holder">Where the kept task is joined.</param>
    /// <param name="wait">How the holder waits for it.</param>
    /// <param name="kept">What the kept task runs.</param>
    [Theory]
    [MemberData(nameof(FieldCells))]
    public void Field_cells_are_exact_outside_the_lists(Holder holder, FieldWait wait, Kept kept)
    {
        var cell = new FieldCell(holder, wait, kept);
        var pair = BlockingWaitAssumptionMatrixTests.Pairs(Analyze(HostedService(cell)), "First", "After");

        if (FieldMisses.Any(miss => miss.Cell == cell))
            Assert.False(pair, $"{cell}: listed as a known miss, but the pair is reported now; take it off the list.");
        else if (FieldWider.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.True(FieldExpected(cell) == pair, $"{cell}: expected the pair {(FieldExpected(cell) ? "kept" : "removed")}.");
    }

    /// <summary>Whether the requirements keep the pair of the kept task's write and the holder's write. A wait that a timeout or a
    /// token ends — <c>Wait(int)</c> returning <c>false</c>, <c>WaitAsync</c> throwing <c>TimeoutException</c> or
    /// <c>OperationCanceledException</c> while the task runs, <c>WhenAny</c> completing with the delay — leaves the task running
    /// whatever it runs. A wait whose fault the holder survives — caught, suppressed, or followed by a <c>finally</c> — leaves the
    /// child running where the kept task may fault before its join of the child. Every other wait returns only after the task
    /// completed and lets its fault leave the holder before the write. <c>StopAsync</c> reads as the same body under its
    /// precondition, startup completed before stop (see <see cref="Holder.StopAsync"/>).</summary>
    /// <param name="cell">The cell.</param>
    private static bool FieldExpected(FieldCell cell) =>
        cell.Wait is FieldWait.TimedWait or FieldWait.WaitAsyncTimeout or FieldWait.WaitAsyncToken or FieldWait.WhenAnyDelay ||
        cell.Kept == Kept.LeavesChild && cell.Wait is FieldWait.CaughtAwait or FieldWait.SuppressedAwait or FieldWait.WriteInFinally;

    /// <summary>Which operation decides the handler's pair over a field: the task, stored in <c>_t</c>, writes in <c>First</c> and
    /// leaves nothing, so every exit of the <c>await</c> follows it and none of the three shapes races. Read from the field inside
    /// the <c>try</c> — directly or into a local there — the handle's read stands in the handler's scope as an operation that may
    /// throw before the wait (<c>WaitParts</c>, as <c>BlockingWaitAssumptionMatrixTests.What_computes_the_handle_inside_the_try_decides_the_handler_pair</c>
    /// pins for <c>Wait()</c>), and the pair is kept; copied before the <c>try</c>, it is not. <paramref name="kept"/> is today's
    /// answer.</summary>
    /// <param name="shape">Where the field is read: <c>direct</c>, <c>copy-inside-try</c> or <c>copy-before-try</c>.</param>
    /// <param name="kept">Whether the analysis keeps the pair today; the runtime removes it in every row.</param>
    [Theory]
    [InlineData("direct", true)]
    [InlineData("copy-inside-try", true)]
    [InlineData("copy-before-try", false)]
    public void The_field_read_inside_the_try_decides_the_handler_pair(string shape, bool kept)
    {
        var body = shape switch
        {
            "direct" => "_t = Task.Run(() => First()); try { await _t; } catch (Exception) { } After();",
            "copy-inside-try" => "_t = Task.Run(() => First()); try { var t = _t; await t; } catch (Exception) { } After();",
            _ => "_t = Task.Run(() => First()); var t = _t; try { await t; } catch (Exception) { } After();"
        };

        // The field has no initializer: a second value stored in it, such as Task.CompletedTask, would leave the handle unproven.
        Assert.Equal(kept, BlockingWaitAssumptionMatrixTests.Pairs(Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, "private Task? _t;")),
                                                                    "First", "After"));
    }

    /// <summary>A hosted service whose <c>StartAsync</c> keeps a task in a field and whose holder joins it as the cell says.</summary>
    /// <param name="cell">The cell.</param>
    private static string HostedService(FieldCell cell)
    {
        var task = cell.Kept == Kept.Plain
            ? "Task.Run(() => First())"
            : "Task.Run(() => { var inner = Task.Run(() => First()); if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); inner.Wait(); })";
        var join = cell.Wait switch
        {
            FieldWait.Await => "await _task!; After();",
            FieldWait.Wait => "_task!.Wait(); After();",
            FieldWait.CaughtAwait => "try { await _task!; } catch (Exception) { } After();",
            FieldWait.SuppressedAwait => "await _task!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); After();",
            FieldWait.NullCheckedAwait => "if (_task is { } pending) await pending; After();",
            FieldWait.WriteInFinally => "try { await _task!; } finally { After(); }",
            FieldWait.TimedWait => "_task!.Wait(100); After();",
            FieldWait.WaitAsyncTimeout => "try { await _task!.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { } After();",
            FieldWait.WaitAsyncToken => "try { await _task!.WaitAsync(cancellationToken); } catch (OperationCanceledException) { } After();",
            _ => "await Task.WhenAny(_task!, Task.Delay(Timeout.Infinite, cancellationToken)); After();"
        };
        var (start, stop, helper) = cell.Holder switch
        {
            Holder.SameBody => ($"_task = {task}; {join}", string.Empty, string.Empty),
            Holder.Helper => ($"_task = {task}; await JoinAsync(cancellationToken);", string.Empty,
                              $"private async Task JoinAsync(CancellationToken cancellationToken) {{ {join} }}"),
            _ => ($"_task = {task};", join, string.Empty)
        };
        return Startup("services.AddHostedService<Kept>();") + $$"""

            public sealed class Kept : IHostedService
            {
                public static int Value;
                private Task? _task;
                private void First() { Value = 1; }
                private void After() { Value = 3; }
                public async Task StartAsync(CancellationToken cancellationToken)
                {
                    {{start}}
                    await Task.CompletedTask;
                }
                public async Task StopAsync(CancellationToken cancellationToken)
                {
                    {{stop}}
                    await Task.CompletedTask;
                }
                {{helper}}
            }
            """;
    }

    // ---- a group of tasks: WhenAll and WaitAll ----

    /// <summary>How the worker waits for the group of task <c>a</c>, whose work writes in <c>First</c>, and its sibling <c>b</c>.</summary>
    public enum Group
    {
        /// <summary><c>await Task.WhenAll(a, b);</c></summary>
        WhenAllAwait,

        /// <summary><c>Task.WhenAll(a, b).Wait();</c></summary>
        WhenAllWait,

        /// <summary><c>Task.WaitAll(a, b);</c></summary>
        WaitAll,

        /// <summary><c>await Task.WhenAll(all);</c> over a list built from both tasks.</summary>
        WhenAllList,

        /// <summary><c>await Task.WhenAll(a, b).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);</c></summary>
        WhenAllSuppressed,

        /// <summary><c>await Task.WhenAll(a, b).WaitAsync(stoppingToken);</c></summary>
        WhenAllToken,

        /// <summary><c>Task.WaitAll(new[] { a, b }, stoppingToken);</c></summary>
        WaitAllToken,

        /// <summary><c>Task.WaitAll(new[] { a, b }, 100);</c></summary>
        WaitAllTimeout,

        /// <summary><c>await b; await a;</c> — not a group: the contrast that a fault of <c>b</c> skips the wait for <c>a</c>.</summary>
        Sequential
    }

    /// <summary>What the sibling <c>b</c> does.</summary>
    public enum Sibling
    {
        /// <summary>Returns.</summary>
        Quiet,

        /// <summary>Calls a program method that may throw.</summary>
        Faults
    }

    /// <summary>Whether the worker's write follows the group's wait under a <c>catch</c> of every exception or with no handler.</summary>
    public enum GroupReader
    {
        /// <summary><c>try { wait } catch (Exception) { } After();</c></summary>
        AfterCatch,

        /// <summary><c>wait After();</c></summary>
        AfterUncaught
    }

    /// <summary>One cell of the group row.</summary>
    /// <param name="Group">How the group is waited for.</param>
    /// <param name="Sibling">What the sibling does.</param>
    /// <param name="Kept">What task <c>a</c> runs: the write itself, or a child it may leave running.</param>
    /// <param name="Reader">Where the worker's write stands.</param>
    public sealed record GroupCell(Group Group, Sibling Sibling, Kept Kept, GroupReader Reader)
    {
        /// <summary>The cell's name, its axes in order.</summary>
        public override string ToString() => $"{Group}/{Sibling}/{Kept}/{Reader}";
    }

    /// <summary>A cell of the group row whose answer differs from its expectation, with the issue or the reason.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Why">The issue that owns the wrong answer, or why the analysis cannot be exact there.</param>
    private sealed record ListedGroup(GroupCell Cell, string Why);

    /// <summary>The cells of the group row whose race the analysis misses.</summary>
    private static readonly ListedGroup[] GroupMisses =
    [
        new(new GroupCell(Group.Sequential, Sibling.Faults, Kept.Plain, GroupReader.AfterCatch),
            "#364: the fault of the earlier await skips the wait for a, yet the pair is removed; the code that orders it was not " +
            "read (the issue's account stands)."),
        .. from sibling in Enum.GetValues<Sibling>()
           select new ListedGroup(new GroupCell(Group.WhenAllSuppressed, sibling, Kept.LeavesChild, GroupReader.AfterUncaught), SUPPRESSED)
    ];

    /// <summary>The cells of the group row whose answer is wider than their expectation, listed by hand.</summary>
    private static readonly ListedGroup[] GroupWider =
    [
        .. from @group in new[] { Group.WhenAllAwait, Group.WhenAllSuppressed }
           from sibling in Enum.GetValues<Sibling>()
           select new ListedGroup(new GroupCell(@group, sibling, Kept.Plain, GroupReader.AfterCatch),
                                  "#366 under decision D-0018 of 5d-H2b: HappensBefore.WaitParts keeps, for an await, the " +
                                  "exceptional paths of group construction, so the Task.WhenAll call inside the try may throw " +
                                  "before the wait and the catch is reached around it (see The_group_built_inside_the_try_decides_the_handler_pair)." +
                                  (@group == Group.WhenAllSuppressed
                                      ? " WaitParts counts ConfigureAwait(ConfigureAwaitOptions) among those operations too; which of " +
                                        "the two decides is not told apart."
                                      : string.Empty)),
        .. from @group in new[] { Group.WhenAllToken, Group.WaitAllToken }
           from sibling in Enum.GetValues<Sibling>()
           from kept in Enum.GetValues<Kept>()
           select new ListedGroup(new GroupCell(@group, sibling, kept, GroupReader.AfterUncaught), TOKEN_WAIT),
        .. from sibling in Enum.GetValues<Sibling>()
           from kept in Enum.GetValues<Kept>()
           from reader in Enum.GetValues<GroupReader>()
           where kept == Kept.Plain || reader == GroupReader.AfterUncaught
           select new ListedGroup(new GroupCell(Group.WhenAllList, sibling, kept, reader),
                                  reader == GroupReader.AfterCatch ? $"{UNKNOWN_MEMBERS} {LIST_BUILT_IN_TRY}" : UNKNOWN_MEMBERS)
    ];

    /// <summary>Why a wait under a token keeps the pair with a write its normal return reaches.</summary>
    private const string TOKEN_WAIT = "IrLowering makes no join of a wait under a token: Task.WaitAll(tasks, token) is no " +
                                      "IrJoinKind.WaitAll, which it makes of WaitAll(tasks) alone, and WaitAsync(token) gives an " +
                                      "IrTaskKind.CompletionOf task, which carries no join, so the normal return, which comes only " +
                                      "after every task completed, orders nothing (by rule: ADR 0030 keeps token waits conservative, " +
                                      "SPEC TD-067; a precision change is the owner's).";

    /// <summary>Why a group over a list kept in a local orders nothing.</summary>
    private const string UNKNOWN_MEMBERS = "IrLowering.ListedElements names no members for a List<Task> kept in a local, and a group " +
                                           "with unknown members orders nothing on its normal return (see Listings_a_group_waits_for).";

    /// <summary>Why a group over a list kept in a local, built inside a <c>try</c>, has a second reason under the handler.</summary>
    private const string LIST_BUILT_IN_TRY = "Under the catch the Task.WhenAll call inside the try keeps the pair too (#366); which of " +
                                             "the two decides is not told apart.";

    /// <summary>Every cell of the group row.</summary>
    public static IEnumerable<GroupCell> AllGroupCells() =>
        from @group in Enum.GetValues<Group>()
        from sibling in Enum.GetValues<Sibling>()
        from kept in Enum.GetValues<Kept>()
        from reader in Enum.GetValues<GroupReader>()
        select new GroupCell(@group, sibling, kept, reader);

    /// <summary>The cells of the group row as theory data.</summary>
    public static TheoryData<Group, Sibling, Kept, GroupReader> GroupCells()
    {
        var cells = new TheoryData<Group, Sibling, Kept, GroupReader>();
        foreach (var cell in AllGroupCells())
            cells.Add(cell.Group, cell.Sibling, cell.Kept, cell.Reader);
        return cells;
    }

    /// <summary>Every listed cell of the group row is a cell of the row, listed once, with its reason: a miss expects the pair, a
    /// wider cell expects none.</summary>
    [Fact]
    public void Every_listed_group_cell_names_a_cell_its_list_fits()
    {
        var cells = AllGroupCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(72, cells.Count);
        Assert.Equal(3, GroupMisses.Length);
        Assert.Equal(18, GroupWider.Length);
        Assert.Equal(1, GroupMisses.Count(miss => miss.Why.StartsWith("#364:", StringComparison.Ordinal)));
        Assert.Equal(2, GroupMisses.Count(miss => miss.Why == SUPPRESSED));
        Assert.Equal(4, GroupWider.Count(wider => wider.Why.StartsWith("#366", StringComparison.Ordinal)));
        Assert.Equal(8, GroupWider.Count(wider => wider.Why == TOKEN_WAIT));
        Assert.Equal(6, GroupWider.Count(wider => wider.Why.StartsWith(UNKNOWN_MEMBERS, StringComparison.Ordinal)));
        Assert.Equal(2, GroupWider.Count(wider => wider.Why.EndsWith(LIST_BUILT_IN_TRY, StringComparison.Ordinal)));
        Assert.All(GroupMisses, miss => Assert.True(cells.Contains(miss.Cell.ToString()) && GroupExpected(miss.Cell) && miss.Why.Length > 0));
        Assert.All(GroupWider, wider => Assert.True(cells.Contains(wider.Cell.ToString()) && !GroupExpected(wider.Cell) && wider.Why.Length > 0));
        Assert.Equal(GroupMisses.Length + GroupWider.Length,
                     GroupMisses.Concat(GroupWider).Select(listed => listed.Cell.ToString()).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A cell of the group row outside the lists answers exactly; one in <see cref="GroupMisses"/> misses its pair, and one
    /// in <see cref="GroupWider"/> keeps it.</summary>
    /// <param name="group">How the group is waited for.</param>
    /// <param name="sibling">What the sibling does.</param>
    /// <param name="kept">What task <c>a</c> runs.</param>
    /// <param name="reader">Where the worker's write stands.</param>
    [Theory]
    [MemberData(nameof(GroupCells))]
    public void Group_cells_are_exact_outside_the_lists(Group group, Sibling sibling, Kept kept, GroupReader reader)
    {
        var cell = new GroupCell(group, sibling, kept, reader);
        var pair = BlockingWaitAssumptionMatrixTests.Pairs(Analyze(BlockingWaitAssumptionMatrixTests.Worker(GroupBody(cell),
                                                                                                              BlockingWaitAssumptionMatrixTests.FAIL)),
                                                           "First", "After");

        if (GroupMisses.Any(miss => miss.Cell == cell))
            Assert.False(pair, $"{cell}: listed as a known miss, but the pair is reported now; take it off the list.");
        else if (GroupWider.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.True(GroupExpected(cell) == pair, $"{cell}: expected the pair {(GroupExpected(cell) ? "kept" : "removed")}.");
    }

    /// <summary>Whether the requirements keep the pair of <c>a</c>'s write and the worker's. <c>Task.WhenAll</c> completes only when
    /// every task it lists has completed, faulted ones included, and <c>Task.WaitAll</c> throws its <c>AggregateException</c> only then;
    /// so a group's wait leaves running only the child <c>a</c> may leave, and only where the worker survives the fault — under a
    /// <c>catch</c>, or with the fault suppressed. A token ends the wait early by an exception alone, a timeout by a normal return of
    /// <c>false</c>. Two awaits in a row are no group: the fault of <c>b</c> skips the wait for <c>a</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static bool GroupExpected(GroupCell cell)
    {
        var caught = cell.Reader == GroupReader.AfterCatch;
        var leaves = cell.Kept == Kept.LeavesChild;
        return cell.Group switch
        {
            Group.WaitAllTimeout => true,
            Group.WhenAllToken or Group.WaitAllToken => caught,
            Group.WhenAllSuppressed => leaves,
            Group.Sequential => caught && (leaves || cell.Sibling == Sibling.Faults),
            _ => caught && leaves
        };
    }

    /// <summary>The worker's body of a cell of the group row.</summary>
    /// <param name="cell">The cell.</param>
    private static string GroupBody(GroupCell cell)
    {
        var a = cell.Kept == Kept.Plain
            ? "Task.Run(() => First())"
            : "Task.Run(() => { var inner = Task.Run(() => First()); if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); inner.Wait(); })";
        var b = cell.Sibling == Sibling.Faults ? "Task.Run(() => Fail())" : "Task.Run(() => { })";
        var wait = cell.Group switch
        {
            Group.WhenAllAwait => "await Task.WhenAll(a, b);",
            Group.WhenAllWait => "Task.WhenAll(a, b).Wait();",
            Group.WaitAll => "Task.WaitAll(a, b);",
            Group.WhenAllList => "var all = new System.Collections.Generic.List<Task> { a, b }; await Task.WhenAll(all);",
            Group.WhenAllSuppressed => "await Task.WhenAll(a, b).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);",
            Group.WhenAllToken => "await Task.WhenAll(a, b).WaitAsync(stoppingToken);",
            Group.WaitAllToken => "Task.WaitAll(new[] { a, b }, stoppingToken);",
            Group.WaitAllTimeout => "Task.WaitAll(new[] { a, b }, 100);",
            _ => "await b; await a;"
        };
        var tasks = $"var a = {a}; var b = {b};";
        return cell.Reader == GroupReader.AfterCatch ? $"{tasks} try {{ {wait} }} catch (Exception) {{ }} After();" : $"{tasks} {wait} After();";
    }

    /// <summary>How the listing a group waits for is built (the twist of a conditional or lazy listing), each awaited by
    /// <c>await Task.WhenAll(…); After();</c> with no handler. A task that writes in <c>First</c> races with <c>After</c> only where it
    /// was started and the group does not list it: <c>WhenAll</c> enumerates its argument once, when called, and waits for every task
    /// it found. A deferred <c>Select</c> enumerated again after the group starts tasks the group does not wait for; that form is left
    /// out, since both enumerations start tasks through one selector writing in one place, and a pair with a task of the first one
    /// would answer as well (not established: it needs a write site of its own for the second enumeration). <paramref name="kept"/>
    /// is today's answer; where it differs from <paramref name="expected"/>, the reason stands in the results of #505.</summary>
    /// <param name="listing">The name of the listing's form.</param>
    /// <param name="expected">Whether the requirements keep the pair.</param>
    /// <param name="kept">Whether the analysis keeps the pair today.</param>
    [Theory]
    [InlineData("array", false, false)]
    [InlineData("array-local", false, true)]
    [InlineData("collection-expression", false, false)]
    [InlineData("collection-expression-local", false, true)]
    [InlineData("list-initializer", false, true)]
    [InlineData("list-added", false, true)]
    [InlineData("list-added-conditionally", true, true)]
    [InlineData("started-and-added-conditionally", false, true)]
    [InlineData("select-materialized", false, true)]
    [InlineData("select-deferred", false, true)]
    [InlineData("where-not-completed", false, true)]
    public void Listings_a_group_waits_for(string listing, bool expected, bool kept)
    {
        const string A = "var a = Task.Run(() => First()); var b = Task.Run(() => { });";
        const string SELECT = "System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 2), _ => Task.Run(() => First()))";
        var body = listing switch
        {
            "array" => $"{A} await Task.WhenAll(new[] {{ a, b }});",
            "array-local" => $"{A} var all = new[] {{ a, b }}; await Task.WhenAll(all);",
            "collection-expression" => $"{A} await Task.WhenAll([a, b]);",
            "collection-expression-local" => $"{A} Task[] all = [a, b]; await Task.WhenAll(all);",
            "list-initializer" => $"{A} var all = new System.Collections.Generic.List<Task> {{ a, b }}; await Task.WhenAll(all);",
            "list-added" => $"{A} var all = new System.Collections.Generic.List<Task>(); all.Add(a); all.Add(b); await Task.WhenAll(all);",
            "list-added-conditionally" => $"{A} var all = new System.Collections.Generic.List<Task> {{ b }}; if (Environment.TickCount % 2 == 0) all.Add(a); await Task.WhenAll(all);",
            "started-and-added-conditionally" => "var all = new System.Collections.Generic.List<Task> { Task.Run(() => { }) }; " +
                                                 "if (Environment.TickCount % 2 == 0) all.Add(Task.Run(() => First())); await Task.WhenAll(all);",
            "select-materialized" => $"var all = System.Linq.Enumerable.ToList({SELECT}); await Task.WhenAll(all);",
            "select-deferred" => $"await Task.WhenAll({SELECT});",
            _ => $"{A} var all = new[] {{ a, b }}; await Task.WhenAll(System.Linq.Enumerable.Where(all, t => !t.IsCompleted));"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker($"{body} After();"));

        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{listing}: today's answer moved; expected {(expected ? "a pair" : "no pair")} by the runtime.");
    }

    /// <summary>The issues filed on groups, each on its own input. #362: a helper waits <c>Task.WhenAll(a)</c> then
    /// <c>Task.WhenAll(b)</c>; only <c>b</c> may fault, so its wait's exception comes after <c>a</c>, writing in <c>First</c>, has
    /// completed, and the write after the caller's <c>catch</c> does not race with it. #360: each pass of a loop waits its own task through
    /// <c>Task.WhenAll(t).Wait()</c> before the next pass starts another, so the task's write does not race with the next pass's. In
    /// each the analysis keeps the pair the runtime removes. #366 stands on
    /// <see cref="The_group_built_inside_the_try_decides_the_handler_pair"/> alone: the issue's own input starts the group's second
    /// member between the child's start and the join, a launch the analysis reads as possibly throwing too
    /// (<c>AwaitLeavingWorkMatrixTests</c>, R3), so a pair there would not tell the two apart.</summary>
    /// <param name="issue">The issue whose input the case writes.</param>
    /// <param name="body">The worker's body.</param>
    /// <param name="second">The method whose write is paired with <c>First</c>'s.</param>
    [Theory]
    [InlineData("#362", "var a = Task.Run(() => First()); var b = Task.Run(() => Fail()); try { Both(a, b); } catch (Exception) { } After();", "After")]
    [InlineData("#360", "for (var pass = 0; pass < 2; pass++) { var t = Task.Run(() => First()); Task.WhenAll(t).Wait(); }", "First")]
    public void Group_issues_keep_pairs_the_runtime_orders(string issue, string body, string second)
    {
        const string BOTH = "private static void Both(Task a, Task b) { Task.WhenAll(a).Wait(); Task.WhenAll(b).Wait(); }";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, BlockingWaitAssumptionMatrixTests.FAIL + BOTH));

        Assert.True(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", second), $"{issue}: the wider answer is gone; the issue may be fixed.");
    }

    /// <summary>#366 isolated with known inline members: <c>a</c> writes in <c>First</c>, <c>b</c> returns, and neither can leave work
    /// running, so every exit of the group's <c>await</c> follows <c>a</c> and no shape races. Both members start before the
    /// <c>try</c> in both rows, which differ only in where the group is built. Built inside the <c>try</c>, the <c>Task.WhenAll</c> call
    /// stands in the handler's scope as an operation that may throw before the wait (D-0018), and the pair is kept; built before the
    /// <c>try</c>, the group's <c>await</c> alone stands there, and its exceptional exit is ordered. <c>Task.WhenAll</c> over tasks that
    /// are not null throws nothing in its contract, so the runtime removes the pair in both rows; decision D-0018 of 5d-H2b and the
    /// cells <c>EarlierWhenAllWithTheChild</c> of <c>AwaitLeavingWorkMatrixTests</c> keep that possible exception on purpose, so #366
    /// asks to change a requirement, not to fix a defect.</summary>
    /// <param name="inside">Whether the group is built inside the <c>try</c>, rather than before it.</param>
    /// <param name="kept">Whether the analysis keeps the pair today; the runtime removes it in both rows.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_group_built_inside_the_try_decides_the_handler_pair(bool inside, bool kept)
    {
        const string TASKS = "var a = Task.Run(() => First()); var b = Task.Run(() => { }); ";
        var body = inside
            ? TASKS + "try { await Task.WhenAll(a, b); } catch (Exception) { } After();"
            : TASKS + "var all = Task.WhenAll(a, b); try { await all; } catch (Exception) { } After();";

        Assert.Equal(kept, BlockingWaitAssumptionMatrixTests.Pairs(Analyze(BlockingWaitAssumptionMatrixTests.Worker(body)), "First", "After"));
    }

    // ---- Parallel loops ----

    /// <summary>The <c>Parallel</c> call whose return the worker relies on.</summary>
    public enum Loop
    {
        /// <summary><c>Parallel.For(0, 2, i => body)</c></summary>
        For,

        /// <summary><c>Parallel.ForEach(new[] { 1, 2 }, i => body)</c></summary>
        ForEach,

        /// <summary><c>Parallel.Invoke(() => body, () => { })</c></summary>
        Invoke,

        /// <summary><c>Parallel.For</c> with <c>ParallelOptions { CancellationToken = stoppingToken }</c>.</summary>
        ForToken,

        /// <summary><c>await Parallel.ForEachAsync(new[] { 1, 2 }, async (i, ct) => body)</c></summary>
        ForEachAsync
    }

    /// <summary>What one run of the loop's body does with the write in <c>First</c>.</summary>
    public enum Body
    {
        /// <summary>Writes itself.</summary>
        Plain,

        /// <summary>Starts a task that writes and waits for it: <c>Wait()</c>, or <c>await</c> in <c>ForEachAsync</c>.</summary>
        StartsAndWaits,

        /// <summary>Starts a task that writes and does not wait for it (the twist of a body starting unawaited work).</summary>
        StartsUnawaited,

        /// <summary>Starts a task that writes and may throw before its wait.</summary>
        LeavesChild
    }

    /// <summary>One cell of the <c>Parallel</c> row.</summary>
    /// <param name="Loop">The <c>Parallel</c> call.</param>
    /// <param name="Body">What the body does with the write.</param>
    /// <param name="Reader">Whether the worker's write follows the call under a <c>catch</c> or with no handler.</param>
    public sealed record LoopCell(Loop Loop, Body Body, GroupReader Reader)
    {
        /// <summary>The cell's name, its axes in order.</summary>
        public override string ToString() => $"{Loop}/{Body}/{Reader}";
    }

    /// <summary>A cell of the <c>Parallel</c> row whose answer differs from its expectation, with the issue or the reason.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Why">The issue that owns the wrong answer, or why the analysis cannot be exact there.</param>
    private sealed record ListedLoop(LoopCell Cell, string Why);

    /// <summary>The cells of the <c>Parallel</c> row whose race the analysis misses: none; #358's form, a body's own later write, is
    /// <see cref="Unawaited_work_of_a_parallel_body_against_the_body_s_later_write"/>.</summary>
    private static readonly ListedLoop[] LoopMisses = [];

    /// <summary>The cells of the <c>Parallel</c> row whose answer is wider than their expectation, listed by hand.</summary>
    private static readonly ListedLoop[] LoopWider =
    [
        .. new (Body Body, GroupReader Reader)[]
           {
               (Body.Plain, GroupReader.AfterCatch), (Body.Plain, GroupReader.AfterUncaught), (Body.StartsAndWaits, GroupReader.AfterCatch),
               (Body.StartsAndWaits, GroupReader.AfterUncaught), (Body.LeavesChild, GroupReader.AfterUncaught)
           }
           .Select(form => new ListedLoop(new LoopCell(Loop.Invoke, form.Body, form.Reader),
                                          "IrLowering has no case for Parallel.Invoke among the Parallel forms it lowers (For, " +
                                          "ForEach and ForEachAsync), so its return joins nothing; how its delegates come to run " +
                                          "unordered with the worker was not read (candidate limitation, owner's decision).")),
        new(new LoopCell(Loop.ForEachAsync, Body.Plain, GroupReader.AfterCatch), LOOP_CALL_IN_TRY),
        new(new LoopCell(Loop.ForEachAsync, Body.StartsAndWaits, GroupReader.AfterCatch), $"{LOOP_CALL_IN_TRY} {NESTED_CHILD}"),
        new(new LoopCell(Loop.ForEachAsync, Body.StartsAndWaits, GroupReader.AfterUncaught), NESTED_CHILD),
        new(new LoopCell(Loop.ForEachAsync, Body.LeavesChild, GroupReader.AfterUncaught), NESTED_CHILD)
    ];

    /// <summary>Why a handler around <c>await Parallel.ForEachAsync(…)</c> keeps the pair.</summary>
    private const string LOOP_CALL_IN_TRY = "HappensBefore.WaitParts counts the ForEachAsync call computing the awaited task among " +
                                            "the operations that may throw before the wait, so the handler is reached around it; the " +
                                            "loop's task saved before the try is ordered (see ForEachAsync_bodies_and_the_bypass_of_its_join).";

    /// <summary>Why a child a <c>ForEachAsync</c> body waited for keeps its pair with the write after the loop.</summary>
    private const string NESTED_CHILD = "a child task a body waits for keeps its pair with the write after the awaited loop, while the " +
                                        "body's own write, after a suspension too, is ordered; the code that decides it was not " +
                                        "established.";

    /// <summary>Every cell of the <c>Parallel</c> row.</summary>
    public static IEnumerable<LoopCell> AllLoopCells() =>
        from loop in Enum.GetValues<Loop>()
        from body in Enum.GetValues<Body>()
        from reader in Enum.GetValues<GroupReader>()
        select new LoopCell(loop, body, reader);

    /// <summary>The cells of the <c>Parallel</c> row as theory data.</summary>
    public static TheoryData<Loop, Body, GroupReader> LoopCells()
    {
        var cells = new TheoryData<Loop, Body, GroupReader>();
        foreach (var cell in AllLoopCells())
            cells.Add(cell.Loop, cell.Body, cell.Reader);
        return cells;
    }

    /// <summary>Every listed cell of the <c>Parallel</c> row is a cell of the row, listed once, with its reason: a miss expects the
    /// pair, a wider cell expects none.</summary>
    [Fact]
    public void Every_listed_loop_cell_names_a_cell_its_list_fits()
    {
        var cells = AllLoopCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(40, cells.Count);
        Assert.Empty(LoopMisses);
        Assert.Equal(9, LoopWider.Length);
        Assert.Equal(2, LoopWider.Count(wider => wider.Why.Contains(LOOP_CALL_IN_TRY, StringComparison.Ordinal)));
        Assert.Equal(3, LoopWider.Count(wider => wider.Why.Contains(NESTED_CHILD, StringComparison.Ordinal)));
        Assert.Equal(5, LoopWider.Count(wider => wider.Cell.Loop == Loop.Invoke));
        Assert.All(LoopMisses, miss => Assert.True(cells.Contains(miss.Cell.ToString()) && LoopExpected(miss.Cell) && miss.Why.Length > 0));
        Assert.All(LoopWider, wider => Assert.True(cells.Contains(wider.Cell.ToString()) && !LoopExpected(wider.Cell) && wider.Why.Length > 0));
        Assert.Equal(LoopMisses.Length + LoopWider.Length,
                     LoopMisses.Concat(LoopWider).Select(listed => listed.Cell.ToString()).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A cell of the <c>Parallel</c> row outside the lists answers exactly; one in <see cref="LoopMisses"/> misses its pair,
    /// and one in <see cref="LoopWider"/> keeps it.</summary>
    /// <param name="loop">The <c>Parallel</c> call.</param>
    /// <param name="body">What the body does with the write.</param>
    /// <param name="reader">Whether the worker's write follows the call under a <c>catch</c> or with no handler.</param>
    [Theory]
    [MemberData(nameof(LoopCells))]
    public void Loop_cells_are_exact_outside_the_lists(Loop loop, Body body, GroupReader reader)
    {
        var cell = new LoopCell(loop, body, reader);
        var pair = BlockingWaitAssumptionMatrixTests.Pairs(Analyze(BlockingWaitAssumptionMatrixTests.Worker(LoopBody(cell))), "First", "After");

        if (LoopMisses.Any(miss => miss.Cell == cell))
            Assert.False(pair, $"{cell}: listed as a known miss, but the pair is reported now; take it off the list.");
        else if (LoopWider.Any(wider => wider.Cell == cell))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.True(LoopExpected(cell) == pair, $"{cell}: expected the pair {(LoopExpected(cell) ? "kept" : "removed")}.");
    }

    /// <summary>Whether the requirements keep the pair of the body's write and the worker's. Every <c>Parallel</c> call ends — by
    /// returning, by an <c>AggregateException</c> of its bodies' faults, or by the <c>OperationCanceledException</c> of its options'
    /// token — only after every body it started has finished: a fault ends the loop «as soon as all currently running iterations
    /// finish», <c>Parallel.For</c> throws its cancellation after its workers return, and the task of <c>ForEachAsync</c> completes when
    /// its last worker signals that it stopped iterating. So only work a body leaves running races with the write after the call: a task
    /// it does not wait for, under either reader, and a child its fault leaves, where a <c>catch</c> lets the worker go on.</summary>
    /// <param name="cell">The cell.</param>
    private static bool LoopExpected(LoopCell cell) =>
        cell.Body == Body.StartsUnawaited || cell.Body == Body.LeavesChild && cell.Reader == GroupReader.AfterCatch;

    /// <summary>The worker's body of a cell of the <c>Parallel</c> row.</summary>
    /// <param name="cell">The cell.</param>
    private static string LoopBody(LoopCell cell)
    {
        var awaits = cell.Loop == Loop.ForEachAsync;
        var wait = awaits ? "await inner;" : "inner.Wait();";
        var body = cell.Body switch
        {
            Body.Plain => "First();",
            Body.StartsAndWaits => $"var inner = Task.Run(() => First()); {wait}",
            Body.StartsUnawaited => "_ = Task.Run(() => First());",
            _ => $"var inner = Task.Run(() => First()); if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); {wait}"
        };
        var call = cell.Loop switch
        {
            Loop.For => $"Parallel.For(0, 2, i => {{ {body} }});",
            Loop.ForEach => $"Parallel.ForEach(new[] {{ 1, 2 }}, i => {{ {body} }});",
            Loop.Invoke => $"Parallel.Invoke(() => {{ {body} }}, () => {{ }});",
            Loop.ForToken => $"Parallel.For(0, 2, new ParallelOptions {{ CancellationToken = stoppingToken }}, i => {{ {body} }});",
            _ => $"await Parallel.ForEachAsync(new[] {{ 1, 2 }}, async (i, ct) => {{ {body} await Task.CompletedTask; }});"
        };
        return cell.Reader == GroupReader.AfterCatch ? $"try {{ {call} }} catch (Exception) {{ }} After();" : $"{call} After();";
    }

    /// <summary>#358 on its own input: a hosted service that runs once calls <c>Parallel.For</c>; each body creates a box, starts a
    /// task writing <c>box.Count</c> in <c>One</c> without waiting for it and writes <c>box.Count</c> in <c>Two</c>. Nothing orders the
    /// task's write before the body's, so the two race; the same body run through <c>Task.Run</c> keeps the pair. The first row is a
    /// known miss (#358): its expected answer is a kept pair. The body of <c>Parallel.ForEachAsync</c> is the twist of an async loop
    /// body starting unawaited work. Both writes must be collected, <c>One</c> in a spawned execution of its own, and the pair asked
    /// about has one end at each, so a self-pair of a repeated write answers nothing.</summary>
    /// <param name="through">How the body is run: <c>parallel</c>, <c>foreach-async</c> or <c>task-run</c>.</param>
    /// <param name="kept">Whether the analysis keeps the pair today; the requirements keep it in every row.</param>
    [Theory]
    [InlineData("parallel", false)]
    [InlineData("foreach-async", true)]
    [InlineData("task-run", true)]
    public void Unawaited_work_of_a_parallel_body_against_the_body_s_later_write(string through, bool kept)
    {
        const string BODY = "var box = new Box(); var t = Task.Run(() => box.One()); box.Two();";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            through switch
            {
                "parallel" => $"Parallel.For(0, 2, _ => {{ {BODY} }});",
                "foreach-async" => $"await Parallel.ForEachAsync(new[] {{ 1, 2 }}, async (i, ct) => {{ {BODY} await Task.CompletedTask; }});",
                _ => $"await Task.Run(() => {{ {BODY} }});"
            },
            "private sealed class Box { public int Count; public void One() { Count = 1; } public void Two() { Count = 2; } }"));
        var one = Assert.Single(run.Of("Count"), access => access.Symbol.EndsWith(".One()", StringComparison.Ordinal));
        var two = Assert.Single(run.Of("Count"), access => access.Symbol.EndsWith(".Two()", StringComparison.Ordinal));

        Assert.NotEqual(one.ExecutionId, two.ExecutionId);
        Assert.StartsWith(two.ExecutionId + ">", one.ExecutionId, StringComparison.Ordinal);
        Assert.Equal(kept, run.PairsOn("Count").Any(pair => pair.First == one && pair.Second == two || pair.First == two && pair.Second == one));
    }

    /// <summary>The bodies of an awaited <c>Parallel.ForEachAsync</c> and the bypass of its join (#365), each followed by a write in
    /// <c>After</c>. The loop's task completes only after every body finished (<c>Parallel.ForEachAsync.cs</c>, dotnet/runtime
    /// release/10.0): a body's own write after a suspension and a child the body waits for, by <c>await</c> or by <c>Wait()</c>, all
    /// precede the write after the <c>await</c>, while a child the body does not wait for does not. A loop's task saved before a
    /// <c>try</c> that may fault before awaiting it is bypassed while bodies run, so a write after the <c>catch</c> races with them;
    /// faulting only after the <c>await</c>, it does not. <paramref name="kept"/> is today's answer; where it differs from
    /// <paramref name="expected"/>, the reason stands in the results of #505.</summary>
    /// <param name="form">The name of the body or of the bypass.</param>
    /// <param name="expected">Whether the requirements keep the pair.</param>
    /// <param name="kept">Whether the analysis keeps the pair today.</param>
    [Theory]
    [InlineData("tail-write", false, false)]
    [InlineData("tail-write-loop-in-try", false, true)]
    [InlineData("tail-write-saved-loop-in-try", false, false)]
    [InlineData("awaited-child", false, true)]
    [InlineData("awaited-child-after-a-suspension", false, true)]
    [InlineData("blocking-joined-child", false, true)]
    [InlineData("detached-child", true, true)]
    [InlineData("365-fault-before-the-saved-loop-s-await", true, true)]
    [InlineData("365-fault-after-the-saved-loop-s-await", false, false)]
    public void ForEachAsync_bodies_and_the_bypass_of_its_join(string form, bool expected, bool kept)
    {
        const string LOOP = "Parallel.ForEachAsync(new[] { 1, 2 }, async (i, ct) => { ";
        var body = form switch
        {
            "tail-write" => $"await {LOOP}await Task.Yield(); First(); }}); After();",
            "tail-write-loop-in-try" => $"try {{ await {LOOP}await Task.Yield(); First(); }}); }} catch (Exception) {{ }} After();",
            "tail-write-saved-loop-in-try" => $"var loop = {LOOP}await Task.Yield(); First(); }}); try {{ await loop; }} catch (Exception) {{ }} After();",
            "awaited-child" => $"await {LOOP}var inner = Task.Run(() => First()); await inner; }}); After();",
            "awaited-child-after-a-suspension" => $"await {LOOP}await Task.Yield(); var inner = Task.Run(() => First()); await inner; }}); After();",
            "blocking-joined-child" => $"await {LOOP}var inner = Task.Run(() => First()); inner.Wait(); await Task.Yield(); }}); After();",
            "detached-child" => $"await {LOOP}_ = Task.Run(() => First()); await Task.Yield(); }}); After();",
            "365-fault-before-the-saved-loop-s-await" => $"var loop = {LOOP}await Task.Yield(); First(); }}); try {{ Fail(); await loop; }} catch (Exception) {{ }} After();",
            _ => $"var loop = {LOOP}await Task.Yield(); First(); }}); try {{ await loop; Fail(); }} catch (Exception) {{ }} After();"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, BlockingWaitAssumptionMatrixTests.FAIL));

        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{form}: today's answer moved; expected {(expected ? "a pair" : "no pair")} by the runtime.");
    }

    // ---- a timer's disposal ----

    /// <summary>The ways a worker leaves the wait for a one-shot <c>System.Threading.Timer</c> whose callback writes in <c>First</c>,
    /// then writes in <c>After</c>. <c>Dispose()</c> waits for nothing: «callbacks can occur after the Dispose() method overload has been
    /// called». <c>Dispose(WaitHandle)</c> signals the handle once «all currently queued callbacks have completed», so a return of
    /// <c>WaitOne()</c> follows them, and <c>WaitOne(100)</c> may return before. <c>DisposeAsync</c> «completes when all work associated
    /// with the timer has ceased» and stores in it every exception but a usage one, so obtaining it throws nothing either. A write the
    /// worker reaches around the wait — a fault before it under a <c>catch</c> or a <c>finally</c>, a wait on one branch, a token or a
    /// timeout ending the wait — races with the callback; <c>using</c> calls <c>Dispose()</c>, <c>await using</c> awaits
    /// <c>DisposeAsync</c>. Each form writes in <c>After</c> once. The drain obtained inside the <c>try</c> and the drain obtained before
    /// it tell apart the call that obtains the <c>ValueTask</c> from the <c>await</c> of it; the forms over <c>Dispose(WaitHandle)</c> and
    /// <c>WaitOne</c>, whose exceptional exit the analysis reads apart from the <c>await</c>'s, stand in rows of their own.
    /// <paramref name="kept"/> is today's answer; where it differs from <paramref name="expected"/>, the issue or reason stands in the
    /// results of #505.</summary>
    /// <param name="form">The name of the way the wait is reached or left.</param>
    /// <param name="expected">Whether the requirements keep the pair.</param>
    /// <param name="kept">Whether the analysis keeps the pair today.</param>
    [Theory]
    [InlineData("dispose-async", false, false)]
    [InlineData("drain-obtained-inside-the-try-caught", false, true)]
    [InlineData("drain-obtained-before-the-try-caught", false, false)]
    [InlineData("365-drain-before-the-try-caught", true, true)]
    [InlineData("drain-obtained-inside-the-try-write-in-finally", false, true)]
    [InlineData("drain-obtained-before-the-try-write-in-finally", false, false)]
    [InlineData("365-drain-before-the-try-write-in-finally", true, true)]
    [InlineData("dispose-async-on-one-branch", true, true)]
    [InlineData("dispose-async-under-a-token", true, true)]
    [InlineData("dispose-async-as-task-suppressed", false, true)]
    [InlineData("await-using", false, true)]
    [InlineData("using", true, true)]
    [InlineData("wait-handle", false, false)]
    [InlineData("wait-handle-dispose-inside-the-try-caught", false, true)]
    [InlineData("wait-handle-dispose-before-the-try-caught", false, true)]
    [InlineData("fault-before-wait-one-caught", true, true)]
    [InlineData("wait-handle-timeout", true, true)]
    public void Ways_around_the_wait_for_a_timer_s_callbacks(string form, bool expected, bool kept)
    {
        const string TIMER = "var timer = new Timer(_ => First(), null, 0, Timeout.Infinite);";
        const string HANDLE = "var done = new ManualResetEvent(false);";
        const string DRAIN = "var drain = timer.DisposeAsync();";
        var body = form switch
        {
            "dispose-async" => $"{TIMER} await timer.DisposeAsync(); After();",
            "drain-obtained-inside-the-try-caught" => $"{TIMER} try {{ await timer.DisposeAsync(); }} catch (Exception) {{ }} After();",
            "drain-obtained-before-the-try-caught" => $"{TIMER} {DRAIN} try {{ await drain; }} catch (Exception) {{ }} After();",
            "365-drain-before-the-try-caught" => $"{TIMER} {DRAIN} try {{ Fail(); await drain; }} catch (Exception) {{ }} After();",
            "drain-obtained-inside-the-try-write-in-finally" => $"{TIMER} try {{ await timer.DisposeAsync(); }} finally {{ After(); }}",
            "drain-obtained-before-the-try-write-in-finally" => $"{TIMER} {DRAIN} try {{ await drain; }} finally {{ After(); }}",
            "365-drain-before-the-try-write-in-finally" => $"{TIMER} {DRAIN} try {{ Fail(); await drain; }} finally {{ After(); }}",
            "dispose-async-on-one-branch" => $"{TIMER} if (Environment.TickCount % 2 == 0) await timer.DisposeAsync(); After();",
            "dispose-async-under-a-token" => $"{TIMER} try {{ await timer.DisposeAsync().AsTask().WaitAsync(stoppingToken); }} catch (OperationCanceledException) {{ }} After();",
            "dispose-async-as-task-suppressed" => $"{TIMER} await timer.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); After();",
            "await-using" => "await using (var timer = new Timer(_ => First(), null, 0, Timeout.Infinite)) { } After();",
            "using" => "using (var timer = new Timer(_ => First(), null, 0, Timeout.Infinite)) { } After();",
            "wait-handle" => $"{TIMER} {HANDLE} timer.Dispose(done); done.WaitOne(); After();",
            "wait-handle-dispose-inside-the-try-caught" => $"{TIMER} {HANDLE} try {{ timer.Dispose(done); done.WaitOne(); }} catch (Exception) {{ }} After();",
            "wait-handle-dispose-before-the-try-caught" => $"{TIMER} {HANDLE} timer.Dispose(done); try {{ done.WaitOne(); }} catch (Exception) {{ }} After();",
            "fault-before-wait-one-caught" => $"{TIMER} {HANDLE} timer.Dispose(done); try {{ Fail(); done.WaitOne(); }} catch (Exception) {{ }} After();",
            _ => $"{TIMER} {HANDLE} timer.Dispose(done); done.WaitOne(100); After();"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, BlockingWaitAssumptionMatrixTests.FAIL));
        Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{form}: today's answer moved; expected {(expected ? "a pair" : "no pair")} by the runtime.");
    }

    // ---- WhenAny and WaitAsync ----

    /// <summary>Waits that end on the first of several events, with task <c>a</c> writing in <c>First</c> and <c>b</c> returning, each
    /// followed by a write in <c>After</c>. <c>Task.WhenAny</c> completes with the first task to complete, and «the other tasks will
    /// continue running until completion»; with <c>a</c> its only task it completes when <c>a</c> does, and a loop that removes each
    /// finished task and waits again until none is left ends after all of them. <c>WaitAsync(TimeSpan)</c> faults with a
    /// <c>TimeoutException</c> while <c>a</c> may run, so its normal completion follows <c>a</c>'s; with <c>SuppressThrowing</c> its end
    /// by the token goes on to the write. <paramref name="kept"/> is today's answer; where it differs from <paramref name="expected"/>,
    /// the reason stands in the results of #505.</summary>
    /// <param name="form">The name of the wait.</param>
    /// <param name="expected">Whether the requirements keep the pair.</param>
    /// <param name="kept">Whether the analysis keeps the pair today.</param>
    [Theory]
    [InlineData("when-any-of-two", true, true)]
    [InlineData("when-any-of-two-then-its-result", true, true)]
    [InlineData("when-any-of-one", false, true)]
    [InlineData("when-any-drained", false, true)]
    [InlineData("wait-async-timeout", false, true)]
    [InlineData("wait-async-timeout-caught", true, true)]
    [InlineData("wait-async-token-suppressed", true, true)]
    public void Waits_that_end_on_the_first_event(string form, bool expected, bool kept)
    {
        const string TASKS = "var a = Task.Run(() => First()); var b = Task.Run(() => { });";
        var body = form switch
        {
            "when-any-of-two" => $"{TASKS} await Task.WhenAny(a, b);",
            "when-any-of-two-then-its-result" => $"{TASKS} var first = await Task.WhenAny(a, b); await first;",
            "when-any-of-one" => $"{TASKS} await Task.WhenAny(a);",
            "when-any-drained" => $"{TASKS} var pending = new System.Collections.Generic.List<Task> {{ a, b }}; " +
                                  "while (pending.Count > 0) { var done = await Task.WhenAny(pending); pending.Remove(done); }",
            "wait-async-timeout" => $"{TASKS} await a.WaitAsync(TimeSpan.FromSeconds(1));",
            "wait-async-timeout-caught" => $"{TASKS} try {{ await a.WaitAsync(TimeSpan.FromSeconds(1)); }} catch (TimeoutException) {{ }}",
            _ => $"{TASKS} await a.WaitAsync(stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker($"{body} After();"));

        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{form}: today's answer moved; expected {(expected ? "a pair" : "no pair")} by the runtime.");
    }

    // ---- the await of a helper's task: the issues ----

    /// <summary>#500 on its own input: a task starts a child writing in <c>First</c> and may throw before its join of it; the worker
    /// awaits the task with <c>ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c>, which does not throw at the fault, and
    /// writes. The fault leaves the child running, so the two race; with <c>ConfigureAwait(false)</c> the fault leaves the worker
    /// before the write and only the normal return, after the join, reaches it. Today both rows lose the pair, the first by the
    /// happens-before path: a known miss whose expected answer is a kept pair.</summary>
    /// <param name="suppress">Whether the await suppresses the fault, rather than <c>ConfigureAwait(false)</c>.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Suppressed_fault_of_a_task_leaving_its_child_is_ordered_by_the_path(bool suppress)
    {
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(
            "var outer = Task.Run(() => { var inner = Task.Run(() => First()); " +
            "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); inner.Wait(); }); " +
            $"await outer.ConfigureAwait({(suppress ? "ConfigureAwaitOptions.SuppressThrowing" : "false")}); After();"));
        var child = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".First()", StringComparison.Ordinal));
        var write = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.Equal(TimingBranch.HappensBeforePath, BlockingWaitAssumptionMatrixTests.Branch(run, child, write));
        Assert.False(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                     suppress ? "#500: the known miss is reported now; expect the pair." : "the pair the requirements remove is back.");
    }

    /// <summary>#504 on its inputs, the three helpers of #503: <c>A</c> starts a child writing in <c>First</c>, may block on it and
    /// awaits it; <c>B</c> may leave before its <c>await</c> of <c>A</c>'s task; <c>C</c> awaits <c>B</c>'s task and writes after
    /// <c>Task.Yield</c>. Part 1: <c>B</c> returns early, and the worker starts <c>C</c> and then loops for ever, with no normal exit.
    /// Part 2: <c>B</c> throws an exception made before <c>A</c> starts, on a condition computed before too, so no operation that may
    /// throw stands at the early exit, and <c>C</c> catches the fault. Leaving early, <c>B</c> hands <c>C</c> a task complete, or
    /// faulted, while the child may run, so the two writes race; leaving after its <c>await</c>, the child completed first. Both early
    /// rows are known misses (#504, parts 1 and 2): the read of <c>B</c>'s exits finds no normal exit of the worker's execution to
    /// stop at, and a <c>throw</c> whose operand and condition hold no operation that may throw registers no exceptional exit. Each
    /// early row has a contrast that undoes its one change and keeps the pair: the same helpers under a worker that awaits <c>C</c>
    /// and returns (<c>loop-returning-worker</c>), and a <c>throw new …</c> at the early exit (<c>new-exception</c>), as
    /// <c>BlockingWaitAssumptionMatrixTests.Three_helpers_the_middle_one_leaving_before_its_await_keep_the_pair</c> holds too. What
    /// orders a missed pair is the completion proof of the queried passes (<see cref="TimingBranch.PassCompletion"/>), with no
    /// assumption; the fixtures do not tell apart which branch of the proof's climb decides it.</summary>
    /// <param name="part">The input: <c>loop</c>, <c>loop-returning-worker</c>, <c>ready-exception</c> or <c>new-exception</c>.</param>
    /// <param name="early">Whether <c>B</c> may leave before its <c>await</c> of <c>A</c>'s task, rather than after it.</param>
    /// <param name="kept">Whether the analysis keeps the pair today; the requirements keep it exactly where <paramref name="early"/>.</param>
    [Theory]
    [InlineData("loop", true, false)]
    [InlineData("loop", false, false)]
    [InlineData("loop-returning-worker", true, true)]
    [InlineData("ready-exception", true, false)]
    [InlineData("ready-exception", false, false)]
    [InlineData("new-exception", true, true)]
    public void Helpers_leaving_early_without_an_operation_that_may_throw_or_under_a_worker_that_never_returns(string part, bool early,
                                                                                                             bool kept)
    {
        const string A = "private async Task A(bool probe) { var t = Task.Run(() => { First(); return 1; }); " +
                         "if (probe) { try { t.Wait(); } catch (Exception) { } } await t; } ";
        var throwing = part == "new-exception" ? "throw new InvalidOperationException();" : "throw failure;";
        var members = part.StartsWith("loop", StringComparison.Ordinal)
            ? A + $"private async Task B() {{ var a = A(Environment.TickCount % 3 == 0); {(early ? "if (Environment.TickCount % 2 == 0) return; await a;" : "await a; if (Environment.TickCount % 2 == 0) return;")} }} " +
              "private async Task C() { var b = B(); await b; await Task.Yield(); After(); }"
            : A + $"private async Task B(bool leave, Exception failure) {{ var a = A(Environment.TickCount % 3 == 0); {(early ? $"if (leave) {throwing} await a;" : $"await a; if (leave) {throwing}")} }} " +
              "private async Task C() { var leave = Environment.TickCount % 2 == 0; var failure = new InvalidOperationException(); var b = B(leave, failure); " +
              "try { await b; } catch (InvalidOperationException) { await Task.Yield(); After(); } }";
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(part == "loop" ? "var c = C(); for (;;) { }" : "var c = C(); await c;", members));
        var child = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".First()", StringComparison.Ordinal));
        var write = Assert.Single(run.Of("Value"), access => access.Symbol.EndsWith(".After()", StringComparison.Ordinal));

        Assert.Equal(kept ? null : TimingBranch.PassCompletion, BlockingWaitAssumptionMatrixTests.Branch(run, child, write));
        Assert.Empty(BlockingWaitAssumptionMatrixTests.Assumed(run, child, write));
        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"#504 {part}: today's answer moved; expected {(early ? "a pair" : "no pair")} by the runtime.");
    }

    // ---- blocking waits and Thread.Join ----

    /// <summary>The cells of the blocking waits and of <c>Thread.Join</c> the other matrices leave open, each with a write in
    /// <c>First</c> and one in <c>After</c>. A blocking <c>Wait()</c> returns or throws only after its task completed (TD-060, under
    /// the environment assumptions); a <c>finally</c> runs its code on the fault path too. A task without a normal return that faults
    /// before its join leaves its child running (the loop twist). <c>Thread.Join()</c> returns when the thread ended and throws only
    /// on an interrupt or for a thread never started, which this program does not do; <c>Join(int)</c> may return <c>false</c> first.
    /// A thread whose body returns before its join of a child it started leaves that child running. #361: a task joins
    /// <c>inner</c> (writing in <c>First</c>) before it may fault leaving <c>extra</c> running, so its fault orders <c>First</c>'s
    /// write though not <c>extra</c>'s. <paramref name="kept"/> is today's answer; where it differs from <paramref name="expected"/>,
    /// the issue or reason stands in the results of #505.</summary>
    /// <param name="form">The name of the form.</param>
    /// <param name="expected">Whether the requirements keep the pair.</param>
    /// <param name="kept">Whether the analysis keeps the pair today.</param>
    [Theory]
    [InlineData("wait-in-finally", false, false)]
    [InlineData("write-in-finally", false, false)]
    [InlineData("fault-before-wait-write-in-finally", true, true)]
    [InlineData("outer-without-normal-return-faults-before-its-join", true, true)]
    [InlineData("outer-without-normal-return-joins-first", false, true)]
    [InlineData("361-joined-child-before-a-fault-leaving-another", false, false)]
    [InlineData("361-wait-all-with-a-task-leaving-its-child", false, true)]
    [InlineData("thread-join", false, false)]
    [InlineData("thread-join-caught", false, true)]
    [InlineData("fault-before-thread-join-caught", true, true)]
    [InlineData("thread-join-timeout", true, true)]
    [InlineData("thread-join-in-finally", false, false)]
    [InlineData("thread-returns-before-joining-its-child", true, true)]
    public void Blocking_waits_and_thread_joins_left_early(string form, bool expected, bool kept)
    {
        const string TASK = "var t = Task.Run(() => First());";
        const string THREAD = "var th = new Thread(() => First()); th.Start();";
        const string LEAVES = "var outer = Task.Run((Action)(() => { var inner = Task.Run(() => First()); while (true) { ";
        var body = form switch
        {
            "wait-in-finally" => $"{TASK} try {{ Fail(); }} finally {{ t.Wait(); }} After();",
            "write-in-finally" => $"{TASK} try {{ t.Wait(); }} finally {{ After(); }}",
            "fault-before-wait-write-in-finally" => $"{TASK} try {{ Fail(); t.Wait(); }} finally {{ After(); }}",
            "outer-without-normal-return-faults-before-its-join" => $"{LEAVES}Fail(); inner.Wait(); }} }})); try {{ outer.Wait(); }} catch (Exception) {{ }} After();",
            "outer-without-normal-return-joins-first" => $"{LEAVES}inner.Wait(); Fail(); }} }})); try {{ outer.Wait(); }} catch (Exception) {{ }} After();",
            "361-joined-child-before-a-fault-leaving-another" => "var outer = Task.Run(() => { var inner = Task.Run(() => First()); inner.Wait(); " +
                                                                "var extra = Task.Run(() => Second()); Fail(); extra.Wait(); }); " +
                                                                "try { outer.Wait(); } catch (Exception) { } After();",
            "361-wait-all-with-a-task-leaving-its-child" => "var inner = Task.Run(() => First()); " +
                                                           "var extra = Task.Run(() => { var c = Task.Run(() => Second()); Fail(); c.Wait(); }); " +
                                                           "try { Task.WaitAll(inner, extra); } catch (Exception) { } After();",
            "thread-join" => $"{THREAD} th.Join(); After();",
            "thread-join-caught" => $"{THREAD} try {{ th.Join(); }} catch (Exception) {{ }} After();",
            "fault-before-thread-join-caught" => $"{THREAD} try {{ Fail(); th.Join(); }} catch (Exception) {{ }} After();",
            "thread-join-timeout" => $"{THREAD} th.Join(100); After();",
            "thread-join-in-finally" => $"{THREAD} try {{ Fail(); }} finally {{ th.Join(); }} After();",
            _ => "var th = new Thread(() => { var inner = Task.Run(() => First()); if (Environment.TickCount % 2 == 0) return; inner.Wait(); }); " +
                 "th.Start(); th.Join(); After();"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, BlockingWaitAssumptionMatrixTests.FAIL));

        Assert.True(kept == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After"),
                    $"{form}: today's answer moved; expected {(expected ? "a pair" : "no pair")} by the runtime.");
    }

    /// <summary>What the runtime does with the child's write and the reader's write: the antecedent completes while the child may run
    /// exactly where it may leave before its join, and the continuation runs then unless its options exclude that completion. The
    /// continuation's task completes once the antecedent has completed, whatever its options, so a write after an <c>await</c> of that
    /// task that swallows its exception keeps the pair wherever the antecedent may complete early. An antecedent with no normal
    /// return never runs to completion, so its <c>OnlyOnRanToCompletion</c> continuation never runs.</summary>
    /// <param name="cell">The cell.</param>
    private static Outcome Expected(Cell cell)
    {
        // Task.Run(Action) and an async lambda alike: a throw, a cancellation check or a cancellable wait faults (or cancels) the
        // antecedent before its join; an early return, a join on one branch and a timed wait that returns false complete it normally.
        var ranEarly = cell.Leave is Leave.EarlyReturn or Leave.ConditionalJoin or Leave.TimedWait;
        var endedEarly = ranEarly || cell.Leave is Leave.Throw or Leave.ThrowIfCanceled or Leave.CanceledWait or Leave.LoopWithoutExit;
        if (cell.Reader != Reader.InContinuation || cell.Option != Option.OnlyOnRanToCompletion)
            return endedEarly ? Outcome.Pair : Outcome.NoPair;
        if (cell.Leave is Leave.LoopWithoutExit or Leave.LoopJoinFirst)
            return Outcome.Unreachable;
        return ranEarly ? Outcome.Pair : Outcome.NoPair;
    }

    /// <summary>Whether the child's write and the reader's write form a pair in the cell's program. The child's write must be
    /// collected; the reader's is required only where it can run.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Paired(Cell cell)
    {
        var options = cell.Option == Option.OnlyOnRanToCompletion ? ", TaskContinuationOptions.OnlyOnRanToCompletion" : string.Empty;
        var outer = $"var outer = {Antecedent(cell.Leave, cell.Join)};";
        var body = cell.Reader switch
        {
            Reader.InContinuation => $"{outer} outer.ContinueWith(_ => After(){options});",
            Reader.AfterCaughtAwait => $"{outer} var next = outer.ContinueWith(_ => {{ }}{options}); try {{ await next; }} catch (Exception) {{ }} After();",
            _ => $"{outer} var next = outer.ContinueWith(_ => {{ }}{options}); await next.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); After();"
        };
        var run = Analyze(BlockingWaitAssumptionMatrixTests.Worker(body, BlockingWaitAssumptionMatrixTests.FAIL));
        return Expected(cell) == Outcome.Unreachable ? PairsIfCollected(run, "First", "After") : BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "After");
    }

    /// <summary>Whether a write of one method pairs with a write of another, the second of which the analysis may not collect at all:
    /// the first must be collected, and a second that is not collected pairs with nothing.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="first">The method whose write must be collected.</param>
    /// <param name="second">The method whose write may be missing.</param>
    private static bool PairsIfCollected(EngineRun run, string first, string second)
    {
        Assert.Contains(run.Of("Value"), access => access.Symbol.EndsWith($".{first}()", StringComparison.Ordinal));
        return run.Of("Value").Any(access => access.Symbol.EndsWith($".{second}()", StringComparison.Ordinal)) &&
               BlockingWaitAssumptionMatrixTests.Pairs(run, first, second);
    }

    /// <summary>A <c>Task.Run</c> whose work starts a child writing in <c>First</c> and leaves or joins it as given. A synchronous
    /// lambda whose end is unreachable converts to <c>Func&lt;Task&gt;</c> as well, which overload resolution prefers, so it is cast to
    /// <c>Action</c> to stay the work of a plain <c>Task.Run(Action)</c>.</summary>
    /// <param name="leave">How the work may leave before its join of the child.</param>
    /// <param name="join">How the work joins the child.</param>
    private static string Antecedent(Leave leave, InnerJoin join)
    {
        const string THROW = "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();";
        var wait = join == InnerJoin.Wait ? "inner.Wait();" : "await inner;";
        var shape = leave switch
        {
            Leave.Throw => $"{THROW} {wait}",
            Leave.ThrowAfterJoin => $"{wait} {THROW}",
            Leave.CaughtThenJoin => $"try {{ Fail(); }} catch (Exception) {{ }} {wait}",
            Leave.EarlyReturn => $"if (Environment.TickCount % 2 == 0) return; {wait}",
            Leave.ConditionalJoin => $"if (Environment.TickCount % 2 == 0) {{ {wait} }}",
            Leave.ThrowIfCanceled => $"stoppingToken.ThrowIfCancellationRequested(); {wait}",
            Leave.CanceledWait => join == InnerJoin.Wait ? "inner.Wait(stoppingToken);" : "await inner.WaitAsync(stoppingToken);",
            Leave.TimedWait => join == InnerJoin.Wait ? "inner.Wait(100);" : "await Task.WhenAny(inner, Task.Delay(100));",
            Leave.LoopWithoutExit => $"while (true) {{ Fail(); {wait} }}",
            Leave.LoopJoinFirst => $"while (true) {{ {wait} Fail(); }}",
            Leave.JoinInFinally => $"try {{ Fail(); }} finally {{ {wait} }}",
            _ => wait
        };
        var work = $"{(join == InnerJoin.Await ? "async " : string.Empty)}() => {{ var inner = Task.Run(() => First()); {shape} }}";
        return join == InnerJoin.Wait && leave is Leave.LoopWithoutExit or Leave.LoopJoinFirst ? $"Task.Run((Action)({work}))" : $"Task.Run({work})";
    }

    /// <summary>The cells of one way of leaving, under both joins, for the given options and readers, each listed with its reason.
    /// The cells are named here, not chosen through <see cref="Expected"/>.</summary>
    /// <param name="leave">The way of leaving.</param>
    /// <param name="readers">The options and readers listed.</param>
    /// <param name="why">The issue or the reason.</param>
    private static IEnumerable<Listed> Listing(Leave leave, (Option Option, Reader Reader)[] readers, string why) =>
        from inner in Enum.GetValues<InnerJoin>()
        from reader in readers
        select new Listed(new Cell(leave, inner, reader.Option, reader.Reader), why);
}
