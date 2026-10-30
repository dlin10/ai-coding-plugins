using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// A shim started the way a host starts it, spoken to in newline-delimited JSON-RPC over its stdin and stdout. The
/// process is exposed so that a test can put it in a job before the first tool call starts a daemon.
/// </summary>
internal sealed class ShimProcess : IDisposable
{
	private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(90);

	private readonly StringBuilder _stderr = new();
	private int _nextId;

	private ShimProcess(Process process)
	{
		Process = process;
		process.ErrorDataReceived += (_, e) => { lock (_stderr) _stderr.AppendLine(e.Data); };
		process.BeginErrorReadLine();
	}

	public Process Process { get; }

	/// <summary>Everything the shim has logged so far, for a failing assertion to show.</summary>
	public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

	/// <summary>Starts a shim that finds its daemon on <paramref name="pipeName"/>.</summary>
	/// <param name="pipeName">The pipe the shim and its daemon meet on.</param>
	public static ShimProcess Start(string pipeName)
	{
		var start = new ProcessStartInfo(Cli.ExecutablePath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8
		};
		foreach (var argument in new[] { "mcp", "--pipe", pipeName }) start.ArgumentList.Add(argument);
		return new ShimProcess(Process.Start(start)!);
	}

	/// <summary>The MCP handshake, as every host sends it before anything else.</summary>
	public async Task<JsonNode> InitializeAsync()
	{
		var result = await RequestAsync("initialize", new
		{
			protocolVersion = "2025-06-18",
			capabilities = new { },
			clientInfo = new { name = "roslyn-mcp-tests", version = "1" }
		});
		await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
		return result;
	}

	/// <summary>Calls a tool and returns the JSON object its text content carries.</summary>
	/// <param name="name">The tool's name.</param>
	/// <param name="arguments">The tool's arguments, serialized as they are.</param>
	public async Task<JsonNode> CallToolAsync(string name, object arguments)
	{
		var result = await RequestAsync("tools/call", new { name, arguments });
		if (result["isError"]?.GetValue<bool>() == true)
			throw new InvalidOperationException($"{name} failed: {result.ToJsonString()}\n{Stderr}");
		return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
	}

	/// <summary>Sends a request and returns its result, skipping whatever else the shim writes meanwhile.</summary>
	/// <param name="method">The JSON-RPC method.</param>
	/// <param name="parameters">Its parameters, serialized as they are.</param>
	public async Task<JsonNode> RequestAsync(string method, object parameters)
	{
		var id = Interlocked.Increment(ref _nextId);
		await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
		using var timeout = new CancellationTokenSource(ResponseTimeout);
		while (true)
		{
			var line = await Process.StandardOutput.ReadLineAsync(timeout.Token)
			           ?? throw new EndOfStreamException($"The shim closed stdout while {method} was pending.\n{Stderr}");
			var message = JsonNode.Parse(line)!;
			if (message["id"]?.GetValue<int>() != id) continue;
			if (message["error"] is { } error)
				throw new InvalidOperationException($"{method} failed: {error.ToJsonString()}\n{Stderr}");
			return message["result"]!;
		}
	}

	private async Task SendAsync(object message)
	{
		await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
		await Process.StandardInput.FlushAsync();
	}

	/// <summary>Closes stdin, as a host does when it is done, and kills the shim if it does not exit.</summary>
	public void Dispose()
	{
		try { Process.StandardInput.Close(); } catch (IOException) { }
		if (!Process.WaitForExit(10_000)) Process.Kill(entireProcessTree: false);
		Process.Dispose();
	}
}
