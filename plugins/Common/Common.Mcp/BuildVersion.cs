using System.Reflection;

namespace Common.Mcp;

public static class BuildVersion
{
    public static string Of(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0]
        ?? "0.0.0";
}
