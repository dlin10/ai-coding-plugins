using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class FixRetryAuditMatrixTests : IDisposable
{
    private const string Plan = "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `exit 0` (R2)\n";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-retry-" + Guid.NewGuid().ToString("n"));
    private static readonly Selection Builder = new("builder", null);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> RetryCells()
    {
        foreach (var last in new[] { "none", "failed", "timeout", "other" })
            foreach (var global in new[] { "none", "failure", "interruption" })
                yield return [last, global];
    }

    [Theory]
    [MemberData(nameof(RetryCells))]
    public async Task Attempt_failure_wins_and_interruption_is_appended(string last, string global)
    {
        var run = NewRun();
        var prior = last == "none" ? null : Report() with
        {
            Gate = new GateRun(last == "other" ? "not_run" : last, "G1", "exit 9", 9, "attempt output", 2, "detail")
        };
        run.ReadDecisionLedger().RecordFixAttempt("A", ["F-0001"], prior, false);
        var globalBrief = global switch
        {
            "failure" => "global failure evidence",
            "interruption" => Gatekeeper.CutShortBrief(["written.cs"]),
            _ => null
        };
        run.WriteState(run.ReadState() with { PendingGateFailure = globalBrief });
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Report());
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None);
        var prompt = Assert.Single(vendor.Sessions).PromptText;
        var expected = new System.Text.StringBuilder();
        if (last is "failed" or "timeout")
        {
            var description = "The gate (G1) " + (last == "failed"
                ? "exited 9 after 2 s" : "was killed after 2 s without finishing")
                + " when the host ran it after your previous turn.\n\nCommand:\n\n```\nexit 9\n```\n\nIts output ended with:\n\n```text\nattempt output\n```";
            expected.Append(ExpectedFailure(description));
            if (global == "interruption") expected.Append("\n# The previous attempt was cut short\n\n" + globalBrief + "\n");
        }
        else if (global == "failure") expected.Append(ExpectedFailure(globalBrief!));
        else if (global == "interruption") expected.Append("\n# The previous attempt was cut short\n\n" + globalBrief + "\n");
        var retryStart = prompt.IndexOf("\n# The previous attempt", StringComparison.Ordinal);
        Assert.Equal(expected.ToString().ReplaceLineEndings("\n"),
            retryStart < 0 ? "" : prompt[retryStart..].ReplaceLineEndings("\n"));
        Assert.DoesNotContain("The same command runs again", prompt, StringComparison.Ordinal);
        if (last is "failed" or "timeout") Assert.DoesNotContain("global failure evidence", prompt, StringComparison.Ordinal);
        if (global == "interruption")
        {
            Assert.Contains("written.cs", prompt, StringComparison.Ordinal);
            Assert.Contains("Read what you changed", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Killed_background_brief_follows_the_current_attempt_error()
    {
        var run = NewRun();
        var failure = Report() with { Gate = new GateRun("failed", "G1", "exit 9", 9, "attempt output", 2, null) };
        run.ReadDecisionLedger().RecordFixAttempt("A", ["F-0001"], failure, false);
        var killed = Gatekeeper.PendingFailure(Report(), ["unfinished command"], null);
        run.WriteState(run.ReadState() with { PendingGateFailure = killed });
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Report());
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None);
        var prompt = vendor.Sessions.Single().PromptText;
        Assert.True(prompt.IndexOf("attempt output", StringComparison.Ordinal)
            < prompt.IndexOf("Your previous turn ended with work still running", StringComparison.Ordinal));
        Assert.Contains("unfinished command", prompt, StringComparison.Ordinal);
        Assert.Contains("wait for it before you answer", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("full", false)]
    [InlineData("full", true)]
    [InlineData("targeted", false)]
    [InlineData("targeted", true)]
    public async Task Fresh_and_resumed_prompts_name_current_gate_and_owners(string mode, bool resumed)
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        if (resumed)
        {
            vendor.Enqueue(Report(), resumeToken: "session");
            await Fix(vendor).FixAsync(run, Builder, null, "first", ["F-0002"], CancellationToken.None, gate: mode);
        }
        vendor.Enqueue(Report(), resumeToken: "session");
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: mode);
        var session = vendor.Sessions.Last();
        Assert.Equal(resumed ? "session" : null, session.StartedWithResumeToken);
        Assert.Contains("gate=" + mode, session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Do not run executable task or fix gates", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("or reproduce their full equivalent", session.PromptText, StringComparison.Ordinal);
        Assert.Contains(mode == "full" ? "G1:\n```\nexit 0" : "Fix gate:\n```\nexit 0",
            session.PromptText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(mode == "full" ? "server runs the full plan G-commands" : "Orchestrator runs all full plan checks",
            session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_failure_survives_foreign_targeted_success_cut_short_and_reload()
    {
        var run = NewRun();
        run.WritePlan(Plan.Replace("**G1.** `exit 0`", "**G1.** `exit 9`"));
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Report(), resumeToken: "session");
        var failed = await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None);
        Assert.Equal("failed", failed.Gate!.Outcome);
        vendor.Enqueue(Report(), resumeToken: "session");
        await Fix(vendor).FixAsync(run, Builder, null, "B", ["F-0002"], CancellationToken.None, gate: "targeted");
        Assert.Null(run.ReadState().PendingGateFailure);
        vendor.Enqueue(new OperationCanceledException("cut short"), resumeToken: "session");
        await Assert.ThrowsAsync<TurnCutShortException>(() => Fix(vendor).FixAsync(run, Builder, null, "A",
            ["F-0001"], CancellationToken.None));
        Assert.Contains("exited 9", vendor.Sessions.Last().PromptText, StringComparison.Ordinal);
        run = RunDirectory.Open(_workspace, run.RunId);
        var attempt = run.ReadDecisionLedger().FindFixAttempt("A")!;
        Assert.False(attempt.Terminal);
        Assert.Equal(failed.Gate, attempt.LastResult!.Gate);
        run.ReadDecisionLedger().BeginFixAttempt("A", ["F-0001"], "full");
        Assert.Equal(failed.Gate, run.ReadDecisionLedger().FindFixAttempt("A")!.LastResult!.Gate);
        Assert.Contains("cut short", run.ReadState().PendingGateFailure!, StringComparison.Ordinal);
        run.WritePlan(Plan);
        vendor.Enqueue(Report());
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None);
        var prompt = vendor.Sessions.Last().PromptText;
        Assert.True(prompt.IndexOf("exited 9", StringComparison.Ordinal) < prompt.IndexOf("Your previous turn was cut short", StringComparison.Ordinal));
        var ledger = run.ReadDecisionLedger();
        ledger.RecordFixAttempt("first-cut", ["F-0002"], null, false);
        Assert.Null(ledger.FindFixAttempt("first-cut")!.LastResult);
    }

    [Fact]
    public async Task Flow_records_mode_gate_attempt_and_current_pending_after_replay_and_host_verification()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Report());
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("Fix gate: passed", flow, StringComparison.Ordinal);
        Assert.DoesNotContain("Gates Fix gate", flow, StringComparison.Ordinal);
        Assert.Contains("gateMode: targeted", flow, StringComparison.Ordinal);
        Assert.Contains("fixAttemptId: A", flow, StringComparison.Ordinal);
        Assert.Contains("fixFindingIds: F-0001", flow, StringComparison.Ordinal);
        Assert.Contains("pendingFullGateAttempt: A — F-0001", flow, StringComparison.Ordinal);
        await Fix(vendor).FixAsync(run, Builder, new OrchestratorDecisionBatch("verified",
            [new OrchestratorDecision("F-0001", "hostVerified", "orchestrator", "all gates passed", "attempt A; exit 0")]),
            null, [], CancellationToken.None);
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        flow = File.ReadAllText(run.FlowLogPath);
        var final = flow[flow.LastIndexOf("## Fix attempt", StringComparison.Ordinal)..];
        Assert.Contains("gateMode: targeted", final, StringComparison.Ordinal);
        Assert.Contains("pendingFullGateFindingIds: \n", final.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("pendingFullGateAttempt:", final, StringComparison.Ordinal);
        Assert.Single(vendor.Sessions);
    }

    private RunDirectory NewRun()
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WritePlan(Plan);
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true, CodeReviewRounds: 1));
        run.ReadDecisionLedger().AddFindings([new Finding("major", "one.cs", "first"),
            new Finding("major", "two.cs", "second")], LedgerPhase.CodeReview);
        return run;
    }

    private static BuildResult Report() => new("done", [], new Verification("unavailable", "server owns gate"), "fixed");
    private static string ExpectedFailure(string description) =>
        "\n# The previous attempt did not pass its gate\n\n" + description + "\n\n"
        + "The selected executable gate runs on the server after this turn. Do not run this gate yourself "
        + "or reproduce its full equivalent. Fix the cause; separate diagnostic checks are optional. "
        + "Report only those checks in verification, or unavailable with an explicit reason if you left "
        + "verification to the server. Their success does not prove the gate passed.\n";
    private static ReviewFix Fix(IVendor vendor) => new(vendor, Prompts());
    private static PromptLibrary Prompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts"))) directory = directory.Parent;
        return new PromptLibrary(Path.Combine(directory!.FullName, "prompts"));
    }
}
