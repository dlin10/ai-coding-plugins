using System.Text.Json.Serialization;

namespace PlanForge.Vendors;

/// <summary>
/// What a Critic must return. Replaces the VERDICT:/COVERAGE:/ROSLYN: marker parsers, the
/// "exactly one VERDICT line and it is last" checks, and the critique sidecar files.
/// </summary>
internal sealed record Critique(string Verdict, IReadOnlyList<Finding> Findings, string Summary);

internal sealed record Finding(string Severity, string Where, string What);

/// <summary>What a Builder must return after working one task.</summary>
internal sealed record BuildResult(string Status, IReadOnlyList<string> FilesChanged, string Summary);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Critique))]
[JsonSerializable(typeof(BuildResult))]
internal sealed partial class ContractJson : JsonSerializerContext;

internal static class Schemas
{
    public static VendorSchema<Critique> Critique { get; } = new(
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
                "required": ["severity", "where", "what"]
              }
            },
            "summary": { "type": "string" }
          },
          "required": ["verdict", "findings", "summary"]
        }
        """,
        ContractJson.Default.Critique);

    public static VendorSchema<BuildResult> BuildResult { get; } = new(
        """
        {
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["done", "blocked"] },
            "filesChanged": { "type": "array", "items": { "type": "string" } },
            "summary": { "type": "string" }
          },
          "required": ["status", "filesChanged", "summary"]
        }
        """,
        ContractJson.Default.BuildResult);
}
