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

    /// <summary>Which solver decided a candidate, as the schema names it (ADR 0004).</summary>
    private const string SOLVER = "z3";

    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Dictionary<string, string> COUNTER_MEANINGS = new(StringComparer.Ordinal)
    {
        [CoverageCounters.SCC_BUDGET_EXCEEDED] = "recursive cycles whose contexts were merged past the budget, then propagated to a fixpoint",
        [CoverageCounters.OPAQUE_CALL] = "calls without a source body that the library semantics table does not describe, members of the types a recognizer " +
                                         "models included; each one no recognizer models has an unknown effect on what its arguments reach and, " +
                                         "through a receiver, on the state of the type declaring the member",
        [CoverageCounters.KNOWN_CALL] = "calls without a source body that the library semantics table describes, with the effects it gives them",
        [CoverageCounters.OUT_OF_RANGE_CALL] = "opaque calls of a member the table describes, in an assembly version outside its supported range",
        [CoverageCounters.DELEGATE_TO_OPAQUE] = "delegates handed to such calls other than the recognized spawn and timer APIs and DI factories, minimal API " +
                                                "handlers included; each one a call no recognizer models is handed runs in an unknown execution",
        [CoverageCounters.ELEMENT_OPERATION] = "array element reads and writes, not analyzed",
        [CoverageCounters.UNANALYSED_REGISTRATION] = "unsupported registrations in reached members, binding nothing",
        [CoverageCounters.UNRESOLVED_LOCATOR] = "service locator calls with no constant type, no known scope or no binding, each a place of a semantic gap",
        [CoverageCounters.NO_RECEIVER_OBJECT] = "virtual, interface or delegate calls with no receiver object, calling nothing the analysis has and " +
                                                "modelled with an unknown effect on what their arguments reach",
        [CoverageCounters.MERGED_CONTEXT] = "method contexts merged past the context limit",
        [CoverageCounters.WILDCARD_ACCESS] = "accesses collapsed into a wildcard resource",
        [CoverageCounters.UNPROVEN_REFERENCE] = "reference accesses whose target has no proven source location",
        [CoverageCounters.SEMANTIC_GAP] = "callees the analysis could not reduce that touch a mutable region that is not owned, take a delegate or feed a " +
                                          "shared region, by materiality: roots, regions, call sites"
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
        {
            var pairs = report.Analysis.Pairs;
            Line($"- Pairs: comparisons {Number(pairs.Comparisons)} of a Cartesian bound {Number(pairs.CartesianBound)}; " +
                 $"buckets {Number(pairs.Buckets)}, largest bucket {Number(pairs.LargestBucket)}; " +
                 $"candidates {Number(pairs.Candidates)}, suppressed {Number(pairs.Suppressed)}");
            var ordering = Ordering(report.Analysis);
            Line($"- pairs ordered by happens-before: {Number(ordering.OrderedPairs)}");
            Line($"- spawn sites: {(ordering.SpawnSites.Count == 0 ? "none" : string.Join(", ", ordering.SpawnSites.Select(pair => $"{pair.Key} {Number(pair.Value)}")))}");
            Line($"- timers: disabled {Number(ordering.Timers.Disabled)}, one-shot {Number(ordering.Timers.OneShot)}, periodic {Number(ordering.Timers.Periodic)}");
            Line($"- joins without proven identity: {Number(ordering.UnprovenJoins)}");
            AppendScopes(markdown, report.Analysis);
        }
        Line("- Not analyzed in this version: resolution of semantic gaps by the resolver, path feasibility, element accesses");
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

    private sealed record TimerCounts(int Disabled, int OneShot, int Periodic);

    private sealed record OrderingCounts(int OrderedPairs, IReadOnlyDictionary<string, int> SpawnSites, TimerCounts Timers, int UnprovenJoins);

    /// <summary>The phase-3 ordering counters over the scopes' sites, not their numbers: comparisons happens-before removed, then each
    /// spawn site, timer creation site and join without proven identity counted once by source however many scopes reach it, a timer site
    /// in the widest kind any of its contexts gives it.</summary>
    private static OrderingCounts Ordering(AnalysisResult analysis)
    {
        int Rank(string counter) => OrderingCounters.TIMER_KINDS.TakeWhile(kind => kind != counter).Count();
        var spawnSites = analysis.Coverage.SelectMany(coverage => coverage.SpawnSites)
                                 .Distinct()
                                 .GroupBy(site => site.Api, StringComparer.Ordinal)
                                 .OrderBy(group => group.Key, StringComparer.Ordinal)
                                 .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var timers = analysis.Coverage.SelectMany(coverage => coverage.TimerSites)
                             .GroupBy(site => site.Site)
                             .Select(group => group.Max(site => Rank(site.Counter)))
                             .ToArray();
        int Timers(string counter) => timers.Count(rank => rank == Rank(counter));
        return new OrderingCounts(analysis.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_ORDERED), spawnSites,
                                  new TimerCounts(Timers(OrderingCounters.TIMERS_DISABLED), Timers(OrderingCounters.TIMERS_ONE_SHOT),
                                                  Timers(OrderingCounters.TIMERS_PERIODIC)),
                                  analysis.Coverage.SelectMany(coverage => coverage.UnprovenJoins).Distinct().Count());
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
                if (counter == CoverageCounters.SEMANTIC_GAP)
                {
                    foreach (var gap in coverage.Gaps)
                        Line($"    - {gap.Callee} ({gap.Kind}): roots {Number(gap.Roots)}, regions {Number(gap.Regions)}, call sites {Number(gap.CallSites)}");
                }
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
                    .Append(Number(group.FindingIds.Count)).Append(" findings, ")
                    .Append(Number(group.OccurrenceCount)).Append(" occurrences)\n");
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
        markdown.Append("- Occurrences: ").Append(Number(finding.OccurrenceCount));
        if (finding.OccurrenceCount > finding.Occurrences.Count)
            markdown.Append(" (the first ").Append(Number(finding.Occurrences.Count)).Append(" listed)");
        markdown.Append('\n');
        foreach (var occurrence in finding.Occurrences)
        {
            markdown.Append("  - ").Append(occurrence.RootA.Display).Append(" (").Append(string.Join(" → ", occurrence.CallPathA))
                    .Append(") with ").Append(occurrence.RootB.Display).Append(" (").Append(string.Join(" → ", occurrence.CallPathB))
                    .Append("); protection ").Append(occurrence.Protection).Append('\n');
        }
        AppendReadSources(markdown, "A", finding.AccessA);
        AppendReadSources(markdown, "B", finding.AccessB);
        markdown.Append("- Resource: ").Append(finding.Resource.Assembly).Append(" · ")
                .Append(finding.Resource.Region).Append(" · ")
                .Append(ResourceText(finding.Resource)).Append(" · scope ").Append(finding.Resource.Scope)
                .Append(" · ownership ").Append(Ownership(ResourceAccess(finding))).Append('\n');
        markdown.Append("- Binding evidence: ").Append(Items(BindingEvidence(finding))).Append('\n');
        markdown.Append("- Overlap: ").Append(string.Join(" ", finding.ConcurrencyEvidence)).Append(" A: ")
                .Append(Policy(finding.AccessA.PathRoot.Policy)).Append("; B: ").Append(Policy(finding.AccessB.PathRoot.Policy)).Append('\n');
        markdown.Append("- Protection: ").Append(finding.ProtectionResult).Append("; A holds ").Append(Holdings(finding.AccessA))
                .Append("; B holds ").Append(Holdings(finding.AccessB)).Append("; common single-object protection: ")
                .Append(Items(CommonProtection(finding))).Append('\n');
        markdown.Append("- Path feasibility: ").Append(PathFeasibility(finding)).Append('\n');
        markdown.Append("- Scenario: ").Append(string.Join("; ", finding.Scenario)).Append('\n');
        markdown.Append("- Remediation: ").Append(Remediation(finding)).Append('\n');
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

    /// <summary>What the resource is, in words a reader can act on: a collection has two of them, and which one a finding is
    /// about decides what fixes it (ADR 0010).</summary>
    internal static string ResourceText(AccessResource resource) =>
        resource.Selector is null
            ? string.Join(".", resource.AccessPath)
            : $"{string.Join(".", resource.AccessPath.Take(resource.AccessPath.Count - 1))}, cell {resource.Selector.Text}";

    /// <summary>
    /// What to do about the pair, chosen by what the two sides do and never by the resource alone. A pair of compound
    /// operations is not answered by a thread-safe collection: <c>Count</c> and then <c>Add</c> are two atomic calls with a gap
    /// between them, and only one critical section over the check and the change closes it. A read against a write needs both
    /// sides under one primitive, since a lock around the write alone leaves the read unguarded. An atomic member answers only
    /// the last case: two plain operations on one cell.
    /// </summary>
    internal static string Remediation(Finding finding)
    {
        var operations = new[] { finding.AccessA.Operation, finding.AccessB.Operation };
        if (operations.Any(operation => operation == AccessOperation.CompoundOperation))
        {
            return "put the check and the change of the sequence inside one critical section; a thread-safe collection does " +
                   "not help here, because its members are already atomic one by one and the gap is between them; " +
                   "verify manually.";
        }

        if (operations.Any(operation => operation.IsUnknownEffect()))
        {
            return "hold one synchronization primitive around the call that may read and write this resource and around every other " +
                   "access to it, or stop handing that call shared state; the analysis cannot see what the call does; verify manually.";
        }

        if (operations.Any(operation => operation == AccessOperation.Read))
        {
            return "put both the read and the write under one synchronization primitive; guarding the write alone leaves the " +
                   "read free to observe a half-finished update; verify manually.";
        }

        if (finding.Resource.Selector is not null)
        {
            return "perform the whole update of this cell with one atomic member, so that no execution can act on a value " +
                   "another has already replaced; verify manually.";
        }

        return "make every access to this resource hold one synchronization primitive for the whole of its update; " +
               "verify manually.";
    }

    /// <summary>What the solver said about the two paths meeting (TD-093), and that it was never asked where it was not.</summary>
    private static string PathFeasibility(Finding finding) => finding.PathFeasibility switch
    {
        SolverAnswer.Sat => "satisfiable: the solver found values on which both paths run",
        SolverAnswer.Unsat => "unsatisfiable",
        SolverAnswer.Unknown => "unknown: the solver did not decide it",
        _ => "not analyzed: the candidate carried nothing for the solver to decide"
    };

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
        return finding.RuleId switch
        {
            "DCA1002" => $"Non-atomic update of shared {member}",
            "DCA1003" => $"Inconsistent synchronization of shared {member}",
            _ => $"Unsynchronized access to shared {member}"
        };
    }

    private static void AppendAccess(StringBuilder markdown, string role, Access access)
    {
        markdown.Append("- Access ").Append(role).Append(": ").Append(access.Symbol).Append(" performs ")
                .Append(access.Operation.ToWireName()).Append(" at ").Append(access.Source.Path).Append(':')
                .Append(Number(access.Source.StartLine)).Append(" under root ").Append(access.PathRoot.Display)
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
            // 2.2 lists the semantic gaps of a finding's scope in its analysis, in place of the coverage state it did not analyze.
            SchemaVersion = "2.2",
            report.RunId,
            Findings = findings,
            Groups = groups,
            Narrative = narrative
        });
    }

    private static object FindingJson(Finding finding, RunReport report, AnalysisResult analysis) => new
    {
        finding.FindingId,
        finding.Fingerprint,
        finding.GroupFingerprint,
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
        finding.OccurrenceCount,
        Occurrences = finding.Occurrences.Select(occurrence => new
        {
            Roots = new[] { occurrence.RootA.RootId, occurrence.RootB.RootId },
            CallPaths = new[] { occurrence.CallPathA, occurrence.CallPathB },
            occurrence.Protection
        }).ToArray(),
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
        PathFeasibility = new
        {
            Result = finding.PathFeasibility switch
            {
                SolverAnswer.Sat => "sat",
                SolverAnswer.Unsat => "unsat",
                SolverAnswer.Unknown => "unknown",
                _ => "not-analyzed"
            },
            Solver = finding.PathFeasibility is null ? null : SOLVER
        },
        finding.Scenario,
        Remediation = Remediation(finding),
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
            SemanticGaps = analysis.Coverage.Where(coverage => coverage.ScopeId == finding.Resource.Scope)
                                   .SelectMany(coverage => coverage.Gaps.Select(gap => new
                                   {
                                       Scope = coverage.ScopeId,
                                       gap.Callee,
                                       gap.Kind,
                                       gap.Roots,
                                       gap.Regions,
                                       gap.CallSites
                                   }))
                                   .ToArray()
        },
        finding.Evidence
    };

    private static object AccessJson(string role, Access access) => new
    {
        Role = role,
        Operation = access.Operation.ToWireName(),
        Root = access.PathRoot.RootId,
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
        // Which cell of the collection, beside the path that already ends with it: a reader of the schema should not have to
        // parse a segment to tell the collection's structure from one of its cells (ADR 0010, TD-043).
        Selector = resource.Selector?.Text,
        Kind = resource.Selector is null ? "storage" : "element",
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
        group.Fingerprint,
        group.RuleId,
        Severity = (string?)null,
        ConfidenceLabel = group.ConfidenceLabel.ToLowerInvariant(),
        Resource = ResourceJson(group.Resource),
        Ownership = new { Kind = group.Ownership.ToString(), Evidence = group.OwnershipEvidence },
        group.FindingIds,
        group.OccurrenceCount,
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
        var ordering = analysis is null ? null : Ordering(analysis);
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
            Pairs = analysis is null
                ? null
                : new
                {
                    analysis.Pairs.Comparisons,
                    analysis.Pairs.CartesianBound,
                    analysis.Pairs.Buckets,
                    analysis.Pairs.LargestBucket,
                    analysis.Pairs.Candidates,
                    analysis.Pairs.Suppressed,
                    Skips = analysis.Pairs.Skips.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                },
            OrderedPairs = ordering?.OrderedPairs,
            SpawnSites = ordering?.SpawnSites,
            Timers = ordering?.Timers,
            UnprovenJoins = ordering?.UnprovenJoins,
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
