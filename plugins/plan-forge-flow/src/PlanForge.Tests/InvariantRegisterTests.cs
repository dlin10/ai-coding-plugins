using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// A finding's invariant: what ingestion keeps of it, the register a closure leaves, and where the
/// next Critic and the Builder are shown it. See docs/adr/0051.
/// </summary>
public sealed class InvariantRegisterTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Ingestion_keeps_the_invariant_fields_and_stores_blank_ones_as_none()
    {
        var ledger = NewRun().ReadDecisionLedger();

        var critique = ledger.IngestCritique(Critique(
            new VendorFinding("major", "Approach: task 2", "misses the ref path", "every reader of the key sees the stored value",
                              ["call kinds", "", "docs"], "callers of Store.Read", true, ""),
            new VendorFinding("minor", "Gates: G1", "count", " ", [], "", false, "")), LedgerPhase.PlanReview);

        var first = ledger.Snapshot.Entries[0].Finding;
        Assert.Equal("every reader of the key sees the stored value", first.Invariant);
        Assert.Equal(["call kinds", "docs"], first.Axes);
        Assert.Equal("callers of Store.Read", first.Searched);
        Assert.True(first.Complete);
        Assert.Null(first.InvariantOf);
        var second = ledger.Snapshot.Entries[1].Finding;
        Assert.Null(second.Invariant);
        Assert.Null(second.Axes);
        Assert.Null(second.Searched);
        Assert.False(second.Complete);
        Assert.Equal("every reader of the key sees the stored value", critique.Findings[0].Invariant);
        Assert.Equal("F-0001", critique.Findings[0].FindingId);
    }

    [Fact]
    public void An_invariantOf_the_run_never_had_is_dropped_with_a_flow_note_and_a_known_one_marks_the_line()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "first", "one rule")), LedgerPhase.PlanReview);

        var critique = ledger.IngestCritique(Critique([Assessment("F-0001")],
            Invariant("major", "second", "one rule", invariantOf: "F-0001"),
            Invariant("major", "third", "another rule", invariantOf: "F-0009")), LedgerPhase.PlanReview, out var notes);
        run.AppendFlowCritique("Plan review", 2, critique, notes);

        Assert.Equal("F-0001", ledger.Snapshot.Entries[1].Finding.InvariantOf);
        Assert.Null(ledger.Snapshot.Entries[2].Finding.InvariantOf);
        Assert.Equal("F-0003: invariantOf F-0009 names no finding of this run; stored as none", Assert.Single(notes));
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("- F-0002 **major** second — what (again: F-0001)", flow, StringComparison.Ordinal);
        Assert.Contains("  invariant: one rule", flow, StringComparison.Ordinal);
        Assert.Contains("- F-0003 **major** third — what\n", flow.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("F-0003: invariantOf F-0009 names no finding of this run", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revision_closure_keeps_the_invariant_with_its_evidence_as_the_claim()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Approach: task 2", "one rule", axes: ["matrix", "gate"]),
                                       Invariant("minor", "Gates: G1", "")), LedgerPhase.PlanReview);

        ledger.Apply(new OrchestratorDecisionBatch("close", [
            new OrchestratorDecision("F-0001", "addressedByRevision", "orchestrator", "revised", "read Ledger.cs:10-40; cell major x none"),
            new OrchestratorDecision("F-0002", "addressedByRevision", "orchestrator", "revised")
        ]), LedgerPhase.PlanReview);

        var record = Assert.Single(ledger.Snapshot.ClosedInvariants!);
        Assert.Equal(new ClosedInvariant("F-0001", "plan_review", "major", "Approach: task 2", "one rule", record.Axes, null,
                                         LedgerClosureKind.Revision, "read Ledger.cs:10-40; cell major x none"), record);
        Assert.Equal(["matrix", "gate"], record.Axes);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public void A_host_closure_keeps_its_evidence_as_the_claim()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule")), LedgerPhase.CodeReview);

        ledger.Apply(new OrchestratorDecisionBatch("host", [
            new OrchestratorDecision("F-0001", "hostVerified", "orchestrator", "verified", "test StoreTests.Ref passes")
        ]), LedgerPhase.CodeReview);

        var record = Assert.Single(ledger.Snapshot.ClosedInvariants!);
        Assert.Equal(LedgerClosureKind.HostVerified, record.ClosedBy);
        Assert.Equal("test StoreTests.Ref passes", record.Claim);
    }

    [Fact]
    public async Task A_fix_attempt_closed_by_its_gate_keeps_the_builders_report_as_the_claim()
    {
        var run = NewRun(plan: "## Gates\n\n1. **G1.** `exit 0` passes.\n");
        var ledger = run.ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule", axes: ["ref path"])), LedgerPhase.CodeReview);
        var vendor = new RecordingVendor("codex");
        var summary = "rule: one rule; Store.Read fixed; ref path fixed; " + new string('x', 700);
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "ran"), summary), "builder-token");

        await new ReviewFix(vendor, new PromptLibrary(RepositoryPrompts())).FixAsync(
            run, new Selection("builder", null), null, "attempt", ["F-0001"], CancellationToken.None);

        var record = Assert.Single(run.ReadDecisionLedger().Snapshot.ClosedInvariants!);
        Assert.Equal(LedgerClosureKind.AutomaticGate, record.ClosedBy);
        Assert.Equal(summary[..600] + "…", record.Claim);
    }

    [Fact]
    public void A_fix_attempt_recorded_as_closed_keeps_its_summary_as_the_claim()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule")), LedgerPhase.CodeReview);

        ledger.RecordFixAttempt("A", ["F-0001"], new BuildResult("done", [], new Verification("unavailable", "server gate"),
            "covered: call kinds", new GateRun("passed", "G1", "exit 0", 0, "ok", 0.01, null)), true);

        var record = Assert.Single(ledger.Snapshot.ClosedInvariants!);
        Assert.Equal(LedgerClosureKind.AutomaticGate, record.ClosedBy);
        Assert.Equal("covered: call kinds", record.Claim);
        Assert.Empty(ledger.Snapshot.Entries);
    }

    [Fact]
    public void The_register_survives_a_reopen_and_later_writes()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule", axes: ["docs"])), LedgerPhase.CodeReview);
        ledger.Apply(new OrchestratorDecisionBatch("host", [
            new OrchestratorDecision("F-0001", "hostVerified", "orchestrator", "verified", "evidence")
        ]), LedgerPhase.CodeReview);
        var saved = Assert.Single(ledger.Snapshot.ClosedInvariants!);

        var reopened = DecisionLedger.Open(run);
        reopened.AddFinding(new Finding("minor", "Other.cs", "later"), LedgerPhase.CodeReview);

        var kept = Assert.Single(DecisionLedger.Open(run).Snapshot.ClosedInvariants!);
        Assert.Equal(saved with { Axes = null }, kept with { Axes = null });
        Assert.Equal(saved.Axes, kept.Axes);
        Assert.Contains("\"closedInvariants\"", File.ReadAllText(run.DecisionLedgerPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_v3_ledger_without_invariants_is_rewritten_by_the_ledger_exactly_as_before()
    {
        const string old =
            """
            {"schemaVersion":3,"nextFindingNumber":3,"entries":[
              {"findingId":"F-0001","origin":"plan_review","activePhase":"plan_review","finding":{"severity":"major","where":"a","what":"a"},"disposition":"deferred","decision":{"decisionBatchId":"b","by":"user","reason":"later"},"reopening":null},
              {"findingId":"F-0002","origin":"code_review","activePhase":"code_review","finding":{"severity":"minor","where":"b","what":"b"},"disposition":"unresolved","decision":null,"reopening":null}],
             "appliedDecisionBatches":[{"decisionBatchId":"b","digest":"00","result":{"decisionBatchId":"b","outcome":"applied","decisionFindingIds":["F-0001"],"reopenedFindingIds":[],"closedFindingIds":[]}}],
             "fixAttempts":[]}
            """;
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var run = NewRun();
        File.WriteAllText(run.DecisionLedgerPath, JsonNode.Parse(old)!.ToJsonString(options));

        var ledger = DecisionLedger.Open(run);
        ledger.Apply(new DecisionBatchRequest("c", [
            new LedgerDispositionDecision("F-0002", LedgerDisposition.Deferred, LedgerDecisionMaker.User, "not now")
        ], [], []));

        var expected = JsonNode.Parse(old)!;
        expected["entries"]![1]!["disposition"] = "deferred";
        expected["entries"]![1]!["decision"] = JsonNode.Parse("""{"decisionBatchId":"c","by":"user","reason":"not now"}""");
        expected["appliedDecisionBatches"]!.AsArray().Add(JsonNode.Parse(
            $$$"""{"decisionBatchId":"c","digest":"{{{ledger.Snapshot.AppliedDecisionBatches[1].Digest}}}","result":{"decisionBatchId":"c","outcome":"applied","decisionFindingIds":["F-0002"],"reopenedFindingIds":[],"closedFindingIds":[]}}"""));
        Assert.Null(ledger.Snapshot.ClosedInvariants);
        Assert.Equal(expected.ToJsonString(options), File.ReadAllText(run.DecisionLedgerPath));
    }

    [Fact]
    public void The_projection_groups_a_chain_under_its_root_and_puts_major_chains_first()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("minor", "Gates: G1", "a minor rule"),
                                       Invariant("major", "Approach: task 2", "the major rule", axes: ["matrix"])),
                              LedgerPhase.PlanReview);
        ledger.Apply(new OrchestratorDecisionBatch("close", [
            new OrchestratorDecision("F-0002", "addressedByRevision", "orchestrator", "revised", "covered the matrix and the gate")
        ]), LedgerPhase.PlanReview);
        ledger.IngestCritique(Critique([Assessment("F-0001")],
            Invariant("major", "Requirements: R3", "the major rule, again", axes: ["docs"], invariantOf: "F-0002")),
            LedgerPhase.PlanReview);

        var projection = ledger.RenderProjection(LedgerPhase.PlanReview).ReplaceLineEndings("\n");

        var section = projection[projection.IndexOf("# Invariants of earlier rounds", StringComparison.Ordinal)..];
        Assert.Contains(
            "## F-0002 | plan_review | major\n"
            + "invariant: the major rule\n"
            + "axes: matrix; docs\n"
            + "- F-0002 (closed: revision) Approach: task 2\n"
            + "- F-0003 (unresolved) Requirements: R3\n"
            + "claim (F-0002): covered the matrix and the gate\n",
            section, StringComparison.Ordinal);
        Assert.True(section.IndexOf("## F-0002", StringComparison.Ordinal) < section.IndexOf("## F-0001", StringComparison.Ordinal));
        Assert.Contains("  invariantOf: F-0002\n", projection, StringComparison.Ordinal);
        Assert.Contains("  invariant: the major rule, again\n  axes: docs\n", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void The_projection_caps_the_section_and_names_the_chains_it_left_out()
    {
        var run = NewRun();
        var register = Enumerable.Range(1, 40).Select(number => new ClosedInvariant(
            $"F-{number:0000}", "code_review", "minor", "Store.cs", $"rule {number} " + new string('x', 1_000), null, null,
            LedgerClosureKind.HostVerified, "checked")).ToArray();
        WriteRegister(run, register);

        var section = Section(DecisionLedger.Open(run).RenderProjection(LedgerPhase.CodeReview));

        Assert.InRange(section.Length, 10_000, 12_000);
        Assert.DoesNotContain(new string('x', 600), section, StringComparison.Ordinal);
        var leftOut = section.Split('\n').Single(line => line.StartsWith("Left out to keep this section within 12,000 characters: ",
                                                                         StringComparison.Ordinal)).TrimEnd();
        var count = int.Parse(leftOut[(leftOut.IndexOf("characters: ", StringComparison.Ordinal) + 12)..].Split(' ')[0]);
        var roots = leftOut[(leftOut.IndexOf("roots ", StringComparison.Ordinal) + 6)..leftOut.IndexOf(" and ", StringComparison.Ordinal)].Split(", ");
        Assert.Equal(10, roots.Length);
        Assert.EndsWith($" and {count - 10} more.", leftOut, StringComparison.Ordinal);
        Assert.All(roots, root => Assert.DoesNotContain($"## {root} |", section, StringComparison.Ordinal));
        Assert.Equal(40 - count, Count(section, "\n## F-"));
        Assert.Contains("## F-0040 |", section, StringComparison.Ordinal);
        Assert.DoesNotContain("## F-0001 |", section, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_chain_is_left_out_and_still_leaves_room_for_the_others()
    {
        var run = NewRun();
        var register = Enumerable.Range(1, 40).Select(number => new ClosedInvariant(
                $"F-{number:0000}", "code_review", "major", $"Place{number}.cs " + new string('w', 400), "the big rule", null,
                number == 1 ? null : "F-0001", LedgerClosureKind.HostVerified, null))
            .Concat(Enumerable.Range(41, 3).Select(number => new ClosedInvariant(
                $"F-{number:0000}", "code_review", "minor", "Small.cs", $"small rule {number}", null, null,
                LedgerClosureKind.HostVerified, null)))
            .ToArray();
        WriteRegister(run, register);

        var section = Section(DecisionLedger.Open(run).RenderProjection(LedgerPhase.CodeReview));

        Assert.True(section.Length <= 12_000);
        Assert.Contains("1 chains, roots F-0001.", section, StringComparison.Ordinal);
        foreach (var root in new[] { "F-0041", "F-0042", "F-0043" })
            Assert.Contains($"## {root} |", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_latest_claim_is_the_one_closed_last_not_the_highest_id()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "First.cs", "one rule")), LedgerPhase.CodeReview);
        ledger.IngestCritique(Critique([Assessment("F-0001")], Invariant("major", "Second.cs", "one rule", invariantOf: "F-0001")),
                              LedgerPhase.CodeReview);
        ledger.Apply(new OrchestratorDecisionBatch("second", [
            new OrchestratorDecision("F-0002", "hostVerified", "orchestrator", "verified", "closed first")
        ]), LedgerPhase.CodeReview);
        ledger.Apply(new OrchestratorDecisionBatch("first", [
            new OrchestratorDecision("F-0001", "hostVerified", "orchestrator", "verified", "closed last")
        ]), LedgerPhase.CodeReview);

        var section = Section(ledger.RenderProjection(LedgerPhase.CodeReview));

        Assert.Contains("claim (F-0001): closed last", section, StringComparison.Ordinal);
        Assert.DoesNotContain("closed first", section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_claim_that_looks_like_a_secret_is_withheld_and_later_critic_input_stays_sendable()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule")), LedgerPhase.CodeReview);

        ledger.RecordFixAttempt("A", ["F-0001"], new BuildResult("done", [], new Verification("unavailable", "server gate"),
            "F-0001 fixed\napi_key: Abcdefghijklmnop1234+", new GateRun("passed", "G1", "exit 0", 0, "ok", 0.01, null)), true);

        var record = Assert.Single(ledger.Snapshot.ClosedInvariants!);
        Assert.DoesNotContain("Abcdefghijklmnop1234", record.Claim, StringComparison.Ordinal);
        Assert.StartsWith("(withheld", record.Claim, StringComparison.Ordinal);
        var projection = ledger.RenderProjection(LedgerPhase.CodeReview);
        Assert.Contains("claim (F-0001): (withheld", projection, StringComparison.Ordinal);
        PlanForge.Review.SensitiveInput.Guard(projection, "the next critic's ledger input");
    }

    [Fact]
    public void A_gate_closure_with_a_blank_builder_report_keeps_the_gate_output()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule")), LedgerPhase.CodeReview);

        ledger.Apply(new DecisionBatchRequest("A:automatic-gate", [], [], [
            new LedgerClosureDecision("F-0001", LedgerClosureKind.AutomaticGate, LedgerDecisionMaker.Orchestrator,
                                      "closed by the fix attempt", "G1 passed: 12 tests")
        ]), "  ");

        Assert.Equal("G1 passed: 12 tests", Assert.Single(ledger.Snapshot.ClosedInvariants!).Claim);
    }

    [Fact]
    public void Each_finding_of_a_fix_attempt_keeps_the_part_of_the_report_that_names_it()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "one rule"), Invariant("major", "Cache.cs", "another rule"),
                                       Invariant("major", "Log.cs", "a third rule")), LedgerPhase.CodeReview);
        var report = "Fixed both.\nF-0001 rule: one rule\n  axes covered: call kinds; left: docs, none exist\n"
                     + "F-0002 rule: another rule\n  axes covered: value channels";

        ledger.Apply(new DecisionBatchRequest("A:automatic-gate", [], [], [
            Gate("F-0001"), Gate("F-0002"), Gate("F-0003")
        ]), report);

        var claims = ledger.Snapshot.ClosedInvariants!.ToDictionary(record => record.FindingId, record => record.Claim);
        Assert.Equal("F-0001 rule: one rule\n  axes covered: call kinds; left: docs, none exist", claims["F-0001"]);
        Assert.Equal("F-0002 rule: another rule\n  axes covered: value channels", claims["F-0002"]);
        Assert.Equal(report, claims["F-0003"]);

        static LedgerClosureDecision Gate(string id) =>
            new(id, LedgerClosureKind.AutomaticGate, LedgerDecisionMaker.Orchestrator, "closed by the fix attempt", "gate output");
    }

    [Fact]
    public void A_multi_line_invariant_is_rendered_on_one_line()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "every reader\n# sees the value",
                                                 axes: ["call\nkinds"])), LedgerPhase.CodeReview);

        var findings = ledger.RenderFixFindings(["F-0001"]).ReplaceLineEndings("\n");
        var projection = ledger.RenderProjection(LedgerPhase.CodeReview).ReplaceLineEndings("\n");

        foreach (var text in new[] { findings, projection })
        {
            Assert.Contains("  invariant: every reader # sees the value\n  axes: call kinds\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n# sees", text, StringComparison.Ordinal);
        }
        Assert.Contains("invariant: every reader # sees the value\naxes: call kinds\n", Section(projection), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fix_prompt_carries_each_findings_invariant_and_axes()
    {
        var ledger = NewRun().ReadDecisionLedger();
        ledger.IngestCritique(Critique(Invariant("major", "Store.cs", "every reader of the key sees the stored value",
                                                 axes: ["call kinds", "tests"])), LedgerPhase.CodeReview);

        var findings = ledger.RenderFixFindings(["F-0001"]).ReplaceLineEndings("\n");

        Assert.Contains("- F-0001 | major | Store.cs — what\n"
                        + "  invariant: every reader of the key sees the stored value\n"
                        + "  axes: call kinds; tests\n", findings, StringComparison.Ordinal);
    }

    private RunDirectory NewRun(string? plan = null)
    {
        var id = Guid.NewGuid().ToString("n");
        var run = RunDirectory.Create(_workspace, id);
        run.WritePlan(plan ?? "# Plan\n\n## Approach\n\n1. Work.\n");
        run.WriteState(new RunState(id, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true, CodeReviewRounds: 1));
        return run;
    }

    private static VendorFinding Invariant(string severity, string where, string invariant,
                                           IReadOnlyList<string>? axes = null, string invariantOf = "") =>
        new(severity, where, "what", invariant, axes ?? [], "a search of the plan", false, invariantOf);

    private static UnresolvedAssessment Assessment(string id) => new(id, true, "still present");

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private static string Section(string projection) =>
        projection[projection.IndexOf("# Invariants of earlier rounds", StringComparison.Ordinal)..];

    private static void WriteRegister(RunDirectory run, IReadOnlyList<ClosedInvariant> register) =>
        File.WriteAllText(run.DecisionLedgerPath, JsonSerializer.Serialize(
            new DecisionLedgerSnapshot(3, register.Count + 1, [], [], [], register), DecisionLedgerJson.Readable.DecisionLedgerSnapshot));

    private static VendorCritique Critique(params VendorFinding[] findings) => Critique([], findings);

    private static VendorCritique Critique(IReadOnlyList<UnresolvedAssessment> assessments, params VendorFinding[] findings) => new()
    {
        Verdict = "revise",
        Findings = findings,
        Summary = "checked",
        UnresolvedAssessments = assessments,
        Reopenings = []
    };

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
