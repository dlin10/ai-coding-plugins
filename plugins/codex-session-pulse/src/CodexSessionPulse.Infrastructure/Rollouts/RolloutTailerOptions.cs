namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>The bounds of the rollout reader (spec §5.2).</summary>
/// <param name="Root">The sessions folder, <c>~/.codex/sessions</c> by default.</param>
/// <param name="PollInterval">How often lengths are polled to cover lost watcher events; 1 to 3 seconds.</param>
/// <param name="MaxAgeDays">How many past day folders discovery looks into; later growth elsewhere is caught by the watcher.</param>
/// <param name="MaxFiles">How many rollout files are tracked at once; the least recently grown one is dropped first.</param>
/// <param name="HistoryBytes">How many bytes from the end of a newly found file are read to restore its last state.</param>
/// <param name="MaxLineBytes">The longest line that is buffered; longer lines are skipped without being held in memory.</param>
/// <param name="RediscoverInterval">How often the day folders are listed again.</param>
public sealed record RolloutTailerOptions(string Root,
                                          TimeSpan PollInterval,
                                          int MaxAgeDays,
                                          int MaxFiles,
                                          int HistoryBytes,
                                          int MaxLineBytes,
                                          TimeSpan RediscoverInterval)
{
    /// <summary>Creates the default bounds for a root: 2 s polling, 7 days, 16 files, 256 KiB history, 1 MiB lines, 10 s rediscovery.</summary>
    /// <param name="root">The sessions folder.</param>
    /// <returns>The options.</returns>
    public static RolloutTailerOptions ForRoot(string root)
    {
        return new RolloutTailerOptions(root, TimeSpan.FromSeconds(2), 7, 16, 256 * 1024, 1024 * 1024, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Gets the default sessions folder: <c>%CODEX_HOME%\sessions</c> when <c>CODEX_HOME</c> is set, otherwise
    /// <c>%USERPROFILE%\.codex\sessions</c>.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <returns>The folder path; it may not exist.</returns>
    public static string DefaultRoot(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        string? codexHome = getEnvironmentVariable("CODEX_HOME");
        string home = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;

        return Path.Combine(home, "sessions");
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Root);

        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromSeconds(3))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "Polling must be 1 to 3 seconds (spec §5.2).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(MaxAgeDays);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLineBytes, 1024);
    }
}
