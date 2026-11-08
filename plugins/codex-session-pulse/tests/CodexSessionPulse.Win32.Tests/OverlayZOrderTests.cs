using System.Runtime.InteropServices;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary><see cref="OverlayWindowInterop.PlaceAbove"/> on real windows: the overlay sits right above Codex in Codex's own band.</summary>
[Collection(ExeCollection.Name)]
public sealed class OverlayZOrderTests
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8;
    private static readonly nint HwndTop = 0;
    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;

    /// <summary>
    /// A Codex window pinned on top would cover a normal overlay; the overlay follows it into the topmost band, right
    /// above it, and back to the normal band when Codex is unpinned.
    /// </summary>
    [Fact]
    public void PlaceAbove_FollowsCodexIntoAndOutOfTheTopmostBand()
    {
        using FakeCodexWindow codex = new("csp-zorder-codex");
        using FakeCodexWindow overlay = new("csp-zorder-overlay");

        try
        {
            Assert.True(SetWindowPos(codex.Hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.False(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.True(IsTopmost(overlay.Hwnd), "The overlay stayed below a topmost Codex.");
            Assert.True(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
            Assert.Equal(overlay.Hwnd, VisibleAbove(codex.Hwnd));

            Assert.True(SetWindowPos(codex.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.False(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.False(IsTopmost(overlay.Hwnd), "The overlay stayed topmost after Codex was unpinned.");
            Assert.Equal(overlay.Hwnd, VisibleAbove(codex.Hwnd));
        }
        finally
        {
            SetWindowPos(overlay.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            SetWindowPos(codex.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>
    /// A normal Codex activated to the top of the normal band, a topmost window right above it, and the normal overlay
    /// below it: the overlay is not placed, and placing it raises it right above Codex.
    /// </summary>
    [Fact]
    public void PlaceAbove_NormalOverlayBelowCodexAtTheTopOfItsBand_RaisesIt()
    {
        using FakeCodexWindow overlay = new("csp-zorder-overlay");
        using FakeCodexWindow codex = new("csp-zorder-codex", withoutIme: true);
        using FakeCodexWindow other = new("csp-zorder-other");

        try
        {
            Assert.True(SetWindowPos(other.Hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.True(SetWindowPos(codex.Hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.True(IsTopmost(GetWindow(codex.Hwnd, GwHwndPrev)), "The window right above Codex should be topmost.");
            Assert.False(IsTopmost(overlay.Hwnd));
            Assert.False(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));

            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.Equal(overlay.Hwnd, VisibleAbove(codex.Hwnd));
            Assert.False(IsTopmost(overlay.Hwnd));
            Assert.True(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
        }
        finally
        {
            SetWindowPos(other.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>A topmost Codex with another topmost window right above it: the overlay goes between the two, topmost.</summary>
    [Fact]
    public void PlaceAbove_TopmostCodexUnderAnotherTopmostWindow_PutsTheOverlayBetween()
    {
        using FakeCodexWindow codex = new("csp-zorder-codex");
        using FakeCodexWindow other = new("csp-zorder-other");
        using FakeCodexWindow overlay = new("csp-zorder-overlay");

        try
        {
            // Both topmost, Codex placed right after the other window, so no system topmost window comes between them.
            Assert.True(SetWindowPos(other.Hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.True(SetWindowPos(codex.Hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.True(SetWindowPos(codex.Hwnd, other.Hwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.Equal(other.Hwnd, VisibleAbove(codex.Hwnd));

            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.True(IsTopmost(overlay.Hwnd), "The overlay stayed below a topmost Codex.");
            Assert.Equal(overlay.Hwnd, VisibleAbove(codex.Hwnd));
            Assert.True(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
        }
        finally
        {
            foreach (nint hwnd in new[] { overlay.Hwnd, other.Hwnd, codex.Hwnd })
            {
                SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
    }

    /// <summary>
    /// An overlay another process minimized is not placed (it shows as a minimized stub at the bottom left of the work
    /// area, the "125 % glitch" of compatibility.md 12.4), and placing it restores it without activating it.
    /// </summary>
    [Fact]
    public void PlaceAbove_RestoresAMinimizedOverlay()
    {
        using FakeCodexWindow codex = new("csp-zorder-codex");
        using FakeCodexWindow overlay = new("csp-zorder-overlay");

        overlay.Minimize();
        Assert.True(SpinWait.SpinUntil(() => IsIconic(overlay.Hwnd), 2000));
        Assert.False(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));

        Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

        Assert.False(IsIconic(overlay.Hwnd));
        Assert.True(OverlayWindowInterop.IsPlacedFor(overlay.Hwnd, codex.Hwnd));
    }

    /// <summary>The first visible window above one in z-order; hidden windows, such as a thread's IME window, sit between.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>The visible window right above it, or 0.</returns>
    private static nint VisibleAbove(nint hwnd)
    {
        nint above = GetWindow(hwnd, GwHwndPrev);

        while (above != 0 && !IsWindowVisible(above))
        {
            above = GetWindow(above, GwHwndPrev);
        }

        return above;
    }

    private static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopmost) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hwnd, int index);
}
