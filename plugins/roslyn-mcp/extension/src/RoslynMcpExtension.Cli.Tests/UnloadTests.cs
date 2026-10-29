using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Cli.Tools;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// roslyn_unload through the shim's tool and client, with every session connected to a daemon session of its own, all
/// served in this process from one registry.
/// </summary>
public sealed class UnloadTests : IDisposable
{
	private static readonly string SolutionPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Unload.slnx");
	private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Unload.cs");

	private readonly OneFileLoader _loader = new(FilePath);
	private readonly SolutionRegistry _registry;
	private readonly ConcurrentBag<IDisposable> _disposables = [];

	public UnloadTests() => _registry = new SolutionRegistry(_loader);

	public void Dispose()
	{
		foreach (var disposable in _disposables) disposable.Dispose();
		_registry.Dispose();
	}

	[Fact]
	public async Task RoslynUnloadUnloadsAtOnceAndAnotherSessionsNextCallLoadsTheSolutionAgain()
	{
		var first = NewSession();
		var second = NewSession();
		await first.Client.LoadAsync(SolutionPath);
		await second.Client.LoadAsync(SolutionPath);

		var unload = await first.Unload.Unload();
		var released = _loader.Released(0);
		var symbols = await second.Client.GetDocumentSymbolsAsync(FilePath);

		Assert.True(unload.RequestSucceeded, unload.ErrorMessage);
		Assert.True(unload.Unloaded);
		Assert.Equal(SolutionPath, unload.Solution, ignoreCase: true);
		Assert.True(released);
		Assert.True(symbols.RequestSucceeded, symbols.ErrorMessage);
		Assert.Equal(2, _loader.Loads);
	}

	[Fact]
	public async Task UnloadingASolutionTheDaemonDoesNotHoldUnloadsNothing()
	{
		var session = NewSession();

		var unload = await session.Unload.Unload(SolutionPath);

		Assert.True(unload.RequestSucceeded, unload.ErrorMessage);
		Assert.False(unload.Unloaded);
		Assert.Equal(0, _loader.Loads);
	}

	[Fact]
	public async Task UnloadingWithoutASolutionInASessionThatLoadedNoneSaysSo()
	{
		var unload = await NewSession().Unload.Unload();

		Assert.False(unload.RequestSucceeded);
		Assert.Equal(ToolErrorCodes.NoSolution, unload.ErrorCode);
	}

	/// <summary>A shim session, its client connected to a daemon session of its own served in this process.</summary>
	private Session NewSession()
	{
		var pipeName = Daemons.NewPipeName();
		var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
		                                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		_ = server.WaitForConnectionAsync().ContinueWith(
			_ => _disposables.Add(JsonRpc.Attach(server, new DaemonSession(_registry, TimeSpan.FromMinutes(1), new DaemonInfo()))),
			CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		var client = new DaemonClient(pipeName);
		_disposables.Add(client);
		_disposables.Add(server);
		return new Session(client, new UnloadTool(client));
	}

	/// <summary>A shim's session: the client every other tool calls, and roslyn_unload.</summary>
	/// <param name="Client">The client the other tools call.</param>
	/// <param name="Unload">roslyn_unload over that client.</param>
	private sealed record Session(DaemonClient Client, UnloadTool Unload);
}
