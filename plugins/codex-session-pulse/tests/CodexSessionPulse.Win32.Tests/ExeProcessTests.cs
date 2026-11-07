using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>Starts the real <c>CodexSessionPulse.exe</c> and checks the two modes from outside.</summary>
public sealed class ExeProcessTests
{
    private const int TimeoutMs = 30_000;
    private const string DebugVariable = "CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES";
    private const string HomeVariable = "CODEX_SESSION_PULSE_HOME";

    /// <summary>A valid payload makes <c>--hook</c> exit 0 without ever loading WPF.</summary>
    [Fact]
    public void Hook_WithJsonOnStdin_ExitsZeroWithoutLoadingWpf()
    {
        (int exitCode, string stdout) = Run("--hook", "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"s1\"}", debug: true);

        string[] assemblies = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(0, exitCode);
        Assert.Contains("CodexSessionPulse", assemblies);
        Assert.DoesNotContain(assemblies, name => name.StartsWith("PresentationFramework", StringComparison.Ordinal));
        Assert.DoesNotContain(assemblies, name => name.StartsWith("PresentationCore", StringComparison.Ordinal));
        Assert.DoesNotContain(assemblies, name => name.StartsWith("WindowsBase", StringComparison.Ordinal));
    }

    /// <summary>Truncated and non-JSON input still exits 0.</summary>
    [Theory]
    [InlineData("{\"hook_event_name\":\"Sess")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Hook_WithBadInput_ExitsZero(string input)
    {
        (int exitCode, _) = Run("--hook", input, debug: false);

        Assert.Equal(0, exitCode);
    }

    /// <summary>Input beyond the 1 MiB cap is cut off and the hook still exits 0.</summary>
    [Fact]
    public void Hook_WithOversizedInput_ExitsZero()
    {
        (int exitCode, _) = Run("--hook", new string('x', 3 * 1024 * 1024), debug: false);

        Assert.Equal(0, exitCode);
    }

    /// <summary>The overlay with the development stub starts, writes its log under the redirected home and keeps running.</summary>
    [Fact]
    public void Overlay_WithStub_StartsAndKeepsRunning()
    {
        string home = Path.Combine(Path.GetTempPath(), "csp-overlay-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(home, "logs", "overlay.log");
        ProcessStartInfo startInfo = new(Exe, "--overlay --stub") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        startInfo.Environment[HomeVariable] = home;

        using Process process = Process.Start(startInfo)!;

        try
        {
            Stopwatch waited = Stopwatch.StartNew();

            while (!LogHas(log, "anchor ") && waited.ElapsedMilliseconds < TimeoutMs && !process.HasExited)
            {
                Thread.Sleep(100);
            }

            Assert.False(process.HasExited, "The overlay exited.");
            Assert.True(LogHas(log, "overlay start"), "The overlay did not log its start.");
            Assert.True(LogHas(log, "stub True"), "The overlay did not take --stub.");
            Assert.True(LogHas(log, "anchor "), "The window anchor reported no state.");
            Assert.False(LogHas(log, "ERROR"), "The overlay logged an error.");

            // Graceful exit, the same path as tray Exit, so no dead icon is left in the notification area.
            nint tray = FindTrayWindow(process.Id);
            Assert.NotEqual(0, tray);
            PostMessage(tray, WmClose, 0, 0);

            Assert.True(process.WaitForExit(TimeoutMs), "The overlay did not exit on WM_CLOSE.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(LogHas(log, "overlay stop"), "The overlay did not log its stop.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(TimeoutMs);
            }

            Directory.Delete(home, recursive: true);
        }
    }

    private const uint WmClose = 0x0010;

    /// <summary>Finds the hidden tray window of a process by its window class.</summary>
    /// <param name="processId">The overlay process.</param>
    /// <returns>The window, or 0 when there is none.</returns>
    private static nint FindTrayWindow(int processId)
    {
        for (nint window = FindWindowEx(0, 0, TrayIcon.WindowClassName, null); window != 0;
             window = FindWindowEx(0, window, TrayIcon.WindowClassName, null))
        {
            GetWindowThreadProcessId(window, out uint owner);

            if (owner == processId)
            {
                return window;
            }
        }

        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static string Exe => Path.Combine(typeof(ExeProcessTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "AppOutputDir").Value!, "CodexSessionPulse.exe");

    private static bool LogHas(string path, string text)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);

            return reader.ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static (int ExitCode, string Stdout) Run(string argument, string stdin, bool debug)
    {
        ProcessStartInfo startInfo = new(Exe, argument)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };

        if (debug)
        {
            startInfo.Environment[DebugVariable] = "1";
        }

        using Process process = Process.Start(startInfo)!;

        try
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The hook stops reading at its cap and may exit while the rest of an oversized payload is still being written.
        }

        string stdout = process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(TimeoutMs), "The process did not exit in time.");

        return (process.ExitCode, stdout);
    }
}
