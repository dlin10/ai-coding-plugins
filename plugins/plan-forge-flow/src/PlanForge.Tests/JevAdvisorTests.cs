using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Advisors;
using PlanForge.Advisors.Jev;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises the real Jev adapter over recorded, deterministic HTTP exchanges.</summary>
public sealed class JevAdvisorTests
{
    /// <summary>A sentinel credential that must never appear in failures.</summary>
    private const string KEY = "secret-jev-key-never-in-diagnostics";

    /// <summary>The pinned requested model, deliberately different from the served model.</summary>
    private const string MODEL = "jev-1.13.0";

    /// <summary>A complete service response with reported usage.</summary>
    private const string SUCCESS = """
        {"model":"jev-1.14.0","answers":{"relevance":{"type":"choice","choice":"yes","probabilities":{"yes":0.8,"no":0.2},"confidence":0.6}},"usage":{"input_tokens":120,"output_tokens":12}}
        """;

    /// <summary>Only the intended endpoint receives the credential and ordered wire questions.</summary>
    [Fact]
    public async Task Request_has_the_endpoint_bearer_header_and_ordered_choice_shape()
    {
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        var request = Request();
        await advisor.AskAsync(request, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", sent.Url);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("Bearer " + KEY, sent.Authorization);
        Assert.Equal("application/json", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Equal(["state", "model", "questions"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(MODEL, root.GetProperty("model").GetString());
        Assert.Equal("context", root.GetProperty("state").GetProperty("text").GetString());
        var question = root.GetProperty("questions").GetProperty("relevance");
        Assert.Equal(["type", "instructions", "criteria"], question.EnumerateObject().Select(property => property.Name));
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Does it apply?", question.GetProperty("instructions").GetString());
        Assert.Equal(["yes", "no"], question.GetProperty("criteria").EnumerateObject().Select(property => property.Name));
        Assert.Equal("It applies", question.GetProperty("criteria").GetProperty("yes").GetString());
        Assert.Equal("It does not", question.GetProperty("criteria").GetProperty("no").GetString());
        Assert.DoesNotContain(KEY, sent.Body);
    }

    /// <summary>The exact sent bytes, JSON escaping included, so that a change of transport cannot alter them.</summary>
    [Fact]
    public async Task The_request_body_is_byte_for_byte_stable()
    {
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        await advisor.AskAsync(Request("<T> & \"q\" 世界\n"), CancellationToken.None);
        Assert.Equal("{\"state\":{\"text\":\"\\u003CT\\u003E \\u0026 \\u0022q\\u0022 \\u4E16\\u754C\\n\"},\"model\":\"jev-1.13.0\","
                     + "\"questions\":{\"relevance\":{\"type\":\"choice\",\"instructions\":\"Does it apply?\","
                     + "\"criteria\":{\"yes\":\"It applies\",\"no\":\"It does not\"}}}}", Assert.Single(handler.Requests).Body);
    }

    /// <summary>The service's answers, distribution, confidence, model and usage are preserved.</summary>
    [Fact]
    public async Task A_200_maps_the_choice_and_response_metadata()
    {
        using var advisor = new JevAdvisor(KEY, MODEL, new StubHandler());
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        var answer = Assert.IsType<Answered>(Assert.Single(result.Answers).Value);
        Assert.Equal("relevance", Assert.Single(result.Answers).Key);
        Assert.Equal("yes", answer.Choice);
        Assert.Equal(0.8, answer.Probabilities["yes"]);
        Assert.Equal(0.2, answer.Probabilities["no"]);
        Assert.Equal(0.6, answer.Confidence);
        Assert.Equal("jev-1.14.0", result.ServedModel);
        Assert.Equal(120, result.Usage!.InputTokens);
        Assert.Equal(12, result.Usage.OutputTokens);
    }

    /// <summary>Usage and confidence that the service does not report stay unknown.</summary>
    [Fact]
    public async Task Missing_usage_and_confidence_are_null()
    {
        var handler = Respond(200, """{"model":"jev-1.13.0","answers":{"relevance":{"type":"choice","choice":"yes","probabilities":{"yes":1,"no":0}}}}""");
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Null(result.Usage);
        Assert.Null(Assert.IsType<Answered>(result.Answers["relevance"]).Confidence);
    }

    /// <summary>Partial token usage does not invent the missing count.</summary>
    [Fact]
    public async Task Missing_output_tokens_stay_null()
    {
        using var advisor = new JevAdvisor(KEY, MODEL, Respond(200, SUCCESS.Replace("\"output_tokens\":12", "\"output_tokens\":null", StringComparison.Ordinal)));
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Equal(120, result.Usage!.InputTokens);
        Assert.Null(result.Usage.OutputTokens);
    }

    /// <summary>The adapter reports its exact service bounds without making a request.</summary>
    [Fact]
    public void Limits_match_the_Jev_contract()
    {
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        Assert.Equal("jev", advisor.Id);
        Assert.Equal(MODEL, advisor.RequestedModel);
        Assert.Equal(new AdvisorLimits("estimated_tokens", 64_000, 32_000, 64, 255, 8, TimeSpan.FromSeconds(30)), advisor.Limits);
        Assert.Empty(handler.Requests);
    }

    /// <summary>An unparseable response is sanitized.</summary>
    [Fact]
    public Task An_unparseable_200_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse, KEY);

    /// <summary>A blank served model is not a usable response.</summary>
    [Fact]
    public Task A_blank_model_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("jev-1.14.0", " ", StringComparison.Ordinal));

    /// <summary>A JSON null cannot supply the response envelope.</summary>
    [Fact]
    public Task A_null_envelope_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse, "null");

    /// <summary>A wrong wire type is an unparseable response.</summary>
    [Fact]
    public Task A_wrong_field_type_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("\"confidence\":0.6", "\"confidence\":\"" + KEY + "\"", StringComparison.Ordinal));

    /// <summary>An empty successful status is not a Choice response.</summary>
    [Fact]
    public Task A_204_is_invalid_response() => AssertFailure(204, AdvisorFailureKind.InvalidResponse);

    /// <summary>A redirect is rejected without issuing a second HTTP request.</summary>
    [Fact]
    public async Task A_302_is_invalid_response_and_is_not_followed()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = Response(302, KEY);
            response.Headers.Location = new Uri("https://example.invalid/secret-target");
            return Task.FromResult(response);
        });
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.InvalidResponse);
        Assert.Single(handler.Requests);
    }

    /// <summary>Authentication failures have a typed, content-free outcome.</summary>
    [Fact]
    public Task A_401_is_unauthorized() => AssertFailure(401, AdvisorFailureKind.Unauthorized);

    /// <summary>Access denials are distinct from missing credentials.</summary>
    [Fact]
    public Task A_403_is_forbidden() => AssertFailure(403, AdvisorFailureKind.Forbidden);

    /// <summary>Unrecognized client errors are rejected.</summary>
    [Fact]
    public Task A_404_is_rejected() => AssertFailure(404, AdvisorFailureKind.Rejected);

    /// <summary>A server-reported timeout uses the timeout category.</summary>
    [Fact]
    public Task A_408_is_timed_out() => AssertFailure(408, AdvisorFailureKind.TimedOut);

    /// <summary>The service's token-limit marker distinguishes a size refusal.</summary>
    [Fact]
    public Task A_400_token_limit_is_too_large() => AssertFailure(400, AdvisorFailureKind.TooLarge, "max_tokens_exceeded " + KEY);

    /// <summary>An ordinary bad request is rejected and is never retried.</summary>
    [Fact]
    public Task Another_400_is_rejected_without_retry() => AssertFailure(400, AdvisorFailureKind.Rejected);

    /// <summary>The HTTP size status needs no response-body marker.</summary>
    [Fact]
    public Task A_413_is_too_large() => AssertFailure(413, AdvisorFailureKind.TooLarge);

    /// <summary>Unsupported content is a rejected request.</summary>
    [Fact]
    public Task A_415_is_rejected() => AssertFailure(415, AdvisorFailureKind.Rejected);

    /// <summary>Validation failures are rejected requests.</summary>
    [Fact]
    public Task A_422_is_rejected() => AssertFailure(422, AdvisorFailureKind.Rejected);

    /// <summary>Server faults are unavailable and are not retried.</summary>
    [Fact]
    public Task A_500_is_unavailable() => AssertFailure(500, AdvisorFailureKind.Unavailable);

    /// <summary>A 503 is unavailable rather than a retryable overload status.</summary>
    [Fact]
    public Task A_503_is_unavailable() => AssertFailure(503, AdvisorFailureKind.Unavailable);

    /// <summary>Other informational, successful and redirect statuses are invalid responses.</summary>
    /// <param name="status">A non-200 status outside the error ranges.</param>
    [Theory]
    [InlineData(101)]
    [InlineData(201)]
    [InlineData(307)]
    public Task Other_non_error_statuses_are_invalid_response(int status) => AssertFailure(status, AdvisorFailureKind.InvalidResponse);

    /// <summary>Transport errors never retain their original, potentially sensitive exception.</summary>
    [Fact]
    public async Task A_transport_exception_is_unavailable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException(KEY));
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Unavailable);
        Assert.Single(handler.Requests);
    }

    /// <summary>Failures while consuming a response are sanitized transport failures too.</summary>
    [Fact]
    public async Task An_io_exception_is_unavailable()
    {
        using var advisor = new JevAdvisor(KEY, MODEL, new StubHandler((_, _) => throw new IOException(KEY)));
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Unavailable);
    }

    /// <summary>Rate limiting may recover on the next attempt with an identical body and credential.</summary>
    [Fact]
    public async Task A_429_then_200_succeeds_on_retry()
    {
        var waits = new List<TimeSpan>();
        var handler = new StubHandler((attempt, _) => Task.FromResult(Response(attempt == 1 ? 429 : 200, attempt == 1 ? KEY : SUCCESS)));
        using var advisor = new JevAdvisor(KEY, MODEL, handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Assert.Equal("jev-1.14.0", (await advisor.AskAsync(Request(), CancellationToken.None)).ServedModel);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);
    }

    /// <summary>Rate limiting is retried twice with the default one-then-two-second backoff.</summary>
    [Fact]
    public Task Three_429s_end_rate_limited_after_three_attempts() => AssertRetries(429, AdvisorFailureKind.RateLimited);

    /// <summary>Overload is retried twice and preserves the last failure category.</summary>
    [Fact]
    public Task Three_529s_end_overloaded_after_three_attempts() => AssertRetries(529, AdvisorFailureKind.Overloaded);

    /// <summary>The last attempt determines the exhausted retry outcome.</summary>
    [Fact]
    public async Task Exhaustion_uses_the_last_attempts_kind()
    {
        var handler = new StubHandler((attempt, _) => Task.FromResult(Response(attempt == 3 ? 529 : 429, KEY)));
        using var advisor = new JevAdvisor(KEY, MODEL, handler, NoDelay);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Overloaded);
        Assert.Equal(3, handler.Requests.Count);
    }

    /// <summary>Retry-After seconds are honored.</summary>
    [Fact]
    public Task Retry_after_two_seconds_is_honored() => AssertRetryAfter(2, 2);

    /// <summary>A large Retry-After cannot consume the call's time budget.</summary>
    [Fact]
    public Task Retry_after_sixty_seconds_is_capped_at_five() => AssertRetryAfter(60, 5);

    /// <summary>The adapter's own thirty-second timeout has a distinct typed outcome.</summary>
    [Fact]
    public async Task The_thirty_second_timeout_is_timed_out()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(200, SUCCESS);
        });
        var clock = new MapperClock();
        using var advisor = new JevAdvisor(KEY, MODEL, handler, clock: clock);
        var call = advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(31));
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => call.WaitAsync(TimeSpan.FromSeconds(1))),
            AdvisorFailureKind.TimedOut);
    }

    /// <summary>The request remains active just before thirty seconds and can still complete successfully.</summary>
    [Fact]
    public async Task Just_under_thirty_seconds_does_not_time_out()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler((_, ct) => release.Task.WaitAsync(ct));
        var clock = new MapperClock();
        using var advisor = new JevAdvisor(KEY, MODEL, handler, clock: clock);
        var call = advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.False(call.IsCompleted);
        release.SetResult(Response(200, SUCCESS));
        Assert.Equal("jev-1.14.0", (await call.WaitAsync(TimeSpan.FromSeconds(1))).ServedModel);
    }

    /// <summary>Caller cancellation stays cancellation, with the caller's token and no secret message.</summary>
    [Fact]
    public async Task Caller_cancellation_surfaces_as_operation_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(200, SUCCESS);
        });
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AskAsync(Request(), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Single(handler.Requests);
    }

    /// <summary>A cancelled retry wait cannot cause another attempt.</summary>
    [Fact]
    public async Task Caller_cancellation_interrupts_retry_backoff()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = Respond(429, KEY);
        using var advisor = new JevAdvisor(KEY, MODEL, handler, async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AskAsync(Request(), cancellation.Token));
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Single(handler.Requests);
    }

    /// <summary>A caller who already cancelled sends no content.</summary>
    [Fact]
    public async Task Pre_cancelled_calls_send_nothing()
    {
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AskAsync(Request(), cancellation.Token));
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Empty(handler.Requests);
    }

    /// <summary>ASCII sizing is derived from the actual sent bytes.</summary>
    [Fact]
    public Task Measure_matches_an_ascii_body() => AssertMeasure("An ASCII context");

    /// <summary>Non-Latin text is measured after JSON escaping and UTF-8 encoding.</summary>
    [Fact]
    public Task Measure_matches_cyrillic_and_cjk() => AssertMeasure("Привет мир 世界 日本語");

    /// <summary>JSON escaping, including HTML-sensitive characters, is counted exactly as sent.</summary>
    [Fact]
    public Task Measure_matches_quotes_slashes_newlines_and_angle_brackets() => AssertMeasure("\"quoted\" C:\\repo\\file\nnext\r\n<T>");

    /// <summary>The state-plus-question bound uses the largest question, rather than all questions.</summary>
    [Fact]
    public async Task Measure_matches_the_state_plus_largest_serialized_question()
    {
        var request = new AdviceRequest(new JsonObject { ["text"] = "Shared state 世界\n" },
        [
            new("short", "Short", [new("a", "A")]),
            new("largest", new string('長', 80), [new("yes", "A long description <T>\n"), new("no", "No")]),
            new("medium", "Medium sized question", [new("a", "One"), new("b", "Two")])
        ]);
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        var measure = advisor.Measure(request);
        Assert.Empty(handler.Requests);
        await advisor.AskAsync(request, CancellationToken.None);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var stateBytes = Encoding.UTF8.GetByteCount(body.RootElement.GetProperty("state").GetRawText());
        var questions = body.RootElement.GetProperty("questions").EnumerateObject().ToArray();
        Assert.Equal(["short", "largest", "medium"], questions.Select(question => question.Name));
        var sizes = questions.Select(question => Encoding.UTF8.GetByteCount(question.Value.GetRawText())).ToArray();
        Assert.True(sizes[1] > sizes[0] && sizes[1] > sizes[2]);
        Assert.Equal((stateBytes + sizes.Max() + 2) / 3, measure.StatePlusLargestQuestion);
    }

    /// <summary>Creates a simple ordered request without serializing through reflection.</summary>
    /// <param name="text">The shared state text.</param>
    private static AdviceRequest Request(string text = "context") => new(new JsonObject { ["text"] = text },
        [new("relevance", "Does it apply?", [new("yes", "It applies"), new("no", "It does not")])]);

    /// <summary>Creates a handler that always returns the supplied response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static StubHandler Respond(int status, string body) => new((_, _) => Task.FromResult(Response(status, body)));

    /// <summary>Creates a response containing a body that must not leak through a failure.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>Skips a retry wait while honoring cancellation.</summary>
    /// <param name="wait">The requested retry wait.</param>
    /// <param name="ct">The call's linked cancellation token.</param>
    private static Task NoDelay(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Checks one non-retryable failure, including diagnostic sanitization.</summary>
    /// <param name="status">The status returned by the stub.</param>
    /// <param name="kind">The expected service-independent failure.</param>
    /// <param name="body">The body, defaulting to the sentinel secret.</param>
    private static async Task AssertFailure(int status, AdvisorFailureKind kind, string body = KEY)
    {
        var handler = Respond(status, body);
        using var advisor = new JevAdvisor(KEY, MODEL, handler, NoDelay);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)), kind);
        Assert.Single(handler.Requests);
    }

    /// <summary>Checks that every service failure is a fixed message with no retained sensitive cause.</summary>
    /// <param name="failure">The adapter's failure.</param>
    /// <param name="kind">The expected category.</param>
    private static void AssertSanitized(AdvisorException failure, AdvisorFailureKind kind)
    {
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(new AdvisorException(kind).Message, failure.Message);
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Null(failure.InnerException);
    }

    /// <summary>Checks retry count, wait schedule and the final sanitized failure.</summary>
    /// <param name="status">The repeatedly returned HTTP status.</param>
    /// <param name="kind">The final expected failure kind.</param>
    private static async Task AssertRetries(int status, AdvisorFailureKind kind)
    {
        var waits = new List<TimeSpan>();
        var handler = Respond(status, KEY);
        using var advisor = new JevAdvisor(KEY, MODEL, handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)), kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    /// <summary>Checks a Retry-After duration through the injected delay.</summary>
    /// <param name="seconds">The server's requested duration.</param>
    /// <param name="expected">The duration the adapter must wait.</param>
    private static async Task AssertRetryAfter(int seconds, int expected)
    {
        var waits = new List<TimeSpan>();
        var handler = new StubHandler((attempt, _) =>
        {
            var response = Response(attempt == 1 ? 429 : 200, attempt == 1 ? KEY : SUCCESS);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        });
        using var advisor = new JevAdvisor(KEY, MODEL, handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(expected)], waits);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>Compares a measurement to the exact recorded UTF-8 body.</summary>
    /// <param name="text">The state text whose escaping and encoding are exercised.</param>
    private static async Task AssertMeasure(string text)
    {
        var handler = new StubHandler();
        using var advisor = new JevAdvisor(KEY, MODEL, handler);
        var request = Request(text);
        var size = advisor.Measure(request);
        Assert.Empty(handler.Requests);
        await advisor.AskAsync(request, CancellationToken.None);
        var body = Assert.Single(handler.Requests).Body;
        Assert.Equal((Encoding.UTF8.GetByteCount(body) + 2) / 3, size.Total);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(text, json.RootElement.GetProperty("state").GetProperty("text").GetString());
    }

    /// <summary>A captured request, detached from the disposed HTTP objects.</summary>
    /// <param name="Url">The destination.</param>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Authorization">The request-specific authorization header.</param>
    /// <param name="ContentType">The request body's media type.</param>
    /// <param name="Body">The exact UTF-8 body decoded as text.</param>
    private sealed record SentRequest(string? Url, string Method, string? Authorization, string? ContentType, string Body);

    /// <summary>A deterministic HTTP transport that records every attempt before returning or failing.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>The response or failure for each one-based attempt.</summary>
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        /// <summary>Requests in send order.</summary>
        public List<SentRequest> Requests { get; } = [];

        /// <summary>Creates a recording transport with an optional response behavior.</summary>
        /// <param name="respond">The per-attempt response, defaulting to a successful Choice answer.</param>
        public StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>>? respond = null)
            => _respond = respond ?? ((_, _) => Task.FromResult(Response(200, SUCCESS)));

        /// <summary>Captures a request and invokes the next stub response.</summary>
        /// <param name="request">The outgoing request.</param>
        /// <param name="cancellationToken">The timeout and caller cancellation token.</param>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri?.AbsoluteUri, request.Method.Method, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await _respond(Requests.Count, cancellationToken);
        }
    }
}
