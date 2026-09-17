using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

public sealed class ClaudeArgumentTests
{
    private const string SelfPluginSettings =
        """{"enabledPlugins":{"plan-forge-flow@dlin10-ai-coding-plugins":false}}""";

    [Fact]
    public void Critic_disables_the_self_plugin_and_does_not_persist_its_session()
    {
        var session = new ClaudeCliSession(
            new RoleSpec(VendorRole.Critic, "review the plan"),
            new Selection("sonnet", "low"), null);

        Assert.Equal(
        [
            "--print",
            "--output-format", "stream-json",
            "--verbose",
            "--json-schema", "schema.json",
            "--append-system-prompt", "review the plan",
            "--model", "sonnet",
            "--effort", "low",
            "--settings", SelfPluginSettings,
            "--no-session-persistence"
        ], session.BuildArguments("schema.json"));
    }

    [Fact]
    public void Builder_disables_the_self_plugin_but_keeps_its_resumable_session()
    {
        var session = new ClaudeCliSession(
            new RoleSpec(VendorRole.Builder, "implement the task"),
            new Selection("sonnet", null), null, "session-1");

        var arguments = session.BuildArguments("schema.json");

        Assert.Contains(SelfPluginSettings, arguments);
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.Equal("session-1", arguments[arguments.IndexOf("--resume") + 1]);
    }

    /// <summary>
    /// Issue #90: under `acceptEdits` alone a headless builder was refused `dotnet test` and every
    /// Roslyn call. A blanket shell rule also lifts the safety checks a pattern rule leaves standing
    /// (measured 2026-09-17, see CONTEXT.md).
    /// </summary>
    [Fact]
    public void A_builder_may_run_commands_and_call_the_servers_it_was_granted()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "build"), new Selection("sonnet", null), null,
                                           grantedServers: ["roslyn-mcp-plan-forge-flow", "plugin:context7:context7"]);

        var arguments = session.BuildArguments("schema.json");

        Assert.Equal("Bash,PowerShell,mcp__roslyn-mcp-plan-forge-flow,mcp__plugin_context7_context7",
                     arguments[arguments.IndexOf("--allowedTools") + 1]);
    }

    [Fact]
    public void A_builder_granted_no_server_may_still_run_commands()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "build"), new Selection("sonnet", null), null);

        var arguments = session.BuildArguments("schema.json");

        Assert.Equal("Bash,PowerShell", arguments[arguments.IndexOf("--allowedTools") + 1]);
    }

    /// <summary>The critic judges symbol claims through Roslyn too, and stays without a shell.</summary>
    [Fact]
    public void A_critic_is_granted_its_servers_and_nothing_else()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Critic, "review"), new Selection("sonnet", null), null,
                                           grantedServers: ["roslyn-aicodingplugins"]);

        var arguments = session.BuildArguments("schema.json");

        Assert.Equal("mcp__roslyn-aicodingplugins", arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("acceptEdits", arguments);
    }

    /// <summary>
    /// A long command needs a foreground way to run, or the model backgrounds it and loses it with
    /// the session (issue #91). A foreground call sends a heartbeat every 30 s, so the server's
    /// idle reaper does not interfere.
    /// </summary>
    [Fact]
    public void Every_worker_may_run_a_foreground_command_for_thirty_minutes()
    {
        Assert.Equal("1800000", ClaudeCliSession.BuildEnvironment()["BASH_MAX_TIMEOUT_MS"]);
    }

    [Fact]
    public void Both_roles_override_only_the_self_plugin_and_keep_ambient_configuration()
    {
        var sessions = new[]
        {
            new ClaudeCliSession(new RoleSpec(VendorRole.Critic, "review"), new Selection("sonnet", null), null),
            new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "build"), new Selection("sonnet", null), null)
        };

        foreach (var session in sessions)
        {
            var arguments = session.BuildArguments("schema.json");
            var settings = arguments[arguments.IndexOf("--settings") + 1];
            using var document = JsonDocument.Parse(settings);
            var plugins = document.RootElement.GetProperty("enabledPlugins");

            Assert.Single(plugins.EnumerateObject());
            Assert.False(plugins.GetProperty("plan-forge-flow@dlin10-ai-coding-plugins").GetBoolean());
            Assert.DoesNotContain("--safe-mode", arguments);
            Assert.DoesNotContain("--bare", arguments);
            Assert.DoesNotContain("--disable-slash-commands", arguments);
            Assert.DoesNotContain("--strict-mcp-config", arguments);
            Assert.DoesNotContain("--setting-sources", arguments);
        }
    }
}
