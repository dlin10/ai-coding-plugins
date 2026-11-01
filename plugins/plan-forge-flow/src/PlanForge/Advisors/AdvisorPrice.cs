namespace PlanForge.Advisors;

/// <summary>
/// Reported token totals of several Advisor replies. Input and output add up the counts that are known and stay unknown
/// until one is; a reply's cached and written counts are added only together with that same reply's input count, so the
/// totals never hold more cached plus written tokens than input.
/// </summary>
/// <param name="InputTokens">The known input tokens, or null when no reply reported any.</param>
/// <param name="OutputTokens">The known output tokens, or null when no reply reported any.</param>
/// <param name="CachedInputTokens">The cached part of the input, or null when no counted reply reported one.</param>
/// <param name="CacheWriteTokens">The cache-written part of the input, or null when no counted reply reported one.</param>
internal sealed record AdviceUsageTotals(long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? CacheWriteTokens)
{
    /// <summary>Totals of no reply: every count unknown.</summary>
    public static AdviceUsageTotals Empty { get; } = new(null, null, null, null);

    /// <summary>Adds one reply's usage; a reply without usage adds nothing.</summary>
    /// <param name="usage">The reply's reported usage, or null when it reported none.</param>
    public AdviceUsageTotals Add(AdviceUsage? usage) => usage is null ? this
        : Add(usage.InputTokens is null ? new AdviceUsageTotals(null, usage.OutputTokens, null, null)
              : new AdviceUsageTotals(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CacheWriteTokens));

    /// <summary>Adds other totals, each of the four counts the same way, known counts only.</summary>
    /// <param name="other">Totals already consistent under the same rule.</param>
    public AdviceUsageTotals Add(AdviceUsageTotals other) =>
        new(Sum(InputTokens, other.InputTokens), Sum(OutputTokens, other.OutputTokens),
            Sum(CachedInputTokens, other.CachedInputTokens), Sum(CacheWriteTokens, other.CacheWriteTokens));

    /// <summary>Adds two counts, staying unknown only when both are.</summary>
    /// <param name="left">One count, or null when unknown.</param>
    /// <param name="right">The other count, or null when unknown.</param>
    private static long? Sum(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);
}

/// <summary>The one pricing rule for Advisor tokens, reading every rate through IAdvisor.</summary>
internal static class AdvisorPrice
{
    /// <summary>
    /// Prices input tokens: the uncached rest at the uncached rate, cached tokens at the cache-read rate and written tokens
    /// at the cache-write rate, an unknown cache count taken as zero. Linear, so the price of consistent totals equals the
    /// sum of their replies' prices. Unrounded.
    /// </summary>
    /// <param name="advisor">The Advisor whose three input rates apply.</param>
    /// <param name="inputTokens">All input tokens, or null when unknown.</param>
    /// <param name="cachedTokens">The part of the input read from the cache, or null when unknown.</param>
    /// <param name="writtenTokens">The part of the input written to the cache, or null when unknown.</param>
    public static decimal? Usd(IAdvisor advisor, long? inputTokens, long? cachedTokens, long? writtenTokens)
    {
        if (inputTokens is not { } input) return null;
        var cached = cachedTokens ?? 0;
        var written = writtenTokens ?? 0;
        return ((input - cached - written) * advisor.InputUsdPerMillion + cached * advisor.CacheReadUsdPerMillion
                + written * advisor.CacheWriteUsdPerMillion) / 1_000_000m;
    }
}
