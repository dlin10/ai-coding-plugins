using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Issue #24: the run behind it logged 224 tool-use events that each carried only the item type,
/// so the denial that explained the whole failure never reached the log. These pin the detail —
/// command, exit code, output — that sessions now extract and the log now carries.
/// </summary>
public sealed class VendorEventDetailTests
{
    [Fact]
    public void Codex_item_detail_carries_command_exit_code_and_output()
    {
        using var parameters = JsonDocument.Parse(
            """
            {
              "threadId": "t1",
              "item": {
                "type": "commandExecution",
                "command": "pwsh -NoProfile -Command \"dotnet --version\"",
                "exitCode": -1,
                "aggregatedOutput": "windows sandbox: CreateProcessAsUserW failed: 5 (Access is denied.)",
                "status": "failed"
              }
            }
            """);

        var detail = CodexAppServerSession.ItemDetail(parameters.RootElement);

        Assert.NotNull(detail);
        Assert.Contains(("command", "pwsh -NoProfile -Command \"dotnet --version\""), detail);
        Assert.Contains(("exitCode", "-1"), detail);
        Assert.Contains(("status", "failed"), detail);
        Assert.Contains(detail, field => field.Name == "output" && field.Value!.Contains("Access is denied"));
    }

    [Fact]
    public void Codex_item_detail_is_null_when_the_item_has_none_of_the_fields()
    {
        using var parameters = JsonDocument.Parse("""{ "threadId": "t1", "item": { "type": "webSearch" } }""");

        Assert.Null(CodexAppServerSession.ItemDetail(parameters.RootElement));
    }

    [Fact]
    public async Task Claude_tool_use_carries_the_command_and_its_result_carries_the_error()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var call = JsonDocument.Parse(
            """
            {
              "type": "assistant",
              "message": { "content": [
                { "type": "tool_use", "id": "call_1", "name": "Bash", "input": { "command": "dotnet test" } }
              ] }
            }
            """))
        {
            session.Observe(call.RootElement);
        }

        using (var result = JsonDocument.Parse(
            """
            {
              "type": "user",
              "message": { "content": [
                { "type": "tool_result", "tool_use_id": "call_1", "is_error": true, "content": "Access is denied." }
              ] }
            }
            """))
        {
            session.Observe(result.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var use = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolUse);
        Assert.Equal("Bash", use.Text);
        Assert.Contains(("command", "dotnet test"), use.Fields!);

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Equal("Bash", outcome.Text);
        Assert.Contains(("isError", "true"), outcome.Fields!);
        Assert.Contains(("output", "Access is denied."), outcome.Fields!);
    }

    [Fact]
    public async Task Claude_tool_result_flattens_text_blocks_and_survives_an_unknown_call_id()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var result = JsonDocument.Parse(
            """
            {
              "type": "user",
              "message": { "content": [
                { "type": "tool_result", "tool_use_id": "call_x", "content": [
                  { "type": "text", "text": "line one" },
                  { "type": "text", "text": "line two" }
                ] }
              ] }
            }
            """))
        {
            session.Observe(result.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Equal("?", outcome.Text);
        Assert.Contains(("isError", "false"), outcome.Fields!);
        Assert.Contains(("output", "line one\nline two"), outcome.Fields!);
    }

    [Fact]
    public void Structured_fields_land_as_separate_entries_in_the_run_log()
    {
        var path = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"), "forge.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var channel = Channel.CreateUnbounded<VendorEvent>();

        try
        {
            using (RunLog.Use(new RunLog(path)))
            {
                channel.Writer.Emit("codex", new VendorEvent(VendorEventKind.ToolResult, "commandExecution",
                    [("command", "dotnet test"), ("exitCode", "-1")]));
            }

            using var entry = JsonDocument.Parse(File.ReadAllLines(path).Single());
            var fields = entry.RootElement.GetProperty("fields");

            Assert.Equal("vendor.toolresult", entry.RootElement.GetProperty("event").GetString());
            Assert.Equal("commandExecution", fields.GetProperty("text").GetString());
            Assert.Equal("dotnet test", fields.GetProperty("command").GetString());
            Assert.Equal("-1", fields.GetProperty("exitCode").GetString());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_build_result_schema_requires_verification()
    {
        Assert.Contains("\"verification\"", Schemas.BuildResult.Json, StringComparison.Ordinal);
        Assert.Contains("\"required\": [\"status\", \"filesChanged\", \"verification\", \"summary\"]",
                        Schemas.BuildResult.Json, StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize(
            """
            {
              "status": "done",
              "filesChanged": ["a.cs"],
              "verification": { "outcome": "unavailable", "evidence": "CreateProcessAsUserW failed: 5" },
              "summary": "implemented, could not run the tests"
            }
            """,
            ContractJson.Default.BuildResult);

        Assert.NotNull(parsed);
        Assert.Equal("unavailable", parsed.Verification.Outcome);
        Assert.Equal("CreateProcessAsUserW failed: 5", parsed.Verification.Evidence);
    }

    private static async Task<List<VendorEvent>> CollectAsync(ClaudeCliSession session)
    {
        var events = new List<VendorEvent>();
        await foreach (var raised in session.Events) events.Add(raised);
        return events;
    }
}
