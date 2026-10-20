using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Run;

namespace PlanForge.Evidence;

/// <summary>
/// Says when a Run that has used the Evidence map holds a plan that changed after a plan-review round
/// no complete map call has followed. Patching the plan in answer to the map before its round is the
/// protocol, so the round, not the change alone, is what makes a map due. It only warns: the map is
/// advice, and the Orchestrator may have stopped it for this Run on purpose.
/// </summary>
internal static class EvidenceMapCurrency
{
    /// <summary>The warning for the Run's current plan, or null when the map is current or was never used.</summary>
    /// <param name="run">The Run whose plan and Evidence map are read.</param>
    /// <param name="reviewRounds">The plan-review rounds the Run has run so far.</param>
    public static string? Warning(RunDirectory run, int reviewRounds)
    {
        if (!File.Exists(run.EvidenceMapPath)) return null;
        EvidencePlanMark? mapped;
        // A damaged file is the map call's to report; a plan writer has nothing to add to that.
        try { mapped = EvidenceStore.Load(run.EvidenceMapPath).LastCompletePlan; }
        catch (JsonException) { return null; }
        if (mapped is null)
            return "This Run has used the Evidence map, but no map call has completed. Call forge.evidence.map "
                 + "until complete is true before the next plan-review round, unless the map was stopped for this Run.";

        var current = PlanOutline.Hash(run.ReadPlan());
        if (mapped.ReviewRounds >= reviewRounds || mapped.PlanHash == current) return null;
        return $"The plan has changed since the last complete Evidence map: the map judged plan {mapped.PlanHash} "
             + $"before plan-review round {mapped.ReviewRounds + 1}, and the plan is now {current} after round {reviewRounds}. "
             + "Call forge.evidence.map again before the next plan-review round and take up only its new flags, "
             + "unless the map was stopped for this Run.";
    }

    /// <summary>Adds the warning beside a result whose own type is a tool or Worker contract.</summary>
    /// <param name="json">The serialized result.</param>
    /// <param name="warning">The warning, or null to return the result unchanged.</param>
    public static string Attach(string json, string? warning)
    {
        if (warning is null) return json;

        var result = JsonNode.Parse(json)!.AsObject();
        result["evidenceMapWarning"] = warning;
        return result.ToJsonString();
    }
}
