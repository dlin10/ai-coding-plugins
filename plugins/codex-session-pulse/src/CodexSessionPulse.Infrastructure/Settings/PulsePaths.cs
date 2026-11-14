namespace CodexSessionPulse.Infrastructure.Settings;

/// <summary>
/// The overlay's own files. They live under the profile root, not <c>%LOCALAPPDATA%</c>: a process started from inside the
/// Codex MSIX package has its <c>%LOCALAPPDATA%</c> writes redirected into the package (docs/compatibility.md §3.2), so the
/// hook and the overlay would see different files there.
/// </summary>
/// <param name="Root">The root folder, <c>%USERPROFILE%\.codex-session-pulse</c> by default.</param>
public sealed record PulsePaths(string Root)
{
    /// <summary>The variable that moves the root, for tests and development runs.</summary>
    public const string HomeVariable = "CODEX_SESSION_PULSE_HOME";

    /// <summary>Gets the settings file.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Gets the log folder.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Resolves the root from <see cref="HomeVariable"/> or the user profile. A relative value is ignored: it would resolve
    /// against the current directory, which for a hook is the user's project.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <returns>The paths.</returns>
    public static PulsePaths FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        string? home = getEnvironmentVariable(HomeVariable);

        return new PulsePaths(home is not null && Path.IsPathFullyQualified(home)
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-session-pulse"));
    }
}
