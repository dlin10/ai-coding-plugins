using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>Every test class that starts the real exe shares this collection, so no two of them run at once.</summary>
[CollectionDefinition(Name)]
public sealed class ExeCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "exe";
}

/// <summary>
/// A scratch home for the real exe: its own settings, logs, mutex and pipe (the home path is hashed into their names),
/// and its own <c>CODEX_HOME</c>, so a test never touches the user's overlay or rollouts. Disposing ends every overlay the
/// test started.
/// </summary>
internal sealed class LifecycleHome : IDisposable
{
    private const string HomeVariable = "CODEX_SESSION_PULSE_HOME";
    private const uint WmClose = 0x0010;

    private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);
    private readonly List<int> _started = [];

    /// <summary>Creates the home with settings that match no window, unless a rule is given.</summary>
    /// <param name="processNames">The Codex process names the overlay looks for.</param>
    /// <param name="titleFilter">The title filter, or <see langword="null"/>.</param>
    public LifecycleHome(string[]? processNames = null, string? titleFilter = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "csp-life-" + Guid.NewGuid().ToString("N")[..12]);
        CodexHome = Path.Combine(Root, "codex");
        Directory.CreateDirectory(Path.Combine(CodexHome, "sessions"));
        File.WriteAllText(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["codexProcessNames"] = processNames ?? ["csp-no-such-process"],
            ["codexExecutablePathFilters"] = Array.Empty<string>(),
            ["codexWindowTitleFilter"] = titleFilter,
        }));
    }

    /// <summary>Gets the exe under test.</summary>
    public static string Exe => Path.Combine(typeof(LifecycleHome).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "AppOutputDir").Value!, "CodexSessionPulse.exe");

    /// <summary>Gets the home folder.</summary>
    public string Root { get; }

    /// <summary>Gets the <c>CODEX_HOME</c> the overlay reads rollouts from.</summary>
    public string CodexHome { get; }

    /// <summary>Gets the overlay log.</summary>
    public string OverlayLog => Path.Combine(Root, "logs", "overlay.log");

    /// <summary>Gets the hook log.</summary>
    public string HookLog => Path.Combine(Root, "logs", "hook.log");

    /// <summary>Copies a rollout fixture into today's day folder, where discovery looks.</summary>
    /// <param name="fileName">The fixture's file name.</param>
    public void AddRollout(string fileName)
    {
        string fixtures = typeof(LifecycleHome).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                                         .Single(attribute => attribute.Key == "FixturesRoot").Value!;
        DateTime today = DateTime.Now;
        string day = Path.Combine(CodexHome, "sessions", today.ToString("yyyy", CultureInfo.InvariantCulture),
                                  today.ToString("MM", CultureInfo.InvariantCulture), today.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(day);
        File.Copy(Path.Combine(fixtures, "rollouts", fileName), Path.Combine(day, fileName));
    }

    /// <summary>Starts the exe with this home.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="redirectStdin">Whether stdin is a pipe the test writes.</param>
    /// <returns>The process.</returns>
    public Process Start(string arguments, bool redirectStdin)
    {
        ProcessStartInfo startInfo = new(Exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            WorkingDirectory = Path.GetTempPath(),
        };
        startInfo.Environment[HomeVariable] = Root;
        startInfo.Environment["CODEX_HOME"] = CodexHome;
        Process process = Process.Start(startInfo)!;
        _started.Add(process.Id);

        return process;
    }

    /// <summary>Runs <c>--hook</c> with a payload and waits for it.</summary>
    /// <param name="payload">The stdin JSON.</param>
    /// <param name="noStart">Whether to pass <c>--no-start</c>.</param>
    /// <param name="job">A job to put the hook in before it reads stdin, or <see langword="null"/>.</param>
    /// <returns>The exit code and the time from start to exit.</returns>
    public (int ExitCode, long ElapsedMs) RunHook(string payload, bool noStart = false, KillOnCloseJob? job = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using Process process = Start(noStart ? "--hook --no-start" : "--hook", redirectStdin: true);
        job?.Assign(process);
        process.StandardInput.Write(payload);
        process.StandardInput.Close();

        Assert.True(process.WaitForExit(30_000), "The hook did not exit.");
        elapsed.Stop();

        return (process.ExitCode, elapsed.ElapsedMilliseconds);
    }

    /// <summary>Gets the overlays this test started that are still running.</summary>
    /// <returns>The processes.</returns>
    public List<Process> RunningOverlays()
    {
        List<Process> overlays = [];

        foreach (Process process in Process.GetProcessesByName("CodexSessionPulse"))
        {
            try
            {
                if (!process.HasExited && process.StartTime >= _startedAt
                    && string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                {
                    overlays.Add(process);
                    continue;
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // Exited meanwhile, or not ours to inspect.
            }

            process.Dispose();
        }

        return overlays;
    }

    /// <summary>Waits until the log holds a text a number of times.</summary>
    /// <param name="log">The log file.</param>
    /// <param name="text">The text.</param>
    /// <param name="count">How many times.</param>
    /// <param name="timeoutMs">How long to wait.</param>
    /// <returns>Whether it appeared in time.</returns>
    public static bool WaitForLog(string log, string text, int count = 1, int timeoutMs = 20_000)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (Count(log, text) < count)
        {
            if (waited.ElapsedMilliseconds > timeoutMs)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    /// <summary>Counts a text in a log.</summary>
    /// <param name="log">The log file.</param>
    /// <param name="text">The text.</param>
    /// <returns>The number of occurrences, 0 without the file.</returns>
    public static int Count(string log, string text)
    {
        try
        {
            using FileStream stream = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            string content = reader.ReadToEnd();
            int count = 0;

            for (int index = content.IndexOf(text, StringComparison.Ordinal); index >= 0;
                 index = content.IndexOf(text, index + text.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Ends an overlay the way tray Exit does, through <c>WM_CLOSE</c> to its tray window.</summary>
    /// <param name="overlay">The overlay.</param>
    /// <returns>Whether it exited with code 0.</returns>
    public static bool CloseGracefully(Process overlay)
    {
        for (nint window = FindWindowEx(0, 0, TrayIcon.WindowClassName, null); window != 0;
             window = FindWindowEx(0, window, TrayIcon.WindowClassName, null))
        {
            GetWindowThreadProcessId(window, out uint owner);

            if (owner == overlay.Id)
            {
                PostMessage(window, WmClose, 0, 0);

                if (!overlay.WaitForExit(20_000))
                {
                    return false;
                }

                try
                {
                    return overlay.ExitCode == 0;
                }
                catch (InvalidOperationException)
                {
                    // A process found by name, not started here: its exit code is not readable, its exit is enough.
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Ends every overlay of this home: the ones the test started, the ones the logs name (a hook's start, a bootstrap) and
    /// any found by name, since a process found right after its start may not list its modules yet.
    /// </summary>
    public void Dispose()
    {
        HashSet<int> pids = [.. _started, .. LoggedPids(HookLog, "started the overlay, pid "), .. LoggedPids(OverlayLog, "overlay bootstrap: pid ")];

        foreach (int pid in pids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);

                if (process.StartTime >= _startedAt && string.Equals(process.ProcessName, "CodexSessionPulse", StringComparison.OrdinalIgnoreCase))
                {
                    End(process);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        foreach (Process overlay in RunningOverlays())
        {
            End(overlay);
            overlay.Dispose();
        }

        for (int attempt = 0; attempt < 25 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void End(Process overlay)
    {
        if (!overlay.HasExited && !CloseGracefully(overlay) && !overlay.HasExited)
        {
            overlay.Kill();
            overlay.WaitForExit(10_000);
        }
    }

    private static IEnumerable<int> LoggedPids(string log, string marker)
    {
        if (!File.Exists(log))
        {
            yield break;
        }

        string content;

        using (FileStream stream = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(stream))
        {
            content = reader.ReadToEnd();
        }

        for (int index = content.IndexOf(marker, StringComparison.Ordinal); index >= 0;
             index = content.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            int start = index + marker.Length;
            int end = start;

            while (end < content.Length && char.IsAsciiDigit(content[end]))
            {
                end++;
            }

            if (int.TryParse(content.AsSpan(start, end - start), CultureInfo.InvariantCulture, out int pid))
            {
                yield return pid;
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>
/// A visible, unowned top-level window of the test process that a <see cref="LifecycleHome"/> configured with this process's
/// name and the window's title takes for a Codex window. It has its own thread and message loop.
/// </summary>
internal sealed class FakeCodexWindow : IDisposable
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WmClose = 0x0010;
    private const int SwMinimize = 6;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private nint _hwnd;

    /// <summary>Creates and shows the window.</summary>
    /// <param name="title">The window title.</param>
    public FakeCodexWindow(string title)
    {
        _thread = new Thread(() => Run(title)) { IsBackground = true };
        _thread.Start();
        Assert.True(_created.Wait(10_000), "The fake window was not created.");
        Assert.NotEqual(0, _hwnd);
    }

    /// <summary>Gets the name of the process that owns the window, as the overlay's rule sees it.</summary>
    public static string ProcessName => Path.GetFileNameWithoutExtension(Environment.ProcessPath!);

    /// <summary>Gets the window handle.</summary>
    public nint Hwnd => _hwnd;

    /// <summary>Minimizes the window; it stays a Codex window.</summary>
    public void Minimize() => ShowWindow(_hwnd, SwMinimize);

    /// <summary>Closes the window and waits for its thread.</summary>
    public void Dispose()
    {
        if (_hwnd != 0 && IsWindow(_hwnd))
        {
            PostMessage(_hwnd, WmClose, 0, 0);
        }

        _thread.Join(10_000);
        _created.Dispose();
    }

    private void Run(string title)
    {
        _hwnd = CreateWindowEx(0, "STATIC", title, WsOverlappedWindow | WsVisible, 100, 100, 640, 480, 0, 0, 0, 0);
        _created.Set();

        while (_hwnd != 0 && IsWindow(_hwnd))
        {
            while (PeekMessage(out Msg message, 0, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width,
                                              int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>
/// A job that kills every process in it once closed, like the one Codex runs each hook in (flags 0x2800 with breakaway,
/// docs/compatibility.md §4), or the stricter one Codex gives MCP servers (0x2000, no breakaway). From roslyn-mcp's tests.
/// </summary>
/// <param name="allowBreakaway">Whether a process may start a child outside the job.</param>
internal sealed class KillOnCloseJob(bool allowBreakaway) : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x800;
    private const int ExtendedLimitInformation = 9;

    private nint _handle = Create(allowBreakaway);

    /// <summary>Puts a process in the job; its later children start in the job too.</summary>
    /// <param name="process">The process.</param>
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Closes the job, which kills every process still in it.</summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    private static nint Create(bool allowBreakaway)
    {
        nint handle = CreateJobObject(0, null);

        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        ExtendedLimits limits = new() { LimitFlags = KillOnJobClose | (allowBreakaway ? BreakawayOk : 0) };

        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
