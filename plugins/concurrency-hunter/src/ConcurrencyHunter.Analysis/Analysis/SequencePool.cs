namespace ConcurrencyHunter.Analysis;

/// <summary>One copy of each distinct value and of each distinct sequence of values its owner creates. The accesses of a scope restate
/// the same steps, labels and checks in every execution that makes them, and without a pool each holds a list of its own (issue 454).
/// Values compare by their own equality and sequences item by item. A pool lives as long as its owner and is not thread-safe: it is
/// never shared between threads.</summary>
/// <typeparam name="T">The type of the values.</typeparam>
internal sealed class SequencePool<T> where T : notnull
{
    private readonly HashSet<T> _values = [];
    private readonly HashSet<T[]> _sequences = new(SequenceComparer.INSTANCE);

    /// <summary>The pool's copy of a sequence: the empty list for none, else one array of the pool's copies of its values, the first
    /// the pool met with these values in this order.</summary>
    /// <param name="values">The values, in order.</param>
    internal IReadOnlyList<T> Get(IEnumerable<T> values)
    {
        var sequence = values.Select(Value).ToArray();
        if (sequence.Length == 0)
            return [];
        if (_sequences.TryGetValue(sequence, out var pooled))
            return pooled;
        _sequences.Add(sequence);
        return sequence;
    }

    /// <summary>The pool's copy of a value: the value itself the first time the pool meets it, that first value after.</summary>
    /// <param name="value">The value.</param>
    private T Value(T value)
    {
        if (_values.TryGetValue(value, out var pooled))
            return pooled;
        _values.Add(value);
        return value;
    }

    /// <summary>Compares two sequences item by item.</summary>
    private sealed class SequenceComparer : IEqualityComparer<T[]>
    {
        internal static readonly SequenceComparer INSTANCE = new();

        public bool Equals(T[]? x, T[]? y) => ReferenceEquals(x, y) || x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(T[] obj)
        {
            var hash = new HashCode();
            // bounded: one access's steps, labels or checks, which Get has already walked.
            foreach (var item in obj)
                hash.Add(item);
            return hash.ToHashCode();
        }
    }
}
