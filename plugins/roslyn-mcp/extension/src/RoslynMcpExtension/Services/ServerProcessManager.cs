using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Services;

internal enum ServerStartFailureKind
{
	None,
	DotnetNotFound,
	ProcessExited,
	ReadinessTimeout,
	ProcessStartFailed
}

internal sealed class ServerStartResult(bool succeeded, ServerStartFailureKind failureKind, string message)
{
	public bool Succeeded { get; } = succeeded;
	public ServerStartFailureKind FailureKind { get; } = failureKind;
	public string Message { get; } = message;
}

internal sealed class ServerProcessManager(OutputLogger? logger)
{
	private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
	private readonly object _processGate = new();
	private Process? _serverProcess;

	public bool IsRunning
	{
		get
		{
			lock (_processGate)
				return _serverProcess is { HasExited: false };
		}
	}

	public async Task<ServerStartResult> StartAsync(string pipeName,
	                                                 int port,
	                                                 string serverName,
	                                                 Task readyTask)
	{
		if (IsRunning)
			return new ServerStartResult(true, ServerStartFailureKind.None, "Server is already running.");

		var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		var serverDir = Path.Combine(extensionDir!, "McpServer");
		var serverPath = Path.Combine(serverDir, "RoslynMcpExtension.Server.dll");

		if (!File.Exists(serverPath))
		{
			var message = $"Server executable not found at expected path: {serverPath}";
			logger?.Log(message);
			return new ServerStartResult(false, ServerStartFailureKind.ProcessStartFailed, message);
		}

		var dotnetPath = ResolveDotnetPath();
		if (dotnetPath == null)
		{
			const string message = "Unable to start the Roslyn MCP server: .NET 10 is required, but dotnet.exe was not found on PATH or under %ProgramFiles%\\dotnet.";
			logger?.Log(message);
			return new ServerStartResult(false, ServerStartFailureKind.DotnetNotFound, message);
		}

		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = dotnetPath,
				Arguments = $"\"{serverPath}\" --pipe \"{pipeName}\" --port {port} --name \"{serverName}\"",
				WorkingDirectory = serverDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true
			},
			EnableRaisingEvents = true
		};
		var exitSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

		process.Exited += (_, _) =>
		{
			var exitCode = process.ExitCode;
			exitSource.TrySetResult(exitCode);
			logger?.Log($"MCP Server process exited with code {exitCode}");

			var disposeProcess = false;
			lock (_processGate)
			{
				if (ReferenceEquals(_serverProcess, process))
				{
					_serverProcess = null;
					disposeProcess = true;
				}
			}

			if (disposeProcess)
				process.Dispose();
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
				logger?.Log($"[MCP Server] {e.Data}");
		};

		try
		{
			lock (_processGate)
				_serverProcess = process;

			process.Start();
			process.BeginErrorReadLine();
		}
		catch (Exception ex)
		{
			lock (_processGate)
			{
				if (ReferenceEquals(_serverProcess, process))
					_serverProcess = null;
			}

			process.Dispose();
			var message = $"MCP Server process failed to start: {ex.Message}";
			logger?.Log(message);
			return new ServerStartResult(false, ServerStartFailureKind.ProcessStartFailed, message);
		}

		var timeoutTask = Task.Delay(ReadinessTimeout);
		#pragma warning disable VSTHRD003 // These tasks intentionally represent external process/RPC completion.
		var completedTask = await Task.WhenAny(readyTask, exitSource.Task, timeoutTask);
		#pragma warning restore VSTHRD003

		if (completedTask == readyTask)
		{
			await readyTask;
			if (exitSource.Task.IsCompleted)
			{
				var exitCode = await exitSource.Task;
				var exitMessage = $"MCP Server exited while becoming ready (exit code {exitCode}). Port {port} may already be in use.";
				logger?.Log(exitMessage);
				return new ServerStartResult(false, ServerStartFailureKind.ProcessExited, exitMessage);
			}

			logger?.Log($"MCP Server started on http://localhost:{port}/mcp (pipe: {pipeName})");
			return new ServerStartResult(true, ServerStartFailureKind.None, "Server is ready.");
		}

		if (completedTask == exitSource.Task)
		{
			var exitCode = await exitSource.Task;
			var message = $"MCP Server exited before becoming ready (exit code {exitCode}). Port {port} may already be in use.";
			logger?.Log(message);
			return new ServerStartResult(false, ServerStartFailureKind.ProcessExited, message);
		}

		await StopAsync();
		var timeoutMessage = $"MCP Server did not become ready within {ReadinessTimeout.TotalSeconds:0} seconds. Port {port} may already be in use.";
		logger?.Log(timeoutMessage);
		return new ServerStartResult(false, ServerStartFailureKind.ReadinessTimeout, timeoutMessage);
	}

	public async Task StopAsync()
	{
		Process? process;
		lock (_processGate)
		{
			process = _serverProcess;
			_serverProcess = null;
		}

		if (process == null) return;

		logger?.Log("Stopping MCP Server...");

		try
		{
			if (!process.HasExited)
			{
				process.Kill();
				process.WaitForExit(5000);
			}
		}
		catch (Exception ex)
		{
			logger?.Log($"Error stopping server: {ex.Message}");
		}
		finally
		{
			process.Dispose();
		}

		logger?.Log("MCP Server stopped");
		await Task.CompletedTask;
	}

	internal static string? ResolveDotnetPath()
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
}
