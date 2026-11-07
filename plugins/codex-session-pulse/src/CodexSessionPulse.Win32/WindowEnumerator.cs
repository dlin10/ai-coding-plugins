using Microsoft.Win32.SafeHandles;

namespace CodexSessionPulse.Win32;

/// <summary>Reads the live top-level windows and the native state of one window.</summary>
public static class WindowEnumerator
{
    /// <summary>
    /// Lists the visible, unowned top-level windows in z-order, top first. Invisible service windows
    /// (most of Codex Desktop's 19–21 top-level windows) are skipped before any process is opened.
    /// </summary>
    /// <param name="readTitles">Whether to read each window's title; without it the titles are empty.</param>
    /// <returns>The candidates; the executable path is read once per process and every process handle is closed.</returns>
    public static IReadOnlyList<WindowCandidate> EnumerateTopLevel(bool readTitles = true)
    {
        List<(nint Hwnd, uint ProcessId)> windows = [];

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) == 0)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                windows.Add((hwnd, processId));
            }

            return true;
        }, 0);

        Dictionary<uint, string?> paths = [];
        List<WindowCandidate> candidates = new(windows.Count);

        foreach ((nint hwnd, uint processId) in windows)
        {
            if (!paths.TryGetValue(processId, out string? path))
            {
                path = GetExecutablePath(processId);
                paths[processId] = path;
            }

            candidates.Add(new WindowCandidate(hwnd, (int)processId, path, readTitles ? GetTitle(hwnd) : string.Empty, IsVisible: true,
                                               IsCloaked(hwnd), HasOwner: false));
        }

        return candidates;
    }

    /// <summary>
    /// Lists the Codex Desktop windows in z-order, top first. Titles are read only for windows that already match the
    /// process and path rule, so other applications' window titles are never read.
    /// </summary>
    /// <param name="options">The detection rule.</param>
    /// <returns>The matching windows.</returns>
    public static IReadOnlyList<WindowCandidate> EnumerateCodexWindows(WindowDetectorOptions options)
    {
        WindowDetectorOptions anyTitle = options with { TitleFilter = null };

        return EnumerateTopLevel(readTitles: false)
            .Where(candidate => CodexWindowFilter.IsCodexWindow(candidate, anyTitle))
            .Select(candidate => candidate with { Title = GetTitle(candidate.Hwnd) })
            .Where(candidate => CodexWindowFilter.IsCodexWindow(candidate, options))
            .ToList();
    }

    /// <summary>Reads the full executable path of a process.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The path, or <see langword="null"/> when the process cannot be opened (it exited or is elevated).</returns>
    internal static unsafe string? GetExecutablePath(uint processId)
    {
        using SafeProcessHandle process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);

        if (process.IsInvalid)
        {
            return null;
        }

        char* buffer = stackalloc char[1024];
        int size = 1024;

        return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    /// <summary>Gets the id of the process that owns a window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>The process id, or 0 for an invalid window.</returns>
    public static int GetProcessId(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);

        return (int)processId;
    }

    /// <summary>Reads a window title.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>The title, or an empty string.</returns>
    private static unsafe string GetTitle(nint hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);

        if (length <= 0)
        {
            return string.Empty;
        }

        char* buffer = stackalloc char[Math.Min(length, 512) + 1];
        int copied = NativeMethods.GetWindowText(hwnd, buffer, Math.Min(length, 512) + 1);

        return new string(buffer, 0, Math.Max(copied, 0));
    }

    /// <summary>Checks whether DWM cloaks a window (another virtual desktop, a suspended UWP frame).</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns><see langword="true"/> when it is cloaked.</returns>
    private static bool IsCloaked(nint hwnd)
    {
        return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }
}
