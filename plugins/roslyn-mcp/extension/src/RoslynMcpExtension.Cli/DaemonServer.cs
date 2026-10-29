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

	/// <summary>Serves the pipe until the process is ended; returns at once when another daemon already owns it.</summary>
	/// <param name="pipeName">The pipe to listen on.</param>
	public static async Task<int> RunAsync(string pipeName)
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
		var registry = new SolutionRegistry(new MsBuildSolutionLoader());
		var daemon = new DaemonInfo
		{
			ProcessId = Environment.ProcessId,
			Version = ExtensionVersion.Value,
			ExitsWithJob = ExitsWithJob()
		};

		while (true)
		{
			await pipe.WaitForConnectionAsync().ConfigureAwait(false);
			var connected = pipe;
			// The next instance exists before this one is served, so that a shim connecting meanwhile finds a listener.
			pipe = CreatePipe(pipeName, PipeOptions.None);
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
