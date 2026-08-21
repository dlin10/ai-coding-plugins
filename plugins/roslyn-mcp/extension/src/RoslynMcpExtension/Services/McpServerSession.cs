using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;

namespace RoslynMcpExtension.Services;

internal enum ServerStartFailureKind
{
	None,
	DotnetNotFound,
	ProcessExited,
	ReadinessTimeout,
	ProcessStartFailed
}

/// <summary>Per-solution configuration a session is started with.</summary>
internal sealed class McpServerSessionOptions(int port, string serverName, string? solutionDirectory, string? configPath)
{
	public int Port { get; } = port;
	public string ServerName { get; } = serverName;
	public string? SolutionDirectory { get; } = solutionDirectory;
	public string? ConfigPath { get; } = configPath;
}

internal sealed class ServerStartResult
{
	private ServerStartResult(IMcpServerSession? session, ServerStartFailureKind failureKind, string message)
	{
		Session = session;
		FailureKind = failureKind;
		Message = message;
	}

	public IMcpServerSession? Session { get; }
	public ServerStartFailureKind FailureKind { get; }
	public string Message { get; }
	public bool Succeeded => Session != null;

	public static ServerStartResult Started(IMcpServerSession session)
		=> new(session, ServerStartFailureKind.None, "Server is ready.");

	public static ServerStartResult Failed(ServerStartFailureKind failureKind, string message)
		=> new(null, failureKind, message);
}

/// <summary>
/// One run of the MCP server. Exists so that every callback can only ever reach the state of its
/// own run: restarting builds a new session rather than mutating a shared one.
/// </summary>
internal interface IMcpServerSession : IDisposable
{
	int Port { get; }
	bool IsRunning { get; }

	/// <summary>Stops the server, optionally asking it to shut down cleanly first.</summary>
	Task StopAsync(bool graceful);

	/// <summary>Terminates the server without waiting. Used while VS is shutting down.</summary>
	void KillNow();
}

internal sealed class McpServerSession : IMcpServerSession
{
	private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan GracefulSignalTimeout = TimeSpan.FromSeconds(2);

	private readonly NamedPipeServerStream _pipe;
	private readonly JsonRpc _rpc;
	private readonly IServerRpc _serverProxy;
	private readonly ChildProcessHandle _child;
	private readonly RoslynAnalysisService _analysisService;
	private readonly Action _readyHandler;
	private readonly IExtensionLogger? _logger;
	private int _ended;
	private int _stopping;
	private int _disposed;

	private McpServerSession(McpServerSessionOptions options,
							 NamedPipeServerStream pipe,
							 JsonRpc rpc,
							 IServerRpc serverProxy,
							 ChildProcessHandle child,
							 RoslynAnalysisService analysisService,
							 Action readyHandler,
							 IExtensionLogger? logger)
	{
		Port = options.Port;
		_pipe = pipe;
		_rpc = rpc;
		_serverProxy = serverProxy;
		_child = child;
		_analysisService = analysisService;
		_readyHandler = readyHandler;
		_logger = logger;

		_rpc.Disconnected += OnRpcDisconnected;
	}

	public int Port { get; }

	public bool IsRunning => Volatile.Read(ref _disposed) == 0
						  && Volatile.Read(ref _ended) == 0
						  && _child.IsRunning;

	public static async Task<ServerStartResult> StartAsync(McpServerSessionOptions options,
														   RoslynAnalysisService analysisService,
														   IExtensionLogger? logger)
	{
		var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		var serverDir = Path.Combine(extensionDir!, "McpServer");
		var serverPath = Path.Combine(serverDir, "RoslynMcpExtension.Server.dll");

		if (!File.Exists(serverPath))
		{
			var missingServerMessage = $"Server executable not found at expected path: {serverPath}";
			logger?.Log(missingServerMessage);
			return ServerStartResult.Failed(ServerStartFailureKind.ProcessStartFailed, missingServerMessage);
		}

		var dotnetPath = ResolveDotnetPath();
		if (dotnetPath == null)
		{
			const string missingDotnetMessage = "Unable to start the Roslyn MCP server: .NET 10 is required, but dotnet.exe was not found on PATH or under %ProgramFiles%\\dotnet.";
			logger?.Log(missingDotnetMessage);
			return ServerStartResult.Failed(ServerStartFailureKind.DotnetNotFound, missingDotnetMessage);
		}

		var pipeName = $"RoslynMcp_{Guid.NewGuid():N}";
		var readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Action readyHandler = () => readySource.TrySetResult(true);

		NamedPipeServerStream? pipe = null;
		ChildProcessHandle? child = null;
		JsonRpc? rpc = null;
		Task? connected = null;

		void Cleanup()
		{
			if (ReferenceEquals(analysisService.ServerReadyHandler, readyHandler))
				analysisService.ServerReadyHandler = null;

			if (connected != null) _ = ObserveAsync(connected);
			try { rpc?.Dispose(); } catch { }
			try { pipe?.Dispose(); } catch { }
			child?.KillNow();
		}

		async Task<ServerStartResult> AbortAsync()
		{
			ServerStartFailureKind failureKind;
			string message;

			if (child != null && child.Exited.IsCompleted)
			{
				#pragma warning disable VSTHRD003 // Already completed; represents the child process, not our context.
				var exitCode = await child.Exited.ConfigureAwait(false);
				#pragma warning restore VSTHRD003
				failureKind = ServerStartFailureKind.ProcessExited;
				message = $"MCP Server exited before becoming ready (exit code {exitCode?.ToString() ?? "unknown"}). Port {options.Port} may already be in use.";
			}
			else
			{
				failureKind = ServerStartFailureKind.ReadinessTimeout;
				message = $"MCP Server did not become ready within {StartTimeout.TotalSeconds:0} seconds. Port {options.Port} may already be in use.";
			}

			logger?.Log(message);
			Cleanup();
			return ServerStartResult.Failed(failureKind, message);
		}

		try
		{
			// A single connection per session: the child never reconnects, it exits when the
			// pipe drops, so there is nothing to re-accept.
			pipe = new NamedPipeServerStream(pipeName,
											 PipeDirection.InOut,
											 1,
											 PipeTransmissionMode.Byte,
											 PipeOptions.Asynchronous);
			connected = pipe.WaitForConnectionAsync();

			analysisService.ServerReadyHandler = readyHandler;

			logger?.Log($"Starting MCP server on port {options.Port} (pipe: {pipeName})...");
			child = ChildProcessHandle.Start(CreateStartInfo(dotnetPath, serverPath, serverDir, pipeName, options), logger);

			var deadline = Task.Delay(StartTimeout);

			if (await Task.WhenAny(connected, child.Exited, deadline).ConfigureAwait(false) != connected)
				return await AbortAsync().ConfigureAwait(false);

			await connected.ConfigureAwait(false);

			// Attaching a local target and a proxy over one connection is what lets the
			// extension call IServerRpc.ShutdownAsync when it is time to stop the child.
			rpc = new JsonRpc(pipe);
			rpc.AddLocalRpcTarget(analysisService);
			var serverProxy = rpc.Attach<IServerRpc>();
			rpc.StartListening();

			if (await Task.WhenAny(readySource.Task, child.Exited, deadline).ConfigureAwait(false) != readySource.Task)
				return await AbortAsync().ConfigureAwait(false);

			logger?.Log($"MCP Server started on http://localhost:{options.Port}/mcp (pipe: {pipeName})");
			return ServerStartResult.Started(new McpServerSession(options, pipe, rpc, serverProxy, child, analysisService, readyHandler, logger));
		}
		catch (Exception ex)
		{
			var message = $"MCP Server failed to start: {ex.Message}";
			logger?.Log(message);
			Cleanup();
			return ServerStartResult.Failed(ServerStartFailureKind.ProcessStartFailed, message);
		}
	}

	public async Task StopAsync(bool graceful)
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

		if (graceful)
		{
			await SignalShutdownAsync().ConfigureAwait(false);

			// Keep the connection up while the child winds down: it reports its final state
			// over this pipe, and tearing it down first makes those calls fail inside the child.
			await _child.StopAsync(GracefulStopTimeout).ConfigureAwait(false);
			DisposeTransport();
		}
		else
		{
			DisposeTransport();
			_child.KillNow();
		}

		Dispose();
	}

	public void KillNow()
	{
		Interlocked.Exchange(ref _stopping, 1);
		DisposeTransport();
		_child.KillNow();
		Dispose();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		// Only unhook the shared MEF singleton's handler if it is still ours: a newer session
		// may already have replaced it.
		if (ReferenceEquals(_analysisService.ServerReadyHandler, _readyHandler))
			_analysisService.ServerReadyHandler = null;

		DisposeTransport();
		_child.Dispose();
	}

	/// <summary>
	/// Asks the server to shut down so Kestrel closes its listening socket and releases the port.
	/// Killing it outright leaves the port unavailable to the next start for a while.
	/// </summary>
	private async Task SignalShutdownAsync()
	{
		Task request;
		try
		{
			request = _serverProxy.ShutdownAsync();
		}
		catch (Exception ex)
		{
			_logger?.Log($"Could not request MCP server shutdown: {ex.Message}");
			return;
		}

		// The connection goes away as a direct result of this call, so a fault is expected.
		await Task.WhenAny(ObserveAsync(request), Task.Delay(GracefulSignalTimeout)).ConfigureAwait(false);
	}

	// Raised on StreamJsonRpc's own thread; see ChildProcessHandle.OnExited for why a callback
	// like this must never throw.
	private void OnRpcDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
	{
		try
		{
			if (Interlocked.Exchange(ref _ended, 1) != 0) return;
			if (Volatile.Read(ref _stopping) == 0)
				_logger?.Log($"MCP server connection closed unexpectedly ({e.Reason}): {e.Description}. Session ended.");
		}
		catch { }
	}

	private void DisposeTransport()
	{
		try { _rpc.Disconnected -= OnRpcDisconnected; } catch { }
		try { _rpc.Dispose(); } catch { }
		try { _pipe.Dispose(); } catch { }
	}

	private static ProcessStartInfo CreateStartInfo(string dotnetPath,
													string serverPath,
													string serverDir,
													string pipeName,
													McpServerSessionOptions options)
		=> new()
		{
			FileName = dotnetPath,
			Arguments = $"\"{serverPath}\" --pipe \"{pipeName}\" --port {options.Port} --name \"{options.ServerName}\"",
			WorkingDirectory = serverDir,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true
		};

	private static string? ResolveDotnetPath()
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (string.IsNullOrWhiteSpace(directory)) continue;

			var candidate = Path.Combine(directory.Trim().Trim('"'), "dotnet.exe");
			if (File.Exists(candidate))
				return candidate;
		}

		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var fallback = Path.Combine(programFiles, "dotnet", "dotnet.exe");
		return File.Exists(fallback) ? fallback : null;
	}

	/// <summary>Consumes a task's fault so an abandoned operation is never left dangling.</summary>
	private static Task ObserveAsync(Task task)
		=> task.ContinueWith(static t => _ = t.Exception,
							 CancellationToken.None,
							 TaskContinuationOptions.ExecuteSynchronously,
							 TaskScheduler.Default);
}
