using System.Diagnostics;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>
/// The lifecycle rules of spec §4.4 and §11.2 against the real exe: hooks, the single overlay, the pipe, autostart from a
/// job, <c>--no-start</c>, JSONL without hooks and the exit after the last Codex window.
/// </summary>
[Collection(ExeCollection.Name)]
public sealed class HookLifecycleTests
{
    private const string Rollout = "rollout-2026-11-06T23-54-34-00000000-0000-7000-8000-000000000002.jsonl";
    private const string RolloutThread = "00000000-0000-7000-8000-000000000002";

    /// <summary>SessionStart and UserPromptSubmit at once with no overlay: one overlay starts and receives both events.</summary>
    [Fact]
    public async Task ColdParallelStart_BringsUpOneOverlayThatGetsBoth()
    {
        using LifecycleHome home = new();

        Task<(int ExitCode, long ElapsedMs)> start = Task.Run(() => home.RunHook(Payload("SessionStart", RolloutThread, "startup")));
        Task<(int ExitCode, long ElapsedMs)> prompt = Task.Run(() => home.RunHook(Payload("UserPromptSubmit", RolloutThread, null)));

        Assert.Equal(0, (await start).ExitCode);
        Assert.Equal(0, (await prompt).ExitCode);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook UserPromptSubmit"), "UserPromptSubmit did not reach the overlay.");
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook SessionStart (startup)"), "SessionStart did not reach the overlay with its source.");
        Thread.Sleep(1500);

        Assert.Single(home.RunningOverlays());
        Assert.Equal(1, LifecycleHome.Count(home.OverlayLog, "overlay start:"));
        Assert.Equal(0, LifecycleHome.Count(home.OverlayLog, "ERROR"));
    }

    /// <summary>A hook to a running overlay is quick and starts nothing new; SessionEnd reaches it and does not end it.</summary>
    [Fact]
    public void WarmHooks_AreDeliveredAndSessionEndKeepsTheOverlay()
    {
        using LifecycleHome home = new();
        Assert.Equal(0, home.RunHook(Payload("SessionStart", RolloutThread, "resume")).ExitCode);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook SessionStart (resume)"));

        (int exitCode, long elapsedMs) = home.RunHook(Payload("SessionEnd", RolloutThread, null), noStart: true);

        Assert.Equal(0, exitCode);
        Assert.InRange(elapsedMs, 0, 3000);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook SessionEnd"));
        Thread.Sleep(5000);
        Assert.Single(home.RunningOverlays());
    }

    /// <summary>SessionEnd with --no-start and no overlay finishes well within 3 s and starts nothing.</summary>
    [Fact]
    public void NoStart_WithoutOverlay_StartsNothing()
    {
        using LifecycleHome home = new();

        (int exitCode, long elapsedMs) = home.RunHook(Payload("SessionEnd", RolloutThread, null), noStart: true);
        Thread.Sleep(1500);

        Assert.Equal(0, exitCode);
        Assert.InRange(elapsedMs, 0, 3000);
        Assert.Empty(home.RunningOverlays());
        Assert.False(File.Exists(home.OverlayLog));
    }

    /// <summary>
    /// UserPromptSubmit while an overlay holds the mutex but has no pipe yet: the hook gives up within its limit and exits 0,
    /// and an overlay without a single hook still finds the thread from the JSONL tail, inferred.
    /// </summary>
    [Fact]
    public void PromptBeforePipe_ExitsZero_AndJsonlCatchesUp()
    {
        using LifecycleHome home = new();
        home.AddRollout(Rollout);

        using (Mutex starting = new(initiallyOwned: true, MutexName(home)))
        {
            (int exitCode, long elapsedMs) = home.RunHook(Payload("UserPromptSubmit", RolloutThread, null));

            Assert.Equal(0, exitCode);
            Assert.InRange(elapsedMs, 300, 3000);
            Assert.Empty(home.RunningOverlays());
            starting.ReleaseMutex();
        }

        using Process overlay = home.Start("--overlay", redirectStdin: false);

        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "selection Inferred (jsonl-latest-activity)"),
                    "The overlay did not pick the thread up from JSONL.");
        Assert.Equal(0, LifecycleHome.Count(home.OverlayLog, "hook "));
        Assert.True(LifecycleHome.CloseGracefully(overlay));
    }

    /// <summary>
    /// A hook in a kill-on-close job that allows breakaway (Codex's 0x2800) starts the overlay outside the job: closing the
    /// job, as Codex might, leaves the overlay running.
    /// </summary>
    [Fact]
    public void HookInBreakawayJob_OverlaySurvivesTheJob()
    {
        using LifecycleHome home = new();

        using (KillOnCloseJob job = new(allowBreakaway: true))
        {
            Assert.Equal(0, home.RunHook(Payload("SessionStart", RolloutThread, "startup"), job: job).ExitCode);
            Assert.True(LifecycleHome.WaitForLog(home.HookLog, "outside the hook's job"));
            Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook SessionStart"));
        }

        Thread.Sleep(2000);
        Assert.Single(home.RunningOverlays());
    }

    /// <summary>
    /// A hook in a kill-on-close job that refuses breakaway starts the overlay inside it, says so, and the overlay reports
    /// exitsWithJob: closing that job ends it. This is the honest consequence, not a supported path.
    /// </summary>
    [Fact]
    public void HookInStrictJob_OverlayReportsExitsWithJob()
    {
        using LifecycleHome home = new();
        List<Process> overlays;

        using (KillOnCloseJob job = new(allowBreakaway: false))
        {
            Assert.Equal(0, home.RunHook(Payload("SessionStart", RolloutThread, "startup"), job: job).ExitCode);
            Assert.True(LifecycleHome.WaitForLog(home.HookLog, "inside the hook's job (breakaway refused)"));
            Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "exitsWithJob True"));
            overlays = home.RunningOverlays();
            Assert.Single(overlays);
        }

        Assert.True(overlays[0].WaitForExit(10_000), "The overlay outlived the job that kills on close.");
        overlays[0].Dispose();
    }

    /// <summary>
    /// The exit rule on windows the overlay takes for Codex: started by hand with none it keeps running; minimizing and
    /// closing one of two windows keep it; closing the last one ends it within the grace period, through the normal shutdown.
    /// </summary>
    [Fact]
    public void Overlay_ExitsOnlyAfterTheLastCodexWindow()
    {
        string title = "CSP-FAKE-CODEX-" + Guid.NewGuid().ToString("N")[..8];
        using LifecycleHome home = new([FakeCodexWindow.ProcessName], title);
        using Process overlay = home.Start("--overlay", redirectStdin: false);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "anchor NotFound"));
        Thread.Sleep(5000);
        Assert.False(overlay.HasExited, "A manually started overlay exited with no Codex window ever seen.");

        FakeCodexWindow first = new(title);
        FakeCodexWindow second = new(title);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "codex windows 2"));

        first.Minimize();
        first.Dispose();
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "codex windows 1"));
        Thread.Sleep(5000);
        Assert.False(overlay.HasExited, "Closing one of two windows ended the overlay.");

        second.Minimize();
        Thread.Sleep(5000);
        Assert.False(overlay.HasExited, "A minimized window counted as closed.");

        Stopwatch closed = Stopwatch.StartNew();
        second.Dispose();

        Assert.True(overlay.WaitForExit(15_000), "The overlay did not exit after the last window closed.");
        Assert.Equal(0, overlay.ExitCode);
        Assert.InRange(closed.ElapsedMilliseconds, 2500, 6000);
        Assert.Equal(1, LifecycleHome.Count(home.OverlayLog, "no Codex window left after the grace period, exiting"));
        Assert.Equal(1, LifecycleHome.Count(home.OverlayLog, "overlay stop"));
    }

    private static string MutexName(LifecycleHome home)
    {
        // The same rule as PulseChannel.For: the SID, the protocol major and a hash of the moved home.
        ulong hash = 14695981039346656037;

        foreach (char c in Path.GetFullPath(home.Root).ToUpperInvariant())
        {
            hash = (hash ^ c) * 1099511628211;
        }

        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

        return $@"Local\CodexSessionPulse.{sid}.1.{hash:x16}";
    }

    private static string Payload(string eventName, string threadId, string? source)
    {
        string path = $@"C:\\Users\\someone\\.codex\\sessions\\2026\\11\\06\\rollout-2026-11-06T23-54-34-{threadId}.jsonl";
        string sourceField = source is null ? string.Empty : $",\"source\":\"{source}\"";

        return $"{{\"session_id\":\"{threadId}\",\"transcript_path\":\"{path}\",\"cwd\":\"C:\\\\work\",\"hook_event_name\":\"{eventName}\""
               + $",\"model\":\"gpt-5\",\"permission_mode\":\"default\"{sourceField},\"prompt\":\"do not log this\"}}";
    }
}
