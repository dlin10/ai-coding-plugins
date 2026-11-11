using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>
/// Where Codex's input card was seen, together with the Codex window bounds read in the same pass. The card is tracked
/// against those bounds, so a window moved since the read carries the card along until the next read.
/// </summary>
/// <param name="Card">The input card (editor, project chips and buttons) in physical pixels.</param>
/// <param name="Window">The Codex window's extended frame bounds at the time of the read.</param>
public readonly record struct InputSighting(WindowBounds Card, WindowBounds Window);

/// <summary>The geometry of the overlay against the Codex window. Pure, so it is tested without windows.</summary>
public static class OverlayPlacement
{
    /// <summary>The gap between the overlay's bottom edge and the top of Codex's input card, in DIP.</summary>
    public const double InputGapDip = 8;

    /// <summary>
    /// How far the overlay's bottom edge sits above the Codex window's bottom edge when the input card is unknown, in DIP.
    /// It clears the tallest card measured, with the row of project chips (docs/compatibility.md §6.2).
    /// </summary>
    public const double WindowClearanceDip = 170;

    /// <summary>
    /// Places the overlay (spec §3.1, §8.1). With an input card (<c>AboveCodexInput</c>) it is centred over the card with
    /// its bottom edge <see cref="InputGapDip"/> above the card; without one (<c>BottomOfCodexWindow</c>, or no card found)
    /// it is centred on the window with its bottom edge <see cref="WindowClearanceDip"/> above the window's bottom edge.
    /// The offsets shift either position, and the result is clamped into the monitor work area. All rectangles are
    /// physical pixels; coordinates may be negative on a monitor left of or above the primary one.
    /// </summary>
    /// <param name="anchor">The Codex window's extended frame bounds.</param>
    /// <param name="input">Where the input card was last seen, or <see langword="null"/> to place by the window.</param>
    /// <param name="workArea">The work area of the monitor holding the Codex window.</param>
    /// <param name="widthDip">The overlay width in DIP.</param>
    /// <param name="heightDip">The overlay height in DIP.</param>
    /// <param name="dpiScale">The DPI scale of that monitor, where 1.0 is 96 DPI.</param>
    /// <param name="offsetXDip">The horizontal offset in DIP; positive moves right.</param>
    /// <param name="offsetYDip">The vertical offset in DIP; negative moves up.</param>
    /// <returns>The overlay rectangle in physical pixels.</returns>
    public static WindowBounds Compute(WindowBounds anchor,
                                       InputSighting? input,
                                       WindowBounds workArea,
                                       double widthDip,
                                       double heightDip,
                                       double dpiScale,
                                       double offsetXDip,
                                       double offsetYDip)
    {
        int width = Math.Min((int)Math.Round(widthDip * dpiScale), workArea.Width);
        int height = Math.Min((int)Math.Round(heightDip * dpiScale), workArea.Height);
        int centreX;
        int bottom;

        if (input is { } seen)
        {
            // The card keeps its distance from the window's left and bottom edges: Codex lays it out from the bottom.
            centreX = anchor.Left + (seen.Card.Left - seen.Window.Left) + (seen.Card.Width / 2);
            bottom = anchor.Top + anchor.Height - (seen.Window.Top + seen.Window.Height - seen.Card.Top)
                     - (int)Math.Round(InputGapDip * dpiScale);
        }
        else
        {
            centreX = anchor.Left + (anchor.Width / 2);
            bottom = anchor.Top + anchor.Height - (int)Math.Round(WindowClearanceDip * dpiScale);
        }

        int left = centreX - (width / 2) + (int)Math.Round(offsetXDip * dpiScale);
        int top = bottom - height + (int)Math.Round(offsetYDip * dpiScale);

        left = Math.Clamp(left, workArea.Left, workArea.Left + workArea.Width - width);
        top = Math.Clamp(top, workArea.Top, workArea.Top + workArea.Height - height);

        return new WindowBounds(left, top, width, height);
    }
}
