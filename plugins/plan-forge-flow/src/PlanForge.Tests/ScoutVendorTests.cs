using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutVendorTests
{
    [Fact]
    public void Claude_scout_is_resumable_without_session_persistence_disable()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Scout, "scout"),
                                           new Selection("sonnet", "low"), null);

        var arguments = session.BuildArguments("schema.json");

        Assert.True(session.CanResume);
        Assert.Null(session.ResumeToken);
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.DoesNotContain("--permission-mode", arguments);
    }

    [Fact]
    public void Claude_scout_allow_lists_only_web_tools_and_granted_worker_servers()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Scout, "scout"),
                                           new Selection("sonnet", null), null,
                                           grantedServers: ["roslyn-mcp-plan-forge-flow"]);

        var arguments = session.BuildArguments("schema.json");

        Assert.Equal("WebSearch,WebFetch,mcp__roslyn-mcp-plan-forge-flow",
                     arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("Bash", arguments);
        Assert.DoesNotContain("PowerShell", arguments);
        Assert.DoesNotContain("acceptEdits", arguments);
    }

    [Fact]
    public void Claude_scout_resumes_the_persisted_session()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Scout, "scout"),
                                           new Selection("sonnet", null), null, "scout-session");

        var arguments = session.BuildArguments("schema.json");

        Assert.True(session.CanResume);
        Assert.Equal("scout-session", session.ResumeToken);
        Assert.Equal("scout-session", arguments[arguments.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--no-session-persistence", arguments);
    }

    [Fact]
    public void Codex_scout_is_fresh_unsandboxed_and_not_ephemeral()
    {
        var role = new RoleSpec(VendorRole.Scout, "scout", [@"C:\outside"]);
        var arguments = CodexCliSession.BuildArguments(role, new Selection("gpt-scout", null), null,
                                                        "schema.json", "result.json");

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
        Assert.DoesNotContain("--ephemeral", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_workspace_write", StringComparison.Ordinal));
        AssertNoNetworkDisable(arguments);
    }

    [Fact]
    public void Codex_scout_resumes_without_writable_roots_or_network_disabling_flags()
    {
        var role = new RoleSpec(VendorRole.Scout, "scout", [@"C:\outside"]);
        var arguments = CodexCliSession.BuildArguments(role, new Selection("gpt-scout", "high"),
                                                        "scout-thread", "schema.json", "result.json",
                                                        ["roslyn-mcp"]);

        Assert.Equal(["exec", "resume", "scout-thread", "-"], arguments.Take(4));
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
        Assert.Contains("mcp_servers.roslyn-mcp.default_tools_approval_mode=\"approve\"", arguments);
        Assert.DoesNotContain("--ephemeral", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("sandbox_workspace_write", StringComparison.Ordinal));
        AssertNoNetworkDisable(arguments);
    }

    [Fact]
    public void Every_scout_vendor_session_can_resume_and_only_codex_runs_without_a_read_only_launch()
    {
        var selection = new Selection("model", null);
        var claude = new ClaudeCliSession(new RoleSpec(VendorRole.Scout, "scout"), selection, null, "claude");
        var codex = new CodexCliSession(new RoleSpec(VendorRole.Scout, "scout"), selection, null, "codex");

        Assert.All(new IVendorSession[] { claude, codex }, session => Assert.True(session.CanResume));
        Assert.DoesNotContain("acceptEdits", claude.BuildArguments("schema.json"));
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", CodexCliSession.BuildArguments(
            new RoleSpec(VendorRole.Scout, "scout"), selection, null, "schema.json", "result.json"));
    }

    private static void AssertNoNetworkDisable(IEnumerable<string> arguments)
    {
        Assert.DoesNotContain("--no-network", arguments);
        Assert.DoesNotContain("--disable-network", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("network_access=disabled", StringComparison.Ordinal));
    }
}
