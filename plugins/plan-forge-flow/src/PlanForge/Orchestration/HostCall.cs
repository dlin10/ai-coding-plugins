namespace PlanForge.Orchestration;

/// <summary>What a host allows one call of this server's tools.</summary>
internal static class HostCall
{
    /// <summary>
    /// The longest a call works before it answers. Claude Code's manifest <c>"timeout"</c> and Codex's
    /// <c>tool_timeout_sec</c> both end a call at the hour; ten minutes short of it leaves the result
    /// room to land before that clock does.
    /// </summary>
    public const int MAX_SECONDS = 3000;
}
