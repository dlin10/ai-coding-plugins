using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>
/// Runs the real `codex` CLI: costs money and needs a signed-in CLI, so every test here is traited
/// for filtering with --filter Category!=Integration. Replaces the deleted App Server test file —
/// see docs/adr/0012-reach-codex-through-exec.md and
/// docs/adr/0013-strip-the-store-alias-from-the-codex-path.md.
/// </summary>
public sealed class CodexCliTests
{
    private static readonly string[] Verdicts = ["approve", "revise"];

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

    public CodexCliTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The step-8 criterion for the critic half, now against a strict output schema: a live
    /// catalogue, a real turn, and a valid object out of it — plus the stronger claim the old retry
    /// budget assertion becomes once there is no retry loop: a malformed schema is refused by the
    /// API before a token is spent, so a turn is never re-attempted.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Critic_probes_the_full_catalogue_and_returns_a_valid_critique_in_one_attempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var vendor = new CodexCliVendor(Environment.CurrentDirectory);

        var readiness = await vendor.ProbeAsync(ct);
        Assert.True(readiness.Available, $"codex unavailable: {readiness.Detail}");
        _output.WriteLine($"catalogue: {string.Join(", ", vendor.Catalog.Models.Select(m => m.Id))}");

        Assert.Equal(["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini"],
                     vendor.Catalog.Models.Select(model => model.Id));
        Assert.True(vendor.Catalog.Models[0].IsDefault);
        Assert.Equal("low", vendor.Catalog.Models[0].DefaultEffort);

        await using var session = await vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, CriticPrompt),
            new Selection(vendor.Catalog.Models[0].Id, "low"), null, ct);

        var started = 0;
        var counting = Task.Run(async () =>
        {
            await foreach (var e in session.Events.WithCancellation(ct))
                if (e.Kind is VendorEventKind.Started) started++;
        }, ct);

        var critique = await session.RunAsync(PlanWithAHole, Schemas.Critique, ct);
        await session.DisposeAsync();
        await counting;

        _output.WriteLine($"verdict={critique.Verdict} findings={critique.Findings.Count}");

        Assert.Contains(critique.Verdict, Verdicts);
        Assert.NotEmpty(critique.Summary);
        Assert.Equal(1, started);
    }

    /// <summary>
    /// The builder half: a thread that survives its session, because the MCP surface is stateless
    /// and a builder's continuity has to be carried across separate tool calls.
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
            var vendor = new CodexCliVendor(workspace);
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
            var resumed = await second.RunAsync(
                "Report status done with no files changed and the summary 'second turn'.",
                Schemas.BuildResult, ct);

            _output.WriteLine($"second: {resumed.Status} — {resumed.Summary}");
            Assert.Equal("done", resumed.Status);
            Assert.Equal(token, second.ResumeToken);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The reason this whole run exists (issue #58): on a machine whose only `pwsh` is a Store
    /// alias, codex could not start a shell at all, and this assertion is exactly what failed
    /// without the PATH repair — see docs/adr/0013-strip-the-store-alias-from-the-codex-path.md.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Builder_runs_a_shell_command_and_reports_its_output()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var workspace = Path.Combine(Path.GetTempPath(), "planforge-codex", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);

        try
        {
            var vendor = new CodexCliVendor(workspace);
            var role = new RoleSpec(VendorRole.Builder, "You implement one task at a time.");
            var selection = new Selection("gpt-5.6-terra", "low");

            await using var session = await vendor.StartAsync(role, selection, null, ct);

            var events = new List<VendorEvent>();
            var collecting = Task.Run(async () =>
            {
                await foreach (var e in session.Events.WithCancellation(ct)) events.Add(e);
            }, ct);

            var result = await session.RunAsync(
                "Run the shell command `echo hello` and report exactly what it printed as your " +
                "summary. Report status done with no files changed.",
                Schemas.BuildResult, ct);

            await session.DisposeAsync();
            await collecting;

            _output.WriteLine($"result: {result.Status} — {result.Summary}");
            foreach (var e in events) _output.WriteLine($"event: {e.Kind} {e.Text}");

            Assert.Equal("done", result.Status);
            Assert.Contains(events, e => e.Kind is VendorEventKind.ToolResult
                && e.Fields is not null
                && e.Fields.Any(field => field.Name == "exitCode" && field.Value == "0"));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
