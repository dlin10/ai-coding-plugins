using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Which assumptions a pair is counted under when its ordering rests on reading the exceptional exit of a blocking wait for a
/// task as that task's completion (R5, D-0018), over every kind of waiting execution, source of the waited task and way the pair is
/// removed: the wait's own execution gives <c>thread-pool-entry</c> when it runs as thread-pool work and <c>thread-context</c> otherwise,
/// a waited task that takes the thread's scheduler adds <c>thread-context</c>, and <c>observers</c> is always there; a pair the normal
/// return alone removes rests on nothing. Each waiting execution is one program with one cell per waited task and removal.</summary>
public sealed class AssumptionAttributionMatrixTests
{
    public enum Waiter
    {
        TaskRun, QueueUserWorkItem, UnsafeQueueUserWorkItem, StartNewDefault, StartNewNone, ContinueWith, ParallelFor, ThreadingTimer,
        ControllerAction, MinimalApi, GrpcMethod, HostedExecute, Thread, HostedAsyncTail, TaskRunAsyncTail
    }

    public enum Waited { TaskRun, AsyncCall, StartNewNone }

    public enum Removal { Exceptional, NormalReturn }

    public sealed record Cell(Waiter Waiter, Waited Waited, Removal Removal)
    {
        public override string ToString() => $"{Waiter}/{Waited}/{Removal}";

        /// <summary>The class holding the cell's work, wait and write after it.</summary>
        public string Name => $"K_{Waited}_{Removal}";
    }

    /// <summary>The cells whose pair the analysis orders without the wait at all, so that it is ordered even when no exceptional exit
    /// orders anything and, as R5 counts, rests on no assumption, where the requirements expect the wait's assumptions; each by name with
    /// the reason. A name that names no cell expecting assumptions fails <see cref="Every_cell_ordered_without_the_wait_expects_assumptions"/>.</summary>
    private static readonly Dictionary<string, string> OrderedWithoutTheWait = new(StringComparer.Ordinal)
    {
        ["ParallelFor/TaskRun/Exceptional"] = PARALLEL_BODY,
        ["ParallelFor/AsyncCall/Exceptional"] = PARALLEL_BODY,
        ["ParallelFor/StartNewNone/Exceptional"] = PARALLEL_BODY
    };

    private const string PARALLEL_BODY =
        "In the run of a Parallel.For body as the creator of its box, a happens-before path orders the work's write before the body's later " +
        "write with no wait in the body at all, so the pair never needs the wait; outside R5, which counts only what the reading removes.";

    private static readonly ConcurrentDictionary<Waiter, Lazy<(EngineRun Run, IReadOnlyDictionary<string, int> Lines)>> Runs = new();

    public static IEnumerable<Cell> AllCells() =>
        from waiter in Enum.GetValues<Waiter>()
        from waited in Enum.GetValues<Waited>()
        from removal in Enum.GetValues<Removal>()
        select new Cell(waiter, waited, removal);

    public static TheoryData<Waiter, Waited, Removal> Cells()
    {
        var cells = new TheoryData<Waiter, Waited, Removal>();
        foreach (var cell in AllCells())
            cells.Add(cell.Waiter, cell.Waited, cell.Removal);
        return cells;
    }

    [Fact]
    public void There_are_90_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(90, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Waiter>(), waiter => Assert.Contains(cells, cell => cell.Waiter == waiter));
        Assert.All(Enum.GetValues<Waited>(), waited => Assert.Contains(cells, cell => cell.Waited == waited));
        Assert.All(Enum.GetValues<Removal>(), removal => Assert.Contains(cells, cell => cell.Removal == removal));
        Assert.Contains(cells, cell => Expected(cell).Count == 0);
        Assert.All(OrderingAssumptions.ALL, key => Assert.Contains(cells, cell => Expected(cell).Contains(key)));
        Assert.Contains(cells, cell => Expected(cell).SetEquals([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT,
                                                                 OrderingAssumptions.THREAD_POOL_ENTRY]));
    }

    [Fact]
    public void Every_cell_ordered_without_the_wait_expects_assumptions()
    {
        var cells = AllCells().ToDictionary(cell => cell.ToString(), StringComparer.Ordinal);

        Assert.All(OrderedWithoutTheWait, listed =>
        {
            Assert.True(cells.TryGetValue(listed.Key, out var cell), $"{listed.Key} names no cell.");
            Assert.NotEmpty(Expected(cell!));
            Assert.False(string.IsNullOrWhiteSpace(listed.Value));
        });
    }

    /// <summary>Every cell's pair is removed as ordered, and is counted under exactly the assumptions R5 gives; a cell inside
    /// <see cref="OrderedWithoutTheWait"/> is counted under none, as it is listed for.</summary>
    /// <param name="waiter">The execution making the wait.</param>
    /// <param name="waited">Where the waited task comes from.</param>
    /// <param name="removal">Whether the write after the wait follows its exceptional exit too or only its normal return.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Waiter waiter, Waited waited, Removal removal)
    {
        var cell = new Cell(waiter, waited, removal);
        var (timing, assumed) = Decided(cell);
        var expected = Expected(cell);

        Assert.True(timing == PairTiming.Ordered, $"{cell}: the pair is {timing}, not ordered.");
        if (OrderedWithoutTheWait.ContainsKey(cell.ToString()))
            Assert.True(assumed.Count == 0, $"{cell}: listed as ordered without the wait, but it rests on the wait now; take it off the list.");
        else
            Assert.True(expected.SetEquals(assumed), $"{cell}: counted under [{string.Join(", ", assumed.Order(StringComparer.Ordinal))}], " +
                                                     $"expected [{string.Join(", ", expected.Order(StringComparer.Ordinal))}].");
    }

    /// <summary>The assumptions R5 counts a cell's pair under.</summary>
    /// <param name="cell">The cell.</param>
    private static IReadOnlySet<string> Expected(Cell cell)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // R5: a pair the normal return removes would be removed if no exceptional exit ordered anything.
        if (cell.Removal == Removal.NormalReturn)
            return keys;
        keys.Add(OrderingAssumptions.OBSERVERS);
        // D-0018: a wait in thread-pool work rests on the pool's entry, any other on its thread's context.
        keys.Add(OnThreadPool(cell.Waiter) ? OrderingAssumptions.THREAD_POOL_ENTRY : OrderingAssumptions.THREAD_CONTEXT);
        // D-0018: a waited task that takes the scheduler of the thread starting it rests on that thread's context as well.
        if (cell.Waited == Waited.StartNewNone)
            keys.Add(OrderingAssumptions.THREAD_CONTEXT);
        return keys;
    }

    /// <summary>Whether a waiting execution runs as thread-pool work: the pool's own starts, a <c>StartNew</c> on
    /// <c>TaskScheduler.Default</c>, a <c>Parallel</c> body, a timer's callback, a request root, and an async tail in any of them.</summary>
    /// <param name="waiter">The waiting execution.</param>
    private static bool OnThreadPool(Waiter waiter) =>
        waiter is Waiter.TaskRun or Waiter.QueueUserWorkItem or Waiter.UnsafeQueueUserWorkItem or Waiter.StartNewDefault or Waiter.ParallelFor
                  or Waiter.ThreadingTimer or Waiter.ControllerAction or Waiter.MinimalApi or Waiter.GrpcMethod or Waiter.TaskRunAsyncTail;

    /// <summary>How the analysis decides the cell's pair of the work's write and the write after the wait, with the assumptions it
    /// counts it under; every access of the two lines must be decided alike.</summary>
    /// <param name="cell">The cell.</param>
    private static (PairTiming Timing, IReadOnlySet<string> Assumed) Decided(Cell cell)
    {
        var (run, lines) = Program(cell.Waiter);
        var work = Writes(run, lines[$"W:{cell.Name}"]);
        var after = Writes(run, lines[$"X:{cell.Name}"]);
        Assert.True(work.Length != 0, $"{cell}: no write in the work.");
        Assert.True(after.Length != 0, $"{cell}: no write after the wait.");
        var decisions = (from first in work
                         from second in after
                         select Decide(run, first, second)).ToArray();
        Assert.All(decisions, decision => Assert.True(decision.Timing == decisions[0].Timing && decision.Assumed.SetEquals(decisions[0].Assumed),
                                                      $"{cell}: the accesses of one line are decided apart."));
        return decisions[0];
    }

    private static (PairTiming Timing, IReadOnlySet<string> Assumed) Decide(EngineRun run, Access first, Access second)
    {
        var timing = run.Execution.Analysis.Timing(first, second, out var assumed);
        return (timing, assumed);
    }

    private static Access[] Writes(EngineRun run, int line) =>
        run.Accesses("Count").Where(access => access.Operation != AccessOperation.Read && access.Source.StartLine == line).ToArray();

    private static (EngineRun Run, IReadOnlyDictionary<string, int> Lines) Program(Waiter waiter) =>
        Runs.GetOrAdd(waiter, key => new Lazy<(EngineRun, IReadOnlyDictionary<string, int>)>(() => Build(key))).Value;

    // ---- the programs ----

    private static (EngineRun Run, IReadOnlyDictionary<string, int> Lines) Build(Waiter waiter)
    {
        var text = new StringBuilder("public sealed class Box\n{\n    public int Count;\n}\n\n");
        var registrations = new StringBuilder(waiter == Waiter.GrpcMethod ? "services.AddGrpc();\n" : "");
        if (waiter == Waiter.GrpcMethod)
            text.Append(GREETER);
        foreach (var cell in AllCells().Where(cell => cell.Waiter == waiter))
        {
            text.Append(Work(cell));
            var (code, registration) = Entry(cell);
            text.Append(code);
            registrations.Append(registration);
        }

        var source = text + Startup(registrations.ToString());
        return (Analyze(source), Markers(Usings + source));
    }

    /// <summary>The line of each <c>/*W:name*/</c> and <c>/*X:name*/</c> marker.</summary>
    /// <param name="text">The program.</param>
    private static Dictionary<string, int> Markers(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var split = text.Split('\n');
        for (var index = 0; index < split.Length; index++)
        {
            var start = split[index].IndexOf("/*", StringComparison.Ordinal);
            if (start >= 0 && split[index].IndexOf("*/", start, StringComparison.Ordinal) is var end and > 0 && split[index][start + 2] is 'W' or 'X' &&
                split[index][start + 3] == ':')
            {
                lines.Add(split[index][(start + 2)..end], index + 1);
            }
        }

        return lines;
    }

    /// <summary>The cell's work: a box made for this run, a task writing it, the wait — under a handler that catches its exception, or
    /// bare — and a write of the box after it; an async tail that runs the work for the waiters that need one.</summary>
    /// <param name="cell">The cell.</param>
    private static string Work(Cell cell)
    {
        var write = $"box.Count = 1; /*W:{cell.Name}*/";
        var task = cell.Waited switch
        {
            Waited.TaskRun => $"Task.Run(() =>\n{{\n{write}\n}})",
            Waited.AsyncCall => "WorkAsync(box)",
            _ => $"Task.Factory.StartNew(() =>\n{{\n{write}\n}})"
        };
        var wait = cell.Removal == Removal.Exceptional ? "try\n{\nt.Wait();\n}\ncatch (AggregateException)\n{\n}" : "t.Wait();";
        // The write stands after the first await, so it is the async call's tail that makes it and not the caller.
        var asyncWork = cell.Waited == Waited.AsyncCall ? $"private static async Task WorkAsync(Box box)\n{{\nawait Task.Yield();\n{write}\n}}\n" : "";
        return $$"""
            public static class {{cell.Name}}
            {
                public static void Body()
                {
                    var box = new Box();
                    var t = {{task}};
                    {{wait}}
                    box.Count = 2; /*X:{{cell.Name}}*/
                }
                public static async Task TailAsync()
                {
                    await Task.Yield();
                    Body();
                }
            {{asyncWork}}
            }


            """;
    }

    /// <summary>Where the cell's work runs, and the registration that makes it run: a hosted service starting the waiting execution, a
    /// controller action, a minimal API endpoint or a gRPC method.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Code, string Registration) Entry(Cell cell)
    {
        var body = $"{cell.Name}.Body()";
        var service = $"H_{cell.Name}";
        string Hosted(string start) => $$"""
            public sealed class {{service}} : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{start}}
                    return Task.CompletedTask;
                }
            }


            """;
        var hosted = $"services.AddHostedService<{service}>();\n";
        return cell.Waiter switch
        {
            Waiter.TaskRun => (Hosted($"Task.Run(() => {body});"), hosted),
            Waiter.QueueUserWorkItem => (Hosted($"ThreadPool.QueueUserWorkItem(_ => {body});"), hosted),
            Waiter.UnsafeQueueUserWorkItem => (Hosted($"ThreadPool.UnsafeQueueUserWorkItem(_ => {body}, null);"), hosted),
            Waiter.StartNewDefault => (Hosted($"Task.Factory.StartNew(() => {body}, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);"), hosted),
            Waiter.StartNewNone => (Hosted($"Task.Factory.StartNew(() => {body});"), hosted),
            Waiter.ContinueWith => (Hosted($"Task.Run(() => {{ }}).ContinueWith(_ => {body});"), hosted),
            Waiter.ParallelFor => (Hosted($"Parallel.For(0, 2, _ => {body});"), hosted),
            Waiter.ThreadingTimer => (Hosted($"_ = new Timer(_ => {body}, null, 0, Timeout.Infinite);"), hosted),
            Waiter.HostedExecute => (Hosted($"{body};"), hosted),
            Waiter.Thread => (Hosted($"new Thread(() => {body}).Start();"), hosted),
            Waiter.HostedAsyncTail => (Hosted($"_ = {cell.Name}.TailAsync();"), hosted),
            Waiter.TaskRunAsyncTail => (Hosted($"Task.Run(() => {{ _ = {cell.Name}.TailAsync(); }});"), hosted),
            Waiter.ControllerAction => ($$"""
                public sealed class C_{{cell.Name}}Controller : ControllerBase
                {
                    public void Get() => {{body}};
                }


                """, ""),
            Waiter.MinimalApi => ("", $"app.MapGet(\"/{cell.Name}\", () => {body});\n"),
            Waiter.GrpcMethod => ($$"""
                public sealed class G_{{cell.Name}} : Greeter.GreeterBase
                {
                    public override Task<Reply> SayHello(Request request, Grpc.Core.ServerCallContext context)
                    {
                        {{body}};
                        return Task.FromResult(new Reply());
                    }
                }


                """, $"app.MapGrpcService<G_{cell.Name}>();\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    /// <summary>The generated base of a gRPC service whose methods the gRPC roots override.</summary>
    private const string GREETER = """
        public sealed class Request { }
        public sealed class Reply { }

        public static partial class Greeter
        {
            [Grpc.Core.BindServiceMethod(typeof(Greeter), "BindService")]
            public abstract partial class GreeterBase
            {
                public virtual Task<Reply> SayHello(Request request, Grpc.Core.ServerCallContext context) => throw new NotSupportedException();
            }
        }


        """;

    // ---- outside the axes ----

    /// <summary>Which of a proof's alternative waits a case keeps: both, the child's own wait alone, a second wait on the same
    /// assumptions, or a continuation's wait alone.</summary>
    public enum Alternatives { Both, Child, SameKeys, Continuation }

    /// <summary>An exceptional wait the ordering reads as completion, beside an <c>await</c> of the same task on every path to the write:
    /// the await orders the pair whatever the context, so the pair rests on no assumption and is not counted, though it is ordered.</summary>
    [Fact]
    public void Pair_with_an_assumption_free_path_is_not_counted()
    {
        var (run, timing, assumed) = Decide("""
            var t = Task.Run(() => { box.Count = 1; /*W:a*/ });
            try { t.Wait(); } catch (AggregateException) { }
            await t;
            box.Count = 2; /*X:a*/
            """);

        Assert.Equal(PairTiming.Ordered, timing);
        Assert.Empty(assumed);
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_ORDERED) > 0);
        Assert.Empty(run.Pairs.Assumed);
    }

    /// <summary>A pair ordered only through two exceptional waits — one in thread-pool work for a task on the thread's scheduler, one in
    /// the hosted service for that work — rests on both, so it is counted once under the union of their assumptions.</summary>
    [Fact]
    public void Pair_that_needs_two_waits_counts_once_under_the_union_of_their_keys()
    {
        var (run, timing, assumed) = Decide("""
            var outer = Task.Run(() =>
            {
                var inner = Task.Factory.StartNew(() => { box.Count = 1; /*W:a*/ });
                try { inner.Wait(); } catch (AggregateException) { }
            });
            try { outer.Wait(); } catch (AggregateException) { }
            box.Count = 2; /*X:a*/
            """);

        Assert.Equal(PairTiming.Ordered, timing);
        Assert.Equal([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT, OrderingAssumptions.THREAD_POOL_ENTRY],
                     assumed.Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, int>
                     {
                         [OrderingAssumptions.OBSERVERS] = 1, [OrderingAssumptions.THREAD_CONTEXT] = 1, [OrderingAssumptions.THREAD_POOL_ENTRY] = 1
                     },
                     run.Pairs.Assumed.ToDictionary());
    }

    /// <summary>Two exceptional waits that rest on the same assumptions — one in work on the thread's scheduler, one in the hosted service
    /// for that work — order a pair only together: the pair is counted once under each of those assumptions, and the reference pairing
    /// counts it the same.</summary>
    [Fact]
    public void Two_uses_of_one_assumption_count_one_pair()
    {
        var (run, timing, assumed) = Decide("""
            var outer = Task.Factory.StartNew(() =>
            {
                var inner = Task.Run(() => { box.Count = 1; /*W:a*/ });
                try { inner.Wait(); } catch (AggregateException) { }
            });
            try { outer.Wait(); } catch (AggregateException) { }
            box.Count = 2; /*X:a*/
            """);

        Assert.Equal(PairTiming.Ordered, timing);
        Assert.Equal([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT], assumed.Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, int> { [OrderingAssumptions.OBSERVERS] = 1, [OrderingAssumptions.THREAD_CONTEXT] = 1 },
                     run.Pairs.Assumed.ToDictionary());
        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);
        Assert.Equal(reference.Assumed, run.Pairs.Assumed);
        Assert.Equal(reference.Skips, run.Pairs.Skips);
    }

    /// <summary>A pair ordered through the hosted service's exceptional wait for thread-pool work, which every proof needs, and through
    /// either of the work's exceptional waits for its child — a <c>Wait()</c> of the child, or a <c>Task.WaitAll</c> of the child and a
    /// task on the thread's scheduler — rests on every assumption each of those proofs rests on: thread-pool entry included, with both
    /// waits as with the child's own alone, and with alternatives on the same assumptions. It is counted once under each, as the
    /// reference pairing counts it.</summary>
    /// <param name="alternatives">The work's waits for its child.</param>
    [Theory]
    [InlineData(Alternatives.Both)]
    [InlineData(Alternatives.Child)]
    [InlineData(Alternatives.SameKeys)]
    public void Pair_that_needs_one_wait_and_either_of_two_counts_under_every_key_its_proofs_rest_on(Alternatives alternatives)
    {
        const string CHILD = "try { inner.Wait(); } catch (AggregateException) { }";
        var waits = alternatives switch
        {
            Alternatives.Both => CHILD + " try { Task.WaitAll(inner, extra); } catch (AggregateException) { }",
            Alternatives.Child => CHILD,
            Alternatives.SameKeys => CHILD + " try { _ = inner.Result; } catch (AggregateException) { }",
            _ => throw new ArgumentOutOfRangeException(nameof(alternatives), alternatives, null)
        };
        AssertEveryKey($$"""
            var outer = Task.Run(() =>
            {
                var extra = Task.Factory.StartNew(() => { });
                var inner = Task.Run(() => { box.Count = 1; /*W:a*/ return 1; });
                {{waits}}
            });
            try { outer.Wait(); } catch (AggregateException) { }
            box.Count = 2; /*X:a*/
            """);
    }

    /// <summary>The hosted service's exceptional wait for thread-pool work is needed, and the work orders its child before its own end
    /// through either of two exceptional waits on different assumptions: the child's <c>Wait()</c>, or the <c>Wait()</c> of a
    /// continuation of the child, which takes the thread's scheduler. The needed wait alone does not order the pair, so each alternative
    /// that completes the proof with it counts too, thread-pool entry included; with one route alone the pair rests on that route.</summary>
    /// <param name="alternatives">The routes the work orders its child through.</param>
    [Theory]
    [InlineData(Alternatives.Both)]
    [InlineData(Alternatives.Child)]
    [InlineData(Alternatives.Continuation)]
    public void Pair_that_needs_one_wait_and_either_of_two_routes_counts_under_every_key(Alternatives alternatives)
    {
        const string CHILD = "try { inner.Wait(); } catch (AggregateException) { }";
        const string CONTINUATION = "var cont = inner.ContinueWith(_ => { }); try { cont.Wait(); } catch (AggregateException) { }";
        var waits = alternatives switch
        {
            Alternatives.Both => CHILD + " " + CONTINUATION,
            Alternatives.Child => CHILD,
            Alternatives.Continuation => CONTINUATION,
            _ => throw new ArgumentOutOfRangeException(nameof(alternatives), alternatives, null)
        };
        AssertEveryKey($$"""
            var outer = Task.Run(() =>
            {
                var inner = Task.Run(() => { box.Count = 1; /*W:a*/ return 1; });
                {{waits}}
            });
            try { outer.Wait(); } catch (AggregateException) { }
            box.Count = 2; /*X:a*/
            """);
    }

    /// <summary>Asserts that the marked pair of a hosted service's body is ordered and counted once under each of the three assumptions,
    /// as the reference pairing counts it.</summary>
    /// <param name="body">The statements of the service's <c>ExecuteAsync</c>, after the box is made.</param>
    private static void AssertEveryKey(string body)
    {
        var (run, timing, assumed) = Decide(body);

        Assert.Equal(PairTiming.Ordered, timing);
        Assert.Equal([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT, OrderingAssumptions.THREAD_POOL_ENTRY],
                     assumed.Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, int>
                     {
                         [OrderingAssumptions.OBSERVERS] = 1, [OrderingAssumptions.THREAD_CONTEXT] = 1, [OrderingAssumptions.THREAD_POOL_ENTRY] = 1
                     },
                     run.Pairs.Assumed.ToDictionary());
        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);
        Assert.Equal(reference.Assumed, run.Pairs.Assumed);
    }

    /// <summary>Analyses a hosted service running a body and decides the pair of its writes marked <c>/*W:a*/</c> and <c>/*X:a*/</c>.</summary>
    /// <param name="body">The statements of the service's <c>ExecuteAsync</c>, after the box is made.</param>
    private static (EngineRun Run, PairTiming Timing, IReadOnlySet<string> Assumed) Decide(string body)
    {
        var source = Hosted(body);
        var run = Analyze(source);
        var lines = Markers(Usings + source);
        var first = Assert.Single(Writes(run, lines["W:a"]));
        var second = Assert.Single(Writes(run, lines["X:a"]));
        var (timing, assumed) = Decide(run, first, second);
        return (run, timing, assumed);
    }

    private static string Hosted(string body) => Startup("services.AddHostedService<Worker>();") + $$"""

        public sealed class Box
        {
            public int Count;
        }

        public sealed class Worker : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var box = new Box();
                {{body}}
                await Task.CompletedTask;
            }
        }
        """;
}
