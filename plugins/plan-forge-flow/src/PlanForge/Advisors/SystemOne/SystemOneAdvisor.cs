using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlanForge.Advisors.SystemOne;

/// <summary>
/// The systemone wire protocol, shared below IAdvisor by the services that speak it. A subclass owns its endpoint,
/// credential, status meanings and any pacing; this class owns the body, its measurement and limits, retries and parsing.
/// It is a helper, never a contract: the core sees only IAdvisor.
/// </summary>
internal abstract class SystemOneAdvisor : IAdvisor, IDisposable
{
    /// <summary>The shared production transport never follows redirects or stores credentials.</summary>
    private static readonly HttpClient HTTP = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The only address to which this Advisor sends content or authentication.</summary>
    private readonly Uri _endpoint;

    /// <summary>The transport, shared in production and owned when a handler was supplied.</summary>
    private readonly HttpClient _http;

    /// <summary>Whether this instance owns its test transport.</summary>
    private readonly bool _ownsHttp;

    /// <summary>The cancellable retry wait, replaceable by tests.</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>The clock driving the request timeout.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Creates the Advisor without probing or sending any request.</summary>
    /// <param name="endpoint">The only address that receives content or authentication.</param>
    /// <param name="model">The model requested on every attempt.</param>
    /// <param name="handler">An optional owned transport for deterministic tests.</param>
    /// <param name="delay">An optional cancellable retry wait for deterministic tests.</param>
    /// <param name="clock">The timeout clock, defaulting to the system clock.</param>
    protected SystemOneAdvisor(Uri endpoint, string model, HttpMessageHandler? handler,
                               Func<TimeSpan, CancellationToken, Task>? delay, TimeProvider? clock)
    {
        _endpoint = endpoint;
        RequestedModel = model;
        _ownsHttp = handler is not null;
        _http = handler is null ? HTTP : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _delay = delay ?? Task.Delay;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The stable Advisor identifier.</summary>
    public abstract string Id { get; }

    /// <summary>The model requested on every attempt.</summary>
    public string RequestedModel { get; }

    /// <summary>
    /// The protocol's bounds, the same at every service that speaks it, in estimated tokens: UTF-8 bytes divided by three
    /// and rounded up.
    /// </summary>
    public AdvisorLimits Limits { get; } = new("estimated_tokens", 64_000, 32_000, 64, 255, 8, TimeSpan.FromSeconds(30));

    /// <summary>Measures the same serialized body sent by AskAsync, without contacting the service.</summary>
    /// <param name="request">The state and ordered questions to measure.</param>
    public AdviceSize Measure(AdviceRequest request)
    {
        var body = Body(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, SystemOneJson.Default.SystemOneRequest);
        using var state = new MemoryStream();
        using (var writer = new Utf8JsonWriter(state)) request.State.WriteTo(writer, SystemOneJson.Default.Options);
        var largest = body.Questions.Values.Select(question =>
            JsonSerializer.SerializeToUtf8Bytes(question, SystemOneJson.Default.SystemOneQuestion).Length).DefaultIfEmpty(0).Max();
        return new(EstimatedTokens(bytes.Length), EstimatedTokens(checked((int)state.Length + largest)));
    }

    /// <summary>Sends a Choice request, retrying only rate limiting and overload, at most twice.</summary>
    /// <param name="request">The object state and ordered questions to send.</param>
    /// <param name="ct">Caller cancellation, which is never converted to an Advisor failure.</param>
    public async Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(Limits.RequestTimeout, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Body(request), SystemOneJson.Default.SystemOneRequest);
            for (var attempt = 0; ; attempt++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await PaceAsync(timeout.Token);
                using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                Authenticate(message);
                message.Content = new ByteArrayContent(bytes);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Observe(response);
                var status = (int)response.StatusCode;
                var body = status is 200 or 400 or 422 ? await response.Content.ReadAsStringAsync(timeout.Token) : "";
                if (status == 200) return Parse(body);
                var kind = Failure(status, body);
                if (kind is not (AdvisorFailureKind.RateLimited or AdvisorFailureKind.Overloaded) || attempt == 2)
                    throw new AdvisorException(kind);
                var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                var wait = TimeSpan.FromSeconds(seconds is >= 0 ? Math.Min(seconds.Value, 5) : attempt + 1);
                await _delay(wait, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new AdvisorException(AdvisorFailureKind.TimedOut);
        }
        catch (HttpRequestException) { throw new AdvisorException(AdvisorFailureKind.Unavailable); }
        catch (IOException) { throw new AdvisorException(AdvisorFailureKind.Unavailable); }
        catch (JsonException) { throw new AdvisorException(AdvisorFailureKind.InvalidResponse); }
        catch (FormatException) { throw new AdvisorException(AdvisorFailureKind.Unauthorized); }
    }

    /// <summary>Releases an injected transport while leaving the production client shared.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Puts the credential on one outgoing attempt; a value that cannot be a header throws FormatException.</summary>
    /// <param name="message">The attempt's request.</param>
    protected abstract void Authenticate(HttpRequestMessage message);

    /// <summary>Classifies a status other than 200 without retaining the response's content.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The content of a 400 or 422, empty for any other status, used only to recognize a refusal.</param>
    protected abstract AdvisorFailureKind Failure(int status, string body);

    /// <summary>Waits until the service's quota allows the next attempt; completes at once unless overridden.</summary>
    /// <param name="ct">The call's linked caller and timeout cancellation.</param>
    protected virtual Task PaceAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Sees every response before its status is handled; must not throw.</summary>
    /// <param name="response">The attempt's response.</param>
    protected virtual void Observe(HttpResponseMessage response) { }

    /// <summary>Builds ordered wire maps, leaving object-state serialization to the generated contract.</summary>
    /// <param name="request">The service-independent request.</param>
    private SystemOneRequest Body(AdviceRequest request)
    {
        var questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
            questions.Add(question.Id, new("choice", question.Instructions,
                question.Options.ToDictionary(option => option.Value, option => option.Description, StringComparer.Ordinal)));
        return new(request.State, RequestedModel, questions);
    }

    /// <summary>Rounds a UTF-8 byte count up to estimated tokens without integer overflow.</summary>
    /// <param name="bytes">The serialized UTF-8 byte count.</param>
    private static int EstimatedTokens(int bytes) => bytes / 3 + (bytes % 3 == 0 ? 0 : 1);

    /// <summary>Maps the wire response, leaving per-question validity to the core.</summary>
    /// <param name="body">The response JSON, never retained by an exception.</param>
    private static AdviceResult Parse(string body)
    {
        var response = JsonSerializer.Deserialize(body, SystemOneJson.Default.SystemOneResponse);
        if (response is null || string.IsNullOrWhiteSpace(response.Model) || response.Answers is null)
            throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        var answers = new Dictionary<string, AdviceOutcome>(StringComparer.Ordinal);
        foreach (var (id, answer) in response.Answers)
        {
            if (answer is null || answer.Type != "choice" || answer.Choice is null || answer.Probabilities is null)
                throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
            answers.Add(id, new Answered(answer.Choice, answer.Probabilities, answer.Confidence));
        }
        return new(answers, response.Model,
            response.Usage is { } usage ? new(usage.InputTokens, usage.OutputTokens) : null);
    }
}

/// <summary>The systemone request body, in its exact wire property order.</summary>
/// <param name="State">The supplied JSON object.</param>
/// <param name="Model">The requested model.</param>
/// <param name="Questions">Ordered questions keyed by their identifiers.</param>
internal sealed record SystemOneRequest(JsonObject State, string Model, Dictionary<string, SystemOneQuestion> Questions);

/// <summary>The wire form of one Choice question.</summary>
/// <param name="Type">The literal choice discriminator.</param>
/// <param name="Instructions">The question's exact instructions.</param>
/// <param name="Criteria">Option values and descriptions in their original order.</param>
internal sealed record SystemOneQuestion(string Type, string Instructions, Dictionary<string, string> Criteria);

/// <summary>The response envelope with optional reported usage.</summary>
/// <param name="Model">The model that served the request.</param>
/// <param name="Answers">Answers keyed by the question identifiers.</param>
/// <param name="Usage">Reported token counts, when present.</param>
internal sealed record SystemOneResponse(string? Model, Dictionary<string, SystemOneAnswer?>? Answers, SystemOneUsage? Usage);

/// <summary>A wire Choice answer before the core validates it against its question.</summary>
/// <param name="Type">The answer discriminator.</param>
/// <param name="Choice">The reported option value.</param>
/// <param name="Probabilities">The reported distribution.</param>
/// <param name="Confidence">Reported confidence, when present.</param>
internal sealed record SystemOneAnswer(string? Type, string? Choice, Dictionary<string, double>? Probabilities, double? Confidence);

/// <summary>Token counts using the systemone wire names.</summary>
/// <param name="InputTokens">Reported input tokens.</param>
/// <param name="OutputTokens">Reported output tokens.</param>
internal sealed record SystemOneUsage([property: JsonPropertyName("input_tokens")] long? InputTokens,
                                      [property: JsonPropertyName("output_tokens")] long? OutputTokens);

/// <summary>The complete reflection-free systemone wire contract.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SystemOneRequest))]
[JsonSerializable(typeof(SystemOneQuestion))]
[JsonSerializable(typeof(SystemOneResponse))]
internal sealed partial class SystemOneJson : JsonSerializerContext;
