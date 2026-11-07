using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>The detector's state for "Copy diagnostics"; it holds no titles, paths or other user data.</summary>
/// <param name="Rule">The detection rule in words.</param>
/// <param name="CodexWindows">The number of Codex windows at the last refresh.</param>
/// <param name="State">The anchor state at the last refresh.</param>
/// <param name="ProcessHooks">The number of Codex processes with installed per-process hooks.</param>
/// <param name="GlobalHooksInstalled">Whether the foreground and minimize hooks are installed.</param>
/// <param name="PollingActive">Whether the fallback poll is running.</param>
/// <param name="Refreshes">The number of full window enumerations since start.</param>
public sealed record AnchorDiagnostics(string Rule,
                                       int CodexWindows,
                                       AnchorState State,
                                       int ProcessHooks,
                                       bool GlobalHooksInstalled,
                                       bool PollingActive,
                                       long Refreshes);

/// <summary>
/// Follows the foreground Codex Desktop window (spec §3.3, §7.2). WinEvents drive it: foreground and minimize globally;
/// destroy, show, hide and location changes only for the Codex processes. Every callback is coalesced onto the UI thread
/// through the captured <see cref="SynchronizationContext"/>, and a cheap 300 ms poll runs while a Codex window exists.
/// Create, use and dispose it on the UI thread.
/// </summary>
public sealed class CodexWindowAnchor : IWindowAnchor
{
    /// <summary>The diagnostic code of the "window not found" state.</summary>
    public const string WindowNotFound = "window-not-found";

    private const int PollIntervalMs = 300;
    private const uint GaRoot = 2;

    private readonly SynchronizationContext _ui;
    private readonly TimeProvider _time;
    private readonly SnapshotPublisher<AnchorSnapshot> _stream = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly List<WinEventHook> _globalHooks = [];
    private readonly Dictionary<int, WinEventHook[]> _processHooks = [];
    private WindowDetectorOptions _options;
    private IReadOnlyList<WindowCandidate> _windows = [];
    private ITimer? _poll;
    private nint _followed;
    private nint _lastForeground;
    private bool _foreground;
    private bool _refreshQueued;
    private bool _geometryQueued;
    private bool _disposed;
    private bool _published;
    private long _refreshes;

    /// <summary>Creates the anchor; call <see cref="Start"/> to begin.</summary>
    /// <param name="options">The detection rule.</param>
    /// <param name="ui">The UI thread's context; every callback and snapshot runs there.</param>
    /// <param name="time">The time source; the system clock when <see langword="null"/>.</param>
    public CodexWindowAnchor(WindowDetectorOptions options, SynchronizationContext ui, TimeProvider? time = null)
    {
        _options = options;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        Current = new AnchorSnapshot(_time.GetUtcNow(), false, 0, null, 1.0, AnchorState.NotFound, 0, null, WindowNotFound);
    }

    /// <inheritdoc />
    public IObservable<AnchorSnapshot> Snapshots => _stream;

    /// <summary>Gets the latest snapshot.</summary>
    public AnchorSnapshot Current { get; private set; }

    /// <summary>Installs the global hooks and detects the windows.</summary>
    public void Start()
    {
        _globalHooks.Add(new WinEventHook(NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground, 0, OnGlobalEvent));
        _globalHooks.Add(new WinEventHook(NativeMethods.EventSystemMinimizeStart, NativeMethods.EventSystemMinimizeEnd, 0, OnGlobalEvent));
        Refresh();
    }

    /// <summary>Forgets the followed window and enumerates again ("Re-detect Codex windows").</summary>
    public void Redetect()
    {
        _followed = 0;
        Refresh();
    }

    /// <summary>Replaces the detection rule and re-detects.</summary>
    /// <param name="options">The new rule.</param>
    public void UpdateOptions(WindowDetectorOptions options)
    {
        _options = options;
        Redetect();
    }

    /// <inheritdoc />
    public void Attach(nint hwnd)
    {
        _followed = hwnd;
        Refresh();
    }

    /// <summary>Describes the detector for diagnostics.</summary>
    /// <returns>The detector state.</returns>
    public AnchorDiagnostics GetDiagnostics()
    {
        return new AnchorDiagnostics(_options.Describe(), Current.CodexWindows, Current.State, _processHooks.Count,
                                     _globalHooks.Count > 0 && _globalHooks.All(hook => hook.IsInstalled), _poll is not null, _refreshes);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _poll?.Dispose();
            _poll = null;

            foreach (WinEventHook hook in _globalHooks.Concat(_processHooks.Values.SelectMany(hooks => hooks)))
            {
                hook.Dispose();
            }

            _globalHooks.Clear();
            _processHooks.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Enumerates the Codex windows, picks the one to follow and publishes its snapshot.</summary>
    private void Refresh()
    {
        _refreshQueued = false;

        if (_disposed)
        {
            return;
        }

        _refreshes++;
        _windows = WindowEnumerator.EnumerateCodexWindows(_options);
        SyncProcessHooks();

        if (_windows.Count == 0)
        {
            _followed = 0;
            _foreground = false;
            Publish(new AnchorSnapshot(_time.GetUtcNow(), false, 0, null, 1.0, AnchorState.NotFound, 0, null, WindowNotFound));

            return;
        }

        nint foreground = NativeMethods.GetForegroundWindow();
        _lastForeground = foreground;
        nint foregroundRoot = foreground == 0 ? 0 : NativeMethods.GetAncestor(foreground, NativeMethods.GaRootOwner);
        int foregroundProcess = foreground == 0 ? 0 : WindowEnumerator.GetProcessId(foreground);

        if (_windows.Any(window => window.Hwnd == foregroundRoot))
        {
            _followed = foregroundRoot;
            _foreground = true;
        }
        else if (foregroundProcess != _ownProcessId)
        {
            // An unowned Codex popup keeps Codex in the foreground; our own tray menu or dialog changes nothing.
            _foreground = _windows.Any(window => window.ProcessId == foregroundProcess);
        }

        if (!_windows.Any(window => window.Hwnd == _followed))
        {
            _followed = _windows[0].Hwnd;
        }

        PublishGeometry();
    }

    /// <summary>The fallback poll: a full refresh when the foreground window changed, otherwise only the followed window's geometry.</summary>
    private void Poll()
    {
        if (!_disposed && !_refreshQueued)
        {
            if (NativeMethods.GetForegroundWindow() != _lastForeground)
            {
                Refresh();
            }
            else
            {
                PublishGeometry();
            }
        }
    }

    /// <summary>Reads the followed window's state and geometry and publishes it when anything changed.</summary>
    private void PublishGeometry()
    {
        _geometryQueued = false;

        if (_disposed || _followed == 0)
        {
            return;
        }

        if (!NativeMethods.IsWindow(_followed))
        {
            Refresh();

            return;
        }

        AnchorState state = NativeMethods.IsIconic(_followed) ? AnchorState.Minimized
                          : _foreground ? AnchorState.Attached
                          : AnchorState.Background;
        WindowGeometry geometry = WindowGeometry.Read(_followed);

        Publish(new AnchorSnapshot(_time.GetUtcNow(), true, _windows.Count, geometry.Bounds, geometry.DpiScale, state, _followed,
                                   geometry.WorkArea, geometry.Bounds is null ? "bounds-unavailable" : null));
    }

    /// <summary>
    /// Publishes a snapshot unless it equals the current one apart from its time, and runs the fallback poll while a Codex
    /// window exists. Activation does not always raise <c>EVENT_SYSTEM_FOREGROUND</c> (measured: an app activated by
    /// another process came to the front with no event), so the poll also covers Background and Minimized.
    /// </summary>
    /// <param name="snapshot">The new snapshot.</param>
    private void Publish(AnchorSnapshot snapshot)
    {
        bool found = snapshot.State != AnchorState.NotFound;

        if (found && _poll is null)
        {
            _poll = _time.CreateTimer(_ => _ui.Post(_ => Poll(), null),
                                      null, TimeSpan.FromMilliseconds(PollIntervalMs), TimeSpan.FromMilliseconds(PollIntervalMs));
        }
        else if (!found && _poll is not null)
        {
            _poll.Dispose();
            _poll = null;
        }

        // The first snapshot is always published, so subscribers learn "not found" too.
        if (!_published || snapshot with { AtUtc = Current.AtUtc } != Current)
        {
            _published = true;
            Current = snapshot;
            _stream.Publish(snapshot);
        }
    }

    /// <summary>Installs destroy/show/hide and location hooks for each Codex process and removes those of gone processes.</summary>
    private void SyncProcessHooks()
    {
        HashSet<int> processes = _windows.Select(window => window.ProcessId).ToHashSet();

        foreach (int gone in _processHooks.Keys.Where(id => !processes.Contains(id)).ToList())
        {
            foreach (WinEventHook hook in _processHooks[gone])
            {
                hook.Dispose();
            }

            _processHooks.Remove(gone);
        }

        foreach (int id in processes.Where(id => !_processHooks.ContainsKey(id)))
        {
            _processHooks[id] =
            [
                new WinEventHook(NativeMethods.EventObjectDestroy, NativeMethods.EventObjectHide, (uint)id, OnProcessEvent),
                new WinEventHook(NativeMethods.EventObjectLocationChange, NativeMethods.EventObjectLocationChange, (uint)id, OnProcessEvent),
            ];
        }
    }

    private void OnGlobalEvent(uint eventType, nint hwnd) => QueueGeometryOrRefresh(full: true);

    private void OnProcessEvent(uint eventType, nint hwnd)
    {
        if (eventType == NativeMethods.EventObjectLocationChange)
        {
            if (hwnd == _followed)
            {
                QueueGeometryOrRefresh(full: false);
            }
        }
        else if (eventType == NativeMethods.EventObjectDestroy)
        {
            if (_windows.Any(window => window.Hwnd == hwnd))
            {
                QueueGeometryOrRefresh(full: true);
            }
        }
        else if (NativeMethods.GetAncestor(hwnd, GaRoot) == hwnd && NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) == 0)
        {
            // Show or hide of an unowned top-level window: a Codex window opened or closed.
            QueueGeometryOrRefresh(full: true);
        }
    }

    /// <summary>Coalesces bursts of events into one refresh, run later on the UI thread.</summary>
    /// <param name="full">Whether to enumerate the windows again or only re-read the followed one.</param>
    private void QueueGeometryOrRefresh(bool full)
    {
        if (full && !_refreshQueued)
        {
            _refreshQueued = true;
            _ui.Post(_ => Refresh(), null);
        }
        else if (!full && !_refreshQueued && !_geometryQueued)
        {
            _geometryQueued = true;
            _ui.Post(_ => PublishGeometry(), null);
        }
    }
}
