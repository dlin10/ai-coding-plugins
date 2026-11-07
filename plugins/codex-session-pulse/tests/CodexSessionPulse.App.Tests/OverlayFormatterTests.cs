using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.App.Stub;
using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>The overlay line per spec §3.1 and §3.2, formatted from stub snapshots.</summary>
public sealed class OverlayFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>The warm line: label, ~TTL, cached/input with one decimal, speed as a dash, the selection, the source.</summary>
    [Fact]
    public void Warm_ShowsTheReferenceLine()
    {
        OverlayView view = Format(StubSnapshotScript.At(2, Now));

        Assert.Equal(("cache", OverlayTone.Warm, true), (view.StateText, view.Tone, view.ShowMetrics));
        Assert.Equal("~24 min", view.TtlText);
        Assert.Equal("245.1k/245.3k", view.TokensText);
        Assert.Equal("Avg speed —", view.SpeedText);
        Assert.Equal("Auto · confirmed", view.SelectionText);
        Assert.Equal("JSONL", view.SourceText);
        Assert.Equal(0.8, view.BarFraction);
        Assert.True(view.BarKnown);
        Assert.False(view.ShowChooseFolder);
        Assert.Equal("Timing data unavailable", view.SpeedTooltip);
    }

    /// <summary>The tokens tooltip has the two-decimal percentage of §11.1 and the cache write; the state tooltip has the model.</summary>
    [Fact]
    public void Tooltips_CarryPercentageCacheWriteAndModel()
    {
        OverlayView view = Format(StubSnapshotScript.At(2, Now));

        Assert.Contains("(99.91%)", view.TokensTooltip, StringComparison.Ordinal);
        Assert.Contains("Cache write: 1,024 tokens", view.TokensTooltip, StringComparison.Ordinal);
        Assert.Contains("Model: gpt-5.5-codex", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("Estimate", view.TtlTooltip, StringComparison.Ordinal);
    }

    /// <summary>The remaining TTL counts down from the snapshot's time, rounded up to whole minutes, and never below zero.</summary>
    [Fact]
    public void Ttl_CountsDownBetweenSnapshots()
    {
        CacheSnapshot snapshot = StubSnapshotScript.At(2, Now);

        Assert.Equal("~20 min", OverlayFormatter.Format(snapshot, 1, Now.AddMinutes(4).AddSeconds(30), Utc).TtlText);
        Assert.Equal("~0 min", OverlayFormatter.Format(snapshot, 1, Now.AddHours(1), Utc).TtlText);
    }

    /// <summary>Without an estimate the TTL is "TTL unknown" and the bar is indefinite.</summary>
    [Fact]
    public void UnknownTtl_IsSaid()
    {
        OverlayView view = Format(StubSnapshotScript.At(3, Now));

        Assert.Equal("TTL unknown", view.TtlText);
        Assert.False(view.BarKnown);
        Assert.Equal("Auto · inferred", view.SelectionText);
        Assert.Contains("Cache write: unknown", view.TokensTooltip, StringComparison.Ordinal);
    }

    /// <summary>Each state has its §3.2 wording and colour.</summary>
    [Theory]
    [InlineData(0, "Codex session not found", "Neutral", false)]
    [InlineData(1, "Waiting for token usage…", "Neutral", false)]
    [InlineData(4, "cache", "Expiring", true)]
    [InlineData(5, "Cache may have expired", "Neutral", true)]
    [InlineData(6, "Observed cache miss 10:00", "Miss", true)]
    [InlineData(7, "Last update 07:00", "Neutral", true)]
    [InlineData(8, "⚠ Error", "Error", true)]
    public void States_HaveTheirWording(int step, string text, string tone, bool metrics)
    {
        OverlayView view = Format(StubSnapshotScript.At(step, Now));

        Assert.Equal((text, tone, metrics), (view.StateText, view.Tone.ToString(), view.ShowMetrics));
    }

    /// <summary>Only "no session" offers the folder chooser; an expired estimate shows no countdown and an empty bar.</summary>
    [Fact]
    public void NoSession_OffersChooseFolder_AndExpiredShowsNoCountdown()
    {
        Assert.True(Format(StubSnapshotScript.At(0, Now)).ShowChooseFolder);

        OverlayView expired = Format(StubSnapshotScript.At(5, Now));
        Assert.Equal(string.Empty, expired.TtlText);
        Assert.Equal(0, expired.BarFraction);
        Assert.DoesNotContain("miss", expired.StateText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With two or more Codex windows a confirmed selection is shown as inferred, and the tooltip says why.</summary>
    [Fact]
    public void TwoWindows_CapConfidenceAtInferred()
    {
        OverlayView view = OverlayFormatter.Format(StubSnapshotScript.At(2, Now), 2, Now, Utc);

        Assert.Equal("Auto · inferred", view.SelectionText);
        Assert.Contains("2 Codex windows", view.SelectionTooltip, StringComparison.Ordinal);
        Assert.Equal(SelectionConfidence.Unknown, OverlayFormatter.EffectiveConfidence(SelectionConfidence.Unknown, 3));
    }

    /// <summary>Token counts: plain below 1000, one decimal with k or M above, a dash when unknown.</summary>
    [Theory]
    [InlineData(null, "—")]
    [InlineData(512L, "512")]
    [InlineData(245_120L, "245.1k")]
    [InlineData(1_260_000L, "1.3M")]
    public void FormatTokens_UsesSuffixes(long? tokens, string expected)
    {
        Assert.Equal(expected, OverlayFormatter.FormatTokens(tokens));
    }

    /// <summary>Long estimates read in hours and minutes.</summary>
    [Fact]
    public void FormatRemaining_UsesHoursPastAnHour()
    {
        Assert.Equal("~2 h 5 min", OverlayFormatter.FormatRemaining(TimeSpan.FromMinutes(124.5)));
    }

    /// <summary>The stub script covers every cache state, so a stub run exercises the whole line.</summary>
    [Fact]
    public void StubScript_CoversEveryState()
    {
        HashSet<CacheState> states = Enumerable.Range(0, StubSnapshotScript.Count).Select(step => StubSnapshotScript.At(step, Now).State).ToHashSet();

        Assert.Equal(Enum.GetValues<CacheState>().ToHashSet(), states);
    }

    private static OverlayView Format(CacheSnapshot snapshot) => OverlayFormatter.Format(snapshot, 1, Now, Utc);
}
