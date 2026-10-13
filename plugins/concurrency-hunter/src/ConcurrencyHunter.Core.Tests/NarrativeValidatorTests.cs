using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
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
    public void Backticked_C_sharp_keywords_and_literals_are_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("A `catch` or `finally` that sets `false`, `true` or `null`, passes it `ref` or `out`, or locks `this`."),
            GroupScope(CreateFinding()));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Keyword_is_accepted_only_as_the_whole_snippet()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Do not invent `InventedSymbol`, `this.Invented` or `@finally`."),
            GroupScope(CreateFinding()));

        Assert.Equal(["inventedSymbol:InventedSymbol", "inventedSymbol:this.Invented", "inventedSymbol:@finally"], verdict.Reasons);
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

    private const string STORE = "di:Ns.Store@Singleton";
    private const string WORKER_CLOSURE = "closure:Ns.Worker.Run(int)";

    /// <summary>A field of a struct kept in a field is named by the holder's type with the whole path, or by the end of it (R10).</summary>
    [Fact]
    public void Struct_path_holder_type_with_the_whole_path_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `Store._state.Count`, `Ns.Store._state.Count`, `_state.Count` and `Store.Count`."),
            GroupScope(OnPath(STORE, ["_state", "Count"])));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Struct_path_through_a_cell_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `Store._items[0].Count`, `Ns.Store._items[0].Count`, `_items[0].Count` and `_items.[0].Count`."),
            GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    /// <summary>A field the path does not hold is an invention whether the snippet carries the cell or not; dropping the selector
    /// names no path at all.</summary>
    [Fact]
    public void Struct_path_with_an_invented_field_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Not `_items[0].Total`, `Store._items[0].Count.Total`, `Store._items.Total` or `Store._items.Count`."),
            GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[0].Total", "inventedSymbol:Store._items[0].Count.Total", "inventedSymbol:Store._items.Total",
                      "inventedSymbol:Store._items.Count"],
                     verdict.Reasons);
    }

    /// <summary>The selector is checked exactly as the path prints it: another index, an unknown cell or a key is not the proven
    /// cell.</summary>
    [Fact]
    public void Struct_path_with_an_invented_selector_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Not `_items[1].Count`, `_items[?].Count` or `Store._items[\"0\"].Count`."),
            GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[1].Count", "inventedSymbol:_items[?].Count", "inventedSymbol:Store._items[\"0\"].Count"],
                     verdict.Reasons);
    }

    /// <summary>A group's narrative may name only the paths of its own findings; the summary may name any finding's.</summary>
    [Fact]
    public void Struct_path_no_finding_of_the_scope_holds_is_rejected()
    {
        var result = CreateResult(OnPath(STORE, ["_items", "[0]", "Count"]), OnPath(STORE, ["_other", "[0]", "Count"], "F2", "G2"));

        var group = NarrativeValidator.Validate(ValidGroupText("Not `_other[0].Count`."), NarrativeScope.ForGroup(result, "G1", 3));
        var summary = NarrativeValidator.Validate("Both `_items[0].Count` and `_other[0].Count` [E:F1.A] [E:F2.A].", NarrativeScope.ForSummary(result));

        Assert.Equal(["inventedSymbol:_other[0].Count"], group.Reasons);
        Assert.True(summary.Accepted, string.Join(", ", summary.Reasons));
    }

    /// <summary>A selector's brackets match outside string literals: a key holding brackets is one selector, checked like any
    /// other, so the critic's invented key is rejected (R10).</summary>
    [Fact]
    public void Struct_path_with_a_string_key_holding_brackets_is_checked()
    {
        var invented = NarrativeValidator.Validate(ValidGroupText("Not `_items[\"invented[0]\"].Count`."),
                                                   GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));
        var named = NarrativeValidator.Validate(
            ValidGroupText("Both change `_items[\"a[0]\"].Count` and `Store._items[\"a[0]\"].Count`, not `_items[\"a[1]\"].Count`."),
            GroupScope(OnPath(STORE, ["_items", "[\"a[0]\"]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[\"invented[0]\"].Count"], invented.Reasons);
        Assert.Equal(["inventedSymbol:_items[\"a[1]\"].Count"], named.Reasons);
    }

    /// <summary>A key is written as a C# string literal and compared as the key itself: an escaped quote names the quote the key
    /// holds, and a `#` in a key is no numbered variable.</summary>
    [Fact]
    public void Struct_path_with_an_escaped_quote_in_a_string_key_is_compared_as_the_key()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `_items[\"a\\\"b\"].Count` and `_items[\"a#2\"]`, not `_items[\"a\\\"c\"].Count` or `_items[\"a#3\"]` [E:F2.A]."),
            GroupScope(OnPath(STORE, ["_items", "[\"a\"b\"]", "Count"]), OnPath(STORE, ["_items", "[\"a#2\"]"], "F2")));

        Assert.Equal(["inventedSymbol:_items[\"a\\\"c\"].Count", "inventedSymbol:_items[\"a#3\"]"], verdict.Reasons);
    }

    [Fact]
    public void Struct_path_with_a_string_key_a_finding_holds_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `Ns.Store._items[\"k]\"].Count`, `_items[\"k]\"].Count` and `_items.[\"k]\"].Count` [E:F2.A]."),
            GroupScope(OnPath(STORE, ["_items", "[\"k]\"]", "Count"]), OnPath(STORE, ["_items", "[\"@k\"]", "Count"], "F2")));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    [Fact]
    public void Struct_path_with_an_invented_string_key_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Not `_items[\"k\"].Count`, `_items[\"@k]\"].Count` or `Store._items[\"k]\"].Total`."),
            GroupScope(OnPath(STORE, ["_items", "[\"k]\"]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[\"k\"].Count", "inventedSymbol:_items[\"@k]\"].Count", "inventedSymbol:Store._items[\"k]\"].Total"],
                     verdict.Reasons);
    }

    /// <summary>A key written with a <c>\x</c> or <c>\U</c> escape is read as C# reads it and checked as the key it names, and one
    /// with an escape C# does not have is checked by its spelling: neither leaves the path unchecked (R10).</summary>
    [Fact]
    public void Struct_path_with_a_hex_or_long_unicode_escape_in_an_invented_key_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Not `_items[\"\\x31\"].Invented`, `_items[\"\\U00000031\"].Invented` or `_items[\"\\q\"].Count`."),
            GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[\"\\x31\"].Invented", "inventedSymbol:_items[\"\\U00000031\"].Invented",
                      "inventedSymbol:_items[\"\\q\"].Count"],
                     verdict.Reasons);
    }

    [Fact]
    public void Struct_path_with_a_held_key_spelled_with_an_escape_is_accepted()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `_items[\"\\x31\"].Count`, `_items[\"\\x0031\"].Count`, `_items[\"\\u0031\"].Count` and " +
                           "`Store._items[\"\\U00000031\"].Count`, not `_items[\"\\x32\"].Count`."),
            GroupScope(OnPath(STORE, ["_items", "[\"1\"]", "Count"])));

        Assert.Equal(["inventedSymbol:_items[\"\\x32\"].Count"], verdict.Reasons);
    }

    /// <summary>A snippet that is no identifier and no path stays unchecked: an expression, a verdict, a region's text and a
    /// citation.</summary>
    [Fact]
    public void Struct_path_check_leaves_other_snippets_unchecked()
    {
        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Not `value + 1`, `different-identity`, `alloc:Ns.Store.Post()#Ns.Point#2`, `closure:Ns.Worker.Run(int)` or `[E:F1.A]`."),
            GroupScope(OnPath(STORE, ["_items", "[0]", "Count"])));

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    /// <summary>A captured variable is named alone or after its closure's member, numbered as the path numbers it, with the fields of
    /// a struct it holds after it (R4).</summary>
    [Fact]
    public void Capture_cell_captured_variables_of_the_scope_are_accepted()
    {
        var scope = GroupScope(OnPath(WORKER_CLOSURE, ["count#2"]), OnPath(WORKER_CLOSURE, ["note", "Total"], "F2"));

        var verdict = NarrativeValidator.Validate(
            ValidGroupText("Both change `count#2`, `Worker.Run.count#2`, `Ns.Worker.Run.count#2`, `note`, `Run.note`, `note.Total` and " +
                           "`Worker.Run.note.Total` [E:F2.A]."),
            scope);

        Assert.True(verdict.Accepted, string.Join(", ", verdict.Reasons));
    }

    /// <summary>A variable the closure does not hold, or another of the same name, is an invention.</summary>
    [Fact]
    public void Capture_cell_invented_variable_is_rejected()
    {
        var verdict = NarrativeValidator.Validate(ValidGroupText("Not `count#3`, `count`, `total#2` or `Run.count#2.Total`."),
                                                  GroupScope(OnPath(WORKER_CLOSURE, ["count#2"])));

        Assert.Equal(["inventedSymbol:count#3", "inventedSymbol:count", "inventedSymbol:total#2", "inventedSymbol:Run.count#2.Total"],
                     verdict.Reasons);
    }

    /// <summary>The closure of a member whose findings are in another group is outside a group's narrative.</summary>
    [Fact]
    public void Capture_cell_closure_of_a_member_outside_the_scope_is_rejected()
    {
        var result = CreateResult(OnPath(WORKER_CLOSURE, ["count#2"]), OnPath("closure:Ns.Other.Go()", ["count#2"], "F2", "G2"));

        var verdict = NarrativeValidator.Validate(ValidGroupText("Not `Other.Go.count#2` or `Go.count#2`, only `Worker.Run.count#2`."),
                                                  NarrativeScope.ForGroup(result, "G1", 3));

        Assert.Equal(["inventedSymbol:Other.Go.count#2", "inventedSymbol:Go.count#2"], verdict.Reasons);
    }

    /// <summary>The ownership kind the resource text gives may stand in backticks; one no resource of the scope has may not.</summary>
    [Fact]
    public void Capture_cell_ownership_kind_of_the_scope_is_accepted()
    {
        var finding = OnPath(WORKER_CLOSURE, ["count#2"]);
        finding = finding with
        {
            AccessA = finding.AccessA with { Ownership = OwnershipKind.TreeConfined },
            AccessB = finding.AccessB with { Ownership = OwnershipKind.TreeConfined }
        };

        var verdict = NarrativeValidator.Validate(ValidGroupText("The closure is `TreeConfined`, not `Escaped`."), GroupScope(finding));

        Assert.Equal(["inventedSymbol:Escaped"], verdict.Reasons);
    }

    /// <summary>A finding whose resource is on <paramref name="region"/> with the access path <paramref name="path"/>.</summary>
    /// <param name="region">The resource's region display.</param>
    /// <param name="path">The resource's access path, its first segment the holder's own field.</param>
    /// <param name="findingId">The finding's id.</param>
    /// <param name="groupId">The id of the group the finding is in.</param>
    private static Finding OnPath(string region, IReadOnlyList<string> path, string findingId = "F1", string groupId = "G1")
    {
        var finding = CreateFinding(findingId, groupId);
        var resource = new AccessResource("Fixture", FindingTestData.SCOPE, region, path, new MemberKey("Ns.Store", path[0], IrFieldKind.Field), region);
        return finding with
        {
            Resource = resource,
            AccessA = finding.AccessA with { Resource = resource },
            AccessB = finding.AccessB with { Resource = resource }
        };
    }

    private static NarrativeScope GroupScope(params Finding[] findings) =>
        NarrativeScope.ForGroup(CreateResult(findings), findings[0].GroupId, 3);

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
            [$"Contexts of {symbol} were merged; the objects involved may be more than one."],
            evidence);
    }
}
