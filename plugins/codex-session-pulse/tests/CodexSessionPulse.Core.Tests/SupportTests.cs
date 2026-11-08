using Xunit;
using static CodexSessionPulse.Core.Tests.TestUsage;

namespace CodexSessionPulse.Core.Tests;

/// <summary>The small additive pieces: hook counts, the in-memory source, the publisher.</summary>
public sealed class SupportTests
{
    /// <summary>Hooks are counted per type with the last time and type.</summary>
    [Fact]
    public void HookActivityRecorder_CountsPerType()
    {
        HookActivityRecorder recorder = new();
        recorder.Record("SessionStart", T0);
        recorder.Record("UserPromptSubmit", T0.AddSeconds(1));
        recorder.Record("UserPromptSubmit", T0.AddSeconds(2));

        HookActivitySnapshot snapshot = recorder.Snapshot();

        Assert.Equal(1, snapshot.CountsByHookType["SessionStart"]);
        Assert.Equal(2, snapshot.CountsByHookType["UserPromptSubmit"]);
        Assert.Equal(T0.AddSeconds(2), snapshot.LastHookAtUtc);
        Assert.Equal("UserPromptSubmit", snapshot.LastSource);
    }

    /// <summary>The in-memory source yields what was published, then ends.</summary>
    [Fact]
    public async Task InMemorySessionEventSource_YieldsPublishedEvents()
    {
        await using InMemorySessionEventSource source = new();
        source.Publish(Create("A", T0, 10, 0));
        source.Publish(Create("B", T0, 20, 0));
        source.Complete();
        List<string> threads = [];

        await foreach (TokenUsageSnapshot usage in source.WatchAsync(CancellationToken.None))
        {
            threads.Add(usage.ThreadId);
        }

        Assert.Equal(["A", "B"], threads);
    }

    /// <summary>A disposed subscription gets no more values.</summary>
    [Fact]
    public void SnapshotPublisher_Unsubscribe_StopsDelivery()
    {
        SnapshotPublisher<string> publisher = new();
        List<string> received = [];
        IDisposable subscription = publisher.Subscribe(new Collector(received));

        publisher.Publish("one");
        subscription.Dispose();
        publisher.Publish("two");

        Assert.Equal(["one"], received);
        Assert.Equal("two", publisher.Current);
    }

    private sealed class Collector(List<string> received) : IObserver<string>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string value)
        {
            received.Add(value);
        }
    }
}
