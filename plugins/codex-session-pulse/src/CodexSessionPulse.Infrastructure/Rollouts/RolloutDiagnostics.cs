namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// Thread-safe counters of what the reader skipped or recovered from, keyed by
/// <see cref="CodexSessionPulse.Core.DiagnosticCodes"/>. Never holds rollout text, paths or identifiers.
/// </summary>
public sealed class RolloutDiagnostics
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <summary>Gets a copy of the counters.</summary>
    /// <returns>The count per diagnostic code.</returns>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, long>(_counts, StringComparer.Ordinal);
        }
    }

    /// <summary>Gets one counter.</summary>
    /// <param name="code">A diagnostic code.</param>
    /// <returns>The count, 0 when never seen.</returns>
    public long Count(string code)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(code);
        }
    }

    internal void Increment(string code)
    {
        lock (_gate)
        {
            _counts[code] = _counts.GetValueOrDefault(code) + 1;
        }
    }
}
