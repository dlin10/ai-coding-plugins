using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using PlanForge.Vendors.Cursor;
using Xunit;

namespace PlanForge.Tests;

public sealed class VendorTimingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-timing", Guid.NewGuid().ToString("n"));
    private readonly TestClock _clock = new();

    public VendorTimingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Long_commands_are_subtracted_from_the_process_wall_time()
    {
        var attempt = Turn().Start(1, null, _clock);
        attempt.ProcessStarted();
        _clock.Advance(19);
        attempt.ToolStarted("gate");
        _clock.Advance(940);
        attempt.ToolCompleted("gate");
        _clock.Advance(458);
        attempt.ProcessExited();
        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(), null);

        AssertDurations(Record(), "00:23:37", "00:15:40", "00:07:57");
    }

    [Theory]
    [InlineData("claude", "Bash")]
    [InlineData("codex", "command_execution")]
    [InlineData("codex", "mcp_tool_call")]
    [InlineData("cursor", "shell")]
    public void Vendor_tool_pairs_are_matched_by_id_and_parallel_intervals_are_counted_once(string vendor, string tool)
    {
        var role = Role();
        var selection = new Selection("model", null);
        var attempt = new VendorTurn(role, selection, vendor).Start(1, null, _clock);
        var newSessionClaude = new ClaudeCliSession(role, selection, null);
        var newSessionCodex = new CodexCliSession(role, selection, null);
        var newSessionCursor = new CursorAgentSession(role, selection, null);
        Action<JsonElement> observe = vendor switch
        {
            "claude" => root => newSessionClaude.Observe(root, attempt),
            "codex" => root => newSessionCodex.Observe(root, attempt),
            _ => root => newSessionCursor.Observe(root, attempt)
        };

        attempt.ProcessStarted();
        _clock.Advance(2);
        Send(observe, ToolEvent(vendor, tool, "a", true));
        _clock.Advance(1);
        Send(observe, ToolEvent(vendor, tool, "b", true));
        _clock.Advance(3);
        Send(observe, ToolEvent(vendor, tool, "b", false));
        _clock.Advance(2);
        Send(observe, ToolEvent(vendor, tool, "a", false));
        _clock.Advance(2);
        Send(observe, ToolEvent(vendor, tool, "c", true));
        _clock.Advance(1);
        Send(observe, ToolEvent(vendor, tool, "c", false));
        _clock.Advance(1);
        attempt.ProcessExited();
        attempt.Finish(new WorkerUsage(), null);

        AssertDurations(Record(), "00:00:12", "00:00:07", "00:00:05");
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void A_non_string_call_id_leaves_tool_time_unknown_without_losing_the_turn(string vendor)
    {
        var attempt = Turn().Start(1, null, _clock);
        var role = Role();
        var selection = new Selection("model", null);
        var claude = new ClaudeCliSession(role, selection, null);
        var codex = new CodexCliSession(role, selection, null);
        var cursor = new CursorAgentSession(role, selection, null);
        Action<JsonElement> observe = vendor switch
        {
            "claude" => root => claude.Observe(root, attempt),
            "codex" => root => codex.Observe(root, attempt),
            _ => root => cursor.Observe(root, attempt)
        };
        var tool = vendor switch { "claude" => "Bash", "codex" => "command_execution", _ => "shell" };
        attempt.ProcessStarted();
        Send(observe, ToolEvent(vendor, tool, "bad", true).Replace("\"bad\"", "42", StringComparison.Ordinal));
        _clock.Advance(10);
        Send(observe, ToolEvent(vendor, tool, "bad", false).Replace("\"bad\"", "42", StringComparison.Ordinal));
        attempt.ProcessExited();
        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(), null);

        var record = Record();
        Assert.Equal(JsonValueKind.Null, record.GetProperty("toolDuration").ValueKind);
        Assert.Equal("00:00:10", record.GetProperty("duration").GetString());
        Assert.Equal("succeeded", record.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("open")]
    [InlineData("orphan")]
    [InlineData("missing_id")]
    [InlineData("duplicate")]
    public void Unmeasurable_tools_leave_duration_equal_to_wall_time(string stream)
    {
        var attempt = Turn().Start(1, null, _clock);
        attempt.ProcessStarted();
        if (stream is not "none")
        {
            attempt.ToolStarted("complete");
            _clock.Advance(2);
            attempt.ToolCompleted("complete");
            switch (stream)
            {
                case "open": attempt.ToolStarted("unfinished"); break;
                case "orphan": attempt.ToolCompleted("unknown"); break;
                case "missing_id": attempt.ToolStarted(null); attempt.ToolCompleted(null); break;
                case "duplicate": attempt.ToolStarted("same"); attempt.ToolStarted("same"); attempt.ToolCompleted("same"); break;
            }
        }

        _clock.Advance(10);
        attempt.ProcessExited();
        attempt.Cancelled();
        attempt.Finish(new WorkerUsage(), null);

        var record = Record();
        Assert.Equal(JsonValueKind.Null, record.GetProperty("toolDuration").ValueKind);
        Assert.Equal(record.GetProperty("wallDuration").GetString(), record.GetProperty("duration").GetString());
    }

    [Fact]
    public void Each_retry_or_resumed_attempt_keeps_its_local_send_time_and_excludes_post_exit_work()
    {
        var turn = Turn();
        var first = turn.Start(1, null, _clock);
        _clock.Advance(30); // Work before actually launching the vendor.
        first.ProcessStarted();
        _clock.Advance(10);
        first.ProcessExited();
        _clock.Advance(20); // Deserializing and assessing output is outside the process lifetime.
        first.InvalidOutput();
        first.Finish(new WorkerUsage(), "session");

        var second = turn.Start(1, "session", _clock);
        second.ProcessStarted();
        _clock.Advance(4);
        second.ProcessExited();
        second.Succeeded();
        second.Finish(new WorkerUsage(), "session");

        using var document = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        var records = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal("2026-10-02 10:04:54", records[0].GetProperty("at").GetString());
        Assert.Equal("2026-10-02 10:05:24", records[1].GetProperty("at").GetString());
        Assert.Equal("00:00:10", records[0].GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:04", records[1].GetProperty("wallDuration").GetString());
        Assert.DoesNotContain("\\u", File.ReadAllText(TelemetryPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Buffered_tool_results_are_clipped_to_the_process_lifetime()
    {
        var attempt = Turn().Start(1, null, _clock);
        attempt.ProcessStarted();
        _clock.Advance(2);
        attempt.ToolStarted("tool");
        _clock.Advance(8);
        attempt.ProcessExited();
        _clock.Advance(5);
        attempt.ToolCompleted("tool");
        attempt.Finish(new WorkerUsage(), null);

        AssertDurations(Record(), "00:00:10", "00:00:08", "00:00:02");
    }

    [Fact]
    public void Displayed_seconds_preserve_the_subtraction()
    {
        var attempt = Turn().Start(1, null, _clock);
        attempt.ProcessStarted();
        _clock.Advance(0.1);
        attempt.ToolStarted("tool");
        _clock.Advance(1.9);
        attempt.ToolCompleted("tool");
        _clock.Advance(0.1);
        attempt.ProcessExited();
        attempt.Finish(new WorkerUsage(), null);

        AssertDurations(Record(), "00:00:02", "00:00:01", "00:00:01");
    }

    [Fact]
    public void Claude_background_notifications_and_structured_output_do_not_extend_tool_time()
    {
        var role = Role();
        var session = new ClaudeCliSession(role, new Selection("model", null), null);
        var attempt = Turn().Start(1, null, _clock);
        Action<JsonElement> observe = root => session.Observe(root, attempt);
        attempt.ProcessStarted();
        Send(observe, ToolEvent("claude", "Bash", "bg", true));
        _clock.Advance(1);
        Send(observe, ToolEvent("claude", "Bash", "bg", false));
        Send(observe, """{"type":"system","subtype":"task_started","task_id":"task","tool_use_id":"bg"}""");
        _clock.Advance(9);
        Send(observe, """{"type":"system","subtype":"task_notification","task_id":"task","status":"completed"}""");
        Send(observe, """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"StructuredOutput","id":"output","input":{}}]}}""");
        Send(observe, ToolEvent("claude", "StructuredOutput", "output", false));
        attempt.ProcessExited();
        attempt.Finish(new WorkerUsage(), null);

        AssertDurations(Record(), "00:00:10", "00:00:01", "00:00:09");
    }

    [Fact]
    public async Task A_real_worker_process_measures_a_slow_tool_and_stops_timing_before_output_assessment()
    {
        var attempt = Turn().Start(1, null);
        var session = new CodexCliSession(Role(), new Selection("model", null), null);
        var start = ToolEvent("codex", "command_execution", "slow", true);
        var end = ToolEvent("codex", "command_execution", "slow", false);
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("powershell.exe", ["-NoProfile", "-Command", $"Write-Output '{start}'; Start-Sleep -Milliseconds 2200; Write-Output '{end}'"], null, "")
            : new ProcessSpec("/bin/sh", ["-c", $"echo '{start}'; sleep 2.2; echo '{end}'"], null, "");
        var beforeStart = DateTimeOffset.Now;
        DateTimeOffset? firstOutputAt = null;
        var elapsed = Stopwatch.StartNew();
        await foreach (var line in StreamingProcess.RunWorkerAsync(spec, CancellationToken.None, attempt))
        {
            firstOutputAt ??= DateTimeOffset.Now;
            Send(root => session.Observe(root, attempt), line);
        }
        await Task.Delay(2100);
        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(), null);

        var record = Record();
        var wall = TimeSpan.Parse(record.GetProperty("wallDuration").GetString()!, CultureInfo.InvariantCulture);
        var tools = TimeSpan.Parse(record.GetProperty("toolDuration").GetString()!, CultureInfo.InvariantCulture);
        var net = TimeSpan.Parse(record.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(tools >= TimeSpan.FromSeconds(2));
        Assert.Equal(wall - tools, net);
        Assert.True(wall < elapsed.Elapsed - TimeSpan.FromSeconds(1.5));
        Assert.InRange(record.GetProperty("at").GetString()!,
                       VendorAttempt.FormatLocalTimestamp(beforeStart), VendorAttempt.FormatLocalTimestamp(firstOutputAt!.Value));
    }

    private RoleSpec Role() => new(VendorRole.Builder, "role", Telemetry:
        new WorkerTelemetryContext(TelemetryPath, new RunLog(Path.Combine(_root, "forge.log")), "build"));

    private VendorTurn Turn() => new(Role(), new Selection("model", null), "codex");

    private string TelemetryPath => Path.Combine(_root, "telemetry.json");

    private JsonElement Record()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        return Assert.Single(document.RootElement.EnumerateArray()).Clone();
    }

    private static void AssertDurations(JsonElement record, string wall, string tools, string net)
    {
        Assert.Equal(wall, record.GetProperty("wallDuration").GetString());
        Assert.Equal(tools, record.GetProperty("toolDuration").GetString());
        Assert.Equal(net, record.GetProperty("duration").GetString());
    }

    private static void Send(Action<JsonElement> observe, string json)
    {
        using var document = JsonDocument.Parse(json);
        observe(document.RootElement);
    }

    private static string ToolEvent(string vendor, string tool, string id, bool started)
    {
        var template = vendor switch
        {
            "claude" => started
                ? """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"TOOL","id":"ID","input":{}}]}}"""
                : """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"ID","content":"done"}]}}""",
            "codex" => """{"type":"item.STATE","item":{"id":"ID","type":"TOOL","server":"mcp","tool":"read"}}""",
            _ => """{"type":"tool_call","subtype":"STATE","tool_call":{"toolCallId":"ID","TOOLToolCall":{"args":{},"result":{"success":{}}}}}"""
        };
        return template.Replace("TOOL", tool, StringComparison.Ordinal)
                       .Replace("ID", id, StringComparison.Ordinal)
                       .Replace("STATE", started ? "started" : "completed", StringComparison.Ordinal);
    }

    private sealed class TestClock : TimeProvider
    {
        private static readonly DateTimeOffset Initial = new(2026, 10, 2, 7, 4, 24, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(3), "test", "test");
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Initial.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
