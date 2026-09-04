using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The prompt travels on standard input, never as an argument, so everything codex needs to run a
/// turn has to be pinned here rather than proved by starting a process.
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
            "-",
            "--skip-git-repo-check",
            "--json",
            "--output-schema", "schema.json",
            "-o", "result.json",
            "-m", "gpt-5.6-sol",
            "-c", "sandbox_mode=" + TomlValue.String("read-only"),
            "-c", "developer_instructions=" + TomlValue.String("review the plan")
        ], arguments);
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
        Assert.Contains("model_reasoning_effort=" + TomlValue.String("high"), arguments);
        Assert.Contains("sandbox_mode=" + TomlValue.String("workspace-write"), arguments);
    }

    [Fact]
    public void A_builder_with_no_session_id_emits_no_resume_entry()
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task");
        var selection = new Selection("gpt-5.6-sol", null);

        var arguments = CodexCliSession.BuildArguments(role, selection, null, "schema.json", "result.json");

        Assert.DoesNotContain("resume", arguments);
    }
}
