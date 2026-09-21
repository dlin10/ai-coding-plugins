using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// What "Inspect" found: the PATH codex should launch with, whether it was repaired,
/// and the shell codex would resolve against that PATH.
/// </summary>
internal sealed record ShellPath(string? Path, bool Repaired, string? Shell);

/// <summary>The executable and fixed prefix that start the Codex CLI without a command shell.</summary>
internal sealed record CodexCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    internal ProcessSpec CreateProcess(IReadOnlyList<string> arguments,
                                       string? workingDirectory,
                                       string standardInput,
                                       IReadOnlyDictionary<string, string>? environment = null) =>
        new(FileName, [.. PrefixArguments, .. arguments], workingDirectory, standardInput, environment);
}

/// <summary>
/// Owns the two Windows launch details Codex cannot safely leave to a command shell: an npm shim is
/// replaced by node plus its script, and the CLI's own shell is selected through its process PATH.
/// See docs/adr/0013-strip-the-store-alias-from-the-codex-path.md for the latter.
/// </summary>
internal static class CodexLaunch
{
    private const string COMMAND = "codex";

    internal static CodexCommand Command =>
        Resolve(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    internal static CodexCommand Resolve(string? path, bool windows)
    {
        var entryPoint = ExecutableResolver.ResolveRaw(COMMAND, path, windows)
            ?? throw new VendorException($"{COMMAND} was not found on PATH");

        if (!windows || !IsBatch(entryPoint))
            return new CodexCommand(entryPoint, []);

        // npm's .cmd forwards with `%*`, so cmd.exe re-parses prompt-bearing `-c` values. The
        // package's bin entry point is stable and lets ArgumentList reach Node without that shell.
        var directory = Path.GetDirectoryName(entryPoint)!;
        var script = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(script))
            throw new VendorException($"{entryPoint} was found, but its Codex script was not found at {script}");

        var localNode = Path.Combine(directory, "node.exe");
        var node = File.Exists(localNode) ? Path.GetFullPath(localNode)
            : ExecutableResolver.Resolve("node", path, windows);
        if (node is null || !Path.GetExtension(node).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new VendorException($"{entryPoint} requires node.exe, but it was not found on PATH");

        return new CodexCommand(node, [Path.GetFullPath(script)]);
    }

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

    private static bool IsBatch(string path) =>
        Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A Store execution alias resolves to a real path on disk, so existence alone does not tell it
    /// apart from a real shell — measured as a zero-byte stub. Only a candidate with actual bytes
    /// counts.
    /// </summary>
    private static bool IsUsableShell(string? candidate) =>
        candidate is not null && new FileInfo(candidate).Length > 0;
}
