using PlanForge.Vendors;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

public sealed class CursorAgentTests
{
    private const string CriticPrompt =
        "You review implementation plans. Report every gap you find as a finding.";

    private const string PlanWithAHole =
        """
        # Plan: nightly export

        1. Read every row from the orders table.
        2. Write them to a CSV on the shared drive.
        3. Delete rows older than the retention window.
        """;

    private readonly ITestOutputHelper _output;

    public CursorAgentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Parses_the_live_model_catalogue()
    {
        var models = CursorAgentVendor.ParseModels([
            "Available models",
            string.Empty,
            "auto - Auto (default)",
            "gpt-5.3-codex-high - Codex 5.3 High",
            "not a model line"
        ]);

        Assert.Equal(["auto", "gpt-5.3-codex-high"], models.Select(m => m.Id));
    }

    /// <summary>
    /// The step-10 criterion: a vendor with no native schema still returns a valid object, and it
    /// does so within the retry budget.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Critic_returns_a_valid_critique_within_the_retry_budget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var vendor = new CursorAgentVendor();

        var readiness = await vendor.ProbeAsync(ct);
        Assert.True(readiness.Available, $"cursor-agent unavailable: {readiness.Detail}");
        Assert.NotEmpty(vendor.Catalog.Models);
        _output.WriteLine($"catalogue: {readiness.Detail}");

        await using var session = await vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, CriticPrompt), new Selection("auto", null), null, ct);

        var attempts = 0;
        var counting = Task.Run(async () =>
        {
            await foreach (var e in session.Events.WithCancellation(ct))
                if (e.Kind is VendorEventKind.Started) attempts++;
        }, ct);

        var critique = await session.RunAsync(PlanWithAHole, Schemas.Critique, ct);
        await session.DisposeAsync();
        await counting;

        _output.WriteLine($"verdict={critique.Verdict} findings={critique.Findings.Count} attempts={attempts}");

        Assert.Contains(critique.Verdict, (string[])["approve", "revise"]);
        Assert.NotEmpty(critique.Summary);
        Assert.True(attempts <= 2, $"took {attempts} attempts");
    }
}
