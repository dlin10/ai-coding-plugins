using CacheDetective.Graph;

namespace CacheDetective.Rules;

public sealed record OrphanInvalidationFinding(Invalidates Invalidation)
{
    public const string Rule = "ORPHAN_INVALIDATION";
}

public sealed record PatternMismatchFinding(Invalidates Invalidation, CacheKey CachedKey, int Distance)
{
    public const string Rule = "PATTERN_MISMATCH";
}

public sealed record InvalidationRuleResult(IReadOnlyList<OrphanInvalidationFinding> Orphans,
                                            IReadOnlyList<PatternMismatchFinding> PatternMismatches);

public sealed class OrphanInvalidationRule
{
    public InvalidationRuleResult Evaluate(CacheGraph graph)
    {
        var edges = graph.Edges.ToArray();
        var cachedKeys = edges.OfType<Caches>()
            .Select(edge => (CacheKey)edge.To)
            .GroupBy(key => (key.Template, key.Store))
            .Select(group => group.First())
            .ToArray();
        var orphans = edges.OfType<Invalidates>()
            .Where(invalidation => !cachedKeys.Any(key =>
                CacheKeyCovering.Covers(invalidation, key, key.TagsAny)))
            .Select(invalidation => new OrphanInvalidationFinding(invalidation))
            .ToArray();
        var mismatches = new PatternMismatchRule().Refine(orphans, cachedKeys);
        return new InvalidationRuleResult(orphans, mismatches);
    }
}

public sealed class PatternMismatchRule
{
    public IReadOnlyList<PatternMismatchFinding> Refine(
        IEnumerable<OrphanInvalidationFinding> orphans, IEnumerable<CacheKey> cachedKeys)
    {
        var keys = cachedKeys.ToArray();
        var findings = new List<PatternMismatchFinding>();

        foreach (var orphan in orphans)
        {
            var invalidationKey = (CacheKey)orphan.Invalidation.To;
            var invalidationSkeleton = CacheKeyCovering.GetSkeleton(invalidationKey.Template);
            var candidate = keys.Where(key => key.Store == invalidationKey.Store)
                .Select(key => new Candidate(key,
                    LevenshteinDistance(invalidationSkeleton,
                        CacheKeyCovering.GetSkeleton(key.Template))))
                .Where(item => item.Distance is 1 or 2)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Key.Template, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is not null)
            {
                findings.Add(new PatternMismatchFinding(orphan.Invalidation, candidate.Key,
                    candidate.Distance));
            }
        }

        return findings;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] +
                                   (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record Candidate(CacheKey Key, int Distance);
}
