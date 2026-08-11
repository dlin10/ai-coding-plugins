using System.Globalization;
using System.Text;
using FindFiles.Everything;

namespace FindFiles.Formatting;

/// <summary>
/// Renders a <see cref="SearchOutcome"/> as the text an agent reads. Plain lines rather than JSON:
/// the caller needs paths, and JSON would trade roughly three times the tokens for structure nobody
/// consumes. Failure text carries its own guidance, because tool output is read every time while a
/// skill file is only read sometimes.
/// </summary>
internal static class OutputText
{
    internal static string Render(SearchOutcome outcome, SearchRequest request) => outcome.Status switch
    {
        SearchStatus.Ok => RenderHits(outcome),
        SearchStatus.NoMatches => RenderNoMatches(request),
        SearchStatus.EngineDown => "Everything is not running, so index-backed search is unavailable. "
            + "Use Glob or ripgrep for this search, or start Everything (voidtools) and retry.",
        SearchStatus.EsMissing => "es.exe (the Everything command line interface) was not found. "
            + "Install voidtools Everything, or point the ES_PATH environment variable at es.exe. "
            + "Until then, use Glob or ripgrep.",
        SearchStatus.Failed => $"es.exe failed: {outcome.Detail}",
        _ => $"Unhandled search status: {outcome.Status}",
    };

    private static string RenderHits(SearchOutcome outcome)
    {
        var builder = new StringBuilder();
        foreach (var path in outcome.Paths)
        {
            builder.AppendLine(path);
        }

        var shown = outcome.Paths.Count;
        var total = outcome.TotalMatches;

        if (total == SearchOutcome.UnknownTotal)
        {
            builder.Append(CultureInfo.InvariantCulture, $"-- {shown} shown; total match count unavailable");
        }
        else if (total > shown)
        {
            builder.Append(CultureInfo.InvariantCulture,
                           $"-- shown {shown} of {total} matches; narrow the query or raise the limit");
        }
        else
        {
            builder.Append(CultureInfo.InvariantCulture, $"-- {total} {(total == 1 ? "match" : "matches")}");
        }

        return builder.ToString();
    }

    private static string RenderNoMatches(SearchRequest request) =>
        $"No matches in the Everything index for \"{request.Query}\""
        + (string.IsNullOrWhiteSpace(request.Path) ? "." : $" under \"{request.Path}\".")
        + " This does not prove the file is absent: folders excluded from indexing, unindexed network"
        + " shares, and online-only OneDrive or Dropbox placeholders are invisible to the index."
        + " Fall back to a filesystem walk before telling the user the file does not exist, and say"
        + " which method you used.";
}
