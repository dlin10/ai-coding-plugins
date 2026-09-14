using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Reporting;

public static class ReportRenderer
{
    private const int SUMMARY_SECTION_LEVEL = 3;
    private const int GROUP_HEADING_LEVEL = 4;

    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Regex HEADING_PATTERN = new(@"^( {0,3})(#{1,6})(?=[ \t]|$)");
    private static readonly Regex FENCE_PATTERN = new("^ {0,3}(`{3,}|~{3,})");

    public static RenderedBundle Render(RunReport report) => new(RenderMarkdown(report), RenderFindingsJson(report), RenderRunMetadataJson(report));

    private static string RenderMarkdown(RunReport report)
    {
        var markdown = new StringBuilder();
        void Line(string value = "") => markdown.Append(value).Append('\n');

        Line("# Concurrency Hunter report");
        Line();
        Line("| Field | Value |");
        Line("| --- | --- |");
        Line($"| Status | {report.Decision.Status} |");
        Line($"| Reasons | {(report.Decision.Reasons.Count == 0 ? "none" : string.Join(", ", report.Decision.Reasons))} |");
        Line($"| Target | {report.Target ?? "unresolved"} |");
        Line($"| Run | {report.RunId} |");
        Line($"| Started | {Timestamp(report.StartedAt)} |");
        Line($"| Duration | {Seconds((report.FinishedAt - report.StartedAt).TotalSeconds)} s |");
        Line($"| Version | concurrency-hunter {report.EngineVersion} |");
        Line();

        Line("### Executive summary");
        AppendNarrative(markdown, report.AcceptedSummary ?? "_No executive summary was accepted._", SUMMARY_SECTION_LEVEL);
        Line();

        Line("### Coverage");
        Line($"- Projects loaded: {Number(report.ProjectsLoaded)} of {Number(report.ProjectsExpected)}");
        Line($"- Missing projects: {(report.MissingProjects.Count == 0 ? "none" : string.Join(", ", report.MissingProjects))}");
        Line($"- Execution roots: {Number(report.Analysis?.Roots.Count ?? 0)} public actions of ControllerBase descendants");
        Line("- Not analyzed in this version: calls made from an action, dependency-injected state, spawned work, locks other than lock on a static field, path feasibility");
        Line();

        foreach (var label in new[] { "High", "Medium", "Low" })
            AppendFindingSection(markdown, report, label);

        Line("### Diagnostics");
        Line($"- Load: {Seconds(report.LoadSeconds)} s");
        Line($"- Analysis: {Seconds(report.AnalysisSeconds)} s");
        Line($"- Late responses: {Number(report.LateResponses.Count)}");
        foreach (var response in report.LateResponses)
            Line($"  - {response.Target} at {Timestamp(response.ReceivedAt)}");

        foreach (var diagnostic in report.Diagnostics)
        {
            foreach (var diagnosticLine in NormalizeNewlines(diagnostic).Split('\n'))
                Line($"- {diagnosticLine}");
        }

        return markdown.ToString();
    }

    private static void AppendFindingSection(StringBuilder markdown, RunReport report, string label)
    {
        markdown.Append("### ").Append(label).Append(" findings\n");
        if (report.Analysis is null)
        {
            markdown.Append("_Not analyzed._\n\n");
            return;
        }

        var groups = report.Analysis.Groups.Where(group => group.ConfidenceLabel == label).ToArray();
        if (groups.Length == 0)
        {
            markdown.Append("_None._\n\n");
            return;
        }

        foreach (var group in groups)
        {
            var field = group.Resource.AccessPath[^1];
            markdown.Append('#', GROUP_HEADING_LEVEL).Append(' ').Append(group.GroupId).Append(" · ")
                    .Append(group.RuleId).Append(" · ")
                    .Append(field).Append(" on ").Append(group.Resource.Region).Append(" (")
                    .Append(Number(group.FindingIds.Count)).Append(" findings)\n");
            var narrative = report.AcceptedGroupNarratives.GetValueOrDefault(group.GroupId, "_No narrative was accepted for this group._");
            AppendNarrative(markdown, narrative, GROUP_HEADING_LEVEL);
            markdown.Append('\n');

            foreach (var findingId in group.FindingIds)
            {
                var finding = report.Analysis.Findings.Single(item => item.FindingId == findingId);
                AppendFinding(markdown, finding);
            }
        }
    }

    private static void AppendFinding(StringBuilder markdown, Finding finding)
    {
        markdown.Append("##### ").Append(finding.FindingId).Append('\n');
        AppendAccess(markdown, "A", finding.AccessA);
        AppendAccess(markdown, "B", finding.AccessB);
        markdown.Append("- Resource: ").Append(finding.Resource.Assembly).Append(" · ")
                .Append(finding.Resource.Region).Append(" · ")
                .Append(string.Join(".", finding.Resource.AccessPath)).Append('\n');
        markdown.Append("- Protection: ").Append(finding.ProtectionResult).Append('\n');
        markdown.Append("- Scenario: ").Append(string.Join("; ", finding.Scenario)).Append('\n');
        markdown.Append("- Confidence: ").Append(finding.Confidence.Label).Append(" (")
                .Append(Number(finding.Confidence.Score)).Append(')');
        if (finding.Uncertainty.Count > 0)
            markdown.Append(" · ").Append(string.Join(" ", finding.Uncertainty));
        markdown.Append('\n');
        markdown.Append("- Evidence: ").Append(string.Join(", ", finding.Evidence.Select(item => item.Id)))
                .Append("\n\n");
    }

    private static void AppendAccess(StringBuilder markdown, string role, StaticAccess access)
    {
        var protection = access.HeldProtection.Count == 0
            ? "no protection"
            : string.Join(", ", access.HeldProtection);
        markdown.Append("- Access ").Append(role).Append(": ").Append(access.Symbol).Append(" performs ")
                .Append(access.Operation.ToWireName()).Append(" at ").Append(access.Source.Path).Append(':')
                .Append(Number(access.Source.StartLine)).Append(" under root ").Append(access.Root.Display)
                .Append("; holds ").Append(protection).Append(".\n");
    }

    private static string RenderFindingsJson(RunReport report)
    {
        var findings = report.Analysis is null
            ? Array.Empty<object>()
            : report.Analysis.Findings.Select(FindingJson).ToArray();
        var groups = report.Analysis is null
            ? Array.Empty<object>()
            : report.Analysis.Groups.Select(group => GroupJson(group, report.AcceptedGroupNarratives)).ToArray();
        var narrative = NarrativeJson(report);
        return Serialize(new
        {
            SchemaVersion = "2.0",
            report.RunId,
            Findings = findings,
            Groups = groups,
            Narrative = narrative
        });
    }

    private static object FindingJson(Finding finding) => new
    {
        finding.FindingId,
        finding.StableId,
        finding.GroupId,
        finding.RuleId,
        EvidenceMode = "deterministic",
        Confidence = new
        {
            Label = finding.Confidence.Label.ToLowerInvariant(),
            finding.Confidence.Score,
            IsProbability = false,
            Components = new
            {
                finding.Confidence.Components.ResourceIdentity,
                finding.Confidence.Components.ExecutionOverlap,
                finding.Confidence.Components.Operation,
                finding.Confidence.Components.Protection,
                finding.Confidence.Components.PathFeasibility
            }
        },
        Resource = ResourceJson(finding.Resource),
        Accesses = new[] { AccessJson("A", finding.AccessA), AccessJson("B", finding.AccessB) },
        finding.ConcurrencyEvidence,
        ProtectionAnalysis = new
        {
            Result = finding.ProtectionResult,
            CommonProtection = Array.Empty<string>()
        },
        finding.Scenario,
        finding.Uncertainty,
        finding.Evidence
    };

    private static object AccessJson(string role, StaticAccess access) => new
    {
        Role = role,
        Operation = access.Operation.ToWireName(),
        Root = access.Root.RootId,
        Source = new
        {
            access.Source.Path,
            Span = new[]
            {
                access.Source.StartLine,
                access.Source.StartColumn,
                access.Source.EndLine,
                access.Source.EndColumn
            },
            access.Symbol
        },
        access.HeldProtection
    };

    private static object ResourceJson(ResourceId resource) => new
    {
        Domain = "managed-heap",
        resource.Assembly,
        resource.Region,
        resource.AccessPath
    };

    private static object GroupJson(FindingGroup group,
                                    IReadOnlyDictionary<string, string> acceptedGroupNarratives) => new
    {
        group.GroupId,
        group.StableId,
        group.RuleId,
        ConfidenceLabel = group.ConfidenceLabel.ToLowerInvariant(),
        Resource = ResourceJson(group.Resource),
        group.FindingIds,
        NarrativeStatus = acceptedGroupNarratives.ContainsKey(group.GroupId)
            ? "Accepted"
            : group.ConfidenceLabel is "High" or "Medium" ? "Required" : "Optional"
    };

    private static object[] NarrativeJson(RunReport report)
    {
        if (report.Analysis is null)
            return [];

        var narrative = new List<object>();
        foreach (var group in report.Analysis.Groups)
        {
            if (report.AcceptedGroupNarratives.TryGetValue(group.GroupId, out var text))
                narrative.Add(new { Target = group.GroupId, Text = text });
        }

        if (report.AcceptedSummary is not null)
            narrative.Add(new { Target = "summary", Text = report.AcceptedSummary });
        return narrative.ToArray();
    }

    private static string RenderRunMetadataJson(RunReport report)
    {
        var analysis = report.Analysis;
        return Serialize(new
        {
            report.RunId,
            report.Target,
            report.EngineVersion,
            StartedAt = Timestamp(report.StartedAt),
            Deadline = Timestamp(report.Deadline),
            FinishedAt = Timestamp(report.FinishedAt),
            DurationSeconds = DecimalSeconds((report.FinishedAt - report.StartedAt).TotalSeconds),
            Status = report.Decision.Status.ToString(),
            report.Decision.Reasons,
            Timings = new
            {
                LoadSeconds = DecimalSeconds(report.LoadSeconds),
                AnalysisSeconds = DecimalSeconds(report.AnalysisSeconds)
            },
            Counts = new
            {
                report.ProjectsExpected,
                report.ProjectsLoaded,
                Roots = analysis?.Roots.Count ?? 0,
                Accesses = analysis?.Accesses.Count ?? 0,
                Findings = analysis?.Findings.Count ?? 0,
                Groups = analysis?.Groups.Count ?? 0,
                NarrativesAccepted = report.AcceptedGroupNarratives.Count +
                                     (report.AcceptedSummary is null ? 0 : 1)
            },
            report.MissingProjects,
            Narratives = report.Narratives.Select(narrative => new
            {
                narrative.Target,
                narrative.Attempts,
                narrative.Status,
                narrative.LastReasons
            }),
            LateResponses = report.LateResponses.Select(response => new
            {
                response.Target,
                ReceivedAt = Timestamp(response.ReceivedAt)
            }),
            report.Diagnostics
        });
    }

    // A narrative is written with its own heading levels, so they are shifted to start one level below the
    // heading it sits under; otherwise its sections outrank that heading and swallow the finding blocks after it.
    private static void AppendNarrative(StringBuilder markdown, string narrative, int parentLevel)
    {
        var lines = NormalizeNewlines(narrative).Split('\n');
        var headings = HeadingLines(lines).ToArray();
        var shift = headings.Length == 0
            ? 0
            : Math.Max(0, parentLevel + 1 - headings.Min(index => HEADING_PATTERN.Match(lines[index]).Groups[2].Length));
        foreach (var index in headings)
        {
            var heading = HEADING_PATTERN.Match(lines[index]);
            var level = Math.Min(6, heading.Groups[2].Length + shift);
            lines[index] = heading.Groups[1].Value + new string('#', level) + lines[index][heading.Length..];
        }

        var shifted = string.Join('\n', lines);
        markdown.Append(shifted);
        if (!shifted.EndsWith('\n'))
            markdown.Append('\n');
    }

    private static IEnumerable<int> HeadingLines(string[] lines)
    {
        string? fence = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var fenceMatch = FENCE_PATTERN.Match(lines[index]);
            if (fence is not null)
            {
                if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fence[0] &&
                    fenceMatch.Groups[1].Length >= fence.Length)
                    fence = null;
            }
            else if (fenceMatch.Success)
                fence = fenceMatch.Groups[1].Value;
            else if (HEADING_PATTERN.IsMatch(lines[index]))
                yield return index;
        }
    }

    private static string Serialize(object value) => NormalizeNewlines(JsonSerializer.Serialize(value, JSON_OPTIONS));

    private static string Timestamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal DecimalSeconds(double value) => decimal.Parse(Seconds(value), CultureInfo.InvariantCulture);

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
