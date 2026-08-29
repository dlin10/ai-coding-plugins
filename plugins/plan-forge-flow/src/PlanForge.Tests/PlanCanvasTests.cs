using System.Text.Json;
using System.Text.RegularExpressions;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The canvas route: <c>forge.plan.show</c> and the <c>ui://</c> document it renders into. Drives
/// real git in a throwaway repository, like <see cref="ConfirmPlanTests"/>; no model calls.
/// </summary>
public sealed class PlanCanvasTests : IDisposable
{
    private const string Plan =
        """
        # Title

        ## Approach

        1. First task.
        2. Second task.
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;

    public PlanCanvasTests()
    {
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task The_plan_comes_back_whole_with_the_drift_beside_it()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "edited after the baseline\n", ct);

        var view = await ShowAsync(run.RunId, ct);

        Assert.Equal(Plan, view.Plan);
        Assert.Equal(["tracked.txt"], view.DriftedFiles);
        Assert.Equal(run.RunId, view.RunId);
        Assert.False(view.Approved);
    }

    /// <summary>
    /// The whole point of the tool is that it is inert. Approval is still
    /// <c>forge.plan.confirm</c>'s alone — see docs/adr/0003 — so showing a plan must not write one.
    /// </summary>
    /// <remarks>
    /// <c>PLAN.md</c> is no longer written only at approval: a review round writes it too. This run
    /// has had no round, so an existing file here could only have come from the call under test —
    /// which is exactly what makes the assertion still worth making.
    /// </remarks>
    [Fact]
    public async Task Showing_a_plan_approves_nothing_and_writes_no_plan_file()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        await ShowAsync(run.RunId, ct);

        Assert.False(run.ReadState().Approved);
        Assert.False(File.Exists(Path.Combine(run.Path, "PLAN.md")));
    }

    /// <summary>
    /// The host frames this document under a CSP that allows no origin the resource did not
    /// declare, and this one declares none. A stylesheet, font or script pulled from anywhere would
    /// be dropped there and leave the plan unreadable, so the guard is against reintroducing one.
    /// </summary>
    [Fact]
    public void The_canvas_document_reaches_for_no_external_origin()
    {
        var html = PlanCanvas.Plan();

        Assert.DoesNotContain("//fonts.googleapis.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Regex.Matches(html, """(src|href)\s*=\s*["']\s*(https?:)?//""", RegexOptions.IgnoreCase));
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything the canvas renders arrives over the postMessage bridge, and the tool result is
    /// the only source it has for the plan. A document that never listens for it renders the
    /// waiting state forever.
    /// </summary>
    [Fact]
    public void The_canvas_document_speaks_the_bridge_it_is_rendered_behind()
    {
        var html = PlanCanvas.Plan();

        Assert.Contains("ui/initialize", html, StringComparison.Ordinal);
        Assert.Contains("ui/notifications/tool-result", html, StringComparison.Ordinal);
        Assert.Contains("ui/notifications/size-changed", html, StringComparison.Ordinal);
    }

    private async Task<RunDirectory> StartRunAsync(CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await _git.OutputAsync(["add", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);

        var run = RunDirectory.Create(_repo, "20260101-000000-abcdef");
        var baseline = await Baseline.CaptureAsync(_git, ct);
        run.WriteBaseline(baseline);
        run.WriteState(new RunState(run.RunId, _repo, "Canvas", DateTimeOffset.Now,
            ReviewRounds: 2, ReviewRoundCap: 5, BaselineHead: baseline.Head));

        return run;
    }

    private async Task<PlanViewResult> ShowAsync(string runId, CancellationToken ct)
    {
        var json = await ForgeTools.ShowPlan(_repo, runId, Plan, ct);
        return JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanViewResult)!;
    }
}
