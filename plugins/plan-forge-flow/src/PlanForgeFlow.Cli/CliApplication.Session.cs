namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static AgentState Session(CommandContext context, ForgeRole role)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var id = parsed.GetRequired("id");
        var state = context.RequireState();
        var dispatch = state.Dispatch;
        if (!dispatch.Pending || string.IsNullOrWhiteSpace(parsed.Get("dispatch-id")) || parsed.Get("dispatch-id") != dispatch.Id) throw new CliFailure("state", "session dispatch-id does not match the pending dispatch", 3);
        var stage = dispatch.Stage;
        if (stage is null || stage.Value.Definition().Role != role)
        {
            throw new CliFailure("state", $"{role.ToString().ToLowerInvariant()} session does not match pending {stage?.ToWireName()} dispatch", 3);
        }
        var expectedModel = dispatch.Model;
        var expectedEffort = dispatch.Effort?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expectedModel) || string.IsNullOrWhiteSpace(expectedEffort) || expectedEffort == "ultra") throw new CliFailure("state", "pending dispatch has no valid pinned model selection", 3);
        ValidateObservedSelection(parsed, expectedModel, expectedEffort, requireObservation: true);
        state = StateStore.Update(workspace, state, value =>
        {
            if (role == ForgeRole.Builder)
            {
                var previous = value.Agents.BuilderId;
                if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, id, StringComparison.Ordinal)) RequireAuthorizationNote(parsed);
                value.Agents.BuilderId = id;
                value.Agents.LastBuilderDispatchId = dispatch.Id;
            }
            else
            {
                if (value.Agents.ReviewerIds.Contains(id, StringComparer.Ordinal)) throw new CliFailure("state", "reviewer id has already been used", 3);
                value.Agents.ReviewerIds.Add(id);
                value.Agents.LastReviewerId = id;
                value.Agents.LastReviewerDispatchId = dispatch.Id;
            }
        });
        return state.Agents;
    }
}
