using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using PlanForge.Vendors.Cursor;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The task prompt travels on standard input. Role instructions and the rest of the turn contract
/// travel as arguments, so everything codex needs to run a turn is pinned here.
/// </summary>
public sealed class CodexArgumentTests
{
    [Fact]
    public void A_critic_with_no_session_and_no_effort_gets_the_exact_argument_order()
    {
        var role = new RoleSpec(VendorRole.Critic, "review the plan");
        var selection = new Selection("gpt-5.6-sol", null);

        var arguments = CodexCliSession.BuildArguments(role, selection, null, "schema.json", "result.json");

        Assert.Equal(
        [
            "exec",
            "--ephemeral",
            "-",
            "--skip-git-repo-check",
            "--json",
            "--output-schema", "schema.json",
            "-o", "result.json",
            "-m", "gpt-5.6-sol",
            "-c", "service_tier=" + TomlValue.String("default"),
            "-c", "plugins.plan-forge-flow@dlin10-ai-coding-plugins.enabled=false",
            "-c", "sandbox_mode=" + TomlValue.String("read-only"),
            "-c", "developer_instructions=" + TomlValue.String("review the plan")
        ], arguments);
    }

    /// <summary>
    /// A worker inherits `service_tier` from `~/.codex/config.toml`, which the Codex desktop app
    /// rewrites whenever its Fast toggle changes, so standard speed is asked for as explicitly as
    /// Fast is — see docs/adr/0023. `default` and `fast` are the spellings codex-cli 0.157.0 served
    /// as standard and as its priority tier on 2026-09-25.
    /// </summary>
    [Fact]
    public void Every_turn_names_its_speed_so_the_desktop_toggle_cannot_choose_it()
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task");

        var fast = CodexCliSession.BuildArguments(role, new Selection("gpt-6-astra", "high", Fast: true), "thread-1",
                                                  "schema.json", "result.json");
        var standard = CodexCliSession.BuildArguments(role, new Selection("gpt-6-astra", "high"), "thread-1",
                                                      "schema.json", "result.json");

        Assert.Contains("service_tier=" + TomlValue.String("fast"), fast);
        Assert.DoesNotContain("service_tier=" + TomlValue.String("default"), fast);
        Assert.Contains("service_tier=" + TomlValue.String("default"), standard);
        Assert.DoesNotContain("service_tier=" + TomlValue.String("fast"), standard);
    }

    [Fact]
    public void A_builder_resuming_a_session_puts_resume_and_the_id_right_after_exec_and_before_the_dash()
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task");
        var selection = new Selection("gpt-5.6-sol", "high");

        var arguments = CodexCliSession.BuildArguments(role, selection, "thread-1", "schema.json", "result.json");

        Assert.Equal("exec", arguments[0]);
        Assert.Equal("resume", arguments[1]);
        Assert.Equal("thread-1", arguments[2]);
        Assert.Equal("-", arguments[3]);
        Assert.DoesNotContain("--ephemeral", arguments);
        Assert.Contains("model_reasoning_effort=" + TomlValue.String("high"), arguments);
        Assert.Contains("plugins.plan-forge-flow@dlin10-ai-coding-plugins.enabled=false", arguments);
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_roles_disable_only_the_self_plugin_and_keep_the_ambient_configuration()
    {
        var critic = CodexCliSession.BuildArguments(
            new RoleSpec(VendorRole.Critic, "review the plan"),
            new Selection("gpt-5.6-sol", null), null, "schema.json", "result.json");
        var builder = CodexCliSession.BuildArguments(
            new RoleSpec(VendorRole.Builder, "implement the task"),
            new Selection("gpt-5.6-sol", null), null, "schema.json", "result.json");

        foreach (var arguments in new[] { critic, builder })
        {
            Assert.Contains("plugins.plan-forge-flow@dlin10-ai-coding-plugins.enabled=false", arguments);
            Assert.DoesNotContain("--ignore-user-config", arguments);
            Assert.DoesNotContain("--disable", arguments);
        }
    }

    [Fact]
    public void A_builder_with_no_session_id_emits_no_resume_entry()
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task");
        var selection = new Selection("gpt-5.6-sol", null);

        var arguments = CodexCliSession.BuildArguments(role, selection, null, "schema.json", "result.json");

        Assert.DoesNotContain("resume", arguments);
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builder roots remain in the role for compatibility, but cannot limit an unsandboxed launch.
    /// </summary>
    /// <param name="sessionId">The existing Builder session, or null for a fresh launch.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("thread-1")]
    public void A_builder_with_writable_roots_bypasses_the_sandbox_on_every_turn(string? sessionId)
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task", [@"C:\Dev\eShopOnContainers", @"D:\other"]);
        var selection = new Selection("gpt-5.6-sol", null);

        var arguments = CodexCliSession.BuildArguments(role, selection, sessionId, "schema.json", "result.json");

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_workspace_write", StringComparison.Ordinal));
    }

    [Fact]
    public void A_critic_never_receives_writable_roots()
    {
        var role = new RoleSpec(VendorRole.Critic, "review the plan", [@"C:\Dev\eShopOnContainers"]);

        var arguments = CodexCliSession.BuildArguments(role, new Selection("gpt-5.6-sol", null), null, "schema.json", "result.json");

        Assert.Contains("sandbox_mode=" + TomlValue.String("read-only"), arguments);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_workspace_write", StringComparison.Ordinal));
    }

    /// <summary>
    /// Issue #90: under `codex exec` an MCP call nothing approved fails with "approval policy is
    /// never". The key merges into the server the list reported; one naming a server no layer
    /// declared stops codex before it starts (measured 2026-09-17, codex-cli 0.154.0).
    /// </summary>
    [Fact]
    public void Both_roles_approve_each_granted_server_before_the_instructions()
    {
        foreach (var role in new[] { VendorRole.Critic, VendorRole.Builder })
        {
            var arguments = CodexCliSession.BuildArguments(new RoleSpec(role, "work"), new Selection("gpt-5.6-sol", null),
                                                           null, "schema.json", "result.json", ["roslyn-mcp", "roslyn-mcp-plan-forge-flow"]);

            var first = arguments.IndexOf("mcp_servers.roslyn-mcp.default_tools_approval_mode=" + TomlValue.String("approve"));
            Assert.Equal("-c", arguments[first - 1]);
            Assert.Equal("-c", arguments[first + 1]);
            Assert.Equal("mcp_servers.roslyn-mcp-plan-forge-flow.default_tools_approval_mode=" + TomlValue.String("approve"), arguments[first + 2]);
            Assert.Equal("-c", arguments[first + 3]);
            Assert.StartsWith("developer_instructions=", arguments[first + 4], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// codex-rs MAX_USER_INPUT_TEXT_CHARS, which refused run 20261004-093111-b79938's fourth code-review
    /// round at 1,056,671 characters. Neither claude nor cursor-agent states a character limit.
    /// </summary>
    [Fact]
    public void Only_codex_states_a_prompt_character_limit()
    {
        Assert.Equal(1_048_576, ((IVendor)new CodexCliVendor()).PromptCharacterLimit);
        Assert.Null(((IVendor)new ClaudeCliVendor()).PromptCharacterLimit);
        Assert.Null(((IVendor)new CursorAgentVendor()).PromptCharacterLimit);
    }
}
