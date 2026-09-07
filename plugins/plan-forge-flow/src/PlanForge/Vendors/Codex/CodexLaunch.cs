using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// What "Inspect" found: the PATH codex should launch with, whether it was repaired,
/// and the shell codex would resolve against that PATH.
/// </summary>
internal sealed record ShellPath(string? Path, bool Repaired, string? Shell);

/// <summary>
/// Codex chooses its own shell and has no configuration key for it, so the only lever is the PATH
/// of the process forge starts. See docs/adr/0013-strip-the-store-alias-from-the-codex-path.md.
/// </summary>
internal static class CodexLaunch
{
    private const string COMMAND = "codex";

    internal static string Executable =>
        ExecutableResolver.Resolve(COMMAND) ?? throw new VendorException($"{COMMAND} was not found on PATH");

    internal static ShellPath Inspect(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new ShellPath(path, false, null);

        var windows = OperatingSystem.IsWindows();
        var pwsh = ExecutableResolver.Resolve("pwsh", path, windows);

        string workingPath;
        bool repaired;
        if (pwsh is not null && HasWindowsAppsSegment(pwsh))
        {
            workingPath = string.Join(Path.PathSeparator,
                path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(entry => !HasWindowsAppsSegment(entry)));
            repaired = true;
        }
        else
        {
            workingPath = path;
            repaired = false;
        }

        var pwshCandidate = ExecutableResolver.Resolve("pwsh", workingPath, windows);
        var powershellCandidate = ExecutableResolver.Resolve("powershell", workingPath, windows);
        var shell = IsUsableShell(pwshCandidate) ? pwshCandidate
            : IsUsableShell(powershellCandidate) ? powershellCandidate
            : null;

        return new ShellPath(workingPath, repaired, shell);
    }

    private static bool HasWindowsAppsSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "WindowsApps", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A Store execution alias resolves to a real path on disk, so existence alone does not tell it
    /// apart from a real shell — measured as a zero-byte stub. Only a candidate with actual bytes
    /// counts.
    /// </summary>
    private static bool IsUsableShell(string? candidate) =>
        candidate is not null && new FileInfo(candidate).Length > 0;
}
