using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class DecisionLedgerOrchestrationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Direct_plan_review_accepts_typed_decisions_before_prompt()
    {
        var run = NewRun("plan-direct");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("defer me"), LedgerPhase.PlanReview);
        var critic = new RecordingVendor("codex");
        critic.Enqueue(new VendorCritique
        {
            Verdict = "approve", Findings = [], Summary = "done",
            UnresolvedAssessments = [], Reopenings = []
        });

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, "# Plan\n\nTask", new Selection("critic", null), null, null, false,
                         CancellationToken.None,
                         orchestratorDecisions: Batch(Decision("defer", entry.FindingId)));

        Assert.Equal(LedgerDisposition.Deferred, Assert.Single(run.ReadDecisionLedger().Snapshot.Entries).Disposition);
        Assert.Contains(entry.FindingId, critic.Sessions.Single().PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_review_batch_is_phase_checked()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("hostVerified", entry.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Code_review_does_not_accept_plan_decisions()
    {
        var run = NewRun("code-no-decisions");
        Assert.Throws<ArgumentRejectedException>(() => WorkAct.ValidateArguments(
            "review.code", null, new Selection("critic", null), null, null, null, false, null, null,
            Batch(Decision("defer", "F-0001")), null, null));
        Assert.NotNull(run);
    }

    [Fact]
    public async Task Review_code_tool_refuses_an_unapproved_run_before_vendor_start()
    {
        var run = NewRun("code-tool", approved: false);
        await Assert.ThrowsAsync<NotApprovedException>(() => ForgeTools.ReviewCode(
            SessionRoots.None, _workspace, run.RunId, "critic", CancellationToken.None,
            vendor: "codex"));
    }

    [Fact]
    public async Task Decisions_only_review_fix_does_not_need_a_builder()
    {
        var run = NewRun("fix-decisions-only");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("settled"), LedgerPhase.CodeReview);
        run.ReadDecisionLedger().Apply(new DecisionBatchRequest("settle", [
            new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred, LedgerDecisionMaker.User, "later")
        ], [], []));
        var vendor = new RecordingVendor("codex");

        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null),
                      Batch(Decision("accept", entry.FindingId, evidence: "changed")), null, [],
                      CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Empty(vendor.Sessions);
        Assert.Single(run.ReadDecisionLedger().Snapshot.Entries);
    }

    [Fact]
    public void Plan_to_code_reopening_preserves_origin()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("plan scope"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("accept", entry.FindingId, evidence: "new code")), LedgerPhase.CodeReview);
        var reopened = Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal(LedgerPhaseNames.PlanReview, reopened.Origin);
        Assert.Equal(LedgerPhaseNames.CodeReview, reopened.ActivePhase);
        Assert.Equal(LedgerDisposition.Unresolved, reopened.Disposition);
    }

    [Fact]
    public void Decline_only_batch_is_reserved_and_idempotent()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        var batch = new OrchestratorDecisionBatch("decline-batch", [
            Decision("decline", entry.FindingId, reason: "keep settled")
        ]);

        var result = ledger.Apply(batch, LedgerPhase.PlanReview);
        var retry = ledger.Apply(batch, LedgerPhase.PlanReview);

        Assert.Equal("declined", result.Outcome);
        Assert.Equal("no_op", retry.Outcome);
        Assert.Equal("declined", retry.Result.Outcome);
        Assert.Contains(ledger.Snapshot.AppliedDecisionBatches,
                        saved => saved.DecisionBatchId == batch.DecisionBatchId);
        Assert.Equal(LedgerDisposition.Deferred, Assert.Single(ledger.Snapshot.Entries).Disposition);
    }

    [Fact]
    public void Decline_only_batch_conflict_exposes_the_saved_batch_and_result()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("decline-batch", [
            Decision("decline", entry.FindingId, reason: "saved reason")
        ]), LedgerPhase.PlanReview);
        var before = File.ReadAllBytes(ledger.Path);

        var conflict = ledger.Apply(new OrchestratorDecisionBatch("decline-batch", [
            Decision("decline", entry.FindingId, reason: "different reason")
        ]), LedgerPhase.PlanReview);
        var error = Assert.Throws<DecisionLedgerRequestException>(conflict.ThrowIfConflict);

        Assert.Equal("conflict", conflict.Outcome);
        Assert.Equal("declined", conflict.Result.Outcome);
        Assert.Contains("saved canonical decisions", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
    }

    [Fact]
    public void Duplicate_close_requires_a_known_distinct_target()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("one"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("duplicateOf", entry.FindingId, duplicateOf: entry.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Verified_close_is_code_phase_only()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("hostVerified", entry.FindingId, evidence: "host")), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Revision_close_is_plan_phase_only()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("addressedByRevision", entry.FindingId)), LedgerPhase.CodeReview));
    }

    [Fact]
    public void Decision_ids_must_be_unique()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("duplicate"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("defer", entry.FindingId), Decision("reject", entry.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Decision_batch_conflict_returns_saved_result()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("conflict"), LedgerPhase.PlanReview);
        var first = ledger.Apply(Batch(Decision("defer", entry.FindingId, reason: "first")), LedgerPhase.PlanReview);
        var conflict = ledger.Apply(new OrchestratorDecisionBatch(first.Result.DecisionBatchId,
            [Decision("defer", entry.FindingId, reason: "second")]), LedgerPhase.PlanReview);
        Assert.Equal("applied", first.Outcome);
        Assert.Equal("conflict", conflict.Outcome);
        Assert.Equal(first.Result.DecisionBatchId, conflict.Result.DecisionBatchId);
        Assert.Equal(first.Result.DecisionFindingIds, conflict.Result.DecisionFindingIds);
    }

    [Fact]
    public void Identical_decision_batch_is_a_no_op()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("retry"), LedgerPhase.PlanReview);
        var batch = Batch(Decision("defer", entry.FindingId));
        ledger.Apply(batch, LedgerPhase.PlanReview);
        var before = File.ReadAllBytes(ledger.Path);
        var retry = ledger.Apply(batch, LedgerPhase.PlanReview);
        Assert.Equal("no_op", retry.Outcome);
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
    }

    [Fact]
    public void Typed_canonical_decisions_are_sorted_and_utf8_verbatim()
    {
        var bytes = DecisionLedger.CanonicalBytes(new OrchestratorDecisionBatch("batch", [
            Decision("defer", "F-0002", reason: "  второй\r\n"),
            Decision("reject", "F-0001", reason: "  первый  ")
        ]));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.True(text.IndexOf("F-0001", StringComparison.Ordinal) < text.IndexOf("F-0002", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal("  первый  ", json.RootElement.GetProperty("decisions")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Exact_fix_ids_are_normalized_once()
    {
        var ledger = Ledger();
        var ids = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.CodeReview)
                        .Select(entry => entry.FindingId).Reverse().ToArray();
        Assert.Equal(["F-0001", "F-0002"], ledger.NormalizeFixFindingIds(ids));
    }

    [Fact]
    public void Unknown_fix_id_is_rejected()
    {
        var ledger = Ledger();
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.ValidateFixFindingIds(["F-0001"]));
    }

    [Fact]
    public void Settled_fix_id_is_rejected()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.CodeReview);
        ledger.Apply(new DecisionBatchRequest("settle", [], [], [
            new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.Revision,
                                      LedgerDecisionMaker.Orchestrator, "done")
        ]));
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.ValidateFixFindingIds([entry.FindingId]));
    }

    [Fact]
    public async Task Builder_prompt_contains_verbatim_findings_for_exact_fix_IDs_only()
    {
        var run = NewRun("builder-exact");
        var entries = run.ReadDecisionLedger().AddFindings([
            Finding("exact first\r\nline"), Finding("must not be sent")
        ], LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "verified"), "fixed"));

        await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-1", [entries[0].FindingId],
            CancellationToken.None);

        var prompt = Assert.Single(vendor.Sessions).PromptText;
        Assert.Contains("exact first\r\nline", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("must not be sent", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cut_short_fix_round_remains_in_flow_log_without_review_log()
    {
        var run = NewRun("cut-short");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("cut"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null), null, "attempt-cut", [entry.FindingId], CancellationToken.None));

        Assert.Contains("cut short", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(run.Path, "review-log.md")));
    }

    [Fact]
    public async Task Revision_is_guarded_before_plan_review()
    {
        var run = NewRun("revision-guard");
        await Assert.ThrowsAsync<SensitiveContentException>(() => ForgeTools.ReviewPlan(
            SessionRoots.None, _workspace, run.RunId, "critic", CancellationToken.None,
            planDraft: "# Plan", vendor: "codex",
            revision: "api_key: Abcdefghijklmnop1234+"));
    }

    [Fact]
    public void Decision_reason_is_guarded_before_persistence()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("guard"), LedgerPhase.PlanReview);
        Assert.Throws<SensitiveContentException>(() => ledger.Apply(
            Batch(Decision("defer", entry.FindingId, reason: "api_key: Abcdefghijklmnop1234+")), LedgerPhase.PlanReview));
        Assert.Equal(LedgerDisposition.Unresolved, Assert.Single(ledger.Snapshot.Entries).Disposition);
    }

    [Fact]
    public void Reopening_evidence_is_guarded_before_persistence()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("guard"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Throws<SensitiveContentException>(() => ledger.Apply(
            Batch(Decision("accept", entry.FindingId, evidence: "api_key: Abcdefghijklmnop1234+")), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Verified_closure_evidence_is_guarded_before_persistence()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("guard"), LedgerPhase.CodeReview);
        Assert.Throws<SensitiveContentException>(() => ledger.Apply(
            Batch(Decision("hostVerified", entry.FindingId, evidence: "api_key: Abcdefghijklmnop1234+")), LedgerPhase.CodeReview));
    }

    [Fact]
    public async Task Ledger_derived_builder_prompt_is_guarded_before_start()
    {
        var run = NewRun("prompt-guard");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("api_key: Abcdefghijklmnop1234+"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        await Assert.ThrowsAsync<SensitiveContentException>(() => new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null), null, "attempt-guard", [entry.FindingId], CancellationToken.None));
        Assert.Empty(vendor.Sessions);
    }

    [Fact]
    public async Task Passed_gate_closes_fix_entries_independent_of_builder_verification()
    {
        var run = NewRun("gate-close", plan: "## Gates\n\n1. **G1.** `Write-Output ok` passes.\n");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("gate"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("failed", "builder failed"), "reported"));
        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-gate", [entry.FindingId], CancellationToken.None);
        Assert.Equal("passed", result.Gate?.Outcome);
        Assert.Empty(run.ReadDecisionLedger().Snapshot.Entries);
    }

    [Fact]
    public async Task Automatic_gate_evidence_cannot_discard_the_completed_fix_audit()
    {
        var run = NewRun("gate-evidence", plan:
            "## Gates\n\n1. **G1.** `Write-Output 'api_key: Abcdefghijklmnop1234+'` passes.\n");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("gate"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["fixed.cs"], new Verification("passed", "verified"), "fixed"),
                       "builder-token");

        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-evidence", [entry.FindingId],
            CancellationToken.None);

        Assert.Equal("passed", result.Gate?.Outcome);
        Assert.True(run.ReadDecisionLedger().FindFixAttempt("attempt-evidence")!.Terminal);
        Assert.Equal("builder-token", run.ReadState().BuilderSessionId);
        Assert.Contains("Status: done", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_automatic_closure_preserves_fix_result_state_and_flow_before_rejection()
    {
        var run = NewRun("gate-closure-conflict", plan: "## Gates\n\n1. **G1.** `Write-Output ok` passes.\n");
        var entries = run.ReadDecisionLedger().AddFindings(
            [Finding("target"), Finding("other")], LedgerPhase.CodeReview);
        run.ReadDecisionLedger().Apply(new DecisionBatchRequest("attempt-conflict:automatic-gate", [], [], [
            new LedgerClosureDecision(entries[1].FindingId, LedgerClosureKind.AutomaticGate,
                                      LedgerDecisionMaker.Orchestrator, "saved closure", "saved evidence")
        ]));
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["fixed.cs"], new Verification("passed", "verified"), "fixed"),
                       "builder-token");

        var error = await Assert.ThrowsAsync<DecisionLedgerRequestException>(() =>
            new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
                run, new Selection("builder", null), null, "attempt-conflict", [entries[0].FindingId],
                CancellationToken.None));

        Assert.Contains("saved result", error.Message, StringComparison.Ordinal);
        var attempt = run.ReadDecisionLedger().FindFixAttempt("attempt-conflict")!;
        Assert.False(attempt.Terminal);
        Assert.Equal("done", attempt.LastResult!.Status);
        Assert.Equal("builder-token", run.ReadState().BuilderSessionId);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("decisions conflict", flow, StringComparison.Ordinal);
        Assert.Contains("Status: done", flow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_gate_retains_fix_entries()
    {
        var run = NewRun("gate-retain", plan: "## Gates\n\n1. **G1.** `cmd /c exit 3` fails.\n");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("gate"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "verified"), "reported"));
        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-failed", [entry.FindingId], CancellationToken.None);
        Assert.Equal("failed", result.Gate?.Outcome);
        Assert.Single(run.ReadDecisionLedger().Snapshot.Entries);
    }

    [Fact]
    public async Task Failed_gate_with_empty_builder_text_preserves_attempt_flow_and_session_state()
    {
        var run = NewRun("empty-failed", plan: "## Gates\n\n1. **G1.** `cmd /c exit 3` fails.\n");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("gate"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", ""), ""), "builder-token");

        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-empty-failed", [entry.FindingId],
            CancellationToken.None);

        Assert.Equal("gate_failed", result.Status);
        var attempt = run.ReadDecisionLedger().FindFixAttempt("attempt-empty-failed")!;
        Assert.False(attempt.Terminal);
        Assert.Equal(string.Empty, attempt.LastResult!.Summary);
        Assert.Equal(string.Empty, attempt.LastResult.Verification.Evidence);
        Assert.Equal("builder-token", run.ReadState().BuilderSessionId);
        Assert.NotNull(run.ReadState().PendingGateFailure);
        Assert.Contains("Status: gate_failed", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passed_gate_with_empty_builder_text_uses_stable_closure_evidence_and_preserves_audit()
    {
        var run = NewRun("empty-passed", plan: "## Gates\n\n1. **G1.** `exit 0` passes.\n");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("gate"), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", ""), ""), "builder-token");

        var result = await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt-empty-passed", [entry.FindingId],
            CancellationToken.None);

        Assert.Equal("passed", result.Gate?.Outcome);
        var snapshot = run.ReadDecisionLedger().Snapshot;
        Assert.True(Assert.Single(snapshot.FixAttempts!).Terminal);
        Assert.Empty(snapshot.Entries);
        var closure = snapshot.AppliedDecisionBatches.Single(batch =>
            batch.DecisionBatchId == "attempt-empty-passed:automatic-gate");
        Assert.Contains("host gate passed for this fix attempt",
                        System.Text.Encoding.UTF8.GetString(closure.CanonicalBytes), StringComparison.Ordinal);
        Assert.Equal("builder-token", run.ReadState().BuilderSessionId);
        Assert.Null(run.ReadState().PendingGateFailure);
        Assert.Contains("Status: done", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_attempt_ids_require_an_exact_set()
    {
        var ledger = LedgerWithAttemptIds();
        ledger.RecordFixAttempt("attempt", ["F-0001"], null, false);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.RecordFixAttempt("attempt", ["F-0002"], null, false));
    }

    [Fact]
    public void Fix_attempt_sets_are_sorted_in_the_snapshot()
    {
        var ledger = LedgerWithAttemptIds();
        ledger.RecordFixAttempt("attempt", ["F-0002", "F-0001"], null, false);
        Assert.Equal(["F-0001", "F-0002"], Assert.Single(ledger.Snapshot.FixAttempts!).FixFindingIds);
    }

    [Fact]
    public void Terminal_attempts_keep_the_last_result()
    {
        var ledger = LedgerWithAttemptIds();
        var result = new BuildResult("done", [], new Verification("passed", "ok"), "done");
        ledger.RecordFixAttempt("attempt", ["F-0001"], result, true);
        Assert.Equal(result.Status, Assert.Single(ledger.Snapshot.FixAttempts!).LastResult!.Status);
    }

    [Fact]
    public async Task Status_summary_has_compact_phase_lists()
    {
        var run = await NewToolRunAsync("status-summary");
        var ledger = run.ReadDecisionLedger();
        var plan = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        var json = await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId, CancellationToken.None);
        var ledgerJson = JsonNode.Parse(json)!["ledger"]!;
        Assert.Equal([plan.FindingId, code.FindingId],
            ledgerJson["unresolvedFindingIds"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal([plan.FindingId],
            ledgerJson["planReviewActiveFindingIds"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal([code.FindingId],
            ledgerJson["codeReviewActiveFindingIds"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void Projection_keeps_settled_plan_entries_for_code_review()
    {
        var ledger = Ledger();
        var plan = ledger.AddFinding(Finding("settled plan"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", plan.FindingId)), LedgerPhase.PlanReview);
        Assert.Contains(plan.FindingId, ledger.Project(LedgerPhase.CodeReview).Select(entry => entry.FindingId));
    }

    [Fact]
    public void Projection_excludes_closed_entries()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("closed"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("addressedByRevision", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.DoesNotContain(ledger.Project(LedgerPhase.PlanReview), item => item.FindingId == entry.FindingId);
    }

    [Fact]
    public void Rejected_entries_remain_until_reopening()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("rejected"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("reject", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(LedgerDisposition.Rejected, Assert.Single(ledger.Snapshot.Entries).Disposition);
    }

    [Fact]
    public void Reopening_requires_evidence()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("accept", entry.FindingId, evidence: "")), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Only_the_orchestrator_can_accept_reopening()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            new OrchestratorDecisionBatch("reopen", [new OrchestratorDecision(entry.FindingId, "accept", "user", "why", "evidence")]),
            LedgerPhase.PlanReview));
    }

    [Fact]
    public void Only_the_orchestrator_can_close()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("close"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            new OrchestratorDecisionBatch("close", [new OrchestratorDecision(entry.FindingId, "addressedByRevision", "user", "done")]),
            LedgerPhase.PlanReview));
    }

    [Fact]
    public void Defer_preserves_the_original_finding_verbatim()
    {
        var ledger = Ledger();
        var finding = Finding("  exact\r\ntext  ");
        var entry = ledger.AddFinding(finding, LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(finding, Assert.Single(ledger.Snapshot.Entries).Finding);
    }

    [Fact]
    public void Reject_preserves_the_original_finding_verbatim()
    {
        var ledger = Ledger();
        var finding = Finding("  exact\r\ntext  ");
        var entry = ledger.AddFinding(finding, LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("reject", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(finding, Assert.Single(ledger.Snapshot.Entries).Finding);
    }

    [Fact]
    public void Closure_removes_only_the_target_entry()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("addressedByRevision", entries[0].FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(entries[1].FindingId, Assert.Single(ledger.Snapshot.Entries).FindingId);
    }

    [Fact]
    public void Flow_is_not_a_critic_projection_source()
    {
        var run = NewRun("flow-projection");
        var ledger = run.ReadDecisionLedger();
        var entry = ledger.AddFinding(Finding("ledger finding"), LedgerPhase.PlanReview);
        run.AppendFlowDecisionRejected("FLOW-ONLY-MARKER", "batch", "must stay out of worker input");

        var projection = ledger.RenderProjection(LedgerPhase.PlanReview);
        Assert.Contains(entry.FindingId, projection, StringComparison.Ordinal);
        Assert.DoesNotContain("FLOW-ONLY-MARKER", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("must stay out of worker input", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void Critic_projection_contains_ids_and_active_phase()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("projection"), LedgerPhase.PlanReview);
        var projection = ledger.RenderProjection(LedgerPhase.PlanReview);
        Assert.Contains(entry.FindingId, projection, StringComparison.Ordinal);
        Assert.Contains("activePhase=plan_review", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_prompt_does_not_include_other_unresolved_ids()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("wanted"), Finding("unwanted")], LedgerPhase.CodeReview);
        var prompt = ledger.RenderFixFindings([entries[0].FindingId]);
        Assert.Contains("wanted", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("unwanted", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_numbers_are_shared_between_phases()
    {
        var ledger = Ledger();
        Assert.Equal("F-0001", ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview).FindingId);
        Assert.Equal("F-0002", ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview).FindingId);
    }

    [Fact]
    public void Closed_ids_are_not_reused()
    {
        var ledger = Ledger();
        var first = ledger.AddFinding(Finding("first"), LedgerPhase.CodeReview);
        ledger.Apply(new DecisionBatchRequest("close", [], [], [
            new LedgerClosureDecision(first.FindingId, LedgerClosureKind.Revision,
                                      LedgerDecisionMaker.Orchestrator, "done")
        ]));
        Assert.Equal("F-0002", ledger.AddFinding(Finding("second"), LedgerPhase.CodeReview).FindingId);
    }

    [Fact]
    public void New_batches_append_without_rewriting_old_results()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        var first = Batch(Decision("defer", entries[0].FindingId));
        ledger.Apply(first, LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("two", [Decision("reject", entries[1].FindingId)]), LedgerPhase.PlanReview);
        Assert.Equal([first.DecisionBatchId, "two"], ledger.Snapshot.AppliedDecisionBatches.Select(batch => batch.DecisionBatchId));
    }

    [Fact]
    public void Canonical_digest_is_stored()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("digest"), LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("batch", [Decision("defer", entry.FindingId)]), LedgerPhase.PlanReview);
        var saved = Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
        Assert.False(string.IsNullOrWhiteSpace(saved.Digest));
        Assert.Equal("orchestrator", saved.CanonicalFormat);
    }

    [Fact]
    public void Empty_decision_arrays_are_rejected()
    {
        Assert.Throws<DecisionLedgerRequestException>(() => Ledger().Apply(
            new OrchestratorDecisionBatch("empty", []), LedgerPhase.PlanReview));
    }

    [Fact]
    public async Task Empty_fix_sets_are_decisions_only()
    {
        var run = NewRun("empty-fix");
        var result = await new ReviewFix(new RecordingVendor("codex"), new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null), null, null, [], CancellationToken.None);
        Assert.Equal("done", result.Status);
    }

    [Fact]
    public void Fix_attempt_terminal_flag_requires_a_result()
    {
        var ledger = LedgerWithAttemptIds();
        Assert.Throws<DecisionLedgerStateException>(() =>
        {
            ledger.RecordFixAttempt("attempt", ["F-0001"], null, true);
            _ = ledger.Snapshot;
        });
    }

    [Fact]
    public void Fix_attempt_record_is_versioned_with_the_ledger()
    {
        var ledger = LedgerWithAttemptIds();
        ledger.RecordFixAttempt("attempt", ["F-0001"], null, false);
        Assert.Equal(1, ledger.Snapshot.SchemaVersion);
    }

    [Fact]
    public void Active_phase_is_not_changed_by_defer()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("phase"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(LedgerPhaseNames.PlanReview, Assert.Single(ledger.Snapshot.Entries).ActivePhase);
    }

    [Fact]
    public void Accepted_reopening_changes_active_phase()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("phase"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("accept", entry.FindingId, evidence: "changed")), LedgerPhase.CodeReview);
        Assert.Equal(LedgerPhaseNames.CodeReview, Assert.Single(ledger.Snapshot.Entries).ActivePhase);
    }

    [Fact]
    public void Critic_assessment_ids_do_not_allocate_findings()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("assess me"), LedgerPhase.PlanReview);
        var before = ledger.Snapshot.NextFindingNumber;

        var critique = ledger.IngestCritique(new VendorCritique
        {
            Verdict = "approve",
            Findings = [],
            Summary = "assessed",
            UnresolvedAssessments = [new UnresolvedAssessment(entry.FindingId, false, "fixed in the plan")],
            Reopenings = []
        }, LedgerPhase.PlanReview);

        Assert.Empty(critique.Findings);
        Assert.Equal(entry.FindingId, Assert.Single(critique.UnresolvedAssessments!).FindingId);
        Assert.Equal(before, ledger.Snapshot.NextFindingNumber);
    }

    [Fact]
    public void Batch_result_lists_are_sorted()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        var response = ledger.Apply(new OrchestratorDecisionBatch("batch", [
            Decision("defer", entries[1].FindingId), Decision("reject", entries[0].FindingId)
        ]), LedgerPhase.PlanReview);
        Assert.Equal(["F-0001", "F-0002"], response.Result.DecisionFindingIds);
    }

    [Fact]
    public void Reopening_a_code_origin_cannot_move_to_plan()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.CodeReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            new DecisionBatchRequest("reopen", [], [new LedgerReopeningDecision(entry.FindingId,
                LedgerPhaseNames.PlanReview, LedgerDecisionMaker.Orchestrator, "back", "evidence")], [])));
    }

    [Fact]
    public void Plan_projection_omits_code_entries()
    {
        var ledger = Ledger();
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Assert.DoesNotContain(ledger.Project(LedgerPhase.PlanReview), entry => entry.FindingId == code.FindingId);
    }

    [Fact]
    public void Code_projection_contains_active_code_entries()
    {
        var ledger = Ledger();
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Assert.Contains(code.FindingId, ledger.Project(LedgerPhase.CodeReview).Select(entry => entry.FindingId));
    }

    [Fact]
    public void Rejected_decision_maker_is_preserved()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("reject"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("reject", entry.FindingId, by: "user")), LedgerPhase.PlanReview);
        Assert.Equal(LedgerDecisionMaker.User, Assert.Single(ledger.Snapshot.Entries).Decision!.By);
    }

    [Fact]
    public void Closure_reason_is_required()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("close"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("addressedByRevision", entry.FindingId, reason: "")), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Decision_action_is_required()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("action"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("", entry.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Finding_id_format_is_strict()
    {
        Assert.Throws<DecisionLedgerRequestException>(() => Ledger().Apply(
            new OrchestratorDecisionBatch("bad", [Decision("defer", "f-1")]), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Summary_tracks_settled_dispositions()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal([entry.FindingId], ledger.Summary.DeferredFindingIds);
    }

    [Fact]
    public void Host_verified_closure_leaves_an_applied_batch()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("verified"), LedgerPhase.CodeReview);
        ledger.Apply(Batch(Decision("hostVerified", entry.FindingId, evidence: "gate passed")), LedgerPhase.CodeReview);
        Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public void Revision_closure_leaves_an_applied_batch()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("revision"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("addressedByRevision", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public void Duplicate_closure_leaves_an_applied_batch()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("duplicateOf", entries[1].FindingId, duplicateOf: entries[0].FindingId)), LedgerPhase.PlanReview);
        Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal(entries[0].FindingId, Assert.Single(ledger.Snapshot.Entries).FindingId);
    }

    [Fact]
    public void Decline_requires_a_settled_entry()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("open"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("decline", entry.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Accepted_reopening_is_unresolved_after_transition()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("reopen"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("accept", entry.FindingId, evidence: "new")), LedgerPhase.CodeReview);
        Assert.Equal(LedgerDisposition.Unresolved, Assert.Single(ledger.Snapshot.Entries).Disposition);
    }

    [Fact]
    public void Decision_batches_have_utf8_digests()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("utf8"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("defer", entry.FindingId, reason: "причина")), LedgerPhase.PlanReview);
        var saved = Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(saved.CanonicalBytes)).ToLowerInvariant(), saved.Digest);
    }

    [Fact]
    public void Rendered_projection_is_stable_for_same_current_entries()
    {
        var first = Ledger();
        first.AddFinding(Finding("same"), LedgerPhase.PlanReview);
        var second = Ledger();
        second.AddFinding(Finding("same"), LedgerPhase.PlanReview);
        Assert.Equal(first.RenderProjection(LedgerPhase.PlanReview), second.RenderProjection(LedgerPhase.PlanReview));
    }

    [Fact]
    public void Applied_batches_are_ordered_by_arrival()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("one", [Decision("defer", entries[0].FindingId)]), LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("two", [Decision("reject", entries[1].FindingId)]), LedgerPhase.PlanReview);
        Assert.Equal(["one", "two"], ledger.Snapshot.AppliedDecisionBatches.Select(batch => batch.DecisionBatchId));
    }

    [Fact]
    public void Orchestrator_preflight_does_not_mutate()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("preflight"), LedgerPhase.PlanReview);
        var before = File.ReadAllBytes(ledger.Path);
        ledger.ValidateOrchestratorBatch(Batch(Decision("defer", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
    }

    [Fact]
    public void Code_decisions_require_active_code_phase()
    {
        var ledger = Ledger();
        var plan = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("defer", plan.FindingId)), LedgerPhase.CodeReview));
    }

    [Fact]
    public void Plan_decisions_require_active_plan_phase()
    {
        var ledger = Ledger();
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            Batch(Decision("defer", code.FindingId)), LedgerPhase.PlanReview));
    }

    [Fact]
    public void Fix_attempt_result_can_be_read_after_reopen()
    {
        var ledger = LedgerWithAttemptIds();
        var result = new BuildResult("done", [], new Verification("passed", "ok"), "done");
        ledger.RecordFixAttempt("attempt", ["F-0001"], result, true);
        Assert.Equal(result.Status, DecisionLedger.Open(ledger.Path).FindFixAttempt("attempt")!.LastResult!.Status);
    }

    [Fact]
    public void Flow_audit_uses_decision_batch_identity()
    {
        var run = NewRun("flow-batch");
        run.AppendFlowDecisionBatch("test", new DecisionBatchResponse("declined",
            new DecisionBatchResult("batch", "declined", ["F-0001"], [], [])));
        Assert.Contains("batch", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_entries_are_absent_from_summary()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("closed"), LedgerPhase.CodeReview);
        ledger.Apply(Batch(Decision("hostVerified", entry.FindingId, evidence: "ok")), LedgerPhase.CodeReview);
        Assert.Empty(ledger.Summary.UnresolvedFindingIds);
    }

    [Fact]
    public async Task Plan_confirm_approved_true_requires_full_plan_coverage()
    {
        var run = await NewToolRunAsync("confirm-blocked");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("blocking"), LedgerPhase.PlanReview);
        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.ConfirmPlan(
            SessionRoots.None, _workspace, run.RunId, run.ReadPlan(), true, CancellationToken.None));
        Assert.Contains(entry.FindingId, error.Message, StringComparison.Ordinal);
        Assert.False(run.ReadState().Approved);
    }

    [Fact]
    public async Task Plan_confirm_approved_true_applies_final_closure()
    {
        var run = await NewToolRunAsync("confirm-close");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("fixed"), LedgerPhase.PlanReview);
        var result = JsonNode.Parse(await ForgeTools.ConfirmPlan(
            SessionRoots.None, _workspace, run.RunId, run.ReadPlan(), true, CancellationToken.None,
            decisions: new OrchestratorDecisionBatch("final-close", [
                Decision("addressedByRevision", entry.FindingId)
            ])))!;
        Assert.True(result["approved"]!.GetValue<bool>());
        Assert.True(run.ReadState().Approved);
        Assert.Empty(run.ReadDecisionLedger().Snapshot.Entries);
    }

    [Fact]
    public async Task Plan_confirm_does_not_apply_vendor_prompt_sensitive_input_rules()
    {
        var run = await NewToolRunAsync("confirm-local-plan");
        var plan = "# Plan\n\napi_key: Abcdefghijklmnop1234+\n\n## Approach\n\n1. Work.\n";

        var result = JsonNode.Parse(await ForgeTools.ConfirmPlan(
            SessionRoots.None, _workspace, run.RunId, plan, true, CancellationToken.None))!;

        Assert.True(result["approved"]!.GetValue<bool>());
        Assert.True(run.ReadState().Approved);
        Assert.Equal(plan, run.ReadPlan());
    }

    [Fact]
    public async Task Plan_confirm_approved_false_rejects_any_batch_without_ledger_mutation()
    {
        var run = await NewToolRunAsync("confirm-refused");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("open"), LedgerPhase.PlanReview);
        var before = File.ReadAllBytes(run.DecisionLedgerPath);
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.ConfirmPlan(
            SessionRoots.None, _workspace, run.RunId, run.ReadPlan(), false, CancellationToken.None,
            decisions: Batch(Decision("defer", entry.FindingId))));
        Assert.Equal(before, File.ReadAllBytes(run.DecisionLedgerPath));
    }

    [Fact]
    public async Task Confirm_conflicting_batch_exposes_saved_result_and_never_approves()
    {
        var run = await NewToolRunAsync("confirm-conflict");
        var entry = run.ReadDecisionLedger().AddFinding(Finding("settle"), LedgerPhase.PlanReview);
        run.ReadDecisionLedger().Apply(new OrchestratorDecisionBatch("same-key", [
            Decision("defer", entry.FindingId, reason: "saved reason")
        ]), LedgerPhase.PlanReview);

        var error = await Assert.ThrowsAsync<DecisionLedgerRequestException>(() => ForgeTools.ConfirmPlan(
            SessionRoots.None, _workspace, run.RunId, run.ReadPlan(), true, CancellationToken.None,
            decisions: new OrchestratorDecisionBatch("same-key", [
                Decision("reject", entry.FindingId, reason: "different reason")
            ])));

        Assert.Contains("saved canonical decisions", error.Message, StringComparison.Ordinal);
        Assert.Contains("saved result", error.Message, StringComparison.Ordinal);
        Assert.Contains(entry.FindingId, error.Message, StringComparison.Ordinal);
        Assert.False(run.ReadState().Approved);
        Assert.Contains("decisions conflict", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_plan_review_rejects_invalid_ledger_request_before_started_true()
    {
        var run = await NewToolRunAsync("background-preflight");
        var registry = new JobRegistry();
        var vendorCreated = false;
        await Assert.ThrowsAsync<DecisionLedgerRequestException>(() => ForgeTools.StartWork(
            registry, SessionRoots.None, _workspace, run.RunId, "plan.review", "critic", null,
            "codex", run.ReadPlan(), null, null, null, false, CancellationToken.None,
            () => { vendorCreated = true; return new RecordingVendor("codex"); },
            decisions: Batch(Decision("defer", "F-0001"))));
        Assert.False(vendorCreated);
        Assert.Null(registry.Get(run.Path));
        Assert.Contains("preflight", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_review_fix_rejects_overlapping_decision_and_fix_ids_before_started_true()
    {
        var run = NewRun("background-fix-overlap");
        var finding = run.ReadDecisionLedger().AddFinding(Finding("overlap"), LedgerPhase.CodeReview);
        var registry = new JobRegistry();
        var vendorCreated = false;

        var error = await Assert.ThrowsAsync<DecisionLedgerRequestException>(() => ForgeTools.StartWork(
            registry, SessionRoots.None, _workspace, run.RunId, "review.fix", "builder", null,
            "codex", null, null, null, null, false, CancellationToken.None,
            () => { vendorCreated = true; return new RecordingVendor("codex"); },
            decisions: new OrchestratorDecisionBatch("overlap", [Decision("defer", finding.FindingId)]),
            fixAttemptId: "attempt", fixFindingIds: [finding.FindingId]));

        Assert.Contains("cannot also appear", error.Message, StringComparison.Ordinal);
        Assert.False(vendorCreated);
        Assert.Null(registry.Get(run.Path));
        Assert.Equal(LedgerDisposition.Unresolved,
                     Assert.Single(run.ReadDecisionLedger().Snapshot.Entries).Disposition);
    }

    [Fact]
    public async Task Direct_and_background_review_fix_apply_the_same_typed_decision()
    {
        var directRun = NewRun("parity-direct");
        var directFinding = directRun.ReadDecisionLedger().AddFinding(Finding("settle"), LedgerPhase.CodeReview);
        await new ReviewFix(new RecordingVendor("codex"), new PromptLibrary(RepositoryPrompts()))
            .FixAsync(directRun, new Selection("builder", null),
                      new OrchestratorDecisionBatch("parity", [Decision("defer", directFinding.FindingId)]),
                      null, [], CancellationToken.None);

        var backgroundRun = NewRun("parity-background");
        var backgroundFinding = backgroundRun.ReadDecisionLedger().AddFinding(Finding("settle"), LedgerPhase.CodeReview);
        var registry = new JobRegistry();
        var started = JsonNode.Parse(await ForgeTools.StartWork(
            registry, SessionRoots.None, _workspace, backgroundRun.RunId, "review.fix", "builder", null,
            "codex", null, null, null, null, false, CancellationToken.None,
            () => new RecordingVendor("codex"),
            decisions: new OrchestratorDecisionBatch("parity", [Decision("defer", backgroundFinding.FindingId)]),
            fixFindingIds: []))!;
        Assert.True(started["started"]!.GetValue<bool>());

        var polled = JsonNode.Parse(await ForgeTools.PollWork(
            registry, SessionRoots.None, _workspace, backgroundRun.RunId,
            started["jobId"]!.GetValue<string>(), TimeSpan.FromSeconds(10), CancellationToken.None))!;
        Assert.Equal("succeeded", polled["state"]!.GetValue<string>());
        Assert.Equal(Assert.Single(directRun.ReadDecisionLedger().Snapshot.Entries).Disposition,
                     Assert.Single(backgroundRun.ReadDecisionLedger().Snapshot.Entries).Disposition);
    }

    [Fact]
    public async Task Decision_only_review_fix_runs_no_builder_or_gate()
    {
        var run = NewRun("decision-only");
        var result = await new ReviewFix(new RecordingVendor("codex"), new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null), null, null, [], CancellationToken.None);
        Assert.Null(result.Gate);
    }

    [Fact]
    public async Task Completed_fix_attempt_applies_new_decisions_before_returning_saved_result()
    {
        var run = NewRun("terminal-decisions");
        var ledger = run.ReadDecisionLedger();
        var entries = ledger.AddFindings([Finding("fixed"), Finding("defer now")], LedgerPhase.CodeReview);
        var result = new BuildResult("done", [], new Verification("passed", "ok"), "done");
        ledger.RecordFixAttempt("attempt", [entries[0].FindingId], result, true);

        var returned = await new ReviewFix(new RecordingVendor("codex"), new PromptLibrary(RepositoryPrompts()))
            .FixAsync(run, new Selection("builder", null),
                      new OrchestratorDecisionBatch("new-batch", [
                          Decision("defer", entries[1].FindingId)
                      ]), "attempt", [entries[0].FindingId], CancellationToken.None);

        Assert.Equal("done", returned.Status);
        Assert.Equal(LedgerDisposition.Deferred,
            run.ReadDecisionLedger().Snapshot.Entries.Single(entry => entry.FindingId == entries[1].FindingId).Disposition);
    }

    [Fact]
    public void Fix_attempt_retries_after_retained_result()
    {
        var ledger = LedgerWithAttemptIds();
        var result = new BuildResult("gate_failed", [], new Verification("passed", "ok"), "retry");
        ledger.RecordFixAttempt("attempt", ["F-0001"], result, false);
        Assert.False(ledger.FindFixAttempt("attempt")!.Terminal);
    }

    [Fact]
    public void Fix_attempt_conflict_rejects_different_set()
    {
        var ledger = LedgerWithAttemptIds();
        ledger.RecordFixAttempt("attempt", ["F-0001"], null, false);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.RecordFixAttempt("attempt", ["F-0001", "F-0002"], null, false));
    }

    [Fact]
    public void Flow_audit_contains_decision_fix_and_closure()
    {
        var run = NewRun("audit");
        run.AppendFlowDecisionBatch("fix", new DecisionBatchResponse("declined",
            new DecisionBatchResult("batch", "declined", ["F-0001"], [], [])));
        run.AppendFlowCutShort("Fixes — round 1", [], "attempt", ["F-0001"]);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("batch", flow, StringComparison.Ordinal);
        Assert.Contains("fixFindingIds: F-0001", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_review_entries_do_not_block_plan_approval()
    {
        var ledger = Ledger();
        ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Assert.Empty(ledger.Project(LedgerPhase.PlanReview));
    }

    [Fact]
    public void Plan_decision_carryover_is_visible_to_code_projection()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("carry"), LedgerPhase.PlanReview);
        ledger.Apply(Batch(Decision("reject", entry.FindingId)), LedgerPhase.PlanReview);
        Assert.Contains(entry.FindingId, ledger.Project(LedgerPhase.CodeReview).Select(item => item.FindingId));
    }

    [Fact]
    public void Postcondition_counter_is_not_advanced_by_rejected_critique()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("must be assessed"), LedgerPhase.PlanReview);
        var before = ledger.Snapshot;

        Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(new VendorCritique
        {
            Verdict = "approve",
            Findings = [new VendorFinding("major", "new.cs", "new finding")],
            Summary = "invalid coverage",
            UnresolvedAssessments = [],
            Reopenings = []
        }, LedgerPhase.PlanReview));

        var after = ledger.Snapshot;
        Assert.Equal(before.NextFindingNumber, after.NextFindingNumber);
        Assert.Equal(entry.FindingId, Assert.Single(after.Entries).FindingId);
    }

    private RunDirectory NewRun(string id, bool approved = true, string? plan = null)
    {
        var run = RunDirectory.Create(_workspace, id);
        run.WritePlan(plan ?? "# Plan\n\n## Approach\n\n1. Work.\n");
        run.WriteState(new RunState(id, _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    Approved: approved, CodeReviewRounds: 1));
        return run;
    }

    private async Task<RunDirectory> NewToolRunAsync(string id)
    {
        Directory.CreateDirectory(_workspace);
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], CancellationToken.None);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], CancellationToken.None);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "original\n");
        await git.OutputAsync(["add", "tracked.txt"], CancellationToken.None);
        await git.OutputAsync(["commit", "-qm", "initial"], CancellationToken.None);
        var baseline = await Baseline.CaptureAsync(git, CancellationToken.None);
        var run = NewRun(id, approved: false);
        run.WriteBaseline(baseline);
        run.WriteState(run.ReadState() with { BaselineHead = baseline.Head });
        return run;
    }

    private DecisionLedger Ledger() => NewRun(Guid.NewGuid().ToString("n")).ReadDecisionLedger();

    private DecisionLedger LedgerWithAttemptIds()
    {
        var ledger = Ledger();
        ledger.AddFindings([Finding("attempt one"), Finding("attempt two")], LedgerPhase.CodeReview);
        return ledger;
    }

    private static Finding Finding(string what) => new("major", "test.cs", what);

    private static OrchestratorDecisionBatch Batch(params OrchestratorDecision[] decisions) =>
        new($"batch-{Guid.NewGuid():n}", decisions);

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

    private static OrchestratorDecision Decision(string action, string findingId,
                                                 string by = "orchestrator",
                                                 string reason = "reason", string? evidence = null,
                                                 string? duplicateOf = null) =>
        new(findingId, action, by, reason, evidence, duplicateOf);
}
