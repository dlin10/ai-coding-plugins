using System.Globalization;

namespace FindFiles.Everything;

/// <summary>
/// Turns a <see cref="SearchRequest"/> into a ranked, capped result set plus the true match total.
/// </summary>
internal sealed class SearchService(IEsRunner? runner)
{
    internal SearchOutcome Execute(SearchRequest request)
    {
        if (runner is null)
        {
            return new SearchOutcome { Status = SearchStatus.EsMissing };
        }

        var results = runner.Run(EsArgumentBuilder.BuildResultArguments(request));

        switch (results.ExitCode)
        {
            case EsExitCode.EverythingNotRunning:
                return new SearchOutcome { Status = SearchStatus.EngineDown };
            case EsExitCode.NoResults:
                return new SearchOutcome { Status = SearchStatus.NoMatches };
            case EsExitCode.Success:
                break;
            default:
                return new SearchOutcome
                {
                    Status = SearchStatus.Failed,
                    Detail = FirstLine(results.StandardError) ?? $"es.exe exited with code {results.ExitCode}",
                };
        }

        // A zero exit code with no rows still means nothing matched; treat it the same way so the
        // caller never has to distinguish "empty success" from "declared empty".
        if (results.StandardOutputLines.Count == 0)
        {
            return new SearchOutcome { Status = SearchStatus.NoMatches };
        }

        var ranked = Rank(results.StandardOutputLines, request)
                     .Take(request.Limit)
                     .ToList();

        return new SearchOutcome
        {
            Status = SearchStatus.Ok,
            Paths = ranked,
            TotalMatches = ReadTotal(request, results.StandardOutputLines.Count),
        };
    }

    /// <summary>
    /// Shortest path first, because the shallowest copy of a name is usually the canonical one and
    /// backups, caches and build outputs sit deeper. Ties break on ordinal order so output is stable.
    /// </summary>
    private static IEnumerable<string> Rank(IReadOnlyList<string> paths, SearchRequest request) =>
        request.Recent
            ? paths
            : paths.OrderBy(path => path.Length)
                   .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

    private int ReadTotal(SearchRequest request, int fetchedCount)
    {
        if (runner is null)
        {
            return SearchOutcome.UnknownTotal;
        }

        var count = runner.Run(EsArgumentBuilder.BuildCountArguments(request));
        if (count.ExitCode != EsExitCode.Success)
        {
            return SearchOutcome.UnknownTotal;
        }

        var firstLine = FirstLine(string.Join('\n', count.StandardOutputLines));
        if (firstLine is not null
            && int.TryParse(firstLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            return total;
        }

        // Falling back to what we actually fetched keeps the summary honest rather than absent.
        return fetchedCount;
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }
}
