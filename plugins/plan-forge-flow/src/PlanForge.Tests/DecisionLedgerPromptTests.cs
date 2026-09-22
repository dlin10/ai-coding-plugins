using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class DecisionLedgerPromptTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests",
                                                       Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Critique_schema_requires_phase_arrays_and_rejects_extra_properties()
    {
        Assert.Contains("unresolvedAssessments", Schemas.Critique.Json, StringComparison.Ordinal);
        Assert.Contains("reopenings", Schemas.Critique.Json, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", Schemas.Critique.Json, StringComparison.Ordinal);
        Assert.Contains("\"required\": [\"verdict\", \"findings\", \"summary\", \"unresolvedAssessments\", \"reopenings\"]",
            Schemas.Critique.Json, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"verdict\":\"approve\",\"findings\":[],\"summary\":\"ok\",\"unresolvedAssessments\":[],\"reopenings\":[],\"extra\":1}",
            CritiqueJson.Default.VendorCritique));
    }

    [Fact]
    public void Strict_critique_handling_does_not_change_other_vendor_contracts()
    {
        var build = JsonSerializer.Deserialize(
            "{\"status\":\"done\",\"filesChanged\":[],\"verification\":{\"outcome\":\"passed\",\"evidence\":\"ok\"},\"summary\":\"done\",\"extra\":1}",
            ContractJson.Default.BuildResult);
        var scout = JsonSerializer.Deserialize(
            "{\"summary\":\"done\",\"confirmedFacts\":[],\"materialAssumptions\":[],\"openDecisions\":[],\"likelyChangeSurface\":[],\"verificationEvidence\":[],\"extra\":1}",
            ContractJson.Default.ScoutReport);

        Assert.NotNull(build);
        Assert.NotNull(scout);
    }

    [Fact]
    public void Plan_projection_contains_only_active_plan_entries()
    {
        var ledger = Ledger();
        var plan = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);

        var prompt = ledger.RenderProjection(LedgerPhase.PlanReview);

        Assert.Contains(plan.FindingId, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(code.FindingId, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_projection_contains_settled_plan_and_active_code_entries()
    {
        var ledger = Ledger();
        var unresolvedPlan = ledger.AddFinding(Finding("plan unresolved"), LedgerPhase.PlanReview);
        var settledPlan = ledger.AddFinding(Finding("plan settled"), LedgerPhase.PlanReview);
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);
        Settle(ledger, settledPlan, "carry over");

        var prompt = ledger.RenderProjection(LedgerPhase.CodeReview);

        Assert.DoesNotContain(unresolvedPlan.FindingId, prompt, StringComparison.Ordinal);
        Assert.Contains(settledPlan.FindingId, prompt, StringComparison.Ordinal);
        Assert.Contains(code.FindingId, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_projection_includes_a_reopened_plan_origin_entry()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("reopen"), LedgerPhase.PlanReview);
        Settle(ledger, entry, "not now");
        Reopen(ledger, entry, "new code evidence");

        var prompt = ledger.RenderProjection(LedgerPhase.CodeReview);

        Assert.Contains($"{entry.FindingId} | origin=plan_review | activePhase=code_review | disposition=unresolved",
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.FindingId, ledger.RenderProjection(LedgerPhase.PlanReview),
                              StringComparison.Ordinal);
    }

    [Fact]
    public void Accepted_cross_phase_reopening_keeps_origin_and_moves_active_phase()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("cross phase"), LedgerPhase.PlanReview);
        Settle(ledger, entry, "deferred");
        Reopen(ledger, entry, "code changed");

        var saved = Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal(LedgerPhaseNames.PLAN_REVIEW, saved.Origin);
        Assert.Equal(LedgerPhaseNames.CODE_REVIEW, saved.ActivePhase);
        Assert.Equal(LedgerDisposition.Unresolved, saved.Disposition);
    }

    [Fact]
    public void Declined_reopening_is_a_flow_audit_without_ledger_mutation()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        Settle(ledger, entry, "defer");
        var before = File.ReadAllBytes(ledger.Path);
        var result = ledger.IngestCritique(Critique(reopenings: [new ReopeningProposal(entry.FindingId, "new evidence")]),
                                           LedgerPhase.PlanReview);
        run.AppendFlowCritique("Plan review", 1, result);

        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
        Assert.Equal(LedgerDisposition.Deferred, ledger.Snapshot.Entries.Single().Disposition);
        Assert.Contains("Reopening proposals", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Contains(entry.FindingId, File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_unresolved_id_requires_exactly_one_assessment()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("unresolved"), LedgerPhase.PlanReview);

        var error = Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(
            Critique(), LedgerPhase.PlanReview));

        Assert.Contains(entry.FindingId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_assessment_ids_are_rejected()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("duplicate"), LedgerPhase.PlanReview);
        IReadOnlyList<UnresolvedAssessment> assessments = [new UnresolvedAssessment(entry.FindingId, true, "one"),
                                                           new UnresolvedAssessment(entry.FindingId, true, "two")];

        var error = Assert.Throws<DecisionLedgerCritiqueException>(() =>
        {
            ledger.IngestCritique(Critique(assessments: assessments), LedgerPhase.PlanReview);
        });

        Assert.Contains("duplicate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_assessment_ids_are_rejected()
    {
        var ledger = Ledger();

        var error = Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(
            Critique(assessments: [new UnresolvedAssessment("F-0009", true, "evidence")]),
            LedgerPhase.PlanReview));

        Assert.Contains("unexpected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopening_only_accepts_settled_projection_ids()
    {
        var ledger = Ledger();
        var unresolved = ledger.AddFinding(Finding("unresolved"), LedgerPhase.PlanReview);

        var error = Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(
            Critique(assessments: [Assessment(unresolved)],
                     reopenings: [new ReopeningProposal(unresolved.FindingId, "evidence")]),
            LedgerPhase.PlanReview));

        Assert.Contains("unexpected reopening", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void New_findings_are_identified_and_allocated_only_after_validation()
    {
        var ledger = Ledger();
        var result = ledger.IngestCritique(Critique(findings: [new VendorFinding("major", "where", "what")]),
                                           LedgerPhase.PlanReview);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("F-0001", finding.FindingId);
        Assert.Equal("what", ledger.Snapshot.Entries.Single().Finding.What);
    }

    [Fact]
    public void Existing_unresolved_findings_are_assessed_without_new_ids()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("existing"), LedgerPhase.PlanReview);
        var result = ledger.IngestCritique(Critique(assessments: [Assessment(entry)]), LedgerPhase.PlanReview);

        Assert.Empty(result.Findings);
        Assert.Equal(entry.FindingId, Assert.Single(result.UnresolvedAssessments!).FindingId);
        Assert.Equal(entry.FindingId, ledger.Snapshot.Entries.Single().FindingId);
    }

    [Fact]
    public void Closed_entries_are_absent_from_later_projections()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("closed"), LedgerPhase.PlanReview);
        ledger.Apply(new DecisionBatchRequest("close", [], [],
            [new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.Revision,
                                        LedgerDecisionMaker.Orchestrator, "fixed")]));

        Assert.DoesNotContain(entry.FindingId, ledger.RenderProjection(LedgerPhase.PlanReview),
                              StringComparison.Ordinal);
        Assert.DoesNotContain(entry.FindingId, ledger.RenderProjection(LedgerPhase.CodeReview),
                              StringComparison.Ordinal);
    }

    [Fact]
    public void Settled_plan_decisions_carry_over_to_code_without_being_reassessed_as_unresolved()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("carry"), LedgerPhase.PlanReview);
        Settle(ledger, entry, "out of scope");
        var result = ledger.IngestCritique(Critique(reopenings: []), LedgerPhase.CodeReview);

        Assert.Empty(result.UnresolvedAssessments!);
        Assert.Contains(entry.FindingId, ledger.RenderProjection(LedgerPhase.CodeReview), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_critique_does_not_allocate_a_finding()
    {
        var ledger = Ledger();

        Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(
            Critique(findings: [new VendorFinding("minor", "where", "")]), LedgerPhase.PlanReview));

        Assert.Equal(1, ledger.Snapshot.NextFindingNumber);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public async Task Invalid_response_is_rejected_without_spending_a_review_round()
    {
        var run = NewRun();
        run.WritePlan("# plan");
        var existing = run.ReadDecisionLedger().AddFinding(Finding("existing"), LedgerPhase.PlanReview);
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new VendorCritique
        {
            Verdict = "revise", Findings = [], Summary = "missing coverage",
            UnresolvedAssessments = [], Reopenings = []
        });

        await Assert.ThrowsAsync<DecisionLedgerCritiqueException>(() =>
            new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
                .ReviewAsync(run, "# plan", new Selection("model", null), null, null, false,
                             CancellationToken.None));

        Assert.Equal(0, run.ReadState().ReviewRounds);
        Assert.Contains("rejected", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Equal(existing.FindingId, run.ReadDecisionLedger().Snapshot.Entries.Single().FindingId);
    }

    [Fact]
    public async Task The_same_applied_decision_batch_is_safe_when_a_plan_review_is_retried()
    {
        var run = NewRun();
        var entry = run.ReadDecisionLedger().AddFinding(Finding("settle"), LedgerPhase.PlanReview);
        var batch = new OrchestratorDecisionBatch("retry-batch", [
            new OrchestratorDecision(entry.FindingId, "defer", "user", "later")
        ]);
        var critic = new RecordingVendor("claude");
        critic.Enqueue(Critique());
        critic.Enqueue(Critique());
        var act = new PlanReview(critic, new PromptLibrary(RepositoryPrompts()));

        await act.ReviewAsync(run, "# first", new Selection("model", null), null, null, false,
                              CancellationToken.None, batch);
        await act.ReviewAsync(run, "# second", new Selection("model", null), "revised", null, false,
                              CancellationToken.None, batch);

        Assert.Single(run.ReadDecisionLedger().Snapshot.AppliedDecisionBatches);
        Assert.Equal(2, run.ReadState().ReviewRounds);
        Assert.Equal(1, Count(File.ReadAllText(run.FlowLogPath), "decisionBatchId: retry-batch"));
    }

    [Fact]
    public void Projection_rendering_is_stable_for_the_same_current_entries()
    {
        var ledger = Ledger();
        ledger.AddFinding(Finding("same"), LedgerPhase.CodeReview);
        var first = ledger.RenderProjection(LedgerPhase.CodeReview);

        Assert.Equal(first, ledger.RenderProjection(LedgerPhase.CodeReview));
    }

    [Fact]
    public void Projection_order_is_finding_id_order_not_insertion_history()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two"), Finding("three")], LedgerPhase.PlanReview);

        var ids = ledger.Project(LedgerPhase.PlanReview).Select(entry => entry.FindingId).ToArray();

        Assert.Equal(entries.Select(entry => entry.FindingId), ids);
    }

    [Fact]
    public void Assessment_evidence_must_be_non_empty()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("evidence"), LedgerPhase.PlanReview);

        var error = Assert.Throws<DecisionLedgerCritiqueException>(() => ledger.IngestCritique(
            Critique(assessments: [new UnresolvedAssessment(entry.FindingId, true, " ")]),
            LedgerPhase.PlanReview));

        Assert.Contains("evidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N_closed_rounds_with_same_current_entries_produce_byte_identical_critic_input()
    {
        var ledger = Ledger();
        for (var index = 0; index < 5; index++)
        {
            var closed = ledger.AddFinding(Finding($"closed {index}"), LedgerPhase.PlanReview);
            ledger.Apply(new DecisionBatchRequest($"close-{index}", [], [],
                [new LedgerClosureDecision(closed.FindingId, LedgerClosureKind.Revision, LedgerDecisionMaker.Orchestrator,
                                            "fixed")]));
        }
        ledger.AddFinding(Finding("current"), LedgerPhase.PlanReview);

        var inputs = Enumerable.Range(0, 5)
            .Select(_ => System.Text.Encoding.UTF8.GetBytes(ledger.RenderProjection(LedgerPhase.PlanReview)))
            .ToArray();

        Assert.All(inputs, input => Assert.Equal(inputs[0], input));
    }

    private RunDirectory NewRun()
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    BaselineHead: "baseline", CodeReviewRoundCap: 3));
        return run;
    }

    private DecisionLedger Ledger() => DecisionLedger.Open(NewRun());

    private static Finding Finding(string what) => new("major", "test", what);

    private static UnresolvedAssessment Assessment(DecisionLedgerEntry entry) =>
        new(entry.FindingId, true, "still present in current material");

    private static VendorCritique Critique(IReadOnlyList<VendorFinding>? findings = null,
                                           IReadOnlyList<UnresolvedAssessment>? assessments = null,
                                           IReadOnlyList<ReopeningProposal>? reopenings = null) => new()
    {
        Verdict = "approve",
        Findings = findings ?? [],
        Summary = "checked",
        UnresolvedAssessments = assessments ?? [],
        Reopenings = reopenings ?? []
    };

    private static void Settle(DecisionLedger ledger, DecisionLedgerEntry entry, string reason) =>
        ledger.Apply(new DecisionBatchRequest($"settle-{entry.FindingId}",
            [new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                           LedgerDecisionMaker.User, reason)], [], []));

    private static void Reopen(DecisionLedger ledger, DecisionLedgerEntry entry, string evidence) =>
        ledger.Apply(new DecisionBatchRequest($"reopen-{entry.FindingId}", [],
            [new LedgerReopeningDecision(entry.FindingId, LedgerPhaseNames.CODE_REVIEW,
                                         LedgerDecisionMaker.Orchestrator, "accept", evidence)], []));

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

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
