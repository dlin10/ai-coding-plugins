using CacheDetective.Caching;
using CacheDetective.Graph;

namespace CacheDetective.Rules;

/// <summary>
/// The removals a reader will find when they go looking, and a finding must not pretend it did not see. A
/// may-edge covers its key by every rule the matching uses; what it lacks is the certainty that it removes
/// <em>this</em> value rather than another the same site may produce, so it grants no suppression. Omitting
/// it would send the reader to code that plainly removes the key and leave them to conclude the tool is
/// wrong — worse than a finding that shows the removal and says why it was not enough.
/// <para>Shared by both rules that read coverage, so that a reader meets the same explanation whichever
/// finding shows it to them. See <c>docs/adr/0015</c>.</para>
/// </summary>
internal static class PossibleInvalidations
{
    /// <summary>What a may-edge means to a reader of a finding, where the fold that made it a may did not
    /// say more. An edge that carries its own reason keeps it.</summary>
    private const string REASON =
        "This removal may fire with this key, but the site is not certain to remove this value rather than " +
        "another it may produce, so it does not count as coverage.";

    /// <summary>Every may-removal that covers <paramref name="key"/> from a handler the invalidation search
    /// reached, each carrying the reason it did not count.</summary>
    internal static IReadOnlyList<Invalidates> Covering(CacheKey key,
                                                        IReadOnlyDictionary<(string Solution, string Symbol), ReachedHandler> reachableHandlers,
                                                        IEnumerable<GraphEdge> edges) =>
        [.. edges.OfType<Invalidates>()
                 .Where(invalidation => invalidation.Modality == InvalidationModality.May &&
                                        reachableHandlers.ContainsKey(Identify((Handler)invalidation.From)) &&
                                        CacheKeyCovering.Covers(invalidation, key, key.TagsAll))
                 .Select(invalidation => invalidation.Reason is null ? invalidation with { Reason = REASON } : invalidation)];

    private static (string Solution, string Symbol) Identify(Handler handler) => (handler.Solution, handler.Symbol);
}
