using System.Reflection;

namespace CacheDetective;

internal static class BuildInfo
{
    internal const string ServerName = "cache-detective";

    internal static string Version =>
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0]
        ?? "0.0.0";
}
