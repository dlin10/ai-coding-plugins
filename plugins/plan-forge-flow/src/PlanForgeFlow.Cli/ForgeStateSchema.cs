using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class ForgeStateSchema
{
    private const int Version = 3;
    private const string Generation = "v2";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GroupFields =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["workflow"] = ["phase", "round", "maxRounds", "fixRound", "maxFixRounds", "maxBuildRetries", "taskCount", "nextTaskNumber", "amendment", "tasks"],
            ["models"] = ["reviewer", "builder"],
            ["agents"] = ["builderId", "lastBuilderDispatchId", "reviewerIds", "lastReviewerId", "lastReviewerDispatchId"],
            ["dispatch"] = ["id", "stage", "taskNumber", "retry", "pending", "attempt", "lastVerificationPassed", "model", "effort", "conflict"],
            ["baselines"] = ["head", "worktree", "untracked"],
            ["review"] = ["coverage", "verdict", "fixRound", "authorizedPaths", "manifest", "critiqueFile", "verdictFile", "verdictHash", "critiqueFiles"],
            ["approval"] = ["planHash", "nonce", "revision"],
            ["materialization"] = ["transactionId", "generation", "committed"],
        };

    public static JsonObject CreateEmpty(string? planHash = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new()
        {
            ["version"] = Version,
            ["generation"] = Generation,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["workflow"] = new JsonObject { ["phase"] = "materialized", ["round"] = 0, ["maxRounds"] = 5, ["fixRound"] = 0, ["maxFixRounds"] = 3, ["maxBuildRetries"] = 3, ["taskCount"] = null, ["nextTaskNumber"] = 1, ["amendment"] = false, ["tasks"] = null },
            ["models"] = new JsonObject { ["reviewer"] = null, ["builder"] = null },
            ["agents"] = new JsonObject { ["builderId"] = null, ["lastBuilderDispatchId"] = null, ["reviewerIds"] = new JsonArray(), ["lastReviewerId"] = null, ["lastReviewerDispatchId"] = null },
            ["dispatch"] = CreateDispatch(),
            ["baselines"] = new JsonObject { ["head"] = null, ["worktree"] = null, ["untracked"] = new JsonArray() },
            ["review"] = CreateReview(),
            ["approval"] = new JsonObject { ["planHash"] = planHash, ["nonce"] = null, ["revision"] = 0 },
            ["materialization"] = new JsonObject { ["transactionId"] = null, ["generation"] = Generation, ["committed"] = false },
        };
    }

    public static JsonObject CreateDispatch()
        => new() { ["id"] = null, ["stage"] = null, ["taskNumber"] = null, ["retry"] = 0, ["pending"] = false, ["attempt"] = 0, ["lastVerificationPassed"] = null, ["model"] = null, ["effort"] = null, ["conflict"] = null };

    public static JsonObject CreateReview(JsonNode? critiqueFiles = null)
        => new() { ["coverage"] = null, ["verdict"] = null, ["fixRound"] = 0, ["authorizedPaths"] = new JsonArray(), ["manifest"] = null, ["critiqueFile"] = null, ["verdictFile"] = null, ["verdictHash"] = null, ["critiqueFiles"] = critiqueFiles?.DeepClone() ?? new JsonArray() };

    public static void Validate(JsonObject value)
    {
        if (value["version"]?.GetValue<int>() != Version || value["generation"]?.GetValue<string>() != Generation) throw new CliFailure("state", "unsupported Forge state version; expected nested state v3 generation v2", 3);
        RequireExact(value, ["version", "generation", "createdAt", "updatedAt", .. GroupFields.Keys], "state");
        foreach (var (group, fields) in GroupFields)
        {
            if (value[group] is not JsonObject groupValue) throw new CliFailure("state", $"state group {group} is missing", 3);
            RequireExact(groupValue, fields, $"state.{group}");
        }

        if (value["materialization"]!["generation"]?.GetValue<string>() != Generation) throw new CliFailure("state", "state materialization generation is unsupported", 3);
    }

    private static void RequireExact(JsonObject value, IReadOnlyCollection<string> expected, string label)
    {
        var actual = value.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw new CliFailure("state", $"{label} fields are not exact", 3);
    }
}
