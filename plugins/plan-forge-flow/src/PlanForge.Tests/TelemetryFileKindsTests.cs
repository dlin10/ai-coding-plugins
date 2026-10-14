using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// <c>telemetry.json</c> holds Worker and Orchestrator records side by side, and elements the server
/// does not know; every write keeps what is already there.
/// </summary>
public sealed class TelemetryFileKindsTests : IDisposable
{
    // A Vendor record's `at` is the machine's own clock, so a test that compares it with an
    // Orchestrator record names a time on either side of any real one.
    private const string EARLIEST = "2000-01-01 00:00:00";
    private const string LATEST = "9999-12-31 23:59:59";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-telemetry-kinds", Guid.NewGuid().ToString("n"));

    public TelemetryFileKindsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_vendor_record_appended_after_an_orchestrator_record_leaves_both_intact()
    {
        var context = Context();
        WorkerTelemetryFile.Append(context.Path, context.Log,
                                   Orchestrator("interview", inputTokens: 12, at: EARLIEST));
        Vendor(context, "session-1", 7);

        var records = Records(context);
        Assert.Equal(2, records.Length);
        Assert.Equal("orchestrator.usage", records[0].GetProperty("event").GetString());
        Assert.Equal("interview", records[0].GetProperty("phase").GetString());
        Assert.Equal(12, records[0].GetProperty("inputTokens").GetInt64());
        Assert.Equal("vendor.usage", records[1].GetProperty("event").GetString());
        Assert.Equal(7, records[1].GetProperty("inputTokens").GetInt64());
        Assert.False(records[1].TryGetProperty("phase", out _));
    }

    [Fact]
    public void An_orchestrator_record_appended_after_vendor_records_leaves_their_text_unchanged()
    {
        var context = Context();
        Vendor(context, "session-1", 7);
        Vendor(context, "session-2", 9);
        var before = File.ReadAllText(context.Path);

        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("planning", at: LATEST));

        var after = File.ReadAllText(context.Path);
        var vendorText = before[..before.LastIndexOf(']')].TrimEnd();
        Assert.StartsWith(vendorText, after, StringComparison.Ordinal);
        Assert.Equal(3, Records(context).Length);
    }

    [Fact]
    public void LastReport_ignores_an_orchestrator_element_with_the_same_session_id()
    {
        var context = Context();
        Vendor(context, "thread-a", 100);
        WorkerTelemetryFile.Append(context.Path, context.Log,
                                   Orchestrator("build", hostSessionId: "thread-a", inputTokens: 5000) with { Host = "codex" });

        WorkerUsage? seen = null;
        WorkerTelemetryFile.Append(context, "turn", 1, earlier =>
        {
            Assert.All(earlier, record => Assert.Equal("vendor.usage", record.Event));
            seen = WorkerTelemetryFile.LastReport(earlier, "codex", "thread-a");
            return VendorRecord("thread-a", 1);
        });

        Assert.Equal(100, seen!.InputTokens);
    }

    [Fact]
    public void An_element_of_an_unknown_event_survives_an_append()
    {
        var context = Context();
        File.WriteAllText(context.Path,
                          """[{"event":"future.kind","nested":{"list":[1,2.50,"три"]},"flag":true,"empty":null}]""");

        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("done"));
        Vendor(context, "session", 3);

        var records = Records(context);
        Assert.Equal(3, records.Length);
        Assert.Equal("future.kind", records[0].GetProperty("event").GetString());
        Assert.Equal(["event", "nested", "flag", "empty"], records[0].EnumerateObject().Select(property => property.Name));
        var list = records[0].GetProperty("nested").GetProperty("list").EnumerateArray().ToArray();
        Assert.Equal("1", list[0].GetRawText());
        Assert.Equal("2.50", list[1].GetRawText());
        Assert.Equal("три", list[2].GetString());
        Assert.True(records[0].GetProperty("flag").GetBoolean());
        Assert.Equal(JsonValueKind.Null, records[0].GetProperty("empty").ValueKind);
    }

    [Fact]
    public void A_malformed_file_survives_an_orchestrator_append_with_one_failure_carrying_the_event()
    {
        var context = Context();
        File.WriteAllText(context.Path, "[1]");

        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("code_review", round: 2));

        Assert.Equal("[1]", File.ReadAllText(context.Path));
        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(LogPath)));
        Assert.Equal("telemetry.write.failed", entry.RootElement.GetProperty("event").GetString());
        Assert.Equal("error", entry.RootElement.GetProperty("level").GetString());
        Assert.Equal("telemetry", entry.RootElement.GetProperty("source").GetString());
        var fields = entry.RootElement.GetProperty("fields");
        Assert.Equal("orchestrator.usage", fields.GetProperty("event").GetString());
        Assert.Equal("JsonException", fields.GetProperty("error").GetString());
        Assert.False(fields.TryGetProperty("turnId", out _));
        Assert.False(fields.TryGetProperty("attempt", out _));
    }

    [Fact]
    public void The_orchestrator_record_keeps_the_property_order_and_omits_absent_values()
    {
        var full = Context();
        WorkerTelemetryFile.Append(full.Path, full.Log,
                                   new OrchestratorUsageRecord("2026-10-14 09:00:00", "orchestrator.usage", "build",
                                                               null, 2, 5, "claude-code", "session", "telemetry",
                                                               "00:01:00", "00:01:30", "00:00:30",
                                                               10, 4, 3, 2, 1, 0.25, true, null));
        Assert.Equal(["at", "event", "phase", "taskNumber", "taskCount", "host", "hostSessionId", "usageSource",
                      "duration", "wallDuration", "toolDuration", "inputTokens", "cacheReadTokens",
                      "cacheCreationTokens", "outputTokens", "reasoningTokens", "costUsd", "usageIncomplete"],
                     Assert.Single(Records(full)).EnumerateObject().Select(property => property.Name));

        var sparse = Context("sparse");
        WorkerTelemetryFile.Append(sparse.Path, sparse.Log,
                                   Orchestrator("plan_revision", round: 1) with { UsageMissing = "no_host_session" });
        var record = Assert.Single(Records(sparse));
        Assert.Equal(["at", "event", "phase", "round", "host", "duration", "wallDuration", "toolDuration",
                      "usageMissing"],
                     record.EnumerateObject().Select(property => property.Name));
        Assert.Equal("no_host_session", record.GetProperty("usageMissing").GetString());
    }

    [Fact]
    public async Task Concurrent_appends_of_both_kinds_all_land()
    {
        var context = Context();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0) Vendor(context, $"session-{index}", index);
            else WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("build", inputTokens: index));
        })));

        var records = Records(context);
        Assert.Equal(24, records.Length);
        Assert.Equal(12, records.Count(record => record.GetProperty("event").GetString() == "vendor.usage"));
        Assert.Equal(12, records.Count(record => record.GetProperty("event").GetString() == "orchestrator.usage"));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void A_record_opened_before_records_already_in_the_file_is_placed_before_them()
    {
        var context = Context();
        File.WriteAllText(context.Path,
                          """
                          [{"at":"2026-10-14 20:56:55","event":"vendor.usage","act":"plan_review","round":1},
                           {"at":"2026-10-14 21:24:41","event":"vendor.usage","act":"plan_review","round":2}]
                          """);

        WorkerTelemetryFile.Append(context.Path, context.Log,
                                   Orchestrator("plan_revision", round: 1, at: "2026-10-14 21:12:19"));

        Assert.Equal(["2026-10-14 20:56:55", "2026-10-14 21:12:19", "2026-10-14 21:24:41"], Opened(context));
        Assert.Equal("plan_revision", Records(context)[1].GetProperty("phase").GetString());
    }

    [Fact]
    public void A_vendor_record_is_placed_by_its_opening_too()
    {
        var context = Context();
        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("planning", at: "2026-10-14 09:00:00"));
        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("plan_revision", round: 1, at: "2026-10-14 11:00:00"));

        IReadOnlyList<WorkerUsageRecord>? seen = null;
        WorkerTelemetryFile.Append(context, "turn", 1, earlier =>
        {
            seen = earlier;
            return VendorRecord("session", 1, at: "2026-10-14 10:00:00");
        });

        Assert.Empty(seen!);
        Assert.Equal(["2026-10-14 09:00:00", "2026-10-14 10:00:00", "2026-10-14 11:00:00"], Opened(context));
        Assert.Equal(["orchestrator.usage", "vendor.usage", "orchestrator.usage"],
                     Records(context).Select(record => record.GetProperty("event").GetString()));
    }

    [Fact]
    public void A_record_opened_at_the_same_second_goes_after_the_ones_already_there()
    {
        var context = Context();
        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("planning", at: "2026-10-14 09:00:00"));
        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("approval", at: "2026-10-14 09:00:00"));

        Assert.Equal(["planning", "approval"],
                     Records(context).Select(record => record.GetProperty("phase").GetString()));
    }

    [Theory]
    [InlineData("""[{"at":"2026-10-14 21:00:00","event":"vendor.usage"},{"event":"future.kind"}]""")]
    [InlineData("""[{"at":"2026-10-14 21:00:00","event":"vendor.usage"},{"at":5,"event":"future.kind"}]""")]
    public void An_element_without_a_text_at_is_a_boundary_the_record_is_not_moved_past(string existing)
    {
        var context = Context();
        File.WriteAllText(context.Path, existing);

        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("planning", at: "2026-10-14 10:00:00"));

        var records = Records(context);
        Assert.Equal(3, records.Length);
        Assert.Equal("future.kind", records[1].GetProperty("event").GetString());
        Assert.Equal("planning", records[2].GetProperty("phase").GetString());
    }

    [Fact]
    public void Records_already_out_of_order_stay_where_they_are()
    {
        var context = Context();
        File.WriteAllText(context.Path,
                          """[{"at":"2026-10-14 12:00:00","event":"a"},{"at":"2026-10-14 11:00:00","event":"b"}]""");

        WorkerTelemetryFile.Append(context.Path, context.Log, Orchestrator("planning", at: "2026-10-14 11:30:00"));

        Assert.Equal(["2026-10-14 12:00:00", "2026-10-14 11:00:00", "2026-10-14 11:30:00"], Opened(context));
    }

    private static void Vendor(WorkerTelemetryContext context, string sessionId, long inputTokens)
    {
        var role = new RoleSpec(VendorRole.Builder, "role", Telemetry: context);
        var attempt = new VendorTurn(role, new Selection("model", "high"), "codex").Start(1, null);
        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(InputTokens: inputTokens), sessionId);
    }

    private static WorkerUsageRecord VendorRecord(string sessionId,
                                                  long inputTokens,
                                                  string at = "2026-10-14 09:00:00") =>
        new(at, "vendor.usage", "build", null, 1, 1, "codex", "builder", "model", "high",
            false, null, "resumed", sessionId, "turn", 1, "succeeded", "00:00:00", "00:00:00", null, 1,
            inputTokens, null, null, null, null, null, null, null);

    private static OrchestratorUsageRecord Orchestrator(string phase,
                                                        int? round = null,
                                                        string? hostSessionId = null,
                                                        long? inputTokens = null,
                                                        string at = "2026-10-14 09:00:00") =>
        new(at, "orchestrator.usage", phase, round, null, null, "claude-code", hostSessionId,
            null, "00:00:00", "00:00:00", "00:00:00", inputTokens, null, null, null, null, null, false, null);

    private static JsonElement[] Records(WorkerTelemetryContext context)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        return [.. document.RootElement.Clone().EnumerateArray()];
    }

    private static IEnumerable<string?> Opened(WorkerTelemetryContext context) =>
        Records(context).Select(record => record.GetProperty("at").GetString());

    private string LogPath => Path.Combine(_root, "forge.log");

    private WorkerTelemetryContext Context(string name = "telemetry") =>
        new(Path.Combine(_root, name + ".json"), new RunLog(LogPath), "build");
}
