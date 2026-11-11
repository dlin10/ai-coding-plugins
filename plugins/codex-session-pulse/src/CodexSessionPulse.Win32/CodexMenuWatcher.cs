using System.Diagnostics;
using System.Windows.Automation;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>
/// Tells whether something of Codex's covers the overlay's place: its menus, such as the model picker, and its popup
/// panels, such as the "+" panel. They open upward from the input box, under the overlay, so the overlay steps out of
/// their way (docs/compatibility.md §6.4). It reads control types and rectangles only, never names, values or text.
/// <para>
/// <b>Menus</b> raise UI Automation's <c>MenuOpened</c> and <c>MenuClosed</c> on the Codex window. A close is not trusted
/// on its own: a nested menu closes in steps, and the closing element is already gone. So after <c>MenuClosed</c>, and
/// every <see cref="OpenRecheckMs"/> while a menu is open, one search asks whether any element of type <c>Menu</c> is
/// left (about 30 ms of Codex's CPU). The first subscription at the overlay's start can fail (E_FAIL); <see cref="Track"/>
/// then tries again, at most every <see cref="RetryMs"/>.
/// </para>
/// <para>
/// <b>Panels</b> raise no event Codex can be asked for cheaply. So every <see cref="ProbeMs"/> one point just above the
/// overlay (<see cref="SetProbe"/>) is hit-tested, only when Codex's own window is on top there (the median call takes
/// about 3 ms). <see cref="IsPanelAt"/> tells a panel from the thread behind it. The / and @ suggestion lists were not
/// seen at that point.
/// </para>
/// <para>
/// Subscribing, unsubscribing, searching and probing run on the thread pool, never on the UI thread, because a UI
/// Automation call waits on Codex's UI thread. <see cref="IsOpen"/> changes and the callback runs on the UI thread.
/// </para>
/// </summary>
public sealed class CodexMenuWatcher : IDisposable
{
    /// <summary>How long after a <c>MenuClosed</c> the window is searched for menus left open, in milliseconds.</summary>
    public const int CloseRecheckMs = 150;

    /// <summary>How often the window is searched while a menu is open, in case a close event was missed, in milliseconds.</summary>
    public const int OpenRecheckMs = 1000;

    /// <summary>How long after a failed subscription the next <see cref="Track"/> tries again, in milliseconds.</summary>
    public const int RetryMs = 2000;

    /// <summary>How often the point above the overlay is hit-tested for a panel, in milliseconds.</summary>
    public const int ProbeMs = 500;

    /// <summary>How many ancestors of the element at the probe point are looked at for a window-sized layer (measured: 4th).</summary>
    private const int MaxPanelAncestors = 6;

    private static readonly Condition MenuCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu);

    private readonly SynchronizationContext _ui;
    private readonly Action _changed;
    private readonly Timer _recheck;
    private readonly Timer _probeTimer;
    private readonly Lock _gate = new();

    // Shared under _gate.
    private long _lastAttempt;
    private nint _hwnd;
    private int _generation;
    private bool _disposed;
    private bool _probing;
    private Subscription? _subscription;
    private ProbeArea? _probe;

    // Owned by the UI thread.
    private bool _menuOpen;
    private bool _panelOpen;

    /// <summary>Creates a watcher that watches nothing until <see cref="Track"/>.</summary>
    /// <param name="ui">The UI thread's context; <see cref="IsOpen"/> changes and <paramref name="changed"/> runs there.</param>
    /// <param name="changed">Called on the UI thread after <see cref="IsOpen"/> changed.</param>
    public CodexMenuWatcher(SynchronizationContext ui, Action changed)
    {
        _ui = ui;
        _changed = changed;
        _recheck = new Timer(_ => Recheck(), null, Timeout.Infinite, Timeout.Infinite);
        _probeTimer = new Timer(_ => Probe(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Gets whether a Codex menu or panel covers the overlay's place. Read it on the UI thread.</summary>
    public bool IsOpen => _menuOpen || _panelOpen;

    /// <summary>Watches the menus of a Codex window; the same window again changes nothing. Call it on the UI thread.</summary>
    /// <param name="hwnd">The Codex window.</param>
    public void Track(nint hwnd)
    {
        Subscription? old;
        Subscription created = new(hwnd);
        int generation;

        lock (_gate)
        {
            // The same window again changes nothing, unless subscribing failed (it can at the overlay's start): then it
            // is tried again, at most every RetryMs.
            if (_disposed
                || (hwnd == _hwnd && _subscription is { } current
                    && (!current.Failed || Stopwatch.GetElapsedTime(_lastAttempt).TotalMilliseconds < RetryMs)))
            {
                return;
            }

            _lastAttempt = Stopwatch.GetTimestamp();
            bool sameWindow = hwnd == _hwnd;
            old = _subscription;
            _subscription = created;
            _hwnd = hwnd;
            generation = ++_generation;

            if (!sameWindow)
            {
                _probe = null;
            }

            _probeTimer.Change(ProbeMs, Timeout.Infinite);
        }

        _menuOpen = false;
        _panelOpen = false;
        _ = Task.Run(() =>
        {
            old?.Remove();
            created.Add(() => OnOpened(generation), () => ArmRecheck(generation, CloseRecheckMs));
        });
    }

    /// <summary>
    /// Sets the point to hit-test for a panel and what to judge it against. Call it on the UI thread whenever the overlay
    /// is placed over the input box.
    /// </summary>
    /// <param name="x">The point's x, in physical pixels: the input box's centre.</param>
    /// <param name="y">The point's y, in physical pixels: just above the overlay's top edge.</param>
    /// <param name="box">The visible input box.</param>
    /// <param name="window">The Codex window's bounds.</param>
    public void SetProbe(int x, int y, WindowBounds box, WindowBounds window)
    {
        lock (_gate)
        {
            _probe = new ProbeArea(x, y, box, window);
        }
    }

    /// <summary>Stops watching; <see cref="IsOpen"/> becomes false without a callback. Call it on the UI thread.</summary>
    public void Stop()
    {
        Subscription? old;

        lock (_gate)
        {
            old = _subscription;
            _subscription = null;
            _hwnd = 0;
            _probe = null;
            _generation++;

            if (!_disposed)
            {
                _recheck.Change(Timeout.Infinite, Timeout.Infinite);
                _probeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        _menuOpen = false;
        _panelOpen = false;

        if (old is not null)
        {
            _ = Task.Run(old.Remove);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Subscription? old;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            old = _subscription;
            _subscription = null;
            _recheck.Dispose();
            _probeTimer.Dispose();
        }

        if (old is not null)
        {
            // Off the UI thread: removing waits on Codex, which may not answer while the overlay shuts down.
            _ = Task.Run(old.Remove);
        }
    }

    /// <summary>
    /// Tells a Codex panel from the thread behind it at the probe point. The thread's scroll container and the window's
    /// full-size layers span the whole input box. A panel's items do not, and walking up from one reaches a layer the size
    /// of the window before anything much wider than the box, since panels are drawn in a layer of their own (measured: a
    /// "+" item 896 px wide, then 909, 921 and 921 px, then the 2560×1380 layer). Walking up from the thread's own text
    /// reaches its scroll container (2129 px) first.
    /// </summary>
    /// <param name="hit">The rectangle of the element at the probe point.</param>
    /// <param name="ancestors">Its nearest ancestors' rectangles, nearest first; <see langword="null"/> for one without a rectangle.</param>
    /// <param name="box">The visible input box.</param>
    /// <param name="window">The Codex window's bounds.</param>
    /// <returns>Whether a panel covers the point.</returns>
    internal static bool IsPanelAt(WindowBounds hit, IReadOnlyList<WindowBounds?> ancestors, WindowBounds box, WindowBounds window)
    {
        if (hit.Left <= box.Left && hit.Left + hit.Width >= box.Left + box.Width)
        {
            return false;
        }

        for (int i = 0; i < ancestors.Count && i < MaxPanelAncestors; i++)
        {
            if (ancestors[i] is not { } ancestor)
            {
                continue;
            }

            if (ancestor.Width * 20 >= window.Width * 19 && ancestor.Height * 20 >= window.Height * 19)
            {
                return true;
            }

            if (ancestor.Width * 2 >= box.Width * 3)
            {
                return false;
            }
        }

        return false;
    }

    private static WindowBounds? ToBounds(System.Windows.Rect rect) =>
        rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0
            ? null
            : new WindowBounds((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    /// <summary>A menu opened: on the UI thread, say so, and look again later in case its close event is missed.</summary>
    /// <param name="generation">The generation the event's subscription belongs to.</param>
    private void OnOpened(int generation)
    {
        Publish(generation, menu: true);
        ArmRecheck(generation, OpenRecheckMs);
    }

    private void ArmRecheck(int generation, int delayMs)
    {
        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                _recheck.Change(delayMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>On the thread pool: searches the window for a menu left open and publishes the answer.</summary>
    private void Recheck()
    {
        Subscription? subscription;
        int generation;

        lock (_gate)
        {
            subscription = _subscription;
            generation = _generation;
        }

        if (subscription?.Root is not { } root)
        {
            return;
        }

        bool open;

        try
        {
            open = root.FindFirst(TreeScope.Descendants, MenuCondition) is not null;
        }
        catch (Exception)
        {
            // Codex closed or hung mid-call: no menu to step out of the way of. A thread-pool callback must not throw.
            open = false;
        }

        Publish(generation, menu: open);

        if (open)
        {
            ArmRecheck(generation, OpenRecheckMs);
        }
    }

    /// <summary>On the thread pool: hit-tests the probe point, publishes whether a panel covers it, and re-arms.</summary>
    private void Probe()
    {
        ProbeArea? probe;
        nint hwnd;
        int generation;

        lock (_gate)
        {
            if (_disposed || _probing || _hwnd == 0)
            {
                return;
            }

            _probing = true;
            probe = _probe;
            hwnd = _hwnd;
            generation = _generation;
        }

        bool panel = false;

        try
        {
            panel = probe is { } area && IsPanelOver(area, hwnd);
        }
        catch (Exception)
        {
            // Codex closed or hung mid-call: nothing to step out of the way of. A thread-pool callback must not throw.
        }

        Publish(generation, panel: panel);

        lock (_gate)
        {
            _probing = false;

            if (!_disposed && _hwnd != 0)
            {
                _probeTimer.Change(ProbeMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>Hit-tests the probe point, only when Codex's window is the one on top there.</summary>
    /// <param name="area">The probe point and what to judge it against.</param>
    /// <param name="hwnd">The Codex window.</param>
    /// <returns>Whether a panel covers the point.</returns>
    private static bool IsPanelOver(ProbeArea area, nint hwnd)
    {
        // Another app on top there must never get a UI Automation call (it can hang, or build its own tree).
        nint owner = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new NativeMethods.ScreenPoint { X = area.X, Y = area.Y }),
                                               NativeMethods.GaRoot);

        if (owner != hwnd)
        {
            return false;
        }

        CacheRequest cache = new() { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.BoundingRectangleProperty);

        using (cache.Activate())
        {
            AutomationElement hit = AutomationElement.FromPoint(new System.Windows.Point(area.X, area.Y));

            if (ToBounds(hit.Cached.BoundingRectangle) is not { } hitBounds)
            {
                return false;
            }

            List<WindowBounds?> ancestors = [];

            // Only when the hit is narrower than the box: the thread behind the overlay answers in one call.
            if (!(hitBounds.Left <= area.Box.Left && hitBounds.Left + hitBounds.Width >= area.Box.Left + area.Box.Width))
            {
                TreeWalker walker = TreeWalker.RawViewWalker;

                for (AutomationElement? parent = walker.GetParent(hit, cache); parent is not null && ancestors.Count < MaxPanelAncestors;
                     parent = walker.GetParent(parent, cache))
                {
                    ancestors.Add(ToBounds(parent.Cached.BoundingRectangle));
                }
            }

            return IsPanelAt(hitBounds, ancestors, area.Box, area.Window);
        }
    }

    /// <summary>Publishes one of the two answers on the UI thread, when it still belongs to the watched window.</summary>
    /// <param name="generation">The generation the answer belongs to.</param>
    /// <param name="menu">Whether a menu is open, or <see langword="null"/> when this is not the menu answer.</param>
    /// <param name="panel">Whether a panel covers the probe point, or <see langword="null"/> when this is not the panel answer.</param>
    private void Publish(int generation, bool? menu = null, bool? panel = null)
    {
        _ui.Post(_ =>
        {
            bool current;

            lock (_gate)
            {
                current = !_disposed && generation == _generation;
            }

            if (!current)
            {
                return;
            }

            bool before = IsOpen;
            _menuOpen = menu ?? _menuOpen;
            _panelOpen = panel ?? _panelOpen;

            if (IsOpen != before)
            {
                _changed();
            }
        }, null);
    }

    /// <summary>The point above the overlay to hit-test, and what to judge it against.</summary>
    /// <param name="X">The point's x, in physical pixels.</param>
    /// <param name="Y">The point's y, in physical pixels.</param>
    /// <param name="Box">The visible input box.</param>
    /// <param name="Window">The Codex window's bounds.</param>
    private sealed record ProbeArea(int X, int Y, WindowBounds Box, WindowBounds Window);

    /// <summary>
    /// The event handlers on one Codex window, added and removed on the thread pool. <see cref="Add"/> and
    /// <see cref="Remove"/> are serialized, and a subscription removed before it was added never adds its handlers.
    /// </summary>
    /// <param name="hwnd">The Codex window.</param>
    private sealed class Subscription(nint hwnd)
    {
        private readonly Lock _gate = new();
        private AutomationEventHandler? _opened;
        private AutomationEventHandler? _closed;
        private bool _removed;

        /// <summary>Gets the window's element once the handlers are added.</summary>
        public AutomationElement? Root { get; private set; }

        /// <summary>Gets the exception type and HRESULT of a failed add, or <see langword="null"/>.</summary>
        public string? LastError { get; private set; }

        /// <summary>Gets whether adding the handlers failed, so the watcher may try again.</summary>
        public bool Failed => LastError is not null;

        /// <summary>Adds the handlers; a failure (Codex closed) leaves the subscription without them.</summary>
        /// <param name="opened">Called on a UI Automation thread when a menu opens.</param>
        /// <param name="closed">Called on a UI Automation thread when a menu closes.</param>
        public void Add(Action opened, Action closed)
        {
            lock (_gate)
            {
                if (_removed)
                {
                    return;
                }

                try
                {
                    AutomationElement root = AutomationElement.FromHandle(hwnd);
                    _opened = (_, _) => opened();
                    _closed = (_, _) => closed();
                    Automation.AddAutomationEventHandler(AutomationElement.MenuOpenedEvent, root, TreeScope.Subtree, _opened);
                    Automation.AddAutomationEventHandler(AutomationElement.MenuClosedEvent, root, TreeScope.Subtree, _closed);
                    Root = root;
                }
                catch (Exception exception)
                {
                    // Measured: E_FAIL from the first try at the overlay's start; the retry a few seconds later works.
                    LastError = $"{exception.GetType().Name} 0x{exception.HResult:X8}";
                }
            }
        }

        /// <summary>Removes the handlers that were added, and keeps a later <see cref="Add"/> from adding them.</summary>
        public void Remove()
        {
            lock (_gate)
            {
                _removed = true;

                if (Root is not { } root)
                {
                    return;
                }

                try
                {
                    Automation.RemoveAutomationEventHandler(AutomationElement.MenuOpenedEvent, root, _opened);
                    Automation.RemoveAutomationEventHandler(AutomationElement.MenuClosedEvent, root, _closed);
                }
                catch (Exception)
                {
                    // The window is gone; UI Automation drops its handlers with it.
                }

                Root = null;
            }
        }
    }
}
