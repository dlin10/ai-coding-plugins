using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static JsonObject Session(string workspace, ParsedArgs parsed, string role)
    {
        var id = parsed.GetRequired("id");
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>() || string.IsNullOrWhiteSpace(parsed.Get("dispatch-id")) || parsed.Get("dispatch-id") != dispatch["id"]?.GetValue<string>()) throw new CliFailure("state", "session dispatch-id does not match the pending dispatch", 3);
        var stage = dispatch["stage"]?.GetValue<string>();
        if ((role == "builder" && stage is not ("build" or "fix-build")) ||
            (role == "reviewer" && stage is not ("plan" or "code" or "fix-review")))
        {
            throw new CliFailure("state", $"{role} session does not match pending {stage} dispatch", 3);
        }
        var expectedModel = dispatch["model"]?.GetValue<string>();
        var expectedEffort = dispatch["effort"]?.GetValue<string>().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expectedModel) || string.IsNullOrWhiteSpace(expectedEffort) || expectedEffort == "ultra") throw new CliFailure("state", "pending dispatch has no valid pinned model selection", 3);
        ValidateObservedSelection(parsed, expectedModel, expectedEffort, requireObservation: true);
        state = StateStore.Update(workspace, state, value =>
        {
            if (role == "builder")
            {
                var previous = value["agents"]!["builderId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, id, StringComparison.Ordinal)) RequireAuthorizationNote(parsed);
                value["agents"]!["builderId"] = id;
                value["agents"]!["lastBuilderDispatchId"] = dispatch["id"]?.DeepClone();
            }
            else
            {
                var ids = value["agents"]!["reviewerIds"]!.AsArray();
                if (ids.Any(item => item?.GetValue<string>() == id)) throw new CliFailure("state", "reviewer id has already been used", 3);
                ids.Add((JsonNode)JsonValue.Create(id));
                value["agents"]!["lastReviewerId"] = id;
                value["agents"]!["lastReviewerDispatchId"] = dispatch["id"]?.DeepClone();
            }
        });
        return state["agents"]!.AsObject();
    }
}
