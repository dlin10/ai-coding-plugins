using System.Runtime.InteropServices;

namespace CodexSessionPulse.Win32;

/// <summary>How a detached process was started.</summary>
/// <param name="Error">0 on success, otherwise the Win32 error of the last attempt.</param>
/// <param name="BrokeAway">Whether the process was started outside the caller's job (<c>CREATE_BREAKAWAY_FROM_JOB</c>).</param>
/// <param name="ProcessId">The id of the started process, or 0 when none was started.</param>
public sealed record DetachedStart(int Error, bool BrokeAway, int ProcessId);

/// <summary>The job object the current process runs in, for the <c>exitsWithJob</c> diagnostic (spec §4.4 item 5).</summary>
/// <param name="InJob">Whether the process is in any job.</param>
/// <param name="LimitFlags">The limit flags of the innermost job, or 0 outside a job or when they cannot be read.</param>
public sealed record JobMembership(bool InJob, uint LimitFlags)
{
    /// <summary>Gets whether closing the job kills this process (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>).</summary>
    public bool ExitsWithJob => InJob && (LimitFlags & JobProcess.KillOnJobClose) != 0;

    /// <summary>Gets whether a child may leave the job (<c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>).</summary>
    public bool BreakawayAllowed => InJob && (LimitFlags & JobProcess.BreakawayOk) != 0;
}

/// <summary>
/// Starts the overlay so that it outlives the hook that starts it, with the launch pattern of roslyn-mcp ADR 0004/0010:
/// <c>CREATE_BREAKAWAY_FROM_JOB</c> first and inside the job on <c>ERROR_ACCESS_DENIED</c>, no inherited handles, and a
/// short-lived starter. Here the hook itself is the starter: it exits right after, so the overlay's parent is gone.
/// </summary>
/// <remarks>
/// Measured in docs/compatibility.md §4: Codex runs every hook in its own job with flags <c>0x2800</c>
/// (kill on close, breakaway allowed), and a child that stays in that job still outlives the hook, the thread and the app.
/// Breakaway is taken anyway, so a later Codex that does close the job cannot take the overlay with it.
/// </remarks>
public static partial class JobProcess
{
    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.</summary>
    public const uint KillOnJobClose = 0x2000;

    /// <summary><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>.</summary>
    public const uint BreakawayOk = 0x800;

    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const int ErrorAccessDenied = 5;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private static readonly nint CurrentProcess = -1;

    /// <summary>Starts a process outside the caller's job when the job allows it, inside it otherwise.</summary>
    /// <param name="applicationPath">The full path of the executable.</param>
    /// <param name="arguments">The arguments, already quoted.</param>
    /// <param name="workingDirectory">The working directory; never the user's project.</param>
    /// <returns>How it was started.</returns>
    public static DetachedStart StartDetached(string applicationPath, string arguments, string workingDirectory)
    {
        string commandLine = $"\"{applicationPath}\" {arguments}";
        int error = Create(commandLine, workingDirectory, CreateNoWindow | CreateBreakawayFromJob, out int processId);

        if (error != ErrorAccessDenied)
        {
            return new DetachedStart(error, error == 0, processId);
        }

        error = Create(commandLine, workingDirectory, CreateNoWindow, out processId);

        return new DetachedStart(error, false, processId);
    }

    /// <summary>Reads the job the current process runs in.</summary>
    /// <returns>The membership; outside a job, or when the job cannot be queried, the flags are 0.</returns>
    public static JobMembership Current()
    {
        if (!IsProcessInJob(CurrentProcess, 0, out int inJob) || inJob == 0)
        {
            return new JobMembership(false, 0);
        }

        // A zero job handle queries the innermost job of the calling process.
        return QueryInformationJobObject(0, JobObjectExtendedLimitInformationClass, out JobObjectExtendedLimitInformation limits,
                                         Marshal.SizeOf<JobObjectExtendedLimitInformation>(), 0)
            ? new JobMembership(true, limits.BasicLimitInformation.LimitFlags)
            : new JobMembership(true, 0);
    }

    /// <summary>Starts a process with no inherited handles: one holding the hook's stdout would keep Codex waiting for it.</summary>
    /// <param name="commandLine">The full command line.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="flags">The creation flags.</param>
    /// <param name="processId">The id of the started process.</param>
    /// <returns>0, or the Win32 error.</returns>
    private static unsafe int Create(string commandLine, string workingDirectory, uint flags, out int processId)
    {
        StartupInfo startup = new() { Size = sizeof(StartupInfo) };
        char[] mutableCommandLine = (commandLine + '\0').ToCharArray();

        fixed (char* line = mutableCommandLine)
        {
            if (!CreateProcess(null, line, 0, 0, false, flags, 0, workingDirectory, ref startup, out ProcessInformation process))
            {
                processId = 0;

                return Marshal.GetLastPInvokeError();
            }

            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            processId = process.ProcessId;

            return 0;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(string? applicationName,
                                                     char* commandLine,
                                                     nint processAttributes,
                                                     nint threadAttributes,
                                                     [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
                                                     uint creationFlags,
                                                     nint environment,
                                                     string? currentDirectory,
                                                     ref StartupInfo startupInfo,
                                                     out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, out int result);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int infoClass, out JobObjectExtendedLimitInformation info,
                                                          int length, nint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
