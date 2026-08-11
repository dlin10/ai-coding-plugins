namespace FindFiles.Everything;

/// <summary>
/// Builds <c>es.exe</c> argument lists. Arguments are passed through
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so no shell quoting happens here
/// and the query reaches Everything verbatim — which is required, because the query is Everything
/// search syntax and escaping it would break wildcards, boolean operators and <c>ext:</c> filters.
/// </summary>
internal static class EsArgumentBuilder
{
    /// <summary>Arguments that ask only for the total number of matches.</summary>
    internal static List<string> BuildCountArguments(SearchRequest request)
    {
        var arguments = new List<string> { "-get-result-count" };
        AppendFilters(arguments, request);
        arguments.Add(request.Query);
        return arguments;
    }

    /// <summary>
    /// Arguments that ask for the matching paths. <c>-no-result-error</c> makes "nothing found"
    /// exit code 9, which is what lets an empty index result be told apart from success.
    /// </summary>
    internal static List<string> BuildResultArguments(SearchRequest request)
    {
        var arguments = new List<string>
        {
            "-no-result-error",
            "-n",
            request.FetchCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (request.Recent)
        {
            arguments.Add("-sort");
            arguments.Add("date-modified-descending");
        }

        AppendFilters(arguments, request);
        arguments.Add(request.Query);
        return arguments;
    }

    private static void AppendFilters(List<string> arguments, SearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            arguments.Add("-path");
            arguments.Add(request.Path);
        }

        switch (request.Kind)
        {
            case EntryKind.Files:
                arguments.Add("/a-d");
                break;
            case EntryKind.Folders:
                arguments.Add("/ad");
                break;
            case EntryKind.Both:
            default:
                break;
        }

        if (request.Regex)
        {
            arguments.Add("-r");
        }
    }
}
