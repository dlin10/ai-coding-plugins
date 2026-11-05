namespace ConcurrencyHunter.Analysis;

/// <summary>One copy of each distinct string its owner creates. The records an analysis builds over one body name the same path,
/// symbol, type or guard thousands of times, and without a pool each holds a copy of its own. A pool belongs to the one object
/// that creates the strings, lives as long as it does, and is not thread-safe: it is never shared between threads.</summary>
internal sealed class StringPool
{
    private readonly HashSet<string> _strings = new(StringComparer.Ordinal);

    /// <summary>The pool's copy of a string: the string itself the first time the pool meets its value, that first string after.</summary>
    /// <param name="value">The string.</param>
    internal string Get(string value)
    {
        if (_strings.TryGetValue(value, out var pooled))
            return pooled;
        _strings.Add(value);
        return value;
    }

    /// <summary>The pool's copy of a string that may be absent.</summary>
    /// <param name="value">The string, or null.</param>
    internal string? GetOrNull(string? value) => value is null ? null : Get(value);
}
