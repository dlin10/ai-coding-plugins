namespace FindFiles.Everything;

/// <summary>
/// Resolves the path to voidtools' <c>es.exe</c>. Everything ships it next to
/// <c>Everything.exe</c> and normally puts that directory on PATH, but neither is guaranteed.
/// </summary>
internal static class EsLocator
{
    private const string ExecutableName = "es.exe";

    /// <summary>
    /// Returns the first usable <c>es.exe</c>, or <see langword="null"/> when Everything's CLI
    /// is not installed. Probe order: the ES_PATH override, then PATH, then the default
    /// install directories.
    /// </summary>
    internal static string? Locate(Func<string, string?> environment, Func<string, bool> fileExists)
    {
        var overridePath = environment("ES_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && fileExists(overridePath))
        {
            return overridePath;
        }

        foreach (var candidate in PathCandidates(environment))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string? Locate() =>
        Locate(Environment.GetEnvironmentVariable, File.Exists);

    private static IEnumerable<string> PathCandidates(Func<string, string?> environment)
    {
        var pathVariable = environment("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A malformed PATH entry must not abort the probe.
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, ExecutableName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return candidate;
            }
        }

        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var programFiles = environment(variable);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Everything", ExecutableName);
            }
        }
    }
}
