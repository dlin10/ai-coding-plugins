using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Timer callbacks (TD-061, TD-061a): which timers run a callback at all, whether it runs once or periodically, and which waits
/// order it. Each access is a write of <c>State.Value</c> in a helper named for its role; <c>A</c> is an action's write.</summary>
public sealed class TimerTests
{
    // ---- System.Threading.Timer ----

    [Fact]
    public void Callback_overlaps_an_action()
    {
        var run = Analyze(Worker("new Timer(_ => state.F(), null, 0, 1000);"));

        Assert.True(Overlap(run, "F", "A"));
    }

    [Fact]
    public void Periodic_callback_with_a_read_modify_write_pairs_with_itself()
    {
        var run = Analyze(Worker("new Timer(_ => state.Rmw(), null, 0, 1000);"));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ""), Callback(run).Policy);
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    [Fact]
    public void State_passed_to_the_timer_is_the_object_the_action_writes()
    {
        var run = Analyze(Worker("new Timer(value => ((State)value!).F(), state, 0, 1000);"));

        Assert.True(Overlap(run, "F", "A"));
    }

    [Fact]
    public void Captured_alias_is_the_object_the_action_writes()
    {
        var run = Analyze(Worker("var alias = state; new Timer(_ => alias.F(), null, 0, 1000);"));

        Assert.True(Overlap(run, "F", "A"));
    }

    [Fact]
    public void Infinite_due_time_without_change_runs_no_callback_and_is_counted()
    {
        var run = Analyze(Worker("new Timer(_ => state.F(), null, Timeout.Infinite, 1000);"));

        Assert.DoesNotContain(run.Accesses("Value"), access => Is(access, "F"));
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.TimerCallback);
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_DISABLED));
        Assert.Equal(0, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    [Fact]
    public void Infinite_due_time_with_change_is_periodic()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, Timeout.Infinite, 1000); timer.Change(0, 1000);"));

        Assert.True(Overlap(run, "F", "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
        Assert.Equal(0, run.Counter(OrderingCounters.TIMERS_DISABLED));
    }

    [Fact]
    public void Change_on_another_timer_does_not_activate()
    {
        var run = Analyze(Worker("var idle = new Timer(_ => state.F(), null, Timeout.Infinite, 1000); " +
                                 "var other = new Timer(_ => state.G(), null, 0, 1000); other.Change(0, 500); GC.KeepAlive(idle);"));

        Assert.DoesNotContain(run.Accesses("Value"), access => Is(access, "F"));
        Assert.True(Overlap(run, "G", "G"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_DISABLED));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    [Fact]
    public void One_shot_callback_does_not_pair_with_itself_but_overlaps_an_action()
    {
        var run = Analyze(Worker("new Timer(_ => state.Rmw(), null, 100, Timeout.Infinite);"));

        Assert.False(Overlap(run, "Rmw", "Rmw"));
        Assert.True(Overlap(run, "Rmw", "A"));
        Assert.Equal(new InvocationPolicy(Multiplicity.AtMostOnce, SelfOverlap.Serialized, ""), Callback(run).Policy);
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_ONE_SHOT));
    }

    [Fact]
    public void One_shot_timer_created_in_an_http_action_may_overlap_itself()
    {
        var run = Analyze(Action("new Timer(_ => state.F(), null, 100, Timeout.Infinite);"));

        Assert.Equal(SelfOverlap.MayOverlap, Callback(run).Policy.SelfOverlap);
        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Non_constant_period_is_periodic()
    {
        var run = Analyze(Worker("new Timer(_ => state.F(), null, 0, _period);", "private readonly int _period = 1000;"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Zero_period_at_creation_is_one_shot()
    {
        var run = Analyze(Worker("new Timer(_ => state.F(), null, 100, 0);"));

        Assert.False(Overlap(run, "F", "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_ONE_SHOT));
    }

    [Fact]
    public void Change_on_a_one_shot_timer_makes_it_periodic()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 100, Timeout.Infinite); timer.Change(0, 0);"));

        Assert.True(Overlap(run, "F", "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    [Fact]
    public void Creator_write_before_the_timer_does_not_overlap_its_callback_and_one_after_it_does()
    {
        var run = Analyze(Worker("state.P1(); new Timer(_ => state.F(), null, 0, 1000); state.P2();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Action_write_before_creating_a_timer_overlaps_the_callback_of_another_request()
    {
        var run = Analyze(Action("state.P1(); new Timer(_ => state.F(), null, 0, 1000);"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Timer_created_in_a_loop_leaves_the_write_before_it_overlapping()
    {
        var run = Analyze(Worker("state.P1(); for (var i = 0; i < 2; i++) new Timer(_ => state.F(), null, 100, Timeout.Infinite);"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Callback_only_constructor_without_change_runs_no_callback()
    {
        var run = Analyze(Worker("GC.KeepAlive(new Timer(_ => state.F()));"));

        Assert.DoesNotContain(run.Accesses("Value"), access => Is(access, "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_DISABLED));
    }

    [Fact]
    public void Callback_only_constructor_with_change_is_periodic_and_gets_the_timer_as_its_state()
    {
        var run = Analyze(Worker("var timer = new Timer(self => { GC.KeepAlive(self); state.F(); }); timer.Change(0, 1000);"));

        Assert.True(Overlap(run, "F", "F"));
        var heap = run.Execution.Heap.Heap;
        var callback = Assert.Single(heap.Instances.Values, instance => instance.BodyId.EndsWith("#lambda1", StringComparison.Ordinal));
        var timer = Assert.Single(Assert.Single(heap.TimerCallbacks).Timers);
        Assert.Equal([timer], callback.Parameters[0]);
    }

    // ---- waits ----

    [Fact]
    public void Dispose_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); timer.Dispose(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_dispose_async_orders_the_creator_s_later_write_and_keeps_the_callback_s_self_pair()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); state.P2(); await timer.DisposeAsync(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Dispose_async_awaited_as_the_first_await_of_an_async_call_orders_the_write_after_it()
    {
        var run = Analyze(Worker("_ = Step();",
                                 "private async Task Step() { var timer = new Timer(_ => state.F(), null, 0, 1000); await timer.DisposeAsync(); state.P1(); }"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_dispose_async_of_a_timer_from_two_sites_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); " +
                                 "if (stoppingToken.CanBeCanceled) timer = new Timer(_ => state.G(), null, 0, 1000); " +
                                 "await timer.DisposeAsync(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
        Assert.True(run.Counter(OrderingCounters.UNPROVEN_JOINS) >= 1);
    }

    [Fact]
    public void Awaited_dispose_async_of_a_timer_created_in_a_loop_orders_nothing()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) { var timer = new Timer(_ => state.F(), null, 0, 1000); await timer.DisposeAsync(); } " +
                                 "state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_dispose_async_of_a_timer_created_in_a_helper_called_in_a_loop_orders_nothing()
    {
        var run = Analyze(Worker("for (var i = 0; i < 2; i++) await Tick();",
                                 "private async Task Tick() { var timer = new Timer(_ => state.F(), null, 0, 1000); await timer.DisposeAsync(); state.P1(); }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Work_a_callback_detaches_overlaps_the_write_after_dispose_async()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => { state.F(); Task.Run(() => state.G()); }, null, 0, 1000); " +
                                 "await timer.DisposeAsync(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Tail_of_an_async_callback_overlaps_the_write_after_dispose_async()
    {
        var run = Analyze(Worker("var timer = new Timer(async _ => { state.F(); await Task.Yield(); state.G(); }, null, 0, 1000); " +
                                 "await timer.DisposeAsync(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Dispose_with_a_wait_handle_orders_the_write_after_the_wait_but_not_the_one_before()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "timer.Dispose(done); state.P2(); done.WaitOne(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Dispose_with_an_auto_reset_event_orders_the_write_after_the_wait()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new AutoResetEvent(false); " +
                                 "timer.Dispose(done); done.WaitOne(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_with_a_timeout_after_dispose_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "timer.Dispose(done); done.WaitOne(100); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Wait_on_another_handle_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "var other = new ManualResetEvent(false); timer.Dispose(done); other.WaitOne(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Handle_created_signalled_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(true); " +
                                 "timer.Dispose(done); done.WaitOne(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Handle_set_somewhere_else_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "Task.Run(() => done.Set()); timer.Dispose(done); done.WaitOne(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Change_on_a_timer_of_unknown_origin_may_activate_an_idle_timer()
    {
        var run = Analyze(Worker("var idle = new Timer(_ => state.F(), null, Timeout.Infinite, 1000); " +
                                 "System.Linq.Enumerable.First(new[] { idle }).Change(0, 1000);"));

        Assert.True(Overlap(run, "F", "F"));
        Assert.Equal(0, run.Counter(OrderingCounters.TIMERS_DISABLED));
    }

    [Fact]
    public void Handle_also_disposed_into_by_a_timer_of_unknown_origin_orders_nothing()
    {
        var run = Analyze(Worker("var first = new Timer(_ => state.F(), null, 0, 1000); var second = new Timer(_ => state.G(), null, 0, 1000); " +
                                 "var done = new ManualResetEvent(false); first.Dispose(done); System.Linq.Enumerable.First(new[] { second }).Dispose(done); " +
                                 "done.WaitOne(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Handle_disposed_by_two_timers_orders_neither_callback()
    {
        var run = Analyze(Worker("var first = new Timer(_ => state.F(), null, 0, 1000); var second = new Timer(_ => state.G(), null, 0, 1000); " +
                                 "var done = new ManualResetEvent(false); first.Dispose(done); second.Dispose(done); done.WaitOne(); state.P1();"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P1", "G"));
    }

    [Fact]
    public void Elapsed_callback_is_a_child_of_the_execution_that_created_the_timer()
    {
        var source = STATE + """
            public sealed class Ticker
            {
                private readonly System.Timers.Timer _timer;
                private readonly State _state;
                public Ticker(State state)
                {
                    _state = state;
                    _timer = new System.Timers.Timer(100);
                }
                public void Subscribe() { _timer.Elapsed += (_, _) => _state.F(); _timer.Start(); }
            }
            public class TickerController(Ticker ticker) : ControllerBase { public void Arm() => ticker.Subscribe(); }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Ticker>();");
        var run = Analyze(source);

        var callback = Callback(run);
        var parent = run.Execution.Analysis.Execution(callback.ParentId!);
        Assert.Equal(ExecutionKind.LazyConstruction, parent.Kind);
        Assert.Equal("Ticker..ctor(State)", callback.Origin!.Symbol);
        var access = Assert.Single(run.Accesses("Value"), access => Is(access, "F"));
        Assert.Equal(parent.TreeRootId, access.Root.RootId);
        var text = Usings + source;
        var creationLine = text[..text.IndexOf("new System.Timers.Timer(100)", StringComparison.Ordinal)].Count(character => character == '\n') + 1;
        var site = Assert.Single(access.SpawnSites);
        Assert.StartsWith("timer-callback:System.Timers.Timer@Ticker..ctor(State)", site.Segment, StringComparison.Ordinal);
        Assert.Equal(creationLine, site.Source.StartLine);
    }

    [Fact]
    public void One_shot_elapsed_timer_subscribed_in_a_repeated_action_pairs_with_itself()
    {
        var run = Analyze(STATE + """
            public sealed class Ticker
            {
                private readonly System.Timers.Timer _timer;
                private readonly State _state;
                public Ticker(State state)
                {
                    _state = state;
                    _timer = new System.Timers.Timer(100);
                    _timer.AutoReset = false;
                    _timer.Start();
                }
                public void Subscribe() => _timer.Elapsed += (_, _) => _state.Rmw();
            }
            public class TickerController(Ticker ticker) : ControllerBase { public void Arm() => ticker.Subscribe(); }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Ticker>();"));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
    }

    [Fact]
    public void Handle_passed_to_another_dispose_method_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "timer.Dispose(done); _thirdParty!.Dispose(done); state.P2(); done.WaitOne(); state.P1();",
                                 "public abstract class ThirdParty { public abstract void Dispose(WaitHandle handle); } " +
                                 "private readonly ThirdParty? _thirdParty = null;"));

        Assert.True(Overlap(run, "P1", "F"));
        Assert.True(Overlap(run, "P2", "F"));
    }

    [Fact]
    public void Wait_on_only_one_branch_after_dispose_orders_nothing()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); " +
                                 "timer.Dispose(done); if (stoppingToken.CanBeCanceled) { done.WaitOne(); state.P1(); }"));

        Assert.True(Overlap(run, "P1", "F"));
    }

    // ---- System.Timers.Timer ----

    [Fact]
    public void Started_elapsed_timer_with_default_auto_reset_pairs_with_itself()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.Elapsed += (_, _) => state.Rmw(); timer.Start();"));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.True(Overlap(run, "Rmw", "A"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    [Fact]
    public void Elapsed_timer_never_started_runs_no_handler()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.Elapsed += (_, _) => state.F();"));

        Assert.DoesNotContain(run.Accesses("Value"), access => Is(access, "F"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_DISABLED));
    }

    [Fact]
    public void Elapsed_subscription_to_a_known_disabled_or_an_unknown_timer_runs_a_periodic_handler()
    {
        var run = Analyze(Worker("var known = new System.Timers.Timer(100); " +
                                 "var timer = _flag ? known : System.Activator.CreateInstance<System.Timers.Timer>(); " +
                                 "timer.Elapsed += (_, _) => state.Rmw();",
                                 "private readonly bool _flag;"));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.All(Callbacks(run), callback => Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ""), callback.Policy));
    }

    [Fact]
    public void Elapsed_subscription_to_a_timer_an_implementation_does_not_name_runs_a_periodic_handler()
    {
        var run = Analyze(Worker("var known = new System.Timers.Timer(100); " +
                                 "var timer = _flag ? known : _factory.Get(); timer.Elapsed += (_, _) => state.Rmw();",
                                 FACTORY));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.All(Callbacks(run), callback => Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ""), callback.Policy));
    }

    [Fact]
    public void Change_of_a_timer_an_implementation_does_not_name_makes_every_timer_periodic()
    {
        var run = Analyze(Worker("var quiet = new Timer(_ => state.Rmw(), null, Timeout.Infinite, Timeout.Infinite); " +
                                 "var other = new Timer(_ => state.P2(), null, Timeout.Infinite, Timeout.Infinite); " +
                                 "(_flag ? other : _factory.Make()).Change(0, 1000);",
                                 FACTORY));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
    }

    [Fact]
    public void Elapsed_subscription_to_a_helper_that_may_return_an_unknown_timer_runs_a_periodic_handler()
    {
        var run = Analyze(Worker("_helper.Known = new System.Timers.Timer(100); _helper.Get().Elapsed += (_, _) => state.Rmw();", HELPER));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.All(Callbacks(run), callback => Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ""), callback.Policy));
    }

    [Fact]
    public void Access_before_the_creation_site_overlaps_the_callback_of_a_mixed_source()
    {
        var run = Analyze(Worker("state.P1(); _helper.Known = new System.Timers.Timer(100); _helper.Known.Start(); " +
                                 "_helper.Get().Elapsed += (_, _) => state.F();", HELPER));

        Assert.True(Overlap(run, "P1", "F"));
    }

    [Fact]
    public void Awaited_dispose_async_of_a_proven_timer_counts_no_unproven_join()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); await timer.DisposeAsync(); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.Equal(0, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Awaited_dispose_async_of_a_timer_of_unknown_origin_counts_one_unproven_join()
    {
        var run = Analyze(Worker("var timer = _flag ? new Timer(_ => state.F(), null, 0, 1000) : _factory.Make(); " +
                                 "await timer.DisposeAsync(); state.P1();",
                                 FACTORY));

        Assert.Equal(1, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Awaited_dispose_async_through_configure_await_orders_the_write_after_it()
    {
        var run = Analyze(Worker("var timer = new Timer(_ => state.F(), null, 0, 1000); " +
                                 "await timer.DisposeAsync().ConfigureAwait(false); state.P1();"));

        Assert.False(Overlap(run, "P1", "F"));
        Assert.Equal(0, run.Counter(OrderingCounters.UNPROVEN_JOINS));
    }

    [Fact]
    public void Elapsed_subscription_to_a_timer_a_dispatch_the_heap_cannot_resolve_returned_runs_a_periodic_handler()
    {
        var run = Analyze(Worker("_provider.Known = new System.Timers.Timer(100); " +
                                 "ITimerProvider provider = _flag ? _provider : System.Activator.CreateInstance<ITimerProvider>(); " +
                                 "provider.Get().Elapsed += (_, _) => state.Rmw();",
                                 PROVIDER));

        Assert.True(Overlap(run, "Rmw", "Rmw"));
        Assert.All(Callbacks(run), callback => Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ""), callback.Policy));
    }

    [Fact]
    public void Elapsed_timer_enabled_by_the_property_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.Elapsed += (_, _) => state.F(); timer.Enabled = true;"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Elapsed_timer_only_disabled_by_the_property_runs_no_handler()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.Elapsed += (_, _) => state.F(); timer.Enabled = false;"));

        Assert.DoesNotContain(run.Accesses("Value"), access => Is(access, "F"));
    }

    [Fact]
    public void Auto_reset_off_before_the_one_start_is_one_shot()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.AutoReset = false; timer.Elapsed += (_, _) => state.Rmw(); timer.Start();"));

        Assert.False(Overlap(run, "Rmw", "Rmw"));
        Assert.True(Overlap(run, "Rmw", "A"));
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_ONE_SHOT));
    }

    [Fact]
    public void Auto_reset_off_in_one_branch_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); if (stoppingToken.CanBeCanceled) timer.AutoReset = false; " +
                                 "timer.Elapsed += (_, _) => state.F(); timer.Start();"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Auto_reset_off_after_the_start_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.Elapsed += (_, _) => state.F(); timer.Start(); timer.AutoReset = false;"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Auto_reset_off_with_two_starts_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.AutoReset = false; timer.Elapsed += (_, _) => state.F(); " +
                                 "timer.Start(); timer.Start();"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Auto_reset_off_restarted_by_its_own_handler_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.AutoReset = false; " +
                                 "timer.Elapsed += (_, _) => { state.F(); timer.Start(); }; timer.Start();"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Auto_reset_off_started_in_a_loop_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.AutoReset = false; timer.Elapsed += (_, _) => state.F(); " +
                                 "for (var i = 0; i < 2; i++) timer.Start();"));

        Assert.True(Overlap(run, "F", "F"));
    }

    [Fact]
    public void Auto_reset_on_is_periodic()
    {
        var run = Analyze(Worker("var timer = new System.Timers.Timer(100); timer.AutoReset = true; timer.Elapsed += (_, _) => state.F(); timer.Start();"));

        Assert.True(Overlap(run, "F", "F"));
    }

    // ---- counting and PeriodicTimer ----

    [Fact]
    public void Creation_site_disabled_in_one_context_and_periodic_in_another_is_counted_once_as_periodic()
    {
        var run = Analyze("""
            public sealed class State { public int Value; public void F() => Value = 1; public void A() => Value = 2; }
            public abstract class Ticking : BackgroundService
            {
                protected readonly State Shared;
                protected Ticking(State state) => Shared = state;
                protected Timer Make() => new Timer(_ => Shared.F(), null, Timeout.Infinite, 1000);
            }
            public sealed class Idle(State state) : Ticking(state)
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { GC.KeepAlive(Make()); return Task.CompletedTask; }
            }
            public sealed class Busy(State state) : Ticking(state)
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Make().Change(0, 1000); return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Idle>(); services.AddHostedService<Busy>();"));

        Assert.Equal(2, run.Execution.Heap.Heap.Instances.Values.Count(instance => instance.BodyId.EndsWith("Ticking.Make", StringComparison.Ordinal)));
        Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.TimerCallback);
        Assert.Equal(1, run.Counter(OrderingCounters.TIMERS_PERIODIC));
        Assert.Equal(0, run.Counter(OrderingCounters.TIMERS_DISABLED));
    }

    [Fact]
    public void Periodic_timer_iterations_do_not_overlap_each_other_but_overlap_an_action()
    {
        var run = Analyze(Worker("using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); " +
                                 "while (await timer.WaitForNextTickAsync(stoppingToken)) state.Rmw();"));

        Assert.False(Overlap(run, "Rmw", "Rmw"));
        Assert.True(Overlap(run, "Rmw", "A"));
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.TimerCallback);
        Assert.Equal(0, run.Counter(OrderingCounters.TIMERS_PERIODIC));
    }

    private static bool Overlap(EngineRun run, string one, string other)
    {
        Assert.Contains(run.Accesses("Value"), access => Is(access, one));
        Assert.Contains(run.Accesses("Value"), access => Is(access, other));
        return run.PairsOn("Value").Any(pair => Is(pair.First, one) && Is(pair.Second, other) || Is(pair.First, other) && Is(pair.Second, one));
    }

    private static bool Is(Access access, string helper) => access.Symbol.EndsWith($".{helper}()", StringComparison.Ordinal);

    private static IReadOnlyList<ExecutionInstance> Callbacks(EngineRun run) =>
        run.Execution.Analysis.Executions.Where(execution => execution.Kind == ExecutionKind.TimerCallback).ToArray();

    private static ExecutionInstance Callback(EngineRun run) =>
        Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.TimerCallback);

    private const string STATE = """
        public sealed class State
        {
            public int Value;
            public void P1() => Value = 1;
            public void P2() => Value = 2;
            public void F() => Value = 3;
            public void G() => Value = 4;
            public void A() => Value = 5;
            public void Rmw() => Value++;
        }
        public class GateController(State state) : ControllerBase { public void Post() => state.A(); }
        """;

    /// <summary>A helper whose <c>Get</c> returns either the timer it was handed or one from an opaque call.</summary>
    private const string HELPER = """
        private readonly TimerHelper _helper = new TimerHelper();
        public sealed class TimerHelper
        {
            public bool Flag;
            public System.Timers.Timer? Known;
            public System.Timers.Timer Get() => Flag ? Known! : System.Activator.CreateInstance<System.Timers.Timer>();
        }
        """;

    /// <summary>An interface with an implementation in the program, handed out through a receiver the heap cannot follow.</summary>
    private const string PROVIDER = """
        private readonly bool _flag;
        private readonly TimerProvider _provider = new TimerProvider();
        public interface ITimerProvider { System.Timers.Timer Get(); }
        public sealed class TimerProvider : ITimerProvider
        {
            public System.Timers.Timer? Known;
            public System.Timers.Timer Get() => Known!;
        }
        """;

    /// <summary>An interface whose implementation returns timers the heap cannot name.</summary>
    private const string FACTORY = """
        private readonly bool _flag;
        private readonly IFactory _factory = new Factory();
        private interface IFactory { System.Timers.Timer Get(); Timer Make(); }
        private sealed class Factory : IFactory
        {
            private System.Timers.Timer? _elapsed;
            private Timer? _threading;
            public System.Timers.Timer Get() => _elapsed!;
            public Timer Make() => _threading!;
        }
        """;

    private static string Worker(string body, string members = "") => STATE + $$"""
        public sealed class Worker(State state) : BackgroundService
        {
            {{members}}
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    private static string Action(string body) => STATE + $$"""
        public class TimerController(State state) : ControllerBase
        {
            public void Start()
            {
                {{body}}
            }
        }
        """ + Startup("services.AddSingleton<State>();");
}
