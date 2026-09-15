using System.Diagnostics;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class WorkerIdleTimeoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "planforge-idle-" + Guid.NewGuid().ToString("N"));

    public WorkerIdleTimeoutTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_silent_worker_is_killed_as_idle_and_fails_with_its_reason()
    {
        var run = RunDirectory.Create(_directory, "silent");
        var clock = Stopwatch.StartNew();

        VendorException error;
        using (RunLog.Use(run.Log))
        {
            error = await Assert.ThrowsAsync<VendorException>(
                () => CollectAsync(Silent(), TimeSpan.FromMilliseconds(300)));
        }

        Assert.Contains("no stdout", error.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the idle worker ran for {clock.Elapsed}");
        Assert.Equal("idle", Field(Single(Read(run), "process.kill"), "reason"));
    }

    [Fact]
    public async Task Every_stdout_line_resets_the_worker_idle_timeout_and_records_activity()
    {
        var activity = new WorkerActivity();

        IReadOnlyList<string> lines;
        using (WorkerActivity.Use(activity))
        {
            lines = await CollectAsync(Speaking(), TimeSpan.FromSeconds(3));
        }

        Assert.Equal(["first", "second", "third"], lines);
        Assert.NotNull(activity.Snapshot().LastActivityAt);
        Assert.Null(activity.Snapshot().LastEvent);
    }

    [Fact]
    public void A_recognised_event_is_single_line_and_bounded()
    {
        var activity = new WorkerActivity();
        using (WorkerActivity.Use(activity))
        {
            WorkerActivity.RecordEvent("text:\n" + new string('x', 300));
        }

        var lastEvent = Assert.IsType<string>(activity.Snapshot().LastEvent);
        Assert.Equal(200, lastEvent.Length);
        Assert.DoesNotContain('\n', lastEvent);
    }

    private async Task<IReadOnlyList<string>> CollectAsync(ProcessSpec spec, TimeSpan idleTimeout)
    {
        var lines = new List<string>();
        await foreach (var line in StreamingProcess.RunWorkerAsync(spec, idleTimeout, CancellationToken.None))
        {
            lines.Add(line);
        }
        return lines;
    }

    private ProcessSpec Silent() => OperatingSystem.IsWindows()
        ? new ProcessSpec("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            _directory, string.Empty)
        : new ProcessSpec("/bin/sh", ["-c", "sleep 30"], _directory, string.Empty);

    private ProcessSpec Speaking() => OperatingSystem.IsWindows()
        ? new ProcessSpec("powershell.exe",
            ["-NoProfile", "-Command", "Write-Output first; Start-Sleep -Seconds 2; Write-Output second; Start-Sleep -Seconds 2; Write-Output third"],
            _directory, string.Empty)
        : new ProcessSpec("/bin/sh", ["-c", "echo first; sleep 2; echo second; sleep 2; echo third"],
            _directory, string.Empty);

    private static IReadOnlyList<JsonElement> Read(RunDirectory run) =>
        AtomicFile.Read(run.DiagnosticLogPath)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static JsonElement Single(IEnumerable<JsonElement> entries, string name) =>
        entries.Single(entry => entry.GetProperty("event").GetString() == name);

    private static string Field(JsonElement entry, string name) =>
        entry.GetProperty("fields").GetProperty(name).GetString()!;
}
