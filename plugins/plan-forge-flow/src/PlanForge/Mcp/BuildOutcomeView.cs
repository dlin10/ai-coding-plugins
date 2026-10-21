using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;

namespace PlanForge.Mcp;

/// <summary>
/// A build outcome as the Orchestrator receives it from <c>forge.build.next</c>, directly or through
/// a Job.
/// </summary>
/// <remarks>
/// A task whose builder said <c>done</c>, whose own checks <c>passed</c> and whose gate
/// <c>passed</c> is settled: the build reference acts on the status, the two outcomes and the
/// counters, and nothing reads the builder's account, its evidence, its file list or the gate's
/// command and output — the Flow log keeps the account and the evidence verbatim for the user, and
/// leaves the output out on a pass itself. So a clean pass goes out without them, and any other
/// result goes out whole: a failed or unrun gate, a blocked turn, and a pass the builder could not
/// check itself are the moments its words are read. Only the view changes; the result the ledger
/// keeps and a retry replays is the whole one. See docs/adr/0042.
/// </remarks>
internal static class BuildOutcomeView
{
    /// <summary>The outcome as a JSON object, trimmed when its task passed cleanly.</summary>
    /// <param name="outcome">The build act's outcome.</param>
    internal static JsonObject Of(BuildOutcome outcome)
    {
        var node = JsonSerializer.SerializeToNode(outcome, ForgeToolJson.Default.BuildOutcome)!.AsObject();
        if (outcome.Result is not { Status: "done", Verification.Outcome: "passed", Gate.Outcome: "passed" }
            || node["result"] is not JsonObject result)
            return node;

        result.Remove("summary");
        result.Remove("filesChanged");
        result["verification"]!.AsObject().Remove("evidence");
        var gate = result["gate"]!.AsObject();
        gate.Remove("command");
        gate.Remove("output");
        return node;
    }
}
