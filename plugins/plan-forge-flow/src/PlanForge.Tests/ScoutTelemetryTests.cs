using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutTelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-scout-telemetry",
                                                  Guid.NewGuid().ToString("n"));

    public ScoutTelemetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Successful_and_failed_scout_attempts_carry_the_scout_act()
    {
        var context = Context();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new Critique("approve", [], "scout"), "scout-session");
        await using var ignored = await vendor.StartAsync(
            new RoleSpec(VendorRole.Scout, "scout", Telemetry: context),
            new Selection("gpt-scout", "high"), null, CancellationToken.None);
        var session = Assert.Single(vendor.Sessions);

        Assert.Equal("scout", session.Role.Telemetry?.Act);
        var turn = new VendorTurn(session.Role, session.Selection, vendor.Id);

        var successful = turn.Start(10, null);
        successful.Succeeded();
        successful.Finish(new WorkerUsage(), "scout-session");

        var failed = turn.Start(10, "scout-session");
        failed.Failed();
        failed.Finish(new WorkerUsage(), null);

        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        var records = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal("scout", records[0].GetProperty("act").GetString());
        Assert.Equal("scout", records[1].GetProperty("act").GetString());
        Assert.Equal("succeeded", records[0].GetProperty("outcome").GetString());
        Assert.Equal("failed", records[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Scout_usage_uses_the_existing_vendor_selection_and_session_fields()
    {
        var context = Context();
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(new Critique("approve", [], "scout"), "claude-scout-session");
        await using var ignored = await vendor.StartAsync(
            new RoleSpec(VendorRole.Scout, "scout", Telemetry: context),
            new Selection("sonnet", "medium"), "old-session", CancellationToken.None);
        var session = Assert.Single(vendor.Sessions);
        var turn = new VendorTurn(session.Role, session.Selection, vendor.Id);
        var attempt = turn.Start(VendorTurn.PromptBytes(session.Role.SystemPrompt), "old-session");

        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(InputTokens: 7, OutputTokens: 3), "claude-scout-session");

        using var document = JsonDocument.Parse(File.ReadAllText(context.Path));
        var record = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("vendor.usage", record.GetProperty("event").GetString());
        Assert.Equal("scout", record.GetProperty("act").GetString());
        Assert.Equal("claude", record.GetProperty("vendor").GetString());
        Assert.Equal("scout", record.GetProperty("role").GetString());
        Assert.Equal("sonnet", record.GetProperty("model").GetString());
        Assert.Equal("medium", record.GetProperty("effort").GetString());
        Assert.Equal("resumed", record.GetProperty("sessionMode").GetString());
        Assert.Equal("claude-scout-session", record.GetProperty("sessionId").GetString());
        Assert.Equal(7, record.GetProperty("inputTokens").GetInt64());
        Assert.Equal(3, record.GetProperty("outputTokens").GetInt64());
    }

    private WorkerTelemetryContext Context() =>
        new(Path.Combine(_root, "telemetry.json"), new RunLog(Path.Combine(_root, "forge.log")), "scout");
}
