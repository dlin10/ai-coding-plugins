using System.Collections.Concurrent;

namespace ConcurrencyHunter.Analysis;

/// <summary>One copy of each distinct value and of each distinct sequence of values its owner creates. The accesses of a scope restate
/// the same steps, labels and checks in every execution that makes them, and without a pool each holds a list of its own (issue 454).
/// Values compare by their own equality and sequences item by item. A pool lives as long as its owner. It is thread-safe: the
/// collectors of a scope walking on several workers share one (ADR 0040). Every caller keeps the copy the pool returns, and that is
/// what makes two equal sequences one object: the fold of ADR 0038 compares a code flow by reference.</summary>
/// <typeparam name="T">The type of the values.</typeparam>
internal sealed class SequencePool<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, T> _values = new();
    private readonly ConcurrentDictionary<T[], T[]> _sequences = new(SequenceComparer.INSTANCE);

    /// <summary>The pool's copy of a sequence: the empty list for none, else one array of the pool's copies of its values, the first
    /// the pool kept with these values in this order.</summary>
    /// <param name="values">The values, in order.</param>
    internal IReadOnlyList<T> Get(IEnumerable<T> values)
    {
        var sequence = values.Select(Value).ToArray();
        if (sequence.Length == 0)
            return [];
        return _sequences.TryGetValue(sequence, out var pooled) ? pooled : _sequences.GetOrAdd(sequence, sequence);
    }

    /// <summary>The pool's copy of a value: the value itself the first time the pool meets it, that first value after.</summary>
    /// <param name="value">The value.</param>
    private T Value(T value) => _values.TryGetValue(value, out var pooled) ? pooled : _values.GetOrAdd(value, value);

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
