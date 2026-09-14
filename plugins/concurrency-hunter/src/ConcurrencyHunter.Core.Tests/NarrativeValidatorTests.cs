using ConcurrencyHunter.Analysis;
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
        var text = ValidGroupText(new string('é', NarrativeValidator.MaximumBytes));

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
            {new string('x', NarrativeValidator.MaximumBytes)}
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

    private static NarrativeScope GroupScope(Finding finding) =>
        NarrativeScope.ForGroup(CreateResult(finding), finding.GroupId, 3);

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
                             .Select(group => new FindingGroup(
                                 group.Key,
                                 $"stable-{group.Key}",
                                 "DCA1001",
                                 "High",
                                 group.First().Resource,
                                 group.Select(finding => finding.FindingId).ToArray()))
                             .ToArray();
        return new AnalysisResult(
            findings.SelectMany(finding => new[] { finding.AccessA.Root, finding.AccessB.Root }).ToArray(),
            findings.SelectMany(finding => new[] { finding.AccessA, finding.AccessB }).ToArray(),
            findings,
            groups);
    }

    private static Finding CreateFinding(string findingId = "F1", string groupId = "G1",
                                         string path = "src/A/Controller.cs", int startLine = 17,
                                         int endLine = 17, string symbol = "Ns.Controller.Post(string)",
                                         string region = "static:Ns.Controller", string field = "_value",
                                         IReadOnlyList<string>? protection = null)
    {
        var resource = new ResourceId("Fixture", region, [field]);
        var root = new ExecutionRoot("root", symbol, $"ControllerBase action {symbol}");
        var source = new SourceSpan(path, startLine, 1, endLine, 20);
        var heldProtection = protection ?? [];
        var accessA = new StaticAccess(
            resource,
            AccessOperation.Write,
            root,
            symbol,
            source,
            heldProtection,
            heldProtection.Select(item => $"Fixture:{item}").ToArray());
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
