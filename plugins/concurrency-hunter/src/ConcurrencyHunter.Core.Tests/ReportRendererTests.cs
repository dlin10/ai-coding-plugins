using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ReportRendererTests
{
    [Fact]
    public void Report_opens_with_status_target_run_duration_and_version()
    {
        var report = ReportingTestData.CreateReport(ReportingTestData.CreateAnalysis("High"));

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.StartsWith("# Concurrency Hunter report\n", markdown);
        Assert.Contains("| Status | CompleteWithFindings |", markdown);
        Assert.Contains(@"| Target | C:\repo |", markdown);
        Assert.Contains("| Run | run-123 |", markdown);
        Assert.Contains("| Started | 2026-01-02T03:04:05.0000000+00:00 |", markdown);
        Assert.Contains("| Duration | 2.5 s |", markdown);
        Assert.Contains("| Version | concurrency-hunter 0.1.0 |", markdown);
    }

    [Fact]
    public void Accepted_group_narrative_is_inserted_under_its_group_with_headings_nested_below_it()
    {
        const string narrative = "### What can be lost\nA value [E:F1.S].\n```powershell\n# not a heading\n```\n" +
                                 "#### Detail\n### Remediation\n- Fix it.";
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            new Dictionary<string, string> { ["G1"] = narrative });

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.Contains(
            "#### G1 · DCA1001 · _value1 on static:Ns.Controller1 (1 findings, 1 occurrences)\n" +
            "##### What can be lost\nA value [E:F1.S].\n```powershell\n# not a heading\n```\n" +
            "###### Detail\n##### Remediation\n- Fix it.\n\n##### F1",
            markdown);
    }

    [Fact]
    public void Executive_summary_headings_are_nested_below_its_section()
    {
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            acceptedSummary: "Summary [E:F1.R].\n# Remediation\n- Fix it.");

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.Contains("### Executive summary\nSummary [E:F1.R].\n#### Remediation\n- Fix it.\n", markdown);
    }

    [Fact]
    public void Group_without_narrative_carries_the_absence_note()
    {
        var report = ReportingTestData.CreateReport(ReportingTestData.CreateAnalysis("High"));

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.Contains("_No narrative was accepted for this group._", markdown);
    }

    [Fact]
    public void Missing_executive_summary_carries_the_absence_note()
    {
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            acceptedSummary: null);

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.Contains("### Executive summary\n_No executive summary was accepted._", markdown);
    }

    [Fact]
    public void Sections_are_High_Medium_Low_and_empty_ones_say_none()
    {
        var report = ReportingTestData.CreateReport(ReportingTestData.CreateAnalysis("High"));

        var markdown = ReportRenderer.Render(report).ReportMarkdown;
        var high = markdown.IndexOf("### High findings", StringComparison.Ordinal);
        var medium = markdown.IndexOf("### Medium findings", StringComparison.Ordinal);
        var low = markdown.IndexOf("### Low findings", StringComparison.Ordinal);

        Assert.True(high < medium && medium < low);
        Assert.Contains("### Medium findings\n_None._", markdown);
        Assert.Contains("### Low findings\n_None._", markdown);
    }

    [Fact]
    public void Each_finding_shows_accesses_resource_protection_scenario_confidence_and_evidence()
    {
        var report = ReportingTestData.CreateReport(ReportingTestData.CreateAnalysis("High"));

        var markdown = ReportRenderer.Render(report).ReportMarkdown;

        Assert.Contains("- Access A: Ns.Controller1.Post() performs write at src/Controller1.cs:10", markdown);
        Assert.Contains("- Access B: Ns.Controller1.Post() performs read at src/Controller1.cs:20", markdown);
        Assert.Contains("- Resource: Fixture · static:Ns.Controller1 · _value1", markdown);
        Assert.Contains("- Protection: partial", markdown);
        Assert.Contains("- Scenario: A writes `_value1`; B reads `_value1` at the same time", markdown);
        Assert.Contains("- Uncertainty: Path feasibility is not analyzed in this version.\n- Confidence: High (85)\n", markdown);
        Assert.Contains("- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S", markdown);
    }

    /// <summary>What the last phases added reaches the file a model reads: which of a collection's two resources the finding
    /// is about, what the solver said, and what to do about it (ADR 0010, TD-093).</summary>
    [Fact]
    public void Findings_json_carries_the_cell_the_solver_answer_and_the_remediation()
    {
        var analysis = ReportingTestData.CreateAnalysis("High");
        var finding = analysis.Findings[0];
        var resource = finding.Resource with { Selector = ElementSelector.Exact(2), AccessPath = [finding.Resource.AccessPath[0], "[2]"] };
        var report = ReportingTestData.CreateReport(
            FindingTestData.Result([finding with { Resource = resource, PathFeasibility = SolverAnswer.Sat }], analysis.Groups));

        using var json = JsonDocument.Parse(ReportRenderer.Render(report).FindingsJson);
        var rendered = json.RootElement.GetProperty("findings")[0];

        Assert.Equal("[2]", rendered.GetProperty("resource").GetProperty("selector").GetString());
        Assert.Equal("element", rendered.GetProperty("resource").GetProperty("kind").GetString());
        Assert.Equal("sat", rendered.GetProperty("pathFeasibility").GetProperty("result").GetString());
        Assert.Contains("verify manually", rendered.GetProperty("remediation").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_json_parses_with_findings_groups_and_accepted_narrative()
    {
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            new Dictionary<string, string> { ["G1"] = "Accepted group narrative." },
            "Accepted summary.");

        using var json = JsonDocument.Parse(ReportRenderer.Render(report).FindingsJson);
        var root = json.RootElement;
        var finding = Assert.Single(root.GetProperty("findings").EnumerateArray());
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        var narrative = root.GetProperty("narrative").EnumerateArray().ToArray();

        Assert.Equal("2.2", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("F1", finding.GetProperty("findingId").GetString());
        Assert.Equal("deterministic", finding.GetProperty("evidenceMode").GetString());
        Assert.Equal("high", finding.GetProperty("confidence").GetProperty("label").GetString());
        Assert.Equal("managed-heap", finding.GetProperty("resource").GetProperty("domain").GetString());
        Assert.Equal(2, finding.GetProperty("accesses").GetArrayLength());
        Assert.Equal("root-1", finding.GetProperty("accesses")[0].GetProperty("root").GetString());
        Assert.Equal("high", group.GetProperty("confidenceLabel").GetString());
        Assert.Equal("Accepted", group.GetProperty("narrativeStatus").GetString());
        Assert.Equal(["G1", "summary"], narrative.Select(item => item.GetProperty("target").GetString()));
    }

    [Fact]
    public void Run_metadata_json_carries_timings_counts_narratives_and_late_responses()
    {
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            new Dictionary<string, string> { ["G1"] = "Accepted group narrative." });

        using var json = JsonDocument.Parse(ReportRenderer.Render(report).RunMetadataJson);
        var root = json.RootElement;

        Assert.Equal(0.6, root.GetProperty("timings").GetProperty("loadSeconds").GetDouble());
        Assert.Equal(1.2, root.GetProperty("timings").GetProperty("analysisSeconds").GetDouble());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("roots").GetInt32());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("accesses").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("findings").GetInt32());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("narrativesAccepted").GetInt32());
        Assert.Single(root.GetProperty("narratives").EnumerateArray());
        Assert.Single(root.GetProperty("lateResponses").EnumerateArray());
        Assert.Equal("2026-01-02T03:04:08.0000000+00:00",
            root.GetProperty("lateResponses")[0].GetProperty("receivedAt").GetString());
    }

    [Fact]
    public void Rendering_the_same_report_twice_is_identical()
    {
        var report = ReportingTestData.CreateReport(
            ReportingTestData.CreateAnalysis("High"),
            new Dictionary<string, string> { ["G1"] = "Accepted group narrative." });

        var first = ReportRenderer.Render(report);
        var second = ReportRenderer.Render(report);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Report_without_analysis_says_not_analyzed_and_lists_diagnostics()
    {
        var report = ReportingTestData.CreateReport(
            null,
            decision: new StatusDecision(RunStatus.Incomplete, ["AnalysisNotFinished"]));

        var bundle = ReportRenderer.Render(report);

        Assert.Equal(3, Count(bundle.ReportMarkdown, "_Not analyzed._"));
        Assert.Contains("- loader warning", bundle.ReportMarkdown);
        using var findings = JsonDocument.Parse(bundle.FindingsJson);
        Assert.Empty(findings.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Empty(findings.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Empty(findings.RootElement.GetProperty("narrative").EnumerateArray());
    }

    [Fact]
    public void Spawn_evidence_lists_side_a_then_side_b_in_occurrence_and_path_order_once_per_site()
    {
        var analysis = ReportingTestData.SpawnAnalysis();
        var finding = Assert.Single(analysis.Findings);

        var bundle = ReportRenderer.Render(ReportingTestData.CreateReport(analysis));

        // Side A: the spawn site of the first occurrence, then the timer of the second; side B repeats the spawn site twice.
        var spawn = finding.Evidence.Where(item => item.Kind == "spawn").ToArray();
        Assert.Equal(["F1.SP1", "F1.SP2"], spawn.Select(item => item.Id));
        Assert.Equal(["Spawn site: Task.Run in Ns.Worker.Start() at src/Worker.cs:30.",
                      "Spawn site: System.Threading.Timer in Ns.Worker.Arm() at src/Worker.cs:40."], spawn.Select(item => item.Text));
        Assert.Contains("- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S, F1.SP1, F1.SP2\n", bundle.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains($"(Ns.Worker.ExecuteAsync(CancellationToken) → {ReportingTestData.TIMER_SEGMENT} → Ns.Worker.Arm()) with ", bundle.ReportMarkdown,
                        StringComparison.Ordinal);
        using var json = JsonDocument.Parse(bundle.FindingsJson);
        var evidence = json.RootElement.GetProperty("findings")[0].GetProperty("evidence").EnumerateArray()
                           .Where(item => item.GetProperty("kind").GetString() == "spawn")
                           .Select(item => (item.GetProperty("id").GetString(), item.GetProperty("text").GetString()))
                           .ToArray();
        Assert.Equal(spawn.Select(item => ((string?)item.Id, (string?)item.Text)), evidence);
    }

    [Fact]
    public void Findings_json_property_names_are_the_base_ones_with_spawn_and_timer_segments_in_paths()
    {
        // Written down from what 99d49fa renders; phase 3 adds no property at any level.
        var expected = new Dictionary<string, string[]>
        {
            [""] = ["findings", "groups", "narrative", "runId", "schemaVersion"],
            ["findings[]"] = ["accesses", "aiContributions", "aliasEvidence", "analysis", "concurrencyEvidence", "confidence", "evidence", "evidenceMode",
                              "findingId", "fingerprint", "groupFingerprint", "groupId", "occurrenceCount", "occurrences", "pathFeasibility",
                              "protectionAnalysis", "remediation", "resource", "ruleId", "scenario", "severity", "suppression", "title",
                              "uncertainty"],
            ["findings[].confidence"] = ["components", "isProbability", "label", "score"],
            ["findings[].confidence.components"] = ["executionOverlap", "operation", "pathFeasibility", "protection", "resourceIdentity"],
            ["findings[].resource"] = ["accessPath", "assembly", "domain", "kind", "member", "region", "scope", "selector"],
            ["findings[].resource.member"] = ["declaringType", "kind", "name"],
            ["findings[].accesses[]"] = ["codeFlow", "heldProtection", "operation", "readSources", "role", "root", "source"],
            ["findings[].accesses[].source"] = ["path", "span", "symbol"],
            ["findings[].accesses[].readSources[]"] = ["codeFlow", "source", "symbol"],
            ["findings[].accesses[].readSources[].source"] = ["path", "span"],
            ["findings[].occurrences[]"] = ["callPaths", "protection", "roots"],
            ["findings[].protectionAnalysis"] = ["commonProtection", "result"],
            ["findings[].pathFeasibility"] = ["result", "solver"],
            // Phase 5b lists the scope's semantic gaps in place of the coverage state it did not analyze.
            ["findings[].analysis"] = ["ai", "engineVersion", "providers", "semanticGaps"],
            ["findings[].analysis.ai"] = ["acceptedInferenceCount", "rounds", "semanticResolverInvoked", "semanticResolverSkipReason"],
            ["findings[].evidence[]"] = ["id", "kind", "text"],
            ["groups[]"] = ["confidenceLabel", "findingIds", "fingerprint", "groupId", "narrativeStatus", "occurrenceCount", "ownership",
                            "representativeLocations", "resource", "ruleId", "severity"],
            ["groups[].resource"] = ["accessPath", "assembly", "domain", "kind", "member", "region", "scope", "selector"],
            ["groups[].resource.member"] = ["declaringType", "kind", "name"],
            ["groups[].ownership"] = ["evidence", "kind"],
            ["groups[].representativeLocations[]"] = ["line", "path", "symbol"],
            ["narrative[]"] = ["target", "text"]
        };
        var report = ReportingTestData.CreateReport(ReportingTestData.SpawnAnalysis(), new Dictionary<string, string> { ["G1"] = "Narrative [E:F1.S]." });

        using var json = JsonDocument.Parse(ReportRenderer.Render(report).FindingsJson);
        var names = new Dictionary<string, SortedSet<string>>();
        Collect(json.RootElement, "", names);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), names.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, properties) in expected)
            Assert.Equal(properties.Order(StringComparer.Ordinal), names[path]);
        Assert.Contains("spawn:Task.Run@Ns.Worker.Start()", json.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("timer-callback:System.Threading.Timer@Ns.Worker.Arm()", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static void Collect(JsonElement element, string path, Dictionary<string, SortedSet<string>> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!names.TryGetValue(path, out var set))
                    names.Add(path, set = new SortedSet<string>(StringComparer.Ordinal));
                foreach (var property in element.EnumerateObject())
                {
                    set.Add(property.Name);
                    Collect(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, path + "[]", names);
                break;
        }
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }
}
