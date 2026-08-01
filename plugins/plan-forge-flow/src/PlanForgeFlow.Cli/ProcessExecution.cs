using System.Diagnostics;
using System.Text;

namespace PlanForgeFlow;

internal static class ProcessExecution
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaxProcessDuration = TimeSpan.FromSeconds(30);

    private sealed class OutputLimitException(string streamName) : Exception($"{streamName} exceeded {MaxOutputBytes} bytes")
    {
        public string StreamName { get; } = streamName;
    }

    public static ProcessResult Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName) };
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        if (!string.IsNullOrWhiteSpace(workingDirectory)) process.StartInfo.WorkingDirectory = workingDirectory;
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"could not start {fileName}");
        }
        catch (Exception error)
        {
            throw new CliFailure("environment", $"could not start {fileName}: {error.Message}");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, "stdout");
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, "stderr");
        var readers = Task.WhenAll(stdoutTask, stderrTask);
        var deadline = Stopwatch.GetTimestamp() + (long)(MaxProcessDuration.TotalSeconds * Stopwatch.Frequency);
        while (!readers.IsCompleted)
        {
            if (stdoutTask.IsFaulted || stderrTask.IsFaulted)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
                try { readers.GetAwaiter().GetResult(); } catch { }
                var limit = new[] { stdoutTask, stderrTask }
                           .Where(task => task.IsFaulted)
                           .Select(task => task.Exception?.GetBaseException())
                           .OfType<OutputLimitException>()
                           .FirstOrDefault();
                throw new CliFailure("environment", limit is null ? $"process {fileName} output failed" : $"process {fileName} {limit.StreamName} exceeded {MaxOutputBytes} bytes");
            }

            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
                try { readers.GetAwaiter().GetResult(); } catch { }
                throw new CliFailure("environment", $"process {fileName} exceeded {MaxProcessDuration.TotalSeconds:0} second limit");
            }

            try
            {
                readers.Wait((int)Math.Min(100, Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency)));
            }
            catch (AggregateException)
            {
                // Observe and handle the fault on the next loop iteration, where the process tree is terminated.
            }
        }

        if (stdoutTask.IsFaulted || stderrTask.IsFaulted)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
            var limit = new[] { stdoutTask, stderrTask }
                       .Where(task => task.IsFaulted)
                       .Select(task => task.Exception?.GetBaseException())
                       .OfType<OutputLimitException>()
                       .FirstOrDefault();
            try { readers.GetAwaiter().GetResult(); } catch { }
            throw new CliFailure("environment", limit is null ? $"process {fileName} output failed" : $"process {fileName} {limit.StreamName} exceeded {MaxOutputBytes} bytes");
        }

        var remainingAfterReaders = deadline - Stopwatch.GetTimestamp();
        if (remainingAfterReaders <= 0 || !process.WaitForExit((int)Math.Min(int.MaxValue, Math.Max(1, remainingAfterReaders * 1000 / Stopwatch.Frequency))))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
            try { readers.GetAwaiter().GetResult(); } catch { }
            throw new CliFailure("environment", $"process {fileName} exceeded {MaxProcessDuration.TotalSeconds:0} second limit");
        }
        try
        {
            readers.GetAwaiter().GetResult();
        }
        catch (OutputLimitException limit)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
            throw new CliFailure("environment", $"process {fileName} {limit.StreamName} exceeded {MaxOutputBytes} bytes");
        }
        catch (Exception error)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(5000); } catch { }
            throw new CliFailure("environment", $"process {fileName} output failed: {error.Message}");
        }

        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, string streamName)
    {
        var bytes = new MemoryStream(16 * 1024);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaxOutputBytes) throw new OutputLimitException(streamName);
            bytes.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    }
}
