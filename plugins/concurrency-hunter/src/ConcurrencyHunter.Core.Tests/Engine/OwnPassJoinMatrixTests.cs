using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The join of a pass's own work (R1) and the waits that do not make one (R2), over every form of start and wait, the handle
/// waited on, the path to the wait, the parent running the passes and the second side of the pair: the work itself, the parent's
/// access before the start in the next pass, the parent's access after the wait.</summary>
public sealed class OwnPassJoinMatrixTests
{
    public enum Form
    {
        TaskRunAwait, TaskRunWait, TaskRunResult, TaskRunGetResult, TaskRunWaitAll, TaskRunWaitTimeout, TaskRunWaitToken, StartNewAwait,
        AsyncCallAwait, AsyncCallWait, ThreadJoin, ParallelFor, ParallelForEach, TimerDisposeAsync, TimerDisposeWaitHandle
    }

    public enum Handle { Own, Previous, Foreign, Conditional, Completed }

    public enum WaitPath { AllPaths, Bypass, ExceptionBeforeWait }

    public enum Parent { Hosted, Http }

    public enum Side { SameWork, ParentBeforeStart, ParentAfterWait }

    public sealed record Cell(Form Form, Handle Handle, WaitPath Path, Parent Parent, Side Side)
    {
        public override string ToString() => $"{Form}/{Handle}/{Path}/{Parent}/{Side}";
    }

    /// <summary>The cells whose expected answer — no pair — the analysis cannot reach without what the run does not build, each by name
    /// with the reason; a name that names no such cell fails <see cref="Every_wider_cell_names_a_cell_that_expects_no_pair"/>.</summary>
    private static readonly Dictionary<string, string> Wider = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Lazy<Observed>> Runs = new(StringComparer.Ordinal);

    private sealed record Observed(bool SameWork, bool ParentBeforeStart, bool ParentAfterWait);

    public static IEnumerable<Cell> AllCells() =>
        from form in Enum.GetValues<Form>()
        from handle in Enum.GetValues<Handle>()
        from path in Enum.GetValues<WaitPath>()
        from parent in Enum.GetValues<Parent>()
        from side in Enum.GetValues<Side>()
        let cell = new Cell(form, handle, path, parent, side)
        where Allowed(cell)
        select cell;

    public static TheoryData<Form, Handle, WaitPath, Parent, Side> Cells()
    {
        var cells = new TheoryData<Form, Handle, WaitPath, Parent, Side>();
        foreach (var cell in AllCells())
            cells.Add(cell.Form, cell.Handle, cell.Path, cell.Parent, cell.Side);
        return cells;
    }

    [Fact]
    public void There_are_752_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(752, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Form>(), form => Assert.Contains(cells, cell => cell.Form == form));
        Assert.All(Enum.GetValues<Handle>(), handle => Assert.Contains(cells, cell => cell.Handle == handle));
        Assert.All(Enum.GetValues<WaitPath>(), path => Assert.Contains(cells, cell => cell.Path == path));
        Assert.All(Enum.GetValues<Parent>(), parent => Assert.Contains(cells, cell => cell.Parent == parent));
        Assert.All(Enum.GetValues<Side>(), side => Assert.Contains(cells, cell => cell.Side == side));
        Assert.Contains(cells, cell => !PairExpected(cell));
        Assert.Contains(cells, PairExpected);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        var cells = AllCells().ToDictionary(cell => cell.ToString(), StringComparer.Ordinal);

        Assert.All(Wider, wider =>
        {
            Assert.True(cells.TryGetValue(wider.Key, out var cell), $"{wider.Key} names no cell.");
            Assert.False(PairExpected(cell!), $"{wider.Key} expects a pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Value));
        });
    }

    /// <summary>No cell is narrower than its expectation; a cell outside <see cref="Wider"/> answers exactly, and one inside it keeps the
    /// pair it is listed for.</summary>
    /// <param name="form">How the pass starts the work and waits for it.</param>
    /// <param name="handle">What the wait waits on.</param>
    /// <param name="path">How the paths of the pass reach the wait.</param>
    /// <param name="parent">The execution running the passes.</param>
    /// <param name="side">The other access of the pair.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Form form, Handle handle, WaitPath path, Parent parent, Side side)
    {
        var cell = new Cell(form, handle, path, parent, side);
        var observed = Runs.GetOrAdd(Source(cell), source => new Lazy<Observed>(() => Observe(source))).Value;
        var pair = side switch
        {
            Side.SameWork => observed.SameWork,
            Side.ParentBeforeStart => observed.ParentBeforeStart,
            _ => observed.ParentAfterWait
        };

        if (PairExpected(cell))
            Assert.True(pair, $"{cell}: the pair the requirements keep is gone.");
        else if (Wider.ContainsKey(cell.ToString()))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.False(pair, $"{cell}: the pair the requirements remove is still there.");
    }

    /// <summary>Which values of the axes make a cell.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) =>
        // Only a task can be a completed task; the previous, foreign or conditional thread or timer is an object from a field or from
        // another start.
        (cell.Handle != Handle.Completed || IsTask(cell.Form)) &&
        // A Parallel call has no handle: its join is the return of the call itself, which no branch bypasses and no exception precedes.
        (!IsParallel(cell.Form) || cell.Handle == Handle.Own && cell.Path == WaitPath.AllPaths) &&
        // The work of an HTTP action pairs with the action's own accesses whatever the side, so those sides are read in a hosted parent,
        // where a negative cell checks the parent's route as well as the self-pair.
        (cell.Side == Side.SameWork || cell.Parent == Parent.Hosted);

    /// <summary>Whether the requirements keep the pair of a cell.</summary>
    /// <param name="cell">The cell.</param>
    private static bool PairExpected(Cell cell)
    {
        // R2: a wait with a timeout or a token may return before the work completes.
        if (cell.Form is Form.TaskRunWaitTimeout or Form.TaskRunWaitToken)
            return true;
        // R2: the handle of a previous pass, of another start, a conditionally own one or a completed task is not this pass's work.
        if (cell.Handle != Handle.Own)
            return true;
        // R2: a branch that bypasses the wait, or an exception before it that a handler leads to the next pass, leaves the work running.
        if (cell.Path != WaitPath.AllPaths)
            return true;
        // R2: the requests of an HTTP action overlap one another, so its passes are no passes one after another.
        if (cell.Parent == Parent.Http)
            return true;
        // R2: the iterations of one Parallel call and the callbacks of one periodic timer overlap one another.
        if (cell.Side == Side.SameWork && (IsParallel(cell.Form) || IsTimer(cell.Form)))
            return true;
        // R1: the pass waits for its own work on every path, in a parent that does not overlap itself.
        return false;
    }

    private static bool IsTask(Form form) => form is not (Form.ThreadJoin or Form.ParallelFor or Form.ParallelForEach or Form.TimerDisposeAsync or
                                                          Form.TimerDisposeWaitHandle);

    private static bool IsParallel(Form form) => form is Form.ParallelFor or Form.ParallelForEach;

    private static bool IsTimer(Form form) => form is Form.TimerDisposeAsync or Form.TimerDisposeWaitHandle;

    /// <summary>Runs the program of a cell and reads which of the work's pairs it keeps.</summary>
    /// <param name="source">The program.</param>
    private static Observed Observe(string source)
    {
        var run = Analyze(source);
        Assert.Contains(run.Of("Value"), access => Is(access.Symbol, "Write"));
        Assert.Contains(run.Of("Value"), access => Is(access.Symbol, "Before"));
        Assert.Contains(run.Of("Value"), access => Is(access.Symbol, "After"));
        var pairs = run.PairsOn("Value");
        return new Observed(Pairs(pairs, "Write"), Pairs(pairs, "Before"), Pairs(pairs, "After"));

        static bool Pairs(IReadOnlyList<AccessPair> pairs, string other) => pairs.Any(pair =>
            Is(pair.First.Symbol, "Write") && Is(pair.Second.Symbol, other) || Is(pair.First.Symbol, other) && Is(pair.Second.Symbol, "Write"));

        static bool Is(string symbol, string method) => symbol.EndsWith($".{method}()", StringComparison.Ordinal);
    }

    /// <summary>The program of a cell: a loop whose every pass writes in the parent before the start, starts the work, waits as the
    /// cell says and writes in the parent after the wait; the work writes the same field.</summary>
    /// <param name="cell">The cell; its side does not change the program.</param>
    private static string Source(Cell cell)
    {
        var (type, start, wait, completed) = Shape(cell.Form);
        string pass;
        if (IsParallel(cell.Form))
            pass = start("own", "Write");
        else
        {
            var (setup, waited) = cell.Handle switch
            {
                Handle.Own => (start("own", "Write"), "own"),
                Handle.Previous => (start("own", "Write") + " var waited = previous!; previous = own;", "waited"),
                Handle.Foreign => (start("own", "Write") + " " + start("other", "Idle"), "other"),
                Handle.Conditional => (start("own", "Write") + " " + start("other", "Idle") +
                                       " var waited = stoppingToken.CanBeCanceled ? own : other;", "waited"),
                _ => (start("own", "Write"), completed)
            };
            pass = setup + " " + cell.Path switch
            {
                WaitPath.AllPaths => wait(waited),
                WaitPath.Bypass => "if (stoppingToken.CanBeCanceled) { " + wait(waited) + " }",
                _ => "try { MayThrow(); " + wait(waited) + " } catch (InvalidOperationException) { }"
            };
        }

        var members = $$"""
                public static int Value;
                private static bool Flag;
                private {{type}}? previous;
                private void Write() { Value = 1; }
                private int WriteValue() { Write(); return 1; }
                private async Task WriteAsync() { await Task.Yield(); Write(); }
                private void Idle() { }
                private int IdleValue() => 0;
                private async Task IdleAsync() { await Task.Yield(); }
                private void Before() { Value = 2; }
                private void After() { Value = 3; }
                private static void MayThrow() { if (Flag) throw new InvalidOperationException(); }
            """;
        var loop = $$"""
                    for (var pass = 0; pass < 2; pass++)
                    {
                        Before();
                        {{pass}}
                        After();
                    }
            """;
        return cell.Parent == Parent.Hosted
            ? Startup("services.AddHostedService<Worker>();") + $$"""

                public sealed class Worker : BackgroundService
                {
                {{members}}
                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                {{loop}}
                    }
                }
                """
            : Startup() + $$"""

                public sealed class LoopController : ControllerBase
                {
                {{members}}
                    public async Task Post(CancellationToken stoppingToken)
                    {
                {{loop}}
                    }
                }
                """;
    }

    /// <summary>How a form starts work and waits for it: the handle's type, the start of a handle variable running a body (<c>Write</c>
    /// or <c>Idle</c>), the wait on a handle and the completed task of the handle's type.</summary>
    /// <param name="form">The form.</param>
    private static (string Type, Func<string, string, string> Start, Func<string, string> Wait, string Completed) Shape(Form form)
    {
        static string Run(string variable, string body) => $"var {variable} = Task.Run({body});";
        static string Await(string handle) => $"await {handle};";
        static string Wait(string handle) => $"{handle}.Wait();";
        return form switch
        {
            Form.TaskRunAwait => ("Task", Run, Await, "Task.CompletedTask"),
            Form.TaskRunWait => ("Task", Run, Wait, "Task.CompletedTask"),
            Form.TaskRunResult => ("Task<int>", (variable, body) => $"var {variable} = Task.Run({body}Value);", handle => $"_ = {handle}.Result;",
                                   "Task.FromResult(0)"),
            Form.TaskRunGetResult => ("Task", Run, handle => $"{handle}.GetAwaiter().GetResult();", "Task.CompletedTask"),
            Form.TaskRunWaitAll => ("Task", Run, handle => $"Task.WaitAll({handle});", "Task.CompletedTask"),
            Form.TaskRunWaitTimeout => ("Task", Run, handle => $"{handle}.Wait(100);", "Task.CompletedTask"),
            Form.TaskRunWaitToken => ("Task", Run, handle => $"{handle}.Wait(stoppingToken);", "Task.CompletedTask"),
            Form.StartNewAwait => ("Task", (variable, body) => $"var {variable} = Task.Factory.StartNew({body});", Await, "Task.CompletedTask"),
            Form.AsyncCallAwait => ("Task", (variable, body) => $"var {variable} = {body}Async();", Await, "Task.CompletedTask"),
            Form.AsyncCallWait => ("Task", (variable, body) => $"var {variable} = {body}Async();", Wait, "Task.CompletedTask"),
            Form.ThreadJoin => ("Thread", (variable, body) => $"var {variable} = new Thread({body}); {variable}.Start();", handle => $"{handle}.Join();", ""),
            Form.ParallelFor => ("object", (_, body) => $"Parallel.For(0, 2, _ => {body}());", _ => "", ""),
            Form.ParallelForEach => ("object", (_, body) => $"Parallel.ForEach(new[] {{ 0, 1 }}, _ => {body}());", _ => "", ""),
            Form.TimerDisposeAsync => ("Timer", (variable, body) => $"var {variable} = new Timer(_ => {body}(), null, 0, 1000);",
                                       handle => $"await {handle}.DisposeAsync();", ""),
            _ => ("Timer", (variable, body) => $"var {variable} = new Timer(_ => {body}(), null, 0, 1000);",
                  handle => $"var done = new ManualResetEvent(false); {handle}.Dispose(done); done.WaitOne();", "")
        };
    }
}
