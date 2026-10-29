using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static RoslynMcpExtension.Cli.NativeMethods;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Starts the daemon from this same executable. Outside the host's job, so that the daemon and the solutions it
/// holds outlive the session that started it; inside the job only where the job refuses breakaway, as under
/// codex exec, whose job then kills the daemon at the end of the turn.
/// </summary>
internal static class DaemonLauncher
{
	/// <summary>Starts a daemon listening on <paramref name="pipeName"/> and returns once the process exists.</summary>
	/// <param name="pipeName">The pipe the daemon is to listen on.</param>
	public static void Start(string pipeName)
	{
		var commandLine = $"\"{Environment.ProcessPath}\" daemon --pipe \"{pipeName}\"";
		if (TryCreate(commandLine, CreateNoWindow | CreateBreakawayFromJob, out var error))
			return;
		if (error != ErrorAccessDenied || !TryCreate(commandLine, CreateNoWindow, out error))
			throw new Win32Exception(error, $"Could not start the Roslyn MCP daemon: {new Win32Exception(error).Message}");
	}

	/// <summary>
	/// Starts the process with no handle inherited: a daemon holding the shim's stdout would keep the host
	/// waiting for that stream to end after the shim has exited.
	/// </summary>
	/// <param name="commandLine">The executable and its arguments.</param>
	/// <param name="flags">The CreateProcess creation flags.</param>
	/// <param name="error">The Win32 error when the process was not created; zero otherwise.</param>
	private static bool TryCreate(string commandLine, uint flags, out int error)
	{
		var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
		if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
		                   AppContext.BaseDirectory, ref startup, out var process))
		{
			error = Marshal.GetLastWin32Error();
			return false;
		}

		CloseHandle(process.hThread);
		CloseHandle(process.hProcess);
		error = 0;
		return true;
	}
}
