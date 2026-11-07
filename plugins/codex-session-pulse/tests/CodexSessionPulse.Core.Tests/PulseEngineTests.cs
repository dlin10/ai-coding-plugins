using Xunit;
using static CodexSessionPulse.Core.Tests.TestUsage;

namespace CodexSessionPulse.Core.Tests;

/// <summary>Independent thread states, lifecycle and automatic selection (spec §4.4, §5.3, §11.1 items 6–8).</summary>
public sealed class PulseEngineTests
{
    private readonly FakeClock _clock = new(T0);
    private readonly PulseEngine _engine;

    /// <summary>Creates an engine on a fake clock.</summary>
    public PulseEngineTests()
    {
        _engine = new PulseEngine(CacheTrackerOptions.Default, _clock);
    }

    /// <summary>Without any thread the snapshot is NoSession with an unknown selection.</summary>
    [Fact]
    public void Tick_NoThreads_IsNoSession()
    {
        CacheSnapshot snapshot = _engine.Tick();

        Assert.Equal(CacheState.NoSession, snapshot.State);
        Assert.Null(snapshot.ThreadId);
        Assert.Equal(new ThreadSelection(SelectionConfidence.Unknown, SelectionReasons.NoThread), snapshot.Selection);
    }

    /// <summary>A thread seen in session_meta without usage is waiting, with its CLI version in the source.</summary>
    [Fact]
    public void Observe_SessionWithoutUsage_IsWaitingWithCliVersion()
    {
        CacheSnapshot snapshot = _engine.Observe(new ThreadObservation("A", ThreadKind.Main, null, "A", "C:\\r\\a.jsonl",
                                                                       "0.167.0-alpha.3.2", T0));

        Assert.Equal(CacheState.WaitingForUsage, snapshot.State);
        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal("0.167.0-alpha.3.2", snapshot.Source.CliVersion);
        Assert.Equal(SelectionReasons.LatestJsonlActivity, snapshot.Selection.Reason);
    }

    /// <summary>Threads A/B/C: a hit in B does not change A's TTL, and each thread keeps its own numbers.</summary>
    [Fact]
    public void Apply_HitInB_DoesNotChangeA()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.Apply(Create("C", T0.AddMinutes(1), 30000, 29000));
        _engine.OnUserActivity("A", T0.AddMinutes(2));
        _engine.Apply(Create("B", T0.AddMinutes(20), 64244, 64000));
        _clock.UtcNow = T0.AddMinutes(21);

        CacheSnapshot snapshot = _engine.Tick();

        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal(TimeSpan.FromMinutes(9), snapshot.Ttl.Remaining);
        Assert.Equal(40755, snapshot.Input);
        Assert.Equal(CacheState.Warm, snapshot.State);
    }

    /// <summary>Two forks that share a root session keep separate states.</summary>
    [Fact]
    public void Apply_ForksWithSameRoot_AreNotMerged()
    {
        _engine.Apply(Create("F1", T0, 40000, 39000, kind: ThreadKind.Fork, rootSessionId: "R", parentThreadId: "R"));
        CacheSnapshot latest = _engine.Apply(Create("F2", T0.AddMinutes(1), 50000, 0, kind: ThreadKind.Fork,
                                                    rootSessionId: "R", parentThreadId: "R"));
        _engine.OnUserActivity("F1", T0.AddMinutes(2));
        CacheSnapshot first = _engine.Tick();

        Assert.Equal("F2", latest.ThreadId);
        Assert.Equal(50000, latest.Input);
        Assert.Equal(ThreadKind.Fork, latest.Kind);
        Assert.Equal("F1", first.ThreadId);
        Assert.Equal(40000, first.Input);
    }

    /// <summary>A subagent's usage never replaces the main thread on display, even when it is newer.</summary>
    [Fact]
    public void Apply_SubagentUsage_IsNotShown()
    {
        _engine.Apply(Create("M", T0, 40755, 40448));
        CacheSnapshot snapshot = _engine.Apply(Create("S", T0.AddMinutes(1), 36842, 0, kind: ThreadKind.Subagent,
                                                      rootSessionId: "M", parentThreadId: "M"));

        Assert.Equal("M", snapshot.ThreadId);
        Assert.Equal(40755, snapshot.Input);
        Assert.Equal(ThreadKind.Main, snapshot.Kind);
    }

    /// <summary>A user-activity hook confirms the thread; JSONL activity of another thread does not take over.</summary>
    [Fact]
    public void Resolve_UserActivityHook_IsConfirmedAndKeptAgainstBackgroundActivity()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.OnUserActivity("A", T0.AddSeconds(30));

        CacheSnapshot snapshot = _engine.Apply(Create("B", T0.AddMinutes(3), 64244, 64000));

        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal(new ThreadSelection(SelectionConfidence.Confirmed, SelectionReasons.UserActivityHook), snapshot.Selection);
    }

    /// <summary>Without hooks the latest JSONL activity is shown as inferred, and a passive switch keeps it inferred.</summary>
    [Fact]
    public void Resolve_WithoutHooks_LatestJsonlActivityIsInferred()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.Apply(Create("B", T0.AddMinutes(1), 64244, 64000));
        _clock.UtcNow = T0.AddMinutes(10);

        CacheSnapshot snapshot = _engine.Tick();

        Assert.Equal("B", snapshot.ThreadId);
        Assert.Equal(new ThreadSelection(SelectionConfidence.Inferred, SelectionReasons.LatestJsonlActivity), snapshot.Selection);
    }

    /// <summary>A newer SessionStart (resume of another thread) wins over an older user-activity hook.</summary>
    [Fact]
    public void Resolve_NewerSessionStart_WinsOverOlderUserActivity()
    {
        _engine.OnUserActivity("A", T0);

        CacheSnapshot snapshot = _engine.OnSessionStart("B", T0.AddMinutes(1));

        Assert.Equal("B", snapshot.ThreadId);
        Assert.Equal(CacheState.WaitingForUsage, snapshot.State);
        Assert.Equal(SelectionReasons.SessionStartHook, snapshot.Selection.Reason);
    }

    /// <summary>Two or more Codex windows cap a hook-confirmed choice at inferred (§3.3).</summary>
    [Fact]
    public void Resolve_TwoCodexWindows_CapsConfidenceAtInferred()
    {
        _engine.OnUserActivity("A", T0);

        CacheSnapshot two = _engine.OnCodexWindows(2);
        CacheSnapshot one = _engine.OnCodexWindows(1);

        Assert.Equal(SelectionConfidence.Inferred, two.Selection.Confidence);
        Assert.Equal(SelectionReasons.UserActivityHook, two.Selection.Reason);
        Assert.Equal(SelectionConfidence.Confirmed, one.Selection.Confidence);
    }

    /// <summary>SessionEnd removes exactly one thread, is idempotent, and keeps forks with the same root.</summary>
    [Fact]
    public void OnSessionEnd_RemovesOnlyThatThread()
    {
        _engine.Apply(Create("R", T0, 40755, 40448));
        _engine.Apply(Create("F", T0.AddMinutes(1), 50000, 49000, kind: ThreadKind.Fork, rootSessionId: "R", parentThreadId: "R"));
        _engine.OnUserActivity("R", T0.AddMinutes(2));

        _engine.OnSessionEnd("R", T0.AddMinutes(3));
        CacheSnapshot snapshot = _engine.OnSessionEnd("R", T0.AddMinutes(4));

        Assert.Equal("F", snapshot.ThreadId);
        Assert.Equal(SelectionConfidence.Inferred, snapshot.Selection.Confidence);
    }

    /// <summary>An end older than the thread's latest activity is stale and does not remove it.</summary>
    [Fact]
    public void OnSessionEnd_StaleEnd_IsIgnored()
    {
        _engine.Apply(Create("A", T0.AddMinutes(5), 40755, 40448));

        CacheSnapshot snapshot = _engine.OnSessionEnd("A", T0.AddMinutes(1));

        Assert.Equal("A", snapshot.ThreadId);
    }

    /// <summary>After an end, old JSONL cannot bring the thread back; a new start or newer activity can.</summary>
    [Fact]
    public void OnSessionEnd_Tombstone_HoldsOffOldJsonlOnly()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.OnSessionEnd("A", T0.AddMinutes(1));

        CacheSnapshot replayed = _engine.Apply(Create("A", T0, 40755, 40448));
        CacheSnapshot observed = _engine.Observe(new ThreadObservation("A", ThreadKind.Main, null, "A", "p", null, T0));
        CacheSnapshot newer = _engine.Apply(Create("A", T0.AddMinutes(2), 41000, 40448));

        Assert.Equal(CacheState.NoSession, replayed.State);
        Assert.Equal(CacheState.NoSession, observed.State);
        Assert.Equal("A", newer.ThreadId);
        Assert.Equal(41000, newer.Input);
    }

    /// <summary>A resume after an end clears the tombstone and starts a fresh generation.</summary>
    [Fact]
    public void OnSessionStart_AfterEnd_RevivesTheThread()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.OnSessionEnd("A", T0.AddMinutes(1));

        CacheSnapshot snapshot = _engine.OnSessionStart("A", T0.AddMinutes(2));

        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal(CacheState.WaitingForUsage, snapshot.State);
        Assert.Null(snapshot.Input);
    }

    /// <summary>A hook older than the end (delivered late) neither revives the thread nor confirms it.</summary>
    [Fact]
    public void OnUserActivity_OlderThanTheEnd_DoesNotReviveTheThread()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.Apply(Create("B", T0.AddMinutes(1), 30000, 29000));
        _engine.OnSessionEnd("A", T0.AddMinutes(5));

        CacheSnapshot activity = _engine.OnUserActivity("A", T0.AddMinutes(4));
        CacheSnapshot start = _engine.OnSessionStart("A", T0.AddMinutes(3));

        Assert.Equal("B", activity.ThreadId);
        Assert.Equal("B", start.ThreadId);
        Assert.Equal(SelectionConfidence.Inferred, start.Selection.Confidence);
    }

    /// <summary>A thread revived by newer JSONL after its end is inferred: the hook from before the end is forgotten.</summary>
    [Fact]
    public void OnSessionEnd_ThenNewerJsonl_RevivedThreadIsInferred()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.OnUserActivity("A", T0.AddMinutes(1));
        _engine.OnSessionEnd("A", T0.AddMinutes(2));

        CacheSnapshot snapshot = _engine.Apply(Create("A", T0.AddMinutes(3), 41000, 40448));

        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal(new ThreadSelection(SelectionConfidence.Inferred, SelectionReasons.LatestJsonlActivity), snapshot.Selection);
    }

    /// <summary>An older event read late (an old segment after the new one) leaves the thread's source and model alone.</summary>
    [Fact]
    public void Apply_OlderEventFromAnotherFile_KeepsSourceAndModel()
    {
        TokenUsageSnapshot current = Create("A", T0.AddMinutes(5), 64244, 64000) with { SourcePath = "new.jsonl", Model = "gpt-6.1-sol" };
        TokenUsageSnapshot older = Create("A", T0, 40755, 40448) with { SourcePath = "old.jsonl", Model = "gpt-5.4-mini" };
        _engine.Apply(current);

        CacheSnapshot snapshot = _engine.Apply(older);

        Assert.Equal("new.jsonl", snapshot.Source.Path);
        Assert.Equal("gpt-6.1-sol", snapshot.Model);
        Assert.Equal(64244, snapshot.Input);
    }

    /// <summary>A failed source turns the snapshot into Error with a diagnostic code and no free text.</summary>
    [Fact]
    public void OnSourceStatus_Failed_IsErrorWithCode()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));

        CacheSnapshot snapshot = _engine.OnSourceStatus(SourceStatus.Failed, DiagnosticCodes.SourceFailed);

        Assert.Equal(CacheState.Error, snapshot.State);
        Assert.Equal(DiagnosticCodes.SourceFailed, snapshot.Diagnostics);
        Assert.Equal(SourceStatus.Failed, snapshot.Source.Status);
    }

    /// <summary>A SessionEnd naming a subagent is ambiguous (subagents get none of their own) and changes nothing.</summary>
    [Fact]
    public void OnSessionEnd_Subagent_IsIgnored()
    {
        _engine.Apply(Create("M", T0, 40755, 40448));
        _engine.Apply(Create("S", T0.AddMinutes(1), 9000, 8000, kind: ThreadKind.Subagent, rootSessionId: "M", parentThreadId: "M"));

        _engine.OnSessionEnd("S", T0.AddMinutes(2));
        _engine.Apply(Create("S", T0.AddMinutes(1).AddSeconds(30), 9100, 8000, kind: ThreadKind.Subagent, rootSessionId: "M", parentThreadId: "M"));

        Assert.False(_engine.IsEnded("S"));
        Assert.Equal("M", _engine.Tick().ThreadId);
    }

    /// <summary>An unknown thread's end and a repeated end are no-ops for every live thread.</summary>
    [Fact]
    public void OnSessionEnd_UnknownOrRepeated_KeepsOtherThreads()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _engine.OnUserActivity("A", T0.AddMinutes(1));

        _engine.OnSessionEnd("unknown", T0.AddMinutes(2));
        _engine.OnSessionEnd("unknown", T0.AddMinutes(2));
        CacheSnapshot snapshot = _engine.Tick();

        Assert.Equal("A", snapshot.ThreadId);
        Assert.Equal(SelectionConfidence.Confirmed, snapshot.Selection.Confidence);
    }

    /// <summary>The shown thread with no activity for <see cref="PulseEngine.StaleAfter"/> is Stale, not merely expired.</summary>
    [Fact]
    public void Tick_IdlePastStaleAfter_IsStale()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        _clock.UtcNow = T0 + PulseEngine.StaleAfter - TimeSpan.FromMinutes(1);
        Assert.Equal(CacheState.EstimatedExpired, _engine.Tick().State);

        _clock.UtcNow = T0 + PulseEngine.StaleAfter;
        CacheSnapshot snapshot = _engine.Tick();

        Assert.Equal(CacheState.Stale, snapshot.State);
        Assert.Equal(T0, snapshot.LastRequestAt);
    }

    /// <summary>
    /// Pruning drops threads idle past <see cref="PulseEngine.PruneAfter"/> but keeps the newest and the shown one; a pruned
    /// thread's old JSONL cannot bring it back, newer activity can.
    /// </summary>
    [Fact]
    public void Prune_DropsIdleThreads_AndTombstonesThem()
    {
        _engine.Apply(Create("Old", T0, 40755, 40448));
        _engine.Apply(Create("Shown", T0.AddMinutes(1), 30000, 29000));
        _engine.OnUserActivity("Shown", T0.AddMinutes(2));
        _engine.Apply(Create("Newest", T0.AddMinutes(3), 20000, 19000));
        _clock.UtcNow = T0 + PulseEngine.PruneAfter + TimeSpan.FromHours(1);

        int dropped = _engine.Prune();
        CacheSnapshot replayed = _engine.Apply(Create("Old", T0, 40755, 40448));

        Assert.Equal(1, dropped);
        Assert.Equal("Shown", replayed.ThreadId);
        Assert.True(_engine.IsEnded("Old"));

        _engine.Apply(Create("Old", _clock.UtcNow, 41000, 40448));
        _engine.OnSessionEnd("Shown", _clock.UtcNow);
        CacheSnapshot revived = _engine.Tick();

        Assert.False(_engine.IsEnded("Old"));
        Assert.Equal("Old", revived.ThreadId);
        Assert.Equal(41000, revived.Input);
    }

    /// <summary>Beyond <see cref="PulseEngine.MaxLiveThreads"/> the least recently active threads go first.</summary>
    [Fact]
    public void Prune_BoundsTheThreadCount()
    {
        for (int index = 0; index < PulseEngine.MaxLiveThreads + 5; index++)
        {
            _engine.Apply(Create("T" + index, T0.AddSeconds(index), 40755, 40448));
        }

        int dropped = _engine.Prune();

        Assert.Equal(5, dropped);
        Assert.True(_engine.IsEnded("T0"));
        Assert.False(_engine.IsEnded("T5"));
    }

    /// <summary>Every call publishes, and a new subscriber first gets the current snapshot.</summary>
    [Fact]
    public void Snapshots_SubscriberGetsCurrentThenUpdates()
    {
        _engine.Apply(Create("A", T0, 40755, 40448));
        List<CacheSnapshot> received = [];
        using IDisposable subscription = _engine.Snapshots.Subscribe(new Collector(received));

        _engine.Apply(Create("A", T0.AddMinutes(1), 41000, 40448));

        Assert.Equal([40755L, 41000L], received.Select(s => s.Input!.Value));
        Assert.Same(received[^1], _engine.Snapshots.Current);
    }

    private sealed class Collector(List<CacheSnapshot> received) : IObserver<CacheSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(CacheSnapshot value)
        {
            received.Add(value);
        }
    }
}
