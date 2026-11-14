using System.Windows;
using System.Windows.Interop;
using CodexSessionPulse.Win32;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// The compact overlay line (spec §3.1, §7.1). It is never activated: the native window gets <c>WS_EX_NOACTIVATE</c> and
/// <c>WS_EX_TOOLWINDOW</c> before it is first shown, clicks answer <c>MA_NOACTIVATE</c>, and the controller moves it only
/// with <c>SetWindowPos(SWP_NOACTIVATE | SWP_NOOWNERZORDER)</c>.
/// </summary>
internal partial class OverlayWindow : Window
{
    private const int WmMouseActivate = 0x0021;
    private const int WmSettingChange = 0x001A;
    private const int MaNoActivate = 3;

    /// <summary>Creates the window and its native handle without showing it.</summary>
    /// <param name="viewModel">The view model.</param>
    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Handle = new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Raised when the user clicks Choose folder.</summary>
    public event EventHandler? ChooseFolderRequested;

    /// <summary>Raised when Windows reports a settings change, such as the app theme.</summary>
    public event EventHandler? SystemSettingChanged;

    /// <summary>Gets the native handle.</summary>
    public nint Handle { get; }

    /// <summary>Gets the extended style set on the native window, for diagnostics.</summary>
    public long ExtendedStyle { get; private set; }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint handle = new WindowInteropHelper(this).Handle;
        ExtendedStyle = OverlayWindowInterop.ApplyOverlayStyles(handle);
        HwndSource.FromHwnd(handle)!.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;

            return MaNoActivate;
        }

        if (message == WmSettingChange)
        {
            SystemSettingChanged?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e) => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);
}
