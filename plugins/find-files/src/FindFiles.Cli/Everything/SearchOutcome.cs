namespace FindFiles.Everything;

internal enum SearchStatus
{
    Ok,

    /// <summary>The index holds no match. Not the same thing as the file not existing.</summary>
    NoMatches,

    /// <summary>Everything itself is not running, so there is no index to query.</summary>
    EngineDown,

    /// <summary>Everything's CLI could not be found on this machine.</summary>
    EsMissing,

    /// <summary><c>es.exe</c> failed for a reason we do not model.</summary>
    Failed,
}

internal sealed record SearchOutcome
{
    /// <summary>Reported when <c>-get-result-count</c> could not be read.</summary>
    internal const int UnknownTotal = -1;

    internal required SearchStatus Status { get; init; }

    internal IReadOnlyList<string> Paths { get; init; } = [];

    internal int TotalMatches { get; init; } = UnknownTotal;

    /// <summary>Diagnostic text from <c>es.exe</c>, used only by <see cref="SearchStatus.Failed"/>.</summary>
    internal string? Detail { get; init; }
}
