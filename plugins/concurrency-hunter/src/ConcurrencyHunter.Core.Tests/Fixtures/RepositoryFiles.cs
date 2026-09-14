namespace ConcurrencyHunter.Core.Tests.Fixtures;

internal static class RepositoryFiles
{
    internal static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(segments)} above the test binary.");
    }
}
