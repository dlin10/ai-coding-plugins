using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>An async call nobody awaits at once returns to its caller at the callee's first <c>await</c>, so the caller holds what
/// the synchronous prefix holds there, and nothing the rest of the body takes afterwards (TD-060a, ADR 0009).</summary>
public sealed class AsyncPrefixLiftTests
{
    private const string MONITOR_WRITE = "lock (_state.Gate) { _state.Value = 2; }";
    private const string SEMAPHORE_WRITE = "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }";

    [Fact]
    public void Acquisition_after_the_first_await_does_not_protect_the_caller()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public async Task EnterAsync() { await Task.Yield(); One.Wait(); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(Caller(pair).HeldProtection);
    }

    [Fact]
    public void Acquisition_in_the_synchronous_prefix_is_lifted_to_the_caller()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public async Task EnterAsync() { One.Wait(); await Task.Yield(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Monitor_lifted_from_the_prefix_over_the_first_await_is_partial()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { Monitor.Exit(_state.Gate); }", MONITOR_WRITE,
                               "public async Task EnterAsync() { Monitor.Enter(Gate); await Task.Yield(); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.NotEmpty(Caller(pair).HeldProtection);
        Assert.All(Caller(pair).HeldProtections.Values.SelectMany(held => held), held => Assert.False(held.IsExclusive));
    }

    [Fact]
    public void Unpaired_permits_from_the_prefix_give_the_caller_no_protection()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public async Task EnterAsync() { One.Release(2); One.Wait(); await Task.Yield(); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.NotEmpty(Caller(pair).HeldProtection);
        Assert.All(Caller(pair).HeldProtections.Values.SelectMany(held => held), held => Assert.False(held.IsExclusive));
    }

    [Fact]
    public void Awaited_call_still_lifts_what_the_whole_body_leaves_open()
    {
        var run = Analyze(Case("await _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public async Task EnterAsync() { await Task.Yield(); One.Wait(); }", isAsync: true));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Unawaited_call_without_an_acquisition_leaves_the_callers_own_lock_alone()
    {
        var run = Analyze(Case("_state.One.Wait(); try { _ = _state.TouchAsync(); _state.Value = 1; } finally { _state.One.Release(); }",
                               SEMAPHORE_WRITE, "public async Task TouchAsync() { await Task.Yield(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Prefix_acquisition_the_tail_releases_does_not_protect_the_caller()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "public async Task EnterAsync() { One.Wait(); await Task.Yield(); One.Release(); }"));
        Assert.Empty(Caller(Assert.Single(run.PairsOn("Value"))).HeldProtection);
    }

    [Fact]
    public void Async_body_without_an_await_hands_back_its_exit()
    {
        var run = Analyze(Case("_ = _state.EnterAsync(); try { _state.Value = 1; } finally { _state.One.Release(); }", SEMAPHORE_WRITE,
                               "#pragma warning disable CS1998\npublic async Task EnterAsync() { One.Wait(); }\n#pragma warning restore CS1998"));
        Assert.Empty(run.PairsOn("Value"));
    }

    private static Access Caller(AccessPair pair) =>
        Assert.Single(new[] { pair.First, pair.Second }, side => side.Symbol.Contains("First.", StringComparison.Ordinal));

    private static string Case(string first, string second, string members, bool isAsync = false) => Usings + $$"""
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
            protected override {{(isAsync ? "async " : "")}}Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                {{(isAsync ? "" : "return Task.CompletedTask;")}}
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
