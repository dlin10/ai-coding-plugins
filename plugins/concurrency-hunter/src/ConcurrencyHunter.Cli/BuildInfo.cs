using Common.Mcp;

namespace ConcurrencyHunter;

internal static class BuildInfo
{
    internal const string ServerName = "concurrency-hunter";

    internal static string Version => BuildVersion.Of(typeof(BuildInfo).Assembly);
}
