using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Accesses;

/// <summary>
/// Candidate pairs of accesses to one resource in one scope that may run at the same time on the same object: never
/// read/read, never across different sharing keys or an <c>invocation</c> key, and a root with itself only when the
/// root overlaps itself and the object is not a transient of one hosted-service instance. A pair whose sides hold a
/// common single-object lock is suppressed.
/// </summary>
public static class AccessPairing
{
    public const string SKIP_READ_READ = "read-read";
    public const string SKIP_INVOCATION = "invocation";
    public const string SKIP_DIFFERENT_SHARING = "different-sharing";
    public const string SKIP_NO_SELF_OVERLAP = "no-self-overlap";

    public static PairAnalysis Pair(IEnumerable<Access> accesses)
    {
        var pairs = new List<AccessPair>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var candidates = 0;
        var suppressed = 0;
        foreach (var resource in accesses.GroupBy(access => access.Resource.Identity, StringComparer.Ordinal)
                                         .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var group = resource.ToArray();
            for (var first = 0; first < group.Length; first++)
            {
                for (var second = first; second < group.Length; second++)
                {
                    var (left, right) = (group[first], group[second]);
                    candidates++;
                    var skip = SkipReason(left, right);
                    if (skip is not null)
                    {
                        skips[skip] = skips.TryGetValue(skip, out var count) ? count + 1 : 1;
                        continue;
                    }

                    if (left.HeldProtectionIds.Intersect(right.HeldProtectionIds, StringComparer.Ordinal).Any())
                    {
                        suppressed++;
                        continue;
                    }

                    pairs.Add(new AccessPair(left, right, Protection(left, right)));
                }
            }
        }

        return new PairAnalysis(pairs, candidates, suppressed, skips);
    }

    private static string? SkipReason(Access left, Access right)
    {
        if (left.Operation == AccessOperation.Read && right.Operation == AccessOperation.Read)
            return SKIP_READ_READ;
        if (left.SharingKey == SharingKeys.INVOCATION || right.SharingKey == SharingKeys.INVOCATION)
            return SKIP_INVOCATION;
        if (!string.Equals(left.SharingKey, right.SharingKey, StringComparison.Ordinal))
            return SKIP_DIFFERENT_SHARING;
        if (left.Root.RootId == right.Root.RootId &&
            (!(left.Root.OverlapsItself && right.Root.OverlapsItself) ||
             left.SharingKey.StartsWith(SharingKeys.HOSTED_INSTANCE + ":", StringComparison.Ordinal)))
        {
            return SKIP_NO_SELF_OVERLAP;
        }
        return null;
    }

    private static string Protection(Access left, Access right) =>
        (left.HeldProtection.Count != 0, right.HeldProtection.Count != 0) switch
        {
            (false, false) => PairProtection.UNPROTECTED,
            (true, true) => PairProtection.DIFFERENT_IDENTITY,
            _ => PairProtection.PARTIAL
        };
}
