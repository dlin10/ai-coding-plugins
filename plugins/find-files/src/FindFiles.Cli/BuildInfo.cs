using System.Reflection;

namespace FindFiles;

internal static class BuildInfo
{
    internal const string ServerName = "find-files";

    /// <summary>
    /// The single source of truth is <c>&lt;Version&gt;</c> in the csproj; CI compares this value
    /// against the version in each host manifest to catch a binary that was never republished.
    /// </summary>
    internal static string Version =>
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0]
        ?? "0.0.0";
}
