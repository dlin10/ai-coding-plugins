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

    /// <summary>The warm line: the state, the bar, ~TTL and speed as a dash; no tokens, selection or source.</summary>
    [Fact]
    public void Warm_ShowsTheReferenceLine()
    {
        OverlayView view = Format(StubSnapshotScript.At(2, Now));

        Assert.Equal(("cache", OverlayTone.Warm, true), (view.StateText, view.Tone, view.ShowMetrics));
        Assert.Equal("~24 min", view.TtlText);
        Assert.Equal("Avg speed —", view.SpeedText);
        Assert.Equal(0.8, view.BarFraction);
        Assert.True(view.BarKnown);
        Assert.False(view.ShowChooseFolder);
        Assert.Equal("Timing data unavailable", view.SpeedTooltip);
    }

    /// <summary>The state tooltip has the model, the source and the confirmed selection; the TTL tooltip says it estimates.</summary>
    [Fact]
    public void Tooltips_CarryModelSourceSelectionAndEstimate()
    {
        OverlayView view = Format(StubSnapshotScript.At(2, Now));

        Assert.Contains("Model: gpt-5.5-codex", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("Source: JSONL", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("Thread: Auto · confirmed", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("Estimate", view.TtlTooltip, StringComparison.Ordinal);
    }

    /// <summary>The remaining TTL counts down from the snapshot's time, rounded up to whole minutes, and never below zero.</summary>
    [Fact]
    public void Ttl_CountsDownBetweenSnapshots()
    {
        CacheSnapshot snapshot = StubSnapshotScript.At(2, Now);

        Assert.Equal("~20 min", OverlayFormatter.Format(snapshot, 1, 0, Now.AddMinutes(4).AddSeconds(30), Utc).TtlText);
        Assert.Equal("~0 min", OverlayFormatter.Format(snapshot, 1, 0, Now.AddHours(1), Utc).TtlText);
    }

    /// <summary>Without an estimate the TTL is "TTL unknown" and the bar is indefinite.</summary>
    [Fact]
    public void UnknownTtl_IsSaid()
    {
        OverlayView view = Format(StubSnapshotScript.At(3, Now));

        Assert.Equal("TTL unknown", view.TtlText);
        Assert.False(view.BarKnown);
        Assert.Contains("Thread: Auto · inferred", view.StateTooltip, StringComparison.Ordinal);
    }

    /// <summary>Each state has its §3.2 wording and colour.</summary>
    /// <param name="step">The stub step that produces the state.</param>
    /// <param name="text">The expected state text.</param>
    /// <param name="tone">The expected tone name.</param>
    /// <param name="metrics">Whether the metrics are shown.</param>
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
        OverlayView view = OverlayFormatter.Format(StubSnapshotScript.At(2, Now), 2, 0, Now, Utc);

        Assert.Contains("Thread: Auto · inferred", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("2 Codex windows", view.StateTooltip, StringComparison.Ordinal);
        Assert.Equal(SelectionConfidence.Unknown, OverlayFormatter.EffectiveConfidence(SelectionConfidence.Unknown, 3));
    }

    /// <summary>The state tooltip counts hooks; with none while JSONL is live it says to open Review hooks (spec §4.4 item 7).</summary>
    [Fact]
    public void StateTooltip_CountsHooksOrHintsAtReview()
    {
        CacheSnapshot snapshot = StubSnapshotScript.At(2, Now);

        string none = OverlayFormatter.Format(snapshot with { Source = snapshot.Source with { Status = SourceStatus.Available } }, 1, 0, Now, Utc).StateTooltip;
        string some = OverlayFormatter.Format(snapshot, 1, 3, Now, Utc).StateTooltip;

        Assert.EndsWith("Hooks not observed: after a plugin update, open Review hooks in Codex", none, StringComparison.Ordinal);
        Assert.EndsWith("Hook events since start: 3", some, StringComparison.Ordinal);
        Assert.Equal("Hook events since start: none", OverlayFormatter.HookLine(0, SourceStatus.NotFound));
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

    /// <summary>
    /// A page Codex's log names with nothing to show (New chat, a remote host's thread, a thread not loaded) has no
    /// content, so the overlay hides; a plain "no session" keeps its line and the Choose folder button.
    /// </summary>
    /// <param name="reason">The route reason.</param>
    [Theory]
    [InlineData(SelectionReasons.DesktopRouteNewChat)]
    [InlineData(SelectionReasons.DesktopRouteNoLocalData)]
    [InlineData(SelectionReasons.DesktopRouteNotLoaded)]
    public void RoutedEmptyPage_HasNoContent(string reason)
    {
        CacheSnapshot snapshot = StubSnapshotScript.At(0, Now) with { Selection = new ThreadSelection(SelectionConfidence.Confirmed, reason) };

        OverlayView view = Format(snapshot);
        OverlayView noSession = Format(StubSnapshotScript.At(0, Now));

        Assert.False(view.HasContent);
        Assert.False(view.ShowChooseFolder);
        Assert.True(noSession.HasContent);
        Assert.True(noSession.ShowChooseFolder);
    }

    /// <summary>A timed speed shows in whole tokens per second, and its tooltip says the first-token wait is included.</summary>
    [Fact]
    public void Speed_ShowsTokensPerSecond()
    {
        OverlayView view = Format(StubSnapshotScript.At(2, Now) with { Speed = 31.6 });

        Assert.Equal("Avg speed 32 tok/s", view.SpeedText);
        Assert.Contains("wait for the first token is included", view.SpeedTooltip, StringComparison.Ordinal);
    }

    /// <summary>A thread Codex's log names is credited to the log in the state tooltip, not to a hook.</summary>
    [Fact]
    public void RoutedThread_TooltipCreditsTheLog()
    {
        CacheSnapshot snapshot = StubSnapshotScript.At(2, Now) with
        {
            Selection = new ThreadSelection(SelectionConfidence.Confirmed, SelectionReasons.DesktopRoute),
        };

        OverlayView view = Format(snapshot);

        Assert.True(view.HasContent);
        Assert.Contains("Thread: Auto · confirmed", view.StateTooltip, StringComparison.Ordinal);
        Assert.Contains("Codex Desktop's log names the page on screen.", view.StateTooltip, StringComparison.Ordinal);
    }

    private static OverlayView Format(CacheSnapshot snapshot) => OverlayFormatter.Format(snapshot, 1, 0, Now, Utc);
}
