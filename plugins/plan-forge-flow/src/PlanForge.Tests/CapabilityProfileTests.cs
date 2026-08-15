using ModelContextProtocol.Protocol;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

public sealed class CapabilityProfileTests
{
    [Fact]
    public void A_host_without_a_ui_capability_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(new ClientCapabilities()));

    [Fact]
    public void A_client_that_negotiated_nothing_at_all_gets_the_text_profile() =>
        Assert.Equal(CapabilityProfile.Text, CapabilityProfileDetector.Detect(null));
}
