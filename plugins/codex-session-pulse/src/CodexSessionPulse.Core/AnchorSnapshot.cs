namespace CodexSessionPulse.Core;

/// <summary>The immutable state of the Codex Desktop window the overlay follows.</summary>
/// <param name="AtUtc">When the snapshot was computed, in UTC.</param>
/// <param name="IsAttached">Whether a window is currently attached.</param>
/// <param name="CodexWindows">The number of Codex windows found.</param>
/// <param name="Bounds">The window bounds in physical pixels, or <see langword="null"/> when not attached.</param>
/// <param name="DpiScale">The DPI scale of the monitor holding the window, where 1.0 is 96 DPI.</param>
/// <param name="State">The display state of the anchor, including "window not found".</param>
/// <param name="Hwnd">The handle of the followed window, or <see cref="nint.Zero"/> when none.</param>
/// <param name="WorkArea">The work area of the monitor holding the window in physical pixels, used to clamp the overlay, or <see langword="null"/> when unknown.</param>
/// <param name="Diagnostics">A machine-readable diagnostic code, or <see langword="null"/>.</param>
public sealed record AnchorSnapshot(
    DateTimeOffset AtUtc,
    bool IsAttached,
    int CodexWindows,
    WindowBounds? Bounds,
    double DpiScale,
    AnchorState State,
    nint Hwnd,
    WindowBounds? WorkArea,
    string? Diagnostics);

/// <summary>A rectangle in physical pixels.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);
