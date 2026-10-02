using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class GateOwnershipTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Each_task_runs_its_own_gate_once_without_running_the_plan_wide_gate()
    {
        var run = NewRun("""
            ## Gates

            1. **G1.** `Set-Content full-gate.txt ran; if (!(Test-Path second.txt)) { exit 7 }` (R1, R2)

            ## Approach

            1. First. **Gate:** `Add-Content task-gates.txt first` (R1)
            2. Second. **Gate:** `Add-Content task-gates.txt second` (R2)
            """);
        var vendor = Builder();
        vendor.Enqueue(Delegated(), "second-token");
        var build = new Build(vendor, Prompts());

        var first = await build.NextAsync(run, new Selection("builder", null), CancellationToken.None);
        var second = await build.NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal(1, first.TasksCompleted);
        Assert.Equal(2, second.TasksCompleted);
        Assert.Equal("passed", first.Result?.Gate?.Outcome);
        Assert.Equal("Gate", second.Result?.Gate?.Label);
        Assert.Equal("unavailable", second.Result?.Verification.Outcome);
        Assert.Equal(["first", "second"], File.ReadAllLines(Path.Combine(_workspace, "task-gates.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace, "full-gate.txt")));
        Assert.Contains("# Task 2 of 2", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_task_artifact_fails_even_without_plan_gates_or_with_a_passing_one(bool hasPlanGate)
    {
        var planGates = hasPlanGate ? "## Gates\n\n1. **G1.** `Write-Output green` (R1)\n\n" : "";
        var run = NewRun(planGates + "## Approach\n\n1. Create required.txt. **Gate:** `if (!(Test-Path required.txt)) { exit 9 }` (R1)\n");
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "builder says checks passed"), "done"));

        var outcome = await new Build(vendor, Prompts()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal("gate_failed", outcome.Result?.Status);
        Assert.Equal(9, outcome.Result?.Gate?.ExitCode);
        Assert.Equal(0, outcome.TasksCompleted);
    }

    [Fact]
    public async Task A_delegated_gate_failure_retries_the_same_command_with_its_evidence_and_no_builder_gate_run()
    {
        var run = NewRun("""
            ## Approach

            1. Task. **Gate:** `Add-Content attempts.txt ran; if (!(Test-Path ready.txt)) { Write-Output 'task gate failed'; exit 7 }` (R1)
            """);
        var vendor = Builder();
        vendor.Enqueue(Delegated(), "retry-token");
        var build = new Build(vendor, Prompts());

        var first = await build.NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal("gate_failed", first.Result?.Status);
        Assert.Equal(7, first.Result?.Gate?.ExitCode);
        Assert.Equal(0, first.TasksCompleted);
        Assert.Contains("Do not run the task gate", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_workspace, "ready.txt"), "ready");

        var second = await build.NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal("first-token", vendor.Sessions[1].StartedWithResumeToken);
        Assert.Contains("task gate failed", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("exited 7", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Contains("Do not run the task gate", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
        Assert.Equal(first.Result?.Gate?.Command, second.Result?.Gate?.Command);
        Assert.Equal(["ran", "ran"], File.ReadAllLines(Path.Combine(_workspace, "attempts.txt")));
        Assert.Equal("done", second.Result?.Status);
        Assert.Equal("unavailable", second.Result?.Verification.Outcome);
        Assert.Equal(1, second.TasksCompleted);
        Assert.Null(run.ReadState().PendingGateFailure);
    }

    [Theory]
    [InlineData("passed", "done")]
    [InlineData("failed", "blocked")]
    [InlineData("unavailable", "blocked")]
    public async Task A_condition_stays_with_the_builder_and_keeps_its_report(string verification, string status)
    {
        var run = NewRun("## Approach\n\n1. Task. **Gate:** check the documentation; `Set-Content condition.txt ran` is only an example. (R1)\n");
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult(status, [], new Verification(verification, "condition evidence"), "task"));

        var outcome = await new Build(vendor, Prompts()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal(status, outcome.Result?.Status);
        Assert.Equal(verification, outcome.Result?.Verification.Outcome);
        Assert.Equal(status == "done" ? 1 : 0, outcome.TasksCompleted);
        Assert.False(File.Exists(Path.Combine(_workspace, "condition.txt")));
        Assert.Contains("Check the task's condition yourself", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not run the task gate", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("**Gate:** `Add-Content task-gates.txt ran`")]
    [InlineData("**Gate**: `Add-Content task-gates.txt ran`")]
    [InlineData("**gate:**\n```powershell\nAdd-Content task-gates.txt ran\n```")]
    public async Task The_act_prompt_uses_the_same_executable_classification_as_the_server(string taskGate)
    {
        var run = NewRun("## Approach\n\n1. Task. " + taskGate + "\n");
        var vendor = Builder();

        var outcome = await new Build(vendor, Prompts()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal("passed", outcome.Result?.Gate?.Outcome);
        Assert.Equal(["ran"], File.ReadAllLines(Path.Combine(_workspace, "task-gates.txt")));
        Assert.Contains("Do not run the task gate", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_without_a_gate_does_not_invent_an_executable_check()
    {
        var run = NewRun("## Approach\n\n1. Update the documentation.\n");
        var vendor = Builder();

        var outcome = await new Build(vendor, Prompts()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal("not_executable", outcome.Result?.Gate?.Outcome);
        Assert.Equal(1, outcome.TasksCompleted);
        Assert.DoesNotContain("# Task gate", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void Every_builder_contract_preserves_conditions_and_reserves_executable_gates_for_the_server(string vendor)
    {
        var prompt = Prompts().Load(vendor, VendorRole.Builder);
        var text = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("Do not run an executable task gate", text, StringComparison.Ordinal);
        Assert.Contains("Check a task's condition yourself", text, StringComparison.Ordinal);
        Assert.Contains("Separate targeted checks are optional", text, StringComparison.Ordinal);
        Assert.Contains("No Builder checks were run; the task gate is reserved for the server after this turn.", text, StringComparison.Ordinal);
        Assert.Contains("including on a retry", text, StringComparison.Ordinal);
        Assert.Contains("another command, wrapper, or sequence", text, StringComparison.Ordinal);
        Assert.Contains("single targeted test", text, StringComparison.Ordinal);
        Assert.Contains("review-fix", text, StringComparison.Ordinal);
    }

    private RunDirectory NewRun(string plan)
    {
        var run = RunDirectory.Create(_workspace, "gate-ownership");
        run.WritePlan(plan);
        run.WriteState(new RunState("gate-ownership", _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true));
        return run;
    }

    private static BuildResult Delegated() => new("done", [],
        new Verification("unavailable", "No Builder checks were run; the task gate is reserved for the server after this turn."), "implemented");

    private static RecordingVendor Builder()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Delegated(), "first-token");
        return vendor;
    }

    private static PromptLibrary Prompts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return new PromptLibrary(prompts);
        }
        throw new DirectoryNotFoundException("could not locate repository prompts");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
