using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Infrastructure;
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
    /// A gate variable is often a connection string. Status names it, as the resume card does, and
    /// neither the result nor the run's log — where every result is written — carries its value.
    /// Server-only state stays out of the status too — docs/adr/0042.
    /// </summary>
    [Fact]
    public async Task Status_names_the_gate_environment_without_its_values_and_leaves_server_state_out()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: Plan,
                                     gateEnvironment: new Dictionary<string, string> { ["CD_TEST_SQL_CONN"] = "Server=db;Password=hunter2" });

        var json = await ForgeTools.Status(SessionRoots.None, _repo, run.RunId, ct);
        using var status = JsonDocument.Parse(json);
        var saved = status.RootElement.GetProperty("run");

        Assert.Equal(["CD_TEST_SQL_CONN"], saved.GetProperty("gateEnvironment").EnumerateArray().Select(name => name.GetString()));
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        // Shared read: forge.begin's catalogue probes may still be appending to the log.
        Assert.DoesNotContain("hunter2", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        foreach (var hidden in new[] { "taskChanges", "builderSessionId", "builderVendor", "builderSessionScope", "workerTools",
                                       "builderRoots", "startedAt", "baselineHead" })
            Assert.False(saved.TryGetProperty(hidden, out _), hidden);
        Assert.True(saved.GetProperty("approved").GetBoolean());
    }

    /// <summary>
    /// The Orchestrator reads the drift from <c>forge.status</c> to show the user before asking;
    /// repeated with the decision it arrives after the only moment it could matter — docs/adr/0042.
    /// </summary>
    [Fact]
    public async Task The_decision_answers_with_itself_and_leaves_drift_to_status()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "edited after the baseline\n", ct);

        using var answer = JsonDocument.Parse(await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: Plan));

        Assert.Equal(["approved", "taskCount"], answer.RootElement.EnumerateObject().Select(property => property.Name));
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

    /// <summary>
    /// The gate settings arrive with the approval because that is when the gates are known: the
    /// plan they belong to did not exist at <c>forge.begin</c>. Omission preserves them on re-approval;
    /// explicit empty values clear settings the plan no longer needs.
    /// </summary>
    [Fact]
    public async Task The_approval_records_settings_and_reapproval_preserves_omitted_settings()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        var root = Path.GetFullPath(Path.Combine(_repo, "..", "eShop"));

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct,
                                     new Dictionary<string, string> { ["CD_TEST_SQL_CONN"] = "Server=." }, [root], plan: Plan);

        var state = run.ReadState();
        Assert.True(state.Approved);
        Assert.Equal("Server=.", state.GateEnvironment!["CD_TEST_SQL_CONN"]);
        Assert.Equal([root], state.BuilderRoots);

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: Plan);

        state = run.ReadState();
        Assert.Equal("Server=.", state.GateEnvironment!["CD_TEST_SQL_CONN"]);
        Assert.Equal([root], state.BuilderRoots);
    }

    [Fact]
    public async Task A_relative_builder_root_is_refused_before_anything_is_written()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, null, ["../eShop"], plan: Plan));

        Assert.False(run.ReadState().Approved);
        Assert.False(File.Exists(run.PlanPath));
    }

    [Fact]
    public async Task Builder_brief_plan_changed_brief_reapproval_clears_session_and_preserves_progress()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await ConfirmAsync(run.RunId, approved: true, ct);
        run.WriteState(run.ReadState() with { BuilderSessionId = "builder-session", TasksCompleted = 1 });

        var changedBrief = Plan.Replace("# Title", "# Changed title", StringComparison.Ordinal);
        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: changedBrief);

        var state = run.ReadState();
        Assert.Equal(string.Empty, state.BuilderSessionId);
        Assert.Equal(1, state.TasksCompleted);
    }

    [Fact]
    public async Task Builder_brief_plan_task_only_reapproval_preserves_session_and_progress()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await ConfirmAsync(run.RunId, approved: true, ct);
        run.WriteState(run.ReadState() with { BuilderSessionId = "builder-session", TasksCompleted = 1 });

        var changedTask = Plan.Replace("First task.", "Updated first task.", StringComparison.Ordinal);
        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: changedTask);

        var state = run.ReadState();
        Assert.Equal("builder-session", state.BuilderSessionId);
        Assert.Equal(1, state.TasksCompleted);
    }

    /// <summary>
    /// Since forge.plan.patch the orchestrator holds the plan by reference: a run of 5d B3c wrote
    /// it once and patched it thirteen times, and after a compaction it no longer had the text to
    /// send back. Omitted, the plan is the one on disk — see docs/adr/0038 — and it is approved as
    /// it stands, byte for byte.
    /// </summary>
    [Fact]
    public async Task Without_a_plan_the_approval_is_of_PLAN_md_as_it_stands()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        run.WritePlan(Plan);
        var before = File.ReadAllBytes(run.PlanPath);

        var json = await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.ApproveResult)!;

        Assert.True(result.Approved);
        Assert.Equal(2, result.TaskCount);
        Assert.True(run.ReadState().Approved);
        Assert.Equal(before, File.ReadAllBytes(run.PlanPath));
    }

    /// <summary>
    /// The Text-profile route: the hash forge.plan.write answered with is the plan the user was
    /// shown, and confirming with it approves that plan.
    /// </summary>
    [Fact]
    public async Task The_hash_a_write_answers_with_confirms_the_plan_it_wrote()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        var written = JsonSerializer.Deserialize(await ForgeTools.WritePlan(SessionRoots.None, _repo, run.RunId, Plan, ct),
                                                 ForgeToolJson.Default.PlanWriteResult)!;

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, planHash: written.Outline!.PlanHash);

        Assert.True(run.ReadState().Approved);
    }

    /// <summary>
    /// What the user saw is named by its hash, so a plan that moved after they saw it is refused
    /// rather than approved in their name — whether it is the file or the text passed.
    /// </summary>
    [Theory]
    [InlineData(false, "PLAN.md hashes to")]
    [InlineData(true, "the plan passed hashes to")]
    public async Task A_plan_hash_other_than_the_plans_is_refused_and_writes_nothing(bool passed, string reason)
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        run.WritePlan(Plan);
        var shown = PlanOutline.Hash(Plan);
        var moved = Plan.Replace("First task.", "Moved first task.", StringComparison.Ordinal);
        if (!passed) run.WritePlan(moved);
        var plan = File.ReadAllBytes(run.PlanPath);
        var state = File.ReadAllBytes(Path.Combine(run.Path, "state.json"));

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: passed ? moved : null, planHash: shown));

        Assert.Contains($"planHash {shown}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{reason} {PlanOutline.Hash(moved)}", error.Message, StringComparison.Ordinal);
        Assert.Equal(plan, File.ReadAllBytes(run.PlanPath));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(run.Path, "state.json")));
    }

    /// <summary>
    /// A change outside the tasks keeps them built, and the server does not guess which of them it
    /// invalidates. When the user says one does, the approval sends the build back to that task.
    /// </summary>
    [Fact]
    public async Task An_approval_with_rebuildFrom_sends_the_build_back_to_that_task()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await ConfirmAsync(run.RunId, approved: true, ct);
        run.WriteState(run.ReadState() with
        {
            TasksCompleted = 2, BuilderSessionId = "builder-session", BuilderSessionScope = "task 2",
            TaskChanges = [new TaskChange(1, ["first.cs"]), new TaskChange(2, ["second.cs"])]
        });

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct, plan: Plan, rebuildFrom: 2);

        var state = run.ReadState();
        Assert.True(state.Approved);
        Assert.Equal(1, state.TasksCompleted);
        Assert.Equal(1, Assert.Single(state.TaskChanges!).TaskNumber);
        Assert.Equal(string.Empty, state.BuilderSessionId);
        Assert.Contains("The approval asks to rebuild from task 2. Build progress was reset from 2 completed tasks to 1, " +
                        "and the builder will start from task 2.", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 0, "names no built task: pass a task from 1 to 2")]
    [InlineData(true, 3, "names no built task: pass a task from 1 to 2")]
    [InlineData(false, 1, "does not accept rebuildFrom")]
    public async Task A_rebuildFrom_that_names_no_built_task_is_refused_and_writes_nothing(bool approved, int task, string reason)
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        await ConfirmAsync(run.RunId, approved: true, ct);
        run.WriteState(run.ReadState() with { Approved = false, TasksCompleted = 2 });
        var state = File.ReadAllBytes(Path.Combine(run.Path, "state.json"));

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, approved, ct, plan: Plan, rebuildFrom: task));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(run.Path, "state.json")));
    }

    [Fact]
    public async Task Without_a_plan_or_a_PLAN_md_the_call_is_refused()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, true, ct));

        Assert.Contains("forge.plan.write", error.Message, StringComparison.Ordinal);
        Assert.False(run.ReadState().Approved);
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
        var json = await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, runId, approved, ct, plan: Plan);
        return JsonSerializer.Deserialize(json, ForgeToolJson.Default.ApproveResult)!;
    }
}
