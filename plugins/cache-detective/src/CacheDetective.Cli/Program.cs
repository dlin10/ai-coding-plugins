using CacheDetective.Cli;
using CacheDetective.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace CacheDetective;

internal static class Program
{
    private const string USAGE = """
        cachedet — inspect cache and data dependencies in .NET solutions.

        Usage:
          cachedet mcp        Serve the Model Context Protocol over stdio.
          cachedet --version  Print the version.
          cachedet --help     Print this help.
        """;

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(USAGE);
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
                Console.Out.WriteLine(USAGE);
                return ExitCode.Ok;
            case "mcp":
                return await RunMcpAsync().ConfigureAwait(false);
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                Console.Error.WriteLine(USAGE);
                return ExitCode.UsageError;
        }
    }

    private static async Task<int> RunMcpAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<WorkspaceSession>();
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = BuildInfo.ServerName,
                Version = BuildInfo.Version
            };
        }).WithStdioServerTransport()
          .WithTools<WorkspaceTools>()
          .WithTools<TraceTools>()
          .WithTools<FindingTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return ExitCode.Ok;
    }
}
