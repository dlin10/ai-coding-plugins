using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Server.Tests;

public sealed class PackagedValidationTests
{
	[Fact]
	public async Task PackagedServerPreservesSchemasAndCountWireValues()
	{
		var plugin = new DirectoryInfo(AppContext.BaseDirectory);
		while (plugin != null && !File.Exists(Path.Combine(plugin.FullName, ".codex-plugin", "plugin.json"))) plugin = plugin.Parent;
		Assert.NotNull(plugin);
		var vsix = Path.Combine(plugin!.FullName, "extension", "src", "RoslynMcpExtension", "bin", "Release", "net48", "RoslynMcpExtension.vsix");
		Assert.True(File.Exists(vsix), "Build the Release VSIX before packaged tests");
		var temporary = Path.Combine(Path.GetTempPath(), "roslyn-issue70-package-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temporary);
		Process? process = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		try
		{
			ZipFile.ExtractToDirectory(vsix, temporary);
			Assert.Empty(Directory.GetFiles(temporary, "*Issue70*", SearchOption.AllDirectories));
			var server = Directory.GetFiles(temporary, "RoslynMcpExtension.Server.dll", SearchOption.AllDirectories).Single();
			var pipeName = "roslyn-issue70-" + Guid.NewGuid().ToString("N");
			using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			var endpointProbe = new TcpListener(IPAddress.Loopback, 0);
			endpointProbe.Start();
			var port = ((IPEndPoint)endpointProbe.LocalEndpoint).Port;
			endpointProbe.Stop();
			var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
			foreach (var arg in new[] { server, "--pipe", pipeName, "--host", "127.0.0.1", "--port", port.ToString(), "--solution", @"C:\repo\Sample.sln" }) start.ArgumentList.Add(arg);
			process = Process.Start(start)!;
			var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
			var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
			await pipe.WaitForConnectionAsync(timeout.Token);
			var peer = new Peer();
			using var rpc = new JsonRpc(pipe, pipe, peer);
			rpc.StartListening();
			await peer.Ready.Task.WaitAsync(timeout.Token);
			Assert.False(process.HasExited);
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp") };
			http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
			http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
			async Task<JsonNode> Call(string method, object parameters)
			{
				var payload = System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
				using var response = await http.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"), timeout.Token);
				response.EnsureSuccessStatusCode();
				var message = ParseResponse(await response.Content.ReadAsStringAsync(timeout.Token));
				Assert.Null(message["error"]);
				return message["result"]!;
			}
			var initialized = await Call("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "issue70-test", version = "1" } });
			Assert.Equal("1.9.0", initialized["serverInfo"]!["version"]!.GetValue<string>());
			var instructions = initialized["instructions"]!.GetValue<string>();
			Assert.Contains(@"C:\repo\Sample.sln", instructions);
			Assert.Contains($"port {port}", instructions);
			var current = (await Call("tools/list", new { }))["tools"]!.AsArray();
			Assert.Equal(11, current.Count);
			JsonNode Schema(string tool) => current.Single(t => t!["name"]!.GetValue<string>() == tool)!["inputSchema"]!;
			// A symbol is named by position or by symbolId, so neither form can be required.
			foreach (var addressed in new[] { "roslyn_find_references", "roslyn_find_implementations", "roslyn_find_callers", "roslyn_get_symbol_info", "roslyn_describe_type" })
			{
				Assert.Contains("symbolId", Schema(addressed)["properties"]!.AsObject().Select(p => p.Key));
				Assert.Empty(Schema(addressed)["required"]?.AsArray() ?? []);
			}
			Assert.Equal(new[] { "filePath", "column", "line" }.OrderBy(n => n),
			             Schema("roslyn_go_to_definition")["required"]!.AsArray().Select(n => n!.GetValue<string>()).OrderBy(n => n));

			var diagnostics = JsonNode.Parse((await Call("tools/call", new { name = "roslyn_get_diagnostics", arguments = new { filePaths = new[] { "C:/a.cs", "C:/b.cs" } } }))["content"]![0]!["text"]!.GetValue<string>())!;
			Assert.Equal(new[] { "C:/a.cs", "C:/b.cs" }, diagnostics["checkedProjects"]!.AsArray().Select(n => n!.GetValue<string>()));
			var references = JsonNode.Parse((await Call("tools/call", new { name = "roslyn_find_references", arguments = new { symbolId = "M:Ns.Api.Get(System.Int32)" } }))["content"]![0]!["text"]!.GetValue<string>())!;
			Assert.Equal("M:Ns.Api.Get(System.Int32)", references["symbol"]!["symbolId"]!.GetValue<string>());
			Assert.True(references["requestSucceeded"]!.GetValue<bool>(), "An omitted filePath must arrive as null, not as an empty path");

			var validate = Schema("roslyn_validate_file");
			Assert.Equal(new[] { "filePath", "includeWarnings", "runAnalyzers" }, validate["properties"]!.AsObject().Select(p => p.Key).OrderBy(n => n));
			Assert.Equal(new[] { "filePath" }, validate["required"]!.AsArray().Select(n => n!.GetValue<string>()));
			Assert.True(validate["properties"]!["includeWarnings"]!["default"]!.GetValue<bool>());
			Assert.False(validate["properties"]!["runAnalyzers"]!["default"]!.GetValue<bool>());
			foreach (var count in new int?[] { null, 0 })
			{
				peer.Count = count;
				var response = await Call("tools/call", new { name = "roslyn_validate_file", arguments = new { filePath = Path.Combine(temporary, "Consumer.cs") } });
				Assert.False(response["isError"]?.GetValue<bool>() ?? false);
				var result = JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!;
				Assert.True(result["success"]!.GetValue<bool>());
				Assert.True(result["requestSucceeded"]!.GetValue<bool>());
				if (count == null) Assert.Null(result["sourceGeneratedDocumentCount"]);
				else Assert.Equal(0, result["sourceGeneratedDocumentCount"]!.GetValue<int>());
			}
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync(timeout.Token);
			await Task.WhenAll(stdout, stderr);
		}
		finally
		{
			if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
			process?.Dispose();
			Directory.Delete(temporary, recursive: true);
		}
	}

	private static JsonNode ParseResponse(string raw)
	{
		var data = string.Join("\n", raw.Split('\n').Where(line => line.StartsWith("data: ")).Select(line => line.Substring(6)));
		return JsonNode.Parse(string.IsNullOrWhiteSpace(data) ? raw : data)!;
	}

	private sealed class Peer : IRoslynAnalysisRpc
	{
		internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal int? Count;
		public Task LogAsync(string message) => Task.CompletedTask;
		public Task ReadyAsync() { Ready.TrySetResult(); return Task.CompletedTask; }
		public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
			=> Task.FromResult(new ValidateFileResult { FilePath = filePath, Success = true, RequestSucceeded = true, SourceGeneratedDocumentCount = Count });
		// These two echo their input, so the test sees what crossed the pipe.
		public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers, int maxResults)
			=> Task.FromResult(new DiagnosticsResult { CheckedProjects = [.. filePaths ?? []], Complete = true, RequestSucceeded = true });
		public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults)
			=> Task.FromResult(new SymbolListResult { Symbol = new SymbolLocation { SymbolId = symbolId }, RequestSucceeded = filePath == null });
		public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column) => throw new NotSupportedException();
		public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath) => throw new NotSupportedException();
		public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic) => throw new NotSupportedException();
		public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName) => throw new NotSupportedException();
		public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName, string? memberFilter, int maxResults) => throw new NotSupportedException();
	}
}
