using System.Globalization;
using System.Net;

namespace PlanForge.Advisors.MicrosoftDecision;

/// <summary>
/// Spaces request starts to the deployment's request bucket, a mirror of the one the service reports on its responses:
/// <c>limit</c> requests, refilled over a renewal period, of which <c>remaining</c> are left. Until the first report it
/// assumes the smallest tier, one request a second with no burst, so a raised quota speeds it up without configuration.
/// </summary>
internal sealed class QuotaPacer
{
    /// <summary>The bucket size in requests.</summary>
    private const string LIMIT_HEADER = "x-ratelimit-limit-requests";

    /// <summary>The seconds in which an empty bucket refills.</summary>
    private const string PERIOD_HEADER = "x-ratelimit-renewalperiod-requests";

    /// <summary>The requests left when the service answered.</summary>
    private const string REMAINING_HEADER = "x-ratelimit-remaining-requests";

    /// <summary>The share of the reported refill rate spent, so that a bucket refilling in whole steps is never overtaken.</summary>
    private const double SAFETY = 0.9;

    /// <summary>The longest wait in seconds: far beyond any request's deadline, and within what Task.Delay accepts.</summary>
    private const double LONGEST_WAIT = 86_400;

    /// <summary>Guards the bucket estimate.</summary>
    private readonly object _gate = new();

    /// <summary>The monotonic clock the bucket refills by.</summary>
    private readonly TimeProvider _clock;

    /// <summary>The cancellable wait, replaceable by tests.</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>The most requests the bucket holds.</summary>
    private double _capacity = 1;

    /// <summary>Requests added per second.</summary>
    private double _rate = 1;

    /// <summary>The requests the service is believed to have left, net of starts already reserved; negative while callers queue.</summary>
    private double _tokens = 1;

    /// <summary>The timestamp of the last refill.</summary>
    private long _stamp;

    /// <summary>Whether a response has reported the bucket yet.</summary>
    private bool _known;

    /// <summary>Creates a pacer that knows nothing of the quota yet.</summary>
    /// <param name="clock">The monotonic clock, defaulting to the system clock.</param>
    /// <param name="delay">The cancellable wait, defaulting to Task.Delay.</param>
    public QuotaPacer(TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _stamp = _clock.GetTimestamp();
    }

    /// <summary>Reserves the next start and waits for it; completes at once when a request is due now.</summary>
    /// <param name="ct">Cancellation, which gives the reserved start back.</param>
    public Task WaitAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            Refill();
            _tokens -= 1;
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(-_tokens / (_rate * SAFETY), LONGEST_WAIT));
        }
        return wait == TimeSpan.Zero ? Task.CompletedTask : WaitOrReturnAsync(wait, ct);
    }

    /// <summary>Adopts the bucket a response reports; a report never raises the estimate, and a 429 empties it.</summary>
    /// <param name="response">Any response from the service, with or without rate-limit headers.</param>
    public void Observe(HttpResponseMessage response)
    {
        var limit = Count(response, LIMIT_HEADER);
        var period = Count(response, PERIOD_HEADER);
        var remaining = Count(response, REMAINING_HEADER);
        lock (_gate)
        {
            Refill();
            if (response.StatusCode == HttpStatusCode.TooManyRequests) _tokens = Math.Min(_tokens, 0);
            if (limit is not { } size || size == 0 || period is not { } seconds || seconds == 0
                || remaining is not { } left || left > size) return;
            _capacity = size;
            _rate = (double)size / seconds;
            // The first report replaces the cold guess, keeping the starts already reserved against it.
            _tokens = _known ? Math.Min(_tokens, left) : left + Math.Min(_tokens, 0);
            _known = true;
        }
    }

    /// <summary>Waits for a reserved start, returning it to the bucket if the wait is cancelled.</summary>
    /// <param name="wait">The time until the reserved start.</param>
    /// <param name="ct">The caller's cancellation.</param>
    private async Task WaitOrReturnAsync(TimeSpan wait, CancellationToken ct)
    {
        try { await _delay(wait, ct); }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                Refill();
                _tokens = Math.Min(_capacity, _tokens + 1);
            }
            throw;
        }
    }

    /// <summary>Adds the requests the service has refilled since the last refill, up to the capacity.</summary>
    private void Refill()
    {
        var now = _clock.GetTimestamp();
        _tokens = Math.Min(_capacity, _tokens + _clock.GetElapsedTime(_stamp, now).TotalSeconds * _rate * SAFETY);
        _stamp = now;
    }

    /// <summary>Reads one non-negative integer header, or null when it is absent or not a plain integer.</summary>
    /// <param name="response">The response carrying the header.</param>
    /// <param name="name">The header name.</param>
    private static int? Count(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
}
