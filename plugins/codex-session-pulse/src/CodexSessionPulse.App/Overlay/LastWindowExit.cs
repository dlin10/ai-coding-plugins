using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// Decides when the overlay exits (spec §4.4 item 5): only after a Codex window was seen and then confirmed gone for a
/// grace period. The anchor's "not found" (driven by <c>EVENT_OBJECT_DESTROY</c> and a re-enumeration) arms it; after the
/// grace period an <c>EnumWindows</c> recheck that also counts cloaked windows confirms it. A minimized window, a closed
/// thread or <c>SessionEnd</c> never count as gone, and an overlay started by hand before Codex never exits on its own.
/// </summary>
/// <param name="gracePeriod">How long the last window must stay gone before the recheck.</param>
internal sealed class LastWindowExit(TimeSpan gracePeriod)
{
    /// <summary>The grace period of the overlay; with the 1 s display clock the exit comes 3–4 s after the close.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(3);

    private bool _seen;
    private DateTimeOffset? _goneSinceUtc;

    /// <summary>Gets whether the last window is gone and the exit is pending.</summary>
    public bool Pending => _goneSinceUtc is not null;

    /// <summary>Takes a new anchor state.</summary>
    /// <param name="state">The anchor state.</param>
    /// <param name="nowUtc">The current time.</param>
    public void OnAnchor(AnchorState state, DateTimeOffset nowUtc)
    {
        if (state != AnchorState.NotFound)
        {
            _seen = true;
            _goneSinceUtc = null;
        }
        else if (_seen && _goneSinceUtc is null)
        {
            _goneSinceUtc = nowUtc;
        }
    }

    /// <summary>Gets whether the grace period has passed and the windows must be counted again.</summary>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>Whether to recheck now.</returns>
    public bool ShouldRecheck(DateTimeOffset nowUtc) => _goneSinceUtc is { } since && nowUtc - since >= gracePeriod;

    /// <summary>Takes the recheck's count.</summary>
    /// <param name="codexWindows">The Codex windows found, cloaked ones included.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>Whether to exit; with a window left (one on another virtual desktop), the next recheck comes a grace period later.</returns>
    public bool OnRecheck(int codexWindows, DateTimeOffset nowUtc)
    {
        if (codexWindows == 0)
        {
            return true;
        }

        _goneSinceUtc = nowUtc;

        return false;
    }
}
