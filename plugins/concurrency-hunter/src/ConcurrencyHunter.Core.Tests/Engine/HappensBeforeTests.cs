using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The happens-before graph of ADR 0008: which pairs of accesses the order of spawns, joins, continuations, async tails and
/// startup removes, and which it keeps. Each access is a write of <c>Value</c> in a helper named for its role, so a pair is named by
/// its two helpers.</summary>
public sealed class HappensBeforeTests
{
    // ---- task joins ----

    [Fact]
    public void Parent_write_before_the_spawn_does_not_overlap_the_work()
    {
        var run = Analyze(Worker("P1(); var t = Task.Run(() => F()); await t;"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parent_write_between_the_spawn_and_its_await_overlaps_the_work()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); P1(); await t;"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parent_write_after_awaiting_the_task_does_not_overlap_the_work()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); await t; P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parent_write_after_wait_does_not_overlap_the_work_and_one_before_it_does()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); P2(); t.Wait(); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Parent_write_after_thread_join_does_not_overlap_the_thread()
    {
        var run = Analyze(Worker("var thread = new Thread(() => F()); thread.Start(); thread.Join(); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parent_write_between_thread_start_and_join_overlaps_the_thread()
    {
        var run = Analyze(Worker("var thread = new Thread(() => F()); thread.Start(); P1(); thread.Join();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_with_a_timeout_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); t.Wait(100); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Join_with_a_timeout_orders_nothing()
    {
        var run = Analyze(Worker("var thread = new Thread(() => F()); thread.Start(); thread.Join(100); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_with_a_token_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); t.Wait(stoppingToken); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    // ---- exceptional exits ----

    [Fact]
    public void Join_interrupted_into_a_catch_orders_nothing()
    {
        var run = Analyze(Worker("var thread = new Thread(() => F()); thread.Start(); try { thread.Join(); } catch (ThreadInterruptedException) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_in_a_try_with_a_catch_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); try { t.Wait(); } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_all_in_a_try_with_a_catch_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var b = Task.Run(() => G()); try { Task.WaitAll(a, b); } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Await_in_a_try_with_a_catch_still_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); try { await t; } catch (Exception) { } P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_maybe_null_task_in_a_try_orders_nothing()
    {
        var run = Analyze(Worker("Task? t = null; if (stoppingToken.CanBeCanceled) t = Task.Run(() => F()); try { await t!; } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_task_an_opaque_call_returned_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); var same = System.Linq.Enumerable.First(new[] { t }); await same; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Join_on_all_paths_but_one_a_catch_skips_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); try { P2(); t.Wait(); } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Join_in_one_branch_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); if (stoppingToken.CanBeCanceled) t.Wait(); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Join_in_a_finally_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); try { P2(); } finally { t.Wait(); } P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Join_in_one_branch_of_a_finally_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); try { P2(); } finally { if (stoppingToken.CanBeCanceled) t.Wait(); } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    // ---- async work ----

    [Fact]
    public void Joined_thread_with_an_async_body_leaves_its_tail_overlapping()
    {
        var run = Analyze(Worker("var thread = new Thread(async () => { F(); await Task.Yield(); G(); }); thread.Start(); thread.Join(); P1();"));

        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Parallel_for_with_an_async_body_leaves_its_tail_overlapping()
    {
        var run = Analyze(Worker("Parallel.For(0, 2, async index => { F(); await Task.Yield(); G(); }); P1();"));

        Assert.True(Overlap(run, "P1", "G"));
        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_task_run_of_an_async_delegate_orders_its_tail()
    {
        var run = Analyze(Worker("var t = Task.Run(async () => { await Task.Yield(); F(); }); await t; P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_task_run_of_an_async_delegate_in_a_local_orders_its_tail()
    {
        var run = Analyze(Worker("Func<Task> work = async () => { await Task.Yield(); F(); }; var t = Task.Run(work); await t; P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_task_run_of_an_interface_method_group_implemented_async_orders_its_tail()
    {
        var run = Analyze(Worker("IStep step = new AsyncStep(this); var t = Task.Run(step.Run); await t; P1();",
                                 "public interface IStep { Task Run(); } " +
                                 "public sealed class AsyncStep(Worker worker) : IStep { public async Task Run() { await Task.Yield(); worker.F(); } }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Unwrapped_start_new_of_an_async_delegate_in_a_local_orders_its_tail_and_one_await_does_not()
    {
        var unwrapped = Analyze(Worker("Func<Task> work = async () => { await Task.Yield(); F(); }; var t = Task.Factory.StartNew(work).Unwrap(); await t; P1();"));
        var awaitedOnce = Analyze(Worker("Func<Task> work = async () => { await Task.Yield(); F(); }; var t = Task.Factory.StartNew(work); await t; P1();"));

        Assert.False(Overlap(unwrapped, "P1", "F"));
        Assert.True(Overlap(awaitedOnce, "P1", "F"));
    }

    [Fact]
    public void Awaited_twice_task_run_of_a_value_task_delegate_in_a_local_orders_its_tail()
    {
        var run = Analyze(Worker("Func<ValueTask> work = async () => { await Task.Yield(); F(); }; Task<ValueTask> t = Task.Run(work); await (await t); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Unwrapped_start_new_of_a_delegate_with_an_async_and_a_sync_target_orders_nothing()
    {
        var run = Analyze(Worker("Func<Task> work = stoppingToken.CanBeCanceled ? async () => { await Task.Yield(); F(); } : () => { G(); return Task.CompletedTask; }; " +
                                 "var t = Task.Factory.StartNew(work).Unwrap(); await t; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Start_new_of_an_async_delegate_awaited_once_leaves_its_tail_overlapping()
    {
        var run = Analyze(Worker("var t = Task.Factory.StartNew(async () => { await Task.Yield(); F(); }); await t; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Start_new_of_an_async_delegate_awaited_twice_orders_its_tail()
    {
        var run = Analyze(Worker("var t = Task.Factory.StartNew(async () => { await Task.Yield(); F(); }); await (await t); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Task_run_of_a_value_task_delegate_awaited_once_leaves_its_tail_overlapping()
    {
        var run = Analyze(Worker("Task<ValueTask> t = Task.Run<ValueTask>(async () => { await Task.Yield(); F(); }); await t; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Task_run_of_a_value_task_delegate_awaited_twice_orders_its_tail()
    {
        var run = Analyze(Worker("Task<ValueTask> t = Task.Run<ValueTask>(async () => { await Task.Yield(); F(); }); await (await t); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_parallel_for_each_async_orders_its_prefix_and_its_tail()
    {
        var run = Analyze(Worker("await Parallel.ForEachAsync(new[] { 1, 2 }, async (item, token) => { F(); await Task.Yield(); G(); }); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Async_call_prefix_precedes_its_tail_and_the_caller_s_later_writes_overlap_the_tail()
    {
        var run = Analyze(Worker("P3(); var t = Step(); P1(); await t; P2();",
                                 "private async Task Step() { F(); await Task.Yield(); G(); }"));

        Assert.False(Overlap(run, "F", "G"));
        Assert.False(Overlap(run, "P3", "G"));
        Assert.True(Overlap(run, "P1", "G"));
        Assert.False(Overlap(run, "P2", "G"));
    }

    [Fact]
    public void First_await_of_an_async_call_orders_the_work_it_waits_for()
    {
        var run = Analyze(Worker("_ = Step();", "private async Task Step() { var t = Task.Run(() => F()); await t; P1(); }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void First_await_of_a_spawned_async_delegate_orders_the_work_it_waits_for()
    {
        var run = Analyze(Worker("Task.Factory.StartNew(async () => { var t = Task.Run(() => F()); await t; P1(); });"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_that_joins_on_every_path_orders_the_write_after_it()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); Drain(); P1();",
                                 "private Task? _work; private void Drain() { try { P2(); } finally { _work!.Wait(); } }"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Call_that_joins_on_one_branch_only_orders_nothing()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); try { Drain(); } catch (Exception) { } P1();",
                                 "private Task? _work; private readonly bool _always; private void Drain() { if (_always) _work!.Wait(); }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void First_await_of_one_branch_does_not_order_the_work_of_the_other()
    {
        var run = Analyze(Worker("_ = Step();",
                                 "private readonly bool _flag; " +
                                 "private async Task Step() { var a = Task.Run(() => F()); var b = Task.Run(() => G()); " +
                                 "if (_flag) await a; else await b; P1(); }"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Call_that_joins_a_thread_the_caller_may_catch_an_interrupt_from_orders_nothing()
    {
        var run = Analyze(Worker("_worker = new Thread(() => F()); _worker.Start(); try { Drain(); } catch (ThreadInterruptedException) { } P1();",
                                 "private Thread? _worker; private void Drain() => _worker!.Join();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_call_the_caller_catches_a_throw_of_orders_nothing()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); try { await Drain(); } catch (Exception) { } P1();",
                                 "private Task? _work; private async Task Drain() { await _work!; }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_on_a_choice_between_an_async_call_and_an_unknown_task_orders_nothing()
    {
        var run = Analyze(Worker("var chosen = _flag ? Step() : Helper(); chosen.Wait(); P1();",
                                 "private readonly bool _flag; " +
                                 "private async Task Step() { F(); await Task.Yield(); G(); } " +
                                 "private Task Helper() => System.IO.File.WriteAllTextAsync(\"a\", \"b\");"));

        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Wait_on_an_async_call_result_orders_its_tail()
    {
        var run = Analyze(Worker("var t = Step(); t.Wait(); P1();",
                                 "private async Task Step() { F(); await Task.Yield(); G(); }"));

        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Async_void_call_tail_overlaps_the_caller_s_later_writes_only()
    {
        var run = Analyze(Worker("P3(); Fire(); P1();", "private async void Fire() { F(); await Task.Yield(); G(); }"));

        Assert.False(Overlap(run, "P3", "G"));
        Assert.True(Overlap(run, "P1", "G"));
        Assert.False(Overlap(run, "F", "G"));
    }

    // ---- identity ----

    [Fact]
    public void Helper_joining_its_own_spawn_called_in_a_loop_keeps_the_pair()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) await Step();",
                                 "private async Task Step() { var t = Task.Run(() => F()); await t; P1(); }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Field_handle_awaited_in_a_method_called_on_every_path_is_a_join()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); await Drain(); P1();",
                                 "private Task? _work; private async Task Drain() { await _work!; }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Field_handle_from_two_spawn_sites_is_not_proven()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); if (stoppingToken.CanBeCanceled) _work = Task.Run(() => G()); await Drain(); P1();",
                                 "private Task? _work; private async Task Drain() { await _work!; }"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(run.Counter(OrderingCounters.UNPROVEN_JOINS) >= 1);
    }

    [Fact]
    public void Local_handle_from_two_spawn_sites_is_not_proven()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); if (stoppingToken.CanBeCanceled) t = Task.Run(() => G()); await t; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Spawn_in_a_loop_overlaps_itself_although_each_is_awaited()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) { var t = Task.Run(() => F()); await t; }"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Joined_spawn_in_an_http_action_keeps_the_pair()
    {
        var run = Analyze(Action("var t = Task.Run(() => state.F()); await t; state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Write_before_a_spawn_in_an_http_action_overlaps_the_work_of_another_request()
    {
        var run = Analyze(Action("state.P1(); var t = Task.Run(() => state.F()); await t;"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    // ---- groups ----

    [Fact]
    public void When_all_of_listed_tasks_orders_the_write_after_it_but_not_the_tasks_between_them()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var b = Task.Run(() => G()); await Task.WhenAll(a, b); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.False(Overlap(run, "P1", "G"));
        Assert.True(Overlap(run, "F", "G"));
    }

    [Fact]
    public void When_all_of_a_list_orders_nothing_and_is_an_unproven_join()
    {
        var run = Analyze(Worker("var tasks = new System.Collections.Generic.List<Task> { Task.Run(() => F()) }; await Task.WhenAll(tasks); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void When_any_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); await Task.WhenAny(a); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_all_of_an_array_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var b = Task.Run(() => G()); Task.WaitAll(new[] { a, b }); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Wait_all_with_a_timeout_in_a_try_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); try { Task.WaitAll(new[] { a }, 100); } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_all_with_a_token_in_a_try_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); try { Task.WaitAll(new[] { a }, stoppingToken); } catch (Exception) { } P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Continuation_of_a_when_all_is_a_composite_task_that_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var b = Task.Run(() => G()); await Task.WhenAll(a, b).ContinueWith(done => { }); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Task_a_non_async_task_run_delegate_returns_is_a_composite_that_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var b = Task.Run(() => G()); await Task.Run(() => Task.WhenAll(a, b)); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    // ---- parallel ----

    [Fact]
    public void Parallel_for_orders_the_write_after_it_and_its_body_overlaps_itself()
    {
        var run = Analyze(Worker("P2(); Parallel.For(0, 2, index => F()); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.False(Overlap(run, "P2", "F"));
        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Parallel_for_whose_body_throws_into_a_catch_still_orders_the_write_after_it()
    {
        var run = Analyze(Worker("try { Parallel.For(0, 2, index => { F(); throw new InvalidOperationException(); }); } catch (AggregateException) { } P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parallel_for_each_orders_the_write_after_it()
    {
        var run = Analyze(Worker("Parallel.ForEach(new[] { 1, 2 }, item => F()); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Parallel_for_in_an_http_action_overlaps_the_write_after_it()
    {
        var run = Analyze(Action("Parallel.For(0, 2, index => state.F()); state.P1(); await Task.CompletedTask;"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    // ---- continuations ----

    [Fact]
    public void Continuation_follows_its_antecedent_but_overlaps_the_parent()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); a.ContinueWith(done => G()); P1();"));

        Assert.False(Overlap(run, "F", "G"));
        Assert.True(Overlap(run, "P1", "G"));
        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Continuation_of_a_receiver_that_may_come_from_an_opaque_call_is_not_ordered()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var chosen = stoppingToken.CanBeCanceled ? a : Task.Delay(1); chosen.ContinueWith(done => G());"));

        Assert.True(Overlap(run, "F", "G"));
    }

    [Fact]
    public void When_all_that_may_come_from_an_opaque_call_orders_nothing()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); var chosen = stoppingToken.CanBeCanceled ? Task.WhenAll(a) : Task.Delay(1); await chosen; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Unwrap_of_a_task_that_may_come_from_an_opaque_call_orders_nothing()
    {
        var run = Analyze(Worker("var known = Task.Factory.StartNew(async () => { await Task.Yield(); F(); }); " +
                                 "var chosen = stoppingToken.CanBeCanceled ? known : Task.FromResult(Task.CompletedTask); await chosen.Unwrap(); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Continuation_of_a_receiver_from_two_tasks_is_not_ordered()
    {
        var run = Analyze(Worker("var a = Task.Run(() => F()); if (stoppingToken.CanBeCanceled) a = Task.Run(() => H()); a.ContinueWith(done => G());"));

        Assert.True(Overlap(run, "F", "G"));
    }

    [Fact]
    public void Continuation_of_an_antecedent_spawned_in_a_loop_is_not_ordered()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) { var a = Task.Run(() => F()); a.ContinueWith(done => G()); }"));

        Assert.True(Overlap(run, "F", "G"));
    }

    [Fact]
    public void Continuation_in_an_http_action_is_not_ordered()
    {
        var run = Analyze(Action("var a = Task.Run(() => state.F()); await a.ContinueWith(done => state.G());"));

        Assert.True(Overlap(run, "F", "G"));
    }

    [Fact]
    public void Work_the_antecedent_detaches_is_not_ordered_with_the_continuation()
    {
        var run = Analyze(Worker("var a = Task.Run(() => { Task.Run(() => H()); }); a.ContinueWith(done => G());"));

        Assert.True(Overlap(run, "H", "G"));
    }

    [Fact]
    public void Awaited_async_continuation_leaves_its_tail_overlapping()
    {
        var run = Analyze(Worker("var a = Task.Run(() => H()); var t = a.ContinueWith(async done => { await Task.Yield(); F(); }); await t; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Async_continuation_awaited_twice_orders_its_tail()
    {
        var run = Analyze(Worker("var a = Task.Run(() => H()); var t = a.ContinueWith(async done => { await Task.Yield(); F(); }); await (await t); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Unwrapped_async_continuation_orders_its_tail()
    {
        var run = Analyze(Worker("var a = Task.Run(() => H()); var t = a.ContinueWith(async done => { await Task.Yield(); F(); }); await t.Unwrap(); P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    // ---- nesting ----

    [Fact]
    public void Spawn_joined_on_one_branch_inside_joined_work_overlaps_the_parent_s_later_write()
    {
        var run = Analyze(Worker("var outer = Task.Run(() => { var inner = Task.Run(() => F()); if (stoppingToken.CanBeCanceled) inner.Wait(); }); " +
                                 "await outer; P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Join_after_the_first_await_of_an_async_tail_orders_the_write_after_it()
    {
        var run = Analyze(Worker("_ = Step();", "private async Task Step() { await Task.Yield(); var inner = Task.Run(() => F()); await inner; P1(); }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Spawn_joined_inside_joined_work_precedes_the_parent_s_later_write()
    {
        var run = Analyze(Worker("var t = Task.Run(() => { var inner = Task.Run(() => G()); inner.Wait(); }); await t; P1();"));

        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Fire_and_forget_inside_joined_work_is_not_bounded_by_its_end()
    {
        var run = Analyze(Worker("var t = Task.Run(() => { Task.Run(() => G()); }); await t; P1();"));

        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Spawn_inside_work_follows_the_parent_s_write_before_the_outer_spawn()
    {
        var run = Analyze(Worker("P1(); Task.Run(() => { Task.Run(() => G()); });"));

        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Accesses_of_one_execution_are_never_ordered_away_from_a_self_pair()
    {
        var run = Analyze(Action("state.F(); await Task.CompletedTask;"));

        Assert.True(Overlap(run, "F", "F"));
    }

    // ---- startup ----

    [Fact]
    public void Startup_access_is_collected_and_does_not_overlap_an_action()
    {
        var run = Analyze(Shared("", ctor: "Shared.Value = 1;"));

        var startup = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == ExecutionModel.STARTUP);
        Assert.Equal(ExecutionKind.Startup, run.Execution.Analysis.Execution(startup.ExecutionId).Kind);
        Assert.DoesNotContain(run.PairsOn("Value"), pair => pair.First == startup || pair.Second == startup);
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_ORDERED) >= 1);
    }

    [Fact]
    public void Spawn_from_a_hosted_service_constructor_overlaps_an_action()
    {
        var run = Analyze(Shared("", ctor: "Task.Run(() => Shared.Value = 1);"));

        var spawned = Assert.Single(run.Accesses("Value"), access => run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Spawn);
        Assert.Contains(run.PairsOn("Value"), pair => pair.First == spawned != (pair.Second == spawned));
    }

    [Fact]
    public void Grandchild_of_startup_overlaps_an_action()
    {
        var run = Analyze(Shared("", ctor: "Task.Run(() => { Task.Run(() => Shared.Value = 1); });"));

        var spawned = Assert.Single(run.Accesses("Value"), access => run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Spawn);
        Assert.Contains(run.PairsOn("Value"), pair => pair.First == spawned != (pair.Second == spawned));
    }

    [Fact]
    public void Lazy_singleton_constructor_runs_after_startup()
    {
        var run = Analyze("""
            public static class Shared { public static int Value; }
            public sealed class Cache { public Cache() => Shared.Value = 2; }
            public sealed class Warmup : BackgroundService
            {
                public Warmup() => Shared.Value = 1;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public class GateController(Cache cache) : ControllerBase { public void Get() => GC.KeepAlive(cache); }
            """ + Startup("services.AddSingleton<Cache>(); services.AddHostedService<Warmup>();"));

        var startup = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == ExecutionModel.STARTUP);
        var lazy = Assert.Single(run.Accesses("Value"), access => run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.LazyConstruction);
        Assert.DoesNotContain(run.PairsOn("Value"), pair => pair.First == startup || pair.Second == startup);
        Assert.NotNull(lazy);
    }

    // ---- counters ----

    [Fact]
    public void Ordered_comparison_is_counted_as_its_own_skip()
    {
        var run = Analyze(Worker("P1(); var t = Task.Run(() => F()); await t; P2();"));

        Assert.Equal(2, run.Skipped(InterproceduralPairing.SKIP_ORDERED));
    }

    [Fact]
    public void Spawn_site_run_in_two_contexts_is_counted_once()
    {
        var run = Analyze("""
            public sealed class State { public int Value; public void F() => Value = 1; }
            public static class Spawner { public static void Start(State state) => Task.Run(() => state.F()); }
            public sealed class First : BackgroundService
            {
                private readonly State _state = new State();
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Spawner.Start(_state); return Task.CompletedTask; }
            }
            public sealed class Second : BackgroundService
            {
                private readonly State _state = new State();
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Spawner.Start(_state); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<First>(); services.AddHostedService<Second>();"));

        Assert.True(run.Execution.Analysis.Executions.Count(execution => execution.Kind == ExecutionKind.Spawn) >= 2);
        Assert.Equal(1, run.Counter(OrderingCounters.SPAWN_SITES));
    }

    [Fact]
    public void Unproven_join_run_in_two_contexts_is_counted_once()
    {
        var run = Analyze("""
            public sealed class State { public int Value; public void F() => Value = 1; }
            public static class Spawner
            {
                public static void Start(State state)
                {
                    var tasks = new System.Collections.Generic.List<Task> { Task.Run(() => state.F()) };
                    Task.WhenAll(tasks).Wait();
                }
            }
            public sealed class First : BackgroundService
            {
                private readonly State _state = new State();
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Spawner.Start(_state); return Task.CompletedTask; }
            }
            public sealed class Second : BackgroundService
            {
                private readonly State _state = new State();
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Spawner.Start(_state); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<First>(); services.AddHostedService<Second>();"));

        Assert.Equal(1, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Proven_join_is_not_counted_as_unproven()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); await t; P1();"));

        Assert.Equal(0, run.Counter(OrderingCounters.UNPROVEN_JOINS));
        Assert.Equal(1, run.Counter(OrderingCounters.SPAWN_SITES));
    }

    [Fact]
    public void Startup_construction_counter_is_gone()
    {
        var run = Analyze(Shared("", ctor: "Shared.Value = 1;"));

        Assert.DoesNotContain("startup-construction-access", run.Collection.Coverage.Counters.Keys);
        Assert.DoesNotContain("startup-construction-access", typeof(CoverageCounters).GetFields().Select(field => (string)field.GetRawConstantValue()!));
    }

    [Fact]
    public void Call_where_only_one_implementation_waits_orders_nothing()
    {
        var run = Analyze(Waiters(WAITING_FIRST + SKIPPING_SECOND, "await waiter.WaitAsync();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_where_every_implementation_waits_orders_the_write_after_it()
    {
        var run = Analyze(Waiters(WAITING_FIRST + WAITING_SECOND, "await waiter.WaitAsync();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_that_mixes_a_wait_and_an_await_the_caller_catches_a_throw_of_orders_nothing()
    {
        var run = Analyze(Waiters(WAITING_FIRST + AWAITING_SECOND, "try { await waiter.WaitAsync(); } catch (Exception) { }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Joins_over_unknown_handles_are_counted_once_by_operation()
    {
        var run = Analyze(Worker("_ = Wait(_factory.Make()); _ = Wait(_factory.Make());",
                                 """
                                 private readonly IFactory _factory = new Factory();
                                 private async Task Wait(Thread thread) { await _factory.Get(); thread.Join(); }
                                 private interface IFactory { Task Get(); Thread Make(); }
                                 private sealed class Factory : IFactory
                                 {
                                     private Task? _task;
                                     private Thread? _thread;
                                     public Task Get() => _task!;
                                     public Thread Make() => _thread!;
                                 }
                                 """));

        Assert.Equal(2, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Tail_after_alternative_awaits_of_different_methods_orders_neither_task()
    {
        var run = Analyze(Worker("_ = Step();",
                                 "private readonly bool _flag; private Task? _a; private Task? _b; " +
                                 "private async Task Step() { _a = Task.Run(() => F()); _b = Task.Run(() => G()); " +
                                 "if (_flag) await WaitA(); else await WaitB(); P1(); } " +
                                 "private async Task WaitA() { await _a!; } " +
                                 "private async Task WaitB() { await _b!; }"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Tail_after_alternative_awaits_of_the_same_task_orders_it()
    {
        var run = Analyze(Worker("_ = Step();",
                                 "private readonly bool _flag; private Task? _a; " +
                                 "private async Task Step() { _a = Task.Run(() => F()); " +
                                 "if (_flag) await WaitA(); else await WaitB(); P1(); } " +
                                 "private async Task WaitA() { await _a!; } " +
                                 "private async Task WaitB() { await _a!; }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_helper_that_may_return_an_unknown_task_orders_nothing()
    {
        var run = Analyze(Worker("_helper.Known = Work(); await _helper.Get(); P1();", HELPER));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_helper_that_returns_the_known_task_orders_the_work()
    {
        var run = Analyze(Worker("_helper.Known = Work(); await _helper.Take(); P1();", HELPER));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_on_a_receiver_an_opaque_call_may_have_made_orders_nothing()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); var waiting = new Waiting(t); " +
                                 "IWaiter w = _flag ? waiting : _factory.Get(); w.Drain(); P1();", WAITERS));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_on_a_receiver_the_heap_names_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); var waiting = new Waiting(t); IWaiter w = waiting; w.Drain(); P1();", WAITERS));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_on_every_branch_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var t = Task.Run(() => F()); if (_flag) t.Wait(); else t.Wait(); P1();", "private readonly bool _flag;"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Call_that_joins_on_every_branch_orders_the_write_after_it()
    {
        var run = Analyze(Worker("_work = Task.Run(() => F()); Drain(); P1();",
                                 "private Task? _work; private readonly bool _flag; " +
                                 "private void Drain() { if (_flag) _work!.Wait(); else _work!.Wait(); }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Await_of_a_conditional_between_two_async_calls_orders_both_tails()
    {
        var run = Analyze(Worker("await (_flag ? AAsync() : BAsync()); P1();", ASYNC_CALLS));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.False(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Task_of_a_conditional_kept_in_a_local_leaves_both_tails_overlapping()
    {
        var run = Analyze(Worker("var t = _flag ? AAsync() : BAsync(); P1(); await t;", ASYNC_CALLS));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Await_of_a_helper_that_may_return_null_orders_nothing()
    {
        var run = Analyze(Worker("_helper.Known = Work(); try { await _helper.Maybe(); } catch (Exception) { } P1();", HELPER));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.Equal(2, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Await_of_a_task_a_dispatch_the_heap_cannot_resolve_returned_orders_nothing()
    {
        var run = Analyze(Worker("_provider.Known = Work(); " +
                                 "IProvider provider = _flag ? _provider : System.Activator.CreateInstance<IProvider>(); " +
                                 "await provider.Get(); P1();", PROVIDER));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Continuation_of_a_composite_task_is_unrecognized_and_overlaps_itself()
    {
        var run = Analyze(Worker("var a = Task.Run(() => P2()); var b = Task.Run(() => P3()); " +
                                 "var chain = Task.WhenAll(a, b).ContinueWith(async done => { await Task.Yield(); F(); }); " +
                                 "await chain.Unwrap(); P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "F", "F"));
    }

    private static bool Overlap(EngineRun run, string one, string other)
    {
        Assert.Contains(run.Accesses("Value"), access => Is(access, one));
        Assert.Contains(run.Accesses("Value"), access => Is(access, other));
        return run.PairsOn("Value").Any(pair => Is(pair.First, one) && Is(pair.Second, other) || Is(pair.First, other) && Is(pair.Second, one));
    }

    private static bool Is(Access access, string helper) => access.Symbol.EndsWith($".{helper}()", StringComparison.Ordinal);

    private const string HELPERS = """
        public int Value;
        public void P1() => Value = 1;
        public void P2() => Value = 2;
        public void P3() => Value = 3;
        public void F() => Value = 4;
        public void G() => Value = 5;
        public void H() => Value = 6;
        """;

    private static string Worker(string body, string members = "") => $$"""
        public sealed class Worker : BackgroundService
        {
            {{HELPERS}}
            {{members}}
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
        }
        """ + Startup("services.AddHostedService<Worker>();");

    /// <summary>A worker that hands its spawned task to both implementations of <c>IWaiter</c> and calls one of them; the call runs in the
    /// worker's tail, so an implementation that awaits the task waits inside the call.</summary>
    private static string Waiters(string implementations, string call) => $$"""
        public interface IWaiter { Task WaitAsync(); }

        {{implementations}}

        public sealed class Worker : BackgroundService
        {
            {{HELPERS}}
            private readonly bool _flag;
            private readonly FirstWaiter _first = new FirstWaiter();
            private readonly SecondWaiter _second = new SecondWaiter();

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var work = Task.Run(() => F());
                _first.Work = work;
                _second.Work = work;
                IWaiter waiter = _flag ? _first : (IWaiter)_second;
                await Task.Yield();
                {{call}}
                P1();
            }
        }
        """ + Startup("services.AddHostedService<Worker>();");

    private const string WAITING_FIRST = """
        public sealed class FirstWaiter : IWaiter
        {
            public Task? Work;
            public Task WaitAsync() { Work!.Wait(); return Task.CompletedTask; }
        }
        """;

    private const string WAITING_SECOND = """
        public sealed class SecondWaiter : IWaiter
        {
            public Task? Work;
            public Task WaitAsync() { Work!.Wait(); return Task.CompletedTask; }
        }
        """;

    private const string SKIPPING_SECOND = """
        public sealed class SecondWaiter : IWaiter
        {
            public Task? Work;
            public Task WaitAsync() => Task.CompletedTask;
        }
        """;

    private const string AWAITING_SECOND = """
        public sealed class SecondWaiter : IWaiter
        {
            public Task? Work;
            public async Task WaitAsync() => await Work!;
        }
        """;

    /// <summary>A helper whose <c>Get</c> returns either the task it was handed or one from an opaque call, and whose <c>Take</c> returns
    /// only the one it was handed; the task handed to it is an async call whose tail writes.</summary>
    private const string HELPER = """
        private readonly Helper _helper = new Helper();
        private async Task Work() { await Task.Yield(); F(); }
        public sealed class Helper
        {
            public bool Flag;
            public Task? Known;
            public Task Get() => Flag ? Known! : System.IO.File.WriteAllTextAsync("a", "b");
            public Task Take() => Known!;
            public Task Maybe() => Flag ? Known! : null!;
        }
        """;

    /// <summary>An interface with an implementation in the program, handed out through a receiver the heap cannot follow.</summary>
    private const string PROVIDER = """
        private readonly bool _flag;
        private readonly Provider _provider = new Provider();
        private async Task Work() { await Task.Yield(); F(); }
        public interface IProvider { Task Get(); }
        public sealed class Provider : IProvider
        {
            public Task? Known;
            public Task Get() => Known!;
        }
        """;

    /// <summary>An interface whose known implementation waits for the task it was built with, and a factory whose implementation the heap
    /// cannot name.</summary>
    private const string WAITERS = """
        private readonly bool _flag;
        private readonly WaiterFactory _factory = new WaiterFactory();
        public interface IWaiter { void Drain(); }
        public sealed class Waiting(Task work) : IWaiter { public void Drain() => work.Wait(); }
        public sealed class WaiterFactory { public IWaiter Get() => System.Activator.CreateInstance<IWaiter>(); }
        """;

    /// <summary>Two async methods whose tails write.</summary>
    private const string ASYNC_CALLS = """
        private readonly bool _flag;
        private async Task AAsync() { await Task.Yield(); F(); }
        private async Task BAsync() { await Task.Yield(); G(); }
        """;

    private static string Action(string body) => $$"""
        public sealed class State
        {
            {{HELPERS}}
        }
        public class GateController(State state) : ControllerBase
        {
            public async Task Post()
            {
                {{body}}
            }
        }
        """ + Startup("services.AddSingleton<State>();");

    private static string Shared(string startup, string ctor) => $$"""
        public static class Shared { public static int Value; }
        public sealed class Warmup : BackgroundService
        {
            public Warmup() { {{ctor}} }
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
        public class GateController : ControllerBase { public void Post() => Shared.Value = 3; }
        """ + Startup(startup + " services.AddHostedService<Warmup>();");
}
