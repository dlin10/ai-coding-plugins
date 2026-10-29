using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using static RoslynMcpExtension.Cli.NativeMethods;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// The per-user daemon: holds the solutions every shim of this plugin version loads and answers each shim on its
/// own connection to one pipe.
/// </summary>
internal static class DaemonServer
{
	/// <summary>About a minute, kept under the 60 seconds Codex waits on a tool call unless configured otherwise.</summary>
	private static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(50);

	/// <summary>
	/// Serves the pipe until the daemon has held no solution for <paramref name="exitAfter"/>; returns at once when another
	/// daemon already owns it. See docs/adr/0009.
	/// </summary>
	/// <param name="pipeName">The pipe to listen on.</param>
	/// <param name="unloadAfter">How long a solution stays loaded without calls.</param>
	/// <param name="exitAfter">How long the daemon runs on once it holds no solution.</param>
	public static async Task<int> RunAsync(string pipeName, TimeSpan unloadAfter, TimeSpan exitAfter)
	{
		NamedPipeServerStream pipe;
		try
		{
			// The first instance decides which of two daemons started at once serves the pipe: the other exits.
			pipe = CreatePipe(pipeName, PipeOptions.FirstPipeInstance);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"Another daemon already serves {pipeName}: {ex.Message}");
			return 0;
		}

		MSBuildLocator.RegisterDefaults();
		using var registry = new SolutionRegistry(new MsBuildSolutionLoader(new PackageRestore()), TimeProvider.System, unloadAfter, exitAfter);
		var daemon = new DaemonInfo
		{
			ProcessId = Environment.ProcessId,
			Version = ExtensionVersion.Value,
			ExitsWithJob = ExitsWithJob()
		};

		while (true)
		{
			// Idle, the daemon stops listening and exits; a shim whose connection breaks starts a daemon on its next call.
			var connection = pipe.WaitForConnectionAsync();
			if (await Task.WhenAny(connection, registry.Idle).ConfigureAwait(false) != connection)
			{
				pipe.Dispose();
				return 0;
			}

			var connected = pipe;
			// The next instance exists before this one is served, so that a shim connecting meanwhile finds a listener.
			pipe = CreatePipe(pipeName, PipeOptions.None);
			try
			{
				await connection.ConfigureAwait(false);
			}
			catch (IOException)
			{
				// A client that connected and closed again before the wait began, as a probe for the daemon's process does.
				connected.Dispose();
				continue;
			}

			var rpc = JsonRpc.Attach(connected, new DaemonSession(registry, LoadWait, daemon));
			_ = rpc.Completion.ContinueWith(_ => connected.Dispose(), TaskScheduler.Default);
		}
	}

	/// <summary>An instance of the pipe that only the current Windows user can open.</summary>
	/// <param name="pipeName">The pipe's name.</param>
	/// <param name="options">Options beyond asynchronous and current-user-only.</param>
	private static NamedPipeServerStream CreatePipe(string pipeName, PipeOptions options)
		=> new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
		       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | options);

	/// <summary>
	/// Whether this process sits in a job that kills it when the job closes, which is where a shim starts the
	/// daemon when its host refuses breakaway.
	/// </summary>
	private static bool ExitsWithJob()
	{
		if (!IsProcessInJob(CurrentProcess, IntPtr.Zero, out var inJob) || !inJob)
			return false;
		return QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformationClass, out var limits,
		                                 Marshal.SizeOf<JobObjectExtendedLimitInformation>(), IntPtr.Zero)
		       && (limits.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) != 0;
	}
}
