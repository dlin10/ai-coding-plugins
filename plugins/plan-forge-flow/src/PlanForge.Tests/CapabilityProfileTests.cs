using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

public sealed class CapabilityProfileTests
{
    /// <summary>
    /// The MCP Apps extension verbatim as a client's handshake carried it in the docs/adr/0008
    /// measurement. The capability is built by deserializing what the client actually sends rather
    /// than by constructing the SDK's own types, so the test keeps asserting about the wire when the
    /// SDK's shape for extensions changes.
    /// </summary>
    private const string McpAppsCapabilities =
        """{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}}""";


    [Fact]
    public void A_host_without_a_ui_capability_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(new ClientCapabilities()));

    [Fact]
    public void A_client_that_negotiated_nothing_at_all_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(null));

    /// <summary>
    /// The measurement in docs/adr/0002 found no host that negotiated this, and the enum said so.
    /// A host that does is given the plan canvas — see docs/adr/0008 — so the branch needs a guard.
    /// </summary>
    [Fact]
    public void A_host_that_advertises_the_mcp_apps_extension_gets_the_canvas_profile()
    {
        var capabilities = JsonSerializer.Deserialize<ClientCapabilities>(
            McpAppsCapabilities, McpJsonUtilities.DefaultOptions)!;

        Assert.Equal(CapabilityProfile.Canvas, CapabilityProfileDetector.Detect(capabilities));
    }
}
