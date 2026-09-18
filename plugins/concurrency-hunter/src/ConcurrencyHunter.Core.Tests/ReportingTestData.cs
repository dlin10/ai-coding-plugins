using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;

namespace ConcurrencyHunter.Core.Tests;

internal static class ReportingTestData
{
    internal static AnalysisResult CreateAnalysis(params string[] confidenceLabels)
    {
        var findings = new List<Finding>();
        var groups = new List<FindingGroup>();

        for (var index = 0; index < confidenceLabels.Length; index++)
        {
            var number = index + 1;
            var findingId = $"F{number}";
            var groupId = $"G{number}";
            var confidenceLabel = confidenceLabels[index];
            var score = confidenceLabel == "High" ? 85 : confidenceLabel == "Medium" ? 65 : 40;
            var resource = FindingTestData.Resource($"static:Ns.Controller{number}", $"_value{number}");
            var accessA = FindingTestData.Access(
                resource,
                AccessOperation.Write,
                $"root-{number}",
                $"Ns.Controller{number}.Post()",
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
                $"fingerprint-{findingId}",
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
            groups.Add(FindingTestData.Group(groupId, confidenceLabel, resource, [findingId]));
        }

        return FindingTestData.Result(findings, groups);
    }

    internal const string SPAWN_SEGMENT = "spawn:Task.Run@Ns.Worker.Start()";
    internal const string TIMER_SEGMENT = "timer-callback:System.Threading.Timer@Ns.Worker.Arm()";

    /// <summary>One High finding whose side A is a read-modify-write with a read source, listed with two occurrences whose paths pass a
    /// <c>spawn:</c> and a <c>timer-callback:</c> segment, the spawn segment on both sides.</summary>
    internal static AnalysisResult SpawnAnalysis()
    {
        var analysis = CreateAnalysis("High");
        var finding = analysis.Findings[0];
        var readSpan = new SourceSpan("src/Controller1.cs", 9, 5, 9, 12);
        var accessA = finding.AccessA with
        {
            Operation = AccessOperation.ReadModifyWrite,
            ReadSources = [new ReadSource("Ns.Controller1.Post()", readSpan, [new CodeFlowStep("access", "read Ns.Controller1._value1", readSpan)])]
        };
        var spawn = new SpawnSiteLocation(SPAWN_SEGMENT, new SourceSpan("src/Worker.cs", 30, 9, 30, 40));
        var timer = new SpawnSiteLocation(TIMER_SEGMENT, new SourceSpan("src/Worker.cs", 40, 9, 40, 60));
        var occurrences = new[]
        {
            new FindingOccurrence(accessA.Root, finding.AccessB.Root, ["Ns.Worker.ExecuteAsync(CancellationToken)", SPAWN_SEGMENT, "Ns.Worker.Start()"],
                                  ["Ns.Worker.ExecuteAsync(CancellationToken)", SPAWN_SEGMENT, "Ns.Worker.Start()"], "partial")
            {
                SpawnSitesA = [spawn],
                SpawnSitesB = [spawn]
            },
            new FindingOccurrence(accessA.Root, finding.AccessB.Root, ["Ns.Worker.ExecuteAsync(CancellationToken)", TIMER_SEGMENT, "Ns.Worker.Arm()"],
                                  ["Ns.Worker.ExecuteAsync(CancellationToken)", SPAWN_SEGMENT, "Ns.Worker.Start()"], "partial")
            {
                SpawnSitesA = [timer],
                SpawnSitesB = [spawn]
            }
        };
        var spawned = finding with
        {
            AccessA = accessA,
            OccurrenceCount = 2,
            Occurrences = occurrences,
            Evidence = [.. finding.Evidence, .. ConflictFindings.SpawnEvidence(finding.FindingId, occurrences)]
        };
        return analysis with { Findings = [spawned], Accesses = [accessA, finding.AccessB] };
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
