using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Run;

namespace PlanForge.Evidence;

/// <summary>
/// Says when a Run that has used the Evidence map holds a Scout answer no complete map has read. A map judges each item
/// once, so a new answer, not a changed plan, is what makes a map due. It only warns: the map is advice, and the
/// Orchestrator may have stopped it for this Run on purpose.
/// </summary>
internal static class EvidenceMapCurrency
{
    /// <summary>The warning for the Run's Scout answers, or null when the map is current or was never used.</summary>
    /// <param name="run">The Run whose Evidence map is read.</param>
    /// <param name="scoutAnswers">The Scout answers the Run has consumed so far.</param>
    public static string? Warning(RunDirectory run, int scoutAnswers)
    {
        if (!File.Exists(run.EvidenceMapPath)) return null;
        EvidenceStore store;
        // A damaged file is the map call's to report; a plan writer has nothing to add to that.
        try { store = EvidenceStore.Load(run.EvidenceMapPath); }
        catch (JsonException) { return null; }
        if (store.Mapped is not { } mapped)
            // Marks without a mapping come from a version that did not record what it read; nothing tells what is new.
            return store.LastComplete.Count > 0 ? null
                : "This Run has used the Evidence map, but no map call has completed. Call forge.evidence.map "
                  + "until complete is true before the next plan-review round, unless the map was stopped for this Run.";

        if (scoutAnswers <= mapped.ScoutAnswers) return null;
        var answers = scoutAnswers == mapped.ScoutAnswers + 1 ? $"Scout answer {scoutAnswers}"
            : $"Scout answers {mapped.ScoutAnswers + 1}-{scoutAnswers}";
        return $"{answers} arrived after the last complete Evidence map, which read {mapped.ScoutAnswers}. "
             + "Call forge.evidence.map before the next plan-review round; it asks only about the new answers' items. "
             + "Skip it only if the map was stopped for this Run.";
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
