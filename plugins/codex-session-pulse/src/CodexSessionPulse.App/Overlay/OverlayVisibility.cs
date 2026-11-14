using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Overlay;

/// <summary>When the overlay is shown (spec §3.3, §7.4).</summary>
internal static class OverlayVisibility
{
    /// <summary>Decides whether the overlay is shown.</summary>
    /// <param name="state">The anchor state.</param>
    /// <param name="hideWhenCodexInactive">Whether to hide while Codex is not the foreground app.</param>
    /// <param name="userHidden">Whether the user hid it from the tray.</param>
    /// <returns><see langword="true"/> to show it.</returns>
    public static bool ShouldShow(AnchorState state, bool hideWhenCodexInactive, bool userHidden) => !userHidden && state switch
    {
        AnchorState.Attached => true,
        AnchorState.Background => !hideWhenCodexInactive,
        _ => false,
    };
}
