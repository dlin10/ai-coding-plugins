using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>
/// The native side of the overlay window (spec §7.1): extended styles, and placement that never activates, never sets a
/// cross-process owner (<c>GWLP_HWNDPARENT</c>) and never makes the overlay topmost.
/// </summary>
public static class OverlayWindowInterop
{
    /// <summary>Computes the extended style of the overlay: adds no-activate and tool-window, keeps every other bit, never click-through.</summary>
    /// <param name="exStyle">The current extended style.</param>
    /// <returns>The new extended style.</returns>
    public static long WithOverlayStyles(long exStyle)
    {
        return (exStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow) & ~(NativeMethods.WsExTransparent | NativeMethods.WsExTopmost);
    }

    /// <summary>Applies <see cref="WithOverlayStyles"/> to a window.</summary>
    /// <param name="hwnd">The overlay window.</param>
    /// <returns>The extended style now set.</returns>
    public static long ApplyOverlayStyles(nint hwnd)
    {
        long style = WithOverlayStyles(NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle));
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, (nint)style);

        return NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
    }

    /// <summary>
    /// Moves the overlay to <paramref name="bounds"/> and puts it directly above the Codex window in z-order, without activating
    /// it, so any window over Codex also covers the overlay.
    /// </summary>
    /// <param name="overlay">The overlay window.</param>
    /// <param name="codex">The Codex window it follows.</param>
    /// <param name="bounds">The overlay rectangle in physical pixels.</param>
    /// <returns><see langword="true"/> when <c>SetWindowPos</c> succeeded.</returns>
    public static bool PlaceAbove(nint overlay, nint codex, WindowBounds bounds)
    {
        uint flags = NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder;
        nint insertAfter = NativeMethods.HwndTop;
        nint above = NativeMethods.GetWindow(codex, NativeMethods.GwHwndPrev);

        if (above == overlay)
        {
            flags |= NativeMethods.SwpNoZOrder;
        }
        else if (above != 0 && (NativeMethods.GetWindowLongPtr(above, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) == 0)
        {
            // Inserting after the window right above Codex leaves the overlay right above Codex. After a topmost
            // window it would join the topmost band, so in that case HWND_TOP (top of the normal band) is used.
            insertAfter = above;
        }

        return NativeMethods.SetWindowPos(overlay, insertAfter, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
    }

    /// <summary>Checks whether a window is the foreground window.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsForeground(nint hwnd) => NativeMethods.GetForegroundWindow() == hwnd;
}
