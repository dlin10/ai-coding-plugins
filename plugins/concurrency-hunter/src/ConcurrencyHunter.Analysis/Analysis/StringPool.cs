using System.Collections.Concurrent;

namespace ConcurrencyHunter.Analysis;

/// <summary>One copy of each distinct string its owner creates. The records an analysis builds over one body name the same path,
/// symbol, type or guard thousands of times, and without a pool each holds a copy of its own. A pool belongs to the one object
/// that creates the strings and lives as long as it does. It is thread-safe: the collectors of a scope walking on several workers
/// share one (ADR 0040), and every caller keeps the copy the pool returns, so two threads meeting one value hold one string.</summary>
internal sealed class StringPool
{
    private readonly ConcurrentDictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>The pool's copy of a string: the string itself the first time the pool meets its value, that first string after.</summary>
    /// <param name="value">The string.</param>
    internal string Get(string value) => _strings.TryGetValue(value, out var pooled) ? pooled : _strings.GetOrAdd(value, value);

    /// <summary>The pool's copy of a string that may be absent.</summary>
    /// <param name="value">The string, or null.</param>
    internal string? GetOrNull(string? value) => value is null ? null : Get(value);
}
