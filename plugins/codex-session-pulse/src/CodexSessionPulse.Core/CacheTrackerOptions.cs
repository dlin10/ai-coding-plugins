namespace CodexSessionPulse.Core;

/// <summary>The settings of a <see cref="CacheTracker"/> (spec §6.2, §6.3, §8.1).</summary>
/// <param name="EstimatedTtl">The assumed cache lifetime after a confirmed read; an estimate, not a guarantee.</param>
/// <param name="MinCachedTokens">The cached input a request must read to count as a confirmed cache hit.</param>
/// <param name="MinComparablePrefix">The smallest previous prefix (cached + write) the observed-miss heuristic compares against.</param>
public sealed record CacheTrackerOptions(TimeSpan EstimatedTtl, long MinCachedTokens, long MinComparablePrefix)
{
    /// <summary>The defaults: 30 minutes, 1024 tokens, 8192 tokens.</summary>
    public static CacheTrackerOptions Default { get; } = new(TimeSpan.FromMinutes(30), 1024, 8192);
}
