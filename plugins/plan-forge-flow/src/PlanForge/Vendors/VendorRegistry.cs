namespace PlanForge.Vendors;

/// <summary>
/// Turns the vendor id a tool call carries into a vendor. The catalogue is advisory but the id is
/// not: an unknown one is refused here rather than guessed at.
/// </summary>
internal static class VendorRegistry
{
    public const string Default = "claude";

    public static IVendor Create(string? id, string workspaceRoot) =>
        (id is { Length: > 0 } ? id.Trim() : Default).ToLowerInvariant() switch
        {
            "claude" => new ClaudeCliVendor(workspaceRoot),
            "codex" => new CodexAppServerVendor(workspaceRoot),
            "cursor" => new CursorAgentVendor(workspaceRoot),
            _ => throw new VendorException($"unknown vendor '{id}' — expected claude, codex or cursor")
        };
}
