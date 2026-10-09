using System.Collections;
using System.Numerics;

namespace ConcurrencyHunter.Execution;

/// <summary>A set of executions of one analysis, as bits over the numbers the analysis gave its execution ids. It enumerates its ids in
/// ordinal order. An analysis gives equal sets one instance, so the accesses of one node share one set (ADR 0017).</summary>
public sealed class ExecutionSet : IReadOnlySet<string>
{
    private readonly ExecutionIds _ids;
    private readonly ulong[] _bits;
    private string[]? _ordered;

    /// <summary>A set over the ids of an analysis; the bits are the set's own and never change.</summary>
    /// <param name="ids">The numbering of the analysis's execution ids.</param>
    /// <param name="bits">The numbers of the executions in the set, as bits.</param>
    internal ExecutionSet(ExecutionIds ids, ulong[] bits)
    {
        _ids = ids;
        _bits = bits;
        Count = bits.Sum(BitOperations.PopCount);
    }

    public int Count { get; }

    /// <summary>The set's bits, which the caller must not change.</summary>
    internal ulong[] Words => _bits;

    public bool Contains(string item) => _ids.TryGetNumber(item, out var number) && Bits.Contains(_bits, number);

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Ordered()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<string> other) => Copy().IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<string> other) => Copy().IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<string> other) => Copy().IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<string> other) => Copy().IsSupersetOf(other);

    public bool Overlaps(IEnumerable<string> other) => other.Any(Contains);

    public bool SetEquals(IEnumerable<string> other) => Copy().SetEquals(other);

    public override string ToString() => $"[{string.Join(", ", this)}]";

    private string[] Ordered() => _ordered ??= Bits.Numbers(_bits).Select(_ids.Id).Order(StringComparer.Ordinal).ToArray();

    private HashSet<string> Copy() => new(this, StringComparer.Ordinal);
}

/// <summary>The numbers an analysis gives its execution ids, in the order it first meets them; a number never changes.</summary>
internal sealed class ExecutionIds
{
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private readonly List<string> _ids = [];
    private readonly Dictionary<ulong[], ExecutionSet> _sets = new(BitsComparer.INSTANCE);

    /// <summary>The number of an execution id, given the first time it is asked for.</summary>
    /// <param name="id">The execution id.</param>
    internal int Number(string id)
    {
        if (!_numbers.TryGetValue(id, out var number))
        {
            number = _ids.Count;
            _numbers.Add(id, number);
            _ids.Add(id);
        }

        return number;
    }

    internal bool TryGetNumber(string id, out int number) => _numbers.TryGetValue(id, out number);

    internal string Id(int number) => _ids[number];

    /// <summary>The one set of the analysis with these executions; the bits are copied, so the caller may keep changing its own.</summary>
    /// <param name="bits">The numbers of the executions, as bits.</param>
    internal ExecutionSet Set(ulong[] bits)
    {
        if (!_sets.TryGetValue(bits, out var set))
        {
            var copy = (ulong[])bits.Clone();
            _sets.Add(copy, set = new ExecutionSet(this, copy));
        }

        return set;
    }

    private sealed class BitsComparer : IEqualityComparer<ulong[]>
    {
        internal static readonly BitsComparer INSTANCE = new();

        public bool Equals(ulong[]? x, ulong[]? y)
        {
            if (x is null || y is null)
                return ReferenceEquals(x, y);
            for (var index = 0; index < Math.Max(x.Length, y.Length); index++)
            {
                if ((index < x.Length ? x[index] : 0) != (index < y.Length ? y[index] : 0))
                    return false;
            }

            return true;
        }

        public int GetHashCode(ulong[] bits)
        {
            var hash = new HashCode();
            var length = bits.Length;
            while (length > 0 && bits[length - 1] == 0)
                length--;
            for (var index = 0; index < length; index++)
                hash.Add(bits[index]);
            return hash.ToHashCode();
        }
    }
}

/// <summary>Sets of small numbers as bits, a word past the end reading as zero.</summary>
internal static class Bits
{
    internal static readonly ulong[] EMPTY = [];

    internal static ulong[] Of(IEnumerable<int> numbers)
    {
        var set = EMPTY;
        foreach (var number in numbers)
            Add(ref set, number);
        return set;
    }

    internal static bool Contains(ulong[] set, int number) => number >> 6 < set.Length && (set[number >> 6] & (1UL << (number & 63))) != 0;

    internal static bool IsEmpty(ulong[] set) => Array.TrueForAll(set, word => word == 0);

    internal static IEnumerable<int> Numbers(ulong[] set)
    {
        for (var index = 0; index < set.Length; index++)
        {
            for (var word = set[index]; word != 0; word &= word - 1)
                yield return (index << 6) + BitOperations.TrailingZeroCount(word);
        }
    }

    /// <summary>Adds a number to a set, growing it when it is too short.</summary>
    /// <param name="set">The set, changed in place.</param>
    /// <param name="number">The number.</param>
    /// <returns>Whether the number was not in the set.</returns>
    internal static bool Add(ref ulong[] set, int number)
    {
        if (number >> 6 >= set.Length)
            Array.Resize(ref set, (number >> 6) + 1);
        var bit = 1UL << (number & 63);
        if ((set[number >> 6] & bit) != 0)
            return false;
        set[number >> 6] |= bit;
        return true;
    }

    /// <summary>Adds every number of a source to a set, and the numbers that were new to a second set.</summary>
    /// <param name="set">The set, changed in place.</param>
    /// <param name="source">The numbers to add.</param>
    /// <param name="added">Receives the numbers that were not in the set.</param>
    /// <returns>Whether any number was new.</returns>
    internal static bool UnionWith(ref ulong[] set, ulong[] source, ref ulong[] added)
    {
        var changed = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = index < set.Length ? set[index] : 0;
            var fresh = source[index] & ~current;
            if (fresh == 0)
                continue;
            if (index >= set.Length)
                Array.Resize(ref set, source.Length);
            if (index >= added.Length)
                Array.Resize(ref added, source.Length);
            set[index] |= fresh;
            added[index] |= fresh;
            changed = true;
        }

        return changed;
    }

    /// <summary>Adds to a set every number of a source that a mask also holds.</summary>
    /// <param name="set">The set, changed in place.</param>
    /// <param name="source">The numbers to add.</param>
    /// <param name="mask">The numbers that may be added.</param>
    /// <returns>Whether any number was new.</returns>
    internal static bool UnionWith(ref ulong[] set, ulong[] source, ulong[] mask)
    {
        var changed = false;
        for (var index = 0; index < Math.Min(source.Length, mask.Length); index++)
        {
            var fresh = source[index] & mask[index] & ~(index < set.Length ? set[index] : 0);
            if (fresh == 0)
                continue;
            if (index >= set.Length)
                Array.Resize(ref set, Math.Min(source.Length, mask.Length));
            set[index] |= fresh;
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether a set holds every number of another.</summary>
    /// <param name="set">The set.</param>
    /// <param name="subset">The numbers asked about.</param>
    internal static bool Covers(ulong[] set, ulong[] subset)
    {
        for (var index = 0; index < subset.Length; index++)
        {
            if ((subset[index] & ~(index < set.Length ? set[index] : 0)) != 0)
                return false;
        }

        return true;
    }

    /// <summary>Whether two sets hold the same numbers.</summary>
    /// <param name="first">The first set.</param>
    /// <param name="second">The second set.</param>
    internal static bool SameAs(ulong[] first, ulong[] second) => Covers(first, second) && Covers(second, first);

    /// <summary>Adds every number of a source to a set.</summary>
    /// <param name="set">The set, changed in place.</param>
    /// <param name="source">The numbers to add.</param>
    /// <returns>Whether any number was new.</returns>
    internal static bool UnionWith(ref ulong[] set, ulong[] source)
    {
        var changed = false;
        for (var index = 0; index < source.Length; index++)
        {
            if ((source[index] & ~(index < set.Length ? set[index] : 0)) == 0)
                continue;
            if (index >= set.Length)
                Array.Resize(ref set, source.Length);
            set[index] |= source[index];
            changed = true;
        }

        return changed;
    }
}

/// <summary>A list per execution, worked out the first time an execution is asked for: what one execution's visits or steps are,
/// read out of a walk graph the executions share.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="keys">The executions that have a list, in ordinal order.</param>
/// <param name="list">Works out the list of one execution.</param>
internal sealed class ExecutionLists<T>(IReadOnlyList<string> keys, Func<string, IReadOnlyList<T>> list) : IReadOnlyDictionary<string, IReadOnlyList<T>>
{
    private readonly HashSet<string> _keys = keys.ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<T>> _lists = new(StringComparer.Ordinal);

    public IReadOnlyList<T> this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => keys;

    public IEnumerable<IReadOnlyList<T>> Values => keys.Select(key => this[key]);

    public int Count => keys.Count;

    public bool ContainsKey(string key) => _keys.Contains(key);

    public bool TryGetValue(string key, out IReadOnlyList<T> value)
    {
        if (!_keys.Contains(key))
        {
            value = [];
            return false;
        }

        lock (_lists)
        {
            if (!_lists.TryGetValue(key, out var known))
                _lists.Add(key, known = list(key));
            value = known;
            return true;
        }
    }

    public IEnumerator<KeyValuePair<string, IReadOnlyList<T>>> GetEnumerator() =>
        keys.Select(key => new KeyValuePair<string, IReadOnlyList<T>>(key, this[key])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
