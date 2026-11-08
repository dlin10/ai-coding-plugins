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
            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.True(IsTopmost(overlay.Hwnd), "The overlay stayed below a topmost Codex.");
            Assert.Equal(overlay.Hwnd, GetWindow(codex.Hwnd, GwHwndPrev));

            Assert.True(SetWindowPos(codex.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.True(OverlayWindowInterop.PlaceAbove(overlay.Hwnd, codex.Hwnd, 150, 150));

            Assert.False(IsTopmost(overlay.Hwnd), "The overlay stayed topmost after Codex was unpinned.");
            Assert.Equal(overlay.Hwnd, GetWindow(codex.Hwnd, GwHwndPrev));
        }
        finally
        {
            SetWindowPos(overlay.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            SetWindowPos(codex.Hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopmost) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hwnd, int index);
}
