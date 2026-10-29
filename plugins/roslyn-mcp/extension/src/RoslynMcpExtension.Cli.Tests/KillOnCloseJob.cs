using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static RoslynMcpExtension.Cli.NativeMethods;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// A job that kills every process in it once closed. Like the one codex exec puts its MCP servers in, it refuses
/// breakaway; like claude -p's, it may allow it.
/// </summary>
/// <param name="allowBreakaway">Let a process start a child outside the job, as claude -p's job does.</param>
internal sealed class KillOnCloseJob(bool allowBreakaway = false) : IDisposable
{
	private const uint JobObjectLimitBreakawayOk = 0x800;

	private IntPtr _handle = Create(allowBreakaway);

	private static IntPtr Create(bool allowBreakaway)
	{
		var handle = CreateJobObject(IntPtr.Zero, null);
		if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
		var flags = JobObjectLimitKillOnJobClose | (allowBreakaway ? JobObjectLimitBreakawayOk : 0);
		var limits = new JobObjectExtendedLimitInformation
		{
			BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = flags }
		};
		if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref limits,
		                             Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
			throw new Win32Exception(Marshal.GetLastWin32Error());
		return handle;
	}

	/// <summary>Puts a process in the job; the processes it starts from then on start in the job too.</summary>
	/// <param name="process">The process to put in the job.</param>
	public void Assign(Process process)
	{
		if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
	}

	/// <summary>Closes the job, which kills every process still in it.</summary>
	public void Dispose()
	{
		if (_handle == IntPtr.Zero) return;
		CloseHandle(_handle);
		_handle = IntPtr.Zero;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
