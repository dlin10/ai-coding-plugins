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

    /// <summary>
    /// The roots `forge.begin` was given widen the builder's sandbox as a TOML array of TOML strings,
    /// so a Windows path's backslashes are escaped rather than eaten by the TOML parser.
    /// </summary>
    [Fact]
    public void A_builder_with_writable_roots_passes_them_as_the_sandbox_array_after_the_sandbox_mode()
    {
        var role = new RoleSpec(VendorRole.Builder, "implement the task", [@"C:\Dev\eShopOnContainers", @"D:\other"]);
        var selection = new Selection("gpt-5.6-sol", null);

        var arguments = CodexCliSession.BuildArguments(role, selection, null, "schema.json", "result.json");

        var sandbox = arguments.IndexOf("sandbox_mode=" + TomlValue.String("workspace-write"));
        Assert.Equal("-c", arguments[sandbox + 1]);
        Assert.Equal("sandbox_workspace_write.writable_roots=[\"C:\\\\Dev\\\\eShopOnContainers\", \"D:\\\\other\"]", arguments[sandbox + 2]);
    }

    [Fact]
    public void A_critic_never_receives_writable_roots()
    {
        var role = new RoleSpec(VendorRole.Critic, "review the plan", [@"C:\Dev\eShopOnContainers"]);

        var arguments = CodexCliSession.BuildArguments(role, new Selection("gpt-5.6-sol", null), null, "schema.json", "result.json");

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_workspace_write", StringComparison.Ordinal));
    }
}
