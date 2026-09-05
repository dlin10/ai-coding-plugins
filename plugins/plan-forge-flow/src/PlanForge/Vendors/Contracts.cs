using System.Text.Json.Serialization;

namespace PlanForge.Vendors;

/// <summary>
/// What a Critic must return. Replaces the VERDICT:/COVERAGE:/ROSLYN: marker parsers, the
/// "exactly one VERDICT line and it is last" checks, and the critique sidecar files.
/// </summary>
internal sealed record Critique(string Verdict, IReadOnlyList<Finding> Findings, string Summary);

internal sealed record Finding(string Severity, string Where, string What);

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
/// <c>blocked</c>, or no PowerShell was found.
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
                "required": ["severity", "where", "what"],
                "additionalProperties": false
              }
            },
            "summary": { "type": "string" }
          },
          "required": ["verdict", "findings", "summary"],
          "additionalProperties": false
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
}
