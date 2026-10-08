using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class PendingFullGateStateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void V3_json_summary_and_critic_projection_expose_only_current_pending_groups()
    {
        var ledger = PendingLedger();
        var json = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        Assert.Equal(3, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("targeted", json["fixAttempts"]![0]!["gateMode"]!.GetValue<string>());
        Assert.Equal("z-attempt", json["entries"]![0]!["pendingFullGateAttemptId"]!.GetValue<string>());
        Assert.Null(json["entries"]![3]!["pendingFullGateAttemptId"]);
        Assert.Null(json["fixAttempts"]![0]!["lastResult"]!["gateMode"]);
        Assert.Null(json["fixAttempts"]![0]!["lastResult"]!["pendingFullGateFindingIds"]);

        var summary = JsonNode.Parse(JsonSerializer.Serialize(ledger.Summary, ForgeToolJson.Default.LedgerSummary))!.ToJsonString();
        Assert.Equal("{\"unresolvedFindingIds\":[\"F-0001\",\"F-0002\",\"F-0003\",\"F-0004\"]," +
                     "\"deferredFindingIds\":[],\"rejectedFindingIds\":[]," +
                     "\"planReviewActiveFindingIds\":[],\"codeReviewActiveFindingIds\":[\"F-0001\",\"F-0002\",\"F-0003\",\"F-0004\"]," +
                     "\"pendingFullGateFindingIds\":[\"F-0001\",\"F-0002\",\"F-0003\"]," +
                     "\"pendingFullGateAttempts\":[{\"fixAttemptId\":\"a-attempt\",\"findingIds\":[\"F-0003\"]}," +
                     "{\"fixAttemptId\":\"z-attempt\",\"findingIds\":[\"F-0001\",\"F-0002\"]}]}", summary);

        var projection = ledger.RenderProjection(LedgerPhase.CodeReview);
        Assert.Contains("F-0001 | origin=code_review | activePhase=code_review | disposition=unresolved", projection);
        Assert.Contains("pending full host verification: targeted attempt z-attempt", projection);
        Assert.Contains("pending full host verification: targeted attempt a-attempt", projection);
        Assert.DoesNotContain("old-attempt", projection);
        Assert.DoesNotContain("pending full", ledger.RenderProjection(LedgerPhase.PlanReview));

        ledger.Apply(new DecisionBatchRequest("verified", [], [],
            [new LedgerClosureDecision("F-0003", LedgerClosureKind.HostVerified,
                                       LedgerDecisionMaker.Orchestrator, "all gates passed", "host commands exited 0")]));
        Assert.Equal("z-attempt", Assert.Single(ledger.Summary.PendingFullGateAttempts).FixAttemptId);
        Assert.Equal(3, ledger.Snapshot.FixAttempts!.Count);
    }

    [Fact]
    public void Empty_summary_arrays_are_arrays_and_null_response_annotations_are_omitted()
    {
        var ledger = Ledger();
        var summary = JsonNode.Parse(JsonSerializer.Serialize(ledger.Summary, ForgeToolJson.Default.LedgerSummary))!;
        Assert.Equal("[]", summary["pendingFullGateFindingIds"]!.ToJsonString());
        Assert.Equal("[]", summary["pendingFullGateAttempts"]!.ToJsonString());
        var result = Passed();
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, ContractJson.Default.BuildResult))!;
        Assert.False(json.AsObject().ContainsKey("gateMode"));
        Assert.False(json.AsObject().ContainsKey("pendingFullGateFindingIds"));
        result = result with { GateMode = "targeted", PendingFullGateFindingIds = [] };
        json = JsonNode.Parse(JsonSerializer.Serialize(result, ContractJson.Default.BuildResult))!;
        Assert.Equal("targeted", json["gateMode"]!.GetValue<string>());
        Assert.Equal("[]", json["pendingFullGateFindingIds"]!.ToJsonString());
        Assert.DoesNotContain("gateMode", Schemas.BuildResult.Json);
        Assert.DoesNotContain("pendingFullGateFindingIds", Schemas.BuildResult.Json);
    }

    [Fact]
    public void V2_full_history_is_read_without_rewrite_and_migrates_only_on_mutation()
    {
        var ledger = Ledger();
        var entries = ledger.AddFindings([Finding("plan"), Finding("closed")], LedgerPhase.PlanReview);
        var defer = new DecisionBatchRequest("defer", [new LedgerDispositionDecision(entries[0].FindingId,
            LedgerDisposition.Deferred, LedgerDecisionMaker.User, "later")], [], []);
        var canonical = DecisionLedger.CanonicalBytes(defer);
        ledger.Apply(defer);
        ledger.Apply(new DecisionBatchRequest("reopen", [], [new LedgerReopeningDecision(entries[0].FindingId,
            LedgerPhaseNames.CODE_REVIEW, LedgerDecisionMaker.Orchestrator, "needed", "new evidence")], []));
        ledger.Apply(new OrchestratorDecisionBatch("raise", [],
            [new OrchestratorRaise("major", "test", "raised", "orchestrator", "new rule")]), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("history", [entries[1].FindingId], Passed(), true);
        ledger.Apply(new DecisionBatchRequest("close", [], [], [new LedgerClosureDecision(entries[1].FindingId,
            LedgerClosureKind.Revision, LedgerDecisionMaker.Orchestrator, "implemented")]));
        var fixture = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        fixture["schemaVersion"] = 2;
        fixture["fixAttempts"]![0]!.AsObject().Remove("gateMode");
        Save(ledger, fixture);
        var bytes = File.ReadAllBytes(ledger.Path);
        var batches = fixture["appliedDecisionBatches"]!.ToJsonString();
        var oldEntries = fixture["entries"]!.ToJsonString();

        ledger = DecisionLedger.Open(ledger.Path);
        Assert.Equal(2, ledger.Snapshot.SchemaVersion);
        Assert.Equal("full", ledger.FindFixAttempt("history")!.GateMode);
        Assert.Empty(ledger.Summary.PendingFullGateFindingIds);
        Assert.Contains("raised", ledger.RenderProjection(LedgerPhase.CodeReview));
        Assert.Equal("no_op", ledger.Apply(defer).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(ledger.Path));
        Assert.Equal(canonical, DecisionLedger.CanonicalBytes(defer));

        ledger.AddFinding(Finding("next"), LedgerPhase.CodeReview);
        var migrated = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        Assert.Equal(3, migrated["schemaVersion"]!.GetValue<int>());
        Assert.Equal("full", migrated["fixAttempts"]![0]!["gateMode"]!.GetValue<string>());
        Assert.Equal(batches, migrated["appliedDecisionBatches"]!.ToJsonString());
        Assert.Equal(oldEntries, new JsonArray(migrated["entries"]!.AsArray().Take(2).Select(node => node!.DeepClone()).ToArray()).ToJsonString());
        Assert.Equal(5, ledger.Snapshot.NextFindingNumber);
        Assert.Equal("F-0004", ledger.Snapshot.Entries.Last().FindingId);
        Assert.Equal(canonical, DecisionLedger.CanonicalBytes(defer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V2_missing_or_null_attempts_remain_readable(bool explicitNull)
    {
        var ledger = Ledger();
        var fixture = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        fixture["schemaVersion"] = 2;
        if (explicitNull) fixture["fixAttempts"] = null;
        else fixture.AsObject().Remove("fixAttempts");
        Save(ledger, fixture);
        var bytes = File.ReadAllBytes(ledger.Path);
        Assert.Empty(DecisionLedger.Open(ledger.Path).Snapshot.FixAttempts!);
        Assert.Equal(bytes, File.ReadAllBytes(ledger.Path));
        ledger.AddFinding(Finding("mutation"), LedgerPhase.PlanReview);
        Assert.Equal(3, ledger.Snapshot.SchemaVersion);
    }

    [Theory]
    [InlineData("missing-mode")]
    [InlineData("null-mode")]
    [InlineData("bad-mode")]
    [InlineData("numeric-mode")]
    [InlineData("v2-mode")]
    [InlineData("v2-pending")]
    [InlineData("unknown-version")]
    [InlineData("missing-version")]
    [InlineData("missing-attempt")]
    [InlineData("wrong-coverage")]
    [InlineData("nonterminal")]
    [InlineData("full-mode")]
    [InlineData("failed-gate")]
    [InlineData("plan-phase")]
    [InlineData("settled")]
    [InlineData("response-annotations")]
    public void Corrupt_format_or_pending_links_are_rejected_without_rewrite(string corruption)
    {
        var ledger = PendingLedger();
        var fixture = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        var attempt = fixture["fixAttempts"]![0]!;
        var entry = fixture["entries"]![0]!;
        switch (corruption)
        {
            case "missing-mode": attempt.AsObject().Remove("gateMode"); break;
            case "null-mode": attempt["gateMode"] = null; break;
            case "bad-mode": attempt["gateMode"] = "Targeted"; break;
            case "numeric-mode": attempt["gateMode"] = 1; break;
            case "v2-mode": fixture["schemaVersion"] = 2; foreach (var item in fixture["entries"]!.AsArray()) item!.AsObject().Remove("pendingFullGateAttemptId"); break;
            case "v2-pending": fixture["schemaVersion"] = 2; foreach (var item in fixture["fixAttempts"]!.AsArray()) item!.AsObject().Remove("gateMode"); break;
            case "unknown-version": fixture["schemaVersion"] = 4; break;
            case "missing-version": fixture.AsObject().Remove("schemaVersion"); break;
            case "missing-attempt": entry["pendingFullGateAttemptId"] = "missing"; break;
            case "wrong-coverage": entry["pendingFullGateAttemptId"] = "a-attempt"; break;
            case "nonterminal": attempt["terminal"] = false; break;
            case "full-mode": attempt["gateMode"] = "full"; break;
            case "failed-gate": attempt["lastResult"]!["gate"]!["outcome"] = "failed"; break;
            case "plan-phase": entry["origin"] = "plan_review"; entry["activePhase"] = "plan_review"; break;
            case "settled": entry["disposition"] = "deferred"; break;
            case "response-annotations": attempt["lastResult"]!["gateMode"] = "targeted"; break;
        }
        Save(ledger, fixture);
        var bytes = File.ReadAllBytes(ledger.Path);
        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(ledger.Path));
        Assert.Equal(bytes, File.ReadAllBytes(ledger.Path));
    }

    [Fact]
    public void Targeted_terminal_attempt_requires_passed_even_after_all_findings_close()
    {
        var ledger = PendingLedger();
        ledger.Apply(new DecisionBatchRequest("close-all", [], [], ledger.Snapshot.Entries.Select(entry =>
            new LedgerClosureDecision(entry.FindingId, LedgerClosureKind.HostVerified,
                LedgerDecisionMaker.Orchestrator, "verified", "all commands exited 0")).ToArray()));
        Assert.Empty(ledger.Summary.PendingFullGateAttempts);
        Assert.Equal(3, ledger.Snapshot.FixAttempts!.Count);
        var fixture = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        fixture["fixAttempts"]![0]!["lastResult"]!["gate"]!["outcome"] = "failed";
        Save(ledger, fixture);
        Assert.Throws<DecisionLedgerStateException>(() => DecisionLedger.Open(ledger.Path));
    }

    [Fact]
    public void Attempt_binding_retained_results_and_host_annotation_stripping_are_persisted()
    {
        var ledger = Ledger();
        ledger.AddFinding(Finding("fix"), LedgerPhase.CodeReview);
        var result = Passed() with { GateMode = "targeted", PendingFullGateFindingIds = ["F-0001"] };
        ledger.RecordFixAttempt("attempt", ["F-0001"], result, false, "targeted");
        ledger.RecordFixAttempt("attempt", ["F-0001"], null, false, "targeted");
        var saved = ledger.FindFixAttempt("attempt")!;
        Assert.Equal("targeted", saved.GateMode);
        Assert.NotNull(saved.LastResult);
        Assert.Null(saved.LastResult.GateMode);
        Assert.Null(saved.LastResult.PendingFullGateFindingIds);
        var bytes = File.ReadAllBytes(ledger.Path);
        Assert.Throws<DecisionLedgerRequestException>(() => ledger.RecordFixAttempt("attempt", ["F-0001"], null, false));
        Assert.Equal(bytes, File.ReadAllBytes(ledger.Path));
        ledger.RecordFixAttempt("first-interruption", ["F-0001"], null, false);
        Assert.Null(ledger.FindFixAttempt("first-interruption")!.LastResult);
    }

    [Fact]
    public async Task Builder_turn_clears_model_supplied_host_annotations_before_consumers()
    {
        var reported = Passed() with { GateMode = "targeted", PendingFullGateFindingIds = ["F-0001"] };
        await using var session = new RecordingVendorSession(new RoleSpec(VendorRole.Builder, "prompt"),
            new Selection("model", null), null, reported, "token");
        var result = await BuilderTurn.RunAsync(session, _workspace, "fix", CancellationToken.None);
        Assert.Null(result.GateMode);
        Assert.Null(result.PendingFullGateFindingIds);
        Assert.Equal(reported.Summary, result.Summary);
    }

    [Fact]
    public async Task Actual_critic_input_contains_pending_attempts_and_assessments_do_not_verify_them()
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5,
            Approved: true, BaselineHead: "baseline", CodeReviewRoundCap: 3, FullGate: FixGatePolicy.Final));
        run.WritePlan("## Approach\n\n1. **Fix the rule.**\n");
        var ledger = PendingLedger(run);
        var before = File.ReadAllBytes(ledger.Path);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorCritique
        {
            Verdict = "approve", Findings = [], Summary = "assessed",
            UnresolvedAssessments = ledger.Snapshot.Entries.Select(entry =>
                new UnresolvedAssessment(entry.FindingId, false, "no longer observed")).ToArray(),
            Reopenings = []
        });
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        await new CodeReview(vendor, new PromptLibrary(Path.Combine(directory.FullName, "prompts")), new ReviewGit())
            .ReviewAsync(run, new Selection("critic", null), false, CancellationToken.None);
        var prompt = Assert.Single(vendor.Sessions).PromptText;
        Assert.Contains(ledger.RenderProjection(LedgerPhase.CodeReview), prompt);
        Assert.Contains("pending full host verification: targeted attempt z-attempt", prompt);
        Assert.Equal(before, File.ReadAllBytes(ledger.Path));
        Assert.Equal(3, ledger.Summary.PendingFullGateFindingIds.Count);
    }

    private sealed class ReviewGit : IReviewGit
    {
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, IReadOnlyList<string> excludedPaths,
                                                        CancellationToken ct) =>
            Task.FromResult(new ReviewWindow(baselineHead, baselineHead, false, ["tracked.txt"],
                [new ReviewFile("tracked.txt", "--- a/tracked.txt\n+++ b/tracked.txt\n@@ -1 +1 @@\n-old\n+new\n", false)]));
    }

    private DecisionLedger PendingLedger(RunDirectory? run = null)
    {
        var ledger = run is null ? Ledger() : DecisionLedger.Open(run);
        ledger.AddFindings([Finding("one"), Finding("two"), Finding("three"), Finding("not pending")], LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("z-attempt", ["F-0002", "F-0001"], Passed(), true, "targeted");
        ledger.RecordFixAttempt("a-attempt", ["F-0003"], Passed(), true, "targeted");
        ledger.RecordFixAttempt("old-attempt", ["F-0001"], Passed(), true, "targeted");
        var fixture = JsonNode.Parse(AtomicFile.Read(ledger.Path))!;
        fixture["entries"]![0]!["pendingFullGateAttemptId"] = "z-attempt";
        fixture["entries"]![1]!["pendingFullGateAttemptId"] = "z-attempt";
        fixture["entries"]![2]!["pendingFullGateAttemptId"] = "a-attempt";
        Save(ledger, fixture);
        return DecisionLedger.Open(ledger.Path);
    }

    private DecisionLedger Ledger() => DecisionLedger.Open(RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n")));

    private static void Save(DecisionLedger ledger, JsonNode fixture) => AtomicFile.Write(ledger.Path, fixture.ToJsonString());

    private static Finding Finding(string what) => new("major", "test", what);

    private static BuildResult Passed() => new("done", [], new Verification("unavailable", "server gate"), "implemented",
        new GateRun("passed", "Fix gate", "exit 0", 0, "ok", 0.01, null));
}
