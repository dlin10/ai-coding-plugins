using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>
/// The native side of the overlay window (spec §7.1): extended styles, and placement that never activates, never sets a
/// cross-process owner (<c>GWLP_HWNDPARENT</c>) and never lifts the overlay above Codex's own z-order band: it is topmost
/// only while the Codex window itself is, so it never covers a window that Codex does not.
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
    /// Moves the overlay's top-left corner to a point and puts it directly above the Codex window in z-order, without
    /// activating it, so any window over Codex also covers the overlay; a topmost Codex makes the overlay topmost too, and
    /// an unpinned one makes it normal again. The size is left to WPF (<c>SizeToContent</c>), which
    /// rescales it on <c>WM_DPICHANGED</c>; setting it here as well would make the two fight across mixed-DPI monitors.
    /// </summary>
    /// <param name="overlay">The overlay window.</param>
    /// <param name="codex">The Codex window it follows.</param>
    /// <param name="left">The left edge in physical pixels.</param>
    /// <param name="top">The top edge in physical pixels.</param>
    /// <returns><see langword="true"/> when <c>SetWindowPos</c> succeeded.</returns>
    public static bool PlaceAbove(nint overlay, nint codex, int left, int top)
    {
        if (NativeMethods.IsIconic(overlay))
        {
            // Another process can minimize even a tool window (measured: a screenshot tool did); restore it without activating it.
            NativeMethods.ShowWindow(overlay, NativeMethods.SwShowNoActivate);
        }

        uint flags = NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpNoSize;
        bool codexTopmost = IsTopmost(codex);
        nint above = NativeMethods.GetWindow(codex, NativeMethods.GwHwndPrev);
        nint insertAfter;

        // The overlay lives in Codex's band. A Codex pinned on top (measured: a desktop-automation tool left it
        // WS_EX_TOPMOST) puts every normal window below it, the overlay included, so the overlay turns topmost with it
        // and back again when Codex is unpinned.
        if (above == overlay && IsTopmost(overlay) == codexTopmost)
        {
            insertAfter = 0;
            flags |= NativeMethods.SwpNoZOrder;
        }
        else if (above != 0 && above != overlay && IsTopmost(above) == codexTopmost)
        {
            // After the window right above Codex, in the same band: the overlay ends up right above Codex.
            insertAfter = above;
        }
        else
        {
            // Codex tops its band: the top of that band is right above it.
            insertAfter = codexTopmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost;
        }

        return NativeMethods.SetWindowPos(overlay, insertAfter, left, top, 0, 0, flags);
    }

    private static bool IsTopmost(nint window) =>
        (NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0;
}
