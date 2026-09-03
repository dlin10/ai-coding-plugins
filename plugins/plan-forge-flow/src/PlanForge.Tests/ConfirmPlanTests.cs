using System.Text.Json;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The approval route, which since 0.8.0 is the only one. Drives real git in a throwaway
/// repository, like <see cref="BaselineTests"/>; no model calls, so it runs in the default suite.
/// </summary>
public sealed class ConfirmPlanTests : IDisposable
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

    public ConfirmPlanTests()
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
    public async Task A_confirmed_plan_is_recorded_with_its_tasks()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        var result = await ConfirmAsync(run.RunId, approved: true, ct);

        Assert.True(result.Approved);
        Assert.Equal(2, result.TaskCount);
        Assert.True(run.ReadState().Approved);
        Assert.Equal(Plan, run.ReadPlan());
    }

    [Fact]
    public async Task A_refused_plan_leaves_the_run_unapproved()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        var result = await ConfirmAsync(run.RunId, approved: false, ct);

        Assert.False(result.Approved);
        Assert.Equal(0, result.TaskCount);
        Assert.False(run.ReadState().Approved);
    }

    /// <summary>
    /// The field existed before this route did, and both approval paths used to return it empty
    /// whatever the tree looked like.
    /// </summary>
    [Fact]
    public async Task Drift_since_the_baseline_comes_back_with_the_decision()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "edited after the baseline\n", ct);

        var result = await ConfirmAsync(run.RunId, approved: true, ct);

        Assert.Equal(["tracked.txt"], result.DriftedFiles);
    }

    /// <summary>
    /// The orchestrator has to show the drift before it asks, so this is where drift has to be
    /// readable. Arriving with the decision would be arriving too late to affect it.
    /// </summary>
    [Fact]
    public async Task Status_reports_drift_before_anyone_has_been_asked()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "edited after the baseline\n", ct);

        var json = await ForgeTools.Status(SessionRoots.None, _repo, run.RunId, ct);
        var status = JsonSerializer.Deserialize(json, ForgeToolJson.Default.StatusResult)!;

        Assert.Equal(["tracked.txt"], status.DriftedFiles);
        Assert.False(status.Run.Approved);
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
        run.WriteState(new RunState(run.RunId, _repo, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5, BaselineHead: baseline.Head));

        return run;
    }

    private async Task<ApproveResult> ConfirmAsync(string runId, bool approved, CancellationToken ct)
    {
        var json = await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, runId, Plan, approved, ct);
        return JsonSerializer.Deserialize(json, ForgeToolJson.Default.ApproveResult)!;
    }
}
