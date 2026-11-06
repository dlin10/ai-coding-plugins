namespace CodexSessionPulse.App;

/// <summary>The mode the process runs in.</summary>
internal enum LaunchMode
{
    /// <summary>No recognised mode was given.</summary>
    None,

    /// <summary>A short-lived hook invocation.</summary>
    Hook,

    /// <summary>The long-lived WPF overlay.</summary>
    Overlay,
}

/// <summary>The parsed command line.</summary>
/// <param name="Mode">The selected mode.</param>
/// <param name="NoStart">Whether the hook must not start the overlay when it is absent.</param>
internal sealed record LaunchOptions(LaunchMode Mode, bool NoStart)
{
    /// <summary>Parses the command line; unknown arguments are ignored and the last mode wins.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parsed options.</returns>
    public static LaunchOptions Parse(string[] args)
    {
        LaunchMode mode = LaunchMode.None;
        bool noStart = false;

        foreach (string arg in args)
        {
            if (arg == "--hook")
            {
                mode = LaunchMode.Hook;
            }
            else if (arg == "--overlay")
            {
                mode = LaunchMode.Overlay;
            }
            else if (arg == "--no-start")
            {
                noStart = true;
            }
        }

        return new LaunchOptions(mode, noStart);
    }
}
