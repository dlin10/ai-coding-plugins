using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using PlanForge.Vendors.Cursor;

namespace PlanForge.Vendors;

/// <summary>
/// Turns the vendor id a tool call carries into a vendor. The catalogue is advisory but the id is
/// not: an unknown one is refused here rather than guessed at.
/// </summary>
/// <remarks>
/// Deliberately a factory rather than a container registration. A vendor is built around the
/// workspace root, which arrives as a tool argument, so there is nothing to resolve at startup —
/// the container would hold the same three-way mapping and then need a runtime key anyway, trading
/// this message for a resolution failure. The tool surface builds its other collaborators the same
/// way.
/// </remarks>
internal static class VendorFactory
{
    private const string Default = "claude";

    public static IVendor Create(string? id, string workspaceRoot) =>
        (id is { Length: > 0 } ? id.Trim() : Default).ToLowerInvariant() switch
        {
            "claude" => new ClaudeCliVendor(workspaceRoot),
            "codex" => new CodexAppServerVendor(workspaceRoot),
            "cursor" => new CursorAgentVendor(workspaceRoot),
            _ => throw new VendorException($"unknown vendor '{id}' — expected claude, codex or cursor")
        };
}
