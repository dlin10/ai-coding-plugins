using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>
/// The native side of the overlay window (spec §7.1): extended styles, and placement that never activates, never sets a
/// cross-process owner (<c>GWLP_HWNDPARENT</c>) and never lifts the overlay above Codex's own z-order band: it is topmost
/// only while the Codex window itself is, so it never covers a window that Codex does not.
/// </summary>
public static class OverlayWindowInterop
{
    /// <summary>More top-level windows than a desktop holds, hidden ones included; only a z-order change mid-walk reaches it.</summary>
    private const int MaxZOrderSteps = 10_000;

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
            // After the window right above Codex, in the same band: the overlay ends up right above Codex. An insert after
            // a topmost window makes the overlay topmost (OverlayZOrderTests).
            insertAfter = above;
        }
        else if (codexTopmost)
        {
            // Codex tops the topmost band: the top of that band is right above it.
            insertAfter = NativeMethods.HwndTopmost;
        }
        else
        {
            // Codex tops the normal band, right under the topmost windows. HWND_NOTOPMOST puts a topmost window at the top
            // of the normal band but does not move a window that is already normal, and HWND_TOP does not raise the window
            // of a background process, which the overlay is while Codex has the focus (OverlayZOrderTests measured both).
            // So a normal overlay is made topmost for a moment, then demoted to the top of the normal band.
            if (!IsTopmost(overlay))
            {
                NativeMethods.SetWindowPos(overlay, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags | NativeMethods.SwpNoMove);
            }

            insertAfter = NativeMethods.HwndNoTopmost;
        }

        return NativeMethods.SetWindowPos(overlay, insertAfter, left, top, 0, 0, flags);
    }

    /// <summary>
    /// Tells whether the overlay is still placed for Codex: not minimized, in the same z-order band, and somewhere above
    /// it. Neither another process minimizing the overlay (measured: a screen-automation tool does, which left it as a
    /// 160 DIP minimized stub at the bottom left of the work area), nor a pin or unpin of Codex, nor Codex raised above
    /// the overlay without moving moves the Codex window, so the anchor reports nothing; the overlay's clock asks this
    /// once a second and places the overlay again when it is not. "Above", not "right above": Codex's own hidden windows,
    /// such as its IME window, sit between the two, and requiring adjacency would re-place the overlay every second.
    /// </summary>
    /// <param name="overlay">The overlay window.</param>
    /// <param name="codex">The Codex window it follows.</param>
    /// <returns><see langword="true"/> when the overlay is not minimized, in Codex's band and above Codex.</returns>
    public static bool IsPlacedFor(nint overlay, nint codex) =>
        !NativeMethods.IsIconic(overlay) && IsTopmost(overlay) == IsTopmost(codex) && IsAbove(overlay, codex);

    /// <summary>Walks down the z-order from one window looking for another; bounded, since the order can change meanwhile.</summary>
    /// <param name="window">The window that should be higher.</param>
    /// <param name="below">The window that should be lower.</param>
    /// <returns><see langword="true"/> when <paramref name="below"/> was found under <paramref name="window"/>.</returns>
    private static bool IsAbove(nint window, nint below)
    {
        nint next = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext);

        for (int step = 0; next != 0 && step < MaxZOrderSteps; step++)
        {
            if (next == below)
            {
                return true;
            }

            next = NativeMethods.GetWindow(next, NativeMethods.GwHwndNext);
        }

        return false;
    }

    private static bool IsTopmost(nint window) =>
        (NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0;
}
