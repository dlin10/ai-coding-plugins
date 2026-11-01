using System.Net.Http.Headers;
using System.Text.Json;

namespace PlanForge.Advisors;

/// <summary>
/// The HTTP transport shared below IAdvisor by every Advisor that posts one JSON body per attempt. A subclass owns its wire
/// protocol: the body, its measurement and limits, parsing, credential, status meanings and any pacing; this class owns the
/// client, the deadline, retries and the mapping of transport failures. It is a helper, never a contract: the core sees
/// only IAdvisor.
/// </summary>
internal abstract class HttpAdvisor : IAdvisor, IDisposable
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
    protected HttpAdvisor(Uri endpoint, string model, HttpMessageHandler? handler,
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

    /// <summary>The service's published price in USD per million input tokens.</summary>
    public abstract decimal InputUsdPerMillion { get; }

    /// <summary>The service's published price in USD per million cache-read input tokens; the input price unless overridden.</summary>
    public virtual decimal CacheReadUsdPerMillion => InputUsdPerMillion;

    /// <summary>The service's published price in USD per million cache-written input tokens; the input price unless overridden.</summary>
    public virtual decimal CacheWriteUsdPerMillion => InputUsdPerMillion;

    /// <summary>The model requested on every attempt.</summary>
    public string RequestedModel { get; }

    /// <summary>The protocol's sizing, concurrency and timeout limits.</summary>
    public abstract AdvisorLimits Limits { get; }

    /// <summary>Measures the same serialized body sent by AskAsync, without contacting the service.</summary>
    /// <param name="request">The state and ordered questions to measure.</param>
    public abstract AdviceSize Measure(AdviceRequest request);

    /// <summary>Sends a Choice request, retrying only rate limiting and overload, at most twice.</summary>
    /// <param name="request">The object state and ordered questions to send.</param>
    /// <param name="ct">Caller cancellation, which is never converted to an Advisor failure.</param>
    public async Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(Limits.RequestTimeout, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var bytes = Serialize(request);
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
                var body = ReadsBody(status) ? await response.Content.ReadAsStringAsync(timeout.Token) : "";
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

    /// <summary>Serializes the exact request body sent on every attempt of one call.</summary>
    /// <param name="request">The service-independent request.</param>
    protected abstract byte[] Serialize(AdviceRequest request);

    /// <summary>Maps a 200 response, leaving per-question validity to the core.</summary>
    /// <param name="body">The response JSON, never retained by an exception.</param>
    protected abstract AdviceResult Parse(string body);

    /// <summary>Whether a status's content is read before it is parsed or classified.</summary>
    /// <param name="status">The HTTP status code.</param>
    protected abstract bool ReadsBody(int status);

    /// <summary>Puts the credential on one outgoing attempt; a value that cannot be a header throws FormatException.</summary>
    /// <param name="message">The attempt's request.</param>
    protected abstract void Authenticate(HttpRequestMessage message);

    /// <summary>Classifies a status other than 200 without retaining the response's content.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The content of a status the protocol reads, empty for any other, used only to recognize a refusal.</param>
    protected abstract AdvisorFailureKind Failure(int status, string body);

    /// <summary>Waits until the service's quota allows the next attempt; completes at once unless overridden.</summary>
    /// <param name="ct">The call's linked caller and timeout cancellation.</param>
    protected virtual Task PaceAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Sees every response before its status is handled; must not throw.</summary>
    /// <param name="response">The attempt's response.</param>
    protected virtual void Observe(HttpResponseMessage response) { }
}
