using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// A codex Worker whose commands never started answered without reading the repository; see
/// docs/adr/0031. The stream lines are recorded from codex-cli 0.162.1 on 2026-10-12, whose sandbox
/// setup failed before every command of run 20261012-163837-6fc7b1.
/// </summary>
public sealed class CodexShellTests
{
    private const string UNSTARTED =
        """{"type":"item.completed","item":{"id":"item_2","type":"command_execution","command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command 'cmd /c echo probe-ok .'","aggregated_output":"Failed to create unified exec process: helper_unknown_error: setup refresh had errors","exit_code":-1,"status":"failed"}}""";

    private const string STARTED =
        """{"type":"item.completed","item":{"id":"item_3","type":"command_execution","command":"pwsh -Command 'Get-Content PLAN.md'","aggregated_output":"# Plan","exit_code":0,"status":"completed"}}""";

    // A command that ran and failed is still a command the shell started.
    private const string RAN_AND_FAILED =
        """{"type":"item.completed","item":{"id":"item_4","type":"command_execution","command":"pwsh -Command 'rg missing'","aggregated_output":"","exit_code":1,"status":"failed"}}""";

    [Theory]
    [InlineData("Critic")]
    [InlineData("Scout")]
    [InlineData("Builder")]
    public void A_run_whose_every_command_failed_to_start_is_refused_in_codex_s_words(string role)
    {
        var session = Session(Enum.Parse<VendorRole>(role));
        Observe(session, UNSTARTED, UNSTARTED);

        var failure = session.JudgeShell();

        Assert.NotNull(failure);
        Assert.IsAssignableFrom<VendorException>(failure);
        Assert.Contains($"could not start a shell for the {role}", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none of the 2 command(s) it tried started", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"Failed to create unified exec process: helper_unknown_error: setup refresh had errors\"",
                        failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("Retry once codex can start a command.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox", failure.Message, StringComparison.Ordinal);
        Assert.Equal("helper_unknown_error", failure.ErrorCode);
    }

    [Theory]
    [InlineData(STARTED)]
    [InlineData(RAN_AND_FAILED)]
    public void A_run_that_started_one_command_had_a_shell(string started)
    {
        var session = Session(VendorRole.Critic);
        Observe(session, UNSTARTED, started);

        Assert.Null(session.JudgeShell());
    }

    [Fact]
    public void A_run_that_tried_no_command_says_nothing_about_its_shell() =>
        Assert.Null(Session(VendorRole.Scout).JudgeShell());

    [Theory]
    [InlineData("Failed to create unified exec process: helper_unknown_error: setup refresh had errors", "helper_unknown_error")]
    [InlineData("Failed to create unified exec process: program not found", null)]
    [InlineData("Failed to create unified exec process: ", null)]
    [InlineData("Unified exec process failed: helper_unknown_error", null)]
    public void Only_a_bare_identifier_after_the_launch_failure_is_an_error_code(string diagnostic, string? expected) =>
        Assert.Equal(expected, CodexCliSession.ErrorCode(diagnostic));

    private static CodexCliSession Session(VendorRole role) =>
        new(new RoleSpec(role, "contract"), new Selection("gpt-test", null), null);

    private static void Observe(CodexCliSession session, params string[] lines)
    {
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            session.Observe(document.RootElement);
        }
    }
}
