using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Accesses;

/// <summary>The identity of a candidate pair across two analyses of one scope (R5): the resource text a fingerprint hashes and the unordered
/// pair of its two accesses, each by its body id, its source span and its operation's wire name. It names neither an operation id nor an
/// execution, so a lowering that numbers its operations differently, or one site pair met in several executions, gives one identity.</summary>
internal static class PairIdentity
{
    /// <summary>The identity of a pair.</summary>
    /// <param name="pair">The pair, as pairing and the gap marks leave it.</param>
    internal static string Of(AccessPair pair)
    {
        var (low, high) = (Side(pair.First), Side(pair.Second));
        if (string.CompareOrdinal(low, high) > 0)
            (low, high) = (high, low);
        return $"{ConflictFindings.ResourceText(pair.Resource)}|{low}|{high}";
    }

    private static string Side(Access access) =>
        $"{access.BodyId}@{access.Source.Path}:{access.Source.StartLine}:{access.Source.StartColumn}-{access.Source.EndLine}:{access.Source.EndColumn}" +
        $"|{access.Operation.ToWireName()}";
}
