using System.Diagnostics;
using System.Runtime.CompilerServices;
using PlanForge.Diagnostics;
using PlanForge.Vendors;

namespace PlanForge.Infrastructure;

internal sealed record ProcessSpec(string FileName,
                                   IReadOnlyList<string> Arguments,
                                   string? WorkingDirectory,
                                   string StandardInput);

/// <summary>
/// Bounded runner for vendor processes: output cap, timeout, kill-tree. Unlike the old
/// ProcessExecution it hands back stdout line by line, because vendors emit JSONL as they work.
/// </summary>
internal static class StreamingProcess
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;
    private const string Source = "process";

    public static async IAsyncEnumerable<string> RunAsync(ProcessSpec spec,
                                                          TimeSpan timeout,
                                                          [EnumeratorCancellation] CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;

        using var process = new Process();
        process.StartInfo = Build(spec);

        // The launch record is the single most useful line in the log: an argument list nobody can
        // see is how a model id the vendor rejects reads as an unexplained timeout.
        var log = RunLog.Current;
        log?.Write("info", Source, "process.start",
            ("exec", spec.FileName),
            ("args", string.Join(' ', spec.Arguments)),
            ("cwd", spec.WorkingDirectory),
            ("timeout", timeout.ToString()));

        if (!process.Start())
        {
            log?.Write("error", Source, "process.start.failed", ("exec", spec.FileName));
            throw new VendorException($"could not start {spec.FileName}");
        }

        log?.Write("info", Source, "process.started", ("exec", spec.FileName), ("pid", Pid(process)));

        var stderr = process.StandardError.ReadToEndAsync(token);

        await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), token).ConfigureAwait(false);
        process.StandardInput.Close();

        var seen = 0L;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                seen += line.Length;
                if (seen > MaxOutputBytes)
                    throw new VendorException($"{spec.FileName} exceeded {MaxOutputBytes} bytes of output");

                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                // Which cancellation fired decides how the failure reads: the caller walking away
                // is not the same event as the vendor outstaying its timeout.
                var reason = ct.IsCancellationRequested ? "cancelled"
                    : deadline.IsCancellationRequested ? "timeout" : "output-cap";

                // Kill before draining. On the output-cap path nothing has cancelled the stderr
                // read, so a live process would keep it open and the drain would wait forever for
                // the very process we came here to end.
                var pid = Pid(process);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }

                var killed = await DrainAsync(stderr).ConfigureAwait(false);
                log?.Write("warn", Source, "process.kill",
                    ("exec", spec.FileName),
                    ("pid", pid),
                    ("reason", reason),
                    ("stderrTail", killed.Length == 0 ? null : RunLog.Tail(killed)));
            }
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        var error = await DrainAsync(stderr).ConfigureAwait(false);
        log?.Write(process.ExitCode == 0 ? "info" : "error", Source, "process.exit",
            ("exec", spec.FileName),
            ("pid", Pid(process)),
            ("exitCode", process.ExitCode.ToString()),
            ("stderrTail", error.Length == 0 ? null : RunLog.Tail(error)));

        if (process.ExitCode != 0)
            throw new VendorException($"{spec.FileName} exited {process.ExitCode}: {error}", process.ExitCode);
    }

    public static async Task<IReadOnlyList<string>> CollectAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        var lines = new List<string>();
        await foreach (var line in RunAsync(spec, timeout, ct).ConfigureAwait(false)) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Reads stderr without letting the read decide the outcome. On a kill the stream is cancelled
    /// rather than closed, and the tail we wanted is the reason we were killing — losing it to the
    /// same cancellation would leave the log saying only that something stopped.
    /// </summary>
    private static async Task<string> DrainAsync(Task<string> stderr)
    {
        try
        {
            return await stderr.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            return string.Empty;
        }
    }

    // The process may already be gone by the time we ask, and an unusable pid is not worth a throw.
    private static string? Pid(Process process)
    {
        try { return process.Id.ToString(); } catch (InvalidOperationException) { return null; }
    }

    private static ProcessStartInfo Build(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory)) info.WorkingDirectory = spec.WorkingDirectory;
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
