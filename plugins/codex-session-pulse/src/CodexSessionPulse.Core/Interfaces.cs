namespace CodexSessionPulse.Core;

/// <summary>A source of token-usage events; it knows nothing about WPF.</summary>
public interface ISessionEventSource : IAsyncDisposable
{
    /// <summary>Watches the source and yields each token-usage event as it is observed.</summary>
    /// <param name="ct">Stops the watch.</param>
    /// <returns>The stream of observed usage events.</returns>
    IAsyncEnumerable<TokenUsageSnapshot> WatchAsync(CancellationToken ct);
}

/// <summary>A per-thread cache state machine; it knows nothing about file locations.</summary>
public interface ICacheTracker
{
    /// <summary>Folds an observed usage event into the state.</summary>
    /// <param name="usage">The observed usage.</param>
    /// <returns>The snapshot after the event.</returns>
    CacheSnapshot Apply(TokenUsageSnapshot usage);

    /// <summary>Advances the state to a point in time without a new observation.</summary>
    /// <param name="nowUtc">The current time, in UTC.</param>
    /// <returns>The snapshot at that time.</returns>
    CacheSnapshot Tick(DateTimeOffset nowUtc);
}

/// <summary>Tracks the Codex Desktop window the overlay is anchored to.</summary>
public interface IWindowAnchor : IAsyncDisposable
{
    /// <summary>Gets the stream of anchor snapshots.</summary>
    IObservable<AnchorSnapshot> Snapshots { get; }

    /// <summary>Attaches the anchor to a window.</summary>
    /// <param name="hwnd">The handle of the window to follow.</param>
    void Attach(nint hwnd);
}

/// <summary>A source of the current time, so that state machines can be tested.</summary>
public interface IClock
{
    /// <summary>Gets the current time, in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
