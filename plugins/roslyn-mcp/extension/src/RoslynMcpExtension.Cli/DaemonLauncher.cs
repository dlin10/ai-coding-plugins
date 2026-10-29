using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static RoslynMcpExtension.Cli.NativeMethods;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Starts the daemon from this same executable, through a <c>start-daemon</c> process that exits once the daemon
/// exists. A host ends an MCP server by killing its process tree, which it finds by parent process ID, as Claude Code
/// does with <c>taskkill /T /F</c>; a daemon whose parent has exited is in no such tree. The daemon starts outside the
/// host's job, so that it and the solutions it holds outlive the session that started it; inside the job only where
/// the job refuses breakaway, as under codex exec, whose job then kills the daemon at the end of the turn. See
/// docs/adr/0010.
/// </summary>
internal static class DaemonLauncher
{
	/// <summary>How long the shim waits for <c>start-daemon</c>, a cold start of the executable included.</summary>
	private static readonly TimeSpan StarterTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Starts a daemon listening on <paramref name="pipeName"/> and returns once the process exists and the
	/// <c>start-daemon</c> process that started it has exited.
	/// </summary>
	/// <param name="pipeName">The pipe the daemon is to listen on.</param>
	public static void Start(string pipeName)
	{
		var error = Run($"\"{Environment.ProcessPath}\" start-daemon --pipe \"{pipeName}\"");
		if (error != 0)
			throw new Win32Exception(error, $"Could not start the Roslyn MCP daemon: {new Win32Exception(error).Message}");
	}

	/// <summary>
	/// What <c>start-daemon</c> does: starts the daemon outside this process's job, or inside it where the job refuses
	/// breakaway, and returns without waiting for it.
	/// </summary>
	/// <param name="pipeName">The pipe the daemon is to listen on.</param>
	/// <returns>Zero once the daemon process exists; otherwise the Win32 error, which the process exits with.</returns>
	public static int StartDaemon(string pipeName)
	{
		var commandLine = $"\"{Environment.ProcessPath}\" daemon --pipe \"{pipeName}\"";
		var error = Create(commandLine, CreateNoWindow | CreateBreakawayFromJob, out var daemon);
		if (error == ErrorAccessDenied)
			error = Create(commandLine, CreateNoWindow, out daemon);
		if (error == 0)
			Close(daemon);
		return error;
	}

	/// <summary>Runs a process to its end and returns its exit code, or the Win32 error that kept it from running.</summary>
	/// <param name="commandLine">The executable and its arguments.</param>
	private static int Run(string commandLine)
	{
		var error = Create(commandLine, CreateNoWindow, out var process);
		if (error != 0)
			return error;
		try
		{
			if (WaitForSingleObject(process.hProcess, (uint)StarterTimeout.TotalMilliseconds) != WaitObject0)
				return ErrorTimeout;
			return GetExitCodeProcess(process.hProcess, out var exitCode) ? (int)exitCode : Marshal.GetLastWin32Error();
		}
		finally
		{
			Close(process);
		}
	}

	/// <summary>
	/// Starts a process with no handle inherited: a daemon holding the shim's stdout would keep the host
	/// waiting for that stream to end after the shim has exited.
	/// </summary>
	/// <param name="commandLine">The executable and its arguments.</param>
	/// <param name="flags">The CreateProcess creation flags.</param>
	/// <param name="process">The new process's handles, for the caller to close.</param>
	/// <returns>Zero when the process was created; otherwise the Win32 error.</returns>
	private static int Create(string commandLine, uint flags, out ProcessInformation process)
	{
		var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
		if (CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
		                  AppContext.BaseDirectory, ref startup, out process))
			return 0;
		return Marshal.GetLastWin32Error();
	}

	/// <summary>Closes the handles CreateProcess returned, which leaves the process running.</summary>
	/// <param name="process">The process's handles.</param>
	private static void Close(ProcessInformation process)
	{
		CloseHandle(process.hThread);
		CloseHandle(process.hProcess);
	}
}
