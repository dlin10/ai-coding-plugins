using Common.Mcp;

namespace CacheDetective;

internal static class BuildInfo
{
    internal const string ServerName = "cache-detective";

    internal static string Version => BuildVersion.Of(typeof(BuildInfo).Assembly);
}
