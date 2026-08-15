using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    public static async IAsyncEnumerable<string> RunAsync(ProcessSpec spec,
                                                          TimeSpan timeout,
                                                          [EnumeratorCancellation] CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;

        using var process = new Process();
        process.StartInfo = Build(spec);
        if (!process.Start()) throw new VendorException($"could not start {spec.FileName}");

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
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new VendorException($"{spec.FileName} exited {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
    }

    public static async Task<IReadOnlyList<string>> CollectAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        var lines = new List<string>();
        await foreach (var line in RunAsync(spec, timeout, ct).ConfigureAwait(false)) lines.Add(line);
        return lines;
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
