using System.Collections.Concurrent;

namespace ConcurrencyHunter.Analysis;

/// <summary>A number for each distinct string its owner meets: equal strings get one number and different strings different ones, so
/// a key made of the numbers tells apart what a key made of the strings does, and hashes and compares without reading the strings
/// again (issue 464). A number is an identity and nothing more: keys use it for equality and hashing only, and nothing observable may
/// depend on its value or on the order numbers are given out in — no iteration order, no sort, no choice of a first among equals.
/// What is ordered stays ordered by what it was ordered by before. A table belongs to the one object that asks and lives as long as
/// it does. It is thread-safe: the collectors of a scope walking on several workers share one (ADR 0040), and they give the numbers
/// out in another order on every run, some numbers left unused where two threads met a new string at once.</summary>
internal sealed class TextNumbers
{
    private readonly ConcurrentDictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>The number of a string: an unused one the first time the table meets its value, the same one after.</summary>
    /// <param name="value">The string.</param>
    internal int Of(string value) =>
        _numbers.TryGetValue(value, out var number) ? number : _numbers.GetOrAdd(value, _ => Interlocked.Increment(ref _next) - 1);
}
