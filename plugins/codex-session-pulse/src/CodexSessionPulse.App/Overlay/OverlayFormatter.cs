using System.Globalization;
using System.IO;
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

/// <summary>Everything the overlay line shows, already formatted (spec §3.1, §3.2). Core never formats; this is the only place that does.</summary>
/// <param name="StateText">The state label, e.g. <c>cache</c> or <c>Observed cache miss 14:32</c>.</param>
/// <param name="Tone">The colour role.</param>
/// <param name="ShowMetrics">Whether the bar, TTL, tokens and speed are shown.</param>
/// <param name="BarFraction">The filled share of the bar, from 0 to 1.</param>
/// <param name="BarKnown">Whether the bar reflects an estimate; an unknown TTL gives a grey indefinite bar.</param>
/// <param name="TtlText">The approximate remaining time, <c>~24 min</c>, or <c>TTL unknown</c>.</param>
/// <param name="TokensText">Cached over input tokens of the last request, <c>245.1k/245.3k</c>.</param>
/// <param name="SpeedText">Always <c>Avg speed —</c> in P0.</param>
/// <param name="SelectionText">The automatic selection and its confidence, <c>Auto · inferred</c>.</param>
/// <param name="SourceText">The data source in one word.</param>
/// <param name="ShowChooseFolder">Whether the Choose folder button is shown (no session found).</param>
/// <param name="StateTooltip">The state tooltip: times, model, source and selection.</param>
/// <param name="TtlTooltip">The TTL tooltip; always says it is an estimate.</param>
/// <param name="TokensTooltip">The tokens tooltip: percentage and cache write.</param>
/// <param name="SpeedTooltip">The speed tooltip.</param>
/// <param name="SelectionTooltip">The selection tooltip.</param>
/// <param name="SourceTooltip">The source tooltip.</param>
internal sealed record OverlayView(string StateText,
                                   OverlayTone Tone,
                                   bool ShowMetrics,
                                   double BarFraction,
                                   bool BarKnown,
                                   string TtlText,
                                   string TokensText,
                                   string SpeedText,
                                   string SelectionText,
                                   string SourceText,
                                   bool ShowChooseFolder,
                                   string StateTooltip,
                                   string TtlTooltip,
                                   string TokensTooltip,
                                   string SpeedTooltip,
                                   string SelectionTooltip,
                                   string SourceTooltip);

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
                               $"{FormatTokens(snapshot.CachedInput)}/{FormatTokens(snapshot.Input)}",
                               $"Avg speed {Dash}",
                               $"Auto · {Name(confidence)}",
                               FormatSourceText(snapshot.Source.Status),
                               snapshot.State == CacheState.NoSession,
                               StateTooltip(snapshot, confidence, local),
                               TtlTooltip(snapshot, remaining, nowUtc, local),
                               TokensTooltip(snapshot),
                               "Timing data unavailable",
                               SelectionTooltip(snapshot, confidence, codexWindows),
                               SourceTooltip(snapshot.Source, hookEvents));
    }

    /// <summary>The tooltip's hook line: the count since start, or the Review-hooks hint when none came while JSONL is live.</summary>
    /// <param name="hookEvents">The hook events received since the overlay started.</param>
    /// <param name="status">The rollout source status.</param>
    /// <returns>The line.</returns>
    internal static string HookLine(int hookEvents, SourceStatus status) => (hookEvents, status) switch
    {
        ( > 0, _) => $"Hook events since start: {hookEvents.ToString(Invariant)}",
        (_, SourceStatus.Available) => "Hooks not observed: after a plugin update, open Review hooks in Codex",
        _ => "Hook events since start: none",
    };

    /// <summary>Caps a confidence at inferred while two or more Codex windows are open: the foreground window does not prove the tab.</summary>
    /// <param name="confidence">The resolver's confidence.</param>
    /// <param name="codexWindows">The number of Codex windows.</param>
    /// <returns>The confidence to show.</returns>
    public static SelectionConfidence EffectiveConfidence(SelectionConfidence confidence, int codexWindows)
    {
        return confidence == SelectionConfidence.Confirmed && codexWindows >= 2 ? SelectionConfidence.Inferred : confidence;
    }

    /// <summary>Formats a token count with a <c>k</c> or <c>M</c> suffix and one decimal.</summary>
    /// <param name="tokens">The count, or <see langword="null"/> when unknown.</param>
    /// <returns>The text; an unknown count is a dash.</returns>
    public static string FormatTokens(long? tokens)
    {
        return tokens switch
        {
            null => Dash,
            < 1000 => tokens.Value.ToString(Invariant),
            < 1_000_000 => (tokens.Value / 1000.0).ToString("0.0", Invariant) + "k",
            _ => (tokens.Value / 1_000_000.0).ToString("0.0", Invariant) + "M",
        };
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

    private static string StateTooltip(CacheSnapshot snapshot, SelectionConfidence confidence, TimeZoneInfo local)
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
        lines.Add($"Thread: Auto · {Name(confidence)}, {Name(snapshot.Kind)}");

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

    private static string TokensTooltip(CacheSnapshot snapshot)
    {
        double? ratio = snapshot.CacheRatio
                        ?? (snapshot is { CachedInput: { } cached, Input: { } input and > 0 } ? (double)cached / input : null);
        string share = ratio is { } value ? (value * 100).ToString("0.00", Invariant) + "%" : "unknown";
        string write = snapshot.CacheWrite is { } written ? written.ToString("N0", Invariant) + " tokens" : "unknown";

        return $"Cached {Count(snapshot.CachedInput)} of {Count(snapshot.Input)} input tokens of the last request ({share})." + Environment.NewLine +
               $"Cache write: {write}";
    }

    private static string SelectionTooltip(CacheSnapshot snapshot, SelectionConfidence confidence, int codexWindows)
    {
        string text = confidence switch
        {
            SelectionConfidence.Confirmed => "Auto · confirmed: a hook signal confirmed this thread.",
            SelectionConfidence.Inferred => "Auto · inferred: picked from the last reliable event; the tab you are looking at may differ.",
            _ => "Auto · unknown: no thread could be selected yet.",
        };

        if (codexWindows >= 2)
        {
            text += Environment.NewLine + $"{codexWindows} Codex windows are open, so the confidence is at most inferred.";
        }

        if (snapshot.Selection.Reason is { } reason)
        {
            text += Environment.NewLine + $"Reason: {reason}";
        }

        return text;
    }

    private static string FormatSourceText(SourceStatus status) => status switch
    {
        SourceStatus.Available => "JSONL",
        SourceStatus.NotFound => "no JSONL",
        _ => "JSONL error",
    };

    private static string SourceTooltip(SourceInfo source, int hookEvents)
    {
        string file = source.Path is { } path ? Path.GetFileName(path) : "none";

        return $"Source: rollout JSONL, {source.Status}" + Environment.NewLine +
               $"File: {file}" + Environment.NewLine +
               $"Codex CLI: {source.CliVersion ?? "unknown"}" + Environment.NewLine +
               HookLine(hookEvents, source.Status);
    }

    private static string Count(long? tokens) => tokens is { } value ? value.ToString("N0", Invariant) : "unknown";

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
