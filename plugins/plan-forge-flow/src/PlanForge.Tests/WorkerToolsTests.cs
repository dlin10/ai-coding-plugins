using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Run;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Issue #90: a headless worker is refused every MCP call nothing granted, and neither vendor that
/// needs a grant takes a pattern — claude matches a rule on the exact server name, codex approves
/// one server at a time. So the run's patterns are matched here, against each vendor's own list.
/// See docs/adr/0017.
/// </summary>
public sealed class WorkerToolsTests
{
    /// <summary>`claude mcp list` as Claude Code 2.1.273 printed it on 2026-09-17, trimmed.</summary>
    private static readonly string[] ClaudeList =
    [
        "Checking MCP server health…",
        "",
        "claude.ai Claude Docs: https://api.anthropic.com/v1/pages/mcp - ✔ Connected",
        "plugin:context7:context7: https://mcp.context7.com/mcp?client=claude-code-plugin (HTTP) - ✔ Connected",
        "plugin:plan-forge-flow:plan-forge-flow: cmd.exe /d /c C:/Users/Admin/.claude/plugins/cache/dlin10-ai-coding-plugins/plan-forge-flow/0.29.1\\bin\\planforge-launcher.cmd - ✔ Connected",
        "roslyn-mcp-plan-forge-flow: http://localhost:5051/mcp (HTTP) - ✔ Connected",
        "roslyn-aicodingplugins: http://localhost:5053/mcp (HTTP) - ✘ Failed to connect — ConnectionRefused: Unable to connect. Is the computer able to access the url?"
    ];

    [Fact]
    public void The_default_grants_the_roslyn_servers_and_an_empty_list_grants_nothing()
    {
        Assert.Equal(["roslyn-*"], WorkerTools.Effective(null));
        Assert.Empty(WorkerTools.Effective([]));
    }

    [Fact]
    public void A_pattern_matches_whole_server_names_with_a_star_for_any_run_of_characters()
    {
        var servers = new[] { "roslyn-mcp", "roslyn-aicodingplugins", "Roslyn-Upper", "my-roslyn-mcp", "roslyn", "sql-server" };

        Assert.Equal(["roslyn-mcp", "roslyn-aicodingplugins", "Roslyn-Upper"], WorkerTools.Match(["roslyn-*"], servers));
        Assert.Equal(["sql-server"], WorkerTools.Match(["sql-server"], servers));
        Assert.Empty(WorkerTools.Match([], servers));
    }

    [Fact]
    public void A_server_two_patterns_match_is_granted_once()
    {
        Assert.Equal(["roslyn-mcp"], WorkerTools.Match(["roslyn-*", "*-mcp"], ["roslyn-mcp"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("roslyn *")]
    public void A_blank_pattern_or_one_with_whitespace_is_refused(string pattern)
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => WorkerTools.Validate([pattern]));

        Assert.Contains("workerTools", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_keeps_what_it_was_given_including_nothing()
    {
        Assert.Null(WorkerTools.Validate(null));
        Assert.Empty(WorkerTools.Validate([])!);
        Assert.Equal(["roslyn-*", "sql-server"], WorkerTools.Validate(["roslyn-*", "sql-server"]));
    }

    [Fact]
    public void Claude_names_every_listed_server_and_keeps_colons_inside_a_plugin_name()
    {
        Assert.Equal(
        [
            "claude.ai Claude Docs",
            "plugin:context7:context7",
            "plugin:plan-forge-flow:plan-forge-flow",
            "roslyn-mcp-plan-forge-flow",
            "roslyn-aicodingplugins"
        ], ClaudeCliVendor.ParseServerList(ClaudeList));
    }

    /// <summary>
    /// A rule names the server as its tools do, and claude spells a plugin server's tools with
    /// underscores where the listed name has colons — `mcp__plugin_plan-forge-flow_plan-forge-flow__…`.
    /// </summary>
    [Theory]
    [InlineData("roslyn-mcp-plan-forge-flow", "mcp__roslyn-mcp-plan-forge-flow")]
    [InlineData("plugin:context7:context7", "mcp__plugin_context7_context7")]
    [InlineData("claude.ai Claude Docs", "mcp__claude_ai_Claude_Docs")]
    public void A_claude_rule_names_the_server_the_way_its_tools_do(string server, string rule)
    {
        Assert.Equal(rule, ClaudeCliSession.ToolRule(server));
    }

    [Fact]
    public void Codex_names_its_enabled_servers_that_a_config_override_can_address()
    {
        using var document = JsonDocument.Parse(
            """
            [
              { "name": "roslyn-mcp", "enabled": true, "transport": { "type": "streamable_http", "url": "http://localhost:5053/mcp" } },
              { "name": "roslyn-off", "enabled": false, "transport": { "type": "streamable_http", "url": "http://localhost:5054/mcp" } },
              { "name": "roslyn.dotted", "enabled": true, "transport": { "type": "streamable_http", "url": "http://localhost:5055/mcp" } },
              { "name": 7, "enabled": true },
              "not a server",
              { "name": "node_repl", "enabled": true, "transport": { "type": "stdio", "command": "node" } }
            ]
            """);

        Assert.Equal(["roslyn-mcp", "node_repl"], CodexCliVendor.ParseServerList(document.RootElement));
    }

    [Fact]
    public async Task A_grant_records_the_patterns_and_the_servers_they_matched()
    {
        using var log = new ScopedLog();
        var role = new RoleSpec(VendorRole.Critic, "review", WorkerTools: ["roslyn-*"]);

        var granted = await WorkerTools.GrantAsync("claude", role,
            _ => Task.FromResult<IReadOnlyList<string>>(["roslyn-mcp", "context7"]), CancellationToken.None);

        Assert.Equal(["roslyn-mcp"], granted);
        var fields = log.Single("worker.tools").GetProperty("fields");
        Assert.Equal("Critic", fields.GetProperty("role").GetString());
        Assert.Equal("roslyn-*", fields.GetProperty("patterns").GetString());
        Assert.Equal("roslyn-mcp", fields.GetProperty("servers").GetString());
        Assert.Equal("2", fields.GetProperty("listed").GetString());
    }

    /// <summary>
    /// The worker can still do the work by text search, and a failed act would cost more than the
    /// grant is worth — so a lookup that fails starts the worker without grants and says why.
    /// </summary>
    [Fact]
    public async Task A_failed_lookup_grants_nothing_and_logs_why()
    {
        using var log = new ScopedLog();
        var role = new RoleSpec(VendorRole.Builder, "build", WorkerTools: ["roslyn-*"]);

        var granted = await WorkerTools.GrantAsync("codex", role,
            _ => throw new VendorException("codex exited 1"), CancellationToken.None);

        Assert.Empty(granted);
        var entry = log.Single("worker.tools");
        Assert.Equal("warn", entry.GetProperty("level").GetString());
        Assert.Equal("codex exited 1", entry.GetProperty("fields").GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_role_with_no_patterns_looks_nothing_up()
    {
        using var log = new ScopedLog();
        var looked = false;

        var granted = await WorkerTools.GrantAsync("claude", new RoleSpec(VendorRole.Builder, "build", WorkerTools: []),
            _ =>
            {
                looked = true;
                return Task.FromResult<IReadOnlyList<string>>(["roslyn-mcp"]);
            }, CancellationToken.None);

        Assert.Empty(granted);
        Assert.False(looked);
    }

    [Fact]
    public async Task A_cancelled_lookup_is_not_mistaken_for_a_failed_one()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerTools.GrantAsync(
            "claude", new RoleSpec(VendorRole.Builder, "build", WorkerTools: ["roslyn-*"]),
            ct => Task.FromCanceled<IReadOnlyList<string>>(ct), cancelled.Token));
    }

    [Fact]
    public void The_run_state_keeps_the_patterns_it_was_begun_with()
    {
        var state = new RunState("run", "C:\\work", "Text", DateTimeOffset.Now, 0, 5, WorkerTools: ["roslyn-*", "sql-server"]);

        var json = JsonSerializer.Serialize(state, ForgeJson.Default.RunState);
        var read = JsonSerializer.Deserialize(json, ForgeJson.Default.RunState)!;

        Assert.Equal(["roslyn-*", "sql-server"], read.WorkerTools);
    }

    private sealed class ScopedLog : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

        private readonly IDisposable _scope;
        private readonly string _path;

        public ScopedLog()
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "forge.log");
            _scope = RunLog.Use(new RunLog(_path));
        }

        public JsonElement Single(string name) =>
            Assert.Single(File.ReadAllLines(_path).Select(line => JsonDocument.Parse(line).RootElement),
                          entry => entry.GetProperty("event").GetString() == name);

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
