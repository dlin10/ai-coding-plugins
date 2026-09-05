using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// Runs one gate command on the host, in PowerShell, from the workspace root, with the environment
/// <c>forge.begin</c> was given. The exit code is the verdict.
/// </summary>
/// <remarks>
/// <para>
/// PowerShell rather than <c>cmd.exe</c> because a gate that proves a test <em>exists</em> has to
/// count lines (<c>(dotnet test --list-tests … | Select-String …).Count</c>), and because the codex
/// builder ran its own verification in PowerShell, so a gate written for it is a gate the builder
/// could have run. The command travels as <c>-EncodedCommand</c>, which is the one way onto a
/// PowerShell command line that no quote, dollar sign or newline can break.
/// </para>
/// <para>
/// The script around the command makes the exit code mean what a gate needs it to mean. A cmdlet
/// error, or on PowerShell 7.4+ a native command exiting non-zero, terminates the script, and the
/// trap turns that into the native exit code where there is one and 1 otherwise; a script that ran
/// to its end exits with its last native exit code, or 0 when it ran only PowerShell. Without that,
/// a two-line gate whose first line failed would report the second line's success, and a
/// <c>$LASTEXITCODE</c> nobody set would decide a gate that never ran a native command.
/// </para>
/// </remarks>
internal static class GateRunner
{
    private const int OutputTailLength = 4000;

    /// <summary>The same bound a builder turn gets: a gate is a test suite at most, not a build farm.</summary>
    private static readonly TimeSpan DEFAULT_TIMEOUT = TimeSpan.FromMinutes(20);

    private const string Preamble =
        "$ErrorActionPreference = 'Stop'\n"
        + "$PSNativeCommandUseErrorActionPreference = $true\n"
        + "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n"
        // The trap writes the error itself: a trap that exits swallows the record it caught, and the
        // reason a gate failed is exactly what the builder is about to be shown.
        + "trap { [Console]::Error.WriteLine($_.ToString()); if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }\n";

    private const string Epilogue = "if ($LASTEXITCODE) { exit $LASTEXITCODE }\nexit 0\n";

    public static Task<GateRun> RunAsync(GateCommand gate,
                                         string workspaceRoot,
                                         IReadOnlyDictionary<string, string>? environment,
                                         CancellationToken ct) =>
        RunAsync(gate, workspaceRoot, environment, DEFAULT_TIMEOUT, ct);

    internal static async Task<GateRun> RunAsync(GateCommand gate,
                                                 string workspaceRoot,
                                                 IReadOnlyDictionary<string, string>? environment,
                                                 TimeSpan timeout,
                                                 CancellationToken ct)
    {
        var shell = Shell();
        if (shell is null)
        {
            RunLog.Current?.Write("warn", "gate", "gate.no-shell", ("label", gate.Label), ("command", gate.Command));
            return new GateRun("not_run", gate.Label, gate.Command, null, null, null,
                               "no PowerShell was found on PATH to run the gate");
        }

        var spec = new ProcessSpec(shell,
                                   ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(gate.Command)],
                                   workspaceRoot,
                                   string.Empty,
                                   environment);

        RunLog.Current?.Write("info", "gate", "gate.start",
            ("label", gate.Label), ("command", gate.Command), ("cwd", workspaceRoot),
            ("environment", environment is { Count: > 0 } ? string.Join(", ", environment.Keys) : null));

        var output = new StringBuilder();
        var watch = Stopwatch.StartNew();
        GateRun run;
        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, timeout, ct).ConfigureAwait(false))
                output.AppendLine(line);

            run = new GateRun("passed", gate.Label, gate.Command, 0, Tail(output), Elapsed(watch), null);
        }
        catch (VendorException error) when (error.ExitCode is { } code)
        {
            // The message carries the stderr tail after the exit code, which is where a failing
            // gate usually says why.
            output.AppendLine(error.Message);
            run = new GateRun("failed", gate.Label, gate.Command, code, Tail(output), Elapsed(watch), null);
        }
        catch (VendorException error)
        {
            // The output cap: no exit code to report, but the failure is the gate's.
            output.AppendLine(error.Message);
            run = new GateRun("failed", gate.Label, gate.Command, null, Tail(output), Elapsed(watch), error.Message);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The shell itself would not start, which says nothing about the gate: the builder's
            // word stands, and the log says why the host could not check it.
            run = new GateRun("not_run", gate.Label, gate.Command, null, null, Elapsed(watch),
                              $"the host could not start {shell}: {error.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            run = new GateRun("timeout", gate.Label, gate.Command, null, Tail(output), Elapsed(watch),
                              $"the gate did not finish within {timeout.TotalSeconds:0} s and was killed");
        }

        RunLog.Current?.Write(run.Outcome == "passed" ? "info" : "warn", "gate", "gate.finished",
            ("label", run.Label), ("outcome", run.Outcome), ("exitCode", run.ExitCode?.ToString()),
            ("seconds", run.Seconds?.ToString("0.0")), ("output", run.Output));

        return run;
    }

    /// <summary>
    /// The command wrapped so its exit code is the gate's answer, then UTF-16LE base64 as
    /// <c>-EncodedCommand</c> wants it. Internal so a test can pin the wrapping.
    /// </summary>
    private static string Encode(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Preamble + command + "\n" + Epilogue));

    /// <summary>
    /// pwsh first, Windows PowerShell second. The Store's execution alias for pwsh — the zero-byte
    /// stub docs/adr/0013 strips from the codex PATH — is deliberately <em>not</em> skipped here: it
    /// refuses codex's restricted sandbox token, but this server runs as the user and the alias
    /// launches the real pwsh for it, and on a Store install it is the only pwsh on PATH at all.
    /// </summary>
    private static string? Shell() =>
        ExecutableResolver.Resolve("pwsh") ?? ExecutableResolver.Resolve("powershell");

    private static string? Tail(StringBuilder output)
    {
        var text = output.ToString().TrimEnd();
        if (text.Length == 0) return null;
        return text.Length <= OutputTailLength ? text : "… [truncated]" + text[^OutputTailLength..];
    }

    private static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalSeconds, 1);
}
