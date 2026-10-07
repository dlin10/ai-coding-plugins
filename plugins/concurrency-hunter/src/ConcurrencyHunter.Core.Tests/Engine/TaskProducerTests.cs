using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The BCL producers of a completion value (R1, R2, R3): each gives a task that completes with what it is handed — a value, the
/// same task as another, what another task completes with, one of several tasks — and one the heap cannot follow is marked so, never
/// taken for a known default. Only a same task carries the regions, and so the join, of the task it is.</summary>
public sealed class TaskProducerTests
{
    private const string POST = "body:Fixture:M:JobsController.Post";

    private const string Types = """
        public sealed class Box { public int Value; }
        public static class Native
        {
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern Box OpaqueBox();
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern System.Threading.Tasks.Sources.IValueTaskSource<Box> Source();
        }
        """;

    private const string MAKE = "private static async Task<Box> Make() { await Task.Yield(); return new Box(); }";

    private const string OTHER = "private static async Task<Box> Other() { await Task.Yield(); return new Box(); }";

    [Fact]
    public void From_result_completes_a_task_of_its_site_with_the_value()
    {
        var run = Solve(Controller("var pending = Task.FromResult(new Box()); var box = await pending;"));

        var task = Assert.Single(Variable(run, POST, "pending"));
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[task].Kind);
        Assert.Equal([Box(run)], run.Heap.Completion(task));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        Assert.Empty(Single(Root(run), "pending").UnknownSources);
        AssertFollowed(run);
    }

    [Fact]
    public void Value_task_from_result_completes_with_the_value()
    {
        var run = Solve(Controller("var box = await ValueTask.FromResult(new Box());"));

        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Value_task_of_a_value_is_a_task_of_its_site_completed_with_the_value()
    {
        var run = Solve(Controller("var pending = new ValueTask<Box>(new Box()); var box = await pending;"));

        var task = Assert.Single(Variable(run, POST, "pending"));
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[task].Kind);
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Value_task_of_a_task_is_the_same_task()
    {
        var run = Solve(Controller("var pending = Make(); var wrapped = new ValueTask<Box>(pending); var box = await wrapped;", MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([site.Handle!], Variable(run, POST, "wrapped"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Value_task_of_a_value_task_source_completes_unfollowed()
    {
        var run = Solve(Controller("var box = await new ValueTask<Box>(Native.Source(), 0);"));

        Assert.Empty(Variable(run, POST, "box"));
        AssertUnfollowed(run);
    }

    [Fact]
    public void Default_value_task_completes_with_no_object()
    {
        var run = Solve(Controller("var box = await default(ValueTask<Box>);"));

        Assert.Empty(Variable(run, POST, "box"));
        AssertFollowed(run);
        Assert.DoesNotContain(UnknownSource.Other, Single(Root(run), "box").UnknownSources);
    }

    [Fact]
    public void Parameterless_value_task_completes_with_no_object()
    {
        var run = Solve(Controller("var box = await new ValueTask<Box>();"));

        Assert.Empty(Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void As_task_is_the_same_task()
    {
        var run = Solve(Controller("var wrapped = new ValueTask<Box>(Make()); var pending = wrapped.AsTask(); var box = await pending;", MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([site.Handle!], Variable(run, POST, "pending"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Wait_async_with_a_timeout_is_a_new_task_with_the_value_only()
    {
        var run = Solve(Controller("var pending = Make(); var proxy = pending.WaitAsync(TimeSpan.FromSeconds(1)); var box = await proxy;", MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        var proxy = Assert.Single(Variable(run, POST, "proxy"));
        Assert.NotEqual(site.Handle, proxy);
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[proxy].Kind);
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Wait_async_with_a_token_is_a_new_task_with_the_value_only()
    {
        var run = Solve(Controller("var pending = Make(); var proxy = pending.WaitAsync(CancellationToken.None); var box = await proxy;", MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.DoesNotContain(site.Handle!, Variable(run, POST, "proxy"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Wait_async_of_a_task_completing_unfollowed_completes_unfollowed()
    {
        var run = Solve(Controller("var proxy = Task.FromResult(Native.OpaqueBox()).WaitAsync(CancellationToken.None); var box = await proxy;"));

        Assert.Empty(Variable(run, POST, "box"));
        AssertUnfollowed(run);
    }

    [Fact]
    public void Configure_await_kept_in_a_field_and_awaited_in_another_method_is_the_same_task()
    {
        var run = Solve(Types + """
            public sealed class Holder
            {
                public System.Runtime.CompilerServices.ConfiguredTaskAwaitable<Box> Pending;
                public void Start() => Pending = Make().ConfigureAwait(false);
                public async Task Finish() { var box = await Pending; box.Value = 1; }
                private static async Task<Box> Make() { await Task.Yield(); return new Box(); }
            }
            public class JobsController(Holder holder) : ControllerBase
            {
                public async Task Post() { holder.Start(); await holder.Finish(); }
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([Box(run)], Variable(run, "body:Fixture:M:Holder.Finish", "box"));
        var finish = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.StartsWith("body:Fixture:M:Holder.Finish", StringComparison.Ordinal));
        var join = Assert.Single(finish.Summary.Joins);
        Assert.Equal([site.Handle!], join.Handles.SelectMany(handle => run.Heap.Resolve(finish.Id, handle.Values)).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void Set_result_completes_the_source_s_task_with_the_value()
    {
        var run = Solve(Controller("var source = new TaskCompletionSource<Box>(); source.SetResult(new Box()); var pending = source.Task; var box = await pending;"));

        var source = Assert.Single(Variable(run, POST, "source"));
        Assert.Equal([source], Variable(run, POST, "pending"));
        Assert.Equal([Box(run)], run.Heap.Completion(source));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Try_set_result_completes_the_source_s_task_with_the_value()
    {
        var run = Solve(Controller("var source = new TaskCompletionSource<Box>(); source.TrySetResult(new Box()); var box = await source.Task;"));

        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Set_result_of_an_opaque_value_completes_unfollowed()
    {
        var run = Solve(Controller("var source = new TaskCompletionSource<Box>(); source.SetResult(Native.OpaqueBox()); var box = await source.Task;"));

        Assert.Empty(Variable(run, POST, "box"));
        AssertUnfollowed(run);
    }

    [Fact]
    public void Set_from_task_completes_with_what_that_task_completes_with()
    {
        var run = Solve(Controller("var source = new TaskCompletionSource<Box>(); source.SetFromTask(Make()); var box = await source.Task;", MAKE));

        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void Non_generic_source_completes_with_nothing_followed()
    {
        var run = Solve(Controller("var source = new TaskCompletionSource(); source.SetResult(); await source.Task;"));

        var source = Assert.Single(Variable(run, POST, "source"));
        Assert.Empty(run.Heap.Completion(source));
        AssertFollowed(run);
    }

    [Fact]
    public void When_all_over_listed_tasks_completes_with_an_array_of_their_values()
    {
        var run = Solve(Controller("var all = await Task.WhenAll(Make(), Other()); var first = all[0];", MAKE + " " + OTHER));

        var array = Assert.Single(Variable(run, POST, "all"));
        Assert.Equal(HeapRegionKind.Allocation, run.Heap.Regions[array].Kind);
        Assert.Equal(Boxes(run), Variable(run, POST, "first"));
        Assert.Equal(2, Boxes(run).Count);
        AssertFollowed(run);
    }

    [Fact]
    public void When_all_over_a_collection_completes_with_an_array_of_the_values_of_the_tasks_it_holds()
    {
        var run = Solve(Controller("""
            var tasks = new System.Collections.Generic.List<Task<Box>> { Make(), Other() };
            var all = await Task.WhenAll(tasks);
            var first = all[0];
            """, MAKE + " " + OTHER));

        Assert.Single(Variable(run, POST, "all"));
        Assert.Equal(Boxes(run), Variable(run, POST, "first"));
        Assert.Equal(2, Boxes(run).Count);
        AssertFollowed(run);
    }

    [Fact]
    public void When_any_over_listed_tasks_completes_with_one_of_them()
    {
        var run = Solve(Controller("var first = Make(); var second = Other(); var winner = await Task.WhenAny(first, second); var box = await winner;",
                                   MAKE + " " + OTHER));

        var handles = run.Heap.AsyncSpawns.Select(site => site.Handle!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, handles.Length);
        Assert.Equal(handles, Variable(run, POST, "winner"));
        Assert.Equal(Boxes(run), Variable(run, POST, "box"));
        AssertFollowed(run);
    }

    [Fact]
    public void When_any_over_a_collection_completes_with_one_of_its_tasks_unfollowed()
    {
        var run = Solve(Controller("""
            var tasks = new System.Collections.Generic.List<Task<Box>> { Make() };
            var winner = await Task.WhenAny(tasks);
            var box = await winner;
            """, MAKE));

        var site = Assert.Single(run.Heap.AsyncSpawns);
        Assert.Equal([site.Handle!], Variable(run, POST, "winner"));
        Assert.Equal([Box(run)], Variable(run, POST, "box"));
        AssertUnfollowed(run);
    }

    [Fact]
    public void Unnamed_task_member_gives_a_task_whose_completion_is_unfollowed()
    {
        var run = Solve(Controller("var pending = Task.FromException<Box>(new InvalidOperationException()); var box = await pending;"));

        var task = Assert.Single(Variable(run, POST, "pending"));
        Assert.Equal(HeapRegionKind.Task, run.Heap.Regions[task].Kind);
        Assert.Empty(Variable(run, POST, "box"));
        AssertUnfollowed(run);
    }

    [Fact]
    public void Unnamed_value_task_member_gives_a_task_whose_completion_is_unfollowed()
    {
        var run = Solve(Controller("var box = await new ValueTask<Box>(new Box()).Preserve();"));

        AssertUnfollowed(run);
    }

    /// <summary>Asserts that no await of the root reads a task whose completion may be an object the heap does not follow.</summary>
    /// <param name="run">The solved heap run.</param>
    private static void AssertFollowed(HeapRun run) =>
        Assert.DoesNotContain(run.Heap.UnfollowedCompletions, completion => completion.Instance == Root(run).Id);

    /// <summary>Asserts that an await of the root reads a task whose completion may be an object the heap does not follow.</summary>
    /// <param name="run">The solved heap run.</param>
    private static void AssertUnfollowed(HeapRun run) =>
        Assert.Contains(run.Heap.UnfollowedCompletions, completion => completion.Instance == Root(run).Id);

    private static string Controller(string body, string member = "") => Types + $$"""

        public class JobsController : ControllerBase
        {
            public async Task Post()
            {
                {{body}}
            }
            {{member}}
        }
        """ + Startup();

    private static string Box(HeapRun run) => Assert.Single(Boxes(run));

    private static IReadOnlyList<string> Boxes(HeapRun run) =>
        run.Heap.Regions.Values.Where(region => region.TypeKey == "Fixture:Box").Select(region => region.Identity).Order(StringComparer.Ordinal).ToArray();

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
