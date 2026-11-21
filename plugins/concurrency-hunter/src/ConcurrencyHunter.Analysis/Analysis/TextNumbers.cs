namespace ConcurrencyHunter.Analysis;

/// <summary>A number for each distinct string its owner meets, in the order it meets them: equal strings get one number and different
/// strings different ones, so a key made of the numbers tells apart what a key made of the strings does, and hashes and compares
/// without reading the strings again (issue 464). A number is an identity and nothing more: keys use it for equality and hashing
/// only, and nothing observable may depend on its value or on the order numbers are given out in — no iteration order, no sort, no
/// choice of a first among equals. What is ordered stays ordered by what it was ordered by before; a collection running in parallel
/// would give the numbers out in another order on every run. A table belongs to the one object that asks, lives as long as it does,
/// and is not thread-safe: it is never shared between threads.</summary>
internal sealed class TextNumbers
{
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);

    /// <summary>The number of a string: the next unused one the first time the table meets its value, the same one after.</summary>
    /// <param name="value">The string.</param>
    internal int Of(string value)
    {
        if (!_numbers.TryGetValue(value, out var number))
            _numbers.Add(value, number = _numbers.Count);
        return number;
    }
}
