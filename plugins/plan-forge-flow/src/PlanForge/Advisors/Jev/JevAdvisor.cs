using System.Net.Http.Headers;
using PlanForge.Advisors.SystemOne;

namespace PlanForge.Advisors.Jev;

/// <summary>TypeSafe's typed Choice service: systemone at TypeSafe's own endpoint, with a bearer credential.</summary>
internal sealed class JevAdvisor : SystemOneAdvisor
{
    /// <summary>The only endpoint to which this adapter sends content or authentication.</summary>
    private const string ENDPOINT = "https://api.typesafe.ai/v1/systemone";

    /// <summary>The credential, used only on an outgoing request's header.</summary>
    private readonly string _key;

    /// <summary>Creates the adapter without probing or sending any request.</summary>
    /// <param name="key">The credential read by the server from its environment.</param>
    /// <param name="model">The pinned model requested by the server.</param>
    /// <param name="handler">An optional owned transport for deterministic tests.</param>
    /// <param name="delay">An optional cancellable retry wait for deterministic tests.</param>
    /// <param name="clock">The timeout clock, defaulting to the system clock.</param>
    public JevAdvisor(string key, string model, HttpMessageHandler? handler = null,
                      Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null)
        : base(new Uri(ENDPOINT), model, handler, delay, clock) => _key = key;

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "jev";

    /// <summary>Sends the credential as a bearer token.</summary>
    /// <param name="message">The attempt's request.</param>
    protected override void Authenticate(HttpRequestMessage message) =>
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);

    /// <summary>Classifies every HTTP status without exposing the response's content.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">Content used only to recognize the service's size refusal.</param>
    protected override AdvisorFailureKind Failure(int status, string body) => status switch
    {
        400 when body.Contains("max_tokens_exceeded", StringComparison.Ordinal) => AdvisorFailureKind.TooLarge,
        401 => AdvisorFailureKind.Unauthorized,
        403 => AdvisorFailureKind.Forbidden,
        408 => AdvisorFailureKind.TimedOut,
        413 => AdvisorFailureKind.TooLarge,
        429 => AdvisorFailureKind.RateLimited,
        529 => AdvisorFailureKind.Overloaded,
        >= 400 and < 500 => AdvisorFailureKind.Rejected,
        >= 500 and < 600 => AdvisorFailureKind.Unavailable,
        _ => AdvisorFailureKind.InvalidResponse
    };
}
