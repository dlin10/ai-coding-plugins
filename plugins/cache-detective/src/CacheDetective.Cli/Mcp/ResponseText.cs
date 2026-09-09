namespace CacheDetective.Mcp;

/// <summary>
/// Cutting a string down until what carries it fits. Two results have a field that is neither a list nor
/// pageable — an index result's <c>error</c>, a verification's <c>reason</c> — so the only thing that can
/// be done with an over-long one is to shorten it, and both do it the same way here rather than each
/// keeping a copy.
/// <para>The measure is always the serialized weight of the thing being fitted, never the character count.
/// The two differ by a factor of six for a script that escapes to <c>\uXXXX</c>: 1024 characters of
/// Cyrillic weigh about 6 KB of JSON, so a budget kept in characters was no budget at all.</para>
/// </summary>
internal static class ResponseText
{
    /// <summary>
    /// The longest prefix of <paramref name="text"/> for which <paramref name="weigh"/> comes in at or
    /// under <paramref name="budget"/>, marked as cut. Halving rather than searching precisely: the
    /// weighing serializes, and a result this large is already an unusual one.
    /// </summary>
    internal static string? Fitted(string? text, Func<string?, int> weigh, int budget, string marker)
    {
        ArgumentNullException.ThrowIfNull(weigh);
        if (text is null || weigh(text) <= budget)
            return text;

        for (var kept = text.Length / 2; kept > 0; kept /= 2)
        {
            var candidate = Truncate(text, kept, marker);
            if (weigh(candidate) <= budget)
                return candidate;
        }

        return marker.TrimStart();
    }

    /// <summary>A prefix of <paramref name="text"/> plus the marker, cut so that it never ends between the
    /// halves of a surrogate pair — which would turn one character into two broken ones.</summary>
    internal static string Truncate(string text, int characters, string marker)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.AsSpan(0, WithoutSplitSurrogate(text, 0, characters)), marker);
    }

    /// <summary>Shortens the piece by one if it would end on a high surrogate, whose pair is the next
    /// character.</summary>
    internal static int WithoutSplitSurrogate(string message, int start, int length) =>
        length > 1 && start + length < message.Length && char.IsHighSurrogate(message[start + length - 1])
            ? length - 1
            : length;
}
