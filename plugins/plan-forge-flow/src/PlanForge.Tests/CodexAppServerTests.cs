using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

public sealed class CodexAppServerTests
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

    public CodexAppServerTests(ITestOutputHelper output) => _output = output;

    /// <summary>Efforts arrive as objects here, unlike every other catalogue in the system.</summary>
    [Fact]
    public void Parses_the_live_model_catalogue()
    {
        using var page = JsonDocument.Parse(
            """
            {
              "data": [
                { "id": "gpt-5.6-sol",
                  "supportedReasoningEfforts": [
                    { "reasoningEffort": "low", "description": "Fast responses" },
                    { "reasoningEffort": "max", "description": "Maximum reasoning depth" }
                  ] },
                { "id": "gpt-5.6-terra", "supportedReasoningEfforts": [] },
                { "displayName": "no id at all" }
              ],
              "nextCursor": null
            }
            """);

        var models = CodexAppServerVendor.ParseModels(page.RootElement);

        Assert.Equal(["gpt-5.6-sol", "gpt-5.6-terra"], models.Select(model => model.Id));
        Assert.Equal(["low", "max"], models[0].Efforts);
        Assert.Empty(models[1].Efforts);
    }

    [Fact]
    public void Refuses_an_unknown_vendor_id()
    {
        Assert.IsType<ClaudeCliVendor>(VendorFactory.Create(null, "."));
        Assert.IsType<CodexAppServerVendor>(VendorFactory.Create("codex", "."));
        Assert.Throws<VendorException>(() => VendorFactory.Create("grok", "."));
    }

    /// <summary>
    /// The step-8 criterion for the critic half: a live catalogue, a real turn, and a valid object
    /// out of a vendor that has no schema field.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Critic_returns_a_valid_critique_within_the_retry_budget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var vendor = new CodexAppServerVendor(Environment.CurrentDirectory);

        var readiness = await vendor.ProbeAsync(ct);
        Assert.True(readiness.Available, $"codex unavailable: {readiness.Detail}");
        Assert.NotEmpty(vendor.Catalog.Models);
        _output.WriteLine($"catalogue: {string.Join(", ", vendor.Catalog.Models.Select(m => m.Id))}");

        await using var session = await vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, CriticPrompt),
            new Selection(vendor.Catalog.Models[0].Id, "low"), null, ct);

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
        Assert.True(attempts <= SchemaInPrompt.MaxAttempts, $"took {attempts} attempts");
    }

    /// <summary>
    /// The builder half: a thread that survives its session, because the MCP surface is stateless
    /// and a builder's continuity has to be carried across separate tool calls. A thread is only
    /// resumable once a turn has been recorded against it, so the first session has to do work.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Builder_thread_survives_the_session_that_opened_it()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var workspace = Path.Combine(Path.GetTempPath(), "planforge-codex", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);

        try
        {
            var vendor = new CodexAppServerVendor(workspace);
            var role = new RoleSpec(VendorRole.Builder, "You implement one task at a time.");
            var selection = new Selection("gpt-5.6-terra", "low");

            string? token;
            await using (var first = await vendor.StartAsync(role, selection, null, ct))
            {
                var result = await first.RunAsync(
                    "The task is already done. Report status done with no files changed and the summary 'first turn'.",
                    Schemas.BuildResult, ct);

                _output.WriteLine($"first: {result.Status} — {result.Summary}");
                token = first.ResumeToken;
                Assert.False(string.IsNullOrEmpty(token), "a builder session offered no resume token");
            }

            await using var second = await vendor.StartAsync(role, selection, token, ct);
            Assert.Equal(token, second.ResumeToken);
            _output.WriteLine($"resumed thread {token}");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
