namespace FindFiles.Everything;

internal enum EntryKind
{
    Both,
    Files,
    Folders,
}

/// <summary>
/// A name search against the Everything index. <see cref="Query"/> is Everything search syntax,
/// not a literal string: wildcards, boolean operators and filters such as <c>ext:</c> are live.
/// </summary>
internal sealed record SearchRequest
{
    internal const int DefaultLimit = 20;

    /// <summary>
    /// How many results to pull from <c>es.exe</c> before sorting locally. Everything cannot sort
    /// by path length, so shortest-path ranking is only exact within this window; the summary line
    /// always reports the true total so a truncated ranking is visible to the caller.
    /// </summary>
    internal const int FetchWindow = 1000;

    internal required string Query { get; init; }

    /// <summary>Restricts the search to a directory tree via <c>es -path</c>.</summary>
    internal string? Path { get; init; }

    internal int Limit { get; init; } = DefaultLimit;

    /// <summary>Rank by modification date instead of by path length.</summary>
    internal bool Recent { get; init; }

    internal EntryKind Kind { get; init; } = EntryKind.Both;

    internal bool Regex { get; init; }

    /// <summary>
    /// When ranking by date, Everything already returns rows in the wanted order, so only
    /// <see cref="Limit"/> rows are needed. Path-length ranking has to sort locally and therefore
    /// pulls a wider window.
    /// </summary>
    internal int FetchCount => Recent ? Limit : Math.Max(Limit, FetchWindow);
}
