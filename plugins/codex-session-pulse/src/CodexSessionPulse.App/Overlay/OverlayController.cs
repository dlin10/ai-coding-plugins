using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.SettingsUi;
using CodexSessionPulse.App.Stub;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Settings;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// Wires the overlay together on the UI thread: settings, the Codex window anchor, the overlay window and its placement,
/// the tray, diagnostics and the 1 s display clock. The cache snapshots come from <see cref="UpdateCache"/>; until the
/// real pipeline is connected, <c>--stub</c> drives it from <see cref="StubSnapshotScript"/>.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private const int StubStepSeconds = 5;

    private readonly PulsePaths _paths;
    private readonly SettingsStore _store;
    private readonly RollingFileLog _log;
    private readonly bool _stub;
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly OverlayViewModel _viewModel = new(TimeProvider.System);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _stubTimer = new() { Interval = TimeSpan.FromSeconds(StubStepSeconds) };
    private PulseSettings _settings = new();
    private IReadOnlyList<string> _settingsProblems = [];
    private OverlayWindow? _window;
    private CodexWindowAnchor? _anchor;
    private IDisposable? _anchorSubscription;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _userHidden;
    private int _stubStep;
    private AnchorState? _loggedState;
    private int _loggedWindows = -1;

    /// <summary>Creates the controller; <see cref="Start"/> does the work.</summary>
    /// <param name="paths">The overlay's files.</param>
    /// <param name="log">The overlay log.</param>
    /// <param name="stub">Whether the development stub drives the cache snapshots.</param>
    public OverlayController(PulsePaths paths, RollingFileLog log, bool stub)
    {
        _paths = paths;
        _log = log;
        _stub = stub;
        _store = new SettingsStore(paths.SettingsFile, _userProfile);
    }

    /// <summary>Loads the settings, creates the window, the anchor and the tray, and starts the clocks.</summary>
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
        _anchorSubscription = _anchor.Snapshots.Subscribe(new AnchorObserver(OnAnchor));

        _tray = new TrayIcon("Codex Session Pulse",
        [
            new TrayMenuItem("Show/Hide", ToggleHidden),
            new TrayMenuItem("Re-detect Codex windows", () => _anchor.Redetect()),
            new TrayMenuItem("Settings", OpenSettings),
            new TrayMenuItem("Copy diagnostics", CopyDiagnostics),
            new TrayMenuItem(null, () => { }),
            new TrayMenuItem("Exit", () => Application.Current.Shutdown()),
        ]);
        _tray.CommandFailed += exception => _log.Error("tray command failed", exception);
        _tray.CloseRequested += () =>
        {
            _log.Info("WM_CLOSE received, exiting");
            Application.Current.Shutdown();
        };
        _log.Info($"tray icon {(_tray.IsAdded ? "added" : "not added")}; overlay ex-style 0x{_window.ExtendedStyle:X8}");

        UpdateCache(StubOrEmpty());
        _anchor.Start();

        _clock.Tick += (_, _) => _viewModel.Tick();
        _clock.Start();

        if (_stub)
        {
            _stubTimer.Tick += (_, _) => UpdateCache(StubSnapshotScript.At(++_stubStep, DateTimeOffset.UtcNow));
            _stubTimer.Start();
        }
    }

    /// <summary>Gets the hook counter that Copy diagnostics reports; the integration step records each received hook into it.</summary>
    public HookActivityRecorder Hooks { get; } = new();

    /// <summary>Shows a new cache snapshot. The integration step calls this from the real pipeline, on the UI thread.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public void UpdateCache(CacheSnapshot snapshot) => _viewModel.Update(snapshot);

    /// <inheritdoc />
    public void Dispose()
    {
        _clock.Stop();
        _stubTimer.Stop();
        _anchorSubscription?.Dispose();
        _anchor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            _loggedState = snapshot.State;
            _loggedWindows = snapshot.CodexWindows;
            _log.Info($"anchor {snapshot.State}, codex windows {snapshot.CodexWindows}, dpi {snapshot.DpiScale}");
        }

        _viewModel.Update(snapshot);
        Reposition();
    }

    /// <summary>Shows, hides and places the overlay for the current anchor and settings.</summary>
    private void Reposition()
    {
        if (_window is null || _anchor is null)
        {
            return;
        }

        AnchorSnapshot anchor = _anchor.Current;

        if (!OverlayVisibility.ShouldShow(anchor.State, _settings.HideWhenCodexInactive, _userHidden)
            || anchor.Bounds is not { } codex || anchor.WorkArea is not { } workArea)
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }

            return;
        }

        FrameworkElement content = (FrameworkElement)_window.Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        WindowBounds bounds = OverlayPlacement.Compute(codex, workArea, content.DesiredSize.Width, content.DesiredSize.Height,
                                                       anchor.DpiScale, _settings.OffsetX, _settings.OffsetY);

        OverlayWindowInterop.PlaceAbove(_window.Handle, anchor.Hwnd, bounds.Left, bounds.Top);

        if (!_window.IsVisible)
        {
            // ShowActivated is false, so WPF shows it with SW_SHOWNA: no activation, Codex keeps the focus.
            _window.Show();
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
        _settings = settings;
        _settingsProblems = [];
        _log.Info("settings saved and applied");
        ApplyTheme();
        _anchor?.UpdateOptions(ToDetectorOptions(settings));
        Reposition();
    }

    private void CopyDiagnostics()
    {
        if (_anchor is null || _window is null)
        {
            return;
        }

        DiagnosticsInput input = new(AppVersion, RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
                                     _settings, _settingsProblems, _paths.SettingsFile, _log.CurrentFile, _anchor.GetDiagnostics(),
                                     _anchor.Current, _viewModel.Cache, Hooks.Snapshot(), _stub, _tray?.IsAdded == true, _window.IsVisible, _userHidden,
                                     _window.ExtendedStyle);
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

    /// <summary>Forwards anchor snapshots to a handler.</summary>
    /// <param name="onNext">The handler.</param>
    private sealed class AnchorObserver(Action<AnchorSnapshot> onNext) : IObserver<AnchorSnapshot>
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
        public void OnNext(AnchorSnapshot value) => onNext(value);
    }
}
