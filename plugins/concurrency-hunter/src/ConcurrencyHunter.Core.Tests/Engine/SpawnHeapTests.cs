using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What the reachable set, the summaries and the whole-program heap make of spawns, joins and timers: reached work, handle
/// regions, bound arguments, async call edges and the unknown sources of handles.</summary>
public sealed class SpawnHeapTests
{
    private const string POST = "body:Fixture:M:JobsController.Post";

    [Fact]
    public void Task_run_body_is_reached_and_runs_as_spawn_work()
    {
        var run = Solve(Controller("Task.Run(() => GC.KeepAlive(this));"));

        Assert.True(run.Program.Reaches(POST + "#lambda1"));
        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal((IrSpawnKind.TaskRun, SpawnRole.Work), (spawn.Kind, Assert.Single(spawn.Callees).Role));
        Assert.Equal(POST + "#lambda1", run.Heap.Instances[spawn.Callees[0].InstanceId].BodyId);
    }

    [Fact]
    public void Delegate_handed_to_a_user_method_without_a_body_is_reached_and_handed_off_but_no_spawn()
    {
        var run = Solve(Controller("Native.Run(() => GC.KeepAlive(this));", """
            public static class Native
            {
                [System.Runtime.InteropServices.DllImport("native")]
                public static extern void Run(Action work);
            }
            """));

        // The call is opaque, so what it is handed runs in an unknown execution (R3), which is no spawn.
        Assert.True(run.Program.Reaches(POST + "#lambda1"));
        Assert.Equal(POST + "#lambda1", run.Heap.Instances[Assert.Single(Assert.Single(run.Heap.DelegateHandoffs).Callees)].BodyId);
        Assert.Empty(run.Heap.Spawns);
    }

    [Fact]
    public void Handles_of_two_sites_are_two_regions()
    {
        var run = Solve(Controller("var first = Task.Run(() => { }); var second = Task.Run(() => { });"));

        var handles = run.Heap.Spawns.Select(spawn => Assert.Single(spawn.Handles)).ToArray();
        Assert.Equal(2, handles.Distinct(StringComparer.Ordinal).Count());
        Assert.All(handles, handle => Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[handle].Kind));
        Assert.Equal(handles.Order(StringComparer.Ordinal), Variable(run, POST, "first").Concat(Variable(run, POST, "second")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Handle_of_one_site_differs_by_the_context_it_runs_in()
    {
        var run = Solve(Controller("new Starter().Start(); new Starter().Start();",
                                   "public sealed class Starter { public Task Start() => Task.Run(() => { }); }"));

        var handles = run.Heap.Spawns.Select(spawn => run.Heap.Regions[Assert.Single(spawn.Handles)]).ToArray();
        Assert.Equal(2, handles.Length);
        Assert.NotEqual(handles[0].Identity, handles[1].Identity);
        Assert.NotEqual(handles[0].Context, handles[1].Context);
        Assert.Equal(handles[0].Group, handles[1].Group);
    }

    [Fact]
    public void Thread_handle_is_the_region_of_its_new_thread()
    {
        var run = Solve(Controller("var thread = new Thread(() => { }); thread.Start();"));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal(IrSpawnKind.ThreadStart, spawn.Kind);
        var thread = run.Heap.Regions[Assert.Single(spawn.Handles)];
        Assert.Equal(HeapRegionKind.Allocation, thread.Kind);
        Assert.EndsWith(":System.Threading.Thread", thread.TypeKey, StringComparison.Ordinal);
        Assert.Equal(POST + "#lambda1", run.Heap.Instances[Assert.Single(spawn.Callees).InstanceId].BodyId);
    }

    [Fact]
    public void Timer_state_reaches_the_callback_parameter_and_its_write_is_to_the_parent_s_object()
    {
        AssertStateWritesTheParentObject("var timer = new Timer(state => ((Box)state!).Value = 1, box, 0, 1000);");
    }

    [Fact]
    public void Queue_user_work_item_state_reaches_the_callback_parameter()
    {
        AssertStateWritesTheParentObject("ThreadPool.QueueUserWorkItem(state => ((Box)state!).Value = 1, box);");
    }

    [Fact]
    public void Generic_queue_user_work_item_state_reaches_the_callback_parameter()
    {
        AssertStateWritesTheParentObject("ThreadPool.QueueUserWorkItem(state => state.Value = 1, box, preferLocal: false);");
    }

    [Fact]
    public void Unsafe_queue_user_work_item_state_reaches_the_callback_parameter()
    {
        AssertStateWritesTheParentObject("ThreadPool.UnsafeQueueUserWorkItem(state => ((Box)state!).Value = 1, box);");
    }

    [Fact]
    public void Start_new_state_reaches_the_work_parameter()
    {
        AssertStateWritesTheParentObject("Task.Factory.StartNew(state => { ((Box)state!).Value = 1; }, box);");
    }

    [Fact]
    public void Continue_with_state_reaches_the_second_continuation_parameter()
    {
        AssertStateWritesTheParentObject("Task.CompletedTask.ContinueWith((done, state) => { ((Box)state!).Value = 1; }, box);");
    }

    [Fact]
    public void Thread_start_argument_reaches_the_parameterized_work()
    {
        AssertStateWritesTheParentObject("var thread = new Thread(state => ((Box)state!).Value = 1); thread.Start(box);");
    }

    [Fact]
    public void Work_item_execute_is_reached_and_writes_the_field_of_its_receiver()
    {
        var run = Solve(Controller("var item = new Item(); ThreadPool.UnsafeQueueUserWorkItem(item, false);", """
            public sealed class Item : IThreadPoolWorkItem
            {
                public int Runs;
                public void Execute() => Runs = 1;
            }
            """));

        const string EXECUTE = "body:Fixture:M:Item.Execute";
        Assert.True(run.Program.Reaches(EXECUTE));
        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal((IrSpawnKind.UnsafeQueueUserWorkItem, EXECUTE), (spawn.Kind, run.Heap.Instances[Assert.Single(spawn.Callees).InstanceId].BodyId));
        var item = Assert.Single(Variable(run, POST, "item"));
        Assert.Equal([item], StoreRegions(run, run.Heap.Instances[spawn.Callees[0].InstanceId], "Runs"));
    }

    [Fact]
    public void Continuation_parameter_points_to_its_antecedent_and_the_continuation_remembers_it()
    {
        var run = Solve(Controller("var first = Task.Run(() => { }); var next = first.ContinueWith(done => GC.KeepAlive(done));"));

        var first = Assert.Single(Variable(run, POST, "first"));
        var next = Assert.Single(Variable(run, POST, "next"));
        var continuation = Assert.Single(run.Heap.Spawns, spawn => spawn.Kind == IrSpawnKind.ContinueWith);
        Assert.Equal([first], run.Heap.Instances[Assert.Single(continuation.Callees).InstanceId].Parameters[0]);
        Assert.Equal([first], run.Heap.Antecedents[next]);
        Assert.Equal([next], continuation.Handles);
    }

    [Fact]
    public void Captured_local_reaches_the_spawned_body()
    {
        var run = Solve(Controller("var box = new Box(); Task.Run(() => box.Value = 1); box.Value = 2;", "public sealed class Box { public int Value; }"));

        var callee = run.Heap.Instances[Assert.Single(Assert.Single(run.Heap.Spawns).Callees).InstanceId];
        var box = Assert.Single(Variable(run, POST, "box"));
        Assert.Equal([box], StoreRegions(run, callee, "Value"));
        Assert.Equal([box], StoreRegions(run, Root(run), "Value"));
    }

    [Fact]
    public void When_all_with_listed_tasks_remembers_them()
    {
        var run = Solve(Controller("var first = Task.Run(() => { }); var second = Task.Run(() => { }); var all = Task.WhenAll(first, second);"));

        var group = run.Heap.TaskGroups[Assert.Single(Variable(run, POST, "all"))];
        Assert.True(group.MembersKnown);
        Assert.Equal(Variable(run, POST, "first").Concat(Variable(run, POST, "second")).Order(StringComparer.Ordinal),
                     group.Members.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void When_all_with_a_list_does_not_know_its_tasks()
    {
        var run = Solve(Controller("var tasks = new System.Collections.Generic.List<Task> { Task.Run(() => { }) }; var all = Task.WhenAll(tasks);"));

        var group = run.Heap.TaskGroups[Assert.Single(Variable(run, POST, "all"))];
        Assert.False(group.MembersKnown);
        Assert.Empty(group.Members);
    }

    [Fact]
    public void Start_new_with_async_work_has_an_outer_region_and_a_tail_that_unwrap_gives()
    {
        var run = Solve(Controller("var outer = Task.Factory.StartNew(async () => await Task.Yield()); var tail = outer.Unwrap();"));

        var spawn = Assert.Single(run.Heap.Spawns);
        var outer = Assert.Single(spawn.Handles);
        Assert.NotNull(spawn.Tail);
        Assert.NotEqual(outer, spawn.Tail);
        Assert.Equal([outer], Variable(run, POST, "outer"));
        Assert.Equal([spawn.Tail], Variable(run, POST, "tail"));
        Assert.Equal(spawn.Tail, run.Heap.Tails[outer]);
    }

    [Fact]
    public void Task_run_of_a_value_task_gives_the_tail_through_an_await_of_its_await()
    {
        var run = Solve("""
            public class JobsController : ControllerBase
            {
                public async Task Post()
                {
                    var outer = Task.Run(async ValueTask () => await Task.Yield());
                    var tail = await outer;
                    await tail;
                }
            }
            """ + Startup());

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.False(run.Heap.Summaries(spawn).Single().AwaitsWorkTask);
        Assert.NotNull(spawn.Tail);
        Assert.Equal([spawn.Tail], Variable(run, POST, "tail"));
        var root = Root(run);
        var last = root.Summary.Joins.Where(join => join.Kind == SummaryJoinKind.Await).OrderBy(join => join.OperationId).Last();
        Assert.Equal([spawn.Tail], run.Heap.Resolve(root.Id, Assert.Single(Assert.Single(last.Handles).Values)));
    }

    [Fact]
    public void Task_run_of_async_task_work_waits_for_it_and_has_no_tail()
    {
        var run = Solve(Controller("var whole = Task.Run(async () => await Task.Yield());"));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Null(spawn.Tail);
        Assert.True(Assert.Single(Root(run).Summary.Spawns).AwaitsWorkTask);
    }

    [Fact]
    public void Parallel_for_each_body_gets_its_element_as_an_unknown_value()
    {
        var run = Solve(Controller("Parallel.ForEach(new[] { new Box() }, item => item.Value = 1);", "public sealed class Box { public int Value; }"));

        var body = run.Heap.Instances[Assert.Single(Assert.Single(run.Heap.Spawns).Callees).InstanceId];
        Assert.Empty(body.Parameters.GetValueOrDefault(0) ?? new HashSet<string>());
        Assert.True(run.Heap.Spawns[0].Callees[0].Role == SpawnRole.Work);
    }

    [Fact]
    public void Parallel_local_init_and_local_finally_are_reached_and_local_values_flow_to_the_body_and_local_finally()
    {
        var run = Solve(Controller(
            "Parallel.For(0, 4, () => new Box(), (index, state, local) => { GC.KeepAlive(local); return new Box(); }, local => GC.KeepAlive(local));",
            "public sealed class Box { public int Value; }"));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.True(run.Program.Reaches(POST + "#lambda1"));
        Assert.True(run.Program.Reaches(POST + "#lambda3"));
        var byRole = spawn.Callees.ToDictionary(callee => callee.Role, callee => run.Heap.Instances[callee.InstanceId]);
        Assert.Equal([SpawnRole.Work, SpawnRole.LocalInit, SpawnRole.LocalFinally], byRole.Keys.Order());
        var boxes = run.Heap.Regions.Values.Where(region => region.TypeKey == "Fixture:Box").Select(region => region.Identity).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Equal(boxes, byRole[SpawnRole.Work].Parameters[2].Order(StringComparer.Ordinal));
        Assert.Equal(boxes, byRole[SpawnRole.LocalFinally].Parameters[0].Order(StringComparer.Ordinal));
        Assert.Empty(byRole[SpawnRole.LocalInit].Parameters);
    }

    [Fact]
    public void Timer_with_only_a_callback_is_its_own_state()
    {
        var run = Solve(Controller("var timer = new Timer(state => GC.KeepAlive(state));"));

        var site = Assert.Single(run.Heap.TimerCallbacks);
        var timer = Assert.Single(site.Timers);
        Assert.Equal([timer], Variable(run, POST, "timer"));
        Assert.Equal([timer], run.Heap.Instances[Assert.Single(site.Callees)].Parameters[0]);
    }

    [Fact]
    public void Elapsed_handler_is_reached_as_a_timer_callback()
    {
        var run = Solve(Controller("var timer = new System.Timers.Timer(1000); timer.Elapsed += (sender, e) => GC.KeepAlive(this); timer.Start();"));

        var site = Assert.Single(run.Heap.TimerCallbacks);
        Assert.Equal(IrTimerAction.ElapsedSubscribe, site.Action);
        Assert.Equal(Variable(run, POST, "timer"), site.Timers.Order(StringComparer.Ordinal));
        Assert.Equal(POST + "#lambda1", run.Heap.Instances[Assert.Single(site.Callees)].BodyId);
    }

    [Fact]
    public void Unawaited_interface_call_with_an_async_implementation_stores_its_site_handle_in_the_field()
    {
        var run = Solve("""
            public interface IJob { Task RunAsync(); }
            public sealed class Job : IJob { public async Task RunAsync() => await Task.Yield(); }
            public sealed class Tracker { public Task? Pending; }
            public class JobsController(IJob job, Tracker tracker) : ControllerBase
            {
                public void Post() => tracker.Pending = job.RunAsync();
            }
            """ + Startup("services.AddSingleton<IJob, Job>(); services.AddSingleton<Tracker>();"));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal(IrSpawnKind.AsyncCall, site.Kind);
        Assert.Equal("body:Fixture:M:Job.RunAsync", run.Heap.Instances[Assert.Single(site.Callees)].BodyId);
        var tracker = Assert.Single(run.Regions("di:Tracker@Singleton"));
        Assert.Equal([site.Handle], run.Heap.PointsTo(tracker.Identity, "Pending"));
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[site.Handle!].Kind);
        Assert.Contains(run.Heap.Edges, edge => edge.CalleeInstance == site.Callees[0] && edge.OperationId == site.OperationId);
    }

    [Fact]
    public void Async_void_call_is_an_async_spawn_without_a_handle()
    {
        var run = Solve(Controller("Notify();", member: "private async void Notify() => await Task.Yield();"));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal((IrSpawnKind.AsyncVoid, (string?)null), (site.Kind, site.Handle));
    }

    [Fact]
    public void Immediately_awaited_async_call_is_not_an_async_spawn()
    {
        var run = Solve("""
            public class JobsController : ControllerBase
            {
                public async Task Post() => await Save();
                private async Task Save() => await Task.Yield();
            }
            """ + Startup());

        Assert.Empty(run.Heap.AsyncSpawns);
        Assert.True(run.Program.Reaches("body:Fixture:M:JobsController.Save"));
    }

    [Fact]
    public void Thread_created_in_one_method_and_started_in_another_runs_its_method_group_work()
    {
        AssertThreadWorkRunsFromAnotherMethod("new Thread(Work)", "private void Work() => Runs = 1;");
    }

    [Fact]
    public void Thread_with_parameterized_work_created_in_one_method_and_started_in_another_runs_it()
    {
        AssertThreadWorkRunsFromAnotherMethod("new Thread(Work, 4096)", "private void Work(object? state) => Runs = 1;");
    }

    [Fact]
    public void Handle_that_may_still_be_null_is_marked_with_an_unknown_source()
    {
        var run = Solve(Controller("Task? t = null; if (flag) t = Task.Run(() => { }); GC.KeepAlive(t);", parameters: "bool flag"));

        var t = Assert.Single(Root(run).Summary.Variables, variable => variable.SymbolKey.Contains("|t|", StringComparison.Ordinal));
        Assert.Contains(UnknownSource.Null, t.UnknownSources);
        Assert.Single(run.Heap.Resolve(Root(run).Id, Assert.Single(t.Values, value => value is CallResultValue)));
    }

    [Fact]
    public void Handle_from_an_opaque_factory_is_marked_with_an_unknown_source()
    {
        var run = Solve("""
            public class JobsController : ControllerBase
            {
                public async Task Post() { var pending = Task.Delay(10); await pending; }
            }
            """ + Startup());

        var join = Assert.Single(Root(run).Summary.Joins);
        Assert.Equal([UnknownSource.OpaqueCall], Assert.Single(join.Handles).UnknownSources);
        Assert.Empty(run.Heap.Resolve(Root(run).Id, Assert.Single(Assert.Single(join.Handles).Values)));
    }

    [Fact]
    public void Summary_lists_spawn_join_timer_and_wait_events_with_their_operations()
    {
        var run = Solve("""
            public class JobsController : ControllerBase
            {
                public async Task Post()
                {
                    var task = Task.Run(() => { });
                    task.Wait();
                    var timer = new Timer(_ => { }, null, 0, Timeout.Infinite);
                    var handle = new ManualResetEvent(false);
                    timer.Dispose(handle);
                    handle.WaitOne();
                    await Task.WhenAll(task);
                }
            }
            """ + Startup());

        var summary = Root(run).Summary;
        var spawn = Assert.Single(summary.Spawns);
        Assert.Equal(IrSpawnKind.TaskRun, spawn.Kind);
        Assert.Equal([SummaryJoinKind.Wait, SummaryJoinKind.WaitOne, SummaryJoinKind.Await],
                     summary.Joins.OrderBy(join => join.OperationId).Select(join => join.Kind));
        Assert.Equal([IrTimerAction.Create, IrTimerAction.DisposeWaitHandle], summary.Timers.Select(timer => timer.Action));
        var create = summary.Timers[0];
        Assert.Equal((IrTimerInterval.Zero, IrTimerInterval.Infinite), (create.DueTime, create.Period));
        var whenAll = Assert.Single(summary.WhenAlls);
        Assert.True(whenAll.TasksKnown);
        var task = Assert.Single(Variable(run, POST, "task"));
        Assert.Equal([task], run.Heap.Resolve(Root(run).Id, Assert.Single(summary.Joins[0].Handles).Values.Single()));
        Assert.Equal([task], spawn.Handle!.Values.SelectMany(value => run.Heap.Resolve(Root(run).Id, value)));
        Assert.All(summary.Joins.Where(join => join.Kind != SummaryJoinKind.Await), join => Assert.False(join.ThrowsOnlyAfterCompletion));
    }

    private static void AssertStateWritesTheParentObject(string statement)
    {
        var run = Solve(Controller($"var box = new Box(); {statement} box.Value = 2;", "public sealed class Box { public int Value; }"));

        var callee = Assert.Single(run.Instances(POST + "#lambda1"));
        var box = Assert.Single(Variable(run, POST, "box"));
        Assert.Contains(box, callee.Parameters.Values.SelectMany(values => values));
        Assert.Equal([box], StoreRegions(run, callee, "Value"));
        Assert.Equal([box], StoreRegions(run, Root(run), "Value"));
    }

    private static void AssertThreadWorkRunsFromAnotherMethod(string creation, string work)
    {
        var run = Solve($$"""
            public sealed class Runner
            {
                private Thread? _thread;
                public int Runs;
                public void Create() => _thread = {{creation}};
                public void Begin() => _thread!.Start();
                {{work}}
            }
            public class JobsController(Runner runner) : ControllerBase
            {
                public void Post() { runner.Create(); runner.Begin(); }
            }
            """ + Startup("services.AddSingleton<Runner>();"));

        Assert.True(run.Program.Reaches(Assert.Single(run.Program.Result.ReachedBodies.Keys, body => body.StartsWith("body:Fixture:M:Runner.Work", StringComparison.Ordinal))));
        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal(IrSpawnKind.ThreadStart, spawn.Kind);
        Assert.StartsWith("body:Fixture:M:Runner.Begin", run.Heap.Instances[spawn.CallerInstance].BodyId, StringComparison.Ordinal);
        var callee = run.Heap.Instances[Assert.Single(spawn.Callees).InstanceId];
        Assert.StartsWith("body:Fixture:M:Runner.Work", callee.BodyId, StringComparison.Ordinal);
        var thread = run.Heap.Regions[Assert.Single(spawn.Handles)];
        Assert.StartsWith("body:Fixture:M:Runner.Create", thread.SiteBodyId, StringComparison.Ordinal);
        var runner = Assert.Single(run.Regions("di:Runner@Singleton"));
        Assert.Equal([runner.Identity], StoreRegions(run, callee, "Runs"));
    }

    private static string Controller(string body, string declarations = "", string member = "", string parameters = "") => $$"""
        {{declarations}}
        public class JobsController : ControllerBase
        {
            public void Post({{parameters}})
            {
                {{body}}
            }
            {{member}}
        }
        """ + Startup();

    private static MethodInstance Root(HeapRun run) =>
        Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.StartsWith(POST, StringComparison.Ordinal) && !instance.BodyId.Contains('#'));

    /// <summary>The regions a local of a body holds, over every instance of the body.</summary>
    private static IReadOnlyList<string> Variable(HeapRun run, string bodyPrefix, string name) =>
        run.Heap.Instances.Values.Where(instance => instance.BodyId.StartsWith(bodyPrefix, StringComparison.Ordinal) && !instance.BodyId.Contains('#'))
           .SelectMany(instance => instance.Summary.Variables.Where(variable => variable.SymbolKey.Contains($"|{name}|", StringComparison.Ordinal))
                                           .SelectMany(variable => run.Heap.Resolve(instance.Id, variable.Values)))
           .Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal)
           .ToArray();

    private static IReadOnlySet<string> StoreRegions(HeapRun run, MethodInstance instance, string field) =>
        instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store && access.Field.Name == field)
                .SelectMany(access => access.Bases)
                .SelectMany(value => run.Heap.Resolve(instance.Id, value))
                .ToHashSet(StringComparer.Ordinal);
}

internal static class SpawnHeapTestExtensions
{
    /// <summary>Resolves a set of abstract values of an instance.</summary>
    internal static IEnumerable<string> Resolve(this HeapSolution heap, string instanceId, IEnumerable<AbstractValue> values) =>
        values.SelectMany(value => heap.Resolve(instanceId, value));

    /// <summary>The summary spawns of a spawn site's caller at its operation.</summary>
    internal static IEnumerable<SummarySpawn> Summaries(this HeapSolution heap, SpawnSite site) =>
        heap.Instances[site.CallerInstance].Summary.Spawns.Where(spawn => spawn.OperationId == site.OperationId);
}
