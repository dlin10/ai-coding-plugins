using System;
using System.IO;

namespace RoslynMcpExtension.Shared;

/// <summary>
/// Git's linked worktrees. A file a developer keeps untracked, such as ".roslynmcp.json", lives in the repository's
/// main working tree only, and a worktree finds it at the same relative path there.
/// </summary>
public static class WorkTrees
{
    /// <summary>
    /// Maps a directory inside a linked Git worktree onto the same relative directory in the
    /// repository's main working tree. Returns false outside a linked worktree, which includes an
    /// ordinary clone, where ".git" is a directory rather than a pointer file.
    /// </summary>
    /// <param name="dir">The directory to map.</param>
    /// <param name="mapped">The same relative directory in the main working tree, which need not exist.</param>
    public static bool TryMapToMainWorkTree(string dir, out string mapped)
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
}
