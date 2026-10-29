using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynMcpExtension.Cli.Tools;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>The stdio MCP server a host starts: every tool, forwarded to the per-user daemon.</summary>
internal static class ShimHost
{
	private const string Instructions =
		"Roslyn MCP without Visual Studio: a per-user daemon loads C# solutions with MSBuild and keeps them loaded across"
		+ " sessions. Call roslyn_load with the absolute path of the .sln or .slnx file first; every other tool answers from"
		+ " that solution only, and a file it does not contain fails with DocumentNotFound naming the solution searched.";

	/// <summary>Serves MCP over stdin and stdout until the host closes them.</summary>
	/// <param name="pipeName">The pipe the daemon listens on.</param>
	public static async Task<int> RunAsync(string pipeName)
	{
		using var daemon = new DaemonClient(pipeName);
		var builder = Host.CreateApplicationBuilder();
		// stdout carries the protocol, so every log line goes to stderr.
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
		builder.Services.AddSingleton(daemon);
		builder.Services.AddSingleton<IRoslynAnalysisRpc>(daemon);
		builder.Services.AddMcpServer(options =>
			{
				options.ServerInfo = new Implementation
				{
					Name = "roslyn-mcp",
					Version = ExtensionVersion.Value
				};
				options.ServerInstructions = Instructions;
			})
			.WithStdioServerTransport()
			.WithTools<LoadTool>()
			.WithTools<ValidateFileTool>()
			.WithTools<GetDiagnosticsTool>()
			.WithTools<FindReferencesTool>()
			.WithTools<FindImplementationsTool>()
			.WithTools<FindCallersTool>()
			.WithTools<GoToDefinitionTool>()
			.WithTools<DocumentSymbolsTool>()
			.WithTools<SearchSymbolsTool>()
			.WithTools<DeadCodeTool>()
			.WithTools<SymbolInfoTool>()
			.WithTools<DescribeTypeTool>();

		await builder.Build().RunAsync().ConfigureAwait(false);
		return 0;
	}
}
