using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What the heap makes of a task's completion value (R1, R2): an await yields what the task completes with wherever the
/// task travelled first, every producer completes the task it makes, and a completion the heap cannot follow is marked so, never
/// taken for a known default.</summary>
public sealed class CompletionValueHeapTests
{
    private const string POST = "body:Fixture:M:JobsController.Post";

    private const string Types = """
        public sealed class Box { public int Value; }
        public static class Native
        {
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern Task<Box> Load();
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern Box OpaqueBox();
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern Func<Box> Work();
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern void Hand(object value);
        }
        """;

    private const string ITERATE = "private static System.Collections.Generic.IEnumerable<Box> Iterate() { yield return new Box(); }";

    private const string MAKE = "private static async Task<Box> Make() { await Task.Yield(); return new Box(); }";

    [Fact]
    public void Call_awaited_at_once_gives_its_task_and_the_await_what_the_async_body_returns()
    {
        var run = Solve(Controller("var box = await Make(); box.Value = 1;", MAKE));

        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        var call = Root(run).Summary.Calls.Single(call => call.Target.Contains("Make", StringComparison.Ordinal));
        var task = Assert.Single(run.Heap.Resolve(Root(run).Id, new CallResultValue(call.OperationId)));
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[task].Kind);
        Assert.Equal([Box(run)], run.Heap.Completion(task));
        Assert.Empty(run.Heap.AsyncSpawns);
    }

    [Fact]
    public void Async_call_kept_in_a_local_and_awaited_later_gives_what_the_body_returns()
    {
        var run = Solve(Controller("var pending = Make(); GC.KeepAlive(this); var box = await pending;", MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([site.Handle!], Variable(run, POST, "pending"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Task_kept_in_a_field_and_awaited_in_another_method_gives_what_the_body_returns()
    {
        var run = Solve(Types + """
            public sealed class Holder
            {
                public Task<Box>? Pending;
                public void Start() => Pending = Make();
                public async Task Finish() { var box = await Pending!; box.Value = 1; }
                private static async Task<Box> Make() { await Task.Yield(); return new Box(); }
            }
            public class JobsController(Holder holder) : ControllerBase
            {
                public async Task Post() { holder.Start(); await holder.Finish(); }
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        Assert.Equal([Box(run)], Variable(run, "body:Fixture:M:Holder.Finish", "box"));
    }

    [Fact]
    public void Task_a_non_async_helper_returns_is_awaited_for_what_it_completes_with()
    {
        var run = Solve(Controller("var box = await Helper();", "private static Task<Box> Helper() => Make(); " + MAKE));

        // The helper hands back the task the call it makes starts, so the call awaited at once gives that task, not a task of its own.
        var site = Assert.Single(run.Heap.AsyncSpawns);
        var call = Root(run).Summary.Calls.Single(call => call.Target.Contains("Helper", StringComparison.Ordinal));
        Assert.Equal([site.Handle!], run.Heap.Resolve(Root(run).Id, new CallResultValue(call.OperationId)));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Awaited_task_of_a_task_gives_the_inner_task_and_its_await_the_object()
    {
        var run = Solve(Controller("var inner = await Outer(); var box = await inner;",
                                   "private static async Task<Task<Box>> Outer() { await Task.Yield(); return Make(); } " + MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([site.Handle!], Variable(run, POST, "inner"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Unwrap_gives_the_tail_whose_await_gives_what_the_async_work_returns()
    {
        var run = Solve(Controller("""
            var outer = Task.Factory.StartNew(async () => { await Task.Yield(); return new Box(); });
            var tail = outer.Unwrap();
            var box = await tail;
            """));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal([spawn.Tail!], Variable(run, POST, "tail"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Start_new_over_async_work_completes_with_its_tail()
    {
        var run = Solve(Controller("""
            var tail = await Task.Factory.StartNew(async () => { await Task.Yield(); return new Box(); });
            var box = await tail;
            """));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Equal([spawn.Tail!], run.Heap.Completion(Assert.Single(spawn.Handles)));
        Assert.Equal([spawn.Tail!], Variable(run, POST, "tail"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Task_run_over_async_work_completes_with_what_the_work_returns()
    {
        var run = Solve(Controller("var box = await Task.Run(async () => { await Task.Yield(); return new Box(); });"));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.Null(spawn.Tail);
        Assert.Equal([Box(run)], run.Heap.Completion(Assert.Single(spawn.Handles)));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Task_run_over_a_non_async_work_returning_a_task_completes_with_that_task_s_value()
    {
        var run = Solve(Controller("var box = await Task.Run(() => Make());", MAKE));

        var spawn = Assert.Single(run.Heap.Spawns);
        Assert.True(Assert.Single(run.Heap.Summaries(spawn)).AwaitsWorkTask);
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Continue_with_completes_with_what_the_continuation_returns()
    {
        var run = Solve(Controller("var first = Task.Run(() => { }); var box = await first.ContinueWith(done => new Box());"));

        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Await_of_an_opaque_call_s_task_names_no_object_and_keeps_the_call_s_unknown_source()
    {
        var run = Solve(Controller("var box = await Native.Load(); box.Value = 1;"));

        Assert.Empty(Variable(run, POST, "box"));
        var sources = Single(Root(run), "box").UnknownSources;
        Assert.Contains(UnknownSource.OpaqueCall, sources);
        Assert.DoesNotContain(UnknownSource.Other, sources);
    }

    [Fact]
    public void Task_run_of_an_opaque_object_is_a_known_handle_whose_completion_is_unfollowed()
    {
        var run = Solve(Controller("var task = Task.Run(() => Native.OpaqueBox()); var box = await task;"));

        var spawn = Assert.Single(run.Heap.Spawns);
        var join = Assert.Single(Root(run).Summary.Joins);
        var handle = Assert.Single(join.Handles);
        Assert.Empty(handle.UnknownSources);
        Assert.Equal(spawn.Handles, run.Heap.Resolve(Root(run).Id, handle.Values).ToHashSet(StringComparer.Ordinal));
        Assert.Contains((Root(run).Id, join.OperationId), run.Heap.UnfollowedCompletions);
        Assert.Empty(Variable(run, POST, "box"));
    }

    [Fact]
    public void Task_run_of_a_delegate_from_an_opaque_call_completes_unfollowed_not_with_a_default()
    {
        var run = Solve(Controller("Func<Box> work = Native.Work(); var box = await Task.Run(work);"));

        var join = Assert.Single(Root(run).Summary.Joins);
        Assert.Contains((Root(run).Id, join.OperationId), run.Heap.UnfollowedCompletions);
    }

    [Fact]
    public void Task_run_of_a_known_work_completes_followed()
    {
        var run = Solve(Controller("var box = await Task.Run(() => new Box());"));

        var join = Assert.Single(Root(run).Summary.Joins);
        Assert.DoesNotContain((Root(run).Id, join.OperationId), run.Heap.UnfollowedCompletions);
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
    }

    [Fact]
    public void Async_body_returning_an_opaque_object_completes_unfollowed_wherever_its_task_is_awaited()
    {
        var run = Solve(Types + """
            public sealed class Holder
            {
                public Task<Box>? Pending;
                public void Start() => Pending = Make();
                public async Task Finish() { var box = await Pending!; box.Value = 1; }
                private static async Task<Box> Make() { await Task.Yield(); return Native.OpaqueBox(); }
            }
            public class JobsController(Holder holder) : ControllerBase
            {
                public async Task Post() { holder.Start(); await holder.Finish(); }
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        var finish = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.StartsWith("body:Fixture:M:Holder.Finish", StringComparison.Ordinal));
        var join = Assert.Single(finish.Summary.Joins);
        Assert.Contains((finish.Id, join.OperationId), run.Heap.UnfollowedCompletions);
    }

    [Fact]
    public void Await_of_a_custom_awaitable_names_no_object_and_stays_unfollowed()
    {
        var run = Solve(Controller("var box = await new Later(); box.Value = 1;", declarations: """
            public sealed class Later { public LaterAwaiter GetAwaiter() => new(); }
            public sealed class LaterAwaiter : System.Runtime.CompilerServices.INotifyCompletion
            {
                public bool IsCompleted => true;
                public Box GetResult() => new Box();
                public void OnCompleted(Action continuation) => continuation();
            }
            """));

        Assert.Empty(Variable(run, POST, "box"));
        Assert.Contains(UnknownSource.Other, Single(Root(run), "box").UnknownSources);
    }

    [Fact]
    public void Object_a_shared_task_completes_with_escapes_through_the_completion_slot_without_a_source_location()
    {
        var execution = Execute(Solve(Types + """
            public static class Shared
            {
                public static Task<Box>? Pending;
            }
            public class JobsController : ControllerBase
            {
                public void Post() => Shared.Pending = Make();
                private static async Task<Box> Make() { await Task.Yield(); return new Box(); }
            }
            """ + Startup()));

        var box = execution.Analysis.Ownership[Box(execution.Heap)];
        Assert.Equal(OwnershipKind.Escaped, box.Kind);
        Assert.Contains(box.Evidence, hop => hop.Contains("stored into <completion> of task:", StringComparison.Ordinal) &&
                                             hop.EndsWith("(no source location).", StringComparison.Ordinal));
    }

    /// <summary>An iterator a task completes with, at any depth and whatever produced it, escapes to an opaque call handed the task, as
    /// the iterator handed to it directly does (R2): the unknown code may await the task and enumerate what it gives.</summary>
    /// <param name="body">The body of the controller action that hands the task to the opaque call.</param>
    /// <param name="member">An extra member of the controller the body calls, beside the iterator method.</param>
    [Theory]
    [InlineData("Native.Hand(Iterate());", "")]
    [InlineData("Native.Hand(Task.FromResult(Iterate()));", "")]
    [InlineData("Native.Hand(Task.FromResult(Task.FromResult(Iterate())));", "")]
    [InlineData("Native.Hand(Wrap());", "private static async Task<System.Collections.Generic.IEnumerable<Box>> Wrap() { await Task.Yield(); return Iterate(); }")]
    [InlineData("Native.Hand(Wrap());", "private static async Task<Task<System.Collections.Generic.IEnumerable<Box>>> Wrap() { await Task.Yield(); return Task.FromResult(Iterate()); }")]
    [InlineData("Native.Hand(Task.Run(() => Iterate()));", "")]
    [InlineData("var source = new TaskCompletionSource<System.Collections.Generic.IEnumerable<Box>>(); source.SetResult(Iterate()); Native.Hand(source.Task);", "")]
    [InlineData("var items = Iterate(); Native.Hand(Task.FromResult<Func<System.Collections.Generic.IEnumerable<Box>>>(() => items));", "")]
    public void Iterator_a_task_handed_to_an_opaque_call_completes_with_escapes_as_one_handed_directly(string body, string member)
    {
        var run = Solve(Controller(body, ITERATE + " " + member));

        var iterator = Assert.Single(run.Heap.IteratorObjects);
        Assert.Contains(iterator.RegionId, run.Heap.UnknownIterators);
    }

    [Fact]
    public void Iterator_a_task_completes_with_and_its_awaiter_enumerates_does_not_escape()
    {
        var run = Solve(Controller("var items = await Task.FromResult(Iterate()); foreach (var item in items) item.Value = 1;", ITERATE));

        var iterator = Assert.Single(run.Heap.IteratorObjects);
        Assert.DoesNotContain(iterator.RegionId, run.Heap.UnknownIterators);
    }

    /// <summary>What a call gives a value consuming its task twice is the task's completion followed twice: a non-async helper returning
    /// a task of a task, as a helper returning the inner task directly gives it once (R1).</summary>
    /// <param name="body">The body of the controller action that consumes the helper's task into <c>box</c>.</param>
    /// <param name="member">The helper the body calls.</param>
    [Theory]
    [InlineData("var box = await Helper();", "private static Task<Box> Helper() => Make();")]
    [InlineData("var inner = await Helper(); var box = await inner;", "private static Task<Task<Box>> Helper() => Task.FromResult(Make());")]
    [InlineData("var inner = Helper().Result; var box = inner.GetAwaiter().GetResult();", "private static Task<Task<Box>> Helper() => Task.FromResult(Make());")]
    public void Call_consumed_at_its_own_depth_gives_what_its_innermost_task_completes_with(string body, string member)
    {
        var run = Solve(Controller(body, member + " " + MAKE));

        var helper = Root(run).Summary.Calls.Single(call => call.Target.Contains("Helper", StringComparison.Ordinal)).OperationId;
        var depth = body.Contains("inner", StringComparison.Ordinal) ? 2 : 1;
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        Assert.NotEqual([Box(run)], run.Heap.Gives(Root(run).Id, helper, depth - 1));
        Assert.Equal([Box(run)], run.Heap.Gives(Root(run).Id, helper, depth));
    }

    private static string Controller(string body, string member = "", string declarations = "") => Types + declarations + $$"""

        public class JobsController : ControllerBase
        {
            public async Task Post()
            {
                {{body}}
            }
            {{member}}
        }
        """ + Startup();

    private static string Box(HeapRun run) =>
        Assert.Single(run.Heap.Regions.Values, region => region.TypeKey == "Fixture:Box").Identity;

    private static MethodInstance Root(HeapRun run) =>
        Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.StartsWith(POST, StringComparison.Ordinal) && !instance.BodyId.Contains('#'));

    private static SummaryVariable Single(MethodInstance instance, string name) =>
        Assert.Single(instance.Summary.Variables, variable => variable.SymbolKey.Contains($"|{name}|", StringComparison.Ordinal));

    /// <summary>The regions a local of a body holds, over every instance of the body.</summary>
    /// <param name="run">The solved heap run.</param>
    /// <param name="bodyPrefix">The prefix of the body's id.</param>
    /// <param name="name">The local's name.</param>
    private static IReadOnlyList<string> Variable(HeapRun run, string bodyPrefix, string name) =>
        run.Heap.Instances.Values.Where(instance => instance.BodyId.StartsWith(bodyPrefix, StringComparison.Ordinal) && !instance.BodyId.Contains('#'))
           .SelectMany(instance => instance.Summary.Variables.Where(variable => variable.SymbolKey.Contains($"|{name}|", StringComparison.Ordinal))
                                           .SelectMany(variable => run.Heap.Resolve(instance.Id, variable.Values)))
           .Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal)
           .ToArray();
}
