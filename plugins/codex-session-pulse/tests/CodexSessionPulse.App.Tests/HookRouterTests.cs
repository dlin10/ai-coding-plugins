using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>The hook-confirmed selection survives an engine recreated by a settings save.</summary>
public sealed class HookRouterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A prompt hook selects the older thread; a new engine fed the same JSONL gets that selection back on replay.</summary>
    [Fact]
    public void Replay_GivesTheNewEngineTheHookSelection()
    {
        HookRouter router = new();
        PulseEngine first = EngineWithTwoThreads();

        router.Route(first, new HookMessage("UserPromptSubmit", "A", null, T0.AddMinutes(2)), T0.AddMinutes(2));
        PulseEngine second = EngineWithTwoThreads();
        string? beforeReplay = second.Tick().ThreadId;
        router.Replay(second);

        Assert.Equal("A", first.Tick().ThreadId);
        Assert.Equal("B", beforeReplay);
        Assert.Equal(first.Tick().Selection, second.Tick().Selection);
        Assert.Equal("A", second.Tick().ThreadId);
    }

    /// <summary>
    /// As in the overlay, the new engine has read no JSONL yet when the selection is replayed; the tail that follows
    /// still leaves the hook-confirmed thread on display.
    /// </summary>
    [Fact]
    public void Replay_IntoAnEngineThatHasReadNothingYet_KeepsTheSelection()
    {
        HookRouter router = new();
        router.Route(null, new HookMessage("UserPromptSubmit", "A", null, T0.AddMinutes(2)), T0.AddMinutes(2));
        PulseEngine engine = new(CacheTrackerOptions.Default, new FixedClock(T0.AddMinutes(5)));

        router.Replay(engine);
        engine.Observe(new ThreadObservation("A", ThreadKind.Main, null, "A", @"C:\r\a.jsonl", null, T0));
        engine.Observe(new ThreadObservation("B", ThreadKind.Main, null, "B", @"C:\r\b.jsonl", null, T0.AddMinutes(1)));

        Assert.Equal("A", engine.Tick().ThreadId);
        Assert.Equal(SelectionConfidence.Confirmed, engine.Tick().Selection.Confidence);
    }

    /// <summary>The thread's SessionEnd forgets it, and a hook without a thread id or of another event changes nothing.</summary>
    [Fact]
    public void Replay_AfterSessionEndOrForeignHooks_SelectsNothing()
    {
        HookRouter router = new();

        router.Route(null, new HookMessage("UserPromptSubmit", "A", null, T0.AddMinutes(2)), T0.AddMinutes(2));
        router.Route(null, new HookMessage("SessionEnd", "A", null, T0.AddMinutes(3)), T0.AddMinutes(3));
        router.Route(null, new HookMessage("SessionStart", null, "startup", T0.AddMinutes(4)), T0.AddMinutes(4));
        router.Route(null, new HookMessage(HookMessage.Other, "A", null, T0.AddMinutes(5)), T0.AddMinutes(5));
        PulseEngine engine = EngineWithTwoThreads();
        router.Replay(engine);

        Assert.Equal("B", engine.Tick().ThreadId);
    }

    private static PulseEngine EngineWithTwoThreads()
    {
        PulseEngine engine = new(CacheTrackerOptions.Default, new FixedClock(T0.AddMinutes(5)));
        engine.Observe(new ThreadObservation("A", ThreadKind.Main, null, "A", @"C:\r\a.jsonl", null, T0));
        engine.Observe(new ThreadObservation("B", ThreadKind.Main, null, "B", @"C:\r\b.jsonl", null, T0.AddMinutes(1)));

        return engine;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
