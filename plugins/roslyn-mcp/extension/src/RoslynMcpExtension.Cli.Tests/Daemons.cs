using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>Where the executable under test is, and the daemons the tests start with it.</summary>
internal static class Cli
{
	/// <summary>roslyn-mcp.exe in the Cli project's own output, built in the configuration these tests are.</summary>
	public static string ExecutablePath
	{
		get
		{
			var output = new DirectoryInfo(AppContext.BaseDirectory);
			var configuration = output.Parent!;
			var sources = configuration.Parent!.Parent!.Parent!;
			return Path.Combine(sources.FullName, "RoslynMcpExtension.Cli", "bin", configuration.Name, output.Name, "roslyn-mcp.exe");
		}
	}
}

/// <summary>Each test gets a pipe of its own, and leaves no daemon behind on it.</summary>
internal static class Daemons
{
	public static string NewPipeName() => "roslyn-mcp-test-" + Guid.NewGuid().ToString("N");

	/// <summary>The process serving a pipe, or null when nothing answers on it.</summary>
	/// <param name="pipeName">The pipe's name.</param>
	public static int? ServerProcessId(string pipeName)
	{
		using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
		try
		{
			client.Connect(500);
		}
		catch (TimeoutException)
		{
			return null;
		}

		if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
			throw new Win32Exception(Marshal.GetLastWin32Error());
		return (int)processId;
	}

	/// <summary>Kills the daemon serving a pipe, by its exact process ID, with the build hosts it started.</summary>
	/// <param name="pipeName">The pipe's name.</param>
	public static void Kill(string pipeName)
	{
		if (ServerProcessId(pipeName) is not { } processId) return;
		using var daemon = Process.GetProcessById(processId);
		daemon.Kill(entireProcessTree: true);
		daemon.WaitForExit(10_000);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
