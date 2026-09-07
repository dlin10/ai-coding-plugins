using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The fix step never talks to git: the findings arrive from the orchestrator, already filtered
/// against the plan, so a bare temp folder is workspace enough.
/// </summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public ReviewFixTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Findings_reach_the_builder_under_the_fix_heading()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed"));

        var result = await NewFix(builder).FixAsync(NewRun(), new Selection("builder-model", "low"),
                                                    "- **major** tracked.txt — fix it", null, ct);

        Assert.Equal("done", result.Status);
        var session = Assert.Single(builder.Sessions);
        Assert.Equal(VendorRole.Builder, session.Role.Role);
        Assert.Contains("# Fix these review findings", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("fix it", session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deferred_findings_land_in_the_review_log_with_their_reason()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed"));
        var run = NewRun(reviewRounds: 5, codeReviewRounds: 1);

        await NewFix(builder).FixAsync(run, new Selection("builder-model", null),
                                       "- **major** tracked.txt — fix it",
                                       "- staged coverage — the approved plan excludes it", ct);

        var log = run.ReadReviewLog();
        Assert.Contains("## Round 6 fixes", log, StringComparison.Ordinal);
        Assert.Contains("Deferred by the orchestrator", log, StringComparison.Ordinal);
        Assert.Contains("the approved plan excludes it", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deferred_only_findings_are_logged_without_starting_a_builder()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        var run = NewRun(reviewRounds: 5, codeReviewRounds: 1);

        var result = await NewFix(builder).FixAsync(run, new Selection("builder-model", null),
                                                    " \n",
                                                    "- staged coverage — the approved plan excludes it", ct);

        Assert.Equal("done", result.Status);
        Assert.Equal("passed", result.Verification.Outcome);
        Assert.Empty(builder.Sessions);
        var log = run.ReadReviewLog();
        Assert.Contains("## Round 6 fixes", log, StringComparison.Ordinal);
        Assert.Contains("the approved plan excludes it", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_requires_an_approved_plan()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        var run = NewRun(approved: false);

        await Assert.ThrowsAsync<NotApprovedException>(() => NewFix(builder).FixAsync(
            run, new Selection("builder-model", null), "- fix it", null, ct));

        Assert.Empty(builder.Sessions);
    }

    [Fact]
    public async Task Sensitive_findings_are_guarded_before_the_builder_starts()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");

        await Assert.ThrowsAsync<SensitiveContentException>(() => NewFix(builder).FixAsync(
            NewRun(), new Selection("builder-model", null),
            "api_key: Abcdefghijklmnop1234+", null, ct));

        Assert.Empty(builder.Sessions);
    }

    [Fact]
    public async Task Sensitive_deferred_findings_are_guarded_before_the_builder_starts()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");

        await Assert.ThrowsAsync<SensitiveContentException>(() => NewFix(builder).FixAsync(
            NewRun(), new Selection("builder-model", null),
            "- fix it", "api_key: Abcdefghijklmnop1234+", ct));

        Assert.Empty(builder.Sessions);
    }

    [Fact]
    public async Task A_fresh_fix_builder_does_not_receive_a_foreign_token_and_records_its_vendor()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "new-token");
        var run = NewRun(builderVendor: "claude", builderSessionId: "foreign-token");

        await NewFix(builder).FixAsync(run, new Selection("builder-model", "low"), "- fix it", null, ct);

        Assert.Null(Assert.Single(builder.Sessions).StartedWithResumeToken);
        Assert.Equal("new-token", run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_fresh_null_fix_token_clears_the_foreign_token_and_stays_fresh()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        var run = NewRun(builderVendor: "claude", builderSessionId: "foreign-token");

        await NewFix(builder).FixAsync(run, new Selection("builder-model", null), "- fix it", null, ct);

        Assert.Null(Assert.Single(builder.Sessions).StartedWithResumeToken);
        Assert.Equal(string.Empty, run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_second_fix_resumes_the_builder_session()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "next-token");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "final-token");
        var run = NewRun(builderVendor: "codex", builderSessionId: "existing-token");

        var fix = NewFix(builder);
        await fix.FixAsync(run, new Selection("builder-model", "low"), "- first fix", null, ct);
        await fix.FixAsync(run, new Selection("builder-model", "low"), "- second fix", null, ct);

        Assert.Equal(2, builder.Sessions.Count);
        Assert.Equal("existing-token", builder.Sessions[0].StartedWithResumeToken);
        Assert.Equal("next-token", builder.Sessions[1].StartedWithResumeToken);
        Assert.Equal("final-token", run.ReadState().BuilderSessionId);
    }

    [Fact]
    public async Task The_fix_round_lands_in_the_flow_log_with_the_builder_outcome()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed the guard"));
        var run = NewRun(reviewRounds: 5, codeReviewRounds: 1);

        await NewFix(builder).FixAsync(run, new Selection("builder-model", null),
                                       "- **major** tracked.txt — fix it",
                                       "- staged coverage — the approved plan excludes it", ct);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Fixes — round 1", flow, StringComparison.Ordinal);
        Assert.Contains("fix it", flow, StringComparison.Ordinal);
        Assert.Contains("Deferred by the orchestrator", flow, StringComparison.Ordinal);
        Assert.Contains("Status: done", flow, StringComparison.Ordinal);
        Assert.Contains("fixed the guard", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run-wide gates are the fix round's gates, so the fix is judged by them the way a task is
    /// judged by its own: a failing one rewrites the status, withholds nothing (there is no task
    /// count to withhold) and is put in front of the next fix.
    /// </summary>
    [Fact]
    public async Task A_fix_round_runs_the_executable_run_wide_gates_and_a_failing_one_marks_the_fix_gate_failed()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed"));
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed again"));
        var run = NewRun(plan: "## Gates\n\n1. **G1.** `Write-Output g1` passes.\n2. **G2.** `cmd /c exit 3` passes.\n\n## Approach\n\n1. Task.\n");
        var fix = NewFix(builder);

        var first = await fix.FixAsync(run, new Selection("builder-model", null), "- fix it", null, ct);

        Assert.Equal("gate_failed", first.Status);
        Assert.Equal("failed", first.Gate?.Outcome);
        Assert.Equal("G1, G2", first.Gate?.Label);
        Assert.Equal(3, first.Gate?.ExitCode);
        Assert.Contains("cmd /c exit 3", run.ReadState().PendingGateFailure, StringComparison.Ordinal);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("Status: gate_failed", flow, StringComparison.Ordinal);
        Assert.Contains("Gates G1, G2: failed", flow, StringComparison.Ordinal);

        await fix.FixAsync(run, new Selection("builder-model", null), "- fix it again", null, ct);

        Assert.Contains("did not pass its gate", builder.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("exited 3", builder.Sessions[1].PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fix_round_whose_run_wide_gates_all_pass_keeps_the_builder_status()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("unavailable", "sandbox"), "fixed"));
        var run = NewRun(plan: "## Gates\n\n1. **G1.** `Write-Output g1` passes.\n\n## Approach\n\n1. Task.\n");

        var result = await NewFix(builder).FixAsync(run, new Selection("builder-model", null), "- fix it", null, ct);

        Assert.Equal("done", result.Status);
        Assert.Equal("passed", result.Gate?.Outcome);
        Assert.Equal("G1", result.Gate?.Label);
        Assert.Null(run.ReadState().PendingGateFailure);
    }

    [Fact]
    public async Task A_plan_without_run_wide_gates_leaves_the_fix_on_the_builder_s_word()
    {
        var ct = CancellationToken.None;
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "fixed"));
        var run = NewRun();

        var result = await NewFix(builder).FixAsync(run, new Selection("builder-model", null), "- fix it", null, ct);

        Assert.Equal("done", result.Status);
        Assert.Equal("not_executable", result.Gate?.Outcome);
        Assert.Contains("Gate: not executable — the plan states no gate", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    private ReviewFix NewFix(RecordingVendor builder) =>
        new(builder, new PromptLibrary(RepositoryPrompts()));

    private RunDirectory NewRun(bool approved = true,
                                int reviewRounds = 0,
                                int codeReviewRounds = 0,
                                string builderVendor = "",
                                string builderSessionId = "",
                                string plan = "## Approach\n\n1. Task.\n")
    {
        var run = RunDirectory.Create(_workspace, "review-fix");
        run.WritePlan(plan);
        run.WriteState(new RunState("review-fix", _workspace, "Text", DateTimeOffset.Now, reviewRounds, 5,
            Approved: approved, BuilderSessionId: builderSessionId, BuilderVendor: builderVendor,
            CodeReviewRounds: codeReviewRounds, CodeReviewRoundCap: 3));
        return run;
    }

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }
}
