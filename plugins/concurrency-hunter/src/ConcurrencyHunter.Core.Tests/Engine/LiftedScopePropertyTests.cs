using ConcurrencyHunter.Accesses;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A scope a callee opens and its caller closes keeps what was true of it in the callee: a holding over a suspension point
/// and permits that do not pair survive every level they are lifted through (ADR 0009, TD-083).</summary>
public sealed class LiftedScopePropertyTests
{
    private const string MONITOR_WRITE = "lock (_state.Gate) { _state.Value = 2; }";
    private const string SEMAPHORE_WRITE = "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }";

    [Fact]
    public void Monitor_an_iterator_leaves_open_over_a_yield_is_partial_after_the_loop()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { } try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }",
                               MONITOR_WRITE, "public IEnumerable<int> Walk() { Monitor.Enter(Gate); yield return 1; }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Semaphore_an_iterator_leaves_open_over_a_yield_stays_sufficient_after_the_loop()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { } try { _state.Value = 1; } finally { _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public IEnumerable<int> Walk() { One.Wait(); yield return 1; }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Unpaired_release_in_the_callee_gives_the_caller_no_protection()
    {
        var run = Analyze(Case("_state.Enter(); try { _state.Value = 1; } finally { _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public void Enter() { One.Release(2); One.Wait(); }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Unpaired_release_in_an_iterator_gives_the_code_after_the_loop_no_protection()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { } try { _state.Value = 1; } finally { _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public IEnumerable<int> Walk() { One.Release(2); One.Wait(); yield return 1; }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Holding_over_a_suspension_survives_two_levels_of_lifting()
    {
        var run = Analyze(Case("_state.Outer(); try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }", MONITOR_WRITE,
                               "public IEnumerable<int> Walk() { Monitor.Enter(Gate); yield return 1; } " +
                               "public void Drain() { foreach (var value in Walk()) { } } " +
                               "public void Outer() { Drain(); }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Unpaired_permits_survive_two_levels_of_lifting()
    {
        var run = Analyze(Case("_state.Outer(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public void Enter() { One.Release(2); One.Wait(); } public void Outer() { Enter(); }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Monitor_wrapper_without_a_suspension_stays_sufficient()
    {
        var run = Analyze(Case("_state.Enter(); try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }", MONITOR_WRITE,
                               "public void Enter() { Monitor.Enter(Gate); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Monitor_wrapper_without_a_suspension_stays_sufficient_through_two_levels()
    {
        var run = Analyze(Case("_state.Outer(); try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }", MONITOR_WRITE,
                               "public void Enter() { Monitor.Enter(Gate); } public void Outer() { Enter(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Paired_semaphore_wrapper_stays_sufficient_through_two_levels()
    {
        var run = Analyze(Case("_state.Outer(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public void Enter() { One.Wait(); } public void Outer() { Enter(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Holding_over_a_suspension_in_one_of_two_callees_weakens_the_lifted_entry()
    {
        var run = Analyze(Case("_state.Opener.Enter(_state); try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }", MONITOR_WRITE,
                               "public readonly IOpener Opener = DateTime.UtcNow.Ticks > 0 ? new AcquiringOpener() : new WalkingOpener(); " +
                               "public IEnumerable<int> Walk() { Monitor.Enter(Gate); yield return 1; }") + """
            public interface IOpener { void Enter(State state); }
            public sealed class AcquiringOpener : IOpener { public void Enter(State state) { Monitor.Enter(state.Gate); } }
            public sealed class WalkingOpener : IOpener { public void Enter(State state) { foreach (var value in state.Walk()) { } } }
            """);
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Unpaired_permits_survive_a_lifted_entry_into_a_semaphore_already_held()
    {
        var run = Analyze(Case("_state.One.Wait(); _state.Enter(); try { _state.Value = 1; } finally { _state.One.Release(); _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public void Enter() { One.Release(2); One.Wait(); }"));
        AssertPartialUnderHeldLock(run);
    }

    [Fact]
    public void Paired_lifted_entry_into_a_semaphore_already_held_stays_sufficient()
    {
        var run = Analyze(Case("_state.One.Wait(); _state.Enter(); try { _state.Value = 1; } finally { _state.One.Release(); _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public void Enter() { One.Wait(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Callee_that_may_leave_the_callers_monitor_leaves_the_code_after_it_unprotected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); _state.Done(); _state.Value = 1;", MONITOR_WRITE,
                               "public void Done() { if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Gate); }"));
        AssertFirstUnprotected(run);
    }

    [Fact]
    public void Callee_that_may_leave_the_callers_monitor_does_so_through_two_levels()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); _state.Outer(); _state.Value = 1;", MONITOR_WRITE,
                               "public void Done() { if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Gate); } public void Outer() { Done(); }"));
        AssertFirstUnprotected(run);
    }

    [Fact]
    public void Handler_around_a_callee_that_leaves_the_callers_monitor_and_throws_is_unprotected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); try { _state.Fail(); } catch (Exception) { _state.Value = 1; }", MONITOR_WRITE,
                               "public void Fail() { Monitor.Exit(Gate); throw new Exception(); }"));
        AssertFirstUnprotected(run);
    }

    [Fact]
    public void Callee_taking_the_callers_monitor_again_keeps_the_callers_holding()
    {
        var run = Analyze(Case("lock (_state.Gate) { _state.Locked(); _state.Value = 1; }", MONITOR_WRITE,
                               "public void Locked() { lock (Gate) { } }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Callee_that_may_leave_a_monitor_the_caller_does_not_hold_keeps_the_callers_monitor()
    {
        var run = Analyze(Case("lock (_state.Gate) { _state.Leave(); _state.Value = 1; }", MONITOR_WRITE,
                               "public readonly object Other = new(); " +
                               "public void Leave() { if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Other); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    private static void AssertFirstUnprotected(EngineRun run)
    {
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(Assert.Single(new[] { pair.First, pair.Second }, side => side.Symbol.Contains("First.", StringComparison.Ordinal))
                                .HeldProtection);
    }

    [Fact]
    public void Callee_that_lets_go_of_the_callers_lock_on_one_branch_takes_its_protection()
    {
        var run = Analyze(Case("lock (_state.Gate) { _state.Toggle(_state.Flag); _state.Value = 1; }", MONITOR_WRITE,
                               "public bool Flag; public void Toggle(bool flag) { if (flag) Monitor.Enter(Gate); else Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(new[] { pair.First, pair.Second }.Single(side => side.Symbol.Contains("First.", StringComparison.Ordinal)).HeldProtection);
    }

    [Fact]
    public void Callee_that_takes_and_leaves_its_own_lock_keeps_the_callers()
    {
        var run = Analyze(Case("lock (_state.Gate) { _state.Guarded(); _state.Value = 1; }", MONITOR_WRITE,
                               "public bool Flag; public void Guarded() { lock (Gate) { Flag = true; } }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    /// <summary>The one surviving pair is the lifted write against the other worker's, and the lifted write does stand under the
    /// carried lock — which excludes nobody.</summary>
    private static void AssertPartialUnderHeldLock(EngineRun run)
    {
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        var lifted = Assert.Single(new[] { pair.First, pair.Second }, side => side.Symbol.Contains("First.", StringComparison.Ordinal));
        Assert.Contains(new[] { pair.First, pair.Second }, side => side.Symbol.Contains("Second.", StringComparison.Ordinal));
        Assert.NotEmpty(lifted.HeldProtection);
        Assert.All(lifted.HeldProtections.Values.SelectMany(held => held), held => Assert.False(held.IsExclusive));
    }

    private static string Case(string first, string second, string members) => Usings + $$"""
        using System.Collections.Generic;
        public sealed class State
        {
            public readonly object Gate = new();
            public readonly SemaphoreSlim One = new(1, 1);
            public int Value;
            {{members}}
        }
        public sealed class First : BackgroundService
        {
            private readonly State _state;
            public First(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }
        public sealed class Second : BackgroundService
        {
            private readonly State _state;
            public Second(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<First>(); services.AddHostedService<Second>();");
}
