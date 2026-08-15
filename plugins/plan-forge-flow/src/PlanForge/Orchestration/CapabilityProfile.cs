using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace PlanForge.Orchestration;

/// <summary>
/// What the connected host can actually do. Only <see cref="Text"/> is implemented; no host
/// negotiates the UI capability yet, so <see cref="Canvas"/> exists to be detected, not served.
/// See docs/adr/0002.
/// </summary>
internal enum CapabilityProfile
{
    Text,
    Canvas
}

internal static class CapabilityProfileDetector
{
    public static CapabilityProfile Detect(ClientCapabilities? capabilities) =>
        capabilities is not null && McpApps.GetUiCapability(capabilities) is not null
            ? CapabilityProfile.Canvas
            : CapabilityProfile.Text;
}
