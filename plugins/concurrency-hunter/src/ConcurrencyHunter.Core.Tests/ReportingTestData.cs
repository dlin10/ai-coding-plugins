using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Reporting;

namespace ConcurrencyHunter.Core.Tests;

internal static class ReportingTestData
{
    internal static AnalysisResult CreateAnalysis(params string[] confidenceLabels)
    {
        var findings = new List<Finding>();
        var groups = new List<FindingGroup>();
        var roots = new List<ExecutionRoot>();
        var accesses = new List<StaticAccess>();
        for (var index = 0; index < confidenceLabels.Length; index++)
        {
            var number = index + 1;
            var findingId = $"F{number}";
            var groupId = $"G{number}";
            var confidenceLabel = confidenceLabels[index];
            var score = confidenceLabel == "High" ? 85 : confidenceLabel == "Medium" ? 65 : 40;
            var resource = new ResourceId(
                "Fixture",
                $"static:Ns.Controller{number}",
                [$"_value{number}"]);
            var root = new ExecutionRoot(
                $"root-{number}",
                $"Ns.Controller{number}.Post()",
                $"ControllerBase action Ns.Controller{number}.Post()");
            var accessA = new StaticAccess(
                resource,
                AccessOperation.Write,
                root,
                root.Symbol,
                new SourceSpan($"src/Controller{number}.cs", 10 + index, 5, 10 + index, 12),
                ["static:Ns.Sync.Gate"],
                ["Fixture:static:Ns.Sync.Gate"]);
            var accessB = accessA with
            {
                Operation = AccessOperation.Read,
                Source = new SourceSpan($"src/Controller{number}.cs", 20 + index, 7, 20 + index, 14),
                HeldProtection = [],
                HeldProtectionIds = []
            };
            var evidence = new[] { "A", "B", "R", "O", "P", "S" }
                .Select(suffix => new EvidenceItem($"{findingId}.{suffix}", suffix, $"Evidence {suffix}"))
                .ToArray();
            findings.Add(new Finding(
                findingId,
                $"stable-{findingId}",
                groupId,
                "DCA1001",
                resource,
                accessA,
                accessB,
                "partial",
                new FindingConfidence(
                    confidenceLabel,
                    score,
                    new ConfidenceComponents(25, 20, 20, 20, 0)),
                ["Two ControllerBase actions may run concurrently in one process."],
                [$"A writes `_value{number}`", $"B reads `_value{number}` at the same time",
                    "B observes either the old or the new value"],
                ["Path feasibility is not analyzed in this version."],
                evidence));
            groups.Add(new FindingGroup(
                groupId,
                $"stable-{groupId}",
                "DCA1001",
                confidenceLabel,
                resource,
                [findingId]));
            roots.Add(root);
            accesses.Add(accessA);
            accesses.Add(accessB);
        }

        return new AnalysisResult(roots, accesses, findings, groups);
    }

    internal static RunReport CreateReport(AnalysisResult? analysis,
                                           IReadOnlyDictionary<string, string>? acceptedNarratives = null,
                                           string? acceptedSummary = "Executive summary.",
                                           StatusDecision? decision = null) => new(
        "run-123",
        @"C:\repo",
        "0.1.0",
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 2, 3, 5, 5, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 2, 3, 4, 7, 500, TimeSpan.Zero),
        decision ?? new StatusDecision(
            analysis is not null && analysis.Findings.Count > 0
                ? RunStatus.CompleteWithFindings
                : RunStatus.CompleteClean,
            []),
        2,
        1,
        ["Missing.Project"],
        analysis,
        acceptedNarratives ?? new Dictionary<string, string>(StringComparer.Ordinal),
        acceptedSummary,
        [new NarrativeRecord("G1", 2, "Accepted", ["first rejection"])],
        [new LateResponse("summary", new DateTimeOffset(2026, 1, 2, 3, 4, 8, TimeSpan.Zero))],
        ["loader warning"],
        0.6,
        1.2);
}
