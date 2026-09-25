using System.Text.Json.Nodes;
using PlanForge.Run;

namespace PlanForge.Mcp;

/// <summary>
/// A Fast turn the vendor served at standard speed for part of it still counts, so the warning rides
/// on the act's own result instead of failing it, and goes to the flow log for the user. The result
/// is a worker contract as often as not, so the warning is added beside it rather than to its type,
/// which is also the schema a worker answers in. See docs/adr/0023.
/// </summary>
internal static class SpeedWarnings
{
    internal static string Attach(RunDirectory run, string json, string? warning)
    {
        if (warning is null) return json;

        run.AppendFlowSpeedWarning(warning);
        var result = JsonNode.Parse(json)!.AsObject();
        result["speedWarning"] = warning;
        return result.ToJsonString();
    }
}
