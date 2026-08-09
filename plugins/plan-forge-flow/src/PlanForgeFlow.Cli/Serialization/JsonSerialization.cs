using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForgeFlow.Workflow.State;
using PlanForgeFlow.Cursor;

namespace PlanForgeFlow.Serialization;

/// <summary>
/// Source-generated metadata for JSON that is owned by Plan Forge.  Keeping this
/// separate from <see cref="CodexJsonContext"/> means persisted files fail closed
/// when their schema is not what this executable expects.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(ForgeState))]
[JsonSerializable(typeof(PendingRun))]
[JsonSerializable(typeof(CursorCapAudit))]
[JsonSerializable(typeof(MaterializationTransaction))]
[JsonSerializable(typeof(PendingPlanDocument))]
[JsonSerializable(typeof(MaterializationRequest))]
[JsonSerializable(typeof(ReviewDecision))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ReviewManifest))]
[JsonSerializable(typeof(JsonSuccess<ForgeState>))]
[JsonSerializable(typeof(JsonSuccess<PendingRun>))]
[JsonSerializable(typeof(JsonSuccess<PendingRun?>))]
[JsonSerializable(typeof(JsonSuccess<MaterializeData>))]
[JsonSerializable(typeof(JsonSuccess<InstallAgentsData>))]
[JsonSerializable(typeof(JsonSuccess<DispatchState>))]
[JsonSerializable(typeof(JsonSuccess<ReviewManifest>))]
[JsonSerializable(typeof(JsonSuccess<AuthorizationData>))]
[JsonSerializable(typeof(JsonSuccess<VerdictData>))]
[JsonSerializable(typeof(JsonSuccess<AgentState>))]
[JsonSerializable(typeof(JsonSuccess<DoctorData>))]
[JsonSerializable(typeof(JsonSuccess<StatusMissingData>))]
[JsonSerializable(typeof(JsonSuccess<StatusPresentData>))]
[JsonSerializable(typeof(JsonSuccess<CleanupData>))]
[JsonSerializable(typeof(JsonSuccess<HelpData>))]
[JsonSerializable(typeof(JsonFailure))]
internal sealed partial class ForgeJsonContext : JsonSerializerContext;

/// <summary>
/// Codex controls hook and transcript payloads.  These models intentionally
/// tolerate additive fields so a Codex rollout cannot disable plan capture.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(HookInput))]
[JsonSerializable(typeof(HookCaptureOutput))]
[JsonSerializable(typeof(TranscriptEnvelope))]
internal sealed partial class CodexJsonContext : JsonSerializerContext;

internal sealed record PendingPlanDocument(int SchemaVersion, HostKind Host, string Workspace, string Plan);

internal sealed record ModelSelection(string Model, string Effort);

internal sealed record MaterializationRequest(string ReviewLog,
                                              int CompletedReviewRounds,
                                              int MaxRounds,
                                              ModelSelection Reviewer,
                                              ModelSelection Builder);

internal sealed record ReviewDecision(string Verdict, string? Coverage);

internal sealed record WithheldFile(string Path, string Reason);

internal sealed record ReviewManifest
{
    public int Version { get; init; }
    public string Generation { get; init; } = string.Empty;
    public string Coverage { get; init; } = string.Empty;
    public List<string> AllowPaths { get; init; } = [];
    public List<string> SensitiveAllowedPaths { get; init; } = [];
    public List<string> PreExistingAuthorizedPaths { get; init; } = [];
    public List<string> AuthorizedPaths { get; init; } = [];
    public string? AuthorizationNote { get; init; }
    public int ChangedFiles { get; init; }
    public List<string> PreExistingFiles { get; init; } = [];
    public List<string> InRunFiles { get; init; } = [];
    public List<string> UntrackedFiles { get; init; } = [];
    public List<WithheldFile> Withheld { get; init; } = [];
    public long AggregateBytes { get; init; }
    public string? TreeFingerprint { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public Dictionary<string, string> EvidenceHashes { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record JsonSuccess<T>(bool Ok, string Command, T Data);

internal sealed record JsonError(string Code, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WithheldFile>? Details);

internal sealed record JsonFailure(bool Ok, string Command, JsonError Error);

internal sealed record MaterializeData(string Action, string Phase, PinnedSelection Reviewer, PinnedSelection Builder);

internal sealed record InstallAgentsData(bool Installed, List<string> Roles);

internal sealed record AuthorizationData(List<string> AuthorizedPaths, string AuthorizationNote);

internal sealed record VerdictData(string Action, string Stage, string Verdict, string? Coverage, int Round, ReviewState Review);

internal sealed record ToolCheckData(bool Ok, string? Version, string? Error);

internal sealed record DoctorData(string Workspace, ToolCheckData Git, ToolCheckData Dotnet, bool State, string? RepositoryScopeId = null);

internal sealed record StatusMissingData(bool Exists);

internal sealed record StatusPresentData(int Version,
                                         string Generation,
                                         string CreatedAt,
                                         string UpdatedAt,
                                         WorkflowState Workflow,
                                         ModelState Models,
                                         AgentState Agents,
                                         DispatchState Dispatch,
                                         BaselinesState Baselines,
                                         ReviewState Review,
                                         bool Exists,
                                         int SchemaVersion = ForgeState.SchemaVersion,
                                         HostKind Host = HostKind.Codex,
                                         RunIdentity? SourceRun = null,
                                         List<CursorModelWaiverAudit>? ModelWaiverAudit = null,
                                         ReviewerGuarantee? ReviewerGuarantee = null,
                                         ApprovalGuarantee? ApprovalGuarantee = null,
                                         string? RepositoryScopeId = null);

internal sealed record CleanupData(bool Cleaned, bool PurgedAgents);

internal sealed record HelpData(string Usage, List<string> Commands);

internal sealed record HookInput(string? Cwd, [property: JsonPropertyName("transcript_path")] string? TranscriptPath);

internal sealed record HookSpecificOutput(string HookEventName, string AdditionalContext);

internal sealed record HookCaptureOutput(HookSpecificOutput HookSpecificOutput);

internal sealed record TranscriptEnvelope(string? Type, JsonElement Payload);
