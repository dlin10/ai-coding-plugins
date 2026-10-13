using ConcurrencyHunter.Reporting;

namespace ConcurrencyHunter.Runs;

internal sealed record StartResult(string? Error, string? RunId, DateTimeOffset? Deadline, string? ResolvedTarget,
                                   IReadOnlyList<string> Candidates, int CandidatesTotal);

internal sealed record PollResult(string? Error, string? Phase, string? State, bool DeadlineExceeded,
                                  int ProjectsExpected, int ProjectsLoaded, int Roots, int Findings, int Groups,
                                  int NarrativesAccepted, long ElapsedSeconds, long RemainingSeconds,
                                  IReadOnlyList<string> Warnings, int WarningsTotal);

internal sealed record DigestAccess(string Role, string Symbol, string Operation, string Path, int Line, string Root,
                                    IReadOnlyList<string> HeldProtection, IReadOnlyList<string> Calls);

internal sealed record DigestFinding(string FindingId, string ProtectionResult, IReadOnlyList<string> EvidenceIds,
                                     IReadOnlyList<DigestAccess> Accesses, IReadOnlyList<string> BindingEvidence,
                                     IReadOnlyList<string> OverlapEvidence, int OccurrenceCount);

/// <summary>One group as <c>get_groups</c> shows it to a composer (SPEC 9.2).</summary>
/// <param name="GroupId">The group's id.</param>
/// <param name="RuleId">The rule of its findings.</param>
/// <param name="ConfidenceLabel">Its confidence label.</param>
/// <param name="Region">The region of its resource.</param>
/// <param name="AccessPath">The whole access path of its resource, the cell's selector right after the collection field.</param>
/// <param name="Field">The field its findings name: the path's last field, or the boxed struct's type for a box's whole storage.</param>
/// <param name="Ownership">The region's ownership with its evidence.</param>
/// <param name="FindingCount">How many findings the group holds.</param>
/// <param name="Findings">The findings listed, as many as the budget allows.</param>
/// <param name="Scenario">The scenario of its first finding.</param>
/// <param name="OccurrenceCount">How many pairs of roots reach its findings.</param>
internal sealed record GroupDigest(string GroupId, string RuleId, string ConfidenceLabel, string Region,
                                   IReadOnlyList<string> AccessPath, string Field, string Ownership, int FindingCount,
                                   IReadOnlyList<DigestFinding> Findings, IReadOnlyList<string> Scenario, int OccurrenceCount);

internal sealed record GroupsResult(string? Error, IReadOnlyList<GroupDigest> Groups);

internal sealed record SubmissionResult(string Status, IReadOnlyList<string> Reasons, int ReasonsTotal,
                                        int AttemptsRemaining);

internal sealed record RenderResult(string? Error, string? Message, RunStatus? Status, IReadOnlyList<string> Reasons,
                                    int ReasonsTotal, int Findings, int Groups, int NarrativesAccepted,
                                    int LateResponses, double DurationSeconds, string? BundlePath,
                                    string? ReportPath);
