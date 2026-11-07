using System.Runtime.CompilerServices;
using System.Windows;
using CodexSessionPulse.App.Diagnostics;
using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.Infrastructure.Settings;

namespace CodexSessionPulse.App;

/// <summary>
/// The <c>--overlay</c> path. It is the only entry into WPF; <c>Program</c> reaches it through a non-inlined call so
/// the <c>--hook</c> path never JIT-loads the WPF assemblies.
/// </summary>
internal static class OverlayHost
{
    /// <summary>Runs the overlay until Exit is chosen from the tray.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>The process exit code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(LaunchOptions options)
    {
        PulsePaths paths = PulsePaths.FromEnvironment(Environment.GetEnvironmentVariable);
        RollingFileLog log = new(paths.LogsDirectory);
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OverlayController controller = new(paths, log, options.Stub);

        application.DispatcherUnhandledException += (_, e) =>
        {
            // The overlay is a passive display: log, keep running, never show a crash dialog over Codex.
            log.Error("unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        application.Startup += (_, _) => controller.Start();
        application.Exit += (_, _) => controller.Dispose();

        return application.Run();
    }
}
