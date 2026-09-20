using System.Globalization;

namespace ConcurrencyHunter.Analysis;

/// <summary>How a predicate constrains its subject (TD-090). <see cref="Unsupported"/> is a predicate the analysis does not
/// read: it constrains nothing and is reported as an uncertainty instead (TD-095).</summary>
public enum PathRelation
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Null,
    NotNull,
    Type,
    NotType,
    Unsupported
}

/// <summary>
/// One bounded predicate that holds wherever an access runs. <see cref="Subject"/> is the identity of the value it constrains:
/// the canonical identity of a value that is one and the same in every execution, or an identity of one execution's own, which
/// two executions never share. Two predicates are compared only under one subject, so a value that is not proven canonical can
/// never take a pair away.
/// </summary>
public sealed record PathPredicate(string Subject, PathRelation Relation, string? Value, string Text)
{
    /// <summary>The mark a canonical subject carries, which is what tells a value every execution reads alike from one of a
    /// single execution's own.</summary>
    public const string CANONICAL = "canon|";

    public bool IsSupported => Relation != PathRelation.Unsupported;

    /// <summary>The width in bits of the subject's own type and whether that type is signed, so that a guard is decided as the
    /// language runs it: a range over a <c>long</c> read in 32 bits turns satisfiable bounds into an empty interval and takes a
    /// real conflict away (TD-094).</summary>
    public int Width { get; init; } = 32;

    public bool Signed { get; init; } = true;

    /// <summary>Whether this predicate's subject means the same value in every execution. A pair is two executions that may be
    /// under way at once, and a value neither of them proved canonical is the reading execution's own: two runs of one body read
    /// one mutable field into two values, and the branch each took on its own value says nothing about the other's. Only a
    /// canonical subject may be compared across the two sides of a pair (TD-092).</summary>
    public bool IsCanonical => Subject.StartsWith(CANONICAL, StringComparison.Ordinal);
}

/// <summary>
/// What a path's predicates decide about another path, with no solver: constants, ranges and negations of one subject
/// (TD-090, TD-092). Anything else leaves the pair alone, because a wrong answer here hides a real race.
/// </summary>
public static class PathConditions
{
    /// <summary>The uncertainty a guard the analysis does not read puts on a finding (TD-095).</summary>
    public static string UnsupportedUncertainty(string predicate) =>
        $"A guard the analysis does not support was abstracted away: {predicate}.";

    /// <summary>Whether the two paths cannot both be taken: one predicate of each, over one canonical subject, that contradict.
    /// The two sides are two executions, so only a subject proven to be one value in every execution is one value here; anything
    /// else each side holds its own copy of, and no branch either took rules the other out (TD-092).</summary>
    public static bool Contradict(IReadOnlyList<PathPredicate> first, IReadOnlyList<PathPredicate> second) =>
        first.Any(one => second.Any(other => Contradict(one, other)));

    private static bool Contradict(PathPredicate one, PathPredicate other)
    {
        if (!one.IsSupported || !other.IsSupported || !one.IsCanonical || !other.IsCanonical ||
            !string.Equals(one.Subject, other.Subject, StringComparison.Ordinal))
        {
            return false;
        }

        return (one.Relation, other.Relation) switch
        {
            (PathRelation.Null, PathRelation.NotNull) or (PathRelation.NotNull, PathRelation.Null) => true,
            (PathRelation.Type, PathRelation.NotType) or (PathRelation.NotType, PathRelation.Type) => Same(one, other),
            (PathRelation.Equal, PathRelation.NotEqual) or (PathRelation.NotEqual, PathRelation.Equal) => Same(one, other),
            (PathRelation.Equal, PathRelation.Equal) when !Same(one, other) => true,
            _ => Disjoint(Range(one), Range(other))
        };
    }

    private static bool Same(PathPredicate one, PathPredicate other) =>
        string.Equals(one.Value, other.Value, StringComparison.Ordinal);

    /// <summary>The numbers a predicate leaves its subject, when it is a bound on a number at all.</summary>
    private static (long Low, long High)? Range(PathPredicate predicate)
    {
        if (predicate.Value is not { } text || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return null;

        return predicate.Relation switch
        {
            PathRelation.Equal => (value, value),
            PathRelation.Less when value > long.MinValue => (long.MinValue, value - 1),
            PathRelation.LessOrEqual => (long.MinValue, value),
            PathRelation.Greater when value < long.MaxValue => (value + 1, long.MaxValue),
            PathRelation.GreaterOrEqual => (value, long.MaxValue),
            _ => null
        };
    }

    private static bool Disjoint((long Low, long High)? first, (long Low, long High)? second) =>
        first is { } one && second is { } other && (one.High < other.Low || other.High < one.Low);
}
