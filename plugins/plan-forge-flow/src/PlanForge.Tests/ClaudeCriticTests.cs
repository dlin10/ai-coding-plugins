using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ClaudeCriticTests
{
    private static readonly string[] Verdicts = ["approve", "revise"];
    private static readonly string[] Severities = ["blocker", "major", "minor"];

    private const string CriticPrompt =
        "You review implementation plans. Judge only the plan you are given. Report every gap you " +
        "find as a finding, then return your verdict through the structured output tool.";

    // Deliberately holed: no error handling, no verification, and an undefined retention rule.
    private const string PlanWithAHole =
        """
        # Plan: nightly export

        1. Read every row from the orders table.
        2. Write them to a CSV on the shared drive.
        3. Delete rows older than the retention window.
        """;

    /// <summary>
    /// Runs the real `claude` CLI: costs money and needs a logged-in CLI, so it is traited for
    /// filtering with --filter Category!=Integration.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Critic_returns_a_valid_critique()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var vendor = new ClaudeCliVendor();

        var readiness = await vendor.ProbeAsync(ct);
        Assert.True(readiness.Available, $"claude CLI unavailable: {readiness.Detail}");

        await using var session = await vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, CriticPrompt), new Selection("sonnet", "low"), resumeToken: null, ct);

        var critique = await session.RunAsync(PlanWithAHole, Schemas.Critique, ct);

        Assert.Contains(critique.Verdict, Verdicts);
        Assert.NotEmpty(critique.Summary);
        Assert.All(critique.Findings, finding =>
        {
            Assert.Contains(finding.Severity, Severities);
            Assert.NotEmpty(finding.What);
        });
    }

    [Fact]
    public void Critic_sessions_do_not_resume()
    {
        var session = new ClaudeCliSession(
            new RoleSpec(VendorRole.Critic, CriticPrompt), new Selection("sonnet", null), null);

        Assert.False(session.CanResume);
    }

    [Fact]
    public void Builder_sessions_resume()
    {
        var session = new ClaudeCliSession(
            new RoleSpec(VendorRole.Builder, "You implement plan tasks."), new Selection("sonnet", null), null);

        Assert.True(session.CanResume);
    }
}
