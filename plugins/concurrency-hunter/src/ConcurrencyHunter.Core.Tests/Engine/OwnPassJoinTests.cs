using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Work a pass of its start waits for on a handle of its own is ordered before the next pass of that start, in a parent that
/// does not overlap itself; without that wait, or in a parent that does, the work of two passes still meets.</summary>
public sealed class OwnPassJoinTests
{
    [Fact]
    public void Hosted_loop_awaiting_a_helper_that_awaits_its_own_task_run_has_no_pair()
    {
        var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) await WritePass();",
                                 "private async Task WritePass() { await Task.Run(Write); }"));

        Assert.Empty(run.PairsOn("Value"));
        AssertPassCompletion(run);
    }

    [Fact]
    public void Two_calls_of_a_seed_that_waits_for_its_async_work_have_no_pair()
    {
        var run = Analyze(Worker("Seed(); Seed(); await Task.CompletedTask;", SEED));

        Assert.Empty(run.PairsOn("Value"));
        AssertPassCompletion(run);
    }

    /// <summary>Requests of an HTTP action overlap one another, so the action's two calls of the seed are no passes one after another.</summary>
    [Fact]
    public void Two_calls_of_that_seed_in_an_http_action_keep_the_pair()
    {
        var run = Analyze(Startup("services.AddSingleton<Seeder>();") + $$"""

            public sealed class Seeder
            {
                public static int Value;
                private void Write() { Value = 1; }
                {{SEED}}
                public void Run() { Seed(); Seed(); }
            }
            public sealed class SeedController(Seeder seeder) : ControllerBase
            {
                public void Post() => seeder.Run();
            }
            """);

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    [Fact]
    public void Loop_starting_task_run_without_waiting_keeps_the_self_pair()
    {
        var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) { _ = Task.Run(Write); } await Task.CompletedTask;"));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    [Fact]
    public void Own_pass_helper_loop_nested_retry()
    {
        foreach (var body in new[]
        {
            "await Drain(); await Drain();",
            "for (var i = 0; i < 2; i++) await Drain();",
            "for (var i = 0; i < 2; i++) for (var attempt = 0; attempt < 2; attempt++) { try { await Drain(); break; } catch (Exception) { } }"
        })
        {
            var run = Analyze(Worker(body, "private async Task Drain() { var own = Task.Run(() => Value = 1); await own; }"));
            var work = run.Of("Value").Select(access => run.Execution.Analysis.Execution(access.ExecutionId))
                          .Where(execution => execution.Origin?.Api == "Task.Run").ToArray();
            Assert.NotEmpty(work);
            Assert.All(work, execution =>
            {
                Assert.Equal(Multiplicity.Repeated, execution.Policy.Multiplicity);
                Assert.False(run.Execution.Analysis.RunsOncePerRunOf(execution.Id, execution.TreeRootId));
            });
            Assert.True(run.PairsOn("Value").Count == 0, $"Each pass awaits its own work: {body}");
        }
    }

    /// <summary>The pass waits for the task it started, which does not wait for the task it starts in turn.</summary>
    [Fact]
    public void Detached_descendant()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) await Drain();",
                                 "private async Task Drain() { var own = Task.Run(() => { Task.Run(() => Value = 1); }); await own; }"));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    /// <summary>An awaited task faults only after it completes, so a handler that leads to the next pass still follows the work.</summary>
    [Fact]
    public void Await_completed_fault()
    {
        var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) { try { await Drain(); } catch (Exception) { } }",
            "private async Task Drain() { var own = Task.Run((Action)(() => { Value = 1; throw new Exception(); })); await own; }"));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Empty(run.PairsOn("Value"));
    }

    /// <summary>A retry loop catching around the wait for its own pass's work: the wait faults only after the work completed (R3), so the
    /// handler that leads to the next attempt still follows the work.</summary>
    [Fact]
    public void Retry_loop_catching_around_wait_has_no_pair()
    {
        var run = Analyze(Worker(RETRY));

        Assert.Empty(run.PairsOn("Value"));
        AssertPassCompletion(run);
    }

    /// <summary>A reachable call installing a synchronization context lets the wait fault before the work completes (R4), so the next
    /// attempt may start the work again while it still runs.</summary>
    [Fact]
    public void Retry_loop_catching_around_wait_keeps_the_pair_where_a_context_is_installed()
    {
        var run = Analyze(Worker("SynchronizationContext.SetSynchronizationContext(new SynchronizationContext()); " + RETRY));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    [Fact]
    public void Overlapping_parent()
    {
        var run = Analyze(Startup("services.AddSingleton<Board>();") + """

            public sealed class Board { public int Value; }
            public sealed class BoardController(Board board) : ControllerBase
            {
                public async Task Post()
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var own = Task.Run(() => board.Value = 1);
                        await own;
                    }
                }
            }
            """);

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    /// <summary>The tail of one awaited async call ends before the next call's prefix.</summary>
    [Fact]
    public void Previous_tail_before_next_prefix()
    {
        var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) await Step();", """
            private async Task Step() { Prefix(); await Task.Yield(); Tail(); }
            private void Prefix() { Value = 1; }
            private void Tail() { Value = 2; }
            """));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Empty(run.PairsOn("Value"));
    }

    /// <summary>A plain helper that returns the task it starts, directly or through other helpers, returns this pass's handle; one that
    /// returns another task, or one of two, does not.</summary>
    [Fact]
    public void Non_async_returned_task()
    {
        foreach (var members in new[]
        {
            "private Task Start() => Task.Run(() => Value = 1);",
            "private Task Start() => Forward(Create()); private Task Create() => Task.Run(() => Value = 1); private Task Forward(Task task) => task;"
        })
        {
            var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) await Start();", members));
            Assert.NotEmpty(run.Of("Value"));
            Assert.True(run.PairsOn("Value").Count == 0, "Every returned handle belongs to the current helper invocation.");
        }
        foreach (var members in new[]
        {
            "private Task Start() { var own = Task.Run(() => Value = 1); return Forward(own); } private Task Forward(Task task) => Task.CompletedTask;",
            "private Task Start() { var own = Task.Run(() => Value = 1); return Forward(own); } private Task Forward(Task task) => DateTime.UtcNow.Ticks == 0 ? task : Task.CompletedTask;"
        })
        {
            var run = Analyze(Worker("for (var pass = 0; pass < 2; pass++) await Start();", members));
            Assert.True(run.PairsOn("Value").Any(pair => pair.First.ExecutionId == pair.Second.ExecutionId),
                "A foreign or mixed returned handle does not complete the current work.");
        }
    }

    /// <summary>A root invoked again and again, one invocation at a time, that waits for its work on every exit leaves no work running
    /// into its next invocation.</summary>
    [Fact]
    public void Repeated_serialized_parent_that_completes_the_work_on_every_exit_has_no_pair()
    {
        var run = WithRepeatedSerializedRoot(Worker("var own = Task.Run(Write); await own;"));

        Assert.Empty(run.PairsOn("Value"));
        AssertPassCompletion(run);
    }

    /// <summary>An exception before the wait leaves that root invocation with its work still running, and the next invocation starts the
    /// work again.</summary>
    [Fact]
    public void Repeated_serialized_parent_that_leaves_by_exception_before_completion_keeps_the_pair()
    {
        var run = WithRepeatedSerializedRoot(Worker("var own = Task.Run(Write); MayThrow(); await own;",
                                                    "private static bool Flag; private static void MayThrow() { if (Flag) throw new InvalidOperationException(); }"));

        Assert.NotEmpty(run.Of("Value"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId &&
            run.Execution.Analysis.Execution(pair.First.ExecutionId).OverlapsItself);
    }

    /// <summary>A root invoked again and again, one invocation at a time, that waits for a task starting a child: the task's exception
    /// before it joins the child faults the root's wait, whose own exception leaves the invocation after the task completed but with the
    /// child still running, so the next invocation's child overlaps it (R2). Without that exception the root waited for the child on
    /// every exit (R1).</summary>
    /// <param name="leaves">Whether the task may throw before it joins the child.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repeated_serialized_parent_waiting_a_task_that_may_leave_before_its_child_join(bool leaves)
    {
        var before = leaves ? "MayThrow();" : "";
        var run = WithRepeatedSerializedRoot(Worker($"var outer = Task.Run(() => {{ var inner = Task.Run(Write); {before} inner.Wait(); }}); outer.Wait(); await Task.CompletedTask;",
                                                    "private static bool Flag; private static void MayThrow() { if (Flag) throw new InvalidOperationException(); }"));

        Assert.NotEmpty(run.Of("Value"));
        if (leaves)
        {
            Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId &&
                run.Execution.Analysis.Execution(pair.First.ExecutionId).OverlapsItself);
        }
        else
        {
            Assert.Empty(run.PairsOn("Value"));
            AssertPassCompletion(run);
        }
    }

    public enum Kind
    {
        ThreadJoin, ValueTaskResult, ValueTaskGetResult, ValueTaskOfTGetResult, ConfiguredValueTaskGetResult, ConfiguredValueTaskOfTGetResult,
        TimerWaitOne, WaitTimeout, WaitToken, InstallerReachable, ProgramScheduler, Wait, Result, GetResult, WaitAll, WhenAllWait, Await
    }

    public enum At
    {
        Direct, Helper, HelperOfHelper, Starting, StartingOfHelper, Nested, Uncaught, UncaughtHelper, UncaughtStarting, SerializedRoot,
        SerializedRootHelper, SerializedRootStarting
    }

    private static readonly ConcurrentDictionary<(Kind Kind, bool Serialized), Lazy<EngineRun>> Sweep = new();

    /// <summary>An exceptional exit out of a wait has waited for that wait's own work only where the wait's exception comes after its
    /// tasks completed: a blocking wait for a task read so under the environment assumptions, and an await (R3). Every other wait — a
    /// <c>Thread.Join</c>, the <c>Result</c> or <c>GetResult()</c> of a <c>ValueTask</c>, configured or not, the <c>WaitOne()</c> after a
    /// timer's <c>Dispose(handle)</c>, a task wait with a timeout or a token, a task wait under a reachable installer or on the program's
    /// scheduler — may throw while the work runs (R2, R4), so a pass whose exception reaches the next pass or the access after the catch —
    /// through a handler in the same body, out of one or two helpers to the caller's, out of a task the parent waits, or out of a run of a
    /// repeated serialized root — keeps both pairs. Where the exception leaves an execution that runs once, the normal return orders the
    /// work as before, which shows each wait's handle proven. Under every placement the write of the next pass and the write after the
    /// wait are asked.</summary>
    /// <param name="kind">The wait.</param>
    /// <param name="at">Where the wait stands and where its exception goes.</param>
    [Theory]
    [MemberData(nameof(SweepCases))]
    public void A_wait_that_may_throw_before_its_work_completes_keeps_the_pairs_its_exception_reaches(Kind kind, At at)
    {
        var serialized = Serialized(at);
        var run = Sweep.GetOrAdd((kind, serialized), key => new Lazy<EngineRun>(() => SweepProgram(key.Kind, key.Serialized))).Value;
        var field = $"V_{at}";
        Assert.Contains(run.Of(field), access => access.Symbol.EndsWith(".Write()", StringComparison.Ordinal));

        bool[] actual = [SweepPairs(run, field, "Write"), SweepPairs(run, field, "After")];
        Assert.Equal(SweepExpected(kind, at), actual);
    }

    public static TheoryData<Kind, At> SweepCases()
    {
        var cases = new TheoryData<Kind, At>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var at in Enum.GetValues<At>())
                cases.Add(kind, at);
        }

        return cases;
    }

    /// <summary>Whether the next pass's work and the access after the wait pair with the work in a cell of the sweep.</summary>
    /// <param name="kind">The wait.</param>
    /// <param name="at">Where the wait stands.</param>
    private static bool[] SweepExpected(Kind kind, At at)
    {
        var read = kind is Kind.Wait or Kind.Result or Kind.GetResult or Kind.WaitAll or Kind.WhenAllWait or Kind.Await;
        var exceptional = at is not (At.Uncaught or At.UncaughtHelper or At.UncaughtStarting);
        // R2: a wait with a timeout or a token may return before its work completes.
        if (kind is Kind.WaitTimeout or Kind.WaitToken)
            return [true, true];
        // R2, R4: the exception of any other wait than one read under R3 may come while the work runs.
        if (!read && exceptional)
            return [true, true];
        // A WhenAll group's task is no pass's own handle, so neither a pass nor the access after its wait is ordered by it.
        if (kind == Kind.WhenAllWait)
            return [true, true];
        // The callbacks of a timer meet one another whatever waits for it; the wait in the same body orders the access after it, and one
        // in a helper is no wait of the caller.
        if (kind == Kind.TimerWaitOne)
            return [true, at == At.UncaughtHelper];
        return [false, false];
    }

    /// <summary>Whether the work of a cell writes in pair with the method given: <c>Write</c> for the next pass, <c>After</c> for the
    /// access after the wait.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="field">The cell's field.</param>
    /// <param name="other">The other writing method.</param>
    private static bool SweepPairs(EngineRun run, string field, string other) =>
        run.PairsOn(field).Any(pair => Is(pair.First, "Write") && Is(pair.Second, other) || Is(pair.First, other) && Is(pair.Second, "Write"));

    private static bool Serialized(At at) => at is At.SerializedRoot or At.SerializedRootHelper or At.SerializedRootStarting;

    private static bool Is(Access access, string method) =>access.Symbol.EndsWith($".{method}()", StringComparison.Ordinal);

    /// <summary>The program of one wait kind: a hosted service per placement, each writing its own field, under the default policy for
    /// the placements in a loop and with every root invoked again and again, one invocation at a time, for the root placements.</summary>
    /// <param name="kind">The wait.</param>
    /// <param name="serialized">Whether the program holds the root placements.</param>
    private static EngineRun SweepProgram(Kind kind, bool serialized)
    {
        var placements = Enum.GetValues<At>().Where(at => Serialized(at) == serialized).ToArray();
        var source = new StringBuilder(kind == Kind.ProgramScheduler ? SCHEDULER : "");
        foreach (var at in placements)
            source.Append(SweepService(kind, at));
        var program = source + Startup(string.Concat(placements.Select(at => $"services.AddHostedService<P_{at}>();\n")));
        return serialized ? WithRepeatedSerializedRoot(program) : Analyze(program);
    }

    /// <summary>The hosted service of one cell: it starts the work, writing the cell's field, waits for it where the placement says and
    /// writes the field after.</summary>
    /// <param name="kind">The wait.</param>
    /// <param name="at">Where the wait stands.</param>
    private static string SweepService(Kind kind, At at)
    {
        var (type, start, wait) = kind switch
        {
            Kind.ThreadJoin => ("Thread", "var w = new Thread(Write); w.Start();", "w.Join();"),
            Kind.ValueTaskResult => ("ValueTask<int>", "var w = WorkOfTAsync();", "_ = w.Result;"),
            Kind.ValueTaskGetResult => ("ValueTask", "var w = WorkAsync();", "w.GetAwaiter().GetResult();"),
            Kind.ValueTaskOfTGetResult => ("ValueTask<int>", "var w = WorkOfTAsync();", "w.GetAwaiter().GetResult();"),
            Kind.ConfiguredValueTaskGetResult => ("ValueTask", "var w = WorkAsync();", "w.ConfigureAwait(false).GetAwaiter().GetResult();"),
            Kind.ConfiguredValueTaskOfTGetResult => ("ValueTask<int>", "var w = WorkOfTAsync();", "w.ConfigureAwait(false).GetAwaiter().GetResult();"),
            Kind.TimerWaitOne => ("System.Threading.Timer", "var w = new System.Threading.Timer(_ => Write(), null, 0, Timeout.Infinite);",
                                  "var h = new ManualResetEvent(false); w.Dispose(h); h.WaitOne();"),
            Kind.WaitTimeout => ("Task<int>", $"var w = {VALUED};", "w.Wait(100);"),
            Kind.WaitToken => ("Task<int>", $"var w = {VALUED};", "w.Wait(CancellationToken.None);"),
            Kind.ProgramScheduler => ("Task<int>", "var w = Task.Factory.StartNew(() => { Write(); return 1; }, CancellationToken.None, TaskCreationOptions.None, Schedulers.Own);",
                                      "w.Wait();"),
            Kind.Result => ("Task<int>", $"var w = {VALUED};", "_ = w.Result;"),
            Kind.GetResult => ("Task<int>", $"var w = {VALUED};", "w.GetAwaiter().GetResult();"),
            Kind.WaitAll => ("Task<int>", $"var w = {VALUED};", "Task.WaitAll(w);"),
            Kind.WhenAllWait => ("Task<int>", $"var w = {VALUED};", "Task.WhenAll(w).Wait();"),
            Kind.Await => ("Task<int>", $"var w = {VALUED};", "await w;"),
            _ => ("Task<int>", $"var w = {VALUED};", "w.Wait();")
        };
        var awaits = kind == Kind.Await;
        var helpers = awaits
            ? $"private async Task Helper({type} w) {{ {wait} }} private async Task Outer({type} w) {{ await Helper(w); }} " +
              $"private async Task Run() {{ {start} {wait} }} private async Task RunOuter() {{ await Run(); }}"
            : $"private void Helper({type} w) {{ {wait} }} private void Outer({type} w) {{ Helper(w); }} " +
              $"private void Run() {{ {start} {wait} }} private void RunOuter() {{ Run(); }}";
        string Call(string helper, string argument = "w") => awaits ? $"await {helper}({argument});" : $"{helper}({argument});";
        var before = kind == Kind.InstallerReachable ? "if (Environment.ProcessorCount < 0) Install();" : "";
        var body = at switch
        {
            At.Direct => $"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {wait} }} catch (Exception) {{ }} After(); }}",
            At.Helper => $"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {Call("Helper")} }} catch (Exception) {{ }} After(); }}",
            At.HelperOfHelper => $"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {Call("Outer")} }} catch (Exception) {{ }} After(); }}",
            At.Starting => $"for (var pass = 0; pass < 2; pass++) {{ try {{ {Call("Run", "")} }} catch (Exception) {{ }} After(); }}",
            At.StartingOfHelper => $"for (var pass = 0; pass < 2; pass++) {{ try {{ {Call("RunOuter", "")} }} catch (Exception) {{ }} After(); }}",
            At.Nested => $"for (var pass = 0; pass < 2; pass++) {{ var x = Task.Run({(awaits ? "async " : "")}() => {{ {start} {Call("Helper")} }}); " +
                         "try { x.Wait(); } catch (Exception) { } After(); }",
            At.Uncaught => $"for (var pass = 0; pass < 2; pass++) {{ {start} {wait} After(); }}",
            At.UncaughtHelper => $"for (var pass = 0; pass < 2; pass++) {{ {start} {Call("Helper")} After(); }}",
            At.UncaughtStarting => $"for (var pass = 0; pass < 2; pass++) {{ {Call("Run", "")} After(); }}",
            At.SerializedRoot => $"{start} {wait} After();",
            At.SerializedRootHelper => $"{start} {Call("Helper")} After();",
            _ => $"{Call("Run", "")} After();"
        };
        return $$"""

            public sealed class P_{{at}} : BackgroundService
            {
                public static int V_{{at}};
                private void Write() { V_{{at}} = 1; }
                private void After() { V_{{at}} = 3; }
                private static void Install() => SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                private async ValueTask WorkAsync() { await Task.Yield(); Write(); }
                private async ValueTask<int> WorkOfTAsync() { await Task.Yield(); Write(); return 1; }
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{before}}
                    {{body}}
                    await Task.CompletedTask;
                }
                {{helpers}}
            }

            """;
    }

    /// <summary>The review's probes as written: a helper interrupting its own thread, then starting a thread and joining it, called in a
    /// loop under a <c>catch</c> or as the whole body of a repeated serialized root — the interrupted join lets the next pass begin with
    /// the thread still running, so the self-pair stays, and so it does with the <c>catch</c> around the join inside the helper. With no
    /// <c>catch</c> in a root that runs once, the join's exception ends the root and the normal return orders the passes.</summary>
    /// <param name="shape">A short name of the case, which tells the cases apart and names a failing one.</param>
    /// <param name="body">The worker's body.</param>
    /// <param name="serialized">Whether every root is invoked again and again, one invocation at a time.</param>
    /// <param name="paired">Whether the self-pair stays.</param>
    [Theory]
    [InlineData("helper-in-a-loop", "for (var i = 0; i < 2; i++) { try { JoinThread(); } catch (Exception) { } }", false, true)]
    [InlineData("repeated-serialized-root", "JoinThread();", true, true)]
    [InlineData("local-catch", "for (var i = 0; i < 2; i++) { CaughtJoinThread(); }", false, true)]
    [InlineData("uncaught-loop", "for (var i = 0; i < 2; i++) { JoinThread(); }", false, false)]
    public void An_interrupted_thread_join_keeps_the_self_pair_its_exception_reaches(string shape, string body, bool serialized, bool paired)
    {
        const string JOIN = """
            private void JoinThread() { Thread.CurrentThread.Interrupt(); var t = new Thread(Write); t.Start(); t.Join(); }
            private void CaughtJoinThread() { Thread.CurrentThread.Interrupt(); var t = new Thread(Write); t.Start(); try { t.Join(); } catch (Exception) { } }
            """;
        var source = Worker($"{body} await Task.CompletedTask;", JOIN);
        var run = serialized ? WithRepeatedSerializedRoot(source) : Analyze(source);

        Assert.NotEmpty(run.Of("Value"));
        Assert.True(paired == run.PairsOn("Value").Any(pair => pair.First.ExecutionId == pair.Second.ExecutionId),
                    $"{shape}: expected the self-pair {(paired ? "kept" : "removed")}.");
    }

    /// <summary>The task each task kind of the sweep waits for: a <c>Task.Run</c> writing the cell's field.</summary>
    private const string VALUED = "Task.Run(() => { Write(); return 1; })";

    /// <summary>The scheduler a program names.</summary>
    private const string SCHEDULER = """
        public sealed class OwnScheduler : TaskScheduler
        {
            protected override System.Collections.Generic.IEnumerable<Task>? GetScheduledTasks() => null;
            protected override void QueueTask(Task task) => TryExecuteTask(task);
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);
        }

        public static class Schedulers
        {
            public static readonly TaskScheduler Own = new OwnScheduler();
        }

        """;

    private const string RETRY ="for (var attempt = 0; attempt < 3; attempt++) { try { var own = Task.Run(Write); own.Wait(); break; } " +
                                 "catch (AggregateException) { } } await Task.CompletedTask;";

    private const string SEED ="private void Seed() => SeedAsync().Wait(); private async Task SeedAsync() { await Task.Yield(); Write(); }";

    /// <summary>Runs the engine with every root invoked again and again, one invocation at a time.</summary>
    /// <param name="source">The source file of the case.</param>
    private static EngineRun WithRepeatedSerializedRoot(string source)
    {
        var reached = Reach(source);
        var roots = reached.Input.Roots.Select(root => root with
        {
            InvocationPolicy = new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.Serialized, "")
        }).ToArray();
        var execution = Execute(Solve(reached with { Input = reached.Input with { Roots = roots } }));
        var collection = Collect(execution);
        return new EngineRun(execution, collection, InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap));
    }

    /// <summary>Every write of an execution that is started more than once is ordered with itself by the completion proof of the queried
    /// passes, which the production decision took.</summary>
    /// <param name="run">The analysed program, whose only accesses of <c>Value</c> are the work's writes.</param>
    private static void AssertPassCompletion(EngineRun run)
    {
        var repeated = run.Of("Value").Where(access => run.Execution.Analysis.Execution(access.ExecutionId).OverlapsItself).ToArray();
        Assert.NotEmpty(repeated);
        Assert.All(repeated, access =>
        {
            var proof = run.Execution.Analysis.OrderProof(access, access);
            Assert.Equal(TimingBranch.PassCompletion, proof.Branch);
            Assert.Equal(PairTiming.Ordered, proof.Timing);
            Assert.Contains(proof.Frames, frame => frame.Execution == access.ExecutionId && frame.Joins.Count != 0);
        });
    }

    private static string Worker(string body, string members = "") => Startup("services.AddHostedService<Worker>();") + $$"""

        public sealed class Worker : BackgroundService
        {
            public static int Value;
            private void Write() { Value = 1; }
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
            {{members}}
        }
        """;
}
