using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App;

/// <summary>What the overlay learned before WPF started.</summary>
/// <param name="Paths">The overlay's files.</param>
/// <param name="Log">The overlay log.</param>
/// <param name="Pipe">The hook pipe, or <see langword="null"/> when it could not be created; the overlay then runs on JSONL alone.</param>
/// <param name="Job">The job object the overlay runs in.</param>
internal sealed record OverlayStartup(PulsePaths Paths, RollingFileLog Log, HookPipeServer? Pipe, JobMembership Job);

/// <summary>
/// The first, WPF-free part of <c>--overlay</c> (spec §4.4 item 2): take the single-instance mutex, open the hook pipe,
/// then hand over to <see cref="OverlayHost"/>. A second overlay, started by a parallel hook, loses the mutex and exits
/// before it creates a window or a tray icon. The pipe exists before WPF loads, so a hook that has just started the
/// overlay can deliver its event within its short wait.
/// </summary>
internal static class OverlayBootstrap
{
    /// <summary>Runs the overlay unless another one holds the mutex.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>The process exit code; 0 when another overlay already runs.</returns>
    internal static int Run(LaunchOptions options)
    {
        PulsePaths paths = PulsePaths.FromEnvironment(Environment.GetEnvironmentVariable);
        RollingFileLog log = new(paths.LogsDirectory);
        PulseChannel channel = PulseChannel.For(paths);

        Mutex singleInstance;
        bool createdNew;

        try
        {
            singleInstance = new Mutex(initiallyOwned: true, channel.MutexName, out createdNew);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // The mutex exists under a security context this process cannot open: another overlay owns the channel.
            log.Warn($"single-instance mutex not accessible ({exception.GetType().Name}); exiting");

            return 0;
        }

        // Held, never released, until the process ends: the mutex object goes with its last handle.
        using Mutex held = singleInstance;

        if (!createdNew)
        {
            log.Info("another overlay holds the single-instance mutex; exiting");

            return 0;
        }

        using HookPipeServer? pipe = HookPipeServer.TryStart(channel.PipeName, exception => log.Error("hook pipe stopped", exception));
        JobMembership job = JobProcess.Current();

        log.Info($"overlay bootstrap: pid {Environment.ProcessId}, protocol {PulseChannel.ProtocolMajor}, pipe {(pipe is null ? "not available (JSONL only)" : "listening")}, "
                 + $"job {(job.InJob ? $"0x{job.LimitFlags:X4}" : "none")}, exitsWithJob {job.ExitsWithJob}");

        return OverlayHost.Run(options, new OverlayStartup(paths, log, pipe, job));
    }
}
