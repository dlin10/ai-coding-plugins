using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class HandleExecutionMatrixTests
{
    private enum Start { TaskRun, StartNew, Thread, Timer, AsyncCall, Forwarder }
    private enum Stretch { Same, AwaitBetween, InnerLoop, CaughtRetry, Field, Parameter, CalleeReturn }
    private enum Wait { Await, Block }
    private enum Answer { Join, Source }

    private sealed record Cell(Start Start, Stretch Stretch, Wait Wait)
    {
        public override string ToString() => $"{Start}/{Stretch}/{Wait}";
    }

    private static readonly Dictionary<Cell, string> Wider = [];

    private static IEnumerable<Cell> AllCells() =>
        from start in Enum.GetValues<Start>()
        from stretch in Enum.GetValues<Stretch>()
        from wait in Enum.GetValues<Wait>()
        let cell = new Cell(start, stretch, wait)
        where Allowed(cell)
        select cell;

    [Fact]
    public void There_are_66_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(66, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Start>(), value => Assert.Contains(cells, cell => cell.Start == value));
        Assert.All(Enum.GetValues<Stretch>(), value => Assert.Contains(cells, cell => cell.Stretch == value));
        Assert.All(Enum.GetValues<Wait>(), value => Assert.Contains(cells, cell => cell.Wait == value));
        Assert.Contains(cells, cell => Expected(cell) == Answer.Join);
        Assert.Contains(cells, cell => Expected(cell) == Answer.Source);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_a_join()
    {
        var cells = AllCells().ToHashSet();
        Assert.Empty(Wider);
        Assert.All(Wider, item =>
        {
            Assert.Contains(item.Key, cells);
            Assert.Equal(Answer.Join, Expected(item.Key));
            Assert.False(string.IsNullOrWhiteSpace(item.Value));
        });
    }

    [Fact]
    public void Every_cell_is_exact_outside_the_wider_list()
    {
        Assert.All(AllCells(), cell =>
        {
            var observed = Observe(cell);
            if (Wider.ContainsKey(cell))
                Assert.Equal(Answer.Source, observed);
            else
                Assert.Equal(Expected(cell), observed);
        });
    }

    private static bool Allowed(Cell cell) =>
        !(cell.Start == Start.Thread && cell.Wait == Wait.Await) &&
        !(cell.Start == Start.Timer && cell.Wait == Wait.Block) &&
        !(cell.Start is Start.AsyncCall or Start.Forwarder && cell.Stretch == Stretch.CalleeReturn);

    private static Answer Expected(Cell cell) => cell.Stretch switch
    {
        Stretch.Same => Answer.Join, // R1: this execution's direct start and wait lie in one stretch.
        Stretch.InnerLoop or Stretch.CaughtRetry when cell.Wait == Wait.Block => Answer.Join, // R1(b): blocking does not suspend.
        Stretch.InnerLoop or Stretch.CaughtRetry => Answer.Source, // R1(b): the earlier visit of this await suspends.
        Stretch.AwaitBetween => Answer.Source, // R1(b): another await separates the start and wait.
        Stretch.Field or Stretch.Parameter or Stretch.CalleeReturn => Answer.Source, // R1(a): the value is not the direct start.
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };

    private static string Source(Cell cell)
    {
        var type = cell.Start switch { Start.Thread => "Thread", Start.Timer => "Timer", _ => "Task" };
        var start = cell.Start switch
        {
            Start.TaskRun => "Task.Run(Write)",
            Start.StartNew => "Task.Factory.StartNew(Write)",
            Start.Thread => "new Thread(Write)",
            Start.Timer => "new Timer(_ => Write(), null, 0, Timeout.Infinite)",
            Start.AsyncCall => "WriteAsync()",
            Start.Forwarder => "Fwd()",
            _ => throw new ArgumentOutOfRangeException(nameof(cell))
        };
        var handle = cell.Stretch == Stretch.Field ? "_h" : "h";
        var create = $"{(cell.Stretch == Stretch.Field ? "" : "var ")}{handle} = {start};" +
                     (cell.Start == Start.Thread ? $" {handle}.Start();" : "");
        var wait = cell.Wait == Wait.Await
            ? cell.Start == Start.Timer ? $"await {handle}.DisposeAsync();" : $"await {handle};"
            : cell.Start == Start.Thread ? $"{handle}.Join();" : $"{handle}.Wait();";
        var body = cell.Stretch switch
        {
            Stretch.Same or Stretch.Field => $"{create} {wait}",
            Stretch.AwaitBetween => $"{create} await Task.Yield(); {wait}",
            Stretch.InnerLoop => $"{create} do {{ {wait} }} while (Again());",
            Stretch.CaughtRetry => $"{create} while (true) {{ try {{ {wait} break; }} catch (InvalidOperationException) {{ }} }}",
            Stretch.Parameter => $"{create} await WaitFor(h);",
            Stretch.CalleeReturn => $"var h = Make(); {wait}",
            _ => throw new ArgumentOutOfRangeException(nameof(cell))
        };
        var helper = cell.Stretch switch
        {
            Stretch.Parameter => $"private async Task WaitFor({type} h) {{ {wait} }}",
            Stretch.CalleeReturn when cell.Start == Start.Thread => $"private Thread Make() {{ {create} return h; }}",
            Stretch.CalleeReturn => $"private {type} Make() => {start};",
            _ => ""
        };
        return Startup("services.AddHostedService<Worker>();") + $$"""

            public sealed class Worker : BackgroundService
            {
                public int Value;
                {{(cell.Stretch == Stretch.Field ? $"private {type} _h = null!;" : "")}}
                protected override Task ExecuteAsync(CancellationToken token) => LoopAsync();
                private async Task LoopAsync()
                {
                    for (var pass = 0; pass < 3; pass++)
                    {
                        {{body}}
                        await Task.Yield();
                    }
                }
                private void Write() { Value = 1; }
                private bool Again() => Environment.TickCount % 2 == 0;
                {{(cell.Start is Start.AsyncCall or Start.Forwarder ? "private async Task WriteAsync() { await Task.Yield(); Write(); }" : "")}}
                {{(cell.Start == Start.Forwarder ? "private Task Fwd() => WriteAsync();" : "")}}
                {{helper}}
            }
            """;
    }

    private static Answer Observe(Cell cell)
    {
        var run = Analyze(Source(cell));
        var accesses = run.Of("Value");
        Assert.Equal(2, accesses.Count);
        var analysis = run.Execution.Analysis;
        var work = accesses.Select(access => analysis.Execution(access.ExecutionId)).ToArray();
        Assert.Single(work.Select(execution => execution.TreeRootId).Distinct());
        Assert.Equal(2, work.Select(execution => execution.ParentId).Distinct().Count());
        var continued = Assert.Single(accesses, access =>
            analysis.Execution(analysis.Execution(access.ExecutionId).ParentId!).Origin?.Api == "async-call");
        var origin = Assert.IsType<SpawnOrigin>(analysis.Execution(continued.ExecutionId).Origin);
        Assert.All(work, execution => Assert.Equal(origin, execution.Origin));
        var heap = run.Execution.Heap.Heap;
        var instance = Assert.Single(heap.Instances.Values, instance =>
            cell.Stretch == Stretch.Parameter ? instance.BodyId.Contains("M:Worker.WaitFor(", StringComparison.Ordinal)
                                                : instance.BodyId.EndsWith(":M:Worker.LoopAsync", StringComparison.Ordinal));
        int joinOperation;
        int rejectedOperation;
        if (cell.Start == Start.Timer)
        {
            var timer = Assert.Single(heap.TimerCallbacks);
            Assert.Single(timer.Timers);
            var dispose = Assert.Single(instance.Summary.Timers, timer => timer.Action == IrTimerAction.DisposeAsync);
            // Accepted timer joins name the await; a source rejection names its DisposeAsync operation.
            var span = dispose.Provenance.Span;
            var join = Assert.Single(instance.Summary.Joins, join =>
                join.Provenance.Span.StartLine == span.StartLine && join.Provenance.Span.EndLine == span.EndLine &&
                join.Provenance.Span.StartColumn <= span.StartColumn && join.Provenance.Span.EndColumn >= span.EndColumn);
            joinOperation = join.OperationId;
            rejectedOperation = dispose.OperationId;
        }
        else
        {
            var handles = cell.Start is Start.AsyncCall or Start.Forwarder
                ? new[] { Assert.Single(heap.AsyncSpawns, spawn => spawn.OperationId == origin.OperationId &&
                    heap.Instances[spawn.CallerInstance].BodyId == origin.BodyId).Handle! }
                : Assert.Single(heap.Spawns).Handles.ToArray();
            Assert.Single(handles);
            var join = Assert.Single(instance.Summary.Joins, join => join.Handles.Any(handle =>
                handle.Values.SelectMany(value => heap.Resolve(instance.Id, value)).Intersect(handles).Any()));
            joinOperation = rejectedOperation = join.OperationId;
        }
        var proofs = new[]
        {
            analysis.OrderProof(accesses[0], accesses[0]),
            analysis.OrderProof(accesses[1], accesses[1]),
            analysis.OrderProof(accesses[0], accesses[1])
        };
        // A timer self-pair can stop before TimerJoins and emit a placeholder without a start.
        var frames = proofs.SelectMany(proof => proof.Frames)
                           .Where(frame => frame.Execution == continued.ExecutionId && frame.Anchor is not null).ToArray();
        Assert.NotEmpty(frames);
        var joined = frames.Select(frame => frame.Joins.Any(item => item.Instance == instance.Id && item.Operation == joinOperation)).ToArray();
        if (joined.Any(value => value))
        {
            Assert.All(joined, value => Assert.True(value, $"{cell}: the continuation's frames disagree about the join."));
            return Answer.Join;
        }
        Assert.Contains(frames.SelectMany(frame => frame.Rejected), item =>
            item.Instance == instance.Id && item.Operation == rejectedOperation && item.Reason == "source");
        return Answer.Source;
    }
}
