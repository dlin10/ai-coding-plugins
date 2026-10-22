using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
            "build.next", NewApprovedRun("build-dispatched"), new Selection("builder", null), CancellationToken.None);

        Assert.Equal(BuildOutcomeView.Of(direct).ToJsonString(), payload);
    }

    [Fact]
    public async Task Review_fix_dispatch_matches_the_direct_act_and_updates_resume_state()
    {
        var directVendor = new RecordingVendor("codex");
        var response = new BuildResult("done", ["tracked.cs"], new Verification("passed", "the checks ran"), "fixed");
        directVendor.Enqueue(response, "next");
        var directRun = NewApprovedRun("fix-direct", "codex", "old");
        var directFinding = directRun.ReadDecisionLedger().AddFinding(
            new Finding("major", "tracked.cs", "fix it"), LedgerPhase.CodeReview);
        var direct = await new ReviewFix(directVendor, _prompts).FixAsync(
            directRun, new Selection("builder", null), null, "fix", [directFinding.FindingId],
            CancellationToken.None);

        var dispatchedVendor = new RecordingVendor("codex");
        dispatchedVendor.Enqueue(response, "next");
        var dispatchedRun = NewApprovedRun("fix-dispatched", "codex", "old");
        var dispatchedFinding = dispatchedRun.ReadDecisionLedger().AddFinding(
            new Finding("major", "tracked.cs", "fix it"), LedgerPhase.CodeReview);
        var payload = await new WorkAct(dispatchedVendor, _prompts).RunAsync(
            "review.fix", dispatchedRun, new Selection("builder", null), CancellationToken.None,
            decisions: null, fixAttemptId: "fix", fixFindingIds: [dispatchedFinding.FindingId]);

        Assert.Equal(JsonSerializer.Serialize(direct, ContractJson.Default.BuildResult), payload);
        Assert.Equal(directRun.ReadState().BuilderSessionId, dispatchedRun.ReadState().BuilderSessionId);
        Assert.Equal(directRun.ReadState().BuilderVendor, dispatchedRun.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_review_fix_with_neither_decisions_nor_findings_is_refused()
    {
        var vendor = new RecordingVendor("codex");
        var run = NewApprovedRun("fix-empty");

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => new WorkAct(vendor, _prompts).RunAsync(
            "review.fix", run, new Selection("builder", null), CancellationToken.None));

        Assert.Empty(vendor.Sessions);
        Assert.Empty(run.ReadDecisionLedger().Snapshot.AppliedDecisionBatches);
    }

    /// <summary>
    /// A job runs the two Builder acts alone; the critic and Scout acts have their one-call tools
    /// only — docs/adr/0043.
    /// </summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("plan.review")]
    [InlineData("review.code")]
    [InlineData("scout")]
    public async Task An_act_a_job_does_not_run_is_refused(string act)
    {
        var vendor = new RecordingVendor("claude");
        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => new WorkAct(vendor, _prompts)
            .RunAsync(act, NewApprovedRun($"refused-{act}"), new Selection("model", null), CancellationToken.None));

        Assert.Contains("unknown work act", error.Message, StringComparison.Ordinal);
        Assert.Empty(vendor.Sessions);
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
}
