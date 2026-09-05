using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class BuildTests : IDisposable
{
    private const string Plan =
        """
        # Toy plan

        ## Approach

        1. First task.
        2. Second task.
        """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_fresh_builder_does_not_receive_a_foreign_token_and_records_its_vendor()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "new-token");
        var run = NewRun("claude", "foreign-token");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", "low"),
                                                                                  CancellationToken.None);

        var session = Assert.Single(vendor.Sessions);
        Assert.Null(session.StartedWithResumeToken);
        Assert.Equal("new-token", run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_fresh_null_token_clears_the_foreign_token_and_the_next_call_stays_fresh()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        var run = NewRun("claude", "foreign-token");
        var build = new Build(vendor, new PromptLibrary(RepositoryPrompts()));

        await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);
        await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);

        Assert.Equal(2, vendor.Sessions.Count);
        Assert.All(vendor.Sessions, session => Assert.Null(session.StartedWithResumeToken));
        Assert.Equal(string.Empty, run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_builder_reuses_a_token_recorded_for_the_same_vendor()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "next-token");
        var run = NewRun("codex", "existing-token");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", "low"),
                                                                                  CancellationToken.None);

        Assert.Equal("existing-token", Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Equal("next-token", run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task The_build_outcome_lands_in_the_flow_log()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "first task built"));
        var run = NewRun("", "");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", null),
                                                                                  CancellationToken.None);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Task 1 of 2", flow, StringComparison.Ordinal);
        Assert.Contains("Status: done", flow, StringComparison.Ordinal);
        Assert.Contains("Verification: passed — the checks ran", flow, StringComparison.Ordinal);
        Assert.Contains("first task built", flow, StringComparison.Ordinal);
        Assert.Contains("- `tracked.txt`", flow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_builder_leaves_tasks_completed_unchanged_and_retries_the_same_task()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("blocked", [], new Verification("unavailable", "needs a decision"), "stuck"),
                        "retry-token");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"),
                        "next-token");
        var run = NewRun("codex", "");
        var build = new Build(vendor, new PromptLibrary(RepositoryPrompts()));

        var first = await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);
        Assert.Equal(0, first.TasksCompleted);
        Assert.Equal(0, run.ReadState().TasksCompleted);
        Assert.Equal("retry-token", run.ReadState().BuilderSessionId);

        var second = await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);
        Assert.Equal(1, second.TasksCompleted);
        Assert.Equal(1, run.ReadState().TasksCompleted);

        Assert.Equal(2, vendor.Sessions.Count);
        Assert.Contains("# Task 1 of 2", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        Assert.Contains("# Task 1 of 2", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_done_builder_advances_tasks_completed_by_one()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"),
                        "next-token");
        var run = NewRun("codex", "");

        var outcome = await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                                 new Selection("builder-model", "low"),
                                                                                                 CancellationToken.None);

        Assert.Equal(1, outcome.TasksCompleted);
        Assert.Equal(1, run.ReadState().TasksCompleted);
    }

    /// <summary>
    /// The run behind docs/adr/0015: a builder reporting `done` and `passed` while the tests its
    /// gate named were never written. The host runs the gate itself, and its exit code — not the
    /// builder's account — decides whether the task counts.
    /// </summary>
    [Fact]
    public async Task A_failing_gate_withholds_the_task_marks_it_gate_failed_and_briefs_the_retry()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "110 tests passed"), "done"), "token-1");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt", "tests.txt"], new Verification("passed", "124 tests passed"), "done"), "token-2");
        var run = NewRun("codex", "", GatedPlan("Write-Output 'suite ran'; cmd /c exit 2", "Write-Output second"));
        var build = new Build(vendor, new PromptLibrary(RepositoryPrompts()));

        var first = await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);

        Assert.Equal("gate_failed", first.Result?.Status);
        Assert.Equal("failed", first.Result?.Gate?.Outcome);
        Assert.Equal(2, first.Result?.Gate?.ExitCode);
        Assert.Contains("suite ran", first.Result?.Gate?.Output, StringComparison.Ordinal);
        Assert.Equal(0, first.TasksCompleted);
        Assert.Equal(0, run.ReadState().TasksCompleted);
        Assert.Equal("token-1", run.ReadState().BuilderSessionId);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("Status: gate_failed", flow, StringComparison.Ordinal);
        Assert.Contains("Gate: failed — `Write-Output 'suite ran'; cmd /c exit 2` exited 2", flow, StringComparison.Ordinal);
        Assert.Contains("suite ran", flow, StringComparison.Ordinal);

        // The plan is edited between the two calls, standing in for the fix a real builder makes:
        // the retry is the same task, with the gate's own words in front of the builder.
        run.WritePlan(GatedPlan("Write-Output 'suite ran'", "Write-Output second"));
        var second = await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);

        Assert.Contains("# Task 1 of 2", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("did not pass its gate", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("exited 2", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("suite ran", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Equal("done", second.Result?.Status);
        Assert.Equal(1, second.TasksCompleted);
        Assert.Null(run.ReadState().PendingGateFailure);
    }

    [Fact]
    public async Task A_passing_gate_counts_the_task_whatever_the_builder_said_about_its_own_verification()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("unavailable", "the sandbox denied dotnet"), "done"));
        var run = NewRun("", "", GatedPlan("Write-Output green", "Write-Output second"));

        var outcome = await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                                 new Selection("builder-model", null),
                                                                                                 CancellationToken.None);

        Assert.Equal("done", outcome.Result?.Status);
        Assert.Equal("passed", outcome.Result?.Gate?.Outcome);
        Assert.Equal(1, outcome.TasksCompleted);
        Assert.DoesNotContain("did not pass its gate", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        Assert.Contains("Gate: passed — `Write-Output green` exited 0", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gate_environment_from_the_run_state_reaches_the_gate()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "ran"), "done"));
        var run = NewRun("", "", GatedPlan("if ($env:CD_TEST_SQL_CONN -ne 'Server=.') { exit 6 }", "Write-Output second"),
                         new Dictionary<string, string> { ["CD_TEST_SQL_CONN"] = "Server=." });

        var outcome = await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                                 new Selection("builder-model", null),
                                                                                                 CancellationToken.None);

        Assert.Equal("passed", outcome.Result?.Gate?.Outcome);
        Assert.Equal(1, outcome.TasksCompleted);
    }

    [Fact]
    public async Task A_gate_that_is_a_condition_leaves_the_task_on_the_builder_s_word_and_says_so()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        var run = NewRun("", "", "## Approach\n\n1. **Task.** Do it. **Gate:** every new type carries a doc comment. (R1)\n");

        var outcome = await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                                 new Selection("builder-model", null),
                                                                                                 CancellationToken.None);

        Assert.Equal("done", outcome.Result?.Status);
        Assert.Equal("not_executable", outcome.Result?.Gate?.Outcome);
        Assert.Equal(1, outcome.TasksCompleted);
        Assert.Contains("Gate: not executable — the gate is a condition rather than a command",
                        File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_builder_has_its_gate_left_unrun()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("blocked", [], new Verification("unavailable", "stuck"), "stuck"));
        var run = NewRun("", "", GatedPlan("cmd /c exit 1", "Write-Output second"));

        var outcome = await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                                 new Selection("builder-model", null),
                                                                                                 CancellationToken.None);

        Assert.Equal("blocked", outcome.Result?.Status);
        Assert.Equal("not_run", outcome.Result?.Gate?.Outcome);
        Assert.Equal(0, outcome.TasksCompleted);
        Assert.Null(run.ReadState().PendingGateFailure);
    }

    [Fact]
    public async Task The_builder_roots_from_the_run_state_reach_the_builder_s_role()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "ran"), "done"));
        var run = NewRun("", "", Plan, builderRoots: [@"C:\Dev\eShopOnContainers"]);

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run, new Selection("builder-model", null),
                                                                                  CancellationToken.None);

        Assert.Equal([@"C:\Dev\eShopOnContainers"], Assert.Single(vendor.Sessions).Role.WritableRoots);
    }

    private static string GatedPlan(string firstGate, string secondGate) =>
        $"## Approach\n\n1. **First.** Do it. **Gate:** `{firstGate}` (R1)\n2. **Second.** Do it. **Gate:** `{secondGate}` (R2)\n";

    private RunDirectory NewRun(string builderVendor,
                                string builderSessionId,
                                string? plan = null,
                                IReadOnlyDictionary<string, string>? gateEnvironment = null,
                                IReadOnlyList<string>? builderRoots = null)
    {
        var run = RunDirectory.Create(_workspace, "build");
        run.WritePlan(plan ?? Plan);
        run.WriteState(new RunState("build", _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    Approved: true, BuilderSessionId: builderSessionId, BuilderVendor: builderVendor,
                                    GateEnvironment: gateEnvironment, BuilderRoots: builderRoots));
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
