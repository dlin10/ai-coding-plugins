using ModelContextProtocol.Protocol;

namespace PlanForge.Orchestration;

/// <summary>
/// Host behaviour, as opposed to <c>IVendor</c>, which is worker behaviour. It answers only:
/// which capability profile is in play, how the plan is presented, and how approval is asked for.
/// It never enforces anything — no session leases, no transcript reading. See docs/adr/0002.
/// </summary>
internal interface IOrchestrator
{
    CapabilityProfile Profile { get; }

    /// <summary>How the plan reaches the user under this profile.</summary>
    PlanPresentation Present(string plan, int taskCount, IReadOnlyList<string> driftedFiles);

    /// <summary>Whether approval can be asked for in-band, or has to be a separate tool call.</summary>
    bool CanElicitApproval { get; }
}

internal enum PlanPresentationKind
{
    /// <summary>Markdown in the tool result — the only mode any host supports today.</summary>
    Text,

    /// <summary>A ui:// resource rendered by the host. Nothing negotiates this yet.</summary>
    Canvas
}

internal sealed record PlanPresentation(PlanPresentationKind Kind, string Body);

/// <summary>
/// One implementation, driven by what the host actually negotiated rather than by which host it is.
///
/// The design allowed for a per-host implementation each. Measurement did not justify three: Claude
/// Code 2.1.233 and Cursor 1.0.0 negotiate the same protocol version, the same form-elicitation
/// capability, and no UI capability, and they differ only in `roots`, which nothing here uses.
/// Splitting this the moment a host genuinely diverges is a smaller change than maintaining three
/// classes that differ in nothing.
/// </summary>
internal sealed class NegotiatedOrchestrator : IOrchestrator
{
    public NegotiatedOrchestrator(ClientCapabilities? capabilities)
    {
        Profile = CapabilityProfileDetector.Detect(capabilities);
        CanElicitApproval = capabilities?.Elicitation is not null;
    }

    public CapabilityProfile Profile { get; }

    public bool CanElicitApproval { get; }

    public PlanPresentation Present(string plan, int taskCount, IReadOnlyList<string> driftedFiles)
    {
        if (Profile is CapabilityProfile.Canvas)
            return new PlanPresentation(PlanPresentationKind.Canvas, plan);

        var body = new System.Text.StringBuilder()
            .AppendLine(plan)
            .AppendLine()
            .Append("This plan has ").Append(taskCount).AppendLine(" tasks.");

        if (driftedFiles.Count > 0)
        {
            body.AppendLine()
                .AppendLine("The working tree changed after this run began. Files touched since then:");
            foreach (var file in driftedFiles) body.Append("- ").AppendLine(file);
        }

        return new PlanPresentation(PlanPresentationKind.Text, body.ToString());
    }
}
