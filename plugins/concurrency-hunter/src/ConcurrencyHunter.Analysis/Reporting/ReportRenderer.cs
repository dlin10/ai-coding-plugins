using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Reporting;

public static class ReportRenderer
{
    private const int SUMMARY_SECTION_LEVEL = 3;
    private const int GROUP_HEADING_LEVEL = 4;
    private const int REPRESENTATIVE_LOCATIONS = 3;

    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Dictionary<string, string> COUNTER_MEANINGS = new(StringComparer.Ordinal)
    {
        [CoverageCounters.SCC_BUDGET_EXCEEDED] = "recursive cycles whose contexts were merged past the budget, then propagated to a fixpoint",
        [CoverageCounters.OPAQUE_CALL] = "calls without a source body, modelled without effect",
        [CoverageCounters.DELEGATE_TO_OPAQUE] = "delegates handed to such calls, never invoked",
        [CoverageCounters.ELEMENT_OPERATION] = "array element reads and writes, not analyzed",
        [CoverageCounters.UNANALYSED_REGISTRATION] = "reached registrations whose factory or instance is not analyzed",
        [CoverageCounters.NO_RECEIVER_OBJECT] = "virtual, interface or delegate calls with no receiver object, calling nothing",
        [CoverageCounters.STARTUP_CONSTRUCTION_ACCESS] = "accesses of constructions run at startup, not paired",
        [CoverageCounters.MERGED_CONTEXT] = "method contexts merged past the context limit",
        [CoverageCounters.WILDCARD_ACCESS] = "accesses collapsed into a wildcard resource"
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
        Line($"| IR schema | {IrSchema.VERSION} |");
        Line($"| Providers | {ProvidersCell(report.Analysis)} |");
        Line();

        Line("### Executive summary");
        AppendNarrative(markdown, report.AcceptedSummary ?? "_No executive summary was accepted._", SUMMARY_SECTION_LEVEL);
        Line();

        Line("### Coverage");
        Line($"- Projects loaded: {Number(report.ProjectsLoaded)} of {Number(report.ProjectsExpected)}");
        Line($"- Missing projects: {(report.MissingProjects.Count == 0 ? "none" : string.Join(", ", report.MissingProjects))}");
        Line($"- Execution roots: {Number(report.Analysis?.Roots.Count ?? 0)}");
        if (report.Analysis is not null)
            AppendScopes(markdown, report.Analysis);
        Line("- Not analyzed in this version: semantic gaps, path feasibility, spawn sites and ordering, element accesses");
        Line();

        foreach (var label in new[] { "High", "Medium", "Low" })
            AppendFindingSection(markdown, report, label);

        Line("### Suppressed findings");
        Line("Suppressions are not analyzed in this version.");
        Line();

        Line("### Diagnostics");
        Line($"- Load: {Seconds(report.LoadSeconds)} s");
        Line($"- Analysis: {Seconds(report.AnalysisSeconds)} s");
        if (report.Analysis is not null)
        {
            Line($"- Candidate pairs: {Number(report.Analysis.Pairs.Candidates)}");
            Line($"- Suppressed pairs: {Number(report.Analysis.Pairs.Suppressed)}");
            Line($"- Skipped pairs: {Counts(report.Analysis.Pairs.Skips)}");
        }
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

    private static string ProvidersCell(AnalysisResult? analysis)
    {
        if (analysis is null)
            return "not run";
        if (analysis.Providers.Count == 0)
            return "none";
        return string.Join("; ", analysis.Providers.Select(provider =>
            $"{provider.ProviderId} ({(provider.SupportedVersions.Count == 0 ? "no ranges" : string.Join(", ", provider.SupportedVersions))})"));
    }

    // Coverage diagnostics are prefixed with their source: a provider id, "di", "bindings" or "lowering"; lines with
    // no known prefix (scope discovery) are listed as other diagnostics of the scope.
    private static void AppendScopes(StringBuilder markdown, AnalysisResult analysis)
    {
        void Line(string value) => markdown.Append(value).Append('\n');

        foreach (var scope in analysis.Scopes)
        {
            var executable = scope.ExecutableProject is null ? "no executable project" : $"executable {scope.ExecutableProject}";
            Line($"- Process scope {scope.Id}: {executable}; projects {(scope.Projects.Count == 0 ? "none" : string.Join(", ", scope.Projects))}");
            var coverage = analysis.Coverage.FirstOrDefault(item => item.ScopeId == scope.Id);
            if (coverage is null)
                continue;

            var providerIds = coverage.RootsPerProvider.Keys.ToArray();
            Line($"  - Roots per provider: {Counts(coverage.RootsPerProvider)}");
            foreach (var providerId in providerIds)
                Line($"  - Diagnostics from {providerId}: {Items(WithPrefix(coverage.Diagnostics, providerId))}");
            Line($"  - Registrations: {Number(coverage.Registrations)}");
            Line($"  - DI diagnostics: {Items(WithPrefix(coverage.Diagnostics, "di"))}");
            var known = providerIds.Append("di").ToArray();
            var other = coverage.Diagnostics.Where(diagnostic => !known.Any(prefix => diagnostic.StartsWith(prefix + ": ", StringComparison.Ordinal)));
            Line($"  - Other diagnostics: {Items(other)}");
            Line($"  - Reachable bodies: {Number(coverage.Skips.GetValueOrDefault(CoverageCounters.REACHABLE_BODIES))}");
            foreach (var (counter, count) in coverage.Skips.Where(pair => pair.Key != CoverageCounters.REACHABLE_BODIES)
                                                           .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var meaning = COUNTER_MEANINGS.TryGetValue(counter, out var text) ? $": {text}" : "";
                Line($"  - {counter} {Number(count)}{meaning}");
                if (counter == CoverageCounters.OPAQUE_CALL)
                    Line($"    - Top opaque callees: {Items(coverage.TopOpaqueCallees.Select(callee => $"{callee.Callee} {Number(callee.Count)}"))}");
            }

            Line($"  - Lowered but not reached (inventory, not counted against coverage): {Items(coverage.LoweredNotReached)}");
            Line($"  - Source bodies outside the lowered set (inventory, not counted against coverage): {Items(coverage.OutsideLoweredSet)}");
        }
    }

    private static IEnumerable<string> WithPrefix(IEnumerable<string> diagnostics, string prefix) =>
        diagnostics.Where(diagnostic => diagnostic.StartsWith(prefix + ": ", StringComparison.Ordinal))
                   .Select(diagnostic => diagnostic[(prefix.Length + 2)..]);

    private static string Items(IEnumerable<string> items)
    {
        var list = items.Select(item => NormalizeNewlines(item).Replace('\n', ' ')).ToArray();
        return list.Length == 0 ? "none" : string.Join("; ", list);
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {Number(pair.Value)}"));

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
        markdown.Append("- Code path A: ").Append(CodePath(finding.AccessA)).Append('\n');
        markdown.Append("- Code path B: ").Append(CodePath(finding.AccessB)).Append('\n');
        AppendReadSources(markdown, "A", finding.AccessA);
        AppendReadSources(markdown, "B", finding.AccessB);
        markdown.Append("- Resource: ").Append(finding.Resource.Assembly).Append(" · ")
                .Append(finding.Resource.Region).Append(" · ")
                .Append(string.Join(".", finding.Resource.AccessPath)).Append(" · scope ").Append(finding.Resource.Scope)
                .Append(" · ownership ").Append(Ownership(ResourceAccess(finding))).Append('\n');
        markdown.Append("- Binding evidence: ").Append(Items(BindingEvidence(finding))).Append('\n');
        markdown.Append("- Overlap: ").Append(string.Join(" ", finding.ConcurrencyEvidence)).Append(" A: ")
                .Append(Policy(finding.AccessA.Root.Policy)).Append("; B: ").Append(Policy(finding.AccessB.Root.Policy)).Append('\n');
        markdown.Append("- Protection: ").Append(finding.ProtectionResult).Append("; A holds ").Append(Holdings(finding.AccessA))
                .Append("; B holds ").Append(Holdings(finding.AccessB)).Append("; common single-object protection: ")
                .Append(Items(CommonProtection(finding))).Append('\n');
        markdown.Append("- Scenario: ").Append(string.Join("; ", finding.Scenario)).Append('\n');
        markdown.Append("- Uncertainty: ").Append(finding.Uncertainty.Count == 0 ? "none" : string.Join(" ", finding.Uncertainty)).Append('\n');
        markdown.Append("- Confidence: ").Append(finding.Confidence.Label).Append(" (")
                .Append(Number(finding.Confidence.Score)).Append(")\n");
        markdown.Append("- Evidence: ").Append(string.Join(", ", finding.Evidence.Select(item => item.Id)))
                .Append("\n\n");
    }

    private static string CodePath(IReadOnlyList<CodeFlowStep> codeFlow) =>
        codeFlow.Count == 0
            ? "not recorded"
            : string.Join(" → ", codeFlow.Select(step => $"{step.Text} ({step.Source.Path}:{Number(step.Source.StartLine)})"));

    private static string CodePath(Access access) => CodePath(access.CodeFlow);

    /// <summary>Each load a read-modify-write depends on, with its own code path.</summary>
    private static void AppendReadSources(StringBuilder markdown, string role, Access access)
    {
        if (access.Operation != AccessOperation.ReadModifyWrite)
            return;
        foreach (var read in access.ReadSources)
        {
            markdown.Append("- Read source of ").Append(role).Append(": ").Append(read.Symbol).Append(" reads at ").Append(read.Source.Path)
                    .Append(':').Append(Number(read.Source.StartLine)).Append("; code path: ").Append(CodePath(read.CodeFlow)).Append('\n');
        }
    }

    /// <summary>The access whose resource is the finding's, whose region's ownership the finding reports.</summary>
    private static Access ResourceAccess(Finding finding) =>
        finding.AccessA.Resource.Identity == finding.Resource.Identity || finding.AccessB.Resource.Identity != finding.Resource.Identity
            ? finding.AccessA
            : finding.AccessB;

    /// <summary>The ownership kind with the evidence chain that reaches the region, so the report shows it without findings.json.</summary>
    private static string Ownership(Access access) =>
        access.OwnershipEvidence.Count == 0
            ? access.Ownership.ToString()
            : $"{access.Ownership} ({string.Join(" ", access.OwnershipEvidence)})";

    private static IEnumerable<string> OwnershipChain(Access access) =>
        access.OwnershipEvidence.Select(item => $"ownership {access.Ownership}: {item}").DefaultIfEmpty($"ownership {access.Ownership}");

    private static IEnumerable<string> BindingEvidence(Finding finding) =>
        finding.AccessA.BindingEvidence.Concat(finding.AccessB.BindingEvidence)
               .Select(item => $"{item.Text} at {item.Source.Path}:{Number(item.Source.StartLine)}")
               .Distinct(StringComparer.Ordinal);

    private static string Policy(InvocationPolicy policy) => $"{policy.Multiplicity}/{policy.SelfOverlap} in {policy.ScopeBinding}";

    private static string Holdings(Access access) =>
        access.HeldProtection.Count == 0 ? "no protection" : string.Join(", ", access.HeldProtection);

    private static IEnumerable<string> CommonProtection(Finding finding) =>
        finding.AccessA.HeldProtectionIds.Intersect(finding.AccessB.HeldProtectionIds, StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static string Title(Finding finding)
    {
        var member = $"{finding.Resource.Member.DeclaringType}.{finding.Resource.Member.Name}";
        return finding.RuleId == "DCA1002" ? $"Non-atomic update of shared {member}" : $"Unsynchronized access to shared {member}";
    }

    private static void AppendAccess(StringBuilder markdown, string role, Access access)
    {
        markdown.Append("- Access ").Append(role).Append(": ").Append(access.Symbol).Append(" performs ")
                .Append(access.Operation.ToWireName()).Append(" at ").Append(access.Source.Path).Append(':')
                .Append(Number(access.Source.StartLine)).Append(" under root ").Append(access.Root.Display)
                .Append("; holds ").Append(Holdings(access)).Append(".\n");
    }

    private static string RenderFindingsJson(RunReport report)
    {
        var findings = report.Analysis is null
            ? Array.Empty<object>()
            : report.Analysis.Findings.Select(finding => FindingJson(finding, report, report.Analysis)).ToArray();
        var groups = report.Analysis is null
            ? Array.Empty<object>()
            : report.Analysis.Groups.Select(group => GroupJson(group, report.Analysis, report.AcceptedGroupNarratives)).ToArray();
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

    private static object FindingJson(Finding finding, RunReport report, AnalysisResult analysis) => new
    {
        finding.FindingId,
        finding.StableId,
        finding.GroupId,
        finding.RuleId,
        Title = Title(finding),
        Severity = (string?)null,
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
        AliasEvidence = new[]
            {
                $"both accesses reach region {finding.Resource.Region} in scope {finding.Resource.Scope}"
            }
            .Concat(OwnershipChain(ResourceAccess(finding)))
            .Concat(BindingEvidence(finding))
            .ToArray(),
        ProtectionAnalysis = new
        {
            Result = finding.ProtectionResult,
            CommonProtection = CommonProtection(finding).ToArray()
        },
        PathFeasibility = new { Result = "not-analyzed" },
        finding.Scenario,
        finding.Uncertainty,
        AiContributions = Array.Empty<object>(),
        Suppression = (object?)null,
        Analysis = new
        {
            report.EngineVersion,
            Providers = analysis.Providers.Select(provider => provider.ProviderId).ToArray(),
            Ai = new
            {
                SemanticResolverInvoked = false,
                SemanticResolverSkipReason = "NotAvailableInThisVersion",
                Rounds = 0,
                AcceptedInferenceCount = 0
            },
            CoverageState = "not-analyzed"
        },
        finding.Evidence
    };

    private static object AccessJson(string role, Access access) => new
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
        CodeFlow = CodeFlowJson(access.CodeFlow),
        access.HeldProtection,
        ReadSources = access.ReadSources.Select(read => new
        {
            Source = new { read.Source.Path, Span = Span(read.Source) },
            read.Symbol,
            CodeFlow = CodeFlowJson(read.CodeFlow)
        }).ToArray()
    };

    private static string[] CodeFlowJson(IReadOnlyList<CodeFlowStep> codeFlow) =>
        codeFlow.Select(step => $"{step.Text} ({step.Source.Path}:{Number(step.Source.StartLine)})").ToArray();

    private static int[] Span(SourceSpan source) => [source.StartLine, source.StartColumn, source.EndLine, source.EndColumn];

    private static object ResourceJson(AccessResource resource) => new
    {
        Domain = "managed-heap",
        resource.Assembly,
        resource.Region,
        resource.AccessPath,
        resource.Scope,
        Member = new
        {
            resource.Member.DeclaringType,
            resource.Member.Name,
            Kind = resource.Member.Kind.ToString()
        }
    };

    private static object GroupJson(FindingGroup group, AnalysisResult analysis,
                                    IReadOnlyDictionary<string, string> acceptedGroupNarratives) => new
    {
        group.GroupId,
        group.StableId,
        group.RuleId,
        Severity = (string?)null,
        ConfidenceLabel = group.ConfidenceLabel.ToLowerInvariant(),
        Resource = ResourceJson(group.Resource),
        Ownership = new { Kind = group.Ownership.ToString(), Evidence = group.OwnershipEvidence },
        group.FindingIds,
        OccurrenceCount = group.FindingIds.Count,
        RepresentativeLocations = group.FindingIds
                                       .Select(id => analysis.Findings.Single(finding => finding.FindingId == id))
                                       .SelectMany(finding => new[] { finding.AccessA, finding.AccessB })
                                       .Select(access => (access.Source.Path, access.Source.StartLine, access.Symbol))
                                       .Distinct()
                                       .Take(REPRESENTATIVE_LOCATIONS)
                                       .Select(location => new { location.Path, Line = location.StartLine, location.Symbol })
                                       .ToArray(),
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
