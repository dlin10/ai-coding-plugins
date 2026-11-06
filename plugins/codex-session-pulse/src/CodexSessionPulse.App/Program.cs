namespace CodexSessionPulse.App;

/// <summary>
/// The entry point of <c>CodexSessionPulse.exe</c>. Nothing here may touch a WPF type: the
/// <c>--hook</c> path has to stay free of <c>PresentationFramework</c> to meet its warm-start budget.
/// </summary>
internal static class Program
{
    /// <summary>Runs the mode selected on the command line.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The process exit code; <c>--hook</c> always returns 0.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        LaunchOptions options = LaunchOptions.Parse(args);

        if (options.Mode == LaunchMode.Hook)
        {
            return HookRunner.Run(Console.OpenStandardInput(), Console.Out, Environment.GetEnvironmentVariable);
        }

        if (options.Mode == LaunchMode.Overlay)
        {
            return OverlayHost.Run();
        }

        return 2;
    }
}
