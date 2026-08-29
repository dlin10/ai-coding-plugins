using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using PlanForge.Vendors.Cursor;
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

    /// <summary>
    /// The run behind this one had every one of its five shell calls come back with no exit status,
    /// and forge.log said nothing: the session read the final text and dropped the rest. The shapes
    /// are cursor-agent's own, measured against 2026.08.25-3e8eec8.
    /// </summary>
    [Fact]
    public async Task Cursor_tool_calls_carry_the_command_and_the_failure_that_has_no_success()
    {
        var session = new CursorAgentSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("auto", null), null);

        using (var started = JsonDocument.Parse(
            """
            {
              "type": "tool_call",
              "subtype": "started",
              "tool_call": {
                "shellToolCall": { "args": { "command": "dotnet build", "timeout": 30000 } },
                "toolCallId": "call_1",
                "startedAtMs": "1788023822681"
              }
            }
            """))
        {
            Assert.Null(session.Observe(started.RootElement));
        }

        using (var completed = JsonDocument.Parse(
            """
            {
              "type": "tool_call",
              "subtype": "completed",
              "tool_call": {
                "shellToolCall": {
                  "args": { "command": "dotnet build" },
                  "result": { "spawnError": { "error": "The shell command returned no exit status" } }
                },
                "toolCallId": "call_1"
              }
            }
            """))
        {
            Assert.Null(session.Observe(completed.RootElement));
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var use = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolUse);
        Assert.Equal("shell", use.Text);
        Assert.Contains(("command", "dotnet build"), use.Fields!);

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Equal("shell", outcome.Text);
        Assert.Contains(("isError", "true"), outcome.Fields!);
        Assert.Contains(outcome.Fields!, field => field.Name == "output" && field.Value!.Contains("no exit status"));
    }

    /// <summary>
    /// A shell command that ran is the case the exit code belongs to, and the final text still has
    /// to survive the reading that now happens around it.
    /// </summary>
    [Fact]
    public async Task Cursor_reads_the_exit_code_of_a_command_that_ran_and_still_returns_the_result()
    {
        var session = new CursorAgentSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("auto", null), null);

        using (var completed = JsonDocument.Parse(
            """
            {
              "type": "tool_call",
              "subtype": "completed",
              "tool_call": {
                "shellToolCall": {
                  "result": { "success": { "exitCode": 1, "stdout": "error CS0103", "stderr": "" } }
                }
              }
            }
            """))
        {
            session.Observe(completed.RootElement);
        }

        using (var final = JsonDocument.Parse("""{ "type": "result", "result": "{\"status\":\"done\"}" }"""))
        {
            Assert.Equal("{\"status\":\"done\"}", session.Observe(final.RootElement));
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Contains(("isError", "false"), outcome.Fields!);
        Assert.Contains(("exitCode", "1"), outcome.Fields!);
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

    private static async Task<List<VendorEvent>> CollectAsync(IVendorSession session)
    {
        var events = new List<VendorEvent>();
        await foreach (var raised in session.Events) events.Add(raised);
        return events;
    }
}
