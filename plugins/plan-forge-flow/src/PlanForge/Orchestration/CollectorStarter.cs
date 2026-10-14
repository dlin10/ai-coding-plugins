using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PlanForge.Orchestration;

/// <summary>How a collector is launched: the program and the arguments that come before the collector's own.</summary>
/// <param name="FileName">The program's full path.</param>
/// <param name="Arguments">Arguments before <c>--telemetry-collector</c>: the entry assembly under the <c>dotnet</c> host.</param>
internal sealed record CollectorCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>What starting a collector came to.</summary>
internal enum CollectorStartOutcome
{
    /// <summary>A collector process was created, detached from the server.</summary>
    Started,

    /// <summary>
    /// The server's job object refuses breakaway, and the process could not be created inside it either.
    /// </summary>
    CannotDetach,

    /// <summary>No relaunch command is known, or the process could not be created for another reason.</summary>
    Failed,
}

/// <summary>The starter's answer.</summary>
/// <param name="Outcome">What starting came to.</param>
/// <param name="ProcessId">The collector's process id, when one was started.</param>
/// <param name="InsideJob">Whether the collector runs inside the server's job object rather than outside it.</param>
/// <param name="KillsOnClose">
/// Once breakaway was refused, whether the server's job object ends its processes when its last handle
/// closes — so a collector inside it ends with it — or null when its limits could not be read. A report
/// only: it never decides whether a collector is started.
/// </param>
internal sealed record CollectorStart(CollectorStartOutcome Outcome, int? ProcessId = null, bool InsideJob = false,
                                      bool? KillsOnClose = null)
{
    /// <summary>
    /// The log fields saying where the collector runs: <c>insideJob</c>, and for one inside the server's
    /// job object <c>jobKillsOnClose</c> — <c>true</c>, <c>false</c> or <c>unreadable</c>.
    /// </summary>
    public (string Name, string? Value)[] JobFields() =>
        InsideJob
            ? [("insideJob", "true"), ("jobKillsOnClose", KillsOnClose switch { true => "true", false => "false", null => "unreadable" })]
            : [("insideJob", "false")];
}

/// <summary>Creates a process without inheriting handles; the seam a test stands a job object behind.</summary>
/// <param name="fileName">The program's full path.</param>
/// <param name="commandLine">The whole command line, program included.</param>
/// <param name="flags">The <c>CreateProcessW</c> creation flags.</param>
/// <param name="processId">The new process's id, when it was created.</param>
/// <param name="error">The Win32 error, when it was not.</param>
internal delegate bool ProcessCreator(string fileName, string commandLine, int flags, out int processId, out int error);

/// <summary>
/// Starts a Telemetry collector detached from the server (R9): it inherits no handle of the server,
/// standard pipes included, shows no window, and keeps running after the server exits.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.ProcessStartInfo"/> cannot refuse handle inheritance, so the process
/// is created through <c>CreateProcessW</c> with <c>bInheritHandles</c> false. It breaks away from the
/// server's job object when that permits it, and is started inside it otherwise — as under Claude
/// desktop and Codex desktop, whose job objects refuse breakaway, and under <c>codex exec</c>, whose job
/// object also ends its processes when it closes. A collector inside such a job object ends with it;
/// the next phase opening of any server that chose the telemetry export starts a new one (R9). The job
/// object's limits are read only to report that.
/// </remarks>
internal static partial class CollectorStarter
{
    internal const int CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    internal const int ERROR_ACCESS_DENIED = 5;

    private const string PROGRAM = "planforge.exe";
    private const string ASSEMBLY = "planforge.dll";
    private const string DOTNET = "dotnet.exe";
    private const int CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    /// <summary>
    /// The running server's own program: <paramref name="processPath"/> when it is <c>planforge.exe</c>,
    /// or the <c>dotnet</c> host followed by the entry assembly when the server runs as
    /// <c>dotnet planforge.dll</c>; null for anything else.
    /// </summary>
    /// <param name="processPath">The server process's executable.</param>
    /// <param name="entryAssembly">The entry assembly's path.</param>
    public static CollectorCommand? RelaunchCommand(string? processPath, string? entryAssembly)
    {
        var program = Path.GetFileName(processPath);
        if (string.Equals(program, PROGRAM, StringComparison.OrdinalIgnoreCase)) return new CollectorCommand(processPath!, []);
        return string.Equals(program, DOTNET, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(entryAssembly), ASSEMBLY, StringComparison.OrdinalIgnoreCase)
            ? new CollectorCommand(processPath!, [entryAssembly!])
            : null;
    }

    /// <summary>Starts a collector on <paramref name="port"/>.</summary>
    /// <param name="port">The loopback port the collector serves.</param>
    /// <param name="command">The program to launch; the running server's own when null.</param>
    /// <param name="grace">The test-only grace; the collector's default when null.</param>
    /// <param name="create">Creates the process; <c>CreateProcessW</c> when null.</param>
    /// <param name="killOnJobClose">
    /// Whether the calling process's job object ends its processes on close, null when its limits cannot
    /// be read, for the report; <c>QueryInformationJobObject</c> when null.
    /// </param>
    public static CollectorStart Start(int port, CollectorCommand? command = null, TimeSpan? grace = null,
                                       ProcessCreator? create = null, Func<bool?>? killOnJobClose = null)
    {
        // Under the dotnet host the entry assembly sits in the base directory; a single-file build has
        // no assembly path, but then the process itself is planforge.exe.
        command ??= RelaunchCommand(Environment.ProcessPath, Path.Combine(AppContext.BaseDirectory, ASSEMBLY));
        if (command is null || !OperatingSystem.IsWindows()) return new CollectorStart(CollectorStartOutcome.Failed);

        List<string> arguments = [.. command.Arguments, "--telemetry-collector", "--port", port.ToString(CultureInfo.InvariantCulture)];
        if (grace is { } seconds) arguments.AddRange(["--grace-seconds", ((int)seconds.TotalSeconds).ToString(CultureInfo.InvariantCulture)]);
        var commandLine = string.Join(' ', [Quote(command.FileName), .. arguments.Select(Quote)]);
        create ??= CreateWithoutHandles;

        if (create(command.FileName, commandLine, CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB,
                   out var outside, out var error))
        {
            return new CollectorStart(CollectorStartOutcome.Started, outside);
        }

        if (error != ERROR_ACCESS_DENIED) return new CollectorStart(CollectorStartOutcome.Failed);

        // The job object refuses breakaway: start inside it, whether or not closing it ends the collector.
        var kills = (killOnJobClose ?? KillsOnJobClose)();
        return create(command.FileName, commandLine, CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP, out var inside, out _)
            ? new CollectorStart(CollectorStartOutcome.Started, inside, InsideJob: true, KillsOnClose: kills)
            : new CollectorStart(CollectorStartOutcome.CannotDetach, KillsOnClose: kills);
    }

    /// <summary>Creates a process through <c>CreateProcessW</c> with <c>bInheritHandles</c> false.</summary>
    /// <param name="fileName">The program's full path.</param>
    /// <param name="commandLine">The whole command line, program included.</param>
    /// <param name="flags">The creation flags.</param>
    /// <param name="processId">The new process's id, when it was created.</param>
    /// <param name="error">The Win32 error, when it was not.</param>
    private static bool CreateWithoutHandles(string fileName, string commandLine, int flags, out int processId, out int error)
    {
        // CreateProcessW may write into the command line, so it gets a buffer of its own.
        var buffer = Marshal.StringToHGlobalUni(commandLine);
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };

            // The system directory, so the collector holds no handle on the server's working directory.
            if (!CreateProcess(fileName, buffer, 0, 0, false, flags, 0, Environment.SystemDirectory, in startup, out var created))
            {
                error = Marshal.GetLastPInvokeError();
                processId = 0;
                return false;
            }

            CloseHandle(created.Thread);
            CloseHandle(created.Process);
            error = 0;
            processId = created.ProcessId;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Whether the calling process's own job object — the immediate one of a nested chain — ends its
    /// processes when its last handle closes; null when its limits cannot be read.
    /// </summary>
    private static bool? KillsOnJobClose() =>
        QueryInformationJobObject(0, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, out var limits, Marshal.SizeOf<ExtendedLimits>(), 0)
            ? (limits.LimitFlags & JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) != 0
            : null;

    /// <summary>One argument as the Windows command-line parser reads it back.</summary>
    /// <param name="argument">The argument.</param>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

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
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public nuint Affinity;
        public int PriorityClass;
        public int SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int informationClass, out ExtendedLimits information, int length,
                                                          nint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string applicationName, nint commandLine, nint processAttributes, nint threadAttributes,
                                              [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags, nint environment,
                                              string currentDirectory, in StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
