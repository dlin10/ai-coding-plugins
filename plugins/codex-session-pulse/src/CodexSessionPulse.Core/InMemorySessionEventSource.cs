using System.Threading.Channels;

namespace CodexSessionPulse.Core;

/// <summary>An <see cref="ISessionEventSource"/> fed by code: for tests and for the overlay's stub before integration.</summary>
public sealed class InMemorySessionEventSource : ISessionEventSource
{
    private readonly Channel<TokenUsageSnapshot> _channel =
        Channel.CreateUnbounded<TokenUsageSnapshot>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queues a usage event for the watcher.</summary>
    /// <param name="usage">The usage event.</param>
    /// <returns>Whether the event was queued; <see langword="false"/> after <see cref="Complete"/>.</returns>
    public bool Publish(TokenUsageSnapshot usage)
    {
        return _channel.Writer.TryWrite(usage);
    }

    /// <summary>Ends the stream after the queued events.</summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<TokenUsageSnapshot> WatchAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
