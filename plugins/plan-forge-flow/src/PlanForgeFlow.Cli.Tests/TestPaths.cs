namespace PlanForgeFlow.Tests;

internal static class TestPaths
{
    public static string Unique(string category)
    {
        var tempRoot = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && (tempRoot == "/var" || tempRoot.StartsWith("/var/", StringComparison.Ordinal))) tempRoot = "/private" + tempRoot;
        return Path.Combine(tempRoot, category, Guid.NewGuid().ToString("N"));
    }
}
