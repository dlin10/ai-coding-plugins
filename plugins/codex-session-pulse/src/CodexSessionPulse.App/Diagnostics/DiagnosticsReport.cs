using System.Globalization;
using System.Text;
using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App.Diagnostics;

/// <summary>What "Copy diagnostics" reports. It carries states, counts and paths, never message or prompt text.</summary>
/// <param name="AppVersion">The app version.</param>
/// <param name="OsDescription">The OS description.</param>
/// <param name="Settings">The settings in effect.</param>
/// <param name="SettingsProblems">The codes of the settings problems found on load.</param>
/// <param name="SettingsFile">The settings file.</param>
/// <param name="LogFile">The current log file.</param>
/// <param name="Detector">The window detector state.</param>
/// <param name="Anchor">The latest anchor snapshot.</param>
/// <param name="Cache">The latest cache snapshot, or <see langword="null"/>.</param>
/// <param name="Hooks">The hooks received since start.</param>
/// <param name="StubSource">Whether the snapshots come from the development stub.</param>
/// <param name="TrayAdded">Whether the tray icon was added.</param>
/// <param name="OverlayVisible">Whether the overlay is shown.</param>
/// <param name="UserHidden">Whether the user hid it from the tray.</param>
/// <param name="OverlayExtendedStyle">The overlay window's extended style.</param>
/// <param name="Pipeline">The hook pipe, the job and the rollout reader, or <see langword="null"/> before they exist.</param>
/// <param name="Input">The input card locator, or <see langword="null"/> before it exists.</param>
internal sealed record DiagnosticsInput(string AppVersion,
                                        string OsDescription,
                                        PulseSettings Settings,
                                        IReadOnlyList<string> SettingsProblems,
                                        string SettingsFile,
                                        string LogFile,
                                        AnchorDiagnostics Detector,
                                        AnchorSnapshot Anchor,
                                        CacheSnapshot? Cache,
                                        HookActivitySnapshot Hooks,
                                        bool StubSource,
                                        bool TrayAdded,
                                        bool OverlayVisible,
                                        bool UserHidden,
                                        long OverlayExtendedStyle,
                                        PipelineDiagnostics? Pipeline = null,
                                        InputLocatorDiagnostics? Input = null);

/// <summary>The process side of the overlay for "Copy diagnostics": hook pipe, job object, rollout reader and exit rule.</summary>
/// <param name="PipeListening">Whether the hook pipe is open; without it the overlay runs on JSONL alone.</param>
/// <param name="PipeRejected">The pipe connections whose data was not a valid message.</param>
/// <param name="Job">The job object the overlay runs in.</param>
/// <param name="RolloutSource">The rollout source status, or <see langword="null"/> under <c>--stub</c>.</param>
/// <param name="RolloutCounters">The reader's counters by <see cref="DiagnosticCodes"/> value.</param>
/// <param name="ExitPending">Whether the last Codex window is gone and the exit waits for the grace period.</param>
internal sealed record PipelineDiagnostics(bool PipeListening,
                                           long PipeRejected,
                                           JobMembership Job,
                                           SourceStatus? RolloutSource,
                                           IReadOnlyDictionary<string, long> RolloutCounters,
                                           bool ExitPending);

/// <summary>Builds the "Copy diagnostics" text (spec §8.2) with the user name redacted.</summary>
internal static class DiagnosticsReport
{
    /// <summary>Builds the report.</summary>
    /// <param name="input">What to report.</param>
    /// <param name="userProfile">The user profile path, replaced by <c>%USERPROFILE%</c>.</param>
    /// <param name="userName">The user name, replaced by <c>&lt;user&gt;</c> wherever it is left.</param>
    /// <returns>The report.</returns>
    public static string Build(DiagnosticsInput input, string userProfile, string userName)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        PulseSettings settings = input.Settings;
        CacheSnapshot? cache = input.Cache;

        text.AppendLine("Codex Session Pulse diagnostics");
        text.AppendLine(invariant, $"app: {input.AppVersion}");
        text.AppendLine(invariant, $"os: {input.OsDescription}");
        text.AppendLine(invariant, $"snapshot source: {(input.StubSource ? "development stub (--stub)" : "rollout JSONL files, hooks as an accelerator")}");
        text.AppendLine();

        text.AppendLine("[window detector]");
        text.AppendLine(invariant, $"rule: {input.Detector.Rule}");
        text.AppendLine(invariant, $"codex windows: {input.Detector.CodexWindows}");
        text.AppendLine(invariant, $"anchor state: {input.Detector.State}{(input.Anchor.Diagnostics is { } code ? $" ({code})" : string.Empty)}");
        text.AppendLine(invariant, $"anchor bounds: {input.Anchor.Bounds?.ToString() ?? "none"}, work area: {input.Anchor.WorkArea?.ToString() ?? "none"}, dpi scale: {input.Anchor.DpiScale.ToString(invariant)}");
        text.AppendLine(invariant, $"hooks: global {(input.Detector.GlobalHooksInstalled ? "installed" : "missing")}, per-process {input.Detector.ProcessHooks}, polling {(input.Detector.PollingActive ? "on" : "off")}, refreshes {input.Detector.Refreshes}");
        text.AppendLine(invariant, $"overlay: {(input.OverlayVisible ? "visible" : "hidden")}{(input.UserHidden ? " (hidden from the tray)" : string.Empty)}, ex-style 0x{input.OverlayExtendedStyle:X8}, tray {(input.TrayAdded ? "added" : "not added")}");

        if (input.Input is { } locator)
        {
            text.AppendLine(invariant, $"input card: {(locator.Tracking ? "tracking" : "idle")}, {(locator.Found ? "found" : "not found, placed by the window")}, searches {locator.Searches}, last search {locator.LastSearchMs.ToString("F1", invariant)} ms{(locator.LastError is { } error ? $", last error {error}" : string.Empty)}");
        }

        text.AppendLine();

        text.AppendLine("[cache]");

        if (cache is null)
        {
            text.AppendLine("no snapshot yet");
        }
        else
        {
            SelectionConfidence shown = OverlayFormatter.EffectiveConfidence(cache.Selection.Confidence, input.Anchor.CodexWindows);
            text.AppendLine(invariant, $"state: {cache.State}{(cache.Diagnostics is { } cacheCode ? $" ({cacheCode})" : string.Empty)}");
            text.AppendLine(invariant, $"selection: {cache.Selection.Confidence}, shown as {shown}, reason {cache.Selection.Reason ?? "none"}, kind {cache.Kind}");
            text.AppendLine(invariant, $"last request: {cache.LastRequestAt?.ToString("O", invariant) ?? "none"}");
            text.AppendLine(invariant, $"cached/input: {cache.CachedInput?.ToString(invariant) ?? "?"}/{cache.Input?.ToString(invariant) ?? "?"}, cache write: {cache.CacheWrite?.ToString(invariant) ?? "unknown"}, model: {cache.Model ?? "unknown"}");
            text.AppendLine(invariant, $"ttl: estimate {cache.Ttl.Estimate?.TotalMinutes.ToString(invariant) ?? "none"} min, remaining {cache.Ttl.Remaining?.ToString(@"hh\:mm\:ss", invariant) ?? "none"}, basis {cache.Ttl.Basis}");
            text.AppendLine(invariant, $"source: {cache.Source.Status}, path {cache.Source.Path ?? "none"}, codex cli {cache.Source.CliVersion ?? "unknown"}");
        }

        if (input.Pipeline is { RolloutSource: { } rolloutSource } pipeline)
        {
            string counters = pipeline.RolloutCounters.Count == 0
                ? "none"
                : string.Join(", ", pipeline.RolloutCounters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value}"));
            text.AppendLine(invariant, $"rollout source: {rolloutSource}; reader counters: {counters}");
        }
        else
        {
            text.AppendLine("rollout source: not running");
        }

        if (input.Hooks.CountsByHookType.Count == 0)
        {
            text.AppendLine("hook events since start: none observed (after a plugin update, open Review hooks in Codex)");
        }
        else
        {
            string counts = string.Join(", ", input.Hooks.CountsByHookType.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                          .Select(pair => $"{pair.Key} {pair.Value}"));
            text.AppendLine(invariant, $"hook events since start: {counts}; last {input.Hooks.LastSource} at {input.Hooks.LastHookAtUtc?.ToString("O", invariant)}");
        }

        text.AppendLine();

        if (input.Pipeline is { } lifecycle)
        {
            text.AppendLine("[lifecycle]");
            text.AppendLine(invariant, $"hook pipe: {(lifecycle.PipeListening ? "listening" : "not available, hooks fall back to JSONL")}, protocol {Ipc.PulseChannel.ProtocolMajor}, rejected {lifecycle.PipeRejected}");
            text.AppendLine(invariant, $"job: {(lifecycle.Job.InJob ? $"0x{lifecycle.Job.LimitFlags:X4}" : "none")}, exitsWithJob {lifecycle.Job.ExitsWithJob}");
            text.AppendLine(invariant, $"exit: after the last Codex window is gone for {LastWindowExit.DefaultGracePeriod.TotalSeconds.ToString(invariant)} s{(lifecycle.ExitPending ? " (pending now)" : string.Empty)}; SessionEnd never ends the overlay");
            text.AppendLine();
        }

        text.AppendLine("[settings]");
        text.AppendLine(invariant, $"file: {input.SettingsFile}");
        text.AppendLine(invariant, $"problems: {(input.SettingsProblems.Count == 0 ? "none" : string.Join(", ", input.SettingsProblems))}");
        text.AppendLine(invariant, $"session root override: {settings.SessionRootOverride ?? "none"}");
        text.AppendLine(invariant, $"title filter: {(string.IsNullOrEmpty(settings.CodexWindowTitleFilter) ? "none" : "set")}");
        text.AppendLine(invariant, $"anchor {settings.Anchor}, offset {settings.OffsetX},{settings.OffsetY} DIP, hide when inactive {settings.HideWhenCodexInactive}, theme {settings.Theme}");
        text.AppendLine(invariant, $"ttl estimate {settings.EstimatedCacheTtlMinutes} min, min cached tokens {settings.MinCachedTokensForWarm}");
        text.AppendLine(invariant, $"log: {input.LogFile}");

        return Redact(text.ToString(), userProfile, userName);
    }

    /// <summary>
    /// Replaces the profile path, then any remaining user name, and cuts every GUID, such as the thread id in a rollout file
    /// name, to its first 8 characters (spec §10: no raw ids). Codex thread ids are UUIDv7, so those 8 are only a time prefix.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="userProfile">The user profile path.</param>
    /// <param name="userName">The user name.</param>
    /// <returns>The redacted text.</returns>
    public static string Redact(string text, string userProfile, string userName)
    {
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\b([0-9a-fA-F]{8})-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "$1…",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!string.IsNullOrEmpty(userProfile))
        {
            text = text.Replace(userProfile.TrimEnd('\\'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        if (userName.Length >= 2)
        {
            text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
