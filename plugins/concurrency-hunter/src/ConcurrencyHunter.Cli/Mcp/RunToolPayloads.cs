using System.Text.Json.Serialization;
namespace ConcurrencyHunter.Mcp;

internal sealed record ErrorPayload([property: JsonPropertyName("error")] string Error,
                                    [property: JsonPropertyName("message")] string? Message = null,
                                    [property: JsonPropertyName("run_id")] string? RunId = null);

internal sealed record StartPayload([property: JsonPropertyName("run_id")] string? RunId,
                                    [property: JsonPropertyName("deadline")] DateTimeOffset? Deadline,
                                    [property: JsonPropertyName("resolvedTarget")] string? ResolvedTarget,
                                    [property: JsonPropertyName("candidates")] IReadOnlyList<string> Candidates,
                                    [property: JsonPropertyName("candidatesTotal")] int CandidatesTotal);

internal sealed record RunCounts(int ProjectsExpected, int ProjectsLoaded, int Roots, int Findings, int Groups,
                                 int NarrativesAccepted);

internal sealed record PollPayload([property: JsonPropertyName("phase")] string Phase,
                                   [property: JsonPropertyName("state")] string State,
                                   [property: JsonPropertyName("deadlineExceeded")] bool DeadlineExceeded,
                                   [property: JsonPropertyName("counts")] RunCounts Counts,
                                   [property: JsonPropertyName("elapsed")] long Elapsed,
                                   [property: JsonPropertyName("remaining")] long Remaining,
                                   [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
                                   [property: JsonPropertyName("warningsTotal")] int WarningsTotal);

internal sealed record SubmissionPayload([property: JsonPropertyName("status")] string Status,
                                         [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
                                         [property: JsonPropertyName("reasonsTotal")] int ReasonsTotal,
                                         [property: JsonPropertyName("attemptsRemaining")] int AttemptsRemaining);

internal sealed record RenderCounts(int Findings, int Groups, int NarrativesAccepted, int LateResponses);

internal sealed record RenderPayload([property: JsonPropertyName("status")] string Status,
                                     [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
                                     [property: JsonPropertyName("reasonsTotal")] int ReasonsTotal,
                                     [property: JsonPropertyName("counts")] RenderCounts Counts,
                                     [property: JsonPropertyName("duration")] double Duration,
                                     [property: JsonPropertyName("bundlePath")] string BundlePath,
                                     [property: JsonPropertyName("reportPath")] string ReportPath);
