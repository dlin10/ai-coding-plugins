using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>The geometry of the overlay against the Codex window. Pure, so it is tested without windows.</summary>
public static class OverlayPlacement
{
    /// <summary>
    /// Places the overlay centred at the bottom of the Codex window (<c>BottomOfCodexWindow</c>, spec §8.1), shifted by
    /// the offsets, and clamped into the monitor work area. All rectangles are physical pixels; coordinates may be negative
    /// on a monitor left of or above the primary one.
    /// </summary>
    /// <param name="anchor">The Codex window's extended frame bounds.</param>
    /// <param name="workArea">The work area of the monitor holding the Codex window.</param>
    /// <param name="widthDip">The overlay width in DIP.</param>
    /// <param name="heightDip">The overlay height in DIP.</param>
    /// <param name="dpiScale">The DPI scale of that monitor, where 1.0 is 96 DPI.</param>
    /// <param name="offsetXDip">The horizontal offset in DIP; positive moves right.</param>
    /// <param name="offsetYDip">The vertical offset of the overlay's bottom edge from the window's bottom edge in DIP; negative moves up.</param>
    /// <returns>The overlay rectangle in physical pixels.</returns>
    public static WindowBounds Compute(WindowBounds anchor,
                                       WindowBounds workArea,
                                       double widthDip,
                                       double heightDip,
                                       double dpiScale,
                                       double offsetXDip,
                                       double offsetYDip)
    {
        int width = Math.Min((int)Math.Round(widthDip * dpiScale), workArea.Width);
        int height = Math.Min((int)Math.Round(heightDip * dpiScale), workArea.Height);
        int left = anchor.Left + ((anchor.Width - width) / 2) + (int)Math.Round(offsetXDip * dpiScale);
        int top = anchor.Top + anchor.Height - height + (int)Math.Round(offsetYDip * dpiScale);

        left = Math.Clamp(left, workArea.Left, workArea.Left + workArea.Width - width);
        top = Math.Clamp(top, workArea.Top, workArea.Top + workArea.Height - height);

        return new WindowBounds(left, top, width, height);
    }
}
