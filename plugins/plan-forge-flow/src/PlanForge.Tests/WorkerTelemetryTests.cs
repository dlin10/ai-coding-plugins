using System.Text;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class WorkerTelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-telemetry", Guid.NewGuid().ToString("n"));

    public WorkerTelemetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Writes_an_indented_array_in_stable_human_order()
    {
        var context = Context("build", taskNumber: 2, taskCount: 5);
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);
        var turn = new VendorTurn(role, new Selection("gpt-5.3-codex", "high"), "codex");
        var measured = turn.Start(VendorTurn.PromptBytes("role", "привет", "schema"), "session-old");
        measured.Succeeded();
        measured.Finish(new WorkerUsage(4, 5, 6, 7, 3), "session-new");
        measured.Finish(new WorkerUsage(InputTokens: 999), "ignored");

        var json = File.ReadAllText(context.Path);
        Assert.Contains("  {", json, StringComparison.Ordinal);
        Assert.True(File.ReadAllLines(context.Path).Length > 2);
        Assert.True(json.IndexOf("\"at\"", StringComparison.Ordinal) < json.IndexOf("\"event\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"event\"", StringComparison.Ordinal) < json.IndexOf("\"act\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"taskCount\"", StringComparison.Ordinal) < json.IndexOf("\"vendor\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"promptBytes\"", StringComparison.Ordinal) < json.IndexOf("\"inputTokens\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        var record = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("vendor.usage", record.GetProperty("event").GetString());
        Assert.Equal("build", record.GetProperty("act").GetString());
        Assert.Equal(2, record.GetProperty("taskNumber").GetInt32());
        Assert.Equal(5, record.GetProperty("taskCount").GetInt32());
        Assert.Equal("resumed", record.GetProperty("sessionMode").GetString());
        Assert.Equal("session-new", record.GetProperty("sessionId").GetString());
        Assert.Equal(1, record.GetProperty("attempt").GetInt32());
        Assert.Equal("succeeded", record.GetProperty("outcome").GetString());
        Assert.Equal("00:00:00", record.GetProperty("duration").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount("roleприветschema"), record.GetProperty("promptBytes").GetInt64());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}$",
                       record.GetProperty("at").GetString()!);
        Assert.False(record.TryGetProperty("round", out _));
        Assert.False(record.TryGetProperty("costUsd", out _));
        Assert.False(record.TryGetProperty("malformedUsageFields", out _));
    }

    [Fact]
    public void Writes_the_reported_cost_after_the_token_counters()
    {
        var context = Context("build", taskNumber: 1, taskCount: 1);
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);
        var measured = new VendorTurn(role, new Selection("claude-opus-5-5", "high"), "claude").Start(1, null);
        measured.Succeeded();
        measured.Finish(new WorkerUsage(OutputTokens: 7, ReasoningTokens: 3, CostUsd: 0.4213875m), "claude-session");

        var json = File.ReadAllText(context.Path);
        Assert.True(json.IndexOf("\"reasoningTokens\"", StringComparison.Ordinal) < json.IndexOf("\"costUsd\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        var record = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(0.4213875m, record.GetProperty("costUsd").GetDecimal());
    }

    /// <summary>
    /// The requested speed is written for every attempt beside the effort; the speed served only
    /// where the vendor reported it — claude's <c>fast_mode_state</c>. See docs/adr/0023.
    /// </summary>
    [Fact]
    public void Records_the_requested_speed_for_every_attempt_and_the_served_one_where_reported()
    {
        var context = Context("build", taskNumber: 1, taskCount: 1);
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);

        var claude = new VendorTurn(role, new Selection("opus", "high", Fast: true), "claude").Start(1, null);
        claude.Succeeded();
        claude.Finish(new WorkerUsage(), "claude-session", servedFastState: "cooldown");
        var codex = new VendorTurn(role, new Selection("gpt-6-astra", "high"), "codex").Start(1, null);
        codex.Succeeded();
        codex.Finish(new WorkerUsage(), "codex-thread");

        var json = File.ReadAllText(context.Path);
        Assert.True(json.IndexOf("\"effort\"", StringComparison.Ordinal) < json.IndexOf("\"fast\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"fastModeState\"", StringComparison.Ordinal) < json.IndexOf("\"sessionMode\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        var records = document.RootElement.EnumerateArray().ToArray();
        Assert.True(records[0].GetProperty("fast").GetBoolean());
        Assert.Equal("cooldown", records[0].GetProperty("fastModeState").GetString());
        Assert.False(records[1].GetProperty("fast").GetBoolean());
        Assert.False(records[1].TryGetProperty("fastModeState", out _));
    }

    [Fact]
    public void Cursor_schema_retry_keeps_turn_identity_and_numbers_process_attempts()
    {
        var context = Context("plan_review", round: 1);
        var role = new RoleSpec(VendorRole.Critic, "role", Telemetry: context);
        var turn = new VendorTurn(role, new Selection("cursor-model", "default"), "cursor");

        var first = turn.Start(10, resumeToken: null);
        first.InvalidOutput();
        first.Finish(new WorkerUsage(), "chat-1");

        var second = turn.Start(14, "chat-1");
        second.Succeeded();
        second.Finish(new WorkerUsage(OutputTokens: 3), "chat-1");

        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        var records = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(records[0].GetProperty("turnId").GetString(), records[1].GetProperty("turnId").GetString());
        Assert.Equal(1, records[0].GetProperty("attempt").GetInt32());
        Assert.Equal(2, records[1].GetProperty("attempt").GetInt32());
        Assert.Equal("invalid_output", records[0].GetProperty("outcome").GetString());
        Assert.Equal("succeeded", records[1].GetProperty("outcome").GetString());
        Assert.Equal("fresh", records[0].GetProperty("sessionMode").GetString());
        Assert.Equal("resumed", records[1].GetProperty("sessionMode").GetString());
        Assert.Equal("default", records[1].GetProperty("effort").GetString());
    }

    [Fact]
    public void Cancellation_overrides_rejection_but_not_an_accepted_result()
    {
        var context = Context("code_review", round: 2);
        var role = new RoleSpec(VendorRole.Critic, "role", Telemetry: context);
        var turn = new VendorTurn(role, new Selection("model", null), "claude");

        var rejected = turn.Start(1, null);
        rejected.InvalidOutput();
        rejected.Cancelled();
        rejected.Finish(new WorkerUsage(), null);

        var accepted = turn.Start(1, null);
        accepted.Succeeded();
        accepted.Cancelled();
        accepted.Finish(new WorkerUsage(), null);

        var failed = turn.Start(1, null);
        failed.Finish(new WorkerUsage(), null);

        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        var records = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal("cancelled", records[0].GetProperty("outcome").GetString());
        Assert.Equal("succeeded", records[1].GetProperty("outcome").GetString());
        Assert.Equal("failed", records[2].GetProperty("outcome").GetString());
        Assert.All(records, record =>
        {
            Assert.False(record.TryGetProperty("effort", out _));
            Assert.False(record.TryGetProperty("runId", out _));
        });
    }

    [Fact]
    public void A_malformed_existing_file_is_preserved_and_the_failure_is_safe()
    {
        var context = Context("review_fix", round: 3);
        File.WriteAllText(context.Path, "{not an array}");
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);
        var measured = new VendorTurn(role, new Selection("model", null), "claude").Start(1, null);

        measured.Succeeded();
        measured.Finish(new WorkerUsage(InputTokens: 999), null);

        Assert.Equal("{not an array}", File.ReadAllText(context.Path));
        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(LogPath)));
        Assert.Equal("telemetry.write.failed", entry.RootElement.GetProperty("event").GetString());
        var fields = entry.RootElement.GetProperty("fields");
        Assert.Equal("1", fields.GetProperty("attempt").GetString());
        Assert.Equal("JsonException", fields.GetProperty("error").GetString());
        Assert.False(fields.TryGetProperty("inputTokens", out _));
    }

    [Fact]
    public async Task Every_process_local_concurrent_record_lands()
    {
        var context = Context("plan_review", round: 1);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var role = new RoleSpec(VendorRole.Critic, "role", Telemetry: context);
            var attempt = new VendorTurn(role, new Selection("model", null), "codex").Start(1, null);
            attempt.Succeeded();
            attempt.Finish(new WorkerUsage(), null);
        })));

        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        Assert.Equal(16, document.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData(0, 0, 0, 900, "00:00:00")]
    [InlineData(1, 2, 3, 999, "01:02:03")]
    [InlineData(125, 0, 0, 0, "125:00:00")]
    public void Duration_is_truncated_and_total_hours_are_unbounded(int hours, int minutes, int seconds,
                                                                    int milliseconds, string expected)
    {
        var duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes)
                       + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);

        Assert.Equal(expected, VendorAttempt.FormatDuration(duration));
    }

    private string LogPath => Path.Combine(_root, "forge.log");

    private WorkerTelemetryContext Context(string act,
                                           int? round = null,
                                           int? taskNumber = null,
                                           int? taskCount = null) =>
        new(Path.Combine(_root, "telemetry.json"), new RunLog(LogPath), act, round, taskNumber, taskCount);
}
