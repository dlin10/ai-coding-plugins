using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Run;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Cursor;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Issue #41: a stream-json line that parsed as JSON but not as an object crashed the vendor
/// session, and the kill that followed was logged as an output-cap breach that never happened —
/// the reason used to be inferred by elimination, so any consumer fault wore the cap's name.
/// These pin both halves: non-object lines are skipped and logged, and the kill reason tells a
/// cap breach apart from a consumer that stopped reading.
/// </summary>
public sealed class OutputCapMislabelTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public OutputCapMislabelTests()
    {
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Claude_skips_a_line_that_is_json_but_not_an_object()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-claude");
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using var line = JsonDocument.Parse("\"planning\"");
        JsonElement? structured;
        using (RunLog.Use(run.Log))
        {
            structured = session.Observe(line.RootElement);
        }

        Assert.Null(structured);
        var skipped = Single(Read(run), "vendor.skipped-line");
        Assert.Equal("claude", skipped.GetProperty("source").GetString());
        Assert.Contains("planning", Field(skipped, "payload"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_skips_a_line_that_is_json_but_not_an_object_and_still_reads_the_result()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-cursor");
        var lines = Path.Combine(_repo, "lines.jsonl");
        await File.WriteAllLinesAsync(lines,
        [
            "\"planning\"",
            """{"type":"result","result":"the critique","session_id":"chat-1"}"""
        ]);

        var session = new CursorAgentSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        string result;
        using (RunLog.Use(run.Log))
        {
            result = await session.ReadResultAsync(Replaying(lines), CancellationToken.None);
        }

        Assert.Equal("the critique", result);
        var skipped = Single(Read(run), "vendor.skipped-line");
        Assert.Equal("cursor", skipped.GetProperty("source").GetString());
        Assert.Contains("planning", Field(skipped, "payload"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_consumer_fault_is_logged_as_abandoned_rather_than_output_cap()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-abandoned");
        var spec = Replaying(await FloodAsync());

        using (RunLog.Use(run.Log))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in StreamingProcess.RunAsync(spec, TimeSpan.FromMinutes(1), CancellationToken.None))
                    throw new InvalidOperationException("consumer fault");
            });
        }

        Assert.Equal("abandoned", Field(Single(Read(run), "process.kill"), "reason"));
    }

    [Fact]
    public async Task A_genuine_cap_breach_is_still_logged_as_output_cap()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-capped");
        var spec = Replaying(await FloodAsync());

        VendorException error;
        using (RunLog.Use(run.Log))
        {
            error = await Assert.ThrowsAsync<VendorException>(
                () => StreamingProcess.CollectAsync(spec, TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        Assert.Contains("exceeded", error.Message, StringComparison.Ordinal);
        Assert.Equal("output-cap", Field(Single(Read(run), "process.kill"), "reason"));
    }

    /// <summary>
    /// Replays a file to stdout verbatim. Piping a file through type/cat sidesteps the shell
    /// quoting that inline JSON would need.
    /// </summary>
    private ProcessSpec Replaying(string path)
    {
        return OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "type", path], _repo, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"cat '{path}'"], _repo, string.Empty);
    }

    /// <summary>
    /// Twice the 8MB cap, so the replaying process is still blocked on the pipe when the reader
    /// stops — a smaller file could drain into the reader's buffers and exit before the kill,
    /// and an exited process leaves no kill to assert on.
    /// </summary>
    private async Task<string> FloodAsync()
    {
        var path = Path.Combine(_repo, "flood.jsonl");
        var line = new string('x', 1024 * 1024);
        await File.WriteAllLinesAsync(path, Enumerable.Repeat(line, 16));
        return path;
    }

    private static IReadOnlyList<JsonElement> Read(RunDirectory run) =>
        File.ReadAllLines(run.DiagnosticLogPath)
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static JsonElement Single(IEnumerable<JsonElement> entries, string name) =>
        entries.Single(entry => entry.GetProperty("event").GetString() == name);

    private static string Field(JsonElement entry, string name) =>
        entry.GetProperty("fields").GetProperty(name).GetString()!;
}
