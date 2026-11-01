using System.Text.Json.Serialization;
using PlanForge.Advisors;
using PlanForge.Evidence;

namespace PlanForge.Vendors;

/// <summary>Content-free usage for one bounded Evidence map call.</summary>
/// <param name="At">The call's opening in the telemetry timeline's local timestamp format.</param>
/// <param name="Event">The advisor.usage event kind.</param>
/// <param name="Advisor">The service id.</param>
/// <param name="RequestedModel">The requested model.</param>
/// <param name="ServedModel">The first served model, when known.</param>
/// <param name="Requests">AskAsync calls, excluding adapter retries.</param>
/// <param name="FailedRequests">Calls ending in AdvisorException.</param>
/// <param name="JudgmentsAsked">Judgments placed in requests.</param>
/// <param name="InputTokens">The sum of reported input tokens, or null.</param>
/// <param name="OutputTokens">The sum of reported output tokens, or null.</param>
/// <param name="CachedInputTokens">The cached part of the reported input tokens, or null when unknown.</param>
/// <param name="CacheWriteTokens">The cache-written part of the reported input tokens, or null when unknown.</param>
/// <param name="CacheHits">Judgments read from the store at the start.</param>
/// <param name="LatencyP50">Nearest-rank median AskAsync latency in milliseconds.</param>
/// <param name="LatencyP90">Nearest-rank ninetieth percentile AskAsync latency in milliseconds.</param>
/// <param name="Duration">The elapsed call duration.</param>
/// <param name="Complete">Whether every judgment has a persistent outcome.</param>
/// <param name="StoppedBy">Unauthorized, forbidden or budget, when one stopped dispatch.</param>
/// <param name="CostUsd">The reported input tokens at the Advisor's uncached, cache-read and cache-write prices, or null when not reported.</param>
internal sealed record AdvisorUsageRecord(string At, string Event, string Advisor, string RequestedModel,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ServedModel,
                                         int Requests, int FailedRequests, int JudgmentsAsked,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? InputTokens,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? OutputTokens,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? CachedInputTokens,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? CacheWriteTokens,
                                         int CacheHits,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? LatencyP50,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? LatencyP90,
                                         string Duration, bool Complete,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StoppedBy,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? CostUsd)
{
    /// <summary>The event kind every Advisor record carries.</summary>
    internal const string EVENT = "advisor.usage";

    /// <summary>Captures final running progress without retaining item or plan content.</summary>
    /// <param name="progress">Progress available even when the mapper throws.</param>
    /// <param name="advisor">The service selected for the call.</param>
    public static AdvisorUsageRecord From(EvidenceMapProgress progress, IAdvisor advisor) =>
        new(VendorAttempt.FormatLocalTimestamp(progress.At.ToLocalTime()), EVENT, advisor.Id,
            advisor.RequestedModel, progress.Result?.ServedModel, progress.Requests, progress.FailedRequests,
            progress.JudgmentsAsked, progress.InputTokens, progress.OutputTokens, progress.CachedInputTokens,
            progress.CacheWriteTokens, progress.Result?.CacheHits ?? 0,
            progress.LatencyP50, progress.LatencyP90, VendorAttempt.FormatDuration(progress.Duration),
            progress.Result?.Complete ?? false, progress.Result?.StoppedBy, progress.Result?.CostUsd);
}
