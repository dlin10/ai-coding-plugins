using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RoslynMcpExtension.Cli;

/// <summary>The Win32 calls that start a process outside its parent's job and tell a process which job it is in.</summary>
internal static class NativeMethods
{
	public const uint CreateBreakawayFromJob = 0x01000000;
	public const uint CreateNoWindow = 0x08000000;
	public const int ErrorAccessDenied = 5;
	public const uint JobObjectLimitKillOnJobClose = 0x2000;
	public const int JobObjectExtendedLimitInformationClass = 9;

	/// <summary>GetCurrentProcess's pseudo handle, valid without being opened or closed.</summary>
	public static readonly IntPtr CurrentProcess = new(-1);

	[StructLayout(LayoutKind.Sequential)]
	public struct StartupInfo
	{
		public int cb;
		public IntPtr lpReserved;
		public IntPtr lpDesktop;
		public IntPtr lpTitle;
		public int dwX;
		public int dwY;
		public int dwXSize;
		public int dwYSize;
		public int dwXCountChars;
		public int dwYCountChars;
		public int dwFillAttribute;
		public int dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public IntPtr lpReserved2;
		public IntPtr hStdInput;
		public IntPtr hStdOutput;
		public IntPtr hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct ProcessInformation
	{
		public IntPtr hProcess;
		public IntPtr hThread;
		public int dwProcessId;
		public int dwThreadId;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct JobObjectBasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
	                                        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
	                                        string? currentDirectory, ref StartupInfo startupInfo,
	                                        out ProcessInformation processInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

	// A zero job queries the innermost job of the calling process.
	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JobObjectExtendedLimitInformation info,
	                                                    int length, IntPtr returnLength);
}
