using System.Runtime.InteropServices;

namespace CodexSessionPulse.Win32;

/// <summary>A tray menu command.</summary>
/// <param name="Text">The menu text, or <see langword="null"/> for a separator.</param>
/// <param name="Invoke">Runs the command on the UI thread.</param>
public sealed record TrayMenuItem(string? Text, Action Invoke);

/// <summary>
/// A notification-area icon through <c>Shell_NotifyIcon</c> with a native popup menu; no WinForms. It owns a hidden
/// window on the creating thread, so create and dispose it on the UI thread. The icon is drawn at runtime and re-added
/// when Explorer restarts (<c>TaskbarCreated</c>).
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const string WindowClass = "CodexSessionPulse.TrayWindow";
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNull = 0x0000;
    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmNoNotify = 0x80;

    private static readonly Dictionary<nint, TrayIcon> Instances = [];
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static bool s_classRegistered;

    private readonly IReadOnlyList<TrayMenuItem> _items;
    private readonly string _tooltip;
    private readonly nint _window;
    private nint _icon;

    /// <summary>Creates the hidden window and adds the icon.</summary>
    /// <param name="tooltip">The icon tooltip, at most 127 characters.</param>
    /// <param name="items">The menu commands in order.</param>
    public unsafe TrayIcon(string tooltip, IReadOnlyList<TrayMenuItem> items)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _items = items;
        EnsureClass();
        _window = CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */, WindowClass, WindowClass, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);

        if (_window == 0)
        {
            return;
        }

        Instances[_window] = this;
        _icon = CreatePulseIcon(GetSystemMetrics(49 /* SM_CXSMICON */));
        IsAdded = Add();
    }

    /// <summary>Raised when a menu command throws.</summary>
    public event Action<Exception>? CommandFailed;

    /// <summary>Gets a value indicating whether the shell accepted the icon (it may not, e.g. without Explorer).</summary>
    public bool IsAdded { get; private set; }

    /// <summary>Removes the icon and destroys the hidden window and the icon handle; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_window != 0 && Instances.Remove(_window))
        {
            NotifyIconData data = NewData();
            ShellNotifyIcon(NimDelete, ref data);
            DestroyWindow(_window);
            IsAdded = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private bool Add()
    {
        NotifyIconData data = NewData();
        data.Flags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.SetTip(_tooltip);

        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            return false;
        }

        data.VersionOrTimeout = NotifyIconVersion4;

        return ShellNotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData NewData() => new() { Size = Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1 };

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();

        for (int i = 0; i < _items.Count; i++)
        {
            AppendMenu(menu, _items[i].Text is null ? MfSeparator : MfString, (nuint)(i + 1), _items[i].Text);
        }

        GetCursorPos(out Point point);

        // Required so the menu closes when the user clicks elsewhere (KB135788); the overlay itself never takes focus.
        SetForegroundWindow(_window);
        int command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, point.X, point.Y, _window, 0);
        PostMessage(_window, WmNull, 0, 0);
        DestroyMenu(menu);

        if (command > 0 && command <= _items.Count)
        {
            try
            {
                _items[command - 1].Invoke();
            }
            catch (Exception exception)
            {
                // An exception must not unwind through the native window procedure: that would end the process.
                CommandFailed?.Invoke(exception);
            }
        }
    }

    private static unsafe void EnsureClass()
    {
        if (s_classRegistered)
        {
            return;
        }

        fixed (char* name = WindowClass)
        {
            WindowClassEx windowClass = new()
            {
                Size = Marshal.SizeOf<WindowClassEx>(),
                WindowProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProc,
                Instance = GetModuleHandle(null),
                ClassName = name,
            };

            s_classRegistered = RegisterClassEx(ref windowClass) != 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out TrayIcon? tray))
        {
            if (message == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: the low word of lParam is the mouse or keyboard event.
                uint trayEvent = (uint)(lParam & 0xFFFF);

                if (trayEvent is WmLButtonUp or WmRButtonUp or WmContextMenu)
                {
                    tray.ShowMenu();
                }

                return 0;
            }

            if (message == TaskbarCreated && TaskbarCreated != 0)
            {
                tray.IsAdded = tray.Add();

                return 0;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Draws a small ring-and-dot icon with per-pixel alpha, so no icon file has to ship.</summary>
    /// <param name="size">The icon size in pixels.</param>
    /// <returns>The icon handle; the caller destroys it.</returns>
    private static unsafe nint CreatePulseIcon(int size)
    {
        size = Math.Max(size, 16);
        BitmapInfoHeader header = new()
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };

        nint color = CreateDIBSection(0, ref header, 0, out nint bits, 0, 0);
        nint mask = CreateBitmap(size, size, 1, 1, 0);
        uint* pixels = (uint*)bits;
        double centre = (size - 1) / 2.0;
        double outer = size / 2.0 - 0.5;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));
                double ring = Math.Clamp(Math.Min(outer - distance, distance - (outer * 0.62)) + 0.5, 0, 1);
                double dot = Math.Clamp((outer * 0.34) - distance + 0.5, 0, 1);
                double alpha = Math.Max(ring, dot);
                uint a = (uint)(alpha * 255);

                // Teal #14B8A6, premultiplied is not required for icons; straight alpha.
                pixels[(y * size) + x] = (a << 24) | (0x14u << 16) | (0xB8u << 8) | 0xA6u;
            }
        }

        IconInfo info = new() { IsIcon = 1, Mask = mask, Color = color };
        nint icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WindowClassEx
    {
        public int Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint VersionOrTimeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;

        public void SetTip(string text)
        {
            fixed (char* tip = Tip)
            {
                Span<char> target = new(tip, 128);
                target.Clear();
                text.AsSpan(0, Math.Min(text.Length, 127)).CopyTo(target);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int XHotspot;
        public int YHotspot;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
                                               nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
