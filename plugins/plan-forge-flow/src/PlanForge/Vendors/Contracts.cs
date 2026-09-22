using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanForge.Vendors;

/// <summary>The identified critique exposed by the MCP tools and written to the Flow log.</summary>
internal sealed record Critique(string Verdict,
                                IReadOnlyList<Finding> Findings,
                                string Summary,
                                IReadOnlyList<UnresolvedAssessment>? UnresolvedAssessments = null,
                                IReadOnlyList<ReopeningProposal>? Reopenings = null);

internal sealed record Finding(string Severity,
                               string Where,
                               string What,
                               [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                               string? FindingId = null);

/// <summary>
/// The Vendor-only wire answer. Findings do not carry IDs: the Run-local ledger allocates those
/// after the complete answer passes semantic coverage validation.
/// </summary>
internal sealed class VendorCritique : IJsonOnDeserialized
{
    [JsonRequired]
    public required string Verdict { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<VendorFinding> Findings { get; init; } = null!;

    [JsonRequired]
    public required string Summary { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<UnresolvedAssessment> UnresolvedAssessments { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ReopeningProposal> Reopenings { get; init; } = null!;

    public void OnDeserialized()
    {
        if (Verdict is null || Summary is null)
            throw new JsonException("Critiques require verdict and summary.");
        if (Findings is null || Findings.Any(finding => finding is null))
            throw new JsonException("Critiques require a non-null findings array.");
        if (UnresolvedAssessments is null || UnresolvedAssessments.Any(assessment => assessment is null))
            throw new JsonException("Critiques require a non-null unresolvedAssessments array.");
        if (Reopenings is null || Reopenings.Any(reopening => reopening is null))
            throw new JsonException("Critiques require a non-null reopenings array.");
    }
}

internal sealed record VendorFinding([property: JsonRequired] string Severity,
                                     [property: JsonRequired] string Where,
                                     [property: JsonRequired] string What);

internal sealed record UnresolvedAssessment([property: JsonRequired] string FindingId,
                                            [property: JsonRequired] bool StillPresent,
                                            [property: JsonRequired] string Evidence);

internal sealed record ReopeningProposal([property: JsonRequired] string FindingId,
                                         [property: JsonRequired] string Evidence);

/// <summary>
/// One sourced piece of evidence in a Scout report. The Vendor schema deliberately has no size
/// limits; the host clips only the digest it returns to the orchestrator.
/// </summary>
internal sealed class ScoutItem : IJsonOnDeserialized
{
    [JsonRequired]
    public required string Text { get; init; } = null!;

    [JsonRequired]
    public required string SourceKind { get; init; } = null!;

    [JsonRequired]
    public required string Source { get; init; } = null!;

    public void OnDeserialized()
    {
        if (Text is null || SourceKind is null || Source is null)
            throw new JsonException("Scout evidence items require text, sourceKind and source.");

        if (SourceKind is "repository")
        {
            if (!IsRepositoryLocator(Source))
                throw new JsonException("Scout repository sources must use <path>:<positive-line> or <path>#<non-empty-symbol>.");
        }
        else if (SourceKind is "external")
        {
            if (!IsExternalLocator(Source))
                throw new JsonException("Scout external sources must be absolute http:// or https:// URLs.");
        }
        else
        {
            throw new JsonException("Scout sourceKind must be repository or external.");
        }
    }

    internal static bool IsRepositoryLocator(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        var lineSeparator = source.LastIndexOf(':');
        if (lineSeparator > 0
            && int.TryParse(source[(lineSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                            out var line)
            && line > 0)
            return true;

        var symbolSeparator = source.LastIndexOf('#');
        return symbolSeparator > 0
               && symbolSeparator < source.Length - 1
               && !string.IsNullOrWhiteSpace(source[(symbolSeparator + 1)..]);
    }

    internal static bool IsExternalLocator(string source) =>
        !string.IsNullOrWhiteSpace(source)
        && !source.Any(char.IsWhiteSpace)
        && (source.StartsWith("http://", StringComparison.Ordinal)
            || source.StartsWith("https://", StringComparison.Ordinal))
        && Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0;
}

/// <summary>
/// The complete, deliberately flat structured answer returned by a Scout Vendor. These are the
/// five R8 evidence categories; every member is required even when a category is empty.
/// </summary>
internal sealed class ScoutReport : IJsonOnDeserialized
{
    [JsonRequired]
    public required string Summary { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ScoutItem> ConfirmedFacts { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ScoutItem> MaterialAssumptions { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ScoutItem> OpenDecisions { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ScoutItem> LikelyChangeSurface { get; init; } = null!;

    [JsonRequired]
    public required IReadOnlyList<ScoutItem> VerificationEvidence { get; init; } = null!;

    public void OnDeserialized()
    {
        if (Summary is null)
            throw new JsonException("Scout reports require summary.");

        ValidateCategory(ConfirmedFacts, "confirmedFacts");
        ValidateCategory(MaterialAssumptions, "materialAssumptions");
        ValidateCategory(OpenDecisions, "openDecisions");
        ValidateCategory(LikelyChangeSurface, "likelyChangeSurface");
        ValidateCategory(VerificationEvidence, "verificationEvidence");
    }

    private static void ValidateCategory(IReadOnlyList<ScoutItem>? items, string name)
    {
        if (items is null)
            throw new JsonException($"Scout reports require {name}.");

        if (items.Any(item => item is null))
            throw new JsonException($"Scout {name} cannot contain null items.");
    }
}

/// <summary>The bounded host-side view of a complete Scout report.</summary>
internal sealed record ScoutDigest(string Summary,
                                   IReadOnlyList<ScoutItem> ConfirmedFacts,
                                   IReadOnlyList<ScoutItem> MaterialAssumptions,
                                   IReadOnlyList<ScoutItem> OpenDecisions,
                                   IReadOnlyList<ScoutItem> LikelyChangeSurface,
                                   IReadOnlyList<ScoutItem> VerificationEvidence,
                                   bool Truncated);

internal sealed record ScoutFailure(string Code, string Summary);

/// <summary>
/// What a Builder must return after working one task, plus what the server found out for itself.
/// <see cref="Gate"/> is never the builder's to fill in: the schema the vendor answers does not
/// name it, so it deserializes as <see langword="null"/> and the act sets it after running the
/// task's gate on the host. <see cref="Status"/> is the builder's word, except that a task whose
/// gate failed is rewritten to <c>gate_failed</c> — see docs/adr/0015.
/// </summary>
internal sealed record BuildResult(string Status,
                                   IReadOnlyList<string> FilesChanged,
                                   Verification Verification,
                                   string Summary,
                                   GateRun? Gate = null);

/// <summary>
/// The builder's own account of whether it proved the work, separate from whether it did the work.
/// Self-reported: reacting to <c>unavailable</c> or <c>failed</c> belongs to the orchestrator where
/// the gate is a condition rather than a command. Where it is a command, <see cref="GateRun"/> is
/// the server's own answer and this report is context, not verdict.
/// </summary>
internal sealed record Verification(string Outcome, string Evidence);

/// <summary>
/// The server's own run of a gate command on the host, after the builder's turn.
/// </summary>
/// <param name="Outcome">
/// <c>passed</c>, <c>failed</c> or <c>timeout</c> when the command ran; <c>not_executable</c> when
/// the gate is a condition or the task states none, so the builder's verification stands;
/// <c>not_run</c> when there was no point or no way to run it — the builder reported
/// <c>blocked</c> after a verification that <c>failed</c>, so it watched the check fail itself, or
/// no PowerShell was found. A <c>blocked</c> report whose verification was <c>unavailable</c> is run
/// like a <c>done</c> one: see <see cref="Acts.Gatekeeper"/>.
/// </param>
/// <param name="Label"><c>Gate</c> for a task's own gate; the run-wide labels (<c>G1, G2</c>) after a fix round.</param>
/// <param name="Command">The command as the plan wrote it, or <see langword="null"/> when nothing ran.</param>
/// <param name="ExitCode">The shell's exit code, absent on a timeout or when nothing ran.</param>
/// <param name="Output">The tail of what the command wrote, stdout then stderr.</param>
/// <param name="Seconds">How long the command ran.</param>
/// <param name="Detail">Why nothing ran, when nothing did.</param>
internal sealed record GateRun(string Outcome,
                               string Label,
                               string? Command,
                               int? ExitCode,
                               string? Output,
                               double? Seconds,
                               string? Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VendorCritique))]
[JsonSerializable(typeof(VendorFinding))]
[JsonSerializable(typeof(UnresolvedAssessment))]
[JsonSerializable(typeof(ReopeningProposal))]
[JsonSerializable(typeof(Critique))]
[JsonSerializable(typeof(Finding))]
[JsonSerializable(typeof(BuildResult))]
[JsonSerializable(typeof(ScoutReport))]
[JsonSerializable(typeof(ScoutItem))]
[JsonSerializable(typeof(ScoutDigest))]
[JsonSerializable(typeof(ScoutFailure))]
internal sealed partial class ContractJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VendorCritique))]
[JsonSerializable(typeof(VendorFinding))]
[JsonSerializable(typeof(UnresolvedAssessment))]
[JsonSerializable(typeof(ReopeningProposal))]
internal sealed partial class CritiqueJson : JsonSerializerContext;

internal static class Schemas
{
    public static VendorSchema<VendorCritique> Critique { get; } = new(
        """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["approve", "revise"] },
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "severity": { "type": "string", "enum": ["blocker", "major", "minor"] },
                  "where": { "type": "string" },
                  "what": { "type": "string" }
                },
                "required": ["severity", "where", "what"],
                "additionalProperties": false
              }
            },
            "summary": { "type": "string" },
            "unresolvedAssessments": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "findingId": { "type": "string", "pattern": "^F-[0-9]{4}$" },
                  "stillPresent": { "type": "boolean" },
                  "evidence": { "type": "string", "minLength": 1 }
                },
                "required": ["findingId", "stillPresent", "evidence"],
                "additionalProperties": false
              }
            },
            "reopenings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "findingId": { "type": "string", "pattern": "^F-[0-9]{4}$" },
                  "evidence": { "type": "string", "minLength": 1 }
                },
                "required": ["findingId", "evidence"],
                "additionalProperties": false
              }
            }
          },
          "required": ["verdict", "findings", "summary", "unresolvedAssessments", "reopenings"],
          "additionalProperties": false
        }
        """,
        CritiqueJson.Default.VendorCritique);

    public static VendorSchema<BuildResult> BuildResult { get; } = new(
        """
        {
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["done", "blocked"] },
            "filesChanged": { "type": "array", "items": { "type": "string" } },
            "verification": {
              "type": "object",
              "properties": {
                "outcome": { "type": "string", "enum": ["passed", "failed", "unavailable"] },
                "evidence": { "type": "string" }
              },
              "required": ["outcome", "evidence"],
              "additionalProperties": false
            },
            "summary": { "type": "string" }
          },
          "required": ["status", "filesChanged", "verification", "summary"],
          "additionalProperties": false
        }
        """,
        ContractJson.Default.BuildResult);

    public static VendorSchema<ScoutReport> ScoutReport { get; } = new(
        """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "confirmedFacts": { "type": "array", "items": { "$ref": "#/$defs/scoutItem" } },
            "materialAssumptions": { "type": "array", "items": { "$ref": "#/$defs/scoutItem" } },
            "openDecisions": { "type": "array", "items": { "$ref": "#/$defs/scoutItem" } },
            "likelyChangeSurface": { "type": "array", "items": { "$ref": "#/$defs/scoutItem" } },
            "verificationEvidence": { "type": "array", "items": { "$ref": "#/$defs/scoutItem" } }
          },
          "required": ["summary", "confirmedFacts", "materialAssumptions", "openDecisions", "likelyChangeSurface", "verificationEvidence"],
          "additionalProperties": false,
          "$defs": {
            "scoutItem": {
              "type": "object",
              "properties": {
                "text": { "type": "string" },
                "sourceKind": { "type": "string", "enum": ["repository", "external"] },
                "source": {
                  "type": "string",
                  "description": "repository uses <path>:<positive-line> (for example, src/PlanForge/Acts/Scout.cs:42) or <path>#<non-empty-symbol> (for example, src/PlanForge/Acts/Scout.cs#Scout.RunAsync); external uses an absolute http:// or https:// URL (for example, https://example.com/reference)."
                }
              },
              "required": ["text", "sourceKind", "source"],
              "additionalProperties": false
            }
          }
        }
        """,
        ContractJson.Default.ScoutReport);
}
