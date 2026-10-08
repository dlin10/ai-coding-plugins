using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class PendingFullGateTransitionMatrixTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private const string ValidPlan = "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `exit 0` (R2)\n";
    private static readonly string?[] Modes = [null, "full", "targeted", "Targeted", "", "   ", "unknown"];
    private static readonly string[] Actions = ["hostVerified", "defer", "reject", "duplicateOf", "addressedByRevision", "accept", "decline"];
    private static readonly string[] Outcomes = ["passed", "failed", "timeout", "not_run", "background_killed", "cut_short", "sensitive_guard_refusal", "vendor_start_refusal"];

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> DecisionCells()
    {
        foreach (var pending in new[] { false, true })
        foreach (var action in Actions)
        foreach (var entryCode in new[] { false, true })
        foreach (var batchCode in new[] { false, true })
        {
            // Pending belongs only to unresolved code entries; reopening uses a settled fixture.
            if (pending && (!entryCode || action is "accept" or "decline")) continue;
            var allowed = action switch
            {
                "hostVerified" => entryCode && batchCode,
                "addressedByRevision" => !entryCode && !batchCode,
                "accept" or "decline" => !entryCode || batchCode,
                _ => entryCode == batchCode
            };
            yield return [pending, action, entryCode, batchCode, allowed];
        }
    }

    [Fact]
    public void Matrix_axes_and_counts_are_exact()
    {
        Assert.Equal(7, Actions.Distinct().Count());
        Assert.Equal(7, Modes.Length);
        Assert.Equal(38, DecisionCells().Count());
        Assert.Equal(140, BindingCells().Count());
        Assert.Equal(32, RefixCells().Count());
        Assert.Equal(38, DecisionCells().Select(cell => string.Join("|", cell.Take(4))).Distinct().Count());
        Assert.Equal(140, BindingCells().Select(cell => string.Join("|", cell.Select(value => value ?? "<omitted>"))).Distinct().Count());
        Assert.Equal(32, RefixCells().Select(cell => string.Join("|", cell)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(DecisionCells))]
    public void Decisions_preserve_or_clear_pending_by_semantic_transition(bool pending, string action,
                                                                          bool entryCode, bool batchCode, bool allowed)
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        var phase = entryCode ? LedgerPhase.CodeReview : LedgerPhase.PlanReview;
        ledger.AddFindings([Finding("source"), Finding("canonical")], phase);
        if (pending) ledger.RecordFixAttempt("A", ["F-0001", "F-0002"], Passed(), true, "targeted");
        if (action is "accept" or "decline")
            ledger.Apply(new OrchestratorDecisionBatch("settle", [Decision("defer")]), phase);
        var before = File.ReadAllBytes(ledger.Path);
        var canonical = ledger.Snapshot.Entries[1];
        var batch = new OrchestratorDecisionBatch("decision", [Decision(action)]);
        if (!allowed)
        {
            Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(batch,
                batchCode ? LedgerPhase.CodeReview : LedgerPhase.PlanReview));
            Assert.Equal(before, File.ReadAllBytes(ledger.Path));
            return;
        }

        var response = ledger.Apply(batch, batchCode ? LedgerPhase.CodeReview : LedgerPhase.PlanReview);
        Assert.Equal("no_op", ledger.Apply(batch, batchCode ? LedgerPhase.CodeReview : LedgerPhase.PlanReview).Outcome);
        var reopened = DecisionLedger.Open(ledger.Path);
        Assert.Equal(canonical, reopened.Snapshot.Entries.Single(entry => entry.FindingId == "F-0002"));
        if (action is "hostVerified" or "addressedByRevision" or "duplicateOf")
            Assert.DoesNotContain(reopened.Snapshot.Entries, entry => entry.FindingId == "F-0001");
        else
        {
            var source = reopened.Snapshot.Entries.Single(entry => entry.FindingId == "F-0001");
            Assert.Null(source.PendingFullGateAttemptId);
            Assert.Equal(action switch { "defer" or "decline" => LedgerDisposition.Deferred,
                "reject" => LedgerDisposition.Rejected, _ => LedgerDisposition.Unresolved }, source.Disposition);
            if (action == "accept") Assert.Equal(batchCode ? "code_review" : "plan_review", source.ActivePhase);
        }
        Assert.Equal(action == "decline" ? "declined" : "applied", response.Outcome);
    }

    public static IEnumerable<object?[]> BindingCells()
    {
        for (var stored = 0; stored < 5; stored++)
        foreach (var mode in Modes)
        foreach (var same in new[] { false, true })
        foreach (var valid in new[] { false, true })
            yield return [stored, mode, same, valid];
    }

    [Theory]
    [MemberData(nameof(BindingCells))]
    public async Task Binding_and_replay_precede_plan_and_independent_decisions(int stored, string? mode, bool same, bool valid)
    {
        var run = NewRun(valid ? ValidPlan : "## Gates\n1. **G1.** condition only\n");
        var ledger = run.ReadDecisionLedger();
        ledger.AddFindings([Finding("original"), Finding("different"), Finding("independent")], LedgerPhase.CodeReview);
        if (stored > 0) ledger.RecordFixAttempt("B", ["F-0001"], Passed(), stored is 2 or 4,
            stored < 3 ? "full" : "targeted");
        var before = File.ReadAllBytes(ledger.Path);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("blocked", [], new Verification("failed", "diagnostic"), "not completed"));
        var fix = new ReviewFix(vendor, Prompts());
        var ids = new[] { same ? "F-0001" : "F-0002" };
        var batch = new OrchestratorDecisionBatch("independent", [new OrchestratorDecision("F-0003", "defer", "orchestrator", "later")]);
        // R6/R8: omitted mode is full, binding is immutable, replay does not inspect today's plan.
        var inputValid = mode is null or "full" or "targeted";
        var bindingValid = stored == 0 || (same && (mode ?? "full") == (stored < 3 ? "full" : "targeted"));
        var terminal = stored is 2 or 4;
        var allowed = inputValid && bindingValid && (terminal || mode != "targeted" || valid);
        if (!allowed)
        {
            var error = await Record.ExceptionAsync(() => fix.FixAsync(run, new Selection("builder", null), batch,
                "B", ids, CancellationToken.None, gate: mode));
            Assert.True(error is ArgumentRejectedException or DecisionLedgerRequestException, error?.ToString());
            Assert.Empty(vendor.Sessions);
            Assert.Equal(before, File.ReadAllBytes(ledger.Path));
            return;
        }
        var result = await fix.FixAsync(run, new Selection("builder", null), batch, "B", ids, CancellationToken.None, gate: mode);
        Assert.Equal(LedgerDisposition.Deferred, ledger.Snapshot.Entries.Single(entry => entry.FindingId == "F-0003").Disposition);
        Assert.Equal(terminal ? 0 : 1, vendor.Sessions.Count);
        Assert.Equal(terminal ? "done" : "blocked", result.Status);
        Assert.Equal(mode ?? "full", ledger.FindFixAttempt("B")!.GateMode);
    }

    public static IEnumerable<object[]> RefixCells()
    {
        foreach (var old in new[] { false, true })
        foreach (var mode in new[] { "full", "targeted" })
        foreach (var outcome in Outcomes) yield return [old, mode, outcome];
    }

    [Theory]
    [MemberData(nameof(RefixCells))]
    public void Refix_completion_is_atomic_and_does_not_erase_previous_verification(bool old, string mode, string outcome)
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.AddFindings([Finding("selected"), Finding("foreign")], LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("foreign", ["F-0002"], Passed(), true, "targeted");
        if (old) ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
        ledger.BeginFixAttempt("B", ["F-0001"], mode);
        Assert.Equal(old ? "A" : null, ledger.Snapshot.Entries[0].PendingFullGateAttemptId);
        var result = outcome switch
        {
            "passed" => Passed(),
            "failed" or "timeout" => Passed() with { Status = "gate_failed", Gate = Passed().Gate! with { Outcome = outcome, ExitCode = 1 } },
            "not_run" => Passed() with { Status = "blocked", Gate = Passed().Gate! with { Outcome = "not_run" } },
            "background_killed" => Passed() with { Status = "background_killed", Gate = Passed().Gate! with { Outcome = "not_run" } },
            _ => null
        };
        ledger.RecordFixAttempt("B", ["F-0001"], result, outcome == "passed", mode);
        var reloaded = DecisionLedger.Open(ledger.Path);
        Assert.Equal("foreign", reloaded.Snapshot.Entries.Single(entry => entry.FindingId == "F-0002").PendingFullGateAttemptId);
        var source = reloaded.Snapshot.Entries.SingleOrDefault(entry => entry.FindingId == "F-0001");
        if (outcome == "passed" && mode == "full") Assert.Null(source);
        else Assert.Equal(outcome == "passed" ? "B" : old ? "A" : null, source!.PendingFullGateAttemptId);
        Assert.Equal(outcome == "passed", reloaded.FindFixAttempt("B")!.Terminal);
        Assert.Equal(mode, reloaded.FindFixAttempt("B")!.GateMode);
    }

    [Fact]
    public void Both_apply_apis_share_pending_transitions_and_rejected_writes_leave_whole_snapshot()
    {
        foreach (var typed in new[] { false, true })
        {
            var ledger = NewRun().ReadDecisionLedger();
            ledger.AddFindings([Finding("source"), Finding("other")], LedgerPhase.CodeReview);
            ledger.RecordFixAttempt("A", ["F-0001", "F-0002"], Passed(), true, "targeted");
            if (typed) ledger.Apply(new DecisionBatchRequest("defer", [new LedgerDispositionDecision("F-0001",
                LedgerDisposition.Deferred, LedgerDecisionMaker.Orchestrator, "later")], [], []));
            else ledger.Apply(new OrchestratorDecisionBatch("defer", [Decision("defer")]), LedgerPhase.CodeReview);
            Assert.Null(ledger.Snapshot.Entries[0].PendingFullGateAttemptId);
            Assert.Equal("A", ledger.Snapshot.Entries[1].PendingFullGateAttemptId);
            var before = File.ReadAllBytes(ledger.Path);
            Assert.Throws<DecisionLedgerStateException>(() => ledger.RecordFixAttempt("bad", ["F-0002"], null, true, "targeted"));
            Assert.Equal(before, File.ReadAllBytes(ledger.Path));
            Assert.Throws<DecisionLedgerRequestException>(() => ledger.RecordFixAttempt("A", ["F-0002"], null, false, "targeted"));
            Assert.Equal(before, File.ReadAllBytes(ledger.Path));
            Assert.Equal(ledger.Snapshot.Entries, DecisionLedger.Open(ledger.Path).Snapshot.Entries);
        }
    }

    [Fact]
    public void Critic_assessments_and_new_findings_do_not_verify_pending_and_plan_raises_are_refused()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
        ledger.IngestCritique(new VendorCritique { Verdict = "revise", Summary = "assessment", Findings = [new VendorFinding("major", "test.cs", "new")],
            UnresolvedAssessments = [new UnresolvedAssessment("F-0001", false, "not observed")], Reopenings = [] }, LedgerPhase.CodeReview);
        var batch = new OrchestratorDecisionBatch("raise", [], [new OrchestratorRaise("major", "other", "new rule", "orchestrator", "found")]);
        var before = File.ReadAllBytes(ledger.Path);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(batch, LedgerPhase.PlanReview));
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
        ledger.Apply(batch, LedgerPhase.CodeReview);
        Assert.Equal("A", ledger.Snapshot.Entries[0].PendingFullGateAttemptId);
        Assert.All(ledger.Snapshot.Entries.Skip(1), entry => Assert.Null(entry.PendingFullGateAttemptId));
    }

    [Fact]
    public async Task Startup_refusals_keep_binding_and_the_old_pending_group()
    {
        foreach (var secret in new[] { false, true })
        {
            var run = NewRun();
            var ledger = run.ReadDecisionLedger();
            ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
            ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
            var error = await Record.ExceptionAsync(() => new ReviewFix(new RecordingVendor("codex"), Prompts())
                .FixAsync(run, new Selection("builder", null), null, "B", ["F-0001"], CancellationToken.None,
                    note: secret ? "api_key = AbCdEfGhIjKlMnOpQrStUvWxYz1234567890" : null, gate: "targeted"));
            if (secret) Assert.IsType<SensitiveContentException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("targeted", ledger.FindFixAttempt("B")!.GateMode);
            Assert.Null(ledger.FindFixAttempt("B")!.LastResult);
            Assert.Equal("A", Assert.Single(ledger.Summary.PendingFullGateAttempts).FixAttemptId);
        }
    }

    [Fact]
    public void Cut_short_retains_last_result_terminal_replay_cannot_be_overwritten_and_closed_entries_stay_closed()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("B", ["F-0001"], Passed() with { Status = "gate_failed", Gate = Passed().Gate! with { Outcome = "failed" } }, false, "targeted");
        var previous = ledger.FindFixAttempt("B")!.LastResult;
        ledger.BeginFixAttempt("B", ["F-0001"], "targeted");
        ledger.RecordFixAttempt("B", ["F-0001"], null, false, "targeted");
        Assert.Equal(previous!.Gate, ledger.FindFixAttempt("B")!.LastResult!.Gate);
        Assert.Equal(previous.Status, ledger.FindFixAttempt("B")!.LastResult!.Status);
        ledger.Apply(new OrchestratorDecisionBatch("close", [Decision("hostVerified")]), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("B", ["F-0001"], Passed(), true, "targeted");
        ledger.RecordFixAttempt("B", ["F-0001"], null, false, "targeted");
        Assert.True(ledger.FindFixAttempt("B")!.Terminal);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public async Task Full_condition_only_keeps_the_verified_fallback()
    {
        var run = NewRun("## Gates\n1. **G1.** check by reading\n");
        var ledger = run.ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "read condition"), "fixed"));
        var result = await new ReviewFix(vendor, Prompts()).FixAsync(run, new Selection("builder", null), null,
            "B", ["F-0001"], CancellationToken.None);
        Assert.Equal("not_executable", result.Gate!.Outcome);
        Assert.True(ledger.FindFixAttempt("B")!.Terminal);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public void Replacement_failure_preserves_the_complete_snapshot_on_reload()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
        ledger.BeginFixAttempt("B", ["F-0001"], "targeted");
        var before = File.ReadAllBytes(ledger.Path);
        using (var held = new FileStream(ledger.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => ledger.RecordFixAttempt("B", ["F-0001"], Passed(), true, "targeted"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
        var reloaded = DecisionLedger.Open(ledger.Path);
        Assert.False(reloaded.FindFixAttempt("B")!.Terminal);
        Assert.Null(reloaded.FindFixAttempt("B")!.LastResult);
        Assert.Equal("A", Assert.Single(reloaded.Snapshot.Entries).PendingFullGateAttemptId);
    }

    [Fact]
    public async Task Targeted_success_replays_without_the_current_fix_gate_and_full_refix_closes_it()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("unavailable", "server gate"), "fixed"));
        var fix = new ReviewFix(vendor, Prompts());
        var result = await fix.FixAsync(run, new Selection("builder", null), null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        Assert.Equal("passed", result.Gate!.Outcome);
        Assert.Equal("A", Assert.Single(ledger.Snapshot.Entries).PendingFullGateAttemptId);
        run.WritePlan("## Gates\n1. **G1.** condition checked\n");
        var replay = await fix.FixAsync(run, new Selection("builder", null), null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        Assert.Equal(result.Gate, replay.Gate);
        Assert.Single(vendor.Sessions);
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "read condition"), "fixed again"));
        await fix.FixAsync(run, new Selection("builder", null), null, "B", ["F-0001"], CancellationToken.None);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public async Task Actual_cut_short_retry_keeps_the_last_completed_result_and_old_mark()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.AddFinding(Finding("source"), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
        var previous = Passed() with { Status = "gate_failed", Gate = Passed().Gate! with { Outcome = "failed" } };
        ledger.RecordFixAttempt("B", ["F-0001"], previous, false, "targeted");
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new OperationCanceledException("host cancelled"));
        await Assert.ThrowsAsync<TurnCutShortException>(() => new ReviewFix(vendor, Prompts()).FixAsync(run,
            new Selection("builder", null), null, "B", ["F-0001"], CancellationToken.None, gate: "targeted"));
        Assert.Equal(previous.Gate, ledger.FindFixAttempt("B")!.LastResult!.Gate);
        Assert.False(ledger.FindFixAttempt("B")!.Terminal);
        Assert.Equal("A", Assert.Single(ledger.Snapshot.Entries).PendingFullGateAttemptId);
    }

    [Fact]
    public void Typed_closures_obey_the_same_phase_and_pending_rules()
    {
        foreach (var code in new[] { false, true })
        foreach (var kind in new[] { LedgerClosureKind.Revision, LedgerClosureKind.HostVerified })
        {
            var ledger = NewRun().ReadDecisionLedger();
            ledger.AddFinding(Finding("source"), code ? LedgerPhase.CodeReview : LedgerPhase.PlanReview);
            if (code) ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "targeted");
            var before = File.ReadAllBytes(ledger.Path);
            var request = new DecisionBatchRequest("close", [], [], [new LedgerClosureDecision("F-0001", kind,
                LedgerDecisionMaker.Orchestrator, "verified", kind == LedgerClosureKind.HostVerified ? "host passed" : "")]);
            if (code == (kind == LedgerClosureKind.HostVerified))
            {
                ledger.Apply(request);
                Assert.Empty(ledger.Snapshot.Entries);
            }
            else
            {
                Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(request));
                Assert.Equal(before, File.ReadAllBytes(ledger.Path));
            }
        }
    }

    private RunDirectory NewRun(string plan = ValidPlan)
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WritePlan(plan);
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true, CodeReviewRounds: 1));
        return run;
    }

    private static Finding Finding(string what) => new("major", "test.cs", what);
    private static OrchestratorDecision Decision(string action) => new("F-0001", action, "orchestrator", "reason",
        action is "hostVerified" or "accept" ? "host command exited 0; attempt A" : null,
        action == "duplicateOf" ? "F-0002" : null);
    private static BuildResult Passed() => new("done", [], new Verification("unavailable", "host gate"), "fixed",
        new GateRun("passed", "Fix gate", "exit 0", 0, "ok", 0.01, null));
    private static PromptLibrary Prompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts"))) directory = directory.Parent;
        return new PromptLibrary(Path.Combine(directory!.FullName, "prompts"));
    }

}
