namespace CodexSessionPulse.Core;

/// <summary>What the hooks have delivered so far, for diagnostics (spec §8.2: "hooks are not observed").</summary>
/// <param name="CountsByHookType">The number of hooks received per <c>hook_event_name</c>.</param>
/// <param name="LastHookAtUtc">When the last hook arrived, in UTC, or <see langword="null"/> when none has.</param>
/// <param name="LastSource">The <c>hook_event_name</c> of the last hook, or <see langword="null"/> when none has arrived.</param>
public sealed record HookActivitySnapshot(IReadOnlyDictionary<string, int> CountsByHookType,
                                          DateTimeOffset? LastHookAtUtc,
                                          string? LastSource);

/// <summary>Counts hooks per type in a thread-safe way; #252 feeds it, the overlay diagnostics read it.</summary>
public sealed class HookActivityRecorder
{
    private const int MaxHookTypes = 32;
    private const string OtherHookType = "other";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastHookAtUtc;
    private string? _lastSource;

    /// <summary>Records one received hook.</summary>
    /// <param name="hookType">The <c>hook_event_name</c>; past 32 distinct names the rest count as "other".</param>
    /// <param name="atUtc">When the hook arrived, in UTC.</param>
    public void Record(string hookType, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(hookType);

        lock (_gate)
        {
            string key = _counts.ContainsKey(hookType) || _counts.Count < MaxHookTypes ? hookType : OtherHookType;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;

            if (_lastHookAtUtc is null || atUtc >= _lastHookAtUtc)
            {
                _lastHookAtUtc = atUtc;
                _lastSource = key;
            }
        }
    }

    /// <summary>Gets an immutable copy of the counts.</summary>
    /// <returns>The snapshot.</returns>
    public HookActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HookActivitySnapshot(new Dictionary<string, int>(_counts, StringComparer.Ordinal),
                                            _lastHookAtUtc,
                                            _lastSource);
        }
    }
}
