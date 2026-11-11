using ConcurrencyHunter.Providers.LibraryModels.Sources;

namespace ConcurrencyHunter;

internal static class SourceProvider
{
    internal static string ApplicationData
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return Path.Combine(string.IsNullOrWhiteSpace(local)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : local, "concurrency-hunter");
        }
    }

    internal static LibrarySources Create(bool offline) => new(offline, Path.Combine(ApplicationData, "sources"));
}
