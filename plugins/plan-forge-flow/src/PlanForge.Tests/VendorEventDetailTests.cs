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
    public async Task Codex_item_detail_carries_command_exit_code_and_output()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var completed = JsonDocument.Parse(
            """
            {
              "type": "item.completed",
              "item": {
                "type": "command_execution",
                "command": "pwsh -NoProfile -Command \"dotnet --version\"",
                "exit_code": -1,
                "aggregated_output": "windows sandbox: CreateProcessAsUserW failed: 5 (Access is denied.)",
                "status": "failed"
              }
            }
            """))
        {
            session.Observe(completed.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Equal("command_execution", outcome.Text);
        Assert.Contains(("command", "pwsh -NoProfile -Command \"dotnet --version\""), outcome.Fields!);
        Assert.Contains(("exitCode", "-1"), outcome.Fields!);
        Assert.Contains(("status", "failed"), outcome.Fields!);
        Assert.Contains(outcome.Fields!, field => field.Name == "output" && field.Value!.Contains("Access is denied"));
    }

    /// <summary>
    /// Issue #90: a codex worker's MCP calls, refused or not, never reached the run log, so whether a
    /// critic had used Roslyn could not be told afterwards. Shape measured 2026-09-17, codex-cli 0.154.0.
    /// </summary>
    [Fact]
    public async Task Codex_mcp_call_carries_its_server_tool_status_and_refusal()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Critic, "prompt"), new Selection("model", null), null);

        foreach (var line in new[]
        {
            """{"type":"item.started","item":{"id":"item_4","type":"mcp_tool_call","server":"probe","tool":"roslyn_search_symbols","arguments":{"query":"VendorFactory"},"result":null,"error":null,"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_4","type":"mcp_tool_call","server":"probe","tool":"roslyn_search_symbols","arguments":{"query":"VendorFactory"},"result":null,"error":{"message":"MCP tool call requires approval, but approval policy is never"},"status":"failed"}}"""
        })
        {
            using var document = JsonDocument.Parse(line);
            session.Observe(document.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var use = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolUse);
        Assert.Equal("mcp__probe__roslyn_search_symbols", use.Text);
        Assert.Contains(use.Fields!, field => field.Name == "input" && field.Value!.Contains("VendorFactory"));

        var outcome = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolResult);
        Assert.Equal("mcp__probe__roslyn_search_symbols", outcome.Text);
        Assert.Contains(("isError", "true"), outcome.Fields!);
        Assert.Contains(("status", "failed"), outcome.Fields!);
        Assert.Contains(("error", "MCP tool call requires approval, but approval policy is never"), outcome.Fields!);
    }

    [Fact]
    public async Task Codex_item_detail_is_null_when_the_item_has_none_of_the_fields()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var completed = JsonDocument.Parse("""{ "type": "item.completed", "item": { "type": "web_search" } }"""))
        {
            session.Observe(completed.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        Assert.DoesNotContain(events, raised => raised.Kind is VendorEventKind.ToolResult);
    }

    private const string SKILL_BUDGET_WARNING =
        """{"type":"error","message":"Skill descriptions were shortened to fit the skills context budget. Codex can still see every skill, but some descriptions are shorter. Disable unused skills or plugins to leave more room ..."}""";

    private const string USAGE_LIMIT =
        "You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 6:54 PM.";

    /// <summary>
    /// A top-level `error` line is not a failure on its own: codex 0.154.0 emits it for a retried
    /// stream error too, and a run that wrote its result carried the skill-budget notice as one.
    /// Only `turn.failed` ends the turn, so the notice is a warning and never the run's explanation.
    /// </summary>
    [Fact]
    public async Task Codex_error_line_without_a_failed_turn_is_a_warning()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var warning = JsonDocument.Parse(SKILL_BUDGET_WARNING))
        {
            session.Observe(warning.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        Assert.DoesNotContain(events, raised => raised.Kind is VendorEventKind.Failed);
        Assert.Equal("codex wrote no result", session.NoResultMessage);

        var entry = Assert.Single(log.Entries());
        Assert.Equal("warn", entry.GetProperty("level").GetString());
        Assert.Equal("vendor.warning", entry.GetProperty("event").GetString());
        Assert.StartsWith("Skill descriptions were shortened", entry.GetProperty("fields").GetProperty("text").GetString());
    }

    /// <summary>The shape 0.154.0 actually gives its warnings: an item of type `error`.</summary>
    [Fact]
    public async Task Codex_error_item_is_a_warning()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

        using (var item = JsonDocument.Parse(
            """{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Skill descriptions were shortened to fit the skills context budget."}}"""))
        {
            session.Observe(item.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        Assert.DoesNotContain(events, raised => raised.Kind is VendorEventKind.Failed);
        Assert.Equal("codex wrote no result", session.NoResultMessage);

        var entry = Assert.Single(log.Entries());
        Assert.Equal("warn", entry.GetProperty("level").GetString());
        Assert.Equal("vendor.warning", entry.GetProperty("event").GetString());
    }

    /// <summary>
    /// A usage limit arrives as an `error` line and then a `turn.failed` with the same message. The
    /// failed turn names the run's failure once, and a notice after it cannot take its place.
    /// </summary>
    [Fact]
    public async Task Codex_failed_turn_names_the_failure_even_when_a_warning_follows()
    {
        using var log = new ScopedLog();
        var session = new CodexCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);
        var message = "\"" + USAGE_LIMIT + "\"";

        foreach (var line in new[]
                 {
                     """{"type":"error","message":""" + message + "}",
                     """{"type":"turn.failed","error":{"message":""" + message + "}}",
                     SKILL_BUDGET_WARNING
                 })
        {
            using var document = JsonDocument.Parse(line);
            session.Observe(document.RootElement);
        }

        await session.DisposeAsync();
        var events = await CollectAsync(session);

        var failed = Assert.Single(events, raised => raised.Kind is VendorEventKind.Failed);
        Assert.Equal(USAGE_LIMIT, failed.Text);
        Assert.Equal("codex wrote no result: " + USAGE_LIMIT, session.NoResultMessage);

        var levels = log.Entries().Select(entry => (entry.GetProperty("level").GetString(), entry.GetProperty("event").GetString()));
        Assert.Equal([("warn", "vendor.warning"), ("error", "vendor.failed"), ("warn", "vendor.warning")], levels);
    }

    [Fact]
    public async Task Claude_tool_use_carries_the_command_and_its_result_carries_the_error()
    {
        using var log = new ScopedLog();
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
        using var log = new ScopedLog();
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
        using var log = new ScopedLog();
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
        using var log = new ScopedLog();
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

    /// <summary>
    /// A session raising events writes them to <see cref="RunLog.Current"/>, which outside an
    /// ambient scope is whatever log this process served last — another test class's file, held
    /// open for append while that class reads it back, which on a CI runner is an
    /// <see cref="IOException"/> in a test that shares nothing with this one. Scoping a log of our
    /// own is what <see cref="RunLog.Use"/> exists for; these tests assert on the events
    /// themselves, so the file is a sink and nothing reads it.
    /// </summary>
    private sealed class ScopedLog : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

        private readonly IDisposable _scope;

        public ScopedLog()
        {
            Directory.CreateDirectory(_directory);
            _scope = RunLog.Use(new RunLog(Path.Combine(_directory, "forge.log")));
        }

        /// <summary>The entries written so far, in order.</summary>
        public List<JsonElement> Entries()
        {
            var path = Path.Combine(_directory, "forge.log");
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        }

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
