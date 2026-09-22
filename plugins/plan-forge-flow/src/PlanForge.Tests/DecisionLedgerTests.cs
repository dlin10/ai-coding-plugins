using System.Text.Json;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class DecisionLedgerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_new_run_has_an_indented_versioned_snapshot()
    {
        var run = NewRun();

        Assert.True(File.Exists(run.DecisionLedgerPath));
        using var json = JsonDocument.Parse(File.ReadAllText(run.DecisionLedgerPath));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("nextFindingNumber").GetInt32());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("entries").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("appliedDecisionBatches").ValueKind);
        Assert.Contains("\n  \"schemaVersion\"", File.ReadAllText(run.DecisionLedgerPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_numbers_are_run_local_monotonic_and_never_reused()
    {
        var ledger = Ledger();
        var first = ledger.AddFinding(Finding("first"), LedgerPhase.PlanReview);
        var second = ledger.AddFinding(Finding("second"), LedgerPhase.CodeReview);

        ledger.Apply(new DecisionBatchRequest("close-first", [], [],
            [new LedgerClosureDecision(first.FindingId, LedgerClosureKind.Revision,
                                       LedgerDecisionMaker.Orchestrator, "implemented")]));
        var third = ledger.AddFinding(Finding("third"), LedgerPhase.PlanReview);

        Assert.Equal("F-0001", first.FindingId);
        Assert.Equal("F-0002", second.FindingId);
        Assert.Equal("F-0003", third.FindingId);
        Assert.Equal(4, ledger.Snapshot.NextFindingNumber);
    }

    [Fact]
    public void New_entries_keep_origin_and_active_phase()
    {
        var ledger = Ledger();

        var plan = ledger.AddFinding(Finding("plan"), LedgerPhase.PlanReview);
        var code = ledger.AddFinding(Finding("code"), LedgerPhase.CodeReview);

        Assert.Equal(LedgerPhaseNames.PlanReview, plan.Origin);
        Assert.Equal(LedgerPhaseNames.PlanReview, plan.ActivePhase);
        Assert.Equal(LedgerPhaseNames.CodeReview, code.Origin);
        Assert.Equal(LedgerPhaseNames.CodeReview, code.ActivePhase);
    }

    [Fact]
    public void Deferred_entries_preserve_the_finding_decision_maker_and_reason()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("  verbatim\n"), LedgerPhase.PlanReview);

        ledger.Apply(new DecisionBatchRequest("defer-1",
            [new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                            LedgerDecisionMaker.User, "out of scope")], [], []));

        var saved = Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal("  verbatim\n", saved.Finding.What);
        Assert.Equal(LedgerDisposition.Deferred, saved.Disposition);
        Assert.Equal(LedgerDecisionMaker.User, saved.Decision!.By);
        Assert.Equal("out of scope", saved.Decision.Reason);
    }

    [Fact]
    public void Rejected_entries_preserve_the_original_finding()
    {
        var ledger = Ledger();
        var finding = Finding("exact\r\ntext");
        var entry = ledger.AddFinding(finding, LedgerPhase.PlanReview);

        ledger.Apply(new DecisionBatchRequest("reject-1",
            [new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Rejected,
                                            LedgerDecisionMaker.Orchestrator, "not a defect")], [], []));

        var saved = Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal(finding, saved.Finding);
        Assert.Equal(LedgerDisposition.Rejected, saved.Disposition);
    }

    [Fact]
    public void Reopening_a_plan_decision_moves_only_the_active_phase_to_code_review()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("settled"), LedgerPhase.PlanReview);
        ledger.Apply(new DecisionBatchRequest("defer-1",
            [new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                            LedgerDecisionMaker.User, "later")], [], []));

        ledger.Apply(new DecisionBatchRequest("reopen-1", [],
            [new LedgerReopeningDecision(entry.FindingId, LedgerPhaseNames.CodeReview,
                                          LedgerDecisionMaker.Orchestrator, "code changed", "new diff")], []));

        var reopened = Assert.Single(ledger.Snapshot.Entries);
        Assert.Equal(LedgerPhaseNames.PlanReview, reopened.Origin);
        Assert.Equal(LedgerPhaseNames.CodeReview, reopened.ActivePhase);
        Assert.Equal(LedgerDisposition.Unresolved, reopened.Disposition);
        Assert.Equal("new diff", reopened.Reopening!.Evidence);
    }

    [Fact]
    public void Closure_removes_the_entry_but_keeps_the_applied_batch()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("fixed"), LedgerPhase.CodeReview);

        var response = ledger.Apply(new DecisionBatchRequest("close-1", [], [],
            [new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.HostVerified,
                                       LedgerDecisionMaker.Orchestrator, "verified", "test passed")]));

        Assert.Empty(ledger.Snapshot.Entries);
        Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
        Assert.Equal("applied", response.Outcome);
        Assert.Equal([entry.FindingId], response.Result.ClosedFindingIds);
    }

    [Fact]
    public void Malformed_state_is_rejected_without_creating_a_replacement()
    {
        var run = NewRun();
        File.WriteAllText(run.DecisionLedgerPath, "{ not json");

        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(run));
        Assert.Equal("{ not json", File.ReadAllText(run.DecisionLedgerPath));
    }

    [Fact]
    public void Numeric_enum_values_are_rejected_as_malformed_state()
    {
        var run = NewRun();
        run.ReadDecisionLedger().AddFinding(Finding("typed"), LedgerPhase.PlanReview);
        var json = File.ReadAllText(run.DecisionLedgerPath);
        var malformed = json.Replace("\"disposition\": \"unresolved\"", "\"disposition\": 0",
                                     StringComparison.Ordinal);
        Assert.NotEqual(json, malformed);
        File.WriteAllText(run.DecisionLedgerPath, malformed);

        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(run));
    }

    [Fact]
    public void An_unknown_schema_version_is_rejected()
    {
        var run = NewRun();
        File.WriteAllText(run.DecisionLedgerPath,
            "{\"schemaVersion\":99,\"nextFindingNumber\":1,\"entries\":[],\"appliedDecisionBatches\":[]}");

        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(run));
    }

    [Fact]
    public void Entries_must_be_ordered_and_below_the_next_number()
    {
        var run = NewRun();
        File.WriteAllText(run.DecisionLedgerPath,
            """
            {
              "schemaVersion": 1,
              "nextFindingNumber": 3,
              "entries": [
                { "findingId": "F-0002", "origin": "plan_review", "activePhase": "plan_review", "finding": { "severity": "major", "where": "a", "what": "a" }, "disposition": "unresolved" },
                { "findingId": "F-0001", "origin": "plan_review", "activePhase": "plan_review", "finding": { "severity": "major", "where": "b", "what": "b" }, "disposition": "unresolved" }
              ],
              "appliedDecisionBatches": []
            }
            """);

        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(run));
    }

    [Fact]
    public void Canonical_bytes_have_fixed_property_order_and_empty_arrays()
    {
        var request = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision("F-0001", LedgerDisposition.Deferred, LedgerDecisionMaker.User, "reason")
        ], [], []);

        var canonical = DecisionLedger.CanonicalBytes(request);

        Assert.Equal(
            "{\"decisionBatchId\":\"batch\",\"decisions\":[{\"findingId\":\"F-0001\",\"disposition\":\"deferred\",\"by\":\"user\",\"reason\":\"reason\"}],\"reopenings\":[],\"closures\":[]}",
            System.Text.Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Canonical_arrays_are_sorted_by_finding_id()
    {
        var request = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision("F-0002", LedgerDisposition.Deferred, LedgerDecisionMaker.User, "two"),
            new LedgerDispositionDecision("F-0001", LedgerDisposition.Rejected, LedgerDecisionMaker.User, "one")
        ], [], []);

        var canonical = System.Text.Encoding.UTF8.GetString(DecisionLedger.CanonicalBytes(request));

        Assert.True(canonical.IndexOf("F-0001", StringComparison.Ordinal)
                    < canonical.IndexOf("F-0002", StringComparison.Ordinal));
    }

    [Fact]
    public void Canonicalization_keeps_strings_verbatim()
    {
        var reason = "  не менять\r\nстроку  ";
        var request = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision("F-0001", LedgerDisposition.Deferred, LedgerDecisionMaker.User, reason)
        ], [], []);

        using var canonical = JsonDocument.Parse(DecisionLedger.CanonicalBytes(request));

        Assert.Equal(reason, canonical.RootElement.GetProperty("decisions")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Null_arrays_are_not_an_empty_canonical_batch()
    {
        var request = new DecisionBatchRequest("batch", null!, [], []);

        Assert.Throws<DecisionLedgerRequestException>(() => DecisionLedger.CanonicalBytes(request));
    }

    [Fact]
    public void An_identical_batch_is_a_no_op_with_the_saved_result()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("defer"), LedgerPhase.PlanReview);
        var request = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                          LedgerDecisionMaker.User, "later")
        ], [], []);
        ledger.Apply(request);
        var before = File.ReadAllBytes(ledger.Path);

        var retry = ledger.Apply(request);

        Assert.Equal("no_op", retry.Outcome);
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
        Assert.Single(ledger.Snapshot.AppliedDecisionBatches);
    }

    [Fact]
    public void A_batch_id_conflict_returns_the_saved_result_without_mutation()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("defer"), LedgerPhase.PlanReview);
        var first = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                          LedgerDecisionMaker.User, "first")
        ], [], []);
        ledger.Apply(first);
        var before = File.ReadAllBytes(ledger.Path);
        var conflict = new DecisionBatchRequest("batch", [
            new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                          LedgerDecisionMaker.User, "different")
        ], [], []);

        var response = ledger.Apply(conflict);

        Assert.Equal("conflict", response.Outcome);
        Assert.Equal("first", response.Result.DecisionFindingIds.Single() == entry.FindingId
            ? ledger.Snapshot.Entries.Single().Decision!.Reason : "");
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
    }

    [Fact]
    public void A_new_legal_batch_is_appended_after_an_earlier_batch()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("one"), Finding("two")], LedgerPhase.PlanReview);
        ledger.Apply(new DecisionBatchRequest("one", [
            new LedgerDispositionDecision(entries[0].FindingId, LedgerDisposition.Deferred,
                                          LedgerDecisionMaker.User, "one")
        ], [], []));

        var response = ledger.Apply(new DecisionBatchRequest("two", [
            new LedgerDispositionDecision(entries[1].FindingId, LedgerDisposition.Rejected,
                                          LedgerDecisionMaker.Orchestrator, "two")
        ], [], []));

        Assert.Equal("applied", response.Outcome);
        Assert.Equal(2, ledger.Snapshot.AppliedDecisionBatches.Count);
        Assert.Equal([LedgerDisposition.Deferred, LedgerDisposition.Rejected],
                     ledger.Snapshot.Entries.Select(entry => entry.Disposition));
    }

    [Fact]
    public async Task Concurrent_allocations_are_serialized_per_path()
    {
        var ledger = Ledger();

        var allocated = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => ledger.AddFinding(Finding("concurrent"), LedgerPhase.CodeReview))));

        Assert.Equal(24, allocated.Select(entry => entry.FindingId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(25, ledger.Snapshot.NextFindingNumber);
        Assert.Equal(24, ledger.Snapshot.Entries.Count);
    }

    [Fact]
    public void Atomic_replacement_leaves_a_complete_snapshot()
    {
        var ledger = Ledger();

        for (var index = 0; index < 20; index++)
            ledger.AddFinding(Finding(index.ToString()), LedgerPhase.PlanReview);

        var snapshot = ledger.Snapshot;
        Assert.Equal(21, snapshot.NextFindingNumber);
        Assert.Equal(20, snapshot.Entries.Count);
        Assert.DoesNotContain("\"entries\": null", File.ReadAllText(ledger.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Illegal_repeated_or_unknown_operations_are_rejected()
    {
        var ledger = Ledger();

        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            new DecisionBatchRequest("missing", [
                new LedgerDispositionDecision("F-0001", LedgerDisposition.Deferred,
                                              LedgerDecisionMaker.User, "missing")
            ], [], [])));

        var entry = ledger.AddFinding(Finding("once"), LedgerPhase.PlanReview);
        ledger.Apply(new DecisionBatchRequest("settle", [
            new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Deferred,
                                          LedgerDecisionMaker.User, "once")
        ], [], []));

        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(
            new DecisionBatchRequest("again", [
                new LedgerDispositionDecision(entry.FindingId, LedgerDisposition.Rejected,
                                              LedgerDecisionMaker.User, "again")
            ], [], [])));
    }

    [Fact]
    public void Typed_closure_requirements_are_validated()
    {
        var ledger = Ledger();
        var entry = ledger.AddFinding(Finding("close"), LedgerPhase.CodeReview);

        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(new DecisionBatchRequest(
            "bad", [], [], [new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.HostVerified,
                                                       LedgerDecisionMaker.Orchestrator, "verified")])));
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.Apply(new DecisionBatchRequest(
            "bad-duplicate", [], [], [new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.Duplicate,
                                                                  LedgerDecisionMaker.Orchestrator, "duplicate")] )));
    }

    private RunDirectory NewRun() => RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));

    private DecisionLedger Ledger() => DecisionLedger.Open(NewRun());

    private static Finding Finding(string what) => new("major", "test", what);
}
