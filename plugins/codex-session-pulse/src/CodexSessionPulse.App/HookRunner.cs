using System.IO;
using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.Hooks;
using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App;

/// <summary>What one hook run did; the hook logs only the outcomes that need a look.</summary>
internal enum HookOutcome
{
    /// <summary>The running overlay got the message.</summary>
    Delivered,

    /// <summary>No overlay runs and <c>--no-start</c> forbids starting one; nothing was created.</summary>
    NotRunning,

    /// <summary>The hook started the overlay and it got the message.</summary>
    StartedAndDelivered,

    /// <summary>The hook started the overlay, but its pipe was not ready in time; the overlay reads the event from JSONL.</summary>
    StartedNotDelivered,

    /// <summary>The overlay could not be started.</summary>
    StartFailed,

    /// <summary>An overlay runs, but its pipe did not take the message in time.</summary>
    NotDelivered,
}

/// <summary>
/// The <c>--hook</c> path (spec §4.4 items 1, 2 and 6). It reads the hook payload from stdin, keeps it only in memory and
/// never writes it anywhere, reads nothing from the current directory, sends the event to the overlay, starts the overlay
/// when it is absent unless <c>--no-start</c> is given, and always succeeds. No WPF type is reached from here.
/// </summary>
internal static class HookRunner
{
    /// <summary>The environment variable that makes the hook print the names of the loaded assemblies.</summary>
    internal const string DebugAssembliesVariable = "CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES";

    /// <summary>
    /// How long a hook that has just started the overlay waits for its pipe: the bounded retry of the cold-start race.
    /// The overlay opens the pipe before it loads WPF, so this covers process start and the .NET runtime only.
    /// </summary>
    internal const int ColdStartConnectTimeoutMs = 1000;

    /// <summary>Reads the hook payload, delivers it and returns the exit code.</summary>
    /// <param name="stdin">The stream holding the payload.</param>
    /// <param name="noStart">Whether the overlay must not be started when it is absent (<c>SessionEnd</c>).</param>
    /// <param name="getStdout">Opens stdout; called only when the debug variable is set, so the normal path never creates the console writer.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <returns>Always 0: a hook must never fail Codex.</returns>
    internal static int Run(Stream stdin, bool noStart, Func<TextWriter> getStdout, Func<string, string?> getEnvironmentVariable)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            HookPayload payload = HookPayloadReader.Read(stdin);

            // A payload without an event name is not a hook worth an overlay; nothing is sent or started for it.
            if (payload.HookEventName is not null)
            {
                PulsePaths paths = PulsePaths.FromEnvironment(getEnvironmentVariable);
                Deliver(HookMessage.From(payload, startedAtUtc), PulseChannel.For(paths), noStart, paths);
            }
        }
        catch (Exception)
        {
            // A closed or broken stdin, or anything unexpected, must not fail Codex or raise a WER dialog.
            // Nothing from stdin is logged.
        }

        if (getEnvironmentVariable(DebugAssembliesVariable) == "1")
        {
            TextWriter stdout = getStdout();

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                stdout.WriteLine(assembly.GetName().Name);
            }
        }

        return 0;
    }

    /// <summary>Sends the message to the running overlay, or starts the overlay first.</summary>
    /// <param name="message">The message.</param>
    /// <param name="channel">The overlay's mutex and pipe.</param>
    /// <param name="noStart">Whether starting the overlay is forbidden.</param>
    /// <param name="paths">Where the hook log goes.</param>
    /// <returns>What happened.</returns>
    internal static HookOutcome Deliver(HookMessage message, PulseChannel channel, bool noStart, PulsePaths paths)
    {
        byte[] bytes = message.ToBytes();

        if (channel.IsOverlayRunning())
        {
            return HookPipeClient.TrySend(channel.PipeName, bytes, HookPipeClient.ConnectTimeoutMs)
                ? HookOutcome.Delivered
                : Log(paths, HookOutcome.NotDelivered, $"{message.Event}: the running overlay's pipe did not take the message");
        }

        if (noStart)
        {
            return HookOutcome.NotRunning;
        }

        DetachedStart start = JobProcess.StartDetached(Environment.ProcessPath!, "--overlay", AppContext.BaseDirectory);

        if (start.Error != 0)
        {
            return Log(paths, HookOutcome.StartFailed, $"{message.Event}: the overlay did not start, Win32 error {start.Error}");
        }

        string where = start.BrokeAway ? "outside the hook's job" : "inside the hook's job (breakaway refused)";

        return HookPipeClient.TrySend(channel.PipeName, bytes, ColdStartConnectTimeoutMs)
            ? Log(paths, HookOutcome.StartedAndDelivered, $"{message.Event}: started the overlay, pid {start.ProcessId}, {where}")
            : Log(paths, HookOutcome.StartedNotDelivered,
                  $"{message.Event}: started the overlay, pid {start.ProcessId}, {where}; its pipe was not ready within {ColdStartConnectTimeoutMs} ms");
    }

    private static HookOutcome Log(PulsePaths paths, HookOutcome outcome, string line)
    {
        new RollingFileLog(paths.LogsDirectory, maxBytes: 256 * 1024, maxFiles: 2, baseName: "hook").Info(line);

        return outcome;
    }
}
