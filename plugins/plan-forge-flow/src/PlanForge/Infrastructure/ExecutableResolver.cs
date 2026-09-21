namespace PlanForge.Infrastructure;

/// <summary>
/// Finds a vendor CLI on PATH. Generalised from the Codex-only resolver because every vendor here
/// ships through a Windows shim, and UseShellExecute=false does not apply PATHEXT. A shim that
/// names its native executable can be unwrapped here; a Node entry point also needs its script
/// argument, so its vendor resolves the raw shim and owns that launch contract.
/// </summary>
internal static class ExecutableResolver
{
    private static readonly string[] WINDOWS_EXTENSIONS = [".exe", ".cmd", ".bat"];

    public static string? Resolve(string command) =>
        Resolve(command, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>
    /// PATH first, then the given fallback directories. cursor-agent installs outside PATH, and a
    /// process started from a long-lived host inherits a stale PATH anyway.
    /// </summary>
    public static string? Resolve(string command, IEnumerable<string> fallbackDirectories)
    {
        if (Resolve(command) is { } onPath) 
            return onPath;

        var extras = string.Join(Path.PathSeparator, fallbackDirectories);
        return Resolve(command, extras, OperatingSystem.IsWindows());
    }

    internal static string? Resolve(string command, string? pathValue, bool windows)
        => Resolve(command, pathValue, windows, unwrapShims: true);

    internal static string? ResolveRaw(string command, string? pathValue, bool windows)
        => Resolve(command, pathValue, windows, unwrapShims: false);

    private static string? Resolve(string command, string? pathValue, bool windows, bool unwrapShims)
    {
        foreach (var raw in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length == 0) 
                continue;

            if (!windows)
            {
                var native = Path.Combine(directory, command);
                if (File.Exists(native)) 
                    return Path.GetFullPath(native);

                continue;
            }

            foreach (var extension in WINDOWS_EXTENSIONS)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (!File.Exists(candidate)) 
                    continue;

                var full = Path.GetFullPath(candidate);
                return extension is ".exe" || !unwrapShims ? full : UnwrapShim(full) ?? full;
            }
        }

        return null;
    }

    /// <summary>
    /// Follows a .cmd shim to the executable it launches. Running the shim itself would go through
    /// cmd.exe, which re-parses the command line and can corrupt structured or prompt arguments.
    /// </summary>
    private static string? UnwrapShim(string shimPath)
    {
        var shimDirectory = Path.GetDirectoryName(Path.GetFullPath(shimPath));
        if (shimDirectory is null) 
            return null;

        foreach (var line in File.ReadLines(shimPath))
        {
            foreach (var quoted in QuotedTokens(line))
            {
                if (!quoted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) 
                    continue;

                var expanded = quoted.Replace("%~dp0", shimDirectory, StringComparison.OrdinalIgnoreCase)
                                     .Replace("%dp0%", shimDirectory, StringComparison.OrdinalIgnoreCase);

                if (!Path.IsPathFullyQualified(expanded)) 
                    continue;

                var full = Path.GetFullPath(expanded);
                if (File.Exists(full)) 
                    return full;
            }
        }

        return null;
    }

    private static IEnumerable<string> QuotedTokens(string line)
    {
        var index = 0;
        while (true)
        {
            var open = line.IndexOf('"', index);
            if (open < 0) 
                yield break;

            var close = line.IndexOf('"', open + 1);
            if (close < 0) 
                yield break;

            yield return line[(open + 1)..close];
            index = close + 1;
        }
    }
}
