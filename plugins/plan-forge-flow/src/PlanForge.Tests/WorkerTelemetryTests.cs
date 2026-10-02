using System.Text;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
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
        Assert.Equal(record.GetProperty("duration").GetString(), record.GetProperty("wallDuration").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("toolDuration").ValueKind);
        Assert.Equal(Encoding.UTF8.GetByteCount("roleприветschema"), record.GetProperty("promptBytes").GetInt64());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$",
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

    /// <summary>
    /// A session object lives for one call, as in the acts, so the thread's previous report can only
    /// come from the file. Another thread's records in between do not count, and an attempt that
    /// ended before reporting leaves the baseline where it was.
    /// </summary>
    [Fact]
    public void A_resumed_codex_attempt_records_what_it_added_to_the_thread_total()
    {
        var context = Context("build", taskNumber: 1, taskCount: 2);
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);

        CodexTurn(role, null, "thread-a", """{"input_tokens":100,"cached_input_tokens":60,"output_tokens":10}""");
        CodexTurn(role, null, "thread-b", """{"input_tokens":5000,"cached_input_tokens":4000,"output_tokens":500}""");
        CodexTurn(role, "thread-a", "thread-a", """{"input_tokens":250,"cached_input_tokens":180,"output_tokens":25}""");
        CodexTurn(role, "thread-a", "thread-a", usage: null);
        CodexTurn(role, "thread-a", "thread-a", """{"input_tokens":400,"cached_input_tokens":300,"output_tokens":45}""");

        var records = Records(context);
        Assert.Equal(100, records[0].GetProperty("inputTokens").GetInt64());
        Assert.False(records[0].TryGetProperty("sessionTotal", out _));
        Assert.Equal(150, records[2].GetProperty("inputTokens").GetInt64());
        Assert.Equal(120, records[2].GetProperty("cacheReadTokens").GetInt64());
        Assert.Equal(15, records[2].GetProperty("outputTokens").GetInt64());
        Assert.Equal(250, records[2].GetProperty("sessionTotal").GetProperty("inputTokens").GetInt64());
        Assert.False(records[3].TryGetProperty("inputTokens", out _));
        Assert.False(records[3].TryGetProperty("sessionTotal", out _));
        Assert.Equal(150, records[4].GetProperty("inputTokens").GetInt64());
        Assert.Equal(20, records[4].GetProperty("outputTokens").GetInt64());
        Assert.Equal(400, records[4].GetProperty("sessionTotal").GetProperty("inputTokens").GetInt64());
    }

    /// <summary>
    /// The first two reports are the builder's in run 20260928-113803-31c1d7; the third adds a
    /// subagent on another model, which counts toward the turn that ran it.
    /// </summary>
    [Fact]
    public void A_resumed_claude_attempt_records_what_it_added_to_the_session_totals()
    {
        var context = Context("review_fix", round: 1);
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);

        ClaudeTurn(role, null,
                   """{"claude-opus-5-5":{"inputTokens":76,"cacheReadInputTokens":6458883,"cacheCreationInputTokens":230267,"outputTokens":73136,"thinkingTokens":43012}}""",
                   "4.5969366");
        ClaudeTurn(role, "claude-session",
                   """{"claude-opus-5-5":{"inputTokens":128,"cacheReadInputTokens":14159752,"cacheCreationInputTokens":350264,"outputTokens":153236,"thinkingTokens":82573}}""",
                   "8.6992944");
        ClaudeTurn(role, "claude-session",
                   """
                   {
                     "claude-opus-5-5":{"inputTokens":132,"cacheReadInputTokens":14160752,"cacheCreationInputTokens":350274,"outputTokens":153286,"thinkingTokens":82578},
                     "claude-haiku-4-5-20251001":{"inputTokens":2,"cacheReadInputTokens":300,"cacheCreationInputTokens":20,"outputTokens":25,"thinkingTokens":0}
                   }
                   """,
                   "8.8");

        var records = Records(context);
        Assert.Equal(6689226, records[0].GetProperty("inputTokens").GetInt64());
        Assert.Equal(4.5969366m, records[0].GetProperty("costUsd").GetDecimal());
        Assert.False(records[0].TryGetProperty("sessionTotal", out _));
        Assert.Equal(7820918, records[1].GetProperty("inputTokens").GetInt64());
        Assert.Equal(7700869, records[1].GetProperty("cacheReadTokens").GetInt64());
        Assert.Equal(80100, records[1].GetProperty("outputTokens").GetInt64());
        Assert.Equal(39561, records[1].GetProperty("reasoningTokens").GetInt64());
        Assert.Equal(4.1023578m, records[1].GetProperty("costUsd").GetDecimal());
        var total = records[1].GetProperty("sessionTotal");
        Assert.Equal(14510144, total.GetProperty("inputTokens").GetInt64());
        Assert.Equal(8.6992944m, total.GetProperty("costUsd").GetDecimal());
        Assert.Equal(1336, records[2].GetProperty("inputTokens").GetInt64());
        Assert.Equal(75, records[2].GetProperty("outputTokens").GetInt64());
        Assert.Equal(5, records[2].GetProperty("reasoningTokens").GetInt64());
        Assert.Equal(0.1007056m, records[2].GetProperty("costUsd").GetDecimal());
    }

    /// <summary>The kept total is what lets the attempt after an inconsistent report count again.</summary>
    [Fact]
    public void A_total_below_the_previous_report_is_named_and_the_next_attempt_counts_from_it()
    {
        var context = Context("scout");
        var role = new RoleSpec(VendorRole.Scout, "role", Telemetry: context);

        CodexTurn(role, null, "thread", """{"input_tokens":1000,"output_tokens":10}""");
        CodexTurn(role, "thread", "thread", """{"input_tokens":900,"output_tokens":20}""");
        CodexTurn(role, "thread", "thread", """{"input_tokens":950,"output_tokens":25}""");

        var records = Records(context);
        Assert.False(records[1].TryGetProperty("inputTokens", out _));
        Assert.Equal(10, records[1].GetProperty("outputTokens").GetInt64());
        Assert.Equal("usage.input_tokens", Assert.Single(records[1].GetProperty("malformedUsageFields").EnumerateArray()).GetString());
        Assert.Equal(50, records[2].GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, records[2].GetProperty("outputTokens").GetInt64());
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

    /// <summary>One codex call as the acts make it: a new session object, observed, then finished.</summary>
    /// <param name="usage">The <c>turn.completed</c> usage; null for an attempt cancelled before it.</param>
    private static void CodexTurn(RoleSpec role, string? resumeToken, string threadId, string? usage)
    {
        var selection = new Selection("gpt-6-astra", "high");
        var session = new CodexCliSession(role, selection, null, resumeToken);
        Observe(session.Observe, $$"""{"type":"thread.started","thread_id":"{{threadId}}"}""");
        if (usage is not null) Observe(session.Observe, $$"""{"type":"turn.completed","usage":{{usage}}}""");

        var attempt = new VendorTurn(role, selection, "codex").Start(1, resumeToken);
        if (usage is null) attempt.Cancelled();
        else attempt.Succeeded();
        attempt.Finish(session.UsageSince, threadId);
    }

    /// <param name="modelUsage">The result line's <c>modelUsage</c>, the session's running totals.</param>
    private static void ClaudeTurn(RoleSpec role, string? resumeToken, string modelUsage, string totalCostUsd)
    {
        var selection = new Selection("claude-opus-5-5", "high");
        var session = new ClaudeCliSession(role, selection, null, resumeToken);
        Observe(json => session.Observe(json),
                $$"""{"type":"result","subtype":"success","session_id":"claude-session","total_cost_usd":{{totalCostUsd}},"modelUsage":{{modelUsage}}}""");

        var attempt = new VendorTurn(role, selection, "claude").Start(1, resumeToken);
        attempt.Succeeded();
        attempt.Finish(session.UsageSince, "claude-session");
    }

    private static void Observe(Action<JsonElement> observe, string line)
    {
        using var document = JsonDocument.Parse(line);
        observe(document.RootElement);
    }

    private static JsonElement[] Records(WorkerTelemetryContext context)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        return [.. document.RootElement.Clone().EnumerateArray()];
    }

    private string LogPath => Path.Combine(_root, "forge.log");

    private WorkerTelemetryContext Context(string act,
                                           int? round = null,
                                           int? taskNumber = null,
                                           int? taskCount = null) =>
        new(Path.Combine(_root, "telemetry.json"), new RunLog(LogPath), act, round, taskNumber, taskCount);
}
