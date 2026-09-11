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
