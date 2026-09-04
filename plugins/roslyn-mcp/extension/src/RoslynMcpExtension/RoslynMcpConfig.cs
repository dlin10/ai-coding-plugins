using System;
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

        if (TrySearchUpward(solutionDir!, out var port, out configPath))
            return port;

        // The file is developer-local and therefore untracked, so a freshly added Git worktree
        // never carries one. Retry from the same relative folder in the repository's main working
        // tree, which serves the worktree on the port the repository is already configured for.
        if (TryMapToMainWorkTree(solutionDir!, out var mainDir) && TrySearchUpward(mainDir, out port, out configPath))
            return port;

        return fallback;
    }

    private static bool TrySearchUpward(string startDir, out int port, out string? configPath)
    {
        for (DirectoryInfo? dir = new(startDir); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, FileName);
            if (File.Exists(path) && TryReadPort(path, out port))
            {
                configPath = path;
                return true;
            }
        }

        port = 0;
        configPath = null;
        return false;
    }

    /// <summary>
    /// Maps a directory inside a linked Git worktree onto the same relative directory in the
    /// repository's main working tree. Returns false outside a linked worktree, which includes an
    /// ordinary clone, where ".git" is a directory rather than a pointer file.
    /// </summary>
    private static bool TryMapToMainWorkTree(string dir, out string mapped)
    {
        mapped = string.Empty;
        try
        {
            var start = new DirectoryInfo(dir).FullName;
            for (DirectoryInfo? current = new(start); current != null; current = current.Parent)
            {
                var pointer = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(pointer))
                    return false;

                if (!File.Exists(pointer))
                    continue;

                var mainRoot = ResolveMainWorkTreeRoot(current.FullName, pointer);
                if (mainRoot == null)
                    return false;

                var relative = start.Substring(current.FullName.Length)
                                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                mapped = relative.Length == 0 ? mainRoot : Path.Combine(mainRoot, relative);
                return true;
            }
        }
        catch
        {
            // Missing, locked, or malformed Git metadata -> no worktree to map onto.
        }

        return false;
    }

    // A linked worktree's ".git" file points at <common>/worktrees/<name>, whose "commondir" file
    // points back at the shared ".git" directory; the main working tree is that directory's parent.
    private static string? ResolveMainWorkTreeRoot(string workTreeRoot, string pointerFile)
    {
        const string prefix = "gitdir:";
        var pointer = File.ReadAllText(pointerFile).Trim();
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var gitDir = Path.GetFullPath(Path.Combine(workTreeRoot, pointer.Substring(prefix.Length).Trim()));
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return null;

        var commonDir = new DirectoryInfo(Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim())));

        // A bare repository has no main working tree, and its common directory is not named ".git".
        return commonDir.Name == ".git" ? commonDir.Parent?.FullName : null;
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
