namespace CodexSessionPulse.Core;

/// <summary>
/// One model request of a thread: its output tokens and how long it took, from sending the request to the end of the
/// answer. The span includes the wait for the first token, since the rollout records no first-token time (spec §6.4).
/// </summary>
/// <param name="ThreadId">The thread the request belongs to.</param>
/// <param name="CompletedAtUtc">When the answer ended, in UTC.</param>
/// <param name="OutputTokens">The output tokens of the request, reasoning included.</param>
/// <param name="Duration">From sending the request to the end of the answer.</param>
public readonly record struct RequestTiming(string ThreadId, DateTimeOffset CompletedAtUtc, long OutputTokens, TimeSpan Duration);

/// <summary>
/// The output speed of one thread (spec §6.4): Σ output tokens / Σ request time over its last <see cref="Window"/>
/// requests, never an average of per-request speeds, so a short request does not weigh as much as a long one.
/// </summary>
public sealed class SpeedTracker
{
    /// <summary>How many of the latest requests make up the speed.</summary>
    public const int Window = 10;

    private readonly Queue<RequestTiming> _requests = new();

    /// <summary>Gets the output tokens per second over the window, or <see langword="null"/> before the first request.</summary>
    public double? TokensPerSecond { get; private set; }

    /// <summary>
    /// Adds a request. One without output or with no time, and one that ended before the newest already counted (an
    /// older segment read late), is ignored.
    /// </summary>
    /// <param name="timing">The request.</param>
    public void Apply(RequestTiming timing)
    {
        if (timing.OutputTokens <= 0 || timing.Duration <= TimeSpan.Zero
            || (_requests.Count > 0 && timing.CompletedAtUtc <= _requests.Last().CompletedAtUtc))
        {
            return;
        }

        _requests.Enqueue(timing);

        if (_requests.Count > Window)
        {
            _requests.Dequeue();
        }

        TokensPerSecond = _requests.Sum(r => r.OutputTokens) / _requests.Sum(r => r.Duration.TotalSeconds);
    }
}
