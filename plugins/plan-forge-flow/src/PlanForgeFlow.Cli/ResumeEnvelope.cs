using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal static class ResumeEnvelope
{
    private const string Prefix = "<!-- plan-forge-resume:v3:";
    private const string Suffix = " -->";
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ModelPattern = new("^[A-Za-z0-9][A-Za-z0-9._\\[\\]=,-]{0,255}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Build(string humanPlan, JsonObject envelope)
    {
        var plan = CanonicalText.NormalizePlan(humanPlan);
        var copy = JsonNode.Parse(envelope.ToJsonString())!.AsObject();
        Validate(copy, plan);
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
        if (!string.Equals(rawHumanPlan, humanPlan, StringComparison.Ordinal)) throw new CliFailure("state", "approval wrapper human plan is not canonical", 3);
        var encoded = wrapper[(first + Prefix.Length)..end];
        JsonObject envelope;
        try
        {
            if (!Regex.IsMatch(encoded, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)) throw new FormatException("encoding is malformed");
            var decoded = Hashing.Base64UrlDecode(encoded);
            if (!string.Equals(Hashing.Base64UrlEncode(decoded), encoded, StringComparison.Ordinal)) throw new FormatException("encoding is not canonical base64url");
            envelope = JsonNode.Parse(decoded)!.AsObject();
        }
        catch (Exception error) { throw new CliFailure("state", $"approval envelope is malformed: {error.Message}", 3); }
        try { Validate(envelope, humanPlan); }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"approval envelope is malformed: {error.Message}", 3); }
        return (humanPlan, envelope);
    }

    public static void Validate(JsonObject envelope, string humanPlan)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(envelope.ToJsonString()) > 2 * 1024 * 1024) throw new CliFailure("state", "approval envelope exceeds the size bound", 3);
            RequireKeys(envelope, ["version", "plan", "repository", "origin", "nonce", "selections"], "envelope");
            if (envelope["version"]?.GetValue<int>() != 3) throw new CliFailure("state", "approval envelope version 3 is required", 3);

            var plan = envelope["plan"]!.AsObject();
            RequireKeys(plan, ["humanPlanHash", "planRevision", "completedReviewRounds", "maxRounds", "reviewLog"], "envelope.plan");
            var planHash = plan["humanPlanHash"]?.GetValue<string>() ?? string.Empty;
            if (!HashPattern.IsMatch(planHash) || !string.Equals(planHash, Hashing.Sha256Hex(humanPlan), StringComparison.Ordinal))
            {
                throw new CliFailure("state", "approval wrapper human plan hash mismatch", 3);
            }

            var reviewLog = plan["reviewLog"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(CanonicalText.NormalizeReviewLog(reviewLog), reviewLog, StringComparison.Ordinal))
            {
                throw new CliFailure("state", "approval review log is not canonical", 3);
            }

            var revision = plan["planRevision"]?.GetValue<int>() ?? 0;
            var completedRounds = plan["completedReviewRounds"]?.GetValue<int>() ?? 0;
            var maxRounds = plan["maxRounds"]?.GetValue<int>() ?? 0;
            if (revision < 1 || completedRounds < 1 || maxRounds < completedRounds || maxRounds > 100)
            {
                throw new CliFailure("state", "review round bounds are invalid", 3);
            }

            var repository = envelope["repository"]!.AsObject();
            RequireKeys(repository, ["workspaceRoot", "gitCommonDir"], "envelope.repository");
            RequirePath(repository, "workspaceRoot", "envelope.repository");
            RequirePath(repository, "gitCommonDir", "envelope.repository");

            var origin = envelope["origin"]!.AsObject();
            RequireKeys(origin, ["sessionId", "transcriptPath", "turnId", "itemId"], "envelope.origin");
            var sessionId = RequireId(origin, "sessionId", "envelope.origin");
            var transcriptPath = RequirePathValue(origin, "transcriptPath", "envelope.origin", 4096);
            var turnId = RequireId(origin, "turnId", "envelope.origin");
            var itemId = RequireId(origin, "itemId", "envelope.origin");
            var nonce = envelope["nonce"]?.GetValue<string>() ?? string.Empty;
            if (!Regex.IsMatch(nonce, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)) throw new CliFailure("state", "approval nonce is malformed", 3);
            var expectedItemId = IssuanceItemId(sessionId, transcriptPath, turnId, nonce, planHash);
            if (!string.Equals(itemId, expectedItemId, StringComparison.Ordinal)) throw new CliFailure("state", "envelope.origin.itemId is not bound to its approval", 3);

            var selections = envelope["selections"]!.AsObject();
            RequireKeys(selections, ["reviewer", "builder", "builderPlanHash"], "envelope.selections");
            if (!string.Equals(selections["builderPlanHash"]?.GetValue<string>(), planHash, StringComparison.Ordinal)) throw new CliFailure("state", "builder selection is not bound to the plan hash", 3);
            var reviewer = ValidateSelection(selections["reviewer"]?.AsObject(), "envelope.selections.reviewer");
            var builder = ValidateSelection(selections["builder"]?.AsObject(), "envelope.selections.builder");
        }
        catch (CliFailure) { throw; }
        catch (Exception error)
        {
            throw new CliFailure("state", $"approval envelope is malformed: {error.Message}", 3);
        }
    }

    private static void RequirePath(JsonObject value, string key, string label)
    {
        _ = RequirePathValue(value, key, label, 4096);
    }

    private static string RequirePathValue(JsonObject value, string key, string label, int maxBytes)
    {
        var path = value[key]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > maxBytes || path.Any(char.IsControl) || !Path.IsPathRooted(path)) throw new CliFailure("state", $"{label}.{key} is not a bounded absolute path", 3);
        return path;
    }

    private static string RequireId(JsonObject value, string key, string label)
    {
        var id = value[key]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id)) throw new CliFailure("state", $"{label}.{key} is malformed", 3);
        return id;
    }

    private static (string Model, string Effort) ValidateSelection(JsonObject? value, string label)
    {
        if (value is null) throw new CliFailure("state", $"{label} is malformed", 3);
        RequireKeys(value, ["model", "effort"], label);
        var model = value["model"]?.GetValue<string>() ?? string.Empty;
        var effort = value["effort"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
        if (!ModelPattern.IsMatch(model) || !IdPattern.IsMatch(effort) || string.Equals(effort, "ultra", StringComparison.Ordinal))
        {
            throw new CliFailure("state", $"{label} is invalid", 3);
        }
        return (model, effort);
    }

    private static void RequireKeys(JsonObject value, IReadOnlyCollection<string> expected, string label)
    {
        var actual = value.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw new CliFailure("state", $"{label} fields are not exact", 3);
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
        Validate(envelope, normalized);
        return envelope;
    }

    private static string IssuanceItemId(string? sessionId, string? transcriptPath, string? turnId, string nonce, string planHash)
        => "forge-" + Hashing.Sha256Hex($"{sessionId}\n{transcriptPath}\n{turnId}\n{nonce}\n{planHash}")[..48];

}
