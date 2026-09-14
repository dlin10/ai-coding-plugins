using System.Text.Json;
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
            "#### G1 · DCA1001 · _value1 on static:Ns.Controller1 (1 findings)\n" +
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
        Assert.Contains("- Confidence: High (85) · Path feasibility is not analyzed in this version.", markdown);
        Assert.Contains("- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S", markdown);
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

        Assert.Equal("2.0", root.GetProperty("schemaVersion").GetString());
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
