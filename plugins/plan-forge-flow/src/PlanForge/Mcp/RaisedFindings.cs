using System.Text.Json.Nodes;

namespace PlanForge.Mcp;

/// <summary>
/// The IDs a fix call's decision batch raised, carried beside its result the way
/// <see cref="SpeedWarnings"/> carries a warning: the result is the builder's own answer schema, and
/// the orchestrator needs the IDs to send them to a later fix. See docs/adr/0025.
/// </summary>
internal static class RaisedFindings
{
    internal static string Attach(string json, IReadOnlyList<string> raised)
    {
        if (raised.Count == 0) return json;

        var result = JsonNode.Parse(json)!.AsObject();
        result["raisedFindingIds"] = new JsonArray([.. raised.Select(id => (JsonNode?)JsonValue.Create(id))]);
        return result.ToJsonString();
    }
}
