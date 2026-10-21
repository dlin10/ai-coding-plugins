using PlanForge.Advisors.SystemOne;

namespace PlanForge.Advisors.MicrosoftDecision;

/// <summary>
/// Microsoft-Decision-1 in Azure AI Foundry: systemone behind the user's own resource endpoint, with an api-key and
/// the deployment's request quota. The endpoint is not part of an Evidence cache key, so a refusal is cached only when
/// the response identifies the request itself as at fault; anything a wrong endpoint or deployment can cause stays transient.
/// </summary>
internal sealed class MicrosoftDecisionAdvisor : SystemOneAdvisor
{
    /// <summary>The route appended to the configured endpoint; the service takes no api-version.</summary>
    private const string ROUTE = "/providers/microsoft/v1/systemone";

    /// <summary>The credential header; Entra bearer tokens are not supported.</summary>
    private const string KEY_HEADER = "api-key";

    /// <summary>One pacer per process: the quota belongs to the deployment, and an Advisor is built for every map call.</summary>
    private static readonly QuotaPacer PACER = new();

    /// <summary>The credential, used only on an outgoing request's header.</summary>
    private readonly string _key;

    /// <summary>Creates the adapter without probing or sending any request.</summary>
    /// <param name="endpoint">The resource endpoint or gateway base, already accepted by <see cref="RequestUri"/>.</param>
    /// <param name="key">The credential read by the server from its environment.</param>
    /// <param name="deployment">The deployment name, sent as the requested model.</param>
    /// <param name="handler">An optional owned transport for deterministic tests, which also gets a pacer of its own.</param>
    /// <param name="delay">An optional cancellable retry and pacing wait for deterministic tests.</param>
    /// <param name="clock">The timeout and pacing clock, defaulting to the system clock.</param>
    public MicrosoftDecisionAdvisor(string endpoint, string key, string deployment, HttpMessageHandler? handler = null,
                                    Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null)
        : base(RequestUri(endpoint) ?? throw new ArgumentException("The endpoint is not a plain https address.", nameof(endpoint)),
               deployment, handler, delay, clock)
    {
        _key = key;
        Pacer = handler is null ? PACER : new QuotaPacer(clock, delay);
    }

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "microsoft-decision";

    /// <summary>The pacer this instance waits on: the process-wide one in production.</summary>
    internal QuotaPacer Pacer { get; }

    /// <summary>
    /// Builds the request address from a configured endpoint, or returns null unless it is a plain https address without
    /// credentials, query or fragment that is not a Foundry project endpoint, whose route demands an api-version.
    /// </summary>
    /// <param name="endpoint">The configured resource endpoint or gateway base, possibly ending in a slash.</param>
    internal static Uri? RequestUri(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        && endpoint.IndexOfAny(['?', '#', ' ', '\t', '\r', '\n']) < 0
        && !(uri.AbsolutePath.TrimEnd('/') + "/").Contains("/api/projects/", StringComparison.OrdinalIgnoreCase)
            ? new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + ROUTE) : null;

    /// <summary>Sends the credential in the api-key header.</summary>
    /// <param name="message">The attempt's request.</param>
    protected override void Authenticate(HttpRequestMessage message) => message.Headers.Add(KEY_HEADER, _key);

    /// <summary>Waits for the deployment's quota to allow the attempt.</summary>
    /// <param name="ct">The call's linked caller and timeout cancellation.</param>
    protected override Task PaceAsync(CancellationToken ct) => Pacer.WaitAsync(ct);

    /// <summary>Gives every response's quota report to the pacer.</summary>
    /// <param name="response">The attempt's response.</param>
    protected override void Observe(HttpResponseMessage response) => Pacer.Observe(response);

    /// <summary>
    /// Persistent kinds only where the body blames the request: an unsupported question is rejected and a token-limit
    /// refusal is too large. Every other refusal is unavailable and asked again once the configuration is fixed: a 404
    /// (unknown deployment or route), a project endpoint's missing api-version, and a 413 or 422 a gateway could send.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The content of a 400 or 422, used only to recognize the service's own refusals.</param>
    protected override AdvisorFailureKind Failure(int status, string body) => status switch
    {
        400 when body.Contains("unsupported_request_argument", StringComparison.Ordinal) => AdvisorFailureKind.Rejected,
        401 => AdvisorFailureKind.Unauthorized,
        403 => AdvisorFailureKind.Forbidden,
        408 => AdvisorFailureKind.TimedOut,
        422 when body.Contains("token", StringComparison.OrdinalIgnoreCase) => AdvisorFailureKind.TooLarge,
        429 => AdvisorFailureKind.RateLimited,
        >= 400 and < 600 => AdvisorFailureKind.Unavailable,
        _ => AdvisorFailureKind.InvalidResponse
    };
}
