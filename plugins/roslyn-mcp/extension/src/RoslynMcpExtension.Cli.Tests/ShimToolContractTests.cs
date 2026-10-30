using System;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// What a host sees of the shim's tools, and what crosses from a tool's arguments to the daemon and back, against a fake
/// daemon on the shim's pipe, so that nothing loads. The HTTP server's packaged tests checked the same of the tools it
/// served until #214 retired it.
/// </summary>
public sealed class ShimToolContractTests
{
	private const string GetInt = "M:Ns.Api.Get(System.Int32)";

	private readonly string _pipe = Daemons.NewPipeName();

	[Fact]
	public async Task TheShimNamesItsVersionAndTheToolsKeepTheirSchemas()
	{
		using var shim = ShimProcess.Start(_pipe);
		var initialized = await shim.InitializeAsync();
		var tools = (await shim.RequestAsync("tools/list", new { }))["tools"]!.AsArray();

		JsonNode Schema(string tool) => tools.Single(t => t!["name"]!.GetValue<string>() == tool)!["inputSchema"]!;
		Assert.Equal(ExtensionVersion.Value, initialized["serverInfo"]!["version"]!.GetValue<string>());
		// A symbol is named by position or by symbolId, so neither form can be required.
		foreach (var addressed in new[] { "roslyn_find_references", "roslyn_find_implementations", "roslyn_find_callers", "roslyn_get_symbol_info", "roslyn_describe_type" })
		{
			Assert.Contains("symbolId", Schema(addressed)["properties"]!.AsObject().Select(p => p.Key));
			Assert.Empty(Schema(addressed)["required"]?.AsArray() ?? []);
		}
		Assert.Equal(["column", "filePath", "line"],
		             Schema("roslyn_go_to_definition")["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal));

		var validate = Schema("roslyn_validate_file");
		Assert.Equal(["filePath", "includeWarnings", "runAnalyzers"], validate["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
		Assert.Equal(["filePath"], validate["required"]!.AsArray().Select(n => n!.GetValue<string>()));
		Assert.True(validate["properties"]!["includeWarnings"]!["default"]!.GetValue<bool>());
		Assert.False(validate["properties"]!["runAnalyzers"]!["default"]!.GetValue<bool>());
	}

	[Fact]
	public async Task ArgumentsAndCountsCrossTheShimToTheDaemonUnchanged()
	{
		var daemon = new FakeDaemon();
		using var pipe = Serve(daemon);
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await shim.CallToolAsync("roslyn_load", new { solution = @"C:\repo\Sample.slnx" });
		var diagnostics = await shim.CallToolAsync("roslyn_get_diagnostics", new { filePaths = new[] { "C:/a.cs", "C:/b.cs" } });
		var references = await shim.CallToolAsync("roslyn_find_references", new { symbolId = GetInt });
		daemon.GeneratedDocumentCount = null;
		var unobserved = await shim.CallToolAsync("roslyn_validate_file", new { filePath = @"C:\repo\Consumer.cs" });
		daemon.GeneratedDocumentCount = 0;
		var noneGenerated = await shim.CallToolAsync("roslyn_validate_file", new { filePath = @"C:\repo\Consumer.cs" });

		Assert.Equal(SolutionServer.Daemon, load["servedBy"]!.GetValue<string>());
		Assert.Equal(["C:/a.cs", "C:/b.cs"], diagnostics["checkedProjects"]!.AsArray().Select(n => n!.GetValue<string>()));
		Assert.Equal(GetInt, references["symbol"]!["symbolId"]!.GetValue<string>());
		Assert.True(references["requestSucceeded"]!.GetValue<bool>(), "An omitted filePath must arrive as null, not as an empty path");
		Assert.True(unobserved["success"]!.GetValue<bool>());
		Assert.Null(unobserved["sourceGeneratedDocumentCount"]);
		Assert.Equal(0, noneGenerated["sourceGeneratedDocumentCount"]!.GetValue<int>());
	}

	/// <summary>Serves a fake daemon to the first shim that connects to the test's pipe.</summary>
	/// <param name="daemon">The fake daemon.</param>
	private NamedPipeServerStream Serve(FakeDaemon daemon)
	{
		var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
		                                     PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		_ = pipe.WaitForConnectionAsync().ContinueWith(_ => JsonRpc.Attach(pipe, daemon), CancellationToken.None,
		                                                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		return pipe;
	}
}
