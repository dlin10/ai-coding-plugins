namespace CodexSessionPulse.Core;

/// <summary>The real clock: <see cref="TimeProvider.System"/> in UTC.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
