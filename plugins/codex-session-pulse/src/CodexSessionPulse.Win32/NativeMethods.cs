using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexSessionPulse.Win32;

/// <summary>The user32, dwmapi, kernel32 and shell32 calls the Win32 layer makes. Nothing outside this assembly calls them.</summary>
internal static partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExTopmost = 0x00000008;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExNoActivate = 0x08000000;
    internal const long WsExTransparent = 0x00000020;

    internal const uint GwOwner = 4;
    internal const uint GwHwndPrev = 3;
    internal const uint GaRootOwner = 3;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoOwnerZOrder = 0x0200;

    internal const int SwShowNoActivate = 4;

    internal const uint MonitorDefaultToNearest = 2;

    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal const uint WinEventOutOfContext = 0x0000;
    internal const uint WinEventSkipOwnProcess = 0x0002;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMinimizeStart = 0x0016;
    internal const uint EventSystemMinimizeEnd = 0x0017;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectHide = 0x8003;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const int ObjIdWindow = 0;
    internal const int ChildIdSelf = 0;

    internal static readonly nint HwndTop = 0;

    /// <summary>The callback of <see cref="EnumWindows"/>.</summary>
    /// <param name="hwnd">The enumerated window.</param>
    /// <param name="lParam">The caller's value.</param>
    /// <returns><see langword="true"/> to continue.</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    /// <summary>The callback of <see cref="SetWinEventHook"/>.</summary>
    /// <param name="hook">The hook handle.</param>
    /// <param name="eventType">The event.</param>
    /// <param name="hwnd">The window that raised it.</param>
    /// <param name="idObject">The object id.</param>
    /// <param name="idChild">The child id.</param>
    /// <param name="eventThread">The thread that raised it.</param>
    /// <param name="eventTimeMs">When it was raised.</param>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTimeMs);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    // DllImport, not LibraryImport: the source generator does not marshal delegates.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    internal static unsafe partial int GetWindowText(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref int size);
}
