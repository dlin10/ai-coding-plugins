using System.Diagnostics;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The window between a process exiting and its pipe being read out. CI run 33916817192 lost a line
/// in it: `git rev-parse HEAD` exited 0, its only line never arrived within the two seconds the
/// drain then allowed, and `Baseline.CaptureAsync` recorded an empty head as an answer. The drain
/// stays bounded — an inherited handle can hold the pipe for the rest of a run — but a truncation
/// no longer passes for a complete stream in silence.
/// </summary>
public sealed class ProcessDrainTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public ProcessDrainTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// What the bound is for: the process is gone, a child it left behind still owns the pipe, and
    /// waiting for EOF would mean waiting for that child. The drain is deliberately far shorter
    /// here than the hold, so the reader must be what ends the stream.
    /// </summary>
    [Fact]
    public async Task A_pipe_a_child_holds_open_does_not_hold_the_reader()
    {
        var elapsed = Stopwatch.StartNew();

        var lines = await StreamingProcess.CollectAsync(HoldingThePipe(), TimeSpan.FromMinutes(1),
                                                        CancellationToken.None,
                                                        exitDrain: TimeSpan.FromMilliseconds(400));

        Assert.Empty(lines);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20),
                    $"the reader waited {elapsed.Elapsed} on a child that holds the pipe for 6 seconds");
    }

    /// <summary>
    /// The half that was missing. An expired drain drops whatever had not been read and hands back
    /// a stream indistinguishable from a complete one, so the run log is the only place that can
    /// say it happened — and it is what the next empty answer will be diagnosed from.
    /// </summary>
    [Fact]
    public async Task An_expired_drain_says_so_in_the_run_log()
    {
        var log = new RunLog(Path.Combine(_directory, "forge.log"));

        using (RunLog.Use(log))
        {
            await StreamingProcess.CollectAsync(HoldingThePipe(), TimeSpan.FromMinutes(1),
                                                CancellationToken.None,
                                                exitDrain: TimeSpan.FromMilliseconds(400));
        }

        var entry = Entries(Path.Combine(_directory, "forge.log"))
            .Single(line => line.GetProperty("event").GetString() == "process.drain.timeout");
        Assert.Equal("warn", entry.GetProperty("level").GetString());
        Assert.Contains(Executable, entry.GetProperty("fields").GetProperty("exec").GetString()!,
                        StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<JsonElement> Entries(string path) =>
        [.. AtomicFile.Read(path)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)];

    private static string Executable => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /// <summary>
    /// A process that writes nothing, leaves a child holding its stdout for six seconds, and exits
    /// at once — the shape of a vendor whose spawned server outlives it.
    /// </summary>
    private ProcessSpec HoldingThePipe() =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "start", "/b", "cmd.exe", "/c", "ping -n 7 127.0.0.1 >nul"],
                              _directory, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", "sleep 6 &"], _directory, string.Empty);
}
