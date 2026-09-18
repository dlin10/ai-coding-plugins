using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The executions spawn sites, async calls and timers start: which execution runs each access, the policy, parent and origin
/// of a spawned execution, and the path an occurrence reports.</summary>
public sealed class SpawnExecutionTests
{
    [Fact]
    public void Task_run_work_runs_in_an_execution_of_its_own()
    {
        var run = Analyze(Worker("Task.Run(() => Value = 1); Value = 2;"));

        AssertSpawned(run, "Task.Run");
    }

    [Fact]
    public void Start_new_work_runs_in_an_execution_of_its_own()
    {
        var run = Analyze(Worker("Task.Factory.StartNew(() => Value = 1); Value = 2;"));

        AssertSpawned(run, "TaskFactory.StartNew");
    }

    [Fact]
    public void Continuation_runs_in_an_execution_of_its_own()
    {
        var run = Analyze(Worker("Task.CompletedTask.ContinueWith(done => Value = 1); Value = 2;"));

        AssertSpawned(run, "Task.ContinueWith");
    }

    [Fact]
    public void Queued_work_item_runs_in_an_execution_of_its_own()
    {
        var run = Analyze(Worker("ThreadPool.QueueUserWorkItem(_ => Value = 1); Value = 2;"));

        AssertSpawned(run, "ThreadPool.QueueUserWorkItem");
    }

    [Fact]
    public void Started_thread_runs_in_an_execution_of_its_own()
    {
        var run = Analyze(Worker("var thread = new Thread(() => Value = 1); thread.Start(); Value = 2;"));

        AssertSpawned(run, "Thread.Start");
    }

    [Fact]
    public void Parallel_for_body_runs_in_an_execution_of_its_own_that_overlaps_itself()
    {
        var run = Analyze(Worker("Parallel.For(0, 2, index => Value = index); Value = 2;"));

        // The parent's write follows the call's return, so only the body pairs, with itself.
        var body = Assert.Single(run.Accesses("Value"), access => access.ExecutionId != WorkerExecution(run));
        var spawned = Execution(run, body);
        Assert.Equal((ExecutionKind.Spawn, WorkerExecution(run), "Parallel.For"), (spawned.Kind, spawned.ParentId, spawned.Origin!.Api));
        Assert.True(spawned.OverlapsItself);
        Assert.Contains(run.PairsOn("Value"), pair => Pairs(pair, body, body));
    }

    [Fact]
    public void Async_call_prefix_runs_in_the_caller_and_the_rest_in_its_own_execution()
    {
        var run = Analyze(Worker("_ = Step();", "private async Task Step() { First = 1; await Task.Yield(); Second = 2; }"));

        Assert.Equal(WorkerExecution(run), Assert.Single(run.Accesses("First")).ExecutionId);
        var tail = Execution(run, Assert.Single(run.Accesses("Second")));
        Assert.Equal(("async-call", WorkerExecution(run)), (tail.Origin!.Api, tail.ParentId));
    }

    [Fact]
    public void Code_after_an_if_that_awaits_on_one_branch_runs_in_the_caller_and_in_the_tail()
    {
        var run = Analyze(Worker("_ = Step(flag: true);", "private async Task Step(bool flag) { if (flag) await Task.Yield(); After = 1; }"));

        var executions = run.Accesses("After").Select(access => Execution(run, access)).ToArray();
        Assert.Equal(2, executions.Length);
        Assert.Contains(executions, execution => execution.Id == WorkerExecution(run));
        Assert.Contains(executions, execution => execution.Kind == ExecutionKind.Spawn);
    }

    [Fact]
    public void Loop_body_that_awaits_runs_in_the_caller_and_in_the_tail()
    {
        var run = Analyze(Worker("_ = Step();", "private async Task Step() { for (var index = 0; index < 2; index++) { Body = index; await Task.Yield(); } }"));

        var executions = run.Accesses("Body").Select(access => Execution(run, access).Kind).Order().ToArray();
        Assert.Equal([ExecutionKind.Root, ExecutionKind.Spawn], executions);
    }

    [Fact]
    public void Async_void_prefix_runs_in_the_caller()
    {
        var run = Analyze(Worker("Notify();", "private async void Notify() { First = 1; await Task.Yield(); Second = 2; }"));

        Assert.Equal(WorkerExecution(run), Assert.Single(run.Accesses("First")).ExecutionId);
        Assert.Equal("async-void", Execution(run, Assert.Single(run.Accesses("Second"))).Origin!.Api);
    }

    [Fact]
    public void Async_task_run_lambda_runs_its_prefix_and_tail_in_the_spawn_and_both_overlap_the_parent_write()
    {
        var run = Analyze(Worker("Task.Run(async () => { Value = 1; await Task.Yield(); Value = 2; }); Value = 3;"));

        var spawned = run.Accesses("Value").Where(access => access.ExecutionId != WorkerExecution(run)).ToArray();
        Assert.Equal(2, spawned.Length);
        Assert.Single(spawned.Select(access => access.ExecutionId).Distinct());
        Assert.Equal("Task.Run", Execution(run, spawned[0]).Origin!.Api);
        var parent = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == WorkerExecution(run));
        Assert.All(spawned, access => Assert.Contains(run.PairsOn("Value"), pair => Pairs(pair, parent, access)));
    }

    [Fact]
    public void Value_task_run_lambda_leaves_its_tail_to_a_child_execution()
    {
        AssertTailInChild("Task.Run(async ValueTask () => { First = 1; await Task.Yield(); Second = 2; });", "Task.Run");
    }

    [Fact]
    public void Async_start_new_lambda_leaves_its_tail_to_a_child_execution()
    {
        AssertTailInChild("Task.Factory.StartNew(async () => { First = 1; await Task.Yield(); Second = 2; });", "TaskFactory.StartNew");
    }

    [Fact]
    public void Async_thread_lambda_leaves_its_tail_to_a_child_execution()
    {
        AssertTailInChild("var thread = new Thread(async () => { First = 1; await Task.Yield(); Second = 2; }); thread.Start();", "Thread.Start");
    }

    [Fact]
    public void Helper_spawning_once_called_from_a_loop_overlaps_itself()
    {
        var run = Analyze(Worker("for (var index = 0; index < 2; index++) Start();", "private void Start() => Task.Run(() => Value = 1);"));

        Assert.True(SpawnedWriting(run, "Value").OverlapsItself);
    }

    [Fact]
    public void Helper_spawning_once_called_twice_overlaps_itself()
    {
        var run = Analyze(Worker("Start(); Start();", "private void Start() => Task.Run(() => Value = 1);"));

        Assert.True(SpawnedWriting(run, "Value").OverlapsItself);
    }

    [Fact]
    public void Unawaited_interface_call_with_an_async_implementation_is_a_spawn()
    {
        var run = Analyze("""
            public interface IJob { Task RunAsync(); }
            public sealed class Job : IJob { public int Value; public async Task RunAsync() { await Task.Yield(); Value = 1; } }
            public sealed class Worker(IJob job) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _ = job.RunAsync(); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>(); services.AddSingleton<IJob, Job>();"));

        Assert.Equal("async-call", SpawnedWriting(run, "Value").Origin!.Api);
    }

    [Fact]
    public void Unawaited_call_of_an_async_override_through_its_base_is_a_spawn()
    {
        var run = Analyze(Worker("Base job = new Impl(); _ = job.Run();", "", """
            public abstract class Base { public abstract Task Run(); }
            public sealed class Impl : Base { public int Value; public override async Task Run() { await Task.Yield(); Value = 1; } }
            """));

        Assert.Equal("async-call", SpawnedWriting(run, "Value").Origin!.Api);
    }

    [Fact]
    public void Unawaited_call_of_a_func_task_delegate_on_an_async_lambda_is_a_spawn()
    {
        var run = Analyze(Worker("Func<Task> work = async () => { await Task.Yield(); Value = 1; }; _ = work();"));

        Assert.Equal("async-call", SpawnedWriting(run, "Value").Origin!.Api);
    }

    [Fact]
    public void Void_interface_call_implemented_as_async_void_is_a_spawn()
    {
        var run = Analyze("""
            public interface INotifier { void Notify(); }
            public sealed class Notifier : INotifier { public int Value; public async void Notify() { await Task.Yield(); Value = 1; } }
            public sealed class Worker(INotifier notifier) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { notifier.Notify(); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>(); services.AddSingleton<INotifier, Notifier>();"));

        Assert.Equal("async-void", SpawnedWriting(run, "Value").Origin!.Api);
    }

    [Fact]
    public void Async_iterator_called_before_a_spawn_still_pairs_with_it()
    {
        var run = Analyze(Worker("var items = Items(); Task.Run(() => Value = 2); GC.KeepAlive(items);",
                                 "private async System.Collections.Generic.IAsyncEnumerable<int> Items() { Value = 1; await Task.Yield(); yield return 1; }"));

        var inIterator = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == WorkerExecution(run));
        var spawned = Assert.Single(run.Accesses("Value"), access => access.ExecutionId != WorkerExecution(run));
        Assert.Contains(run.PairsOn("Value"), pair => Pairs(pair, inIterator, spawned));
    }

    [Fact]
    public void Async_iterator_call_is_no_spawn_and_its_access_pairs_with_the_caller_s_spawn()
    {
        var run = Analyze(Worker("Task.Run(() => Value = 2); var items = Items(); GC.KeepAlive(items);",
                                 "private async System.Collections.Generic.IAsyncEnumerable<int> Items() { Value = 1; await Task.Yield(); yield return 1; }"));

        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Origin?.Api == "async-call");
        var inIterator = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == WorkerExecution(run));
        var spawned = Assert.Single(run.Accesses("Value"), access => access.ExecutionId != WorkerExecution(run));
        Assert.Contains(run.PairsOn("Value"), pair => Pairs(pair, inIterator, spawned));
    }

    [Fact]
    public void Spawn_in_a_loop_overlaps_itself()
    {
        var run = Analyze(Worker("for (var index = 0; index < 2; index++) Task.Run(() => Value = 1);"));

        Assert.True(SpawnedWriting(run, "Value").OverlapsItself);
    }

    [Fact]
    public void Spawn_outside_a_loop_of_a_once_running_parent_runs_at_most_once()
    {
        var run = Analyze(Worker("Task.Run(() => Value = 1);"));

        var spawned = SpawnedWriting(run, "Value");
        Assert.Equal(new InvocationPolicy(Multiplicity.AtMostOnce, SelfOverlap.Serialized, ""), spawned.Policy);
        Assert.DoesNotContain(run.PairsOn("Value"), pair => pair.First.ExecutionId == pair.Second.ExecutionId);
    }

    [Fact]
    public void Spawn_under_a_repeated_serialized_parent_overlaps_itself()
    {
        var reached = Reach(Worker("Task.Run(() => Value = 1);"));
        var roots = reached.Input.Roots.Select(root => root with { InvocationPolicy = new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.Serialized, "") })
                           .ToArray();
        var execution = Execute(Solve(reached with { Input = reached.Input with { Roots = roots } }));

        var spawned = Assert.Single(execution.Analysis.Executions, candidate => candidate.Kind == ExecutionKind.Spawn);
        Assert.True(spawned.OverlapsItself);
    }

    [Fact]
    public void Spawn_in_an_http_action_overlaps_itself()
    {
        var run = Analyze("""
            public sealed class Board { public int Value; }
            public class BoardController(Board board) : ControllerBase { public void Post() => Task.Run(() => board.Value = 1); }
            """ + Startup("services.AddSingleton<Board>();"));

        Assert.True(SpawnedWriting(run, "Value").OverlapsItself);
    }

    [Fact]
    public void Parallel_for_each_body_overlaps_itself()
    {
        var run = Analyze(Worker("Parallel.ForEach(new[] { 1, 2 }, item => Value = item);"));

        var spawned = SpawnedWriting(run, "Value");
        Assert.True(spawned.OverlapsItself);
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == spawned.Id && pair.Second.ExecutionId == spawned.Id);
    }

    [Fact]
    public void Parallel_local_init_and_local_finally_run_in_the_body_s_execution()
    {
        var run = Analyze(Worker("Parallel.For(0, 2, () => { Value = 1; return 0; }, (index, state, local) => { Value = 2; return local; }, local => Value = 3);"));

        var executions = run.Accesses("Value").Select(access => access.ExecutionId).Distinct().ToArray();
        Assert.Equal("Parallel.For", Execution(run, run.Accesses("Value")[0]).Origin!.Api);
        Assert.Single(executions);
    }

    [Fact]
    public void Recursive_spawn_terminates_and_overlaps_itself()
    {
        var run = Analyze(Worker("Loop();", "private void Loop() => Task.Run(() => { Value = 1; Loop(); });"));

        var spawned = SpawnedWriting(run, "Value");
        Assert.True(spawned.OverlapsItself);
        Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.Spawn);
    }

    [Fact]
    public void Unrecognized_spawn_form_runs_its_delegates_in_an_execution_that_overlaps_itself()
    {
        var run = Analyze(Worker("Parallel.Invoke(() => Value = 1);"));

        var spawned = SpawnedWriting(run, "Value");
        Assert.Equal("unrecognized", spawned.Origin!.Api);
        Assert.True(spawned.OverlapsItself);
    }

    [Fact]
    public void Parent_lock_around_task_run_does_not_protect_the_lambda()
    {
        var run = Analyze(Worker("lock (_gate) { Task.Run(() => Value = 1); Value = 2; }", "private readonly object _gate = new();"));

        var parent = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == WorkerExecution(run));
        var spawned = Assert.Single(run.Accesses("Value"), access => access.ExecutionId != WorkerExecution(run));
        Assert.NotEmpty(parent.HeldProtection);
        Assert.Empty(spawned.HeldProtection);
        Assert.Equal(PairProtection.PARTIAL, Assert.Single(run.PairsOn("Value"), pair => Pairs(pair, parent, spawned)).Protection);
    }

    [Fact]
    public void Spawn_in_a_hosted_service_constructor_starts_an_execution_whose_root_is_startup()
    {
        var run = Analyze(Worker("", "public Worker() => Task.Run(() => Value = 1);"));

        var access = Assert.Single(run.Accesses("Value"));
        var spawned = Execution(run, access);
        Assert.Equal((ExecutionModel.STARTUP, ExecutionModel.STARTUP, ExecutionModel.STARTUP), (spawned.ParentId, spawned.TreeRootId, access.Root.RootId));
    }

    [Fact]
    public void Timer_callback_runs_in_a_child_of_the_execution_that_created_the_timer()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => Value = 1, null, 0, 1000); Value = 2;"));

        var callback = SpawnedWriting(run, "Value");
        Assert.Equal((ExecutionKind.TimerCallback, WorkerExecution(run)), (callback.Kind, callback.ParentId));
        Assert.Equal("System.Threading.Timer", callback.Origin!.Api);
        Assert.Contains("timer-callback:System.Threading.Timer@Worker.ExecuteAsync(CancellationToken)",
                        Assert.Single(run.Accesses("Value"), access => access.ExecutionId == callback.Id).CallPath);
    }

    [Fact]
    public void Occurrence_path_of_a_spawned_access_passes_through_its_spawn_segment()
    {
        var run = Analyze(Worker("Start();", "private void Start() => Task.Run(() => Value = 1);"));

        var access = Assert.Single(run.Accesses("Value"));
        Assert.Equal(["Worker.ExecuteAsync(CancellationToken)", "Worker.Start()", "spawn:Task.Run@Worker.Start()", "Worker.Start()"], access.CallPath);
        Assert.Equal(Execution(run, access).TreeRootId, access.Root.RootId);
        Assert.StartsWith("hosting:execute:", access.Root.RootId, StringComparison.Ordinal);
    }

    private static ExecutionInstance AssertSpawned(EngineRun run, string api)
    {
        var parent = Assert.Single(run.Accesses("Value"), access => access.ExecutionId == WorkerExecution(run));
        var spawnedAccess = Assert.Single(run.Accesses("Value"), access => access.ExecutionId != WorkerExecution(run));
        var spawned = Execution(run, spawnedAccess);
        Assert.Equal((ExecutionKind.Spawn, WorkerExecution(run), api), (spawned.Kind, spawned.ParentId, spawned.Origin!.Api));
        Assert.Contains(run.PairsOn("Value"), pair => Pairs(pair, parent, spawnedAccess));
        return spawned;
    }

    private static void AssertTailInChild(string statement, string api)
    {
        var run = Analyze(Worker(statement));

        var prefix = Execution(run, Assert.Single(run.Accesses("First")));
        var tail = Execution(run, Assert.Single(run.Accesses("Second")));
        Assert.Equal((api, false, WorkerExecution(run)), (prefix.Origin!.Api, prefix.Origin.IsTail, prefix.ParentId));
        Assert.Equal((api, true, prefix.Id), (tail.Origin!.Api, tail.Origin.IsTail, tail.ParentId));
    }

    private static bool Pairs(AccessPair pair, Access one, Access other) =>
        ReferenceEquals(pair.First, one) && ReferenceEquals(pair.Second, other) || ReferenceEquals(pair.First, other) && ReferenceEquals(pair.Second, one);

    private static ExecutionInstance Execution(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId);

    private static ExecutionInstance SpawnedWriting(EngineRun run, string field) =>
        Execution(run, Assert.Single(run.Accesses(field), access => access.ExecutionId != WorkerExecution(run) &&
                                                                   run.Execution.Analysis.Execution(access.ExecutionId).ParentId is not null));

    private static string WorkerExecution(EngineRun run) =>
        Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.Root &&
                                                                      (execution.RootId!.StartsWith("hosting:execute:", StringComparison.Ordinal) ||
                                                                       execution.RootId.StartsWith("aspnetcore:", StringComparison.Ordinal))).Id;

    private static string Worker(string body, string members = "", string declarations = "") => $$"""
        {{declarations}}
        public sealed class Worker : BackgroundService
        {
            public int Value;
            public int First;
            public int Second;
            public int After;
            public int Body;
            {{members}}
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddHostedService<Worker>();");
}
