using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>The overlay rectangle against the Codex window and the monitor work area, in physical pixels.</summary>
public sealed class OverlayPlacementTests
{
    private static readonly WindowBounds Primary = new(0, 0, 1920, 1040);

    /// <summary>At 100% the overlay is centred and its bottom edge sits the offset above the window's bottom edge.</summary>
    [Fact]
    public void At100Percent_CentresAtTheBottomWithTheOffset()
    {
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(100, 50, 1200, 900), Primary, 600, 40, 1.0, 0, -72);

        Assert.Equal(new WindowBounds(400, 838, 600, 40), result);
    }

    /// <summary>At 150% the size and the offsets scale with the monitor DPI.</summary>
    [Fact]
    public void At150Percent_ScalesSizeAndOffsets()
    {
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(0, 0, 1800, 1000), new WindowBounds(0, 0, 2560, 1400), 600, 40, 1.5, 10, -72);

        Assert.Equal(new WindowBounds(465, 832, 900, 60), result);
    }

    /// <summary>A Codex window hanging off the right and bottom edges keeps the overlay inside the work area.</summary>
    [Fact]
    public void ClampsIntoTheWorkArea()
    {
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(1700, 900, 800, 600), Primary, 600, 40, 1.0, 0, 0);

        Assert.Equal(new WindowBounds(1320, 1000, 600, 40), result);
    }

    /// <summary>A monitor left of and above the primary one has negative coordinates.</summary>
    [Fact]
    public void WorksWithNegativeCoordinates()
    {
        WindowBounds left = new(-2560, -200, 2560, 1400);
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(-3000, -150, 1000, 800), left, 500, 40, 1.25, 0, -72);

        Assert.Equal(new WindowBounds(-2560, 510, 625, 50), result);
        Assert.Equal(new WindowBounds(-2413, 510, 625, 50),
                     OverlayPlacement.Compute(new WindowBounds(-2600, -150, 1000, 800), left, 500, 40, 1.25, 0, -72));
    }

    /// <summary>An overlay wider than the work area is cut to it and starts at its left edge.</summary>
    [Fact]
    public void WiderThanTheWorkArea_IsCutToIt()
    {
        WindowBounds narrow = new(0, 0, 400, 800);
        WindowBounds result = OverlayPlacement.Compute(new WindowBounds(0, 0, 400, 800), narrow, 900, 40, 1.0, 0, -72);

        Assert.Equal(new WindowBounds(0, 688, 400, 40), result);
    }
}
