using System.ComponentModel;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// The overlay's view model. It keeps the latest cache and anchor snapshots and re-formats the line when either changes
/// or the 1 s clock ticks. Call it on the UI thread only.
/// </summary>
/// <param name="clock">The time source.</param>
internal sealed class OverlayViewModel(TimeProvider clock) : INotifyPropertyChanged
{
    private CacheSnapshot? _cache;
    private int _codexWindows;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the formatted line; <see langword="null"/> until the first cache snapshot.</summary>
    public OverlayView? View { get; private set; }

    /// <summary>Gets the latest cache snapshot.</summary>
    public CacheSnapshot? Cache => _cache;

    /// <summary>Gets the Auto Warm hint (the button stays disabled in P0).</summary>
    public string AutoWarmHint => OverlayFormatter.AutoWarmHint;

    /// <summary>Takes a new cache snapshot.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public void Update(CacheSnapshot snapshot)
    {
        _cache = snapshot;
        Render();
    }

    /// <summary>Takes a new anchor snapshot; only the window count matters to the line.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public void Update(AnchorSnapshot snapshot)
    {
        if (snapshot.CodexWindows != _codexWindows)
        {
            _codexWindows = snapshot.CodexWindows;
            Render();
        }
    }

    /// <summary>Re-formats the line for the current time, so the TTL counts down between snapshots.</summary>
    public void Tick() => Render();

    private void Render()
    {
        if (_cache is null)
        {
            return;
        }

        OverlayView view = OverlayFormatter.Format(_cache, _codexWindows, clock.GetUtcNow(), TimeZoneInfo.Local);

        if (view != View)
        {
            View = view;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(View)));
        }
    }
}
