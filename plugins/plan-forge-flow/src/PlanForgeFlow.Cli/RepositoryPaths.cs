namespace PlanForgeFlow;

internal static class RepositoryPaths
{
    public static string CanonicalWorkspaceRoot(string cwd)
    {
        var full = CanonicalPath(cwd);
        if (!Directory.Exists(full))
        {
            throw new CliFailure("environment", $"workspace does not exist: {full}");
        }

        try
        {
            var result = ProcessExecution.Run("git", ["-C", full, "rev-parse", "--show-toplevel"]);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                return CanonicalPath(result.Stdout.Trim());
            }
        }
        catch
        {
            // A non-Git workspace is still a valid target for doctor.
        }

        return full;
    }

    public static RepositoryIdentity Identify(string cwd)
    {
        var workspace = CanonicalWorkspaceRoot(cwd);
        var result = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--git-common-dir"]);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            throw new CliFailure("environment", "workspace is not a Git repository");
        }

        var common = result.Stdout.Trim();
        if (!Path.IsPathRooted(common)) common = Path.Combine(workspace, common);
        return new RepositoryIdentity(workspace, CanonicalPath(common));
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var info = new DirectoryInfo(full);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) break;
                var next = Path.GetFullPath(target.FullName);
                if (string.Equals(next, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
                full = next;
            }
            catch { break; }
        }

        var root = Path.GetPathRoot(full);
        return root is not null && string.Equals(full, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                   ? root
                   : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string CodexHome()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static string PluginData()
    {
        var overridePath = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        return string.IsNullOrWhiteSpace(overridePath)
                   ? Path.Combine(CodexHome(), "plugin-data", "plan-forge-flow")
                   : Path.GetFullPath(overridePath);
    }

    public static string AgentsDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("FORGE_AGENTS_DIR");
        return string.IsNullOrWhiteSpace(overridePath)
                   ? Path.Combine(CodexHome(), "agents")
                   : Path.GetFullPath(overridePath);
    }

    public static string SessionContextDirectory() => Path.Combine(PluginData(), "session-context");
    public static string JournalDirectory() => Path.Combine(PluginData(), "materialization-journal-v2");
    public static string TombstoneDirectory() => Path.Combine(PluginData(), "nonce-tombstones-v2");

    public static string SessionCapturePath(string workspaceRoot)
    {
        var key = OperatingSystem.IsWindows() ? workspaceRoot.ToLowerInvariant() : workspaceRoot;
        return Path.Combine(SessionContextDirectory(), Hashing.Sha256Hex(key) + ".json");
    }
}
