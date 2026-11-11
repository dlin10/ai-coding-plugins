using Xunit;
using static CodexSessionPulse.Core.Tests.TestUsage;

namespace CodexSessionPulse.Core.Tests;

/// <summary>The output speed of one thread (spec §6.4, §11.1): Σ output / Σ time over the last requests.</summary>
public sealed class SpeedTrackerTests
{
    /// <summary>A long request weighs more than a short one: the speed is the sum over the sum, not a mean of speeds.</summary>
    [Fact]
    public void Apply_SpeedIsSumOverSum()
    {
        SpeedTracker tracker = new();

        tracker.Apply(new RequestTiming("A", T0.AddSeconds(1), 100, TimeSpan.FromSeconds(1)));
        tracker.Apply(new RequestTiming("A", T0.AddSeconds(40), 300, TimeSpan.FromSeconds(30)));

        // Mean of speeds would be (100 + 10) / 2 = 55.
        Assert.Equal(400 / 31.0, tracker.TokensPerSecond);
    }

    /// <summary>Only the last Window requests count.</summary>
    [Fact]
    public void Apply_KeepsTheLastWindowRequests()
    {
        SpeedTracker tracker = new();
        tracker.Apply(new RequestTiming("A", T0, 10_000, TimeSpan.FromSeconds(1)));

        for (int i = 1; i <= SpeedTracker.Window; i++)
        {
            tracker.Apply(new RequestTiming("A", T0.AddMinutes(i), 20, TimeSpan.FromSeconds(1)));
        }

        Assert.Equal(20.0, tracker.TokensPerSecond);
    }

    /// <summary>No output, no time, or an answer older than the newest counted one is ignored; nothing timed is null.</summary>
    [Fact]
    public void Apply_IgnoresEmptyAndOlderRequests()
    {
        SpeedTracker tracker = new();
        double? before = tracker.TokensPerSecond;

        tracker.Apply(new RequestTiming("A", T0.AddSeconds(10), 50, TimeSpan.FromSeconds(5)));
        tracker.Apply(new RequestTiming("A", T0.AddSeconds(20), 0, TimeSpan.FromSeconds(5)));
        tracker.Apply(new RequestTiming("A", T0.AddSeconds(30), 50, TimeSpan.Zero));
        tracker.Apply(new RequestTiming("A", T0.AddSeconds(5), 5000, TimeSpan.FromSeconds(1)));

        Assert.Null(before);
        Assert.Equal(10.0, tracker.TokensPerSecond);
    }
}
