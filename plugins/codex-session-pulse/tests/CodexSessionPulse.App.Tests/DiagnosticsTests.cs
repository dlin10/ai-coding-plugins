using System.IO;
using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.App.Stub;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>Copy diagnostics, the rolling log, visibility and the command line.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private const string Profile = @"C:\Users\jdoe";
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "csp-logs-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_logs))
        {
            Directory.Delete(_logs, recursive: true);
        }
    }

    /// <summary>The report has the detector state and the cache state, and the user name appears nowhere.</summary>
    [Fact]
    public void Report_HasDetectorStateAndRedactsTheUser()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CacheSnapshot cache = StubSnapshotScript.At(2, now) with { Source = new SourceInfo(SourceStatus.Available, Profile + @"\.codex\sessions\r.jsonl", "0.130.0") };
        AnchorSnapshot anchor = new(now, true, 2, new WindowBounds(0, 0, 1920, 1152), 1.0, AnchorState.Attached, 42, new WindowBounds(0, 0, 1920, 1152), null);
        AnchorDiagnostics detector = new(WindowDetectorOptions.Default.Describe(), 2, AnchorState.Attached, 1, true, true, 17);
        DiagnosticsInput input = new("1.0.0", "Windows 11 for jdoe", new PulseSettings { SessionRootOverride = Profile + @"\sessions" }, ["ttl-out-of-range"],
                                     Profile + @"\.codex-session-pulse\settings.json", Profile + @"\.codex-session-pulse\logs\overlay.log",
                                     detector, anchor, cache, new HookActivityRecorder().Snapshot(), true, true, true, false, 0x08080080);
        HookActivityRecorder hooks = new();
        hooks.Record("UserPromptSubmit", now);
        hooks.Record("SessionStart", now.AddMinutes(-1));
        hooks.Record("UserPromptSubmit", now);

        string report = DiagnosticsReport.Build(input, Profile, "jdoe");
        string withHooks = DiagnosticsReport.Build(input with { Hooks = hooks.Snapshot() }, Profile, "jdoe");

        Assert.Contains("hook events since start: none observed", report, StringComparison.Ordinal);
        Assert.Contains("hook events since start: SessionStart 1, UserPromptSubmit 2; last UserPromptSubmit at", withHooks, StringComparison.Ordinal);

        Assert.DoesNotContain("jdoe", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\.codex\sessions\r.jsonl", report, StringComparison.Ordinal);
        Assert.Contains("codex windows: 2", report, StringComparison.Ordinal);
        Assert.Contains(@"rule: processes [Codex, ChatGPT]", report, StringComparison.Ordinal);
        Assert.Contains("selection: Confirmed, shown as Inferred", report, StringComparison.Ordinal);
        Assert.Contains("state: Warm", report, StringComparison.Ordinal);
        Assert.Contains("hook events since start:", report, StringComparison.Ordinal);
        Assert.Contains("problems: ttl-out-of-range", report, StringComparison.Ordinal);
        Assert.Contains("ex-style 0x08080080", report, StringComparison.Ordinal);
    }

    /// <summary>The log keeps the current file plus four rotated ones, each under the size limit.</summary>
    [Fact]
    public void RollingLog_KeepsFiveFilesUnderTheLimit()
    {
        RollingFileLog log = new(_logs, maxBytes: 1000, maxFiles: 5);

        for (int i = 0; i < 200; i++)
        {
            log.Info($"line {i:D4} " + new string('x', 40));
        }

        string[] files = Directory.GetFiles(_logs).Select(Path.GetFileName).Order().ToArray()!;

        Assert.Equal(["overlay.1.log", "overlay.2.log", "overlay.3.log", "overlay.4.log", "overlay.log"], files);
        Assert.All(Directory.GetFiles(_logs), file => Assert.InRange(new FileInfo(file).Length, 1, 1000));
        Assert.Contains("line 0199", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two writers of one file, as two hooks that log at once: each instance has its own lock, like separate processes,
    /// so they meet only at the file, and every line still arrives whole.
    /// </summary>
    [Fact]
    public void RollingLog_ConcurrentWritersLoseNoLine()
    {
        const int LinesPerWriter = 300;
        RollingFileLog[] writers = [new(_logs, baseName: "hook"), new(_logs, baseName: "hook")];

        // Threads of their own, like two processes, and so no pool thread is held while the writers wait for each other.
        Thread[] threads = Enumerable.Range(0, writers.Length).Select(w => new Thread(() =>
        {
            for (int i = 0; i < LinesPerWriter; i++)
            {
                writers[w].Info($"writer {w} line {i:D4}");
            }
        })).ToArray();
        Array.ForEach(threads, thread => thread.Start());
        Array.ForEach(threads, thread => thread.Join());

        string[] lines = File.ReadAllLines(writers[0].CurrentFile);

        Assert.Equal(writers.Length * LinesPerWriter, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\S+ INFO writer \d line \d{4}$", line));
    }

    /// <summary>The overlay shows only for the foreground Codex window, or a background one with keep-visible, and never when hidden from the tray.</summary>
    /// <param name="state">The anchor state.</param>
    /// <param name="hideWhenInactive">The hide-when-inactive setting.</param>
    /// <param name="userHidden">Whether the user hid it from the tray.</param>
    /// <param name="expected">Whether the overlay is shown.</param>
    [Theory]
    [InlineData(AnchorState.Attached, true, false, true)]
    [InlineData(AnchorState.Background, true, false, false)]
    [InlineData(AnchorState.Background, false, false, true)]
    [InlineData(AnchorState.Minimized, false, false, false)]
    [InlineData(AnchorState.NotFound, false, false, false)]
    [InlineData(AnchorState.Attached, true, true, false)]
    public void Visibility_FollowsTheAnchorAndTheSettings(AnchorState state, bool hideWhenInactive, bool userHidden, bool expected)
    {
        Assert.Equal(expected, OverlayVisibility.ShouldShow(state, hideWhenInactive, userHidden));
    }

    /// <summary><c>--stub</c> is parsed next to <c>--overlay</c>; the hook path ignores it.</summary>
    [Fact]
    public void LaunchOptions_ParseStub()
    {
        Assert.Equal(new LaunchOptions(LaunchMode.Overlay, false, true), LaunchOptions.Parse(["--overlay", "--stub"]));
        Assert.Equal(new LaunchOptions(LaunchMode.Hook, false, false), LaunchOptions.Parse(["--hook"]));
    }
}
