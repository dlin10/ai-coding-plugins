using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Reporting;

public enum RunStatus
{
    CompleteWithFindings,
    CompleteClean,
    Incomplete,
    Failed
}

public sealed record StatusInputs(bool LoadFailed, bool AnalysisFailed, bool LoadComplete, bool DeadlineExceeded,
                                  AnalysisResult? Analysis, IReadOnlySet<string> NarratedGroupIds);

public sealed record StatusDecision(RunStatus Status, IReadOnlyList<string> Reasons);

public sealed record NarrativeRecord(string Target, int Attempts, string Status, IReadOnlyList<string> LastReasons);

public sealed record LateResponse(string Target, DateTimeOffset ReceivedAt);

public sealed record RunReport(string RunId, string? Target, string EngineVersion, DateTimeOffset StartedAt,
                               DateTimeOffset Deadline, DateTimeOffset FinishedAt, StatusDecision Decision,
                               int ProjectsExpected, int ProjectsLoaded, IReadOnlyList<string> MissingProjects,
                               AnalysisResult? Analysis,
                               IReadOnlyDictionary<string, string> AcceptedGroupNarratives,
                               string? AcceptedSummary, IReadOnlyList<NarrativeRecord> Narratives,
                               IReadOnlyList<LateResponse> LateResponses, IReadOnlyList<string> Diagnostics,
                               double LoadSeconds, double AnalysisSeconds);

public sealed record RenderedBundle(string ReportMarkdown, string FindingsJson, string RunMetadataJson);
