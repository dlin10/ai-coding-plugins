using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Advisors;
using PlanForge.Advisors.MicrosoftDecision;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// What Microsoft-Decision-1 adds to the shared systemone transport, over recorded HTTP exchanges: its address, credential
/// header, status meanings and pacing. The transport's timeout, cancellation, measuring and sanitizing are JevAdvisorTests'.
/// </summary>
public sealed class MicrosoftDecisionAdvisorTests
{
    /// <summary>A sentinel credential that must never appear in failures or bodies.</summary>
    private const string KEY = "secret-msdecision-key-never-in-diagnostics";

    /// <summary>The configured resource endpoint.</summary>
    private const string ENDPOINT = "https://resource.services.ai.azure.invalid";

    /// <summary>The deployment name, sent as the requested model.</summary>
    private const string DEPLOYMENT = "Decision-1";

    /// <summary>A response in the shape the live deployment returned on 2026-10-20, with a colon in the question id.</summary>
    private const string SUCCESS = """
        {"model":"microsoft-decision-1","answers":{"relationship:0123456789abcdef":{"choice":"unrelated","confidence":0.66,"probabilities":{"supports":0.02,"unrelated":0.98},"type":"choice"}},"usage":{"input_tokens":162,"output_tokens":2}}
        """;

    /// <summary>Every wait the adapter asked for, retries and pacing alike.</summary>
    private readonly List<TimeSpan> _waits = [];

    /// <summary>The credential goes only in the api-key header, to the route under the configured endpoint, with the deployment as model.</summary>
    [Fact]
    public async Task Request_has_the_route_the_api_key_header_and_the_deployment_as_model()
    {
        var handler = new StubHandler();
        using var advisor = Advisor(handler);
        await advisor.AskAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(ENDPOINT + "/providers/microsoft/v1/systemone", sent.Url);
        Assert.Equal(KEY, sent.ApiKey);
        Assert.Null(sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(DEPLOYMENT, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("choice", body.RootElement.GetProperty("questions").GetProperty("relationship:0123456789abcdef")
                                   .GetProperty("type").GetString());
        Assert.DoesNotContain(KEY, sent.Body);
    }

    /// <summary>The route follows any gateway path, without a trailing slash doubling it.</summary>
    /// <param name="endpoint">The configured endpoint.</param>
    /// <param name="expected">The address requests are sent to.</param>
    [Theory]
    [InlineData("https://r.invalid", "https://r.invalid/providers/microsoft/v1/systemone")]
    [InlineData("https://r.invalid/", "https://r.invalid/providers/microsoft/v1/systemone")]
    [InlineData("https://r.invalid:8443/", "https://r.invalid:8443/providers/microsoft/v1/systemone")]
    [InlineData("https://gw.invalid/apim/decision/", "https://gw.invalid/apim/decision/providers/microsoft/v1/systemone")]
    public void The_endpoint_path_is_kept_and_a_trailing_slash_is_trimmed(string endpoint, string expected) =>
        Assert.Equal(expected, MicrosoftDecisionAdvisor.RequestUri(endpoint)!.AbsoluteUri);

    /// <summary>The answer, the unversioned served model and the usage are preserved.</summary>
    [Fact]
    public async Task A_200_maps_the_choice_and_the_unversioned_served_model()
    {
        using var advisor = Advisor(new StubHandler());
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        var answer = Assert.IsType<Answered>(result.Answers["relationship:0123456789abcdef"]);
        Assert.Equal("unrelated", answer.Choice);
        Assert.Equal(0.98, answer.Probabilities["unrelated"]);
        Assert.Equal(0.66, answer.Confidence);
        Assert.Equal("microsoft-decision-1", result.ServedModel);
        Assert.Equal(new AdviceUsage(162, 2), result.Usage);
    }

    /// <summary>The adapter reports its identity without making a request.</summary>
    [Fact]
    public void The_identity_is_reported_without_a_request()
    {
        var handler = new StubHandler();
        using var advisor = Advisor(handler);
        Assert.Equal("microsoft-decision", advisor.Id);
        Assert.Equal(DEPLOYMENT, advisor.RequestedModel);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Only a body that blames the request is a persistent kind; a wrong endpoint or deployment, and a status a gateway
    /// could send, stay transient. The 400, 404 and token-limit 422 bodies are those the live deployment returned on 2026-10-20.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The response body.</param>
    /// <param name="expected">The name of the expected failure kind.</param>
    [Theory]
    [InlineData(400, """{"error":{"code":"unsupported_request_argument","message":"Model does not support request argument supplied: invalid question q"}}""", nameof(AdvisorFailureKind.Rejected))]
    [InlineData(400, """{"error":{"code":"BadRequest","message":"Missing required query parameter: api-version"}}""", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(401, "", nameof(AdvisorFailureKind.Unauthorized))]
    [InlineData(403, "", nameof(AdvisorFailureKind.Forbidden))]
    [InlineData(404, """{"error":{"code":"DeploymentNotFound","message":"The API deployment x does not exist."}}""", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(404, "404 page not found", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(405, "", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(408, "", nameof(AdvisorFailureKind.TimedOut))]
    [InlineData(413, "", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(415, "", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(422, """{"detail":"invalid TypeSafe request: request is about 67253 tokens; the limit for the state plus all questions is 64000"}""", nameof(AdvisorFailureKind.TooLarge))]
    [InlineData(422, """{"detail":"invalid model request: Question prompt exceeds the 32768-token context limit including the decision token"}""", nameof(AdvisorFailureKind.TooLarge))]
    [InlineData(422, """{"detail":"The question definition is not supported."}""", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(500, "", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(503, "", nameof(AdvisorFailureKind.Unavailable))]
    [InlineData(529, "", nameof(AdvisorFailureKind.Unavailable))]
    public async Task A_status_maps_to_its_failure_kind(int status, string body, string expected)
    {
        var kind = Enum.Parse<AdvisorFailureKind>(expected);
        var handler = Respond(status, body);
        using var advisor = Advisor(handler);
        var failure = await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None));
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(new AdvisorException(kind).Message, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Single(handler.Requests);
    }

    /// <summary>A credential that cannot be a header value fails as unauthorized before anything is sent.</summary>
    [Fact]
    public async Task A_key_that_cannot_be_a_header_is_unauthorized_and_sends_nothing()
    {
        var handler = new StubHandler();
        using var advisor = new MicrosoftDecisionAdvisor(ENDPOINT, KEY + "\nInjected: value", DEPLOYMENT, handler, Delay, new MapperClock());
        var failure = await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None));
        Assert.Equal(AdvisorFailureKind.Unauthorized, failure.Kind);
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Empty(handler.Requests);
    }

    /// <summary>A 429 is retried after its backoff, and the retry also waits for the bucket the 429 proved empty.</summary>
    [Fact]
    public async Task A_429_is_retried_after_its_backoff_and_a_paced_start()
    {
        var handler = new StubHandler(attempt => attempt == 1 ? Response(429, KEY) : Response(200, SUCCESS));
        using var advisor = Advisor(handler);
        Assert.Equal("microsoft-decision-1", (await advisor.AskAsync(Request(), CancellationToken.None)).ServedModel);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1 / 0.9)], _waits);
    }

    /// <summary>Once a response reports the deployment's bucket, requests within it start without waiting.</summary>
    [Fact]
    public async Task A_reported_quota_lets_requests_start_without_waiting()
    {
        var handler = new StubHandler(attempt =>
        {
            var response = Response(200, SUCCESS);
            response.Headers.Add("x-ratelimit-limit-requests", "60");
            response.Headers.Add("x-ratelimit-renewalperiod-requests", "60");
            response.Headers.Add("x-ratelimit-remaining-requests", (60 - attempt).ToString());
            return response;
        });
        using var advisor = Advisor(handler);
        for (var call = 0; call < 10; call++) await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Equal(10, handler.Requests.Count);
        Assert.Empty(_waits);
    }

    /// <summary>A caller who cancels while waiting for the quota sends nothing more.</summary>
    [Fact]
    public async Task Caller_cancellation_interrupts_the_pacing_wait()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler();
        using var advisor = new MicrosoftDecisionAdvisor(ENDPOINT, KEY, DEPLOYMENT, handler, async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, new MapperClock());
        await advisor.AskAsync(Request(), CancellationToken.None);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AskAsync(Request(), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Single(handler.Requests);
    }

    /// <summary>Production Advisors, built afresh for each map call, share one pacer; test transports get their own.</summary>
    [Fact]
    public void Production_Advisors_share_one_pacer()
    {
        using var first = new MicrosoftDecisionAdvisor(ENDPOINT, KEY, DEPLOYMENT);
        using var second = new MicrosoftDecisionAdvisor(ENDPOINT, KEY, DEPLOYMENT);
        using var test = Advisor(new StubHandler());
        Assert.Same(first.Pacer, second.Pacer);
        Assert.NotSame(first.Pacer, test.Pacer);
    }

    /// <summary>Creates an Advisor whose retry and pacing waits are recorded rather than slept.</summary>
    /// <param name="handler">The recording transport.</param>
    private MicrosoftDecisionAdvisor Advisor(StubHandler handler) =>
        new(ENDPOINT, KEY, DEPLOYMENT, handler, Delay, new MapperClock());

    /// <summary>Records a wait without sleeping, honoring cancellation.</summary>
    /// <param name="wait">The requested wait.</param>
    /// <param name="ct">The call's linked cancellation token.</param>
    private Task Delay(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _waits.Add(wait);
        return Task.CompletedTask;
    }

    /// <summary>Creates a request shaped like the core's pair judgment.</summary>
    private static AdviceRequest Request() => new(new JsonObject { ["text"] = "context" }, new JsonObject(),
        [new("relationship:0123456789abcdef", "How does the section relate to the item?",
             [new("supports", "It agrees"), new("unrelated", "It is not about it")])]);

    /// <summary>Creates a handler that always returns the supplied response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static StubHandler Respond(int status, string body) => new(_ => Response(status, body));

    /// <summary>Creates a JSON response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>A captured request, detached from the disposed HTTP objects.</summary>
    /// <param name="Url">The destination.</param>
    /// <param name="ApiKey">The api-key header.</param>
    /// <param name="Authorization">The authorization header, which must be absent.</param>
    /// <param name="Body">The exact UTF-8 body decoded as text.</param>
    private sealed record SentRequest(string? Url, string? ApiKey, string? Authorization, string Body);

    /// <summary>A deterministic HTTP transport that records every attempt before answering it.</summary>
    /// <param name="respond">The response for each one-based attempt, defaulting to a successful answer.</param>
    private sealed class StubHandler(Func<int, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        /// <summary>Requests in send order.</summary>
        public List<SentRequest> Requests { get; } = [];

        /// <summary>Captures a request and returns the next stub response.</summary>
        /// <param name="request">The outgoing request.</param>
        /// <param name="cancellationToken">The timeout and caller cancellation token.</param>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri?.AbsoluteUri,
                request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return respond?.Invoke(Requests.Count) ?? Response(200, SUCCESS);
        }
    }
}
