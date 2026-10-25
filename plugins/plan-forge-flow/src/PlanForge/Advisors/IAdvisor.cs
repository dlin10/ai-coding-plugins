using System.Text.Json.Nodes;

namespace PlanForge.Advisors;

/// <summary>An external service answering bounded Choice questions over supplied state.</summary>
internal interface IAdvisor
{
    /// <summary>The stable service identifier.</summary>
    string Id { get; }

    /// <summary>The model requested from this service.</summary>
    string RequestedModel { get; }

    /// <summary>The service's sizing, concurrency and timeout limits.</summary>
    AdvisorLimits Limits { get; }

    /// <summary>The published price in USD per million input tokens; output is not priced.</summary>
    decimal InputUsdPerMillion { get; }

    /// <summary>Measures the exact body that would be sent, in the service's limit unit.</summary>
    /// <param name="request">The state and ordered questions to measure.</param>
    AdviceSize Measure(AdviceRequest request);

    /// <summary>Asks the questions, preserving caller cancellation and sanitizing service failures.</summary>
    /// <param name="request">The state and ordered questions to send.</param>
    /// <param name="ct">Cancellation requested by the caller.</param>
    Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct);
}

/// <summary>Object state shared by an ordered set of independent Choice questions.</summary>
/// <param name="State">The JSON-object state supplied to every question.</param>
/// <param name="Questions">Questions in the order they must be sent.</param>
internal sealed record AdviceRequest(JsonObject State, IReadOnlyList<ChoiceQuestion> Questions);

/// <summary>One bounded question with ordered alternatives.</summary>
/// <param name="Id">The identifier used to match its answer.</param>
/// <param name="Instructions">The decision the service should make.</param>
/// <param name="Options">The allowed choices, in wire order.</param>
internal sealed record ChoiceQuestion(string Id, string Instructions, IReadOnlyList<ChoiceOption> Options);

/// <summary>One allowed choice and the circumstances in which it applies.</summary>
/// <param name="Value">The value the service may choose.</param>
/// <param name="Description">The meaning of this choice.</param>
internal sealed record ChoiceOption(string Value, string Description);

/// <summary>The service's outcome for a single question.</summary>
internal abstract record AdviceOutcome;

/// <summary>A choice and the service's distribution, to be checked against the question by the core.</summary>
/// <param name="Choice">The selected option value.</param>
/// <param name="Probabilities">The reported probability for each option.</param>
/// <param name="Confidence">The reported confidence, when available.</param>
internal sealed record Answered(string Choice, IReadOnlyDictionary<string, double> Probabilities,
                                double? Confidence = null) : AdviceOutcome;

/// <summary>The service declined to answer a question.</summary>
internal sealed record Refused : AdviceOutcome;

/// <summary>Answers and metadata returned by an Advisor.</summary>
/// <param name="Answers">Outcomes keyed by question identifier.</param>
/// <param name="ServedModel">The model the service reports actually answered.</param>
/// <param name="Usage">Token usage, when the service reports it.</param>
internal sealed record AdviceResult(IReadOnlyDictionary<string, AdviceOutcome> Answers,
                                    string ServedModel,
                                    AdviceUsage? Usage = null);

/// <summary>Reported token usage, preserving unknown counts as null.</summary>
/// <param name="InputTokens">Reported input tokens, or null when unknown.</param>
/// <param name="OutputTokens">Reported output tokens, or null when unknown.</param>
internal sealed record AdviceUsage(long? InputTokens, long? OutputTokens);

/// <summary>Request sizes expressed in the unit named by the Advisor's limits.</summary>
/// <param name="Total">The size of the whole serialized body.</param>
/// <param name="StatePlusLargestQuestion">The serialized state plus the largest serialized question.</param>
internal sealed record AdviceSize(int Total, int StatePlusLargestQuestion);

/// <summary>The bounds an Advisor applies to one request and concurrent requests.</summary>
/// <param name="Unit">The unit used for both size limits and measurements.</param>
/// <param name="MaxTotal">The maximum total request size.</param>
/// <param name="MaxStatePlusQuestion">The maximum state plus one question size.</param>
/// <param name="MaxQuestions">The maximum questions per request.</param>
/// <param name="MaxOptions">The maximum options per question.</param>
/// <param name="MaxConcurrency">The maximum requests in flight.</param>
/// <param name="RequestTimeout">The timeout for one Advisor call, including retries.</param>
internal sealed record AdvisorLimits(string Unit,
                                     int MaxTotal,
                                     int MaxStatePlusQuestion,
                                     int MaxQuestions,
                                     int MaxOptions,
                                     int MaxConcurrency,
                                     TimeSpan RequestTimeout);

/// <summary>A service failure category, independent of its wire protocol.</summary>
internal enum AdvisorFailureKind
{
    /// <summary>The credential was not accepted.</summary>
    Unauthorized,
    /// <summary>The service refused access.</summary>
    Forbidden,
    /// <summary>The service's rate limit was reached.</summary>
    RateLimited,
    /// <summary>The service was overloaded.</summary>
    Overloaded,
    /// <summary>The request exceeded the service's size limit.</summary>
    TooLarge,
    /// <summary>The service rejected the request.</summary>
    Rejected,
    /// <summary>The request timed out.</summary>
    TimedOut,
    /// <summary>The service or transport was unavailable.</summary>
    Unavailable,
    /// <summary>The service returned an unparseable or unexpected response.</summary>
    InvalidResponse
}

/// <summary>A sanitized service failure; its message is fixed for its kind and holds no service content.</summary>
internal sealed class AdvisorException : Exception
{
    /// <summary>Creates a failure without retaining a potentially sensitive underlying exception.</summary>
    /// <param name="kind">The service-independent failure category.</param>
    public AdvisorException(AdvisorFailureKind kind) : base(kind switch
    {
        AdvisorFailureKind.Unauthorized => "Advisor authentication failed.",
        AdvisorFailureKind.Forbidden => "Advisor access was forbidden.",
        AdvisorFailureKind.RateLimited => "Advisor rate limit reached.",
        AdvisorFailureKind.Overloaded => "Advisor is overloaded.",
        AdvisorFailureKind.TooLarge => "Advisor request was too large.",
        AdvisorFailureKind.Rejected => "Advisor request was rejected.",
        AdvisorFailureKind.TimedOut => "Advisor request timed out.",
        AdvisorFailureKind.Unavailable => "Advisor is unavailable.",
        _ => "Advisor returned an invalid response."
    }) => Kind = kind;

    /// <summary>The failure category used by the core's accounting.</summary>
    public AdvisorFailureKind Kind { get; }
}
