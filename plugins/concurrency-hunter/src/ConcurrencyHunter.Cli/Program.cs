using ConcurrencyHunter.Mcp;
using ConcurrencyHunter.Runs;
using Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ConcurrencyHunter;

internal static class Program
{
    private const string USAGE = """
        concurrency-hunter — find races and lost updates in .NET solutions.

        Usage:
          concurrency-hunter mcp        Serve the Model Context Protocol over stdio.
          concurrency-hunter --version  Print the version.
          concurrency-hunter --help     Print this help.
        """;

    internal static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            error.WriteLine(USAGE);
            return ExitCode.UsageError;
        }

        switch (args[0])
        {
            case "--version":
            case "-v":
                output.WriteLine(BuildInfo.Version);
                return ExitCode.Ok;
            case "--help":
            case "-h":
                output.WriteLine(USAGE);
                return ExitCode.Ok;
            case "mcp":
                return await RunMcpAsync().ConfigureAwait(false);
            default:
                error.WriteLine($"Unknown command: {args[0]}");
                error.WriteLine(USAGE);
                return ExitCode.UsageError;
        }
    }

    private static async Task<int> RunMcpAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(serviceProvider =>
        {
            var localApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                localApplicationData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
            }

            return new RunRegistry(
                serviceProvider.GetRequiredService<TimeProvider>(),
                Path.Combine(localApplicationData, "concurrency-hunter", "runs"),
                SolutionAnalysis.RunAsync);
        });
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = BuildInfo.ServerName,
                Version = BuildInfo.Version
            };
        }).WithStdioServerTransport()
          .WithTools<RunTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return ExitCode.Ok;
    }
}
