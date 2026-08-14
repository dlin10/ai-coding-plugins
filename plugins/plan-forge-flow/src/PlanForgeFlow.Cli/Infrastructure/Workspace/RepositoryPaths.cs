using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Infrastructure.Workspace;

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
            var result = new GitClient(full).Run(["rev-parse", "--show-toplevel"]);
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
        var result = new GitClient(workspace).Run(["rev-parse", "--git-common-dir"]);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            throw new CliFailure("environment", "workspace is not a Git repository");
        }

        var common = result.Stdout.Trim();
        if (!Path.IsPathRooted(common)) common = Path.Combine(workspace, common);
        return new RepositoryIdentity(workspace, CanonicalPath(common));
    }

    public static string ScopeId(RepositoryIdentity repository)
    {
        var common = CanonicalPath(repository.GitCommonDir);
        var workspace = CanonicalPath(repository.WorkspaceRoot);
        var value = OperatingSystem.IsWindows() ? $"{common.ToLowerInvariant()}\n{workspace.ToLowerInvariant()}" : $"{common}\n{workspace}";
        return Hashing.Sha256Hex(value)[..24];
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
                if (string.Equals(next, full, WorkspacePathPolicy.Comparison)) break;
                full = next;
            }
            catch { break; }
        }

        var root = Path.GetPathRoot(full);
        return root is not null && string.Equals(full, root, WorkspacePathPolicy.Comparison)
                   ? root
                   : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CodexHome()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    internal static string PluginData(HostKind host = HostKind.Codex)
    {
        var overridePath = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        if (host == HostKind.Claude)
        {
            var claudeData = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
            return string.IsNullOrWhiteSpace(claudeData)
                       ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugin-data", "plan-forge-flow")
                       : Path.GetFullPath(claudeData);
        }
        if (host == HostKind.Cursor)
        {
            var cursorHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
            var home = string.IsNullOrWhiteSpace(cursorHome)
                           ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor")
                           : Path.GetFullPath(cursorHome);
            return Path.Combine(home, "plugin-data", "plan-forge-flow");
        }
        return Path.Combine(CodexHome(), "plugin-data", "plan-forge-flow");
    }

    internal static string AgentsDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("FORGE_AGENTS_DIR");
        return string.IsNullOrWhiteSpace(overridePath)
                   ? Path.Combine(CodexHome(), "agents")
                   : Path.GetFullPath(overridePath);
    }

    private static string PendingPlanDirectory() => Path.Combine(PluginData(), "pending-plans");

    internal static string AppServerSessionDirectory(string workspaceRoot, HostKind host = HostKind.Codex)
    {
        var key = OperatingSystem.IsWindows() ? workspaceRoot.ToLowerInvariant() : workspaceRoot;
        return Path.Combine(PluginData(host), "app-server-sessions", Hashing.Sha256Hex(key));
    }

    public static string PendingPlanPath(string workspaceRoot)
    {
        var key = OperatingSystem.IsWindows() ? workspaceRoot.ToLowerInvariant() : workspaceRoot;
        return Path.Combine(PendingPlanDirectory(), Hashing.Sha256Hex(key) + ".json");
    }
}
