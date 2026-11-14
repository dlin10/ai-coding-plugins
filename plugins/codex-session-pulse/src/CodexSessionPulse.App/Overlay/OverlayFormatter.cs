using System.Globalization;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Overlay;

/// <summary>The colour role of the state text and the bar.</summary>
internal enum OverlayTone
{
    /// <summary>Grey: nothing confirmed, or an estimate that has run out.</summary>
    Neutral,

    /// <summary>A confirmed cache hit within the estimate.</summary>
    Warm,

    /// <summary>At most 25% of the estimate left.</summary>
    Expiring,

    /// <summary>An observed cache miss.</summary>
    Miss,

    /// <summary>The watcher or the parser failed.</summary>
    Error,
}

/// <summary>
/// Everything the overlay line shows, already formatted (spec §3.1, §3.2): the state, the bar, the time left and the
/// speed. Core never formats; this is the only place that does.
/// </summary>
/// <param name="StateText">The state label, e.g. <c>cache</c> or <c>Observed cache miss 14:32</c>.</param>
/// <param name="Tone">The colour role.</param>
/// <param name="ShowMetrics">Whether the bar, the time left and the speed are shown.</param>
/// <param name="BarFraction">The filled share of the bar, from 0 to 1.</param>
/// <param name="BarKnown">Whether the bar reflects an estimate; an unknown TTL gives a grey indefinite bar.</param>
/// <param name="TtlText">The approximate remaining time, <c>~24 min</c>, or <c>TTL unknown</c>.</param>
/// <param name="SpeedText">The output speed, <c>Avg speed 31 tok/s</c>, or <c>Avg speed —</c> before a request is timed.</param>
/// <param name="ShowChooseFolder">Whether the Choose folder button is shown (no session found).</param>
/// <param name="StateTooltip">The state tooltip: times, model, source, the selection and the hooks received.</param>
/// <param name="TtlTooltip">The TTL tooltip; always says it is an estimate.</param>
/// <param name="SpeedTooltip">The speed tooltip.</param>
/// <param name="HasContent">
/// Whether the line has anything to show; <see langword="false"/> on a page Codex's log names with no cache to show
/// (New chat, a thread without a local rollout, a thread not loaded), where the overlay hides.
/// </param>
internal sealed record OverlayView(string StateText,
                                   OverlayTone Tone,
                                   bool ShowMetrics,
                                   double BarFraction,
                                   bool BarKnown,
                                   string TtlText,
                                   string SpeedText,
                                   bool ShowChooseFolder,
                                   string StateTooltip,
                                   string TtlTooltip,
                                   string SpeedTooltip,
                                   bool HasContent);

/// <summary>Turns a <see cref="CacheSnapshot"/> into the overlay line. Pure: time and time zone are parameters.</summary>
internal static class OverlayFormatter
{
    /// <summary>The Auto Warm hint while it is not validated (spec §2, line 70).</summary>
    internal const string AutoWarmHint = "Experimental — not validated";

    private const string Dash = "—";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Formats a snapshot.</summary>
    /// <param name="snapshot">The latest cache snapshot.</param>
    /// <param name="codexWindows">The number of Codex windows; two or more cap the confidence at inferred (spec §3.3).</param>
    /// <param name="hookEvents">The hook events received since the overlay started; none with live JSONL means hooks await Review (spec §4.4 item 7).</param>
    /// <param name="nowUtc">The current time; the remaining TTL counts down from the snapshot's time to it.</param>
    /// <param name="local">The time zone for local times.</param>
    /// <returns>The formatted line.</returns>
    public static OverlayView Format(CacheSnapshot snapshot, int codexWindows, int hookEvents, DateTimeOffset nowUtc, TimeZoneInfo local)
    {
        SelectionConfidence confidence = EffectiveConfidence(snapshot.Selection.Confidence, codexWindows);
        TimeSpan? remaining = snapshot.Ttl.Remaining is { } atSnapshot ? Max(atSnapshot - (nowUtc - snapshot.AtUtc), TimeSpan.Zero) : null;
        bool barKnown = snapshot.Ttl.Estimate is { Ticks: > 0 } && remaining is not null;
        // Whole percent steps: the bar is ~100 DIP wide, and an unchanged view skips a re-render of the layered window.
        double fraction = barKnown ? Math.Round(Math.Clamp(remaining!.Value / snapshot.Ttl.Estimate!.Value, 0, 1), 2) : 0;
        string lastRequest = snapshot.LastRequestAt is { } at ? ToLocal(at, local).ToString("HH:mm", Invariant) : "unknown";

        bool emptyPage = IsEmptyPage(snapshot);
        (string stateText, OverlayTone tone, bool showMetrics) = snapshot.State switch
        {
            CacheState.NoSession => ("Codex session not found", OverlayTone.Neutral, false),
            CacheState.WaitingForUsage => ("Waiting for token usage…", OverlayTone.Neutral, false),
            CacheState.Warm => ("cache", OverlayTone.Warm, true),
            CacheState.ExpiringSoon => ("cache", OverlayTone.Expiring, true),
            CacheState.EstimatedExpired => ("Cache may have expired", OverlayTone.Neutral, true),
            CacheState.ObservedMiss => ($"Observed cache miss {lastRequest}", OverlayTone.Miss, true),
            CacheState.Stale => ($"Last update {lastRequest}", OverlayTone.Neutral, true),
            _ => ("⚠ Error", OverlayTone.Error, snapshot.Input is not null),
        };

        if (snapshot.State is CacheState.EstimatedExpired or CacheState.Stale)
        {
            // The state text already says the estimate ran out or the data is old; an empty bar, no countdown.
            fraction = 0;
        }

        return new OverlayView(stateText,
                               tone,
                               showMetrics,
                               fraction,
                               barKnown,
                               FormatTtl(snapshot, remaining),
                               snapshot.Speed is { } speed ? $"Avg speed {speed.ToString("0", Invariant)} tok/s" : $"Avg speed {Dash}",
                               snapshot.State == CacheState.NoSession && !emptyPage,
                               StateTooltip(snapshot, confidence, codexWindows, hookEvents, local),
                               TtlTooltip(snapshot, remaining, nowUtc, local),
                               snapshot.Speed is null
                                   ? "Timing data unavailable"
                                   : $"Output tokens per second over the thread's last {SpeedTracker.Window} model requests, from sending each " +
                                     "request to the end of its answer. The wait for the first token is included, so the model generates faster.",
                               !emptyPage);
    }

    /// <summary>The state tooltip's hook line: the count since start, or the Review-hooks hint when none came while JSONL is live.</summary>
    /// <param name="hookEvents">The hook events received since the overlay started.</param>
    /// <param name="status">The rollout source status.</param>
    /// <returns>The line.</returns>
    internal static string HookLine(int hookEvents, SourceStatus status) => (hookEvents, status) switch
    {
        ( > 0, _) => $"Hook events since start: {hookEvents.ToString(Invariant)}",
        (_, SourceStatus.Available) => "Hooks not observed: after a plugin update, open Review hooks in Codex",
        _ => "Hook events since start: none",
    };

    /// <summary>
    /// Whether Codex's log says the page on screen has no cache to show: New chat, a thread without a local rollout (a
    /// remote host's thread, a project chat, a cloud task), or a thread not loaded. The overlay hides there rather than
    /// say so, or show another thread's numbers.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>Whether it is such a page.</returns>
    private static bool IsEmptyPage(CacheSnapshot snapshot) =>
        snapshot.State == CacheState.NoSession
        && snapshot.Selection.Reason is SelectionReasons.DesktopRouteNewChat or SelectionReasons.DesktopRouteNoLocalData
                                     or SelectionReasons.DesktopRouteNotLoaded;

    /// <summary>Caps a confidence at inferred while two or more Codex windows are open: the foreground window does not prove the tab.</summary>
    /// <param name="confidence">The resolver's confidence.</param>
    /// <param name="codexWindows">The number of Codex windows.</param>
    /// <returns>The confidence to show.</returns>
    public static SelectionConfidence EffectiveConfidence(SelectionConfidence confidence, int codexWindows)
    {
        return confidence == SelectionConfidence.Confirmed && codexWindows >= 2 ? SelectionConfidence.Inferred : confidence;
    }

    /// <summary>Formats the remaining time as an estimate: always with a tilde, rounded up to whole minutes.</summary>
    /// <param name="remaining">The remaining time.</param>
    /// <returns>The text, e.g. <c>~24 min</c> or <c>~2 h 5 min</c>.</returns>
    public static string FormatRemaining(TimeSpan remaining)
    {
        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return minutes < 60 ? $"~{minutes} min" : $"~{minutes / 60} h {minutes % 60} min";
    }

    private static string FormatTtl(CacheSnapshot snapshot, TimeSpan? remaining)
    {
        if (snapshot.State is CacheState.EstimatedExpired or CacheState.Stale)
        {
            return string.Empty;
        }

        return snapshot.Ttl.Estimate is null || remaining is null ? "TTL unknown" : FormatRemaining(remaining.Value);
    }

    private static string StateTooltip(CacheSnapshot snapshot, SelectionConfidence confidence, int codexWindows, int hookEvents, TimeZoneInfo local)
    {
        List<string> lines = [];

        lines.Add(snapshot.State switch
        {
            CacheState.NoSession => "No rollout JSONL was found.",
            CacheState.WaitingForUsage => "A session exists, but no token usage has been recorded yet.",
            CacheState.Warm => "The last request read from the cache.",
            CacheState.ExpiringSoon => "Estimated: at most a quarter of the assumed TTL is left.",
            CacheState.EstimatedExpired => "The estimated TTL has passed without a new request. This is not an observed miss.",
            CacheState.ObservedMiss => "A new request showed a strong drop in cache reuse against a comparable prefix.",
            CacheState.Stale => "The data has not been updated for a long time, or the file is unavailable.",
            _ => $"The watcher or the parser failed ({snapshot.Diagnostics ?? "no code"}). Use Copy diagnostics in the tray menu.",
        });

        if (snapshot.LastRequestAt is { } at)
        {
            lines.Add($"Last request: {ToLocal(at, local).ToString("yyyy-MM-dd HH:mm:ss", Invariant)} local, " +
                      $"{at.UtcDateTime.ToString("HH:mm:ss", Invariant)} UTC");
        }

        lines.Add($"Model: {snapshot.Model ?? "unknown"}");
        lines.Add($"Source: {FormatSourceText(snapshot.Source.Status)}");
        lines.Add($"Thread: Auto · {Name(confidence)}, {Name(snapshot.Kind)}. {SelectionWhy(snapshot, confidence)}");

        if (codexWindows >= 2)
        {
            lines.Add($"{codexWindows} Codex windows are open, so the confidence is at most inferred.");
        }

        lines.Add(HookLine(hookEvents, snapshot.Source.Status));

        if (snapshot.State == CacheState.Stale && snapshot.Diagnostics is not null)
        {
            lines.Add($"Diagnostics: {snapshot.Diagnostics}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string TtlTooltip(CacheSnapshot snapshot, TimeSpan? remaining, DateTimeOffset nowUtc, TimeZoneInfo local)
    {
        if (snapshot.Ttl.Estimate is not { } estimate || remaining is null)
        {
            return "TTL unknown: not enough data for an estimate.";
        }

        DateTimeOffset expiry = nowUtc + remaining.Value;
        string basis = snapshot.Ttl.Basis switch
        {
            TtlBasis.Observed => "derived from observed cache behaviour",
            TtlBasis.Assumed => "an assumed TTL, not an observation",
            _ => "no basis",
        };

        return $"Estimate ({basis}): {(int)estimate.TotalMinutes} min TTL, about {FormatRemaining(remaining.Value).TrimStart('~')} left." + Environment.NewLine +
               $"Assumed expiry around {ToLocal(expiry, local).ToString("HH:mm", Invariant)} local, {expiry.UtcDateTime.ToString("HH:mm", Invariant)} UTC." + Environment.NewLine +
               "After it, the cache may have expired; a miss is shown only when a new request confirms it.";
    }

    private static string SelectionWhy(CacheSnapshot snapshot, SelectionConfidence confidence)
    {
        bool routed = snapshot.Selection.Reason?.StartsWith(SelectionReasons.DesktopRoute, StringComparison.Ordinal) == true;

        return confidence switch
        {
            SelectionConfidence.Confirmed when routed => "Codex Desktop's log names the page on screen.",
            SelectionConfidence.Confirmed => "A hook signal confirmed this thread.",
            SelectionConfidence.Inferred => "Picked from the last reliable event; the tab you are looking at may differ.",
            _ => "No thread could be selected yet.",
        };
    }

    private static string FormatSourceText(SourceStatus status) => status switch
    {
        SourceStatus.Available => "JSONL",
        SourceStatus.NotFound => "no JSONL",
        _ => "JSONL error",
    };

    private static string Name(SelectionConfidence confidence) => confidence switch
    {
        SelectionConfidence.Confirmed => "confirmed",
        SelectionConfidence.Inferred => "inferred",
        _ => "unknown",
    };

    private static string Name(ThreadKind kind) => kind switch
    {
        ThreadKind.Main => "main thread",
        ThreadKind.Fork => "fork",
        ThreadKind.Subagent => "subagent",
        _ => "kind unknown",
    };

    private static DateTimeOffset ToLocal(DateTimeOffset value, TimeZoneInfo local) => TimeZoneInfo.ConvertTime(value, local);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
