using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.App.SettingsUi;
using CodexSessionPulse.App.Stub;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Desktop;
using CodexSessionPulse.Infrastructure.Rollouts;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// Wires the overlay together on the UI thread: settings, the Codex window anchor, the input card locator, the overlay
/// window and its placement,
/// the tray, diagnostics and the 1 s display clock, and the pipeline behind it: the <see cref="RolloutTailer"/> (the
/// foundation), the <see cref="DesktopRouteWatcher"/> (which thread Codex shows) and the hook pipe (an accelerator) feed
/// one <see cref="PulseEngine"/>, whose snapshots reach <see cref="UpdateCache"/> through the dispatcher. <c>--stub</c>
/// replaces the pipeline with <see cref="StubSnapshotScript"/>.
/// The overlay exits after the last Codex window is gone (<see cref="LastWindowExit"/>), from tray Exit or on
/// <c>WM_CLOSE</c>, all through <see cref="RequestExit"/>.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private const int StubStepSeconds = 5;
    private const int PruneEveryTicks = 60;

    /// <summary>How far above the line's top edge the menu watcher hit-tests for a Codex panel, in physical pixels.</summary>
    private const int ProbeAboveLinePx = 6;

    /// <summary>How far a hook's own timestamp may be from the overlay's clock before the arrival time is used instead.</summary>
    private static readonly TimeSpan MaxHookClockSkew = TimeSpan.FromMinutes(5);

    private readonly PulsePaths _paths;
    private readonly SettingsStore _store;
    private readonly RollingFileLog _log;
    private readonly bool _stub;
    private readonly HookPipeServer? _pipe;
    private readonly JobMembership _job;
    private readonly CancellationTokenSource _stopping = new();
    private readonly LastWindowExit _exit = new(LastWindowExit.DefaultGracePeriod);
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly OverlayViewModel _viewModel = new(TimeProvider.System);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _stubTimer = new() { Interval = TimeSpan.FromSeconds(StubStepSeconds) };
    private PulseSettings _settings = new();
    private IReadOnlyList<string> _settingsProblems = [];
    private OverlayWindow? _window;
    private CodexWindowAnchor? _anchor;
    private IDisposable? _anchorSubscription;
    private CodexInputLocator? _input;
    private CodexMenuWatcher? _menus;
    private bool _loggedInputFound;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _userHidden;
    private int _stubStep;
    private AnchorState? _loggedState;
    private int _loggedWindows = -1;
    private Pipeline? _pipeline;
    private CacheSnapshot? _pendingSnapshot;
    private CacheState? _loggedCacheState;
    private int _ticks;
    private bool _exiting;
    private int _loggedRecheckWindows = -1;
    private readonly HookRouter _hookRouter = new();
    private int _hooksReceived;
    private readonly Lock _routeGate = new();
    private DesktopRouteWatcher? _routes;
    private CodexRoute? _route;
    private int _routeProcessId;

    /// <summary>Creates the controller; <see cref="Start"/> does the work.</summary>
    /// <param name="startup">The bootstrap's files, log, hook pipe and job membership.</param>
    /// <param name="stub">Whether the development stub drives the cache snapshots.</param>
    public OverlayController(OverlayStartup startup, bool stub)
    {
        _paths = startup.Paths;
        _log = startup.Log;
        _pipe = startup.Pipe;
        _job = startup.Job;
        _stub = stub;
        _store = new SettingsStore(_paths.SettingsFile, _userProfile);
    }

    /// <summary>Loads the settings, creates the window, the anchor and the tray, and starts the clocks and the pipeline.</summary>
    public void Start()
    {
        SettingsLoadResult loaded = _store.Load();
        _settings = loaded.Settings;
        _settingsProblems = loaded.Problems;
        _log.Info($"overlay start: version {AppVersion}, stub {_stub}, settings problems [{string.Join(", ", loaded.Problems)}]");

        _window = new OverlayWindow(_viewModel);
        _window.ChooseFolderRequested += (_, _) => ChooseFolder();
        _window.SystemSettingChanged += (_, _) => ApplyTheme();
        _window.SizeChanged += (_, _) => Reposition();
        _window.DpiChanged += (_, _) => Reposition();
        ApplyTheme();

        _anchor = new CodexWindowAnchor(ToDetectorOptions(_settings), SynchronizationContext.Current!);
        _anchorSubscription = _anchor.Snapshots.Subscribe(new Observer<AnchorSnapshot>(OnAnchor));
        _input = new CodexInputLocator(SynchronizationContext.Current!, OnInputChanged);
        _menus = new CodexMenuWatcher(SynchronizationContext.Current!, OnMenusChanged);

        _tray = new TrayIcon("Codex Session Pulse",
        [
            new TrayMenuItem("Show/Hide", ToggleHidden),
            new TrayMenuItem("Re-detect Codex windows", () => _anchor.Redetect()),
            new TrayMenuItem("Settings", OpenSettings),
            new TrayMenuItem("Copy diagnostics", CopyDiagnostics),
            new TrayMenuItem(null, () => { }),
            new TrayMenuItem("Exit", () => RequestExit("tray Exit")),
        ]);
        _tray.CommandFailed += exception => _log.Error("tray command failed", exception);
        _tray.CloseRequested += () => RequestExit("WM_CLOSE received");
        _log.Info($"tray icon {(_tray.IsAdded ? "added" : "not added")}; overlay ex-style 0x{_window.ExtendedStyle:X8}");

        UpdateCache(StubOrEmpty());

        if (_stub)
        {
            _stubTimer.Tick += (_, _) => UpdateCache(StubSnapshotScript.At(++_stubStep, DateTimeOffset.UtcNow));
            _stubTimer.Start();
        }
        else
        {
            _routes = new DesktopRouteWatcher(SystemClock.Instance);
            _routes.RouteChanged += OnRoute;
            StartPipeline();
        }

        _anchor.Start();

        _clock.Tick += (_, _) => OnClock();
        _clock.Start();

        if (_pipe is not null)
        {
            _ = Task.Run(() => ReadHooksAsync(_pipe, _stopping.Token));
        }
    }

    /// <summary>Gets the hook counter that Copy diagnostics reports; every received hook is recorded into it.</summary>
    public HookActivityRecorder Hooks { get; } = new();

    /// <summary>Shows a new cache snapshot, on the UI thread; a page with nothing to show hides the overlay, and back.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public void UpdateCache(CacheSnapshot snapshot)
    {
        bool hadContent = _viewModel.View?.HasContent != false;
        _viewModel.Update(snapshot);
        bool hasContent = _viewModel.View?.HasContent != false;

        if (hasContent != hadContent)
        {
            Reposition();
        }
    }

    /// <summary>Ends the overlay through the one shutdown path that tray Exit, <c>WM_CLOSE</c> and the last window share.</summary>
    /// <param name="reason">Why, for the log.</param>
    public void RequestExit(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.Info($"{reason}, exiting");
        Application.Current.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _clock.Stop();
        _stubTimer.Stop();
        _stopping.Cancel();
        _routes?.Dispose();
        StopPipeline();
        _anchorSubscription?.Dispose();
        _anchor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _input?.Dispose();
        _menus?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _log.Info("overlay stop");
    }

    private static string AppVersion =>
        typeof(OverlayController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static WindowDetectorOptions ToDetectorOptions(PulseSettings settings) =>
        new(settings.CodexProcessNames, settings.CodexWindowTitleFilter, settings.CodexExecutablePathFilters);

    private CacheSnapshot StubOrEmpty()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return _stub
            ? StubSnapshotScript.At(0, now)
            : new CacheSnapshot(now, CacheState.NoSession, null, ThreadKind.Unknown, new ThreadSelection(SelectionConfidence.Unknown, null),
                                null, null, null, null, new CacheTtl(null, null, TtlBasis.Unknown), null,
                                new SourceInfo(SourceStatus.NotFound, null, null), null, null, null);
    }

    private void OnAnchor(AnchorSnapshot snapshot)
    {
        if (snapshot.State != _loggedState || snapshot.CodexWindows != _loggedWindows)
        {
            bool windowsChanged = snapshot.CodexWindows != _loggedWindows;
            _loggedState = snapshot.State;
            _loggedWindows = snapshot.CodexWindows;
            _log.Info($"anchor {snapshot.State}, codex windows {snapshot.CodexWindows}, dpi {snapshot.DpiScale}");

            if (windowsChanged)
            {
                _pipeline?.Engine.OnCodexWindows(snapshot.CodexWindows);
            }
        }

        FollowCodexLog(snapshot.Hwnd);

        bool wasPending = _exit.Pending;
        _exit.OnAnchor(snapshot.State, DateTimeOffset.UtcNow);

        if (_exit.Pending != wasPending)
        {
            _loggedRecheckWindows = -1;
            _log.Info(_exit.Pending ? "last Codex window gone; exit pending after the grace period" : "a Codex window is back; exit cancelled");
        }

        _viewModel.Update(snapshot);
        Reposition();
    }

    /// <summary>
    /// Points the route watcher at the log of the process that owns the Codex window, once per process: Codex restarted
    /// means a new process, a new log and no route until it names one.
    /// </summary>
    /// <param name="hwnd">The anchor's Codex window, or 0.</param>
    private void FollowCodexLog(nint hwnd)
    {
        int processId = _routes is null || hwnd == 0 ? 0 : WindowEnumerator.GetProcessId(hwnd);

        if (processId == 0 || processId == _routeProcessId)
        {
            return;
        }

        _routeProcessId = processId;
        string? family = WindowEnumerator.GetPackageFamilyName(processId);
        _log.Info($"following the Codex Desktop log of its main process{(family is null ? " (no package)" : string.Empty)}");
        _routes!.Follow(DesktopLogTail.LogsRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), family), processId);
    }

    /// <summary>
    /// Takes the page Codex shows, on the watcher's thread: the engine chooses by it, and the rollout reader is asked to
    /// read the routed thread in case discovery left it out. Logged by kind only, never by thread id.
    /// </summary>
    /// <param name="route">The latest route, or <see langword="null"/> when Codex's log names none.</param>
    private void OnRoute(CodexRoute? route)
    {
        try
        {
            _log.Info(route is null ? "codex page unknown; hooks and JSONL choose the thread" : $"codex page {route.Kind}");

            lock (_routeGate)
            {
                _route = route;
                ApplyRoute(_pipeline, route);
            }
        }
        catch (Exception exception)
        {
            // The watcher's timer thread: an escaping exception would end the process.
            _log.Error("codex page update failed", exception);
        }
    }

    /// <summary>Gives a pipeline the route: the engine's choice and, for a local thread, its rollout.</summary>
    /// <param name="pipeline">The pipeline, or <see langword="null"/> while there is none.</param>
    /// <param name="route">The route, or <see langword="null"/>.</param>
    private static void ApplyRoute(Pipeline? pipeline, CodexRoute? route)
    {
        if (pipeline is null)
        {
            return;
        }

        pipeline.Engine.OnRoute(route);

        if (route is { Kind: CodexRouteKind.LocalThread, ThreadId: { } threadId })
        {
            pipeline.Tailer.Follow(threadId);
        }
    }

    /// <summary>A Codex menu or panel opened over the line, or all closed: hide the overlay out of its way, or show it again.</summary>
    private void OnMenusChanged()
    {
        _log.Info(_menus?.IsOpen == true ? "codex menu or panel over the line; overlay hidden" : "codex menus and panels closed");
        Reposition();
    }

    /// <summary>The input card was found, moved, resized or lost: place the overlay again.</summary>
    private void OnInputChanged()
    {
        bool found = _input?.Current is not null;

        if (found != _loggedInputFound)
        {
            _loggedInputFound = found;
            _log.Info(found ? "input card found; placing above it" : "input card not seen; placing by the window");
        }

        Reposition();
    }

    /// <summary>
    /// The 1 s display clock: the view, the overlay's placement (minimized by another process, or in the wrong z-order band
    /// after Codex was pinned or unpinned),
    /// the engine's time-based states, the source status, pruning and the exit check.
    /// </summary>
    private void OnClock()
    {
        _viewModel.Tick();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_window is { IsVisible: true } && _anchor?.Current.Hwnd is { } codex && codex != 0
            && !OverlayWindowInterop.IsPlacedFor(_window.Handle, codex))
        {
            Reposition();
        }

        if (_window is { IsVisible: true } && _settings.Anchor == OverlayAnchor.AboveCodexInput && _anchor?.Current.Hwnd is { } shown && shown != 0)
        {
            // Retries a menu subscription that failed; nothing to do otherwise.
            _menus?.Track(shown);
        }

        if (_pipeline is { } pipeline)
        {
            SourceStatus status = pipeline.Tailer.Status;

            if (status != pipeline.LastStatus)
            {
                pipeline.LastStatus = status;
                _log.Info($"rollout source {status}");
                pipeline.Engine.OnSourceStatus(status, status == SourceStatus.NotFound ? DiagnosticCodes.SessionRootNotFound : null);
            }

            if (++_ticks % PruneEveryTicks == 0 && pipeline.Engine.Prune() is > 0 and int dropped)
            {
                _log.Info($"pruned {dropped} idle threads");
            }

            pipeline.Engine.Tick();
        }

        if (_exit.ShouldRecheck(now))
        {
            int windows = WindowEnumerator.CountCodexWindowsIncludingCloaked(ToDetectorOptions(_settings));

            if (_exit.OnRecheck(windows, now))
            {
                RequestExit("no Codex window left after the grace period");
            }
            else if (windows != _loggedRecheckWindows)
            {
                // Only cloaked windows left, as on another virtual desktop: the recheck repeats, the log line does not.
                _loggedRecheckWindows = windows;
                _log.Info($"recheck found {windows} Codex windows (cloaked included); still running");
            }
        }
    }

    /// <summary>Starts the rollout tailer and the engine for the current settings.</summary>
    private void StartPipeline()
    {
        CacheTrackerOptions trackerOptions = new(TimeSpan.FromMinutes(_settings.EstimatedCacheTtlMinutes), _settings.MinCachedTokensForWarm,
                                                 CacheTrackerOptions.Default.MinComparablePrefix);
        string root = _settings.SessionRootOverride ?? RolloutTailerOptions.DefaultRoot(Environment.GetEnvironmentVariable);
        PulseEngine engine = new(trackerOptions, SystemClock.Instance);
        RolloutTailer tailer = new(RolloutTailerOptions.ForRoot(root), SystemClock.Instance);
        Pipeline pipeline = new(engine, tailer, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));

        tailer.ThreadObserved += observation => engine.Observe(observation);
        tailer.RequestTimed += timing => engine.ApplyTiming(timing);
        pipeline.Subscription = engine.Snapshots.Subscribe(new Observer<CacheSnapshot>(OnEngineSnapshot));
        engine.OnCodexWindows(Math.Max(_loggedWindows, 0));
        // After a settings save the new engine starts with the selection the hooks confirmed, not only the JSONL guess.
        _hookRouter.Replay(engine);

        lock (_routeGate)
        {
            // Under the gate, so a route that arrives meanwhile reaches this pipeline after, not before, the replayed one.
            _pipeline = pipeline;
            ApplyRoute(pipeline, _route);
        }

        _ = Task.Run(() => TailAsync(pipeline));
        _log.Info($"pipeline start: rollouts {(_settings.SessionRootOverride is null ? "default root" : "override root")}, ttl {_settings.EstimatedCacheTtlMinutes} min");
    }

    private void StopPipeline()
    {
        if (_pipeline is not { } pipeline)
        {
            return;
        }

        _pipeline = null;
        pipeline.Stop.Cancel();
        pipeline.Subscription?.Dispose();
        pipeline.Tailer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Feeds the engine from the tailer on the thread pool; a failure turns the snapshot into Error.</summary>
    /// <param name="pipeline">The pipeline to run.</param>
    private async Task TailAsync(Pipeline pipeline)
    {
        try
        {
            await foreach (TokenUsageSnapshot usage in pipeline.Tailer.WatchAsync(pipeline.Stop.Token).ConfigureAwait(false))
            {
                pipeline.Engine.Apply(usage);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception exception)
        {
            _log.Error("rollout reader failed", exception);
            pipeline.Engine.OnSourceStatus(SourceStatus.Failed, DiagnosticCodes.SourceFailed);
        }
    }

    /// <summary>Applies hook messages strictly in arrival order, on one thread-pool reader.</summary>
    /// <param name="pipe">The hook pipe.</param>
    /// <param name="token">Ends the reading.</param>
    private async Task ReadHooksAsync(HookPipeServer pipe, CancellationToken token)
    {
        try
        {
            await foreach (HookMessage message in pipe.Messages.ReadAllAsync(token).ConfigureAwait(false))
            {
                OnHook(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception exception)
        {
            _log.Error("hook reader failed", exception);
        }
    }

    /// <summary>
    /// Counts a hook and routes it into the engine. Only the three events of hooks.json reach the engine, and only with a
    /// thread id from <c>transcript_path</c>: an ephemeral suggestion thread (no path) and any other event, such as
    /// <c>SubagentStart</c> (counted as <see cref="HookMessage.Other"/>), are only counted, so a hook alone never puts a
    /// subagent or a suggestion thread on display.
    /// </summary>
    /// <param name="message">The message.</param>
    private void OnHook(HookMessage message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset at = (message.AtUtc - now).Duration() > MaxHookClockSkew ? now : message.AtUtc;
        Hooks.Record(message.Event, at);
        _log.Info($"hook {message.Event}{(message.Source is { } source ? $" ({source})" : string.Empty)}, "
                  + $"thread {(message.ThreadId is null ? "none" : "from transcript_path")}");

        _hookRouter.Route(_pipeline?.Engine, message, at);

        // One reader, so the dispatcher receives the counts in order.
        int received = Interlocked.Increment(ref _hooksReceived);
        _window?.Dispatcher.BeginInvoke(() => _viewModel.UpdateHooks(received));
    }

    /// <summary>Takes an engine snapshot on any thread and shows the latest one on the UI thread; bursts collapse into one update.</summary>
    /// <param name="snapshot">The snapshot.</param>
    private void OnEngineSnapshot(CacheSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _pendingSnapshot, snapshot) is null)
        {
            _window?.Dispatcher.BeginInvoke(ShowPendingSnapshot);
        }
    }

    private void ShowPendingSnapshot()
    {
        if (Interlocked.Exchange(ref _pendingSnapshot, null) is not { } latest)
        {
            return;
        }

        if (latest.State != _loggedCacheState)
        {
            _loggedCacheState = latest.State;
            _log.Info($"cache {latest.State}, selection {latest.Selection.Confidence} ({latest.Selection.Reason ?? "none"})");
        }

        UpdateCache(latest);
    }

    /// <summary>
    /// Shows, hides and places the overlay for the current anchor, settings and line. It hides on a page with nothing to
    /// show. Over the input card it is as wide as the visible input box; placed by the window, as wide as its text. While it is shown
    /// with <see cref="OverlayAnchor.AboveCodexInput"/>, the input locator and the menu watcher follow the anchor window,
    /// and the overlay hides while a Codex menu is open, since those open upward under it; otherwise both stop, so no UI
    /// Automation call reaches Codex while the overlay is hidden for another reason.
    /// </summary>
    private void Reposition()
    {
        if (_window is null || _anchor is null)
        {
            return;
        }

        AnchorSnapshot anchor = _anchor.Current;

        if (!OverlayVisibility.ShouldShow(anchor.State, _settings.HideWhenCodexInactive, _userHidden)
            || _viewModel.View is { HasContent: false }
            || anchor.Bounds is not { } codex || anchor.WorkArea is not { } workArea)
        {
            _input?.Stop();
            _menus?.Stop();
            HideWindow();

            return;
        }

        InputSighting? input = null;

        if (_settings.Anchor == OverlayAnchor.AboveCodexInput && _input is not null)
        {
            _input.Track(anchor.Hwnd);
            _menus?.Track(anchor.Hwnd);
            input = _input.Current;

            if (_menus?.IsOpen == true)
            {
                // The watcher keeps listening, so the menu's close shows the overlay again.
                HideWindow();

                return;
            }
        }
        else
        {
            _input?.Stop();
            _menus?.Stop();
        }

        FrameworkElement content = (FrameworkElement)_window.Content;
        content.Width = input is { } seen ? (seen.Box ?? seen.Card).Width / anchor.DpiScale : double.NaN;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        WindowBounds bounds = OverlayPlacement.Compute(codex, input, workArea, content.DesiredSize.Width, content.DesiredSize.Height,
                                                       anchor.DpiScale, _settings.OffsetX, _settings.OffsetY);

        if (input is { } placed)
        {
            // A panel that opens upward from the input box covers this point just above the line (§6.4).
            WindowBounds box = placed.Box ?? placed.Card;
            _menus?.SetProbe(box.Left + (box.Width / 2), bounds.Top - ProbeAboveLinePx, box, codex);
        }

        OverlayWindowInterop.PlaceAbove(_window.Handle, anchor.Hwnd, bounds.Left, bounds.Top);

        if (!_window.IsVisible)
        {
            // ShowActivated is false, so WPF shows it with SW_SHOWNA: no activation, Codex keeps the focus.
            _window.Show();
        }
    }

    private void HideWindow()
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }
    }

    private void ApplyTheme()
    {
        if (_window is not null)
        {
            ThemePalette.Apply(_window.Resources, ThemePalette.IsDark(_settings.Theme));
        }
    }

    private void ToggleHidden()
    {
        _userHidden = !_userHidden;
        _log.Info(_userHidden ? "hidden from the tray" : "shown from the tray");
        Reposition();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();

            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _store.Save, _userProfile);
        _settingsWindow.Closed += (_, _) =>
        {
            if (_settingsWindow?.Saved is { } saved)
            {
                ApplySettings(saved);
            }

            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ChooseFolder()
    {
        string? folder = SettingsWindow.PickSessionRoot(null, _userProfile, _settings.SessionRootOverride);

        if (folder is null)
        {
            return;
        }

        PulseSettings updated = _settings with { SessionRootOverride = folder };
        IReadOnlyList<string> problems = _store.Save(updated);

        if (problems.Count == 0)
        {
            ApplySettings(updated);
        }
        else
        {
            _log.Warn($"rollouts folder not saved: {string.Join(", ", problems)}");
        }
    }

    private void ApplySettings(PulseSettings settings)
    {
        PulseSettings previous = _settings;
        _settings = settings;
        _settingsProblems = [];
        _log.Info("settings saved and applied");
        ApplyTheme();
        _anchor?.UpdateOptions(ToDetectorOptions(settings));
        Reposition();

        if (!_stub && (previous.SessionRootOverride != settings.SessionRootOverride
                       || previous.EstimatedCacheTtlMinutes != settings.EstimatedCacheTtlMinutes
                       || previous.MinCachedTokensForWarm != settings.MinCachedTokensForWarm))
        {
            // The root and the tracker settings are fixed per tailer and engine: start both again from the JSONL tail.
            StopPipeline();
            StartPipeline();
        }
    }

    private void CopyDiagnostics()
    {
        if (_anchor is null || _window is null)
        {
            return;
        }

        PipelineDiagnostics pipeline = new(_pipe is not null, _pipe?.Rejected ?? 0, _job, _pipeline?.Tailer.Status,
                                           _pipeline?.Tailer.Diagnostics.Snapshot() ?? new Dictionary<string, long>(), _exit.Pending);
        DiagnosticsInput input = new(AppVersion, RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
                                     _settings, _settingsProblems, _paths.SettingsFile, _log.CurrentFile, _anchor.GetDiagnostics(),
                                     _anchor.Current, _viewModel.Cache, Hooks.Snapshot(), _stub, _tray?.IsAdded == true, _window.IsVisible, _userHidden,
                                     _window.ExtendedStyle, pipeline, _input?.GetDiagnostics(), _routes?.GetDiagnostics());
        string report = DiagnosticsReport.Build(input, _userProfile, Environment.UserName);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(report);
                _log.Info("diagnostics copied");

                return;
            }
            catch (COMException exception) when (attempt < 3)
            {
                // Another process holds the clipboard open; try again shortly.
                _log.Warn($"clipboard busy (0x{exception.HResult:X8}), retrying");
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>One engine with its tailer; replaced as a whole when the rollouts root or the tracker settings change.</summary>
    /// <param name="Engine">The engine.</param>
    /// <param name="Tailer">The rollout reader feeding it.</param>
    /// <param name="Stop">Ends the tail loop.</param>
    private sealed record Pipeline(PulseEngine Engine, RolloutTailer Tailer, CancellationTokenSource Stop)
    {
        /// <summary>Gets or sets the engine subscription.</summary>
        public IDisposable? Subscription { get; set; }

        /// <summary>Gets or sets the source status last passed to the engine.</summary>
        public SourceStatus LastStatus { get; set; } = SourceStatus.Available;
    }

    /// <summary>Forwards anchor or engine snapshots to a handler.</summary>
    /// <typeparam name="T">The snapshot type.</typeparam>
    /// <param name="onNext">The handler.</param>
    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        /// <inheritdoc />
        public void OnCompleted()
        {
        }

        /// <inheritdoc />
        public void OnError(Exception error)
        {
        }

        /// <inheritdoc />
        public void OnNext(T value) => onNext(value);
    }
}
