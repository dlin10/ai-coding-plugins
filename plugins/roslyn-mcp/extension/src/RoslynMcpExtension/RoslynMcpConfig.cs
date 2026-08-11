using System.IO;
using System.Text.RegularExpressions;

namespace RoslynMcpExtension;

/// <summary>
/// Resolves the MCP server HTTP port for the loaded solution. Walks up the directory tree
/// from the solution folder looking for a "<see cref="FileName"/>" file (nearest ancestor
/// wins) and reads its "port" value; falls back to <paramref name="fallback"/> when none is found.
/// </summary>
internal static class RoslynMcpConfig
{
    public const string FileName = ".roslynmcp.json";

    public static int ResolvePort(string? solutionDir, int fallback, out string? configPath)
    {
		configPath = null;
        if (string.IsNullOrEmpty(solutionDir))
            return fallback;

        for (DirectoryInfo? dir = new(solutionDir); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, FileName);
            if (File.Exists(path) && TryReadPort(path, out var port))
			{
				configPath = path;
                return port;
			}
        }

        return fallback;
    }

    // The file is a tiny extension-owned document, e.g. { "port": 5051 }. net48 has no built-in
    // JSON parser, so a targeted regex reads the single value without adding a dependency.
    private static bool TryReadPort(string path, out int port)
    {
        port = 0;
        try
        {
            var match = Regex.Match(File.ReadAllText(path), "\"port\"\\s*:\\s*(\\d{1,5})");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var value) && value is > 0 and <= 65535)
            {
                port = value;
                return true;
            }
        }
        catch
        {
            // Missing, locked, or malformed file -> use the fallback port.
        }

        return false;
    }
}
