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
/// could have run. The command travels as a temporary <c>.ps1</c> run with <c>-File</c>: none of it
/// reaches a command line, so no quote, dollar sign or newline can break it — and no length can
/// either. <c>-EncodedCommand</c>, which this was, could not say the same. Base64 of UTF-16 runs
/// about 2.7× the script, so a gate past roughly 12,000 characters overran the 32,767-character
/// Windows command line and the shell would not start at all: run <c>20260915-143837-8bc4d1</c>
/// lost a 14,824-character gate that way, reported as <c>not_run</c> — which leaves the builder's
/// own word standing, so the task counted unchecked, which is the one thing docs/adr/0015 exists to
/// prevent. The file is UTF-8 <em>with</em> a BOM, because Windows PowerShell 5.1 reads a BOM-less
/// script as the system codepage and a gate naming a non-ASCII path or test is what that mangles.
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

    /// <summary>With the BOM, which is the half of this that matters: see the remarks on the class.</summary>
    private static readonly UTF8Encoding _scriptEncoding = new(encoderShouldEmitUTF8Identifier: true);

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

        string script;
        try
        {
            script = WriteScript(gate.Command);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RunLog.Current?.Write("warn", "gate", "gate.no-script", ("label", gate.Label), ("error", error.Message));
            return new GateRun("not_run", gate.Label, gate.Command, null, null, null,
                               $"the host could not write the gate script: {error.Message}");
        }

        var spec = new ProcessSpec(shell,
                                   ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
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
        finally
        {
            // PowerShell reads the script in full before running it, so this lands even on the
            // timeout path, where the shell was killed a moment ago and may still be exiting.
            try { File.Delete(script); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        RunLog.Current?.Write(run.Outcome == "passed" ? "info" : "warn", "gate", "gate.finished",
            ("label", run.Label), ("outcome", run.Outcome), ("exitCode", run.ExitCode?.ToString()),
            ("seconds", run.Seconds?.ToString("0.0")), ("output", run.Output));

        return run;
    }

    /// <summary>
    /// The command wrapped so its exit code is the gate's answer, written where <c>-File</c> can
    /// read it. The name is the run's, not the gate's: a label reaches this from a plan, and a
    /// file name is no place to find out what a plan may contain.
    /// </summary>
    private static string WriteScript(string command)
    {
        var path = Path.Combine(Path.GetTempPath(), $"planforge-gate-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, Preamble + command + "\n" + Epilogue, _scriptEncoding);
        return path;
    }

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
