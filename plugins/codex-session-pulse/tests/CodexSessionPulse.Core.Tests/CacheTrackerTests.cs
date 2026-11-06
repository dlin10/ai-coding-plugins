using Xunit;
using static CodexSessionPulse.Core.Tests.TestUsage;

namespace CodexSessionPulse.Core.Tests;

/// <summary>The per-thread cache state machine (spec §6, §11.1 items 2–5, 9–10).</summary>
public sealed class CacheTrackerTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>The §1.2 sample: the last request's ratio is 245120 / 245349, 99.91% at two decimals.</summary>
    [Fact]
    public void Apply_SpecSample_RatioOfLastRequestIs9991Percent()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        long[][] rows = [[239071, 235392], [240392, 239104], [242386, 241152], [244157, 242432], [245349, 245120]];
        CacheSnapshot snapshot = null!;

        for (int i = 0; i < rows.Length; i++)
        {
            snapshot = tracker.Apply(Create("A", T0.AddMinutes(i), rows[i][0], rows[i][1]));
        }

        Assert.Equal(245349, snapshot.Input);
        Assert.Equal(245120, snapshot.CachedInput);
        Assert.Equal(0, snapshot.CacheWrite);
        Assert.Equal(99.91, Math.Round(snapshot.CacheRatio!.Value * 100, 2));
        Assert.Equal(CacheState.Warm, snapshot.State);
        Assert.Null(snapshot.Speed);
    }

    /// <summary>The TTL boundaries: warm, expiring soon at exactly 25% left, estimated expired at the deadline.</summary>
    [Theory]
    [InlineData(0, CacheState.Warm, 30 * 60)]
    [InlineData(22 * 60 + 29, CacheState.Warm, 7 * 60 + 31)]
    [InlineData(22 * 60 + 30, CacheState.ExpiringSoon, 7 * 60 + 30)]
    [InlineData(29 * 60 + 59, CacheState.ExpiringSoon, 1)]
    [InlineData(30 * 60, CacheState.EstimatedExpired, 0)]
    [InlineData(5 * 3600, CacheState.EstimatedExpired, 0)]
    public void Tick_AfterHit_FollowsTheEstimate(int elapsedSeconds, CacheState expected, int remainingSeconds)
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 40755, 40448));

        CacheSnapshot snapshot = tracker.Tick(T0.AddSeconds(elapsedSeconds));

        Assert.Equal(expected, snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(remainingSeconds), snapshot.Ttl.Remaining);
        Assert.Equal(Ttl, snapshot.Ttl.Estimate);
        Assert.Equal(TtlBasis.Assumed, snapshot.Ttl.Basis);
        Assert.Equal(T0.AddSeconds(elapsedSeconds), snapshot.AtUtc);
    }

    /// <summary>A tick never moves the anchor: repeated ticks leave the deadline where the hit put it.</summary>
    [Fact]
    public void Tick_Repeated_DoesNotExtendTheAnchor()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 40755, 40448));

        tracker.Tick(T0.AddMinutes(10));
        tracker.Tick(T0.AddMinutes(15));
        CacheSnapshot snapshot = tracker.Tick(T0.AddMinutes(20));

        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Ttl.Remaining);
    }

    /// <summary>A clock moved back never yields more than the full estimate, and moving it forward again is fine.</summary>
    [Fact]
    public void Tick_ClockMovedBackAndForward_StaysWithinTheEstimate()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 40755, 40448));

        CacheSnapshot back = tracker.Tick(T0.AddMinutes(-90));
        CacheSnapshot forward = tracker.Tick(T0.AddMinutes(12));

        Assert.Equal(Ttl, back.Ttl.Remaining);
        Assert.Equal(CacheState.Warm, back.State);
        Assert.Equal(TimeSpan.FromMinutes(18), forward.Ttl.Remaining);
    }

    /// <summary>The configured TTL is used instead of 30 minutes.</summary>
    [Fact]
    public void Tick_ConfiguredTtl_IsTheEstimate()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default with { EstimatedTtl = TimeSpan.FromMinutes(10) });
        tracker.Apply(Create("A", T0, 40755, 40448));

        CacheSnapshot snapshot = tracker.Tick(T0.AddMinutes(8));

        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Ttl.Estimate);
        Assert.Equal(CacheState.ExpiringSoon, snapshot.State);
    }

    /// <summary>A replayed or older event never extends the timer (§6.1).</summary>
    [Fact]
    public void Apply_ReplayOrOlderEvent_DoesNotMoveTheAnchor()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        TokenUsageSnapshot first = Create("A", T0, 40728, 20736);
        TokenUsageSnapshot second = Create("A", T0.AddMinutes(5), 40755, 40448);
        tracker.Apply(first);
        tracker.Apply(second);

        tracker.Apply(second);
        tracker.Apply(first);
        CacheSnapshot snapshot = tracker.Tick(T0.AddMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(25), snapshot.Ttl.Remaining);
        Assert.Equal(40755, snapshot.Input);
        Assert.Equal(T0.AddMinutes(5), snapshot.LastRequestAt);
    }

    /// <summary>A read below the minimum confirms nothing and is not a miss: waiting, with the numbers kept.</summary>
    [Fact]
    public void Apply_ReadBelowMinimum_IsNeitherWarmNorMiss()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);

        CacheSnapshot snapshot = tracker.Apply(Create("A", T0, 51361, 0));

        Assert.Equal(CacheState.WaitingForUsage, snapshot.State);
        Assert.Equal(51361, snapshot.Input);
        Assert.Equal(0, snapshot.CachedInput);
        Assert.Equal(0.0, snapshot.CacheRatio);
        Assert.Equal(TtlBasis.Unknown, snapshot.Ttl.Basis);
        Assert.Null(snapshot.Ttl.Remaining);
    }

    /// <summary>The §6.3 heuristic fires on a known, unchanged model with a large replacement write.</summary>
    [Fact]
    public void Apply_LowReadWithReplacementWrite_IsObservedMiss()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 50000, 40000, write: 0));

        CacheSnapshot snapshot = tracker.Apply(Create("A", T0.AddMinutes(40), 50000, 1000, write: 45000));

        Assert.Equal(CacheState.ObservedMiss, snapshot.State);
        Assert.Null(snapshot.Ttl.Remaining);
    }

    /// <summary>The heuristic never fires on a zero or unknown write, an unknown model, or a model change (§11.1 item 10).</summary>
    [Theory]
    [InlineData(0L, "gpt-6.1-sol")]
    [InlineData(null, "gpt-6.1-sol")]
    [InlineData(45000L, null)]
    [InlineData(45000L, "gpt-5.4-mini")]
    public void Apply_LowReadWithoutComparablePrefix_IsNotObservedMiss(long? write, string? model)
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 50000, 40000, write: 0));

        CacheSnapshot snapshot = tracker.Apply(Create("A", T0.AddMinutes(40), 50000, 1000, write: write, model: model));

        Assert.NotEqual(CacheState.ObservedMiss, snapshot.State);
    }

    /// <summary>An estimated expiry is never reported as an observed miss.</summary>
    [Fact]
    public void Tick_PastTheEstimate_IsEstimatedExpiredNotMiss()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 40755, 40448));

        Assert.Equal(CacheState.EstimatedExpired, tracker.Tick(T0.AddHours(2)).State);
    }

    /// <summary>An invalid event (more cached than input) is ignored; the state is kept.</summary>
    [Fact]
    public void Apply_InvalidUsage_KeepsTheState()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);
        tracker.Apply(Create("A", T0, 40755, 40448));

        CacheSnapshot snapshot = tracker.Apply(Create("A", T0.AddMinutes(1), 100, 200));

        Assert.Equal(40755, snapshot.Input);
        Assert.Equal(T0, snapshot.LastRequestAt);
    }

    /// <summary>An unknown cache write stays null, never 0.</summary>
    [Fact]
    public void Apply_UnknownWrite_StaysNull()
    {
        CacheTracker tracker = new(CacheTrackerOptions.Default);

        Assert.Null(tracker.Apply(Create("A", T0, 40755, 40448, write: null)).CacheWrite);
    }

    /// <summary>Without any usage the tracker waits.</summary>
    [Fact]
    public void Tick_NoUsage_IsWaitingForUsage()
    {
        Assert.Equal(CacheState.WaitingForUsage, new CacheTracker(CacheTrackerOptions.Default).Tick(T0).State);
    }
}
