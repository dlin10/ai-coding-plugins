using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class StateStore
{
    public static string StatePath(string workspace) => Path.Combine(workspace, ".forge", "state.json");

    public static JsonObject CreateEmpty(string? planHash = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["version"] = 3,
            ["generation"] = "v2",
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["workflow"] = new JsonObject { ["phase"] = "materialized", ["round"] = 0, ["maxRounds"] = 5, ["fixRound"] = 0, ["maxFixRounds"] = 3, ["maxBuildRetries"] = 3, ["maxVerdictRetries"] = 2, ["taskCount"] = null, ["nextTaskNumber"] = 1, ["amendment"] = false, ["tasks"] = null },
            ["models"] = new JsonObject { ["reviewer"] = null, ["builder"] = null },
            ["agents"] = new JsonObject { ["builderId"] = null, ["lastBuilderDispatchId"] = null, ["reviewerIds"] = new JsonArray(), ["lastReviewerId"] = null, ["lastReviewerDispatchId"] = null },
            ["dispatch"] = new JsonObject { ["id"] = null, ["stage"] = null, ["taskNumber"] = null, ["retry"] = 0, ["pending"] = false, ["attempt"] = 0, ["lastVerificationPassed"] = null, ["model"] = null, ["effort"] = null, ["resolution"] = null },
            ["baselines"] = new JsonObject { ["head"] = null, ["worktree"] = null, ["untracked"] = new JsonArray() },
            ["review"] = new JsonObject { ["coverage"] = null, ["verdict"] = null, ["fixRound"] = 0, ["authorizedPaths"] = new JsonArray(), ["manifest"] = null, ["critiqueFile"] = null, ["critiqueFiles"] = new JsonArray() },
            ["approval"] = new JsonObject { ["planHash"] = planHash, ["nonce"] = null, ["revision"] = 0 },
            ["materialization"] = new JsonObject { ["transactionId"] = null, ["generation"] = "v2", ["committed"] = false },
        };
    }

    public static JsonObject Load(string workspace)
    {
        var path = StatePath(workspace);
        if (!File.Exists(path)) throw new CliFailure("state", "no Forge state exists", 3);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state must not be a symlink", 3);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null && parent.Exists; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state path contains a symlinked directory", 3);
            }
            var value = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (value["version"]?.GetValue<int>() != 3 || value["generation"]?.GetValue<string>() != "v2") throw new CliFailure("state", "unsupported Forge state version; expected nested state v3 generation v2", 3);
            foreach (var group in new[] { "workflow", "models", "agents", "dispatch", "baselines", "review", "approval", "materialization" })
            {
                if (value[group] is not JsonObject) throw new CliFailure("state", $"state group {group} is missing", 3);
            }
            RequireKeys(value, ["version", "generation", "createdAt", "updatedAt", "workflow", "models", "agents", "dispatch", "baselines", "review", "approval", "materialization"], "state");
            RequireKeys(value["workflow"]!.AsObject(), ["phase", "round", "maxRounds", "fixRound", "maxFixRounds", "maxBuildRetries", "maxVerdictRetries", "taskCount", "nextTaskNumber", "amendment", "tasks"], "state.workflow");
            RequireKeys(value["models"]!.AsObject(), ["reviewer", "builder"], "state.models");
            RequireKeys(value["agents"]!.AsObject(), ["builderId", "lastBuilderDispatchId", "reviewerIds", "lastReviewerId", "lastReviewerDispatchId"], "state.agents");
            RequireKeys(value["dispatch"]!.AsObject(), ["id", "stage", "taskNumber", "retry", "pending", "attempt", "lastVerificationPassed", "model", "effort", "resolution"], "state.dispatch");
            RequireKeys(value["baselines"]!.AsObject(), ["head", "worktree", "untracked"], "state.baselines");
            RequireKeys(value["review"]!.AsObject(), ["coverage", "verdict", "fixRound", "authorizedPaths", "manifest", "critiqueFile", "critiqueFiles"], "state.review");
            RequireKeys(value["approval"]!.AsObject(), ["planHash", "nonce", "revision"], "state.approval");
            RequireKeys(value["materialization"]!.AsObject(), ["transactionId", "generation", "committed"], "state.materialization");
            if (value["materialization"]!["generation"]?.GetValue<string>() != "v2") throw new CliFailure("state", "state materialization generation is unsupported", 3);
            return value;
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"state is malformed: {error.Message}", 3); }
    }

    public static JsonObject Ensure(string workspace, string? planHash = null)
    {
        try { return Load(workspace); }
        catch (CliFailure failure) when (failure.Message == "no Forge state exists")
        {
            var state = CreateEmpty(planHash);
            DurableFiles.WriteJson(StatePath(workspace), state);
            return state;
        }
    }

    private static void RequireKeys(JsonObject value, IReadOnlyCollection<string> expected, string label)
    {
        var actual = value.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw new CliFailure("state", $"{label} fields are not exact", 3);
    }

    public static JsonObject Update(string workspace, Action<JsonObject> update) => Update(workspace, null, update);

    public static JsonObject Update(string workspace, JsonObject? expectedState, Action<JsonObject> update)
    {
        using var stateLock = ForgeStateLock.Acquire(workspace);
        var state = Load(workspace);
        if (expectedState is not null && !string.Equals(Hashing.Sha256Hex(state.ToJsonString()), Hashing.Sha256Hex(expectedState.ToJsonString()), StringComparison.Ordinal)) throw new CliFailure("state", "Forge state changed during the operation; retry against the current dispatch", 3);
        update(state);
        state["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        DurableFiles.WriteJson(StatePath(workspace), state);
        return state;
    }
}
