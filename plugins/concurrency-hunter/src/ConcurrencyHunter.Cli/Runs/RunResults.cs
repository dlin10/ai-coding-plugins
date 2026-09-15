using ConcurrencyHunter.Reporting;

namespace ConcurrencyHunter.Runs;

internal sealed record StartResult(string? Error, string? RunId, DateTimeOffset? Deadline, string? ResolvedTarget,
                                   IReadOnlyList<string> Candidates, int CandidatesTotal);

internal sealed record PollResult(string? Error, string? Phase, string? State, bool DeadlineExceeded,
                                  int ProjectsExpected, int ProjectsLoaded, int Roots, int Findings, int Groups,
                                  int NarrativesAccepted, long ElapsedSeconds, long RemainingSeconds,
                                  IReadOnlyList<string> Warnings, int WarningsTotal);

internal sealed record DigestAccess(string Role, string Symbol, string Operation, string Path, int Line, string Root,
                                    IReadOnlyList<string> HeldProtection);

internal sealed record DigestFinding(string FindingId, string ProtectionResult, IReadOnlyList<string> EvidenceIds,
                                     IReadOnlyList<DigestAccess> Accesses, IReadOnlyList<string> BindingEvidence,
                                     IReadOnlyList<string> OverlapEvidence);

internal sealed record GroupDigest(string GroupId, string RuleId, string ConfidenceLabel, string Region,
                                   IReadOnlyList<string> AccessPath, int FindingCount,
                                   IReadOnlyList<DigestFinding> Findings, IReadOnlyList<string> Scenario);

internal sealed record GroupsResult(string? Error, IReadOnlyList<GroupDigest> Groups);

internal sealed record SubmissionResult(string Status, IReadOnlyList<string> Reasons, int ReasonsTotal,
                                        int AttemptsRemaining);

internal sealed record RenderResult(string? Error, string? Message, RunStatus? Status, IReadOnlyList<string> Reasons,
                                    int ReasonsTotal, int Findings, int Groups, int NarrativesAccepted,
                                    int LateResponses, double DurationSeconds, string? BundlePath,
                                    string? ReportPath);
