using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>The overlay rectangle against the Codex window, its input card and the monitor work area, in physical pixels.</summary>
public sealed class OverlayPlacementTests
{
    private static readonly WindowBounds Primary = new(0, 0, 1920, 1040);

    /// <summary>The Codex window measured at 125% on 2026-11-09, not maximized (docs/compatibility.md §6.2).</summary>
    private static readonly WindowBounds Codex125 = new(321, 107, 1796, 1068);

    private static readonly WindowBounds WorkArea125 = new(0, 0, 2560, 1392);

    /// <summary>Without a card, at 100%, the overlay is centred and its bottom edge sits 170 DIP above the window's.</summary>
    [Fact]
    public void ByTheWindow_At100Percent_CentresAboveTheBottom()
    {
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(100, 50, 1200, 900), null, Primary, 600, 40, 1.0, 0, 0);

        Assert.Equal(new WindowBounds(400, 740, 600, 40), result);
    }

    /// <summary>At 150% the size, the clearance and the offsets scale with the monitor DPI.</summary>
    [Fact]
    public void ByTheWindow_At150Percent_ScalesSizeClearanceAndOffsets()
    {
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(0, 0, 1800, 1000), null, new WindowBounds(0, 0, 2560, 1400),
                                                       600, 40, 1.5, 10, -10);

        Assert.Equal(new WindowBounds(465, 670, 900, 60), result);
    }

    /// <summary>
    /// With the left panel open, the card (and the chat column) sits right of the window's centre; the overlay follows the
    /// card, its bottom edge 8 DIP above the card's top, not the window's centre (#392: 170 DIP to the left).
    /// </summary>
    [Fact]
    public void AboveTheCard_LeftPanelOpen_CentresOnTheCard()
    {
        InputSighting card = new(new WindowBounds(970, 1029, 920, 123), Codex125);

        WindowBounds result = OverlayPlacement.Compute(Codex125, card, WorkArea125, 724, 40, 1.25, 0, 0);

        Assert.Equal(new WindowBounds(978, 969, 905, 50), result);
    }

    /// <summary>The left panel collapsed and a right panel open move the card left of the window's centre; so does the overlay.</summary>
    [Fact]
    public void AboveTheCard_LeftPanelCollapsed_FollowsTheCardLeft()
    {
        InputSighting card = new(new WindowBounds(597, 1029, 920, 123), Codex125);

        WindowBounds result = OverlayPlacement.Compute(Codex125, card, WorkArea125, 724, 40, 1.25, 0, 0);

        Assert.Equal(new WindowBounds(605, 969, 905, 50), result);
    }

    /// <summary>A taller card (project chips, or several lines of input) lifts the overlay with its top edge.</summary>
    [Fact]
    public void AboveTheCard_TallerCard_LiftsTheOverlay()
    {
        InputSighting chips = new(new WindowBounds(970, 969, 920, 183), Codex125);
        InputSighting lines = new(new WindowBounds(597, 954, 920, 198), Codex125);

        Assert.Equal(909, OverlayPlacement.Compute(Codex125, chips, WorkArea125, 724, 40, 1.25, 0, 0).Top);
        Assert.Equal(894, OverlayPlacement.Compute(Codex125, lines, WorkArea125, 724, 40, 1.25, 0, 0).Top);
    }

    /// <summary>
    /// A window moved or resized since the card was read carries the card along by its left and bottom edges, so the overlay
    /// follows a drag before the next read.
    /// </summary>
    [Fact]
    public void AboveTheCard_WindowMovedSinceTheRead_CarriesTheCard()
    {
        InputSighting card = new(new WindowBounds(970, 1029, 920, 123), Codex125);
        WindowBounds moved = Codex125 with { Left = Codex125.Left + 100, Top = Codex125.Top + 50 };
        WindowBounds taller = Codex125 with { Height = Codex125.Height + 100 };

        Assert.Equal(new WindowBounds(1078, 1019, 905, 50), OverlayPlacement.Compute(moved, card, WorkArea125, 724, 40, 1.25, 0, 0));
        Assert.Equal(new WindowBounds(978, 1069, 905, 50), OverlayPlacement.Compute(taller, card, WorkArea125, 724, 40, 1.25, 0, 0));
    }

    /// <summary>The offsets shift the position above the card too.</summary>
    [Fact]
    public void AboveTheCard_AppliesTheOffsets()
    {
        WindowBounds codex = new(0, 0, 1600, 1000);
        InputSighting card = new(new WindowBounds(500, 850, 800, 120), codex);

        WindowBounds result = OverlayPlacement.Compute(codex, card, Primary, 600, 40, 1.0, 10, -20);

        Assert.Equal(new WindowBounds(610, 782, 600, 40), result);
    }

    /// <summary>A Codex window hanging off the right and bottom edges keeps the overlay inside the work area.</summary>
    [Fact]
    public void ClampsIntoTheWorkArea()
    {
        WindowBounds codex = new(1700, 900, 800, 600);

        Assert.Equal(new WindowBounds(1320, 1000, 600, 40), OverlayPlacement.Compute(codex, null, Primary, 600, 40, 1.0, 0, 0));
        Assert.Equal(new WindowBounds(1320, 1000, 600, 40),
                     OverlayPlacement.Compute(codex, new InputSighting(new WindowBounds(1800, 1300, 600, 120), codex), Primary, 600, 40, 1.0, 0, 0));
    }

    /// <summary>A card near the window's top would put the overlay above the work area; it stops at the top edge.</summary>
    [Fact]
    public void AboveTheCard_NearTheTop_ClampsToTheWorkArea()
    {
        WindowBounds codex = new(0, 0, 1600, 1000);
        InputSighting card = new(new WindowBounds(500, 30, 800, 900), codex);

        Assert.Equal(new WindowBounds(600, 0, 600, 40), OverlayPlacement.Compute(codex, card, Primary, 600, 40, 1.0, 0, 0));
    }

    /// <summary>A monitor left of and above the primary one has negative coordinates.</summary>
    [Fact]
    public void WorksWithNegativeCoordinates()
    {
        WindowBounds left = new(-2560, -200, 2560, 1400);
        WindowBounds codex = new(-2600, -150, 1000, 800);

        Assert.Equal(new WindowBounds(-2560, 388, 625, 50),
                     OverlayPlacement.Compute(new WindowBounds(-3000, -150, 1000, 800), null, left, 500, 40, 1.25, 0, 0));
        Assert.Equal(new WindowBounds(-2412, 388, 625, 50), OverlayPlacement.Compute(codex, null, left, 500, 40, 1.25, 0, 0));
        Assert.Equal(new WindowBounds(-2312, 470, 625, 50),
                     OverlayPlacement.Compute(codex, new InputSighting(new WindowBounds(-2400, 530, 800, 100), codex), left, 500, 40, 1.25, 0, 0));
    }

    /// <summary>An overlay wider than the work area is cut to it and starts at its left edge.</summary>
    [Fact]
    public void WiderThanTheWorkArea_IsCutToIt()
    {
        WindowBounds narrow = new(0, 0, 400, 800);
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(0, 0, 400, 800), null, narrow, 900, 40, 1.0, 0, 0);

        Assert.Equal(new WindowBounds(0, 590, 400, 40), result);
    }
}
