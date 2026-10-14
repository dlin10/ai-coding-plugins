namespace PlanForge.Orchestration;

/// <summary>The one decision of where Claude Code keeps its profile: <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.</summary>
internal static class ClaudeConfigDirectory
{
    private const string VARIABLE = "CLAUDE_CONFIG_DIR";

    /// <summary>Claude Code's config dir, as the server process's environment names it.</summary>
    /// <param name="environment">Reads the server process's environment variables.</param>
    public static string Resolve(Func<string, string?> environment) =>
        environment(VARIABLE) is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
