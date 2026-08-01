using System.Text;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class ResumeEnvelope
{
    private const string Prefix = "<!-- plan-forge-resume:v3:";
    private const string Suffix = " -->";
    public static string Build(string humanPlan, JsonObject envelope)
    {
        var plan = CanonicalText.NormalizePlan(humanPlan);
        var copy = JsonNode.Parse(envelope.ToJsonString())!.AsObject();
        var encoded = Hashing.Base64UrlEncode(copy.ToJsonString());
        return plan + Prefix + encoded + Suffix + "\n";
    }

    public static (string HumanPlan, JsonObject Envelope) Parse(string wrapper)
    {
        var first = wrapper.IndexOf(Prefix, StringComparison.Ordinal);
        var last = wrapper.LastIndexOf(Prefix, StringComparison.Ordinal);
        if (first < 0 || first != last) throw new CliFailure("state", "approval wrapper must contain exactly one v3 envelope", 3);
        var end = wrapper.IndexOf(Suffix, first, StringComparison.Ordinal);
        if (end < 0 || wrapper[(end + Suffix.Length)..] != "\n") throw new CliFailure("state", "approval envelope must be terminal", 3);
        var rawHumanPlan = wrapper[..first];
        var humanPlan = CanonicalText.NormalizePlan(rawHumanPlan);
        var encoded = wrapper[(first + Prefix.Length)..end];
        JsonObject envelope;
        try
        {
            var decoded = Hashing.Base64UrlDecode(encoded);
            envelope = JsonNode.Parse(decoded)!.AsObject();
        }
        catch (Exception error) { throw new CliFailure("state", $"approval envelope is malformed: {error.Message}", 3); }
        return (humanPlan, envelope);
    }

    public static JsonObject Create(
        string humanPlan,
        string reviewLog,
        int completedRounds,
        int maxRounds,
        JsonObject reviewer,
        JsonObject builder,
        RepositoryIdentity repository,
        SessionCapture? capture,
        int revision)
    {
        var normalized = CanonicalText.NormalizePlan(humanPlan);
        if (capture is null) throw new CliFailure("state", "approval requires a captured Codex session", 3);
        var reviewerSelection = ModelSelections.Validate("reviewer", reviewer["model"]?.GetValue<string>() ?? string.Empty, reviewer["effort"]?.GetValue<string>() ?? string.Empty);
        var builderSelection = ModelSelections.Validate("builder", builder["model"]?.GetValue<string>() ?? string.Empty, builder["effort"]?.GetValue<string>() ?? string.Empty);
        var nonce = Hashing.Nonce();
        var origin = new JsonObject
        {
            ["sessionId"] = capture.SessionId,
            ["transcriptPath"] = capture.TranscriptPath,
            ["turnId"] = capture.TurnId,
            ["itemId"] = IssuanceItemId(capture.SessionId, capture.TranscriptPath, capture.TurnId, nonce, Hashing.Sha256Hex(normalized)),
        };
        var envelope = new JsonObject
        {
            ["version"] = 3,
            ["plan"] = new JsonObject
            {
                ["humanPlanHash"] = Hashing.Sha256Hex(normalized),
                ["planRevision"] = revision,
                ["completedReviewRounds"] = completedRounds,
                ["maxRounds"] = maxRounds,
                ["reviewLog"] = CanonicalText.NormalizeReviewLog(reviewLog),
            },
            ["repository"] = repository.ToJson(),
            ["origin"] = origin,
            ["nonce"] = nonce,
            ["selections"] = new JsonObject
            {
                ["reviewer"] = reviewerSelection,
                ["builder"] = builderSelection,
                ["builderPlanHash"] = Hashing.Sha256Hex(normalized),
            },
        };
        return envelope;
    }

    private static string IssuanceItemId(string? sessionId, string? transcriptPath, string? turnId, string nonce, string planHash)
        => "forge-" + Hashing.Sha256Hex($"{sessionId}\n{transcriptPath}\n{turnId}\n{nonce}\n{planHash}")[..48];

}
