using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// Whether an execution runs once per run of the creator of a <c>TreeConfined</c> region, and the pair function over it (R5, ADR 0008,
/// ADR 0020). In each cell an HTTP action, the creator, makes an object and writes its field before the start and after its wait, and
/// the started execution writes it too. Repetition and order are decided apart: the execution runs once per run of the creator exactly
/// when its start runs its work once, its site is passed once and its chain is the creator or once-started work; and its order against
/// the creator's writes is exactly that of the cell's once-running twin, the same code with a hosted worker's <c>ExecuteAsync</c> as the
/// creator and the object also stored into a singleton, which today's rules decide on an <c>Escaped</c> region.
/// </summary>
public sealed class OncePerCreatorRunMatrixTests
{
    public enum StartKind
    {
        TaskRun,
        ThreadStart,
        OneShotTimer,
        PeriodicTimer,
        AsyncTail,
        ContinueWith,
        ParallelFor,
        UnresolvedHandoff,
        Unrecognized,
        Recursion
    }

    public enum Site
    {
        Once,
        Loop
    }

    public enum Chain
    {
        Creator,
        OnceWork,
        LoopedWork
    }

    public enum CreatorAccess
    {
        BeforeStart,
        AfterWait
    }

    public sealed record Cell(StartKind Kind, Site Site, Chain Chain, CreatorAccess Access)
    {
        public override string ToString() => $"{Kind}/{Site}/{Chain}/{Access}";
    }

    private const string BEFORE = "box.N = 1;";
    private const string WORK = "box.N = 2;";
    private const string AFTER = "box.N = 3;";

    private static readonly ConcurrentDictionary<string, Observed> RUNS = new(StringComparer.Ordinal);

    public static IEnumerable<Cell> Cells() =>
        from kind in Enum.GetValues<StartKind>()
        from site in Enum.GetValues<Site>()
        from chain in Enum.GetValues<Chain>()
        from access in Enum.GetValues<CreatorAccess>()
        select new Cell(kind, site, chain, access);

    /// <summary>The cells a delegate the creator itself hands to an unresolved call makes: the region stays <c>ThreadConfined</c>
    /// (ADR 0011, TD-051), so they pair nothing and have no twin.</summary>
    /// <param name="cell">The cell.</param>
    private static bool LeftConfined(Cell cell) => cell is { Kind: StartKind.UnresolvedHandoff, Chain: Chain.Creator };

    /// <summary>Whether the started execution of a cell runs once per run of the creator: its start runs its work once, its site is
    /// passed once and its chain is the creator or once-started work.</summary>
    /// <param name="cell">The cell.</param>
    private static bool ExpectedOnce(Cell cell) =>
        cell.Kind is StartKind.TaskRun or StartKind.ThreadStart or StartKind.OneShotTimer or StartKind.AsyncTail or StartKind.ContinueWith &&
        cell.Site == Site.Once && cell.Chain is Chain.Creator or Chain.OnceWork;

    [Fact]
    public void Every_cell_runs_as_expected()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var observed = Observe(cell, twin: false);
            if (LeftConfined(cell))
            {
                if (observed.Ownership.Kind != OwnershipKind.ThreadConfined || observed.Run.PairsOn("N").Count != 0)
                    failures.Add($"{cell}: expected ThreadConfined and no pair, got {observed.Ownership.Kind} and {observed.Run.PairsOn("N").Count} pairs");
                continue;
            }

            if (observed.Ownership.Kind != OwnershipKind.TreeConfined)
            {
                failures.Add($"{cell}: expected TreeConfined, got {observed.Ownership.Kind}");
                continue;
            }

            var expected = ExpectedOnce(cell);
            foreach (var work in observed.Work)
            {
                if (!Starts(cell.Kind, observed.Analysis.Execution(work.ExecutionId)))
                    failures.Add($"{cell}: {work.ExecutionId} is not started by {cell.Kind}");
                var once = observed.Analysis.RunsOncePerRunOf(work.ExecutionId, observed.Ownership.Creator!);
                var self = observed.Analysis.Timing(work, work);
                var pairsItself = observed.Run.Pairs.Pairs.Any(pair => ReferenceEquals(pair.First, work) && ReferenceEquals(pair.Second, work));
                if (once != expected || self != (expected ? PairTiming.NoOverlap : PairTiming.Concurrent) || pairsItself == expected)
                    failures.Add($"{cell}: {work.ExecutionId} runs once {once}, timing {self}, pairs itself {pairsItself}; expected once {expected}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_cell_is_ordered_as_its_once_running_twin()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => !LeftConfined(cell)))
        {
            var tree = Observe(cell, twin: false);
            var twin = Observe(cell, twin: true);
            if (twin.Ownership.Kind != OwnershipKind.Escaped)
            {
                failures.Add($"{cell}: the twin is {twin.Ownership.Kind}, not Escaped");
                continue;
            }

            var ours = tree.Work.Select(work => tree.Analysis.Timing(tree.Creator(cell.Access), work)).ToArray();
            var theirs = twin.Work.Select(work => twin.Analysis.Timing(twin.Creator(cell.Access), work)).ToArray();
            if (!ours.SequenceEqual(theirs))
                failures.Add($"{cell}: [{string.Join(", ", ours)}] against the twin's [{string.Join(", ", theirs)}]");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(StartKind.TaskRun, Chain.Creator, PairTiming.NoOverlap, PairTiming.Ordered, PairTiming.Ordered)]
    [InlineData(StartKind.ParallelFor, Chain.Creator, PairTiming.Concurrent, PairTiming.Ordered, PairTiming.Ordered)]
    [InlineData(StartKind.PeriodicTimer, Chain.Creator, PairTiming.Concurrent, PairTiming.Ordered, PairTiming.Ordered)]
    [InlineData(StartKind.UnresolvedHandoff, Chain.OnceWork, PairTiming.Concurrent, PairTiming.Concurrent, PairTiming.Concurrent)]
    public void Stated_cells_are_ordered_as_stated(StartKind kind, Chain chain, PairTiming itself, PairTiming before, PairTiming after)
    {
        var observed = Observe(new Cell(kind, Site.Once, chain, CreatorAccess.BeforeStart), twin: false);

        var work = Assert.Single(observed.Work);
        Assert.Equal(OwnershipKind.TreeConfined, observed.Ownership.Kind);
        Assert.Equal(itself, observed.Analysis.Timing(work, work));
        Assert.Equal(before, observed.Analysis.Timing(observed.Creator(CreatorAccess.BeforeStart), work));
        Assert.Equal(after, observed.Analysis.Timing(observed.Creator(CreatorAccess.AfterWait), work));
    }

    [Fact]
    public void No_repeating_start_counts_once()
    {
        var repeating = Cells().Where(cell => !LeftConfined(cell) &&
                                              (cell.Kind is StartKind.PeriodicTimer or StartKind.ParallelFor or StartKind.UnresolvedHandoff
                                                   or StartKind.Unrecognized or StartKind.Recursion ||
                                               cell.Site == Site.Loop || cell.Chain == Chain.LoopedWork))
                               .ToArray();

        Assert.NotEmpty(repeating);
        Assert.All(repeating, cell =>
        {
            var observed = Observe(cell, twin: false);
            Assert.NotEmpty(observed.Work);
            Assert.All(observed.Work, work => Assert.False(observed.Analysis.RunsOncePerRunOf(work.ExecutionId, observed.Ownership.Creator!), cell.ToString()));
        });
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(Enum.GetValues<StartKind>(), cells.Select(cell => cell.Kind).Distinct());
        Assert.Equal(Enum.GetValues<Site>(), cells.Select(cell => cell.Site).Distinct());
        Assert.Equal(Enum.GetValues<Chain>(), cells.Select(cell => cell.Chain).Distinct());
        Assert.Equal(Enum.GetValues<CreatorAccess>(), cells.Select(cell => cell.Access).Distinct());
        Assert.Equal(10 * 2 * 3 * 2, cells.Length);
    }

    /// <summary>Whether the execution writing in a cell is started as the cell's start kind says, so the fixture builds what it
    /// claims.</summary>
    /// <param name="kind">The cell's start kind.</param>
    /// <param name="execution">The execution writing in the cell.</param>
    private static bool Starts(StartKind kind, ExecutionInstance execution) => kind switch
    {
        StartKind.TaskRun => execution.Origin is { Api: "Task.Run", IsTail: false },
        StartKind.ThreadStart => execution.Origin?.Api == "Thread.Start",
        StartKind.OneShotTimer => execution is { Kind: ExecutionKind.TimerCallback, StartRepeats: false },
        StartKind.PeriodicTimer => execution is { Kind: ExecutionKind.TimerCallback, StartRepeats: true },
        StartKind.AsyncTail => execution.Origin?.Api == "async-call",
        StartKind.ContinueWith => execution.Origin?.Api == "Task.ContinueWith",
        StartKind.ParallelFor => execution.Origin?.Api == "Parallel.For",
        StartKind.UnresolvedHandoff => execution.Kind == ExecutionKind.UnknownDelegateCall,
        StartKind.Unrecognized => execution.Origin?.Api == "unrecognized",
        _ => execution.IsRecursive
    };

    // ---- the fixture of a cell ----

    /// <summary>The run of a cell's code, its box's ownership, the creator's two writes and the started execution's writes, in source
    /// order of their executions.</summary>
    /// <param name="Run">The engine run of the cell's code.</param>
    /// <param name="Ownership">The ownership of the box.</param>
    /// <param name="Before">The creator's write before the start.</param>
    /// <param name="After">The creator's write after its wait.</param>
    /// <param name="Work">The started execution's writes.</param>
    private sealed record Observed(EngineRun Run, RegionOwnership Ownership, Access Before, Access After, IReadOnlyList<Access> Work)
    {
        public ExecutionAnalysis Analysis => Run.Execution.Analysis;
        public Access Creator(CreatorAccess access) => access == CreatorAccess.BeforeStart ? Before : After;
    }

    /// <summary>The analysis of a cell's code, or of its twin's, run once and kept for every test that asks.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="twin">Whether the once-running twin is asked for.</param>
    private static Observed Observe(Cell cell, bool twin)
    {
        var source = Source(cell, twin);
        return RUNS.GetOrAdd(source, _ =>
        {
            var run = Analyze(source);
            var box = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.TypeKey == "Fixture:Box");
            var writes = run.Collection.Accesses.Where(access => access.Resource.RegionId == box.Identity &&
                                                                 access.Resource.AccessPath.SequenceEqual(["N"]) &&
                                                                 access.Operation == AccessOperation.Write && !access.IsConstructionLocal)
                            .ToArray();
            Access[] At(string text) =>
                writes.Where(access => access.Source.StartLine == Line(source, text)).OrderBy(access => access.ExecutionId, StringComparer.Ordinal).ToArray();
            return new Observed(run, run.Execution.Analysis.Ownership[box.Identity], Assert.Single(At(BEFORE)), Assert.Single(At(AFTER)),
                                At(WORK) is { Length: > 0 } work ? work : throw new InvalidOperationException($"{cell}: no write of the work"));
        });
    }

    private static int Line(string source, string text) =>
        Array.FindIndex((Usings + source).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    /// <summary>The start of the cell's execution and the wait for it, at its site.</summary>
    /// <param name="kind">The start kind.</param>
    /// <param name="site">Whether the site is passed once or in a loop.</param>
    private static (string Start, string Wait) Form(StartKind kind, Site site) => (kind, site) switch
    {
        (StartKind.TaskRun, Site.Once) => ($"var t = Task.Run(() => {{ {WORK} }});", "await t;"),
        (StartKind.TaskRun, _) => ($"var ts = new Task[n]; for (var i = 0; i < n; i++) ts[i] = Task.Run(() => {{ {WORK} }});", "await Task.WhenAll(ts);"),
        (StartKind.ThreadStart, Site.Once) => ($"var t = new Thread(() => {{ {WORK} }}); t.Start();", "t.Join();"),
        (StartKind.ThreadStart, _) => ($"var ts = new Thread[n]; for (var i = 0; i < n; i++) {{ ts[i] = new Thread(() => {{ {WORK} }}); ts[i].Start(); }}",
                                       "foreach (var t in ts) t.Join();"),
        (StartKind.OneShotTimer, Site.Once) => ($"var timer = new System.Threading.Timer(_ => {{ {WORK} }}, null, 0, Timeout.Infinite);",
                                                "await timer.DisposeAsync();"),
        (StartKind.OneShotTimer, _) => ($"var timers = new System.Threading.Timer[n]; for (var i = 0; i < n; i++) timers[i] = new System.Threading.Timer(_ => {{ {WORK} }}, null, 0, Timeout.Infinite);",
                                        "foreach (var timer in timers) await timer.DisposeAsync();"),
        (StartKind.PeriodicTimer, Site.Once) => ($"var timer = new System.Threading.Timer(_ => {{ {WORK} }}, null, 0, 1000);", "await timer.DisposeAsync();"),
        (StartKind.PeriodicTimer, _) => ($"var timers = new System.Threading.Timer[n]; for (var i = 0; i < n; i++) timers[i] = new System.Threading.Timer(_ => {{ {WORK} }}, null, 0, 1000);",
                                         "foreach (var timer in timers) await timer.DisposeAsync();"),
        (StartKind.AsyncTail, Site.Once) => ("var t = Helper.Work(box);", "await t;"),
        (StartKind.AsyncTail, _) => ("var ts = new Task[n]; for (var i = 0; i < n; i++) ts[i] = Helper.Work(box);", "await Task.WhenAll(ts);"),
        (StartKind.ContinueWith, Site.Once) => ($"var first = Task.Run(() => {{ }}); var t = first.ContinueWith(_ => {{ {WORK} }});", "await t;"),
        (StartKind.ContinueWith, _) => ($"var ts = new Task[n]; for (var i = 0; i < n; i++) {{ var first = Task.Run(() => {{ }}); ts[i] = first.ContinueWith(_ => {{ {WORK} }}); }}",
                                        "await Task.WhenAll(ts);"),
        (StartKind.ParallelFor, Site.Once) => ($"Parallel.For(0, n, i => {{ {WORK} }});", ""),
        (StartKind.ParallelFor, _) => ($"for (var j = 0; j < n; j++) Parallel.For(0, n, i => {{ {WORK} }});", ""),
        (StartKind.UnresolvedHandoff, Site.Once) => ($"Sinks.Consumer!.Take(() => {{ {WORK} }});", ""),
        (StartKind.UnresolvedHandoff, _) => ($"for (var i = 0; i < n; i++) Sinks.Consumer!.Take(() => {{ {WORK} }});", ""),
        (StartKind.Unrecognized, Site.Once) => ($"Parallel.Invoke(() => {{ {WORK} }});", ""),
        (StartKind.Unrecognized, _) => ($"for (var i = 0; i < n; i++) Parallel.Invoke(() => {{ {WORK} }});", ""),
        (StartKind.Recursion, Site.Once) => ("var t = Helper.Start(box, n);", "await t;"),
        _ => ("var ts = new Task[n]; for (var i = 0; i < n; i++) ts[i] = Helper.Start(box, n);", "await Task.WhenAll(ts);")
    };

    /// <summary>The cell's start at its depth: by the creator, or below work the creator starts once or in a loop, which waits for its
    /// child as the creator waits for the work.</summary>
    /// <param name="cell">The cell.</param>
    private static string Started(Cell cell)
    {
        var (start, wait) = Form(cell.Kind, cell.Site);
        var inner = $"{start} {wait}";
        var lambda = inner.Contains("await", StringComparison.Ordinal) ? "async () =>" : "() =>";
        return cell.Chain switch
        {
            Chain.Creator => inner,
            Chain.OnceWork => $"var w = Task.Run({lambda} {{ {inner} }}); await w;",
            _ => $"var ws = new Task[n]; for (var k = 0; k < n; k++) ws[k] = Task.Run({lambda} {{ {inner} }}); await Task.WhenAll(ws);"
        };
    }

    /// <summary>A cell's code: an HTTP action creating the box, or for the twin a hosted worker that also stores it into a singleton.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="twin">Whether the once-running twin's code is asked for.</param>
    private static string Source(Cell cell, bool twin)
    {
        var helper = cell.Kind switch
        {
            StartKind.AsyncTail => $"public static async Task Work(Box box) {{ await Task.Delay(1); {WORK} }}",
            StartKind.Recursion => $"public static Task Start(Box box, int depth) => Task.Run(() => {{ {WORK} if (depth > 0) Start(box, depth - 1).Wait(); }});",
            _ => ""
        };
        var body = $$"""
                    var n = Environment.ProcessorCount;
                    var box = new Box();
                    {{(twin ? "keeper.Kept = box;" : "")}}
                    {{BEFORE}}
                    {{Started(cell)}}
                    {{AFTER}}
            """;
        var creator = twin
            ? $$"""
                public sealed class Worker(Keeper keeper) : BackgroundService
                {
                    protected override async Task ExecuteAsync(CancellationToken token)
                    {
                {{body}}
                    }
                }
                """
            : $$"""
                public class BoxController : ControllerBase
                {
                    public async Task Post()
                    {
                {{body}}
                    }
                }
                """;
        return $$"""
            public sealed class Box { public int N; }
            public sealed class Keeper { public Box? Kept; }
            public interface IConsumer { void Take(Action work); }
            public static class Sinks { public static IConsumer? Consumer; }
            public static class Helper
            {
                {{helper}}
            }
            {{creator}}
            """ + Startup(twin ? "services.AddSingleton<Keeper>(); services.AddHostedService<Worker>();" : "");
    }
}
