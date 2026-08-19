using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests.Jobs;

public sealed class WorkActTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-work-act-" + Guid.NewGuid().ToString("N"));
    private readonly PromptLibrary _prompts;

    public WorkActTests()
    {
        Directory.CreateDirectory(_workspace);
        _prompts = new PromptLibrary(RepositoryPrompts());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Plan_review_dispatch_matches_the_direct_act_payload()
    {
        const string draft = "## Approach\n\n1. Review the change.\n";
        var directVendor = new RecordingVendor("claude");
        var response = new Critique("approve", [], "good");
        directVendor.Enqueue(response);
        var direct = await new PlanReview(directVendor, _prompts).ReviewAsync(
            NewRun("plan-direct"), draft, new Selection("critic", "low"), CancellationToken.None);

        var dispatchedVendor = new RecordingVendor("claude");
        dispatchedVendor.Enqueue(response);
        var payload = await new WorkAct(dispatchedVendor, _prompts).RunAsync(
            "plan.review", NewRun("plan-dispatched"), draft, new Selection("critic", "low"), null, null,
            CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(direct, ContractJson.Default.Critique), payload);
    }

    [Fact]
    public async Task Build_dispatch_matches_the_direct_act_payload()
    {
        var directVendor = new RecordingVendor("codex");
        var response = new BuildResult("done", ["tracked.cs"], new Verification("passed", "the checks ran"), "built");
        directVendor.Enqueue(response, "next");
        var direct = await new Build(directVendor, _prompts).NextAsync(
            NewApprovedRun("build-direct"), new Selection("builder", null), CancellationToken.None);

        var dispatchedVendor = new RecordingVendor("codex");
        dispatchedVendor.Enqueue(response, "next");
        var payload = await new WorkAct(dispatchedVendor, _prompts).RunAsync(
            "build.next", NewApprovedRun("build-dispatched"), null, new Selection("builder", null), null, null,
            CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(direct, ForgeToolJson.Default.BuildOutcome), payload);
    }

    [Fact]
    public async Task Code_review_dispatch_matches_the_direct_act_payload()
    {
        var git = new RecordingReviewGit(["tracked.cs"], "diff");
        var directVendor = new RecordingVendor("claude");
        var response = new Critique("approve", [], "good");
        directVendor.Enqueue(response);
        var direct = await new CodeReview(directVendor, _prompts, git).ReviewAsync(
            NewApprovedRun("code-direct"), new Selection("critic", null), CancellationToken.None);

        var dispatchedVendor = new RecordingVendor("claude");
        dispatchedVendor.Enqueue(response);
        var payload = await new WorkAct(dispatchedVendor, _prompts, git).RunAsync(
            "review.code", NewApprovedRun("code-dispatched"), null, new Selection("critic", null), null, null,
            CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(direct, ContractJson.Default.Critique), payload);
    }

    [Fact]
    public async Task Review_fix_dispatch_matches_the_direct_act_and_updates_resume_state()
    {
        const string findings = "- **major** tracked.cs — fix it";
        const string deferred = "- coverage — outside the plan";
        var directVendor = new RecordingVendor("codex");
        var response = new BuildResult("done", ["tracked.cs"], new Verification("passed", "the checks ran"), "fixed");
        directVendor.Enqueue(response, "next");
        var directRun = NewApprovedRun("fix-direct", "codex", "old");
        var direct = await new ReviewFix(directVendor, _prompts).FixAsync(
            directRun, new Selection("builder", null), findings, deferred, CancellationToken.None);

        var dispatchedVendor = new RecordingVendor("codex");
        dispatchedVendor.Enqueue(response, "next");
        var dispatchedRun = NewApprovedRun("fix-dispatched", "codex", "old");
        var payload = await new WorkAct(dispatchedVendor, _prompts).RunAsync(
            "review.fix", dispatchedRun, null, new Selection("builder", null), findings, deferred,
            CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(direct, ContractJson.Default.BuildResult), payload);
        Assert.Equal(directRun.ReadState().BuilderSessionId, dispatchedRun.ReadState().BuilderSessionId);
        Assert.Equal(directRun.ReadState().BuilderVendor, dispatchedRun.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task Blank_review_fix_findings_take_the_deferred_only_path()
    {
        var vendor = new RecordingVendor("codex");
        var run = NewApprovedRun("fix-deferred");

        var payload = await new WorkAct(vendor, _prompts).RunAsync(
            "review.fix", run, null, new Selection("builder", null), " \n", "- outside the plan",
            CancellationToken.None);

        Assert.Empty(vendor.Sessions);
        Assert.Contains("\"status\":\"done\"", payload, StringComparison.Ordinal);
        Assert.Contains("outside the plan", run.ReadReviewLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_act_is_refused()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new WorkAct(new RecordingVendor("claude"), _prompts)
            .RunAsync("unknown", NewRun("unknown"), null, new Selection("model", null), null, null,
                CancellationToken.None));

        Assert.Contains("unknown work act", error.Message, StringComparison.Ordinal);
    }

    private RunDirectory NewRun(string runId)
    {
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5));
        return run;
    }

    private RunDirectory NewApprovedRun(string runId, string builderVendor = "", string builderSessionId = "")
    {
        var run = NewRun(runId);
        run.WritePlan("## Approach\n\n1. Build tracked.cs.\n");
        run.WriteState(run.ReadState() with
        {
            Approved = true,
            BuilderVendor = builderVendor,
            BuilderSessionId = builderSessionId
        });
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

    private sealed class RecordingReviewGit(IReadOnlyList<string> paths, string diff) : IReviewGit
    {
        public Task<string> DiffAsync(IReadOnlyList<string> pathspec, CancellationToken ct) => Task.FromResult(diff);

        public Task<IReadOnlyList<string>> ChangedPathsAsync(IReadOnlyList<string> pathspec, CancellationToken ct) =>
            Task.FromResult(paths);
    }
}
