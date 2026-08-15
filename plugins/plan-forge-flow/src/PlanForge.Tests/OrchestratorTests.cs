using ModelContextProtocol.Protocol;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

public sealed class OrchestratorTests
{
    [Fact]
    public void A_host_without_a_ui_capability_gets_the_text_profile()
    {
        var orchestrator = new NegotiatedOrchestrator(new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() }
        });

        Assert.Equal(CapabilityProfile.Text, orchestrator.Profile);
        Assert.True(orchestrator.CanElicitApproval);
    }

    [Fact]
    public void A_host_without_elicitation_cannot_be_asked_for_approval()
    {
        var orchestrator = new NegotiatedOrchestrator(new ClientCapabilities());

        Assert.Equal(CapabilityProfile.Text, orchestrator.Profile);
        Assert.False(orchestrator.CanElicitApproval);
    }

    [Fact]
    public void The_text_presentation_carries_the_plan_the_task_count_and_the_drift()
    {
        var orchestrator = new NegotiatedOrchestrator(new ClientCapabilities());

        var presentation = orchestrator.Present("# Plan\n\nBody.", taskCount: 3, ["src/a.cs", "src/b.cs"]);

        Assert.Equal(PlanPresentationKind.Text, presentation.Kind);
        Assert.Contains("# Plan", presentation.Body, StringComparison.Ordinal);
        Assert.Contains("3 tasks", presentation.Body, StringComparison.Ordinal);
        Assert.Contains("src/a.cs", presentation.Body, StringComparison.Ordinal);
        Assert.Contains("src/b.cs", presentation.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_tree_adds_no_drift_notice()
    {
        var orchestrator = new NegotiatedOrchestrator(new ClientCapabilities());

        var presentation = orchestrator.Present("# Plan", taskCount: 1, []);

        Assert.DoesNotContain("working tree changed", presentation.Body, StringComparison.Ordinal);
    }
}
