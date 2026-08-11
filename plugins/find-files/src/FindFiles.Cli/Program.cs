using System.Text;
using FindFiles.Cli;
using FindFiles.Everything;
using FindFiles.Mcp;

namespace FindFiles;

internal static class Program
{
    private const string Usage = """
        esfind — index-backed file name search for Windows, backed by voidtools Everything.

        Usage:
          esfind search <query> [options]   Search the Everything index and print matching paths.
          esfind mcp                        Serve the Model Context Protocol over stdio.
          esfind --version                  Print the version.

        Run "esfind search" with no query to see the search options.
        """;

    internal static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return ExitCode.UsageError;
        }

        switch (args[0])
        {
            case "--version":
            case "-v":
                Console.Out.WriteLine(BuildInfo.Version);
                return ExitCode.Ok;

            case "--help":
            case "-h":
                Console.Out.WriteLine(Usage);
                return ExitCode.Ok;

            case "search":
                return SearchCommand.Run(args[1..], CreateSearchService(), Console.Out, Console.Error);

            case "mcp":
                return RunMcp();

            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                Console.Error.WriteLine(Usage);
                return ExitCode.UsageError;
        }
    }

    private static int RunMcp()
    {
        // Own the standard streams explicitly: a byte order mark or a CRLF pair in the JSON-RPC
        // stream corrupts the transport, and the only symptom a user sees is a server that refuses
        // to start. Diagnostics stay on stderr for the same reason.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), encoding);
        using var output = new StreamWriter(Console.OpenStandardOutput(), encoding)
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        new McpServer(CreateSearchService()).Run(input, output);
        return ExitCode.Ok;
    }

    private static SearchService CreateSearchService()
    {
        var executable = EsLocator.Locate();
        return new SearchService(executable is null ? null : new EsRunner(executable));
    }
}
