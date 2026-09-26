using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Narrative;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class NarrativeValidatorTests
{
    [Fact]
    public void Valid_group_narrative_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText(), GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted);
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void Empty_text_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(" \r\n", GroupScope(CreateFinding()));

        Assert.False(verdict.Accepted);
        Assert.Equal(["empty"], verdict.Reasons);
    }

    [Fact]
    public void Text_over_the_size_limit_is_rejected()
    {
        var text = ValidGroupText(new string('é', NarrativeValidator.MAXIMUM_BYTES));

        var verdict = NarrativeValidator.Validate(text, GroupScope(CreateFinding()));

        Assert.Equal(["sizeExceeded"], verdict.Reasons);
    }

    [Fact]
    public void Group_narrative_without_a_citation_is_rejected()
    {
        var text = """
            Race explanation.
            ## Remediation
            - Protect shared state
              - Check: verify manually under load
            """;

        var verdict = NarrativeValidator.Validate(text, GroupScope(CreateFinding()));

        Assert.Equal(["noCitation", "uncitedFinding:F1"], verdict.Reasons);
    }

    [Fact]
    public void Citation_of_an_unknown_evidence_id_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText(citation: "F999.A"),
            GroupScope(CreateFinding()));

        Assert.Equal(["unknownEvidence:F999.A", "uncitedFinding:F1"], verdict.Reasons);
    }

    [Fact]
    public void Citation_of_evidence_from_another_group_is_rejected()
    {
        var first = CreateFinding();
        var second = CreateFinding("F2", "G2");
        var scope = NarrativeScope.ForGroup(CreateResult(first, second), "G1", 3);

        var verdict = NarrativeValidator.Validate(ValidGroupText(body: "Race [E:F1.A]", citation: "F2.A"), scope);

        Assert.Equal(["unknownEvidence:F2.A"], verdict.Reasons);
    }

    [Fact]
    public void Group_narrative_leaving_a_listed_finding_uncited_is_rejected()
    {
        var result = CreateResult(CreateFinding(), CreateFinding("F2"), CreateFinding("F3"));

        var allListed = NarrativeValidator.Validate(ValidGroupText(), NarrativeScope.ForGroup(result, "G1", 3));
        var firstListed = NarrativeValidator.Validate(ValidGroupText(), NarrativeScope.ForGroup(result, "G1", 1));

        Assert.Equal(["uncitedFinding:F2", "uncitedFinding:F3"], allListed.Reasons);
        Assert.True(firstListed.Accepted);
    }

    [Fact]
    public void Group_narrative_without_a_Remediation_section_is_rejected()
    {
        var verdict = NarrativeValidator.Validate("Race [E:F1.A]", GroupScope(CreateFinding()));

        Assert.Equal(["missingRemediation"], verdict.Reasons);
    }

    [Fact]
    public void Recommendation_without_verify_manually_is_rejected()
    {
        var text = """
            Race [E:F1.A]
            ## Remediation
            - Protect shared state
              - Check: run the stress test
            """;

        var verdict = NarrativeValidator.Validate(text, GroupScope(CreateFinding()));

        Assert.Equal(["recommendationWithoutVerifyManually:1"], verdict.Reasons);
    }

    [Fact]
    public void Recommendation_without_a_Check_item_is_rejected()
    {
        var text = """
            Race [E:F1.A]
            ## Remediation
            - Protect shared state and verify manually under load
            """;

        var verdict = NarrativeValidator.Validate(text, GroupScope(CreateFinding()));

        Assert.Equal(["recommendationWithoutCheck:1"], verdict.Reasons);
    }

    [Fact]
    public void Location_in_a_file_outside_the_evidence_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect `Other.cs:17`."),
            GroupScope(CreateFinding()));

        Assert.Equal(["inventedLocation:Other.cs:17"], verdict.Reasons);
    }

    [Fact]
    public void Location_on_a_line_outside_the_evidence_span_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect `Controller.cs:18`."),
            GroupScope(CreateFinding()));

        Assert.Equal(["inventedLocation:Controller.cs:18"], verdict.Reasons);
    }

    [Fact]
    public void Location_in_a_different_directory_with_the_same_file_name_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect `B/Controller.cs:17`."),
            GroupScope(CreateFinding()));

        Assert.Equal(["inventedLocation:B/Controller.cs:17"], verdict.Reasons);
    }

    [Fact]
    public void Location_matching_an_evidence_access_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect `A/Controller.cs:17`."),
            GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Location_outside_backticks_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect Controller.cs:17"),
            GroupScope(CreateFinding()));

        Assert.Equal(["unbacktickedLocation:Controller.cs:17"], verdict.Reasons);
    }

    [Fact]
    public void Backticked_location_is_matched_whole_including_spaces()
    {
        var scope = GroupScope(CreateFinding(path: "src/My Folder/Controller.cs"));

        var accepted = NarrativeValidator.Validate(
            ValidGroupText("Inspect `My Folder/Controller.cs:17`."),
            scope);
        var rejected = NarrativeValidator.Validate(
            ValidGroupText("Inspect `Wrong Controller.cs:17`."),
            scope);

        Assert.True(accepted.Accepted);
        Assert.Equal(["inventedLocation:Wrong Controller.cs:17"], rejected.Reasons);
    }

    [Fact]
    public void Unicode_backticked_identifiers_are_checked()
    {
        var finding = CreateFinding(
            symbol: "Ns.Контроллер.Post()",
            region: "static:Ns.Контроллер",
            field: "_значение");
        var scope = GroupScope(finding);

        var accepted = NarrativeValidator.Validate(ValidGroupText("Use `_значение`."), scope);
        var rejected = NarrativeValidator.Validate(
            ValidGroupText("Do not invent `выдумка` or `Ns.выдумка`."),
            scope);

        Assert.True(accepted.Accepted);
        Assert.Equal(
            ["inventedSymbol:выдумка", "inventedSymbol:Ns.выдумка"],
            rejected.Reasons);
    }

    [Fact]
    public void Verbatim_identifiers_are_checked_on_every_segment()
    {
        var scope = GroupScope(CreateFinding(
            symbol: "Ns.@class.Post()",
            region: "static:Ns.@class",
            field: "@value"));

        var accepted = NarrativeValidator.Validate(ValidGroupText("Use `Ns.@class.Post()` and `@value`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Do not invent `Ns.@Invented`."), scope);

        Assert.True(accepted.Accepted);
        Assert.Equal(["inventedSymbol:Ns.@Invented"], rejected.Reasons);
    }

    [Fact]
    public void Unbackticked_location_with_an_uppercase_extension_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Inspect Unrelated.CS:999 and Other.Cs"),
            GroupScope(CreateFinding()));

        Assert.Equal(
            ["unbacktickedLocation:Unrelated.CS:999", "unbacktickedLocation:Other.Cs"],
            verdict.Reasons);
    }

    [Fact]
    public void Backticked_symbol_absent_from_the_evidence_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Use `InventedSymbol`."),
            GroupScope(CreateFinding()));

        Assert.Equal(["inventedSymbol:InventedSymbol"], verdict.Reasons);
    }

    [Fact]
    public void Backticked_suffix_of_an_evidence_symbol_and_the_field_name_are_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Review `Controller.Post` and `_value`."),
            GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Backticked_synchronization_vocabulary_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Use `Interlocked` with `Volatile.Read()`."),
            GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Backticked_protection_named_in_the_evidence_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Use `Sync.Gate`."),
            GroupScope(CreateFinding(protection: ["static:Ns.Sync.Gate"])));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Backticked_snippet_that_is_not_an_identifier_is_not_checked()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Use `value + 1`."),
            GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Summary_may_cite_any_group_and_needs_no_Remediation()
    {
        var result = CreateResult(CreateFinding(), CreateFinding("F2", "G2"));
        var scope = NarrativeScope.ForSummary(result);

        var verdict = NarrativeValidator.Validate("Across groups [E:F1.A] and [E:F2.B].", scope);

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Every_reason_is_reported_together()
    {
        var text = $"""
            {new string('x', NarrativeValidator.MAXIMUM_BYTES)}
            Race [E:BAD]
            Inspect Unrelated.CS:999
            ## Remediation
            - Use `InventedThing` at `Wrong.cs:999`
            """;

        var verdict = NarrativeValidator.Validate(text, GroupScope(CreateFinding()));

        Assert.Equal(
            [
                "sizeExceeded",
                "unknownEvidence:BAD",
                "uncitedFinding:F1",
                "recommendationWithoutVerifyManually:1",
                "recommendationWithoutCheck:1",
                "unbacktickedLocation:Unrelated.CS:999",
                "inventedLocation:Wrong.cs:999",
                "inventedSymbol:InventedThing"
            ],
            verdict.Reasons);
    }

    [Fact]
    public void Di_region_type_is_accepted_without_its_prefix_and_lifetime_suffix()
    {
        var scope = GroupScope(CreateFinding(region: "di:Ns.Ledger@Singleton", field: "Entry"));

        var verdict = NarrativeValidator.Validate(ValidGroupText("Share `Ns.Ledger`, `Ledger` and `Ledger.Entry`."), scope);

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Lifetime_suffix_and_region_prefix_on_their_own_are_not_accepted()
    {
        var scope = GroupScope(CreateFinding(region: "di:Ns.Ledger@Singleton", field: "Entry",
                                             protection: ["di:Ns.Ledger@Singleton as Ns.Ledger"]));

        var verdict = NarrativeValidator.Validate(ValidGroupText("Do not name `Singleton`, `di` or `static`."), scope);

        Assert.Equal(["inventedSymbol:Singleton", "inventedSymbol:di"], verdict.Reasons);
    }

    [Fact]
    public void Held_protection_names_are_accepted_without_region_prefix_lifetime_suffix_or_note()
    {
        var scope = GroupScope(CreateFinding(protection:
        [
            "di:Ns.Gate@Singleton as Ns.IGate (not one object per process)",
            "this Ns.Worker (not one object per process)",
            "static:Ns.Sync.Mutable (not one object per process)"
        ]));

        var accepted = NarrativeValidator.Validate(
            ValidGroupText("Lock `Ns.Gate`, `IGate`, `Worker` and `Sync.Mutable`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `process` or `Singleton`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedSymbol:process", "inventedSymbol:Singleton"], rejected.Reasons);
    }

    [Fact]
    public void Root_entry_symbol_is_accepted_like_an_access_symbol()
    {
        var scope = GroupScope(CreateFinding(symbol: "Ns.Ledger.Reserve(int)", rootSymbol: "Ns.LedgerWorker.ExecuteAsync(CancellationToken)"));

        var accepted = NarrativeValidator.Validate(
            ValidGroupText("From `Ns.LedgerWorker.ExecuteAsync(CancellationToken)` and `LedgerWorker.ExecuteAsync` to `Ledger.Reserve`."),
            scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `LedgerWorker.StopAsync`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedSymbol:LedgerWorker.StopAsync"], rejected.Reasons);
    }

    [Fact]
    public void Static_region_type_is_still_accepted_without_its_prefix()
    {
        var scope = GroupScope(CreateFinding(region: "static:Ns.Heartbeat", field: "LastBeat"));

        var accepted = NarrativeValidator.Validate(ValidGroupText("Guard `Heartbeat.LastBeat`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `static:Ns.Heartbeat` as `Ns.Heartbeats`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedSymbol:Ns.Heartbeats"], rejected.Reasons);
    }

    [Fact]
    public void Alloc_region_created_type_is_accepted_without_site_and_ordinal()
    {
        var scope = GroupScope(CreateFinding(region: "alloc:Ns.Editor..ctor()#Ns.Memo#2", field: "Title"));

        var accepted = NarrativeValidator.Validate(ValidGroupText("Share `Ns.Memo`, `Memo` and `Memo.Title`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `Memo.Owner`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedSymbol:Memo.Owner"], rejected.Reasons);
    }

    [Fact]
    public void Alloc_region_owner_method_alone_is_not_accepted()
    {
        var scope = GroupScope(CreateFinding(region: "alloc:Ns.Factory.Make()#Ns.Memo", field: "Title"));

        var verdict = NarrativeValidator.Validate(ValidGroupText("Not `Ns.Factory.Make` or `Factory`."), scope);

        Assert.Equal(["inventedSymbol:Ns.Factory.Make", "inventedSymbol:Factory"], verdict.Reasons);
    }

    [Fact]
    public void Call_step_symbol_is_accepted_like_an_access_symbol()
    {
        var finding = CreateFinding(symbol: "Ns.Inventory.Reserve(int)");
        var step = new CodeFlowStep("call", "calls Ns.OrderService.Place() on di:Ns.OrderService@Singleton", finding.AccessA.Source);
        finding = finding with { AccessA = finding.AccessA with { CodeFlow = [step] } };
        var scope = GroupScope(finding);

        var accepted = NarrativeValidator.Validate(
            ValidGroupText("Through `Ns.OrderService.Place()` and `OrderService.Place` to `Inventory.Reserve`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `OrderService.Cancel`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedSymbol:OrderService.Cancel"], rejected.Reasons);
    }

    [Fact]
    public void Read_source_location_of_a_read_modify_write_is_accepted()
    {
        var finding = CreateFinding(symbol: "Ns.Tallies.SetLevel(int)");
        var read = new ReadSource("Ns.Tallies.GetLevel()", new SourceSpan("src/A/Tallies.cs", 9, 1, 9, 20), []);
        finding = finding with { AccessA = finding.AccessA with { Operation = AccessOperation.ReadModifyWrite, ReadSources = [read] } };
        var scope = GroupScope(finding);

        var accepted = NarrativeValidator.Validate(ValidGroupText("Read at `Tallies.cs:9` in `Tallies.GetLevel`."), scope);
        var rejected = NarrativeValidator.Validate(ValidGroupText("Not `Tallies.cs:10`."), scope);

        Assert.True(accepted.Accepted, string.Join(", ", accepted.Reasons));
        Assert.Equal(["inventedLocation:Tallies.cs:10"], rejected.Reasons);
    }

    [Fact]
    public void Citation_of_spawn_evidence_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText(body: "Race [E:F1.A]", citation: "F1.SP1"), GroupScope(SpawnFinding()));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Spawn_site_location_named_by_spawn_evidence_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText("The work starts at `Worker.cs:30`."), GroupScope(SpawnFinding()));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Citation_of_a_spawn_evidence_id_the_finding_does_not_have_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText(body: "Race [E:F1.A]", citation: "F1.SP2"), GroupScope(SpawnFinding()));

        Assert.Equal(["unknownEvidence:F1.SP2"], verdict.Reasons);
    }

    [Fact]
    public void Spawn_site_location_missing_from_the_evidence_is_rejected()
    {
        var withoutSpawn = GroupScope(CreateFinding());
        var otherLine = GroupScope(SpawnFinding());

        Assert.Equal(["inventedLocation:Worker.cs:30"], NarrativeValidator.Validate(ValidGroupText("It starts at `Worker.cs:30`."), withoutSpawn).Reasons);
        Assert.Equal(["inventedLocation:Worker.cs:31"], NarrativeValidator.Validate(ValidGroupText("It starts at `Worker.cs:31`."), otherLine).Reasons);
    }

    /// <summary>A finding whose side A runs in work <c>Task.Run</c> starts at <c>src/A/Worker.cs:30</c>.</summary>
    private static Finding SpawnFinding()
    {
        var finding = CreateFinding();
        var occurrence = new FindingOccurrence(finding.AccessA.Root, finding.AccessB.Root, ["Ns.Worker.Run()", "spawn:Task.Run@Ns.Worker.Run()"], [],
                                               "unprotected")
        {
            SpawnSitesA = [new SpawnSiteLocation("spawn:Task.Run@Ns.Worker.Run()", new SourceSpan("src/A/Worker.cs", 30, 9, 30, 40))]
        };
        return finding with
        {
            Occurrences = [occurrence],
            Evidence = [.. finding.Evidence, .. ConflictFindings.SpawnEvidence(finding.FindingId, [occurrence])]
        };
    }

    private static NarrativeScope GroupScope(Finding finding) =>
        NarrativeScope.ForGroup(CreateResult(finding), finding.GroupId, 3);

    /// <summary>A cell's access path ends with the selector, which names no member; the field before it is what a narrative
    /// about that cell writes, and it has to pass (ADR 0010).</summary>
    [Fact]
    public void The_collection_field_of_a_finding_on_one_cell_is_not_an_invented_symbol()
    {
        var finding = CreateFinding(region: "di:Ns.Ledger@Singleton", field: "Entries");
        var cell = finding.Resource with { Selector = ElementSelector.Key("a"), AccessPath = ["Entries", "[\"a\"]"] };
        var scope = GroupScope(finding with { Resource = cell });

        var verdict = NarrativeValidator.Validate(ValidGroupText("The cell of `Ledger.Entries` is written by both."), scope);

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void The_protection_verdicts_and_the_plain_collections_pass_the_vocabulary()
    {
        var body = "The pair is `unprotected`; another is `partial` and a third `sufficient`, and `different-identity` is " +
                   "the fourth. Neither `Dictionary` nor `List` is safe here.";

        var verdict = NarrativeValidator.Validate(ValidGroupText(body), GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void The_four_collections_of_phase_5b_and_the_node_pass_the_vocabulary()
    {
        var body = "A `HashSet`, a `Queue`, a `Stack` and a `LinkedList` are none of them thread-safe; `LinkedListNode.Value` is a cell.";

        var verdict = NarrativeValidator.Validate(ValidGroupText(body), GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    /// <summary>A narrative about a finding a semantic gap decides may name the unresolved call, as its uncertainty does, by its
    /// member without type arguments or parameters (TD-039).</summary>
    [Fact]
    public void The_callee_of_a_gap_that_decides_a_check_is_accepted_without_type_arguments_or_parameters()
    {
        var finding = CreateFinding() with { GapCallees = ["System.Runtime.InteropServices.CollectionsMarshal.SetCount<T>(List<string>, int)"] };

        var verdict = NarrativeValidator.Validate(ValidGroupText("The call `CollectionsMarshal.SetCount` may change the list; `SetCount` too."),
                                                  GroupScope(finding));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void A_callee_no_gap_of_the_finding_names_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText("The call `CollectionsMarshal.SetCount` may change the list."),
                                                  GroupScope(CreateFinding()));

        Assert.Equal(["inventedSymbol:CollectionsMarshal.SetCount"], verdict.Reasons);
    }

    /// <summary>The vocabulary is a list, not a licence: a type nobody named is still an invention.</summary>
    [Fact]
    public void A_type_outside_the_vocabulary_and_the_evidence_is_still_rejected()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText("Use a `PersistentDictionary` instead."), GroupScope(CreateFinding()));

        Assert.Equal(["inventedSymbol:PersistentDictionary"], verdict.Reasons);
    }

    private static string ValidGroupText(string body = "Race explanation.", string citation = "F1.A") => $"""
        {body}
        [E:{citation}]
        ## Remediation
        - Protect shared state
          - Check: verify manually under load
        """;

    private static AnalysisResult CreateResult(params Finding[] findings)
    {
        var groups = findings.GroupBy(finding => finding.GroupId)
                             .Select(group => FindingTestData.Group(group.Key, "High", group.First().Resource,
                                                                    group.Select(finding => finding.FindingId).ToArray()))
                             .ToArray();
        return FindingTestData.Result(findings, groups);
    }

    private static Finding CreateFinding(string findingId = "F1", string groupId = "G1",
                                         string path = "src/A/Controller.cs", int startLine = 17,
                                         int endLine = 17, string symbol = "Ns.Controller.Post(string)",
                                         string region = "static:Ns.Controller", string field = "_value",
                                         IReadOnlyList<string>? protection = null, string? rootSymbol = null)
    {
        var resource = FindingTestData.Resource(region, field);
        var source = new SourceSpan(path, startLine, 1, endLine, 20);
        var heldProtection = protection ?? [];
        var accessA = FindingTestData.Access(
            resource,
            AccessOperation.Write,
            "root",
            symbol,
            source,
            heldProtection,
            heldProtection.Select(item => $"Fixture:{item}").ToArray());
        accessA = accessA with { Root = accessA.Root with { Symbol = rootSymbol ?? accessA.Root.Symbol } };
        var accessB = accessA with { Operation = AccessOperation.Read };
        var evidence = new[] { "A", "B", "R", "O", "P", "S" }
            .Select(suffix => new EvidenceItem($"{findingId}.{suffix}", suffix, suffix))
            .ToArray();
        return new Finding(
            findingId,
            $"stable-{findingId}",
            groupId,
            "DCA1001",
            resource,
            accessA,
            accessB,
            "unprotected",
            new FindingConfidence("High", 85, new ConfidenceComponents(25, 20, 20, 20, 0)),
            ["The same ControllerBase action may run concurrently with itself."],
            ["A writes", "B reads", "B observes"],
            ["Path feasibility is not analyzed in this version."],
            evidence);
    }
}
