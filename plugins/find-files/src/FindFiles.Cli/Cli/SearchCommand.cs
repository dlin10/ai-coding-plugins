using System.Globalization;
using FindFiles.Everything;
using FindFiles.Formatting;

namespace FindFiles.Cli;

internal static class ExitCode
{
    internal const int Ok = 0;
    internal const int NoMatches = 1;
    internal const int UsageError = 2;
    internal const int Unavailable = 3;
}

/// <summary>
/// The <c>search</c> verb. It exists for manual runs and for the test suite; hosts reach the same
/// logic through the MCP server, so both paths share <see cref="SearchService"/> and
/// <see cref="OutputText"/> and cannot drift apart.
/// </summary>
internal static class SearchCommand
{
    internal static int Run(string[] arguments, SearchService searchService, TextWriter output, TextWriter error)
    {
        if (!TryParse(arguments, out var request, out var parseError))
        {
            error.WriteLine(parseError);
            error.WriteLine(Usage);
            return ExitCode.UsageError;
        }

        var outcome = searchService.Execute(request);
        output.WriteLine(OutputText.Render(outcome, request));

        return outcome.Status switch
        {
            SearchStatus.Ok => ExitCode.Ok,
            SearchStatus.NoMatches => ExitCode.NoMatches,
            _ => ExitCode.Unavailable,
        };
    }

    internal const string Usage = """
        Usage: esfind search <query> [-path <dir>] [-limit <n>] [-recent] [-files|-dirs] [-regex]

          <query>        Everything search syntax (wildcards, ext:, size:, dm:, boolean operators).
          -path <dir>    Restrict the search to this directory tree.
          -limit <n>     Maximum results to print (default 20).
          -recent        Rank by modification date instead of by shortest path.
          -files         Files only.
          -dirs          Folders only.
          -regex         Treat the query as a regular expression.
        """;

    private static bool TryParse(string[] arguments,
                                 out SearchRequest request,
                                 out string parseError)
    {
        request = new SearchRequest { Query = string.Empty };
        parseError = string.Empty;

        string? query = null;
        string? path = null;
        var limit = SearchRequest.DefaultLimit;
        var recent = false;
        var regex = false;
        var kind = EntryKind.Both;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-path":
                    if (!TryTakeValue(arguments, ref index, out path))
                    {
                        parseError = "-path requires a directory";
                        return false;
                    }

                    break;

                case "-limit":
                    if (!TryTakeValue(arguments, ref index, out var limitText)
                        || !int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
                        || limit <= 0)
                    {
                        parseError = "-limit requires a positive integer";
                        return false;
                    }

                    break;

                case "-recent":
                    recent = true;
                    break;

                case "-files":
                    kind = EntryKind.Files;
                    break;

                case "-dirs":
                    kind = EntryKind.Folders;
                    break;

                case "-regex":
                    regex = true;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        parseError = $"Unknown option: {argument}";
                        return false;
                    }

                    if (query is not null)
                    {
                        parseError = "Only one query is accepted; quote it if it contains spaces";
                        return false;
                    }

                    query = argument;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            parseError = "A query is required";
            return false;
        }

        request = new SearchRequest
        {
            Query = query,
            Path = path,
            Limit = limit,
            Recent = recent,
            Kind = kind,
            Regex = regex,
        };

        return true;
    }

    private static bool TryTakeValue(string[] arguments, ref int index, out string? value)
    {
        if (index + 1 >= arguments.Length)
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
