using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>Golden tests of the BCL spawn, join and timer operations, as <see cref="IrPrinter"/> prints them for <c>C.M</c> and
/// its nested bodies: operation id and text, without provenance.</summary>
public sealed class IrSpawnLoweringTests
{
    private static readonly string[] Forms = ["spawn ", "thread-work ", "join ", "when-all ", "unwrap ", "timer "];

    [Fact]
    public async Task Task_run_is_a_spawn_whose_handle_is_its_task()
    {
        Expect(
            [
                "2 spawn TaskRun call=operation:1 handle=%4 work=[%3] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { var t = Task.Run(() => { }); }"));
    }

    [Fact]
    public async Task Task_run_with_an_async_lambda_waits_for_the_lambda_task()
    {
        Expect(
            [
                "2 spawn TaskRun call=operation:1 handle=%4 work=[%3] work-method=- state=- antecedent=- async=true awaits-work=true implicit-join=false"
            ],
            await Bcl("void M() { var t = Task.Run(async () => await Task.Yield()); }"));
    }

    [Fact]
    public async Task Task_run_of_a_value_task_lambda_does_not_wait_for_it()
    {
        Expect(
            [
                "2 spawn TaskRun call=operation:1 handle=%4 work=[%3] work-method=- state=- antecedent=- async=true awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { var t = Task.Run(async ValueTask () => await Task.Yield()); }"));
    }

    [Fact]
    public async Task Start_new_is_a_spawn_with_its_state()
    {
        Expect(
            [
                "3 spawn StartNew call=operation:2 handle=%6 work=[%5] work-method=- state=%1 antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(object box) { var t = Task.Factory.StartNew(state => { }, box); }"));
    }

    [Fact]
    public async Task Start_new_with_an_async_lambda_hands_out_the_outer_task_and_unwrap_names_the_tail()
    {
        Expect(
            [
                "3 spawn StartNew call=operation:2 handle=%6 work=[%5] work-method=- state=- antecedent=- async=true awaits-work=false implicit-join=false",
                "6 unwrap %7 <- %1"
            ],
            await Bcl("void M() { var outer = Task.Factory.StartNew(async () => await Task.Yield()); var tail = outer.Unwrap(); }"));
    }

    [Fact]
    public async Task Continue_with_names_its_receiver_as_the_antecedent()
    {
        Expect(
            [
                "2 spawn ContinueWith call=operation:1 handle=%5 work=[%4] work-method=- state=- antecedent=%1 async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(Task first) { var next = first.ContinueWith(done => { }); }"));
    }

    [Fact]
    public async Task Queue_user_work_item_has_no_handle()
    {
        Expect(
            [
                "2 spawn QueueUserWorkItem call=operation:1 handle=- work=[%1] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { ThreadPool.QueueUserWorkItem(_ => { }); }"));
    }

    [Fact]
    public async Task Generic_queue_user_work_item_with_prefer_local_passes_its_state()
    {
        Expect(
            [
                "2 spawn QueueUserWorkItem call=operation:1 handle=- work=[%2] work-method=- state=%1 antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(string text) { ThreadPool.QueueUserWorkItem(value => { }, text, preferLocal: true); }"));
    }

    [Fact]
    public async Task Unsafe_queue_user_work_item_passes_its_state()
    {
        Expect(
            [
                "2 spawn UnsafeQueueUserWorkItem call=operation:1 handle=- work=[%2] work-method=- state=%1 antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(object box) { ThreadPool.UnsafeQueueUserWorkItem(_ => { }, box); }"));
    }

    [Fact]
    public async Task Unsafe_queue_user_work_item_of_a_work_item_calls_its_execute()
    {
        Expect(
            [
                "4 spawn UnsafeQueueUserWorkItem call=operation:3 handle=- work=[%2] work-method=\"System.Threading.IThreadPoolWorkItem.Execute()\" state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl(
                "sealed class Item : IThreadPoolWorkItem { public void Execute() { } } void M() { ThreadPool.UnsafeQueueUserWorkItem(new Item(), false); }"));
    }

    [Fact]
    public async Task Thread_constructor_binds_the_work_and_start_spawns_it_with_the_thread_as_handle()
    {
        Expect(
            [
                "3 thread-work %3 work=%4 async=false",
                "6 spawn ThreadStart call=operation:5 handle=%1 work=[] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { var thread = new Thread(() => { }); thread.Start(); }"));
    }

    [Fact]
    public async Task Thread_start_with_a_parameter_passes_it_as_state()
    {
        Expect(
            [
                "3 thread-work %4 work=%5 async=false",
                "6 spawn ThreadStart call=operation:5 handle=%2 work=[] work-method=- state=%1 antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(object box) { var thread = new Thread(value => { }, 4096); thread.Start(box); }"));
    }

    [Fact]
    public async Task Parallel_for_joins_when_it_returns()
    {
        Expect(
            [
                "2 spawn ParallelFor call=operation:1 handle=%4 work=[%3] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=true"
            ],
            await Bcl("int _total; void M() { Parallel.For(0, 4, i => _total += i); }"));
    }

    [Fact]
    public async Task Parallel_for_with_local_state_spawns_its_three_delegates()
    {
        Expect(
            [
                "6 spawn ParallelFor call=operation:5 handle=%7 work=[%4,%5,%6] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=true"
            ],
            await Bcl(
                "void M() { Parallel.For(0, 4, new ParallelOptions(), () => 0, (i, state, local) => local + i, local => { }); }"));
    }

    [Fact]
    public async Task Parallel_for_each_joins_when_it_returns()
    {
        Expect(
            [
                "3 spawn ParallelForEach call=operation:2 handle=%4 work=[%3] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=true"
            ],
            await Bcl("void M(List<string> items) { Parallel.ForEach(items, item => { }); }"));
    }

    [Fact]
    public async Task Parallel_for_each_async_waits_for_its_async_body()
    {
        Expect(
            [
                "3 spawn ParallelForEachAsync call=operation:2 handle=%4 work=[%3] work-method=- state=- antecedent=- async=true awaits-work=true implicit-join=false"
            ],
            await Bcl(
                "async Task M(List<int> items) { await Parallel.ForEachAsync(items, async (item, token) => await Task.Delay(item, token)); }"));
    }

    [Fact]
    public async Task Async_lambda_in_a_thread_is_not_waited_for()
    {
        Expect(
            [
                "3 thread-work %3 work=%4 async=true",
                "6 spawn ThreadStart call=operation:5 handle=%1 work=[] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { var thread = new Thread(async () => await Task.Yield()); thread.Start(); }"));
    }

    [Fact]
    public async Task Async_lambda_in_parallel_for_is_not_waited_for()
    {
        Expect(
            [
                "2 spawn ParallelFor call=operation:1 handle=%4 work=[%3] work-method=- state=- antecedent=- async=true awaits-work=false implicit-join=true"
            ],
            await Bcl("void M() { Parallel.For(0, 4, async i => await Task.Yield()); }"));
    }

    [Fact]
    public async Task Dropped_async_call_is_a_call_not_awaited_to_an_async_task_body()
    {
        var lowered = await Lower("void M() { _ = Save(); } async Task Save() => await Task.Yield();");

        Expect([], await Bcl("void M() { _ = Save(); } async Task Save() => await Task.Yield();"));
        var call = Assert.Single(Operations<IrCallOperation>(lowered.Body), call => call.Method == "C.Save()");
        Assert.False(call.IsAwaitedImmediately);
    }

    [Fact]
    public async Task Async_calls_awaited_through_a_conditional_are_marked_awaited()
    {
        const string Members = "async Task M(bool flag) { await (flag ? First() : Second()); } " +
                               "async Task First() => await Task.Yield(); async Task Second() => await Task.Yield();";
        var lowered = await Lower(Members);

        Assert.All(Operations<IrCallOperation>(lowered.Body).Where(call => call.Method is "C.First()" or "C.Second()"),
                   call => Assert.True(call.IsAwaitedImmediately));
    }

    [Fact]
    public async Task Async_void_call_is_a_call_to_an_async_body_returning_void()
    {
        var lowered = await Lower("void M() { Notify(); } async void Notify() => await Task.Yield();");
        var notify = await LowerMember("void M() { Notify(); } async void Notify() => await Task.Yield();", "Notify");

        Assert.False(Assert.Single(Operations<IrCallOperation>(lowered.Body)).IsAwaitedImmediately);
        Assert.StartsWith("body \"body:Fixture:M:C.Notify\" Method owner=\"C.Notify()\" method=\"C.Notify()\" schema=\"1.2\" " +
                          "async=true returns=\"void\" async-iterator=false", IrPrinter.Print(notify.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Immediately_awaited_async_call_is_an_ordinary_call_marked_awaited()
    {
        const string Members = "async Task M() { await Save(); await Save().ConfigureAwait(false); } async Task Save() => await Task.Yield();";
        var lowered = await Lower(Members);

        Expect([], await Bcl(Members));
        Assert.All(Operations<IrCallOperation>(lowered.Body).Where(call => call.Method == "C.Save()"),
                   call => Assert.True(call.IsAwaitedImmediately));
        Assert.StartsWith("body \"body:Fixture:M:C.M\" Method owner=\"C.M()\" method=\"C.M()\" schema=\"1.2\" " +
                          "async=true returns=\"System.Threading.Tasks.Task\" async-iterator=false",
                          IrPrinter.Print(lowered.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Async_iterator_body_is_marked()
    {
        var lowered = await Lower("async IAsyncEnumerable<int> M() { await Task.Yield(); yield return 1; }");

        Assert.True(lowered.Body.IsAsync);
        Assert.True(lowered.Body.IsAsyncIterator);
        Assert.Equal("System.Collections.Generic.IAsyncEnumerable<int>", lowered.Body.ReturnType);
    }

    [Fact]
    public async Task Task_wait_is_a_join_that_may_throw_before_completion()
    {
        Expect(
            [
                "1 join Wait call=operation:0 handles=[%1] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("void M(Task task) { task.Wait(); }"));
    }

    [Fact]
    public async Task Task_wait_with_a_timeout_is_not_a_join()
    {
        Expect([], await Bcl("void M(Task task) { task.Wait(100); task.Wait(TimeSpan.FromSeconds(1)); }"));
    }

    [Fact]
    public async Task Task_wait_with_a_cancellation_token_is_not_a_join()
    {
        Expect([], await Bcl("void M(Task task, CancellationToken token) { task.Wait(token); }"));
    }

    [Fact]
    public async Task Thread_join_is_a_join()
    {
        Expect(
            [
                "1 join Join call=operation:0 handles=[%1] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("void M(Thread thread) { thread.Join(); }"));
    }

    [Fact]
    public async Task Thread_join_with_a_timeout_is_not_a_join()
    {
        Expect([], await Bcl("void M(Thread thread) { thread.Join(100); }"));
    }

    [Fact]
    public async Task Wait_all_with_listed_tasks_joins_them()
    {
        Expect(
            [
                "2 join WaitAll call=operation:1 handles=[%1,%2] handles-known=true throws-only-after-completion=false",
                "7 join WaitAll call=operation:6 handles=[%2,%1] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("void M(Task first, Task second) { Task.WaitAll(first, second); Task.WaitAll(new[] { second, first }); }"));
    }

    [Fact]
    public async Task Wait_all_with_a_collection_expression_joins_its_elements()
    {
        Expect(
            [
                "3 join WaitAll call=operation:2 handles=[%1,%2] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("void M(Task first, Task second) { Task.WaitAll([first, second]); }"));
    }

    [Fact]
    public async Task Wait_all_with_an_array_variable_does_not_know_its_tasks()
    {
        Expect(
            [
                "1 join WaitAll call=operation:0 handles=[] handles-known=false throws-only-after-completion=false"
            ],
            await Bcl("void M(Task[] tasks) { Task.WaitAll(tasks); }"));
    }

    [Fact]
    public async Task Wait_all_with_a_timeout_is_not_a_join()
    {
        Expect([], await Bcl("void M(Task first) { Task.WaitAll(new[] { first }, 100); }"));
    }

    [Fact]
    public async Task Wait_all_with_a_cancellation_token_is_not_a_join()
    {
        Expect([], await Bcl("void M(Task first, CancellationToken token) { Task.WaitAll(new[] { first }, token); }"));
    }

    [Fact]
    public async Task When_all_with_listed_tasks_names_them_and_its_await_is_an_await()
    {
        Expect(
            [
                "3 when-all %5 tasks=[%1,%3] tasks-known=true"
            ],
            await Bcl("async Task M(Task first, Task<int> second) { await Task.WhenAll(first, second); }"));
    }

    [Fact]
    public async Task When_all_with_a_list_does_not_know_its_tasks()
    {
        Expect(
            [
                "2 when-all %3 tasks=[] tasks-known=false"
            ],
            await Bcl("async Task M(List<Task> tasks) { await Task.WhenAll(tasks); }"));
    }

    [Fact]
    public async Task When_any_is_an_ordinary_call()
    {
        Expect([], await Bcl("async Task M(Task first, Task second) { await Task.WhenAny(first, second); }"));
    }

    [Fact]
    public async Task Await_of_configure_await_names_the_awaited_task()
    {
        var lowered = await Lower("async Task M() { var t = Task.Run(() => { }); await t.ConfigureAwait(false); }");

        var run = Assert.Single(Operations<IrCallOperation>(lowered.Body), call => call.Method.StartsWith("System.Threading.Tasks.Task.Run(", StringComparison.Ordinal));
        var spawn = Assert.Single(Operations<IrSpawnOperation>(lowered.Body));
        var awaiting = Assert.Single(Operations<IrAwaitOperation>(lowered.Body));
        Assert.Equal(run.ResultValue, spawn.HandleValue);
        Assert.NotNull(awaiting.TaskValue);
        Assert.NotEqual(awaiting.AwaitableValue, awaiting.TaskValue);
        Assert.Matches(@"await %\d+ result=- task=%\d+", Print(lowered));
    }

    [Fact]
    public async Task Timer_with_zero_due_time_and_a_positive_period()
    {
        Expect(
            [
                "4 timer Create %3 callback=%4 state=%6 due=Zero period=Positive wait-handle=- result=- flag=-"
            ],
            await Bcl("void M() { var timer = new Timer(_ => { }, null, 0, 1000); }"));
    }

    [Fact]
    public async Task Timer_with_infinite_due_time_and_period()
    {
        Expect(
            [
                "4 timer Create %3 callback=%4 state=%6 due=Infinite period=Infinite wait-handle=- result=- flag=-"
            ],
            await Bcl("void M() { var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite); }"));
    }

    [Fact]
    public async Task Timer_with_a_positive_due_time_and_infinite_period()
    {
        Expect(
            [
                "5 timer Create %3 callback=%4 state=%6 due=Positive period=Infinite wait-handle=- result=- flag=-"
            ],
            await Bcl("void M() { var timer = new Timer(_ => { }, null, 500L, -1L); }"));
    }

    [Fact]
    public async Task Timer_with_non_constant_intervals_is_unknown()
    {
        Expect(
            [
                "4 timer Create %5 callback=%6 state=%8 due=Unknown period=Positive wait-handle=- result=- flag=-",
                "13 timer Create %10 callback=%11 state=%13 due=Unknown period=Unknown wait-handle=- result=- flag=-"
            ],
            await Bcl(
                "void M(int due) { var first = new Timer(_ => { }, null, due, 1000); var second = new Timer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1)); }"));
    }

    [Fact]
    public async Task Timer_with_only_a_callback_is_its_own_state_and_never_due()
    {
        Expect(
            [
                "3 timer Create %3 callback=%4 state=%3 due=Infinite period=Infinite wait-handle=- result=- flag=-"
            ],
            await Bcl("void M() { var timer = new Timer(_ => { }); }"));
    }

    [Fact]
    public async Task Timer_change_is_classified()
    {
        Expect(
            [
                "1 timer Change %1 callback=- state=- due=Zero period=Positive wait-handle=- result=- flag=-"
            ],
            await Bcl("void M(Timer timer) { timer.Change(0, 1000); }"));
    }

    [Fact]
    public async Task Timer_dispose_is_recorded()
    {
        Expect(
            [
                "1 timer Dispose %1 callback=- state=- due=- period=- wait-handle=- result=- flag=-"
            ],
            await Bcl("void M(Timer timer) { timer.Dispose(); }"));
    }

    [Fact]
    public async Task Timer_dispose_async_records_its_task()
    {
        Expect(
            [
                "1 timer DisposeAsync %1 callback=- state=- due=- period=- wait-handle=- result=%2 flag=-"
            ],
            await Bcl("async Task M(Timer timer) { await timer.DisposeAsync(); }"));
    }

    [Fact]
    public async Task Timer_dispose_with_a_wait_handle_and_wait_one_are_a_disposal_and_a_wait()
    {
        Expect(
            [
                "5 timer DisposeWaitHandle %1 callback=- state=- due=- period=- wait-handle=%6 result=- flag=-",
                "7 join WaitOne call=operation:6 handles=[%2] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("void M(Timer timer) { var handle = new ManualResetEvent(false); timer.Dispose(handle); handle.WaitOne(); }"));
    }

    [Fact]
    public async Task Wait_one_with_a_timeout_is_not_a_wait()
    {
        Expect([], await Bcl("void M(WaitHandle handle) { handle.WaitOne(100); }"));
    }

    [Fact]
    public async Task Wait_one_on_a_derived_wait_handle_without_override_is_a_wait()
    {
        Expect(
            [
                "1 join WaitOne call=operation:0 handles=[%1] handles-known=true throws-only-after-completion=false"
            ],
            await Bcl("sealed class Signal : WaitHandle { } void M(Signal signal) { signal.WaitOne(); }"));
    }

    [Fact]
    public async Task Wait_one_overridden_by_a_user_type_is_not_a_wait()
    {
        Expect([], await Bcl("sealed class Signal : WaitHandle { public override bool WaitOne() => true; } void M(Signal signal) { signal.WaitOne(); }"));
    }

    [Fact]
    public async Task Timers_timer_subscription_auto_reset_enabled_start_and_stop_are_recorded()
    {
        Expect(
            [
                "7 timer ElapsedSubscribe %2 callback=%8 state=- due=- period=- wait-handle=- result=- flag=-",
                "9 timer SetAutoReset %2 callback=- state=- due=- period=- wait-handle=- result=- flag=True",
                "11 timer SetEnabled %2 callback=- state=- due=- period=- wait-handle=- result=- flag=Unknown",
                "13 timer Start %2 callback=- state=- due=- period=- wait-handle=- result=- flag=-",
                "15 timer Stop %2 callback=- state=- due=- period=- wait-handle=- result=- flag=-"
            ],
            await Bcl("""
            void M(bool flag)
            {
                var timer = new System.Timers.Timer(1000);
                timer.Elapsed += (sender, e) => { };
                timer.AutoReset = true;
                timer.Enabled = flag;
                timer.Start();
                timer.Stop();
            }
            """));
    }

    [Fact]
    public async Task Periodic_timer_is_not_a_spawn()
    {
        Expect([], await Bcl(
            "async Task M(CancellationToken token) { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); await timer.WaitForNextTickAsync(token); }"));
    }

    [Fact]
    public async Task User_task_run_is_an_ordinary_call()
    {
        Expect([], await Bcl("void M() { System.Threading.Tasks.Task.Run(() => { }); }",
                                   "namespace System.Threading.Tasks { public static class Task { public static void Run(Action work) { } } }"));
    }

    [Fact]
    public async Task User_thread_start_and_join_are_ordinary_calls()
    {
        Expect([], await Bcl("void M() { var thread = new System.Threading.Thread(() => { }); thread.Start(); thread.Join(); }",
                                   "namespace System.Threading { public sealed class Thread { public Thread(Action work) { } public void Start() { } public void Join() { } } }"));
    }

    [Fact]
    public async Task User_thread_pool_queue_is_an_ordinary_call()
    {
        Expect([], await Bcl("void M() { System.Threading.ThreadPool.QueueUserWorkItem(_ => { }); }",
                                   "namespace System.Threading { public static class ThreadPool { public static void QueueUserWorkItem(Action<object?> work) { } } }"));
    }

    [Fact]
    public async Task User_parallel_for_is_an_ordinary_call()
    {
        Expect([], await Bcl("void M() { System.Threading.Tasks.Parallel.For(0, 4, i => { }); }",
                                   "namespace System.Threading.Tasks { public static class Parallel { public static void For(int from, int to, Action<int> body) { } } }"));
    }

    [Fact]
    public async Task User_timer_construction_change_and_dispose_are_ordinary_calls()
    {
        Expect([], await Bcl(
            "void M() { var timer = new System.Threading.Timer(_ => { }, null, 0, 1000); timer.Change(0, 1000); timer.Dispose(); }",
            """
            namespace System.Threading
            {
                public sealed class Timer
                {
                    public Timer(Action<object?> callback, object? state, int dueTime, int period) { }
                    public void Change(int dueTime, int period) { }
                    public void Dispose() { }
                }
            }
            """));
    }

    [Fact]
    public async Task Unrelated_user_wait_one_is_not_a_wait()
    {
        Expect([], await Bcl("sealed class Gate { public bool WaitOne() => true; } void M(Gate gate) { gate.WaitOne(); }"));
    }

    [Fact]
    public async Task Unrecognized_form_of_a_recognized_type_spawns_its_delegates_unordered()
    {
        Expect(
            [
                "6 spawn Unrecognized call=operation:5 handle=- work=[%6,%8] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false",
                "10 spawn Unrecognized call=operation:9 handle=- work=[%11] work-method=- state=- antecedent=- async=false awaits-work=false implicit-join=false"
            ],
            await Bcl("void M() { Parallel.Invoke(() => { }, () => { }); var task = new Task(() => { }); }"));
    }

    [Fact]
    public async Task Delegate_handed_to_a_framework_method_of_another_type_is_an_ordinary_call()
    {
        Expect([], await Bcl("void M(List<int> items) { var doubled = System.Linq.Enumerable.Select(items, item => item * 2); }"));
    }

    [Fact]
    public async Task Continue_with_an_async_delegate_on_when_all_hands_out_the_outer_task()
    {
        Expect(
            [
                "2 when-all %6 tasks=[%1,%2] tasks-known=true",
                "5 spawn Unrecognized call=operation:4 handle=- work=[%7] work-method=- state=- antecedent=- async=true awaits-work=false implicit-join=false"
            ],
            await Bcl("void M(Task first, Task second) { var next = Task.WhenAll(first, second).ContinueWith(async done => await Task.Yield()); }"));
    }

    [Fact]
    public async Task Operations_in_a_try_block_reach_the_catch()
    {
        var lowered = await Lower("""
            async Task M(Task task)
            {
                try
                {
                    Risky();
                    await task;
                }
                catch (InvalidOperationException)
                {
                }
            }
            static void Risky() { }
            """);
        var body = lowered.Body;

        var awaitBlock = Assert.Single(body.Blocks, block => block.Operations.OfType<IrAwaitOperation>().Any());
        var tryRegion = body.Regions.Single(region => region.Kind == IrRegionKind.Try);
        var catchRegion = body.Regions.Single(region => region.Kind == IrRegionKind.Catch);
        Assert.InRange(awaitBlock.Ordinal, tryRegion.FirstBlockOrdinal, tryRegion.LastBlockOrdinal);
        Assert.Contains(new IrFlowPredecessor(awaitBlock.Ordinal, IrEdgeKind.Exceptional),
                        body.Blocks[catchRegion.FirstBlockOrdinal].FlowPredecessors);
        Assert.Contains(Operations<IrCallOperation>(body), call => call.Method == "C.Risky()" &&
                                                                   body.Blocks.Single(block => block.Operations.Contains(call)).Ordinal ==
                                                                   awaitBlock.Ordinal);
    }

    [Fact]
    public async Task Validator_rejects_bcl_operations_that_contradict_their_kind()
    {
        var body = (await Lower("void M(Task task, Timer timer) { task.Wait(); timer.Dispose(); }")).Body;
        var block = body.Blocks.Single(candidate => candidate.Operations.OfType<IrJoinOperation>().Any());
        var join = block.Operations.OfType<IrJoinOperation>().Single();
        var timer = block.Operations.OfType<IrTimerOperation>().Single();
        IrOperation[] broken =
        [
            .. block.Operations.Where(operation => operation is not IrJoinOperation and not IrTimerOperation),
            join with { HandlesKnown = false },
            timer with { DueTime = IrTimerInterval.Zero },
            new IrSpawnOperation(90, IrSpawnKind.QueueUserWorkItem, 99, join.HandleValues[0], [join.HandleValues[0]], join.Provenance)
        ];

        var problems = IrValidator.Validate(body with
        {
            Blocks = body.Blocks.Select(candidate => candidate == block ? candidate with { Operations = broken } : candidate).ToArray()
        });

        Assert.Empty(IrValidator.Validate(body));
        Assert.Contains($"Join operation {join.Id} names handles it does not know.", problems);
        Assert.Contains($"Timer operation {timer.Id} does not carry the values of Dispose.", problems);
        Assert.Contains("Operation 90 names operation 99, which is not an earlier call in its block.", problems);
        Assert.Contains("Spawn operation 90 of kind QueueUserWorkItem has a handle.", problems);
    }

    private static void Expect(string[] expected, string[] actual) => Assert.Equal(expected, actual);

    private static async Task<string[]> Bcl(string members, string declarations = "") =>
        Print(await Lower(members, declarations))
            .Split('\n')
            .Where(line => line.StartsWith("operation ", StringComparison.Ordinal))
            .Select(line => line["operation ".Length..line.IndexOf(" | ", StringComparison.Ordinal)])
            .Where(line => Forms.Any(form => line[(line.IndexOf(' ') + 1)..].StartsWith(form, StringComparison.Ordinal)))
            .ToArray();

    private static Task<IrLoweredMethod> Lower(string members, string declarations = "") => LowerMember(members, "M", declarations);

    private static async Task<IrLoweredMethod> LowerMember(string members, string name, string declarations = "")
    {
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;

            {{declarations}}

            class C
            {
                {{members}}
            }
            """;
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers(name).OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None);
    }

    private static string Print(IrLoweredMethod lowered) =>
        string.Concat(new[] { lowered.Body }.Concat(lowered.NestedBodies).Select(IrPrinter.Print));

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        body.Blocks.SelectMany(block => block.Operations).OfType<T>().ToArray();
}
