using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A <c>sequential-awaited</c> call that only the once-per-run threshold keeps out — its one execution runs once, and passes the
/// call's site again — is admitted per pass: the retry of the eShop seeding shape, a seeder that waits for its own async seeding inside
/// a synchronous retry, over the real Polly 7.2.3 asset. The seeding of one pass completes before the next pass, so its runs do not
/// overlap one another or the code after the wait.</summary>
public sealed class PerPassAdmissionTests
{
    /// <summary>The seeding of the eShop shape: a seeder whose async seeding runs inside an async retry, and a synchronous migration
    /// that retries the seeder it is given.</summary>
    private const string SEEDING = """
        public sealed class SqlException : Exception { }
        public sealed class A { }
        public sealed class B { }
        private static async Task SeedAsync()
        {
            var policy = Policy.Handle<SqlException>().WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(1), (e, t, r, c) => { });
            await policy.ExecuteAsync(async () =>
            {
                Value = 1;
                await Task.Yield();
                Value = 2;
            });
        }
        private static void Migrate<T>(Action<T> seeder) where T : new()
        {
            try
            {
                var retry = Policy.Handle<SqlException>().WaitAndRetry(new[] { TimeSpan.FromSeconds(1) });
                retry.Execute(() => seeder(new T()));
            }
            catch (Exception) { }
        }
        """;

    /// <summary>The members of the nested retry: a synchronous retry around a seeder that awaits its own async retry, whose delegate
    /// writes before and after an await, each write on a line of its own.</summary>
    private const string NESTED = """
        private readonly RetryPolicy _retry = Policy.Handle<Exception>().Retry(2);
        private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2);
        private async Task SeedAsync()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Value = 1;
                await Task.Yield();
                Value = 2;
            });
        }
        """;

    /// <summary>A seeder whose delegate writes in its synchronous part through a chain of two methods and in its continuation through
    /// one.</summary>
    /// <param name="modifier">The modifier of the three methods: empty or <c>static </c>.</param>
    private static string Callees(string modifier) => $$"""
        private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2);
        private {{modifier}}void First() { Value = 1; }
        private {{modifier}}void Second() { Value = 2; }
        private {{modifier}}void Outer() { First(); }
        private async Task Seed()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Outer();
                await Task.Yield();
                Second();
            });
        }
        """;

    [Fact]
    public void Distinct_generic_migrations_keep_distinct_seeders()
    {
        var distinct = Seeding("Migrate<A>(a => SeedAsync().Wait()); Migrate<B>(b => { });");

        Assert.True(distinct.Run.Of("Value").Count >= 2);
        Assert.Empty(distinct.Run.PairsOn("Value"));
    }

    /// <summary>Migrations whose seeders do not wait for their async seeding leave it running: the seeding of each generic instance is
    /// admitted on its own, and nothing orders the work of one with the other's.</summary>
    [Fact]
    public void Distinct_generic_migrations_whose_seeders_are_not_waited_keep_their_pair()
    {
        var detached = Seeding("Migrate<A>(a => { var d = SeedAsync(); }); Migrate<B>(b => { var d = SeedAsync(); });");

        Assert.True(detached.Run.Of("Value").Count >= 2);
        Assert.NotEmpty(detached.Run.PairsOn("Value"));
    }

    /// <summary>A method the retry's own handler reaches besides the delegate is no place of a run the admission orders: what the call
    /// runs for something else is an execution of its own, and the pair of the method with the continuation stays.</summary>
    [Fact]
    public void Method_a_handler_of_the_call_also_reaches_keeps_its_pair()
    {
        const string MEMBERS = """
            private static void First() { Value = 1; }
            private static void Second() { Value = 2; }
            private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2, (e, n) => First());
            private async Task Seed()
            {
                await _seed.ExecuteAsync(async () =>
                {
                    First();
                    await Task.Yield();
                    Second();
                });
            }
            """;
        var run = Build("for (var pass = 0; pass < 2; pass++) { await Seed(); }", MEMBERS).Run;

        Assert.True(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "Second"), "The method the handler reaches lost its pair.");
    }

    /// <summary>Two migrations of one seeder enter its async seeding twice. Each blocking wait reads its exceptional exit as the end of
    /// the seeding (ADR 0030), so the seeding of the first entry completes before the second starts and no pair is left.</summary>
    [Fact]
    public void Same_seeder_in_two_sequential_migrations_has_no_pair()
    {
        var twice = Seeding("Migrate<A>(a => SeedAsync().Wait()); Migrate<A>(a => SeedAsync().Wait());");

        Assert.True(twice.Run.Of("Value").Count >= 2);
        Assert.Empty(twice.Run.PairsOn("Value"));
    }

    [Fact]
    public void Nested_retry_of_the_eshop_shape_admits_the_seeder_per_pass()
    {
        var nested = Nested("_retry.Execute(() => SeedAsync().Wait());");

        Assert.True(nested.Run.Of("Value").Count >= 2);
        Assert.Empty(nested.Run.PairsOn("Value"));
        // The wait of the call's task is no join of the end of all its runs: the proof of the pass reads it.
        Assert.Contains(nested.Run.Execution.Analysis.UnprovenJoins,
                        site => site.BodyId.EndsWith("Worker.SeedAsync", StringComparison.Ordinal));
        var (timing, assumed) = nested.Decide(2);
        Assert.Equal(PairTiming.Ordered, timing);
        Assert.True(assumed.SetEquals([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT]), string.Join(",", assumed));
    }

    [Fact]
    public void Nested_retry_whose_pass_does_not_wait_the_seeder_keeps_the_pair()
    {
        var nested = Nested("_retry.Execute(() => { var detached = SeedAsync(); });");

        Assert.True(nested.Run.Of("Value").Count >= 2);
        Assert.NotEmpty(nested.Run.PairsOn("Value"));
    }

    [Fact]
    public void Nested_retry_on_the_thread_pool_counts_under_thread_pool_entry()
    {
        var nested = Nested("await Task.Run(() => _retry.Execute(() => SeedAsync().Wait()));");

        Assert.True(nested.Run.Of("Value").Count >= 2);
        Assert.Empty(nested.Run.PairsOn("Value"));
        var (timing, assumed) = nested.Decide(2);
        Assert.Equal(PairTiming.Ordered, timing);
        Assert.True(assumed.SetEquals([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_POOL_ENTRY]), string.Join(",", assumed));
    }

    [Fact]
    public void Blocking_pass_reached_again_only_after_a_normal_exit_counts_under_no_key()
    {
        var nested = Nested("for (var pass = 0; pass < 2; pass++) { SeedAsync().Wait(); }");

        Assert.True(nested.Run.Of("Value").Count >= 2);
        Assert.Empty(nested.Run.PairsOn("Value"));
        var (timing, assumed) = nested.Decide(2);
        Assert.Equal(PairTiming.Ordered, timing);
        Assert.Empty(assumed);
    }

    /// <summary>The synchronous part of a run is everything the delegate runs before its first await, the methods it calls included: the
    /// continuation of a run precedes a write made two calls below the delegate in the next run.</summary>
    /// <param name="kind">Whether the methods are instance or static ones.</param>
    /// <param name="modifier">The modifier of the methods: empty or <c>static </c>.</param>
    [Theory]
    [InlineData("instance", "")]
    [InlineData("static", "static ")]
    public void Continuation_precedes_the_next_sync_part_written_through_a_chain_of_callees(string kind, string modifier)
    {
        var run = Build("for (var pass = 0; pass < 2; pass++) { var s = Seed(); await s; }", Callees(modifier)).Run;

        Assert.False(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "Second"), $"{kind}: the callee's write keeps a pair.");
    }

    /// <summary>A method the delegate's synchronous part calls is part of the run only when nothing else calls it: one the pass also
    /// calls while the run is in flight writes for the pass as well, and that write overlaps the continuation of the run.</summary>
    /// <param name="kind">Whether the methods are instance or static ones.</param>
    /// <param name="modifier">The modifier of the methods: empty or <c>static </c>.</param>
    [Theory]
    [InlineData("instance", "")]
    [InlineData("static", "static ")]
    public void Callee_the_pass_calls_while_the_run_is_in_flight_keeps_the_pair_with_the_continuation(string kind, string modifier)
    {
        var run = Build("for (var pass = 0; pass < 2; pass++) { var s = Seed(); First(); await s; }", Callees(modifier)).Run;

        Assert.True(BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "Second"), $"{kind}: the shared method's write lost its pair.");
    }

    /// <summary>A seeder that awaits the call whose delegate writes in its synchronous part, in its continuation and, for the pass, a write
    /// of its own, with the helpers that sit between the pass and the seeder.</summary>
    /// <param name="helpers">The async helpers of the chain: <c>Middle</c>, and <c>Outer</c> above it.</param>
    private static string Chain(string helpers) => $$"""
        private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2);
        private void Sync() { Value = 1; }
        private void Cont() { Value = 2; }
        private void Mine() { Value = 3; }
        private async Task Seed()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Sync();
                await Task.Yield();
                Cont();
            });
        }
        {{helpers}}
        """;

    private const string MIDDLE_LOCAL = "private async Task Middle() { var t = Seed(); await t; }";

    private const string MIDDLE_DIRECT = "private async Task Middle() { await Seed(); }";

    private const string OUTER_LOCAL = "private async Task Outer() { var m = Middle(); await m; }";

    private const string OUTER_UNAWAITING = "private async Task Outer() { var m = Middle(); await Task.Yield(); }";

    /// <summary>A chain of async helpers between the pass and the seeding call gives its task to the caller at each <c>await</c>, so the
    /// work of the call is complete only where the caller of the outermost helper waits for that helper's task: a pass that does not
    /// wait for it, through a local task or directly, two helpers deep or one, or two calls before the first wait, keeps the pairs of
    /// the continuation of the call's delegate with the synchronous part of the next run, with itself and with the pass's own write,
    /// and one that waits for every task on the way removes them.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="pass">The statements of the worker, which stand where the pass is.</param>
    /// <param name="helpers">The helpers the pass calls.</param>
    /// <param name="sync">Whether the continuation keeps a pair with the synchronous part of a run.</param>
    /// <param name="self">Whether the continuation keeps a pair with itself.</param>
    /// <param name="mine">Whether the continuation keeps a pair with the pass's own write.</param>
    [Theory]
    [InlineData("local-task-unwaited", "for (var pass = 0; pass < 2; pass++) { var m = Middle(); Mine(); }", MIDDLE_LOCAL, true, true, true)]
    [InlineData("direct-unwaited", "for (var pass = 0; pass < 2; pass++) { var m = Middle(); Mine(); }", MIDDLE_DIRECT, true, true, true)]
    [InlineData("local-task-waited", "for (var pass = 0; pass < 2; pass++) { await Middle(); Mine(); }", MIDDLE_LOCAL, false, false, false)]
    [InlineData("direct-waited", "for (var pass = 0; pass < 2; pass++) { await Middle(); Mine(); }", MIDDLE_DIRECT, false, false, false)]
    [InlineData("local-task-waited-after-the-write", "for (var pass = 0; pass < 2; pass++) { var m = Middle(); Mine(); await m; }", MIDDLE_LOCAL, false, false, true)]
    [InlineData("two-calls-before-the-first-wait", "var a = Middle(); Mine(); var b = Middle(); await a; await b;", MIDDLE_LOCAL, true, true, true)]
    [InlineData("two-calls-each-waited", "await Middle(); await Middle(); Mine();", MIDDLE_LOCAL, false, false, false)]
    [InlineData("three-levels-unwaited", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); Mine(); }", OUTER_LOCAL + " " + MIDDLE_LOCAL, true, true, true)]
    [InlineData("three-levels-waited", "for (var pass = 0; pass < 2; pass++) { await Outer(); Mine(); }", OUTER_LOCAL + " " + MIDDLE_LOCAL, false, false, false)]
    [InlineData("three-levels-middle-not-waited-by-outer", "for (var pass = 0; pass < 2; pass++) { await Outer(); Mine(); }", OUTER_UNAWAITING + " " + MIDDLE_LOCAL, true, true, true)]
    public void Chain_of_async_entries_completes_the_work_only_where_the_outermost_task_is_waited(string label, string pass, string helpers, bool sync,
                                                                                                  bool self, bool mine)
    {
        var run = Build(pass, Chain(helpers)).Run;

        Assert.True(sync == BlockingWaitAssumptionMatrixTests.Pairs(run, "Sync", "Cont"), $"{label}: the next synchronous part {(sync ? "lost" : "kept")} its pair.");
        Assert.True(self == BlockingWaitAssumptionMatrixTests.Pairs(run, "Cont", "Cont"), $"{label}: the continuation {(self ? "lost" : "kept")} its pair with itself.");
        Assert.True(mine == BlockingWaitAssumptionMatrixTests.Pairs(run, "Mine", "Cont"), $"{label}: the pass's write {(mine ? "lost" : "kept")} its pair.");
    }

    private const string OUTER_CALL = "_outer.ExecuteAsync(async () => { var s = Seed(); await s; })";

    private const string OUTER_MEMBERS = """
        private readonly AsyncRetryPolicy _outer = Policy.Handle<Exception>().RetryAsync(2);
        private readonly RetryPolicy _retry = Policy.Handle<Exception>().Retry(2);
        private static void MayThrow() { if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); }
        private async Task HelperDetached() { _outer.ExecuteAsync(async () => { var s = Seed(); await s; }); await Task.Yield(); Mine(); }
        private async Task HelperAwaited() { await _outer.ExecuteAsync(async () => { var s = Seed(); await s; }); await Task.Yield(); Mine(); }
        """;

    /// <summary>The seeding inside the delegate of an outer admitted call is complete for the next run of that delegate when its path
    /// leaves the delegate at a suspension, because the library waits each run's task, whatever happens to the outer call's task; for
    /// code after the outer call returns, and for a next pass of the outer call itself, it is complete only where the outer call's own
    /// task is waited at once (R1, R2). A call nobody waits, one waited after something that may throw and one waited through
    /// <c>Task.WhenAll</c> leave the pass's own write paired with the continuation, in a loop and out of one, and in the tail of a body
    /// that makes the call, and in a loop they leave the runs of the next call paired as well; out of a loop the next run is ordered.
    /// One awaited or blocked on at once removes every pair, and so does a synchronous outer retry whose delegate blocks on the
    /// seeding; one whose delegate leaves the seeding running keeps them all.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="pass">The statements of the worker, which stand where the pass is.</param>
    /// <param name="sync">Whether the continuation keeps a pair with the next synchronous part.</param>
    /// <param name="self">Whether the continuation keeps a pair with itself.</param>
    /// <param name="mine">Whether the continuation keeps a pair with the pass's own write after the outer call.</param>
    [Theory]
    [InlineData("outer-unawaited", OUTER_CALL + "; Mine();", false, false, true)]
    [InlineData("outer-awaited", "await " + OUTER_CALL + "; Mine();", false, false, false)]
    [InlineData("outer-blocked-on", OUTER_CALL + ".Wait(); Mine();", false, false, false)]
    [InlineData("outer-awaited-after-may-throw", "var t = " + OUTER_CALL + "; MayThrow(); await t; Mine();", false, false, true)]
    [InlineData("outer-when-all", "await Task.WhenAll(" + OUTER_CALL + "); Mine();", false, false, true)]
    [InlineData("outer-in-a-loop-unawaited", "for (var pass = 0; pass < 2; pass++) { " + OUTER_CALL + "; Mine(); }", true, true, true)]
    [InlineData("outer-in-a-loop-awaited", "for (var pass = 0; pass < 2; pass++) { await " + OUTER_CALL + "; Mine(); }", false, false, false)]
    [InlineData("outer-in-a-loop-awaited-after-may-throw", "for (var pass = 0; pass < 2; pass++) { var t = " + OUTER_CALL + "; MayThrow(); await t; Mine(); }", true, true, true)]
    [InlineData("sync-outer-detached", "_retry.Execute(() => { var d = Seed(); }); Mine();", true, true, true)]
    [InlineData("sync-outer-blocking", "_retry.Execute(() => Seed().Wait()); Mine();", false, false, false)]
    [InlineData("tail-after-an-unawaited-outer", "var h = HelperDetached(); await h;", false, false, true)]
    public void Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(
        string label, string pass, bool sync, bool self, bool mine)
    {
        var run = Build(pass, Chain(OUTER_MEMBERS)).Run;

        Assert.True(sync == BlockingWaitAssumptionMatrixTests.Pairs(run, "Sync", "Cont"), $"{label}: the next synchronous part {(sync ? "lost" : "kept")} its pair.");
        Assert.True(self == BlockingWaitAssumptionMatrixTests.Pairs(run, "Cont", "Cont"), $"{label}: the continuation {(self ? "lost" : "kept")} its pair with itself.");
        Assert.True(mine == BlockingWaitAssumptionMatrixTests.Pairs(run, "Mine", "Cont"), $"{label}: the pass's write {(mine ? "lost" : "kept")} its pair.");
    }

    /// <summary>The same rule holds for the work of any pass, not only the continuations of a library's runs: a <c>Task.Run</c> whose
    /// task a helper awaits through a local is complete only where the caller of the helper waits for the helper's task.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="pass">The statements of the worker, which stand where the pass is.</param>
    /// <param name="pairs">Whether the work keeps its pairs with itself in the next pass and with the pass's own write.</param>
    [Theory]
    [InlineData("helper-unwaited", "for (var pass = 0; pass < 2; pass++) { var m = Middle(); Mine(); }", true)]
    [InlineData("helper-waited", "for (var pass = 0; pass < 2; pass++) { await Middle(); Mine(); }", false)]
    public void Task_run_awaited_by_a_helper_is_complete_only_where_the_helper_is_waited(string label, string pass, bool pairs)
    {
        const string MEMBERS = """
            private void Work() { Value = 2; }
            private void Mine() { Value = 3; }
            private async Task Middle() { var t = Task.Run(() => Work()); await t; }
            """;
        var run = Build(pass, MEMBERS).Run;

        Assert.True(pairs == BlockingWaitAssumptionMatrixTests.Pairs(run, "Work", "Work"), $"{label}: the work {(pairs ? "lost" : "kept")} its pair with the next pass.");
        Assert.True(pairs == BlockingWaitAssumptionMatrixTests.Pairs(run, "Mine", "Work"), $"{label}: the pass's write {(pairs ? "lost" : "kept")} its pair.");
    }

    /// <summary>A seeder whose delegate calls a method in its synchronous part that the pass calls too, directly or through a second method,
    /// and writes in its continuation.</summary>
    /// <param name="modifier">The modifier of the methods: empty or <c>static </c>.</param>
    private static string Shared(string modifier) => $$"""
        private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2);
        private {{modifier}}void First() { Value = 1; }
        private {{modifier}}void Through() { First(); }
        private {{modifier}}void Second() { Value = 2; }
        private async Task Seed()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Through();
                await Task.Yield();
                Second();
            });
        }
        """;

    /// <summary>A method the delegate's synchronous part calls and the pass calls too is a place of the pass wherever the pass calls it
    /// (R1 a, c): a call before the seeding call, or after the wait for its task, is ordered with the continuation of every run, and one
    /// made while the run is in flight is not. The method being shared is no reason to keep the pair, directly or through a second
    /// method, for a pass that makes its call again and one that makes it once, instance or static.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="modifier">The modifier of the methods: empty or <c>static </c>.</param>
    /// <param name="pass">The statements of the worker, which stand where the pass is.</param>
    /// <param name="pair">Whether the method keeps a pair with the continuation.</param>
    [Theory]
    [InlineData("again-before-the-call", "", "for (var pass = 0; pass < 2; pass++) { First(); await Seed(); }", false)]
    [InlineData("again-after-the-wait", "", "for (var pass = 0; pass < 2; pass++) { await Seed(); First(); }", false)]
    [InlineData("again-through-a-method-before", "", "for (var pass = 0; pass < 2; pass++) { Through(); await Seed(); }", false)]
    [InlineData("again-while-in-flight", "", "for (var pass = 0; pass < 2; pass++) { var s = Seed(); First(); await s; }", true)]
    [InlineData("again-through-a-method-while-in-flight", "", "for (var pass = 0; pass < 2; pass++) { var s = Seed(); Through(); await s; }", true)]
    [InlineData("once-before-the-call", "", "First(); await Seed();", false)]
    [InlineData("once-after-the-wait", "", "await Seed(); First();", false)]
    [InlineData("once-while-in-flight", "", "var s = Seed(); First(); await s;", true)]
    [InlineData("static-again-before-the-call", "static ", "for (var pass = 0; pass < 2; pass++) { First(); await Seed(); }", false)]
    [InlineData("static-again-after-the-wait", "static ", "for (var pass = 0; pass < 2; pass++) { await Seed(); First(); }", false)]
    [InlineData("static-again-while-in-flight", "static ", "for (var pass = 0; pass < 2; pass++) { var s = Seed(); First(); await s; }", true)]
    [InlineData("static-once-before-the-call", "static ", "First(); await Seed();", false)]
    [InlineData("static-once-while-in-flight", "static ", "var s = Seed(); First(); await s;", true)]
    public void Method_the_delegate_also_calls_is_ordered_by_where_the_pass_calls_it(string label, string modifier, string pass, bool pair)
    {
        var run = Build(pass, Shared(modifier)).Run;

        Assert.True(pair == BlockingWaitAssumptionMatrixTests.Pairs(run, "First", "Second"), $"{label}: the shared method {(pair ? "lost" : "kept")} its pair.");
    }

    private const string RUN = "Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; })";

    /// <summary>What the body that makes the call does after the call, in the synchronous part of <c>Helper</c> and past a suspension of
    /// its own: the code after the suspension runs in the body's tail, which the pass starts by calling <c>Helper</c> and waits for
    /// later; it is parent code after the wait (R1 c) wherever the call's task was waited for at once, on every path that reaches the
    /// suspension — a blocking wait, the return of a member without a task, an
    /// <c>await</c> of the call on its branch with another <c>await</c> on the branch that skips it — and not where some path reaches
    /// a suspension around the wait.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="helper">The body of <c>Helper</c>, which the pass calls and awaits, ending in a write of 2 after a suspension.</param>
    /// <param name="after">Whether the continuation keeps a pair with the write of the tail.</param>
    /// <param name="self">Whether the continuation keeps a pair with itself.</param>
    [Theory]
    [InlineData("wait", RUN + ".Wait(); await Task.Yield(); _state.C = 2;", false, false)]
    [InlineData("get", RUN + ".GetAwaiter().GetResult(); await Task.Yield(); _state.C = 2;", false, false)]
    [InlineData("result", "_ = Runs.Lib.TaskOf(async () => { await Task.Yield(); _state.C = 1; return 1; }).Result; await Task.Yield(); _state.C = 2;", false, false)]
    [InlineData("wait-all", "var t = " + RUN + "; Task.WaitAll(t); await Task.Yield(); _state.C = 2;", false, false)]
    [InlineData("task-less-member", "Runs.Lib.Fire(async () => { await Task.Yield(); _state.C = 1; }); await Task.Yield(); _state.C = 2;", false, false)]
    [InlineData("await-on-one-branch", "if (_state.Choose) { await " + RUN + "; } else { await Task.Yield(); } _state.C = 2;", false, false)]
    [InlineData("wait-skipped-on-a-branch", "var t = " + RUN + "; if (_state.Choose) { t.Wait(); } await Task.Yield(); _state.C = 2;", true, true)]
    [InlineData("no-wait", RUN + "; await Task.Yield(); _state.C = 2;", true, true)]
    [InlineData("other-branch-suspends-around-the-wait", "var t = " + RUN + "; if (_state.Choose) { await t; } else { await Task.Yield(); } _state.C = 2;", true, true)]
    public void Write_in_the_tail_of_the_calling_body_is_ordered_after_a_wait_at_once(string label, string helper, bool after, bool self)
    {
        var run = AwaitedRunsFixture.Run("for (var pass = 0; pass < 2; pass++) { var h = Helper(); await h; }", members: $"private async Task Helper() {{ {helper} }}");

        AssertPairs(label, run, after, self);
    }

    /// <summary>The tail of a body higher in the chain runs after that body's own suspension, which the wait for the task of the body
    /// below ends: its write is ordered after the work where it waits that task on every path and the pass waits for it in turn.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="outer">The body of <c>Outer</c>, which calls <c>Inner</c>, whose call is the seeding call.</param>
    /// <param name="pass">The statements of the worker, which stand where the pass is.</param>
    /// <param name="after">Whether the continuation keeps a pair with the write of the outer tail.</param>
    /// <param name="self">Whether the continuation keeps a pair with itself.</param>
    [Theory]
    [InlineData("local-task", "var t = Inner(); await t; _state.C = 2;", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); await o; }", false, false)]
    [InlineData("blocking-wait-of-the-helper", "var t = Inner(); t.Wait(); _state.C = 2;", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); await o; }", false, false)]
    [InlineData("inner-task-not-awaited", "var t = Inner(); await Task.Yield(); _state.C = 2;", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); await o; }", true, true)]
    [InlineData("outer-task-not-awaited", "var t = Inner(); await t; _state.C = 2;", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); }", true, true)]
    [InlineData("inner-task-awaited-on-a-branch", "var t = Inner(); if (_state.Choose) { await t; } _state.C = 2;", "for (var pass = 0; pass < 2; pass++) { var o = Outer(); await o; }", true, true)]
    public void Write_in_the_tail_of_a_body_above_is_ordered_where_that_body_waits_the_task_below(string label, string outer, string pass, bool after, bool self)
    {
        var members = $$"""
            private async Task Inner() { await {{RUN}}; }
            private async Task Outer() { {{outer}} }
            """;
        var run = AwaitedRunsFixture.Run(pass, members: members);

        AssertPairs(label, run, after, self);
    }

    /// <summary>Asserts whether the continuation of a run keeps a pair with the write of 2 and with itself, naming the row in a failure.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="run">The analysed program.</param>
    /// <param name="after">Whether the continuation keeps a pair with the write of 2.</param>
    /// <param name="self">Whether the continuation keeps a pair with itself.</param>
    private static void AssertPairs(string label, EngineRun run, bool after, bool self)
    {
        try
        {
            AwaitedRunsFixture.AssertPairs(run, after, self);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            throw new Xunit.Sdk.XunitException($"{label}: {failure.Message}", failure);
        }
    }

    /// <summary>The program of a test, with its source to find the line of a write in.</summary>
    /// <param name="Run">The analysed program.</param>
    /// <param name="Source">The source file of the program.</param>
    private sealed record Analyzed(EngineRun Run, string Source)
    {
        /// <summary>How the write of a constant to <c>Value</c> meets itself, with the assumptions an ordered pair rests on. Every
        /// access on that line is decided alike.</summary>
        /// <param name="value">The constant the line writes.</param>
        internal (PairTiming Timing, IReadOnlySet<string> Assumed) Decide(int value)
        {
            var line = Array.FindIndex(Source.Split('\n'), text => text.Contains($"Value = {value};", StringComparison.Ordinal)) + 1;
            var writes = Run.Of("Value").Where(access => access.Operation == AccessOperation.Write && access.Source.StartLine == line).ToArray();
            Assert.NotEmpty(writes);
            var decisions = writes.Select(write =>
            {
                var timing = Run.Execution.Analysis.Timing(write, write, out var assumed);
                return (Timing: timing, Assumed: assumed);
            }).ToArray();
            Assert.All(decisions, decision => Assert.True(decision.Timing == decisions[0].Timing && decision.Assumed.SetEquals(decisions[0].Assumed),
                                                          "The accesses of one line are decided apart."));
            return decisions[0];
        }
    }

    /// <summary>Analyses the once-launched worker whose <c>ExecuteAsync</c> runs <paramref name="body"/> beside the seeding members.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    private static Analyzed Seeding(string body) => Build(body, SEEDING);

    /// <summary>Analyses the once-launched worker whose <c>ExecuteAsync</c> runs <paramref name="body"/> beside the nested retry.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    private static Analyzed Nested(string body) => Build(body, NESTED);

    /// <summary>Analyses the once-launched worker whose <c>ExecuteAsync</c> runs <paramref name="body"/> beside <paramref name="members"/>.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    /// <param name="members">The members the body calls.</param>
    private static Analyzed Build(string body, string members)
    {
        var source = Usings + "using Polly;\nusing Polly.Retry;\n" + Startup("services.AddHostedService<Worker>();") + $$"""

            public sealed class Worker : BackgroundService
            {
                public static int Value;
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{body}}
                    await Task.CompletedTask;
                }
                {{members}}
            }
            """;
        var options = new FixtureOptions { MetadataReferences = LibraryPackages.BesideStubs };

        return new Analyzed(AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", source)), "scope:Fixture"), source);
    }
}
