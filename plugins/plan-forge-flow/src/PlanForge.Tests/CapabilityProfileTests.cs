using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

public sealed class CapabilityProfileTests
{
    /// <summary>
    /// Verbatim from a Cursor 3.17.8 handshake. The capability is built by deserializing what the
    /// client actually sends rather than by constructing the SDK's own types, so the test keeps
    /// asserting about the wire when the SDK's shape for extensions changes.
    /// </summary>
    private const string CursorCapabilities =
        """{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}}""";


    [Fact]
    public void A_host_without_a_ui_capability_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(new ClientCapabilities()));

    [Fact]
    public void A_client_that_negotiated_nothing_at_all_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(null));

    /// <summary>
    /// The measurement in docs/adr/0002 found no host that negotiated this, and the enum said so.
    /// Cursor does now — see docs/adr/0008 — so the branch has a caller and needs a guard.
    /// </summary>
    [Fact]
    public void A_host_that_advertises_the_mcp_apps_extension_gets_the_canvas_profile()
    {
        var capabilities = JsonSerializer.Deserialize<ClientCapabilities>(
            CursorCapabilities, McpJsonUtilities.DefaultOptions)!;

        Assert.Equal(CapabilityProfile.Canvas, CapabilityProfileDetector.Detect(capabilities));
    }
}
