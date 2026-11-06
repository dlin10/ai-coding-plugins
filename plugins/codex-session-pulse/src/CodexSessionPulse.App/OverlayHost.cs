using System.Runtime.CompilerServices;
using System.Windows;

namespace CodexSessionPulse.App;

/// <summary>
/// The <c>--overlay</c> path. It is the only class that touches WPF; <c>Program</c> reaches it
/// through a non-inlined call so the <c>--hook</c> path never JIT-loads the WPF assemblies.
/// </summary>
internal static class OverlayHost
{
    /// <summary>Creates the WPF application. A placeholder: it shuts down at once and shows no window yet.</summary>
    /// <returns>The process exit code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        Application application = new();
        application.Shutdown();

        return 0;
    }
}
