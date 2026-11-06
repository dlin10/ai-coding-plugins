namespace CodexSessionPulse.Core;

/// <summary>
/// The cache state machine of one thread (spec §6). Not thread-safe: the caller serializes calls, as
/// <see cref="PulseEngine"/> does. It never formats strings and never moves the TTL anchor on a tick.
/// </summary>
/// <remarks>
/// <para>The anchor is the <see cref="TokenUsageSnapshot.ObservedAtUtc"/> of the last confirmed read
/// (<c>cached &gt;= MinCachedTokens</c>). That is the record's write time after the request completed, so the
/// remaining time is an upper-bound style estimate (§6.2) and the basis is <see cref="TtlBasis.Assumed"/>.</para>
/// <para>A request that reads less than <see cref="CacheTrackerOptions.MinCachedTokens"/> confirms nothing and
/// is not a miss: the frozen <see cref="CacheState"/> has no <c>Unknown/LowReuse</c> value, so the snapshot is
/// <see cref="CacheState.WaitingForUsage"/> with the numbers filled in and <see cref="TtlBasis.Unknown"/>.</para>
/// </remarks>
public sealed class CacheTracker : ICacheTracker
{
    private const double ExpiringSoonShare = 0.25;
    private const double LowReadShare = 0.25;
    private const double ReplacementWriteShare = 0.50;

    private readonly CacheTrackerOptions _options;
    private TokenUsageSnapshot? _last;
    private TokenUsageSnapshot? _reference;
    private DateTimeOffset? _anchorUtc;
    private bool _observedMiss;

    /// <summary>Creates a tracker with no observations.</summary>
    /// <param name="options">The TTL and threshold settings.</param>
    public CacheTracker(CacheTrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the last applied usage, or <see langword="null"/> when none was applied.</summary>
    public TokenUsageSnapshot? LastUsage => _last;

    /// <inheritdoc/>
    /// <remarks>
    /// An invalid event (negative counters, cached or write above input), an event older than the last applied
    /// one, or an exact replay of it leaves the state unchanged, so a replay never extends the timer (§6.1).
    /// </remarks>
    public CacheSnapshot Apply(TokenUsageSnapshot usage)
    {
        if (IsValid(usage) && IsNew(usage))
        {
            if (_last is { } previous && IsModelChange(previous.Model, usage.Model))
            {
                _reference = null;
            }

            _observedMiss = IsObservedMiss(usage);
            bool confirmedRead = usage.CachedInputTokens >= _options.MinCachedTokens;
            _anchorUtc = confirmedRead ? usage.ObservedAtUtc : null;

            if ((confirmedRead || _observedMiss) && usage.Model is not null)
            {
                _reference = usage;
            }

            _last = usage;
        }

        return Tick(usage.ObservedAtUtc);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The elapsed time is clamped at zero, so a clock moved back never yields more than the full estimate, and a
    /// clock moved forward (sleep, resume) only expires the estimate.
    /// </remarks>
    public CacheSnapshot Tick(DateTimeOffset nowUtc)
    {
        TimeSpan estimate = _options.EstimatedTtl;

        if (_last is not { } last)
        {
            return new CacheSnapshot(nowUtc, CacheState.WaitingForUsage, null, ThreadKind.Unknown,
                                     new ThreadSelection(SelectionConfidence.Unknown, null), null, null, null, null,
                                     new CacheTtl(estimate, null, TtlBasis.Unknown), null,
                                     new SourceInfo(SourceStatus.Available, null, null), null, null, null);
        }

        CacheState state;
        CacheTtl ttl;

        if (_observedMiss)
        {
            state = CacheState.ObservedMiss;
            ttl = new CacheTtl(estimate, null, TtlBasis.Unknown);
        }
        else if (_anchorUtc is { } anchor)
        {
            TimeSpan elapsed = nowUtc - anchor;
            TimeSpan remaining = estimate - (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);

            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            state = remaining == TimeSpan.Zero ? CacheState.EstimatedExpired
                  : remaining <= estimate * ExpiringSoonShare ? CacheState.ExpiringSoon
                  : CacheState.Warm;
            ttl = new CacheTtl(estimate, remaining, TtlBasis.Assumed);
        }
        else
        {
            state = CacheState.WaitingForUsage;
            ttl = new CacheTtl(estimate, null, TtlBasis.Unknown);
        }

        double? ratio = last.InputTokens > 0 ? (double)last.CachedInputTokens / last.InputTokens : null;

        return new CacheSnapshot(nowUtc, state, last.ThreadId, last.Kind,
                                 new ThreadSelection(SelectionConfidence.Unknown, null), last.ObservedAtUtc,
                                 last.CachedInputTokens, last.InputTokens, ratio, ttl, null,
                                 new SourceInfo(SourceStatus.Available, last.SourcePath, last.CliVersion), null,
                                 last.Model, last.CacheWriteInputTokens);
    }

    private static bool IsValid(TokenUsageSnapshot usage)
    {
        return usage.InputTokens >= 0
            && usage.CachedInputTokens >= 0
            && usage.OutputTokens >= 0
            && usage.CachedInputTokens <= usage.InputTokens
            && usage.CacheWriteInputTokens is not < 0
            && (usage.CacheWriteInputTokens ?? 0) <= usage.InputTokens;
    }

    private static bool IsModelChange(string? previous, string? current)
    {
        return previous is not null && current is not null && !string.Equals(previous, current, StringComparison.Ordinal);
    }

    private bool IsNew(TokenUsageSnapshot usage)
    {
        if (_last is not { } last)
        {
            return true;
        }

        if (usage.ObservedAtUtc != last.ObservedAtUtc)
        {
            return usage.ObservedAtUtc > last.ObservedAtUtc;
        }

        return usage.InputTokens != last.InputTokens
            || usage.CachedInputTokens != last.CachedInputTokens
            || usage.CacheWriteInputTokens != last.CacheWriteInputTokens
            || usage.OutputTokens != last.OutputTokens;
    }

    /// <summary>The §6.3 heuristic: never on an unknown or changed model, and never without a positive write.</summary>
    private bool IsObservedMiss(TokenUsageSnapshot current)
    {
        if (_reference is not { } previous
         || current.Model is null
         || !string.Equals(previous.Model, current.Model, StringComparison.Ordinal)
         || current.CacheWriteInputTokens is not > 0)
        {
            return false;
        }

        long write = current.CacheWriteInputTokens.Value;
        long previousPrefix = previous.CachedInputTokens + (previous.CacheWriteInputTokens ?? 0);

        return previousPrefix >= _options.MinComparablePrefix
            && current.CachedInputTokens < previousPrefix * LowReadShare
            && write >= previousPrefix * ReplacementWriteShare;
    }
}
