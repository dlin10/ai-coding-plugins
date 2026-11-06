using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>The physical-pixel geometry of a window and of the monitor holding it.</summary>
/// <param name="Bounds">The visible frame (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, without the invisible resize border), or <see langword="null"/>.</param>
/// <param name="WorkArea">The monitor work area (<c>MONITORINFO.rcWork</c>), or <see langword="null"/>.</param>
/// <param name="DpiScale">The window's DPI scale, where 1.0 is 96 DPI.</param>
public readonly record struct WindowGeometry(WindowBounds? Bounds, WindowBounds? WorkArea, double DpiScale)
{
    /// <summary>
    /// Reads the geometry of a window. The values are physical pixels only in a PerMonitorV2-aware process,
    /// which the app manifest declares.
    /// </summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>The geometry; parts that cannot be read are <see langword="null"/>, and the scale falls back to 1.0.</returns>
    public static WindowGeometry Read(nint hwnd)
    {
        WindowBounds? bounds = null;

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds, out NativeMethods.Rect frame,
                                                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>()) == 0
            || NativeMethods.GetWindowRect(hwnd, out frame))
        {
            bounds = ToBounds(frame);
        }

        WindowBounds? workArea = null;
        nint monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };

        if (monitor != 0 && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            workArea = ToBounds(info.Work);
        }

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);

        return new WindowGeometry(bounds, workArea, dpi == 0 ? 1.0 : dpi / 96.0);
    }

    private static WindowBounds ToBounds(NativeMethods.Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
