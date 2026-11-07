using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Rollouts;
using Xunit;
using static CodexSessionPulse.Infrastructure.Tests.Rollouts.RolloutTestRoot;

namespace CodexSessionPulse.Infrastructure.Tests.Rollouts;

/// <summary>The live source end to end: watcher plus polling into the engine (spec §5.2, §11.2).</summary>
public sealed class RolloutTailerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly RolloutTestRoot _root = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        _root.Dispose();
    }

    /// <summary>History on start, then a live append, reach the engine; the observation comes before its usage.</summary>
    [Fact]
    public async Task WatchAsync_StartAndLiveAppend_ReachTheEngine()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(_root.Root) with { PollInterval = TimeSpan.FromSeconds(1) };
        await using RolloutTailer tailer = new(options, _root.Clock);
        PulseEngine engine = new(CacheTrackerOptions.Default, _root.Clock);
        List<string> order = [];
        tailer.ThreadObserved += o =>
        {
            order.Add("meta");
            engine.Observe(o);
        };
        using CancellationTokenSource cts = new(Patience);
        await using IAsyncEnumerator<TokenUsageSnapshot> events = tailer.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await events.MoveNextAsync());
        order.Add("usage");
        engine.Apply(events.Current);
        Append(path, TokenCount(2, 2, 11000, 10500) + "\n");
        Assert.True(await events.MoveNextAsync());
        CacheSnapshot snapshot = engine.Apply(events.Current);

        Assert.Equal(["meta", "usage"], order);
        Assert.Equal(11000, snapshot.Input);
        Assert.Equal(ThreadA, snapshot.ThreadId);
        Assert.Equal(SourceStatus.Available, tailer.Status);
    }

    /// <summary>A rollout created after the start in a new day folder is found without a restart.</summary>
    [Fact]
    public async Task WatchAsync_NewFileAfterStart_IsFound()
    {
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(_root.Root) with { PollInterval = TimeSpan.FromSeconds(1) };
        await using RolloutTailer tailer = new(options, _root.Clock);
        using CancellationTokenSource cts = new(Patience);
        await using IAsyncEnumerator<TokenUsageSnapshot> events = tailer.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = events.MoveNextAsync();

        await Task.Delay(200, cts.Token);
        _root.Clock.UtcNow = _root.Clock.UtcNow.AddDays(1);
        File.WriteAllText(_root.RolloutPath(ThreadB), Meta(ThreadB) + "\n" + TokenCount(1, 1, 20000, 19000) + "\n");

        Assert.True(await next);
        Assert.Equal(ThreadB, events.Current.ThreadId);
    }

    /// <summary>Disposing the source while it is being enumerated ends the enumeration without an exception.</summary>
    [Fact]
    public async Task DisposeAsync_WhileEnumerating_EndsTheEnumeration()
    {
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(_root.Root) with { PollInterval = TimeSpan.FromSeconds(1) };
        RolloutTailer tailer = new(options, _root.Clock);
        using CancellationTokenSource cts = new(Patience);
        await using IAsyncEnumerator<TokenUsageSnapshot> events = tailer.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = events.MoveNextAsync();

        await Task.Delay(300, cts.Token);
        await tailer.DisposeAsync();

        Assert.False(await next);
        Assert.False(cts.IsCancellationRequested);
    }

    /// <summary>Cancelling ends the enumeration without an exception.</summary>
    [Fact]
    public async Task WatchAsync_Cancelled_Ends()
    {
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(_root.Root) with { PollInterval = TimeSpan.FromSeconds(1) };
        await using RolloutTailer tailer = new(options, _root.Clock);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        int count = 0;

        await foreach (TokenUsageSnapshot usage in tailer.WatchAsync(cts.Token))
        {
            count++;
        }

        Assert.Equal(0, count);
    }
}
