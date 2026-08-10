using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Workflow.State;

internal enum ForgePhase
{
    [JsonStringEnumMemberName("materialized")]
    Materialized,
    [JsonStringEnumMemberName("locked")]
    Locked,
    [JsonStringEnumMemberName("build")]
    Build,
    [JsonStringEnumMemberName("code-review")]
    CodeReview,
    [JsonStringEnumMemberName("done")]
    Done,
    [JsonStringEnumMemberName("done-with-findings")]
    DoneWithFindings,
}

internal enum ForgeRole
{
    Reviewer,
    Builder,
}

internal enum DispatchStage
{
    [JsonStringEnumMemberName("code")]
    Code,
    [JsonStringEnumMemberName("build")]
    Build,
    [JsonStringEnumMemberName("fix-build")]
    FixBuild,
    [JsonStringEnumMemberName("fix-review")]
    FixReview,
}

internal sealed record DispatchStageDefinition(ForgeRole Role, ForgePhase ExpectedPhase, bool RequiresFreshReview, bool CompletedByBuilder);

internal static class DispatchStages
{
    private static readonly Dictionary<DispatchStage, string> WireNames = new()
    {
        [DispatchStage.Code] = "code",
        [DispatchStage.Build] = "build",
        [DispatchStage.FixBuild] = "fix-build",
        [DispatchStage.FixReview] = "fix-review",
    };

    private static readonly Dictionary<DispatchStage, DispatchStageDefinition> Definitions =
        new()
        {
            [DispatchStage.Code] = new(ForgeRole.Reviewer, ForgePhase.CodeReview, true, false),
            [DispatchStage.Build] = new(ForgeRole.Builder, ForgePhase.Build, false, true),
            [DispatchStage.FixBuild] = new(ForgeRole.Builder, ForgePhase.CodeReview, false, true),
            [DispatchStage.FixReview] = new(ForgeRole.Reviewer, ForgePhase.CodeReview, true, false),
        };

    private static readonly Dictionary<string, DispatchStage> StagesByWireName =
        WireNames.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    public static DispatchStage Parse(string value)
        => StagesByWireName.TryGetValue(value, out var stage)
               ? stage
               : throw new CliFailure("usage", "--stage must be code|build|fix-build|fix-review");

    public static DispatchStage RequirePendingReviewVerdict(DispatchState dispatch, string? requestedStage)
    {
        if (!dispatch.Pending || dispatch.Stage is not { } dispatchStage || !dispatchStage.IsReview()) throw new CliFailure("state", "review verdict requires a pending review dispatch", 3);
        var expectedStage = requestedStage is null ? dispatchStage : Parse(requestedStage);
        if (!expectedStage.IsReview() || expectedStage != dispatchStage) throw new CliFailure("state", "review verdict stage does not match the pending dispatch", 3);
        return expectedStage;
    }

    extension(DispatchStage stage)
    {
        private bool IsReview() => !stage.Definition().CompletedByBuilder;
        public DispatchStageDefinition Definition() => Definitions[stage];

        public string ToWireName() => WireNames.TryGetValue(stage, out var wireName)
                                          ? wireName
                                          : throw new ArgumentOutOfRangeException(nameof(stage));
    }
}
internal static class ForgePhases
{
    private static readonly Dictionary<ForgePhase, string> WireNames = new()
    {
        [ForgePhase.Materialized] = "materialized",
        [ForgePhase.Locked] = "locked",
        [ForgePhase.Build] = "build",
        [ForgePhase.CodeReview] = "code-review",
        [ForgePhase.Done] = "done",
        [ForgePhase.DoneWithFindings] = "done-with-findings",
    };

    private static readonly Dictionary<string, ForgePhase> PhasesByWireName =
        WireNames.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);

    public static ForgePhase Parse(string value)
        => PhasesByWireName.TryGetValue(value, out var phase)
               ? phase
               : throw new CliFailure("usage", "phase is unsupported");

    public static string ToWireName(this ForgePhase phase) => WireNames.TryGetValue(phase, out var wireName)
                                                               ? wireName
                                                               : throw new ArgumentOutOfRangeException(nameof(phase));
}

internal sealed record PinnedSelection(string Model, string Effort);

internal sealed record BaselineEntry(string Path, string Hash);

internal sealed record CritiqueEntry(string Path, string Hash);
internal sealed record RunIdentity(string Source, string TransactionId);
internal sealed record ReviewerGuarantee(string Kind);
internal sealed record ApprovalGuarantee(string Kind);
internal sealed record CursorModelWaiverAudit(string Role, string Model, string Effort, string CursorVersion, string Observed, string Consent, string Timestamp, string ModelGuarantee);

internal sealed record WorkflowState
{
    public ForgePhase Phase { get; set; } = ForgePhase.Materialized;
    public int Round { get; set; }
    public int MaxRounds { get; set; } = 5;
    public int MaxFixRounds { get; set; } = 3;
    public int MaxBuildRetries { get; set; } = 3;
    public int? TaskCount { get; set; }
    public int NextTaskNumber { get; set; } = 1;
    public bool Amendment { get; set; }
    public List<PlanTask>? Tasks { get; set; }
}

internal sealed record ModelState
{
    public PinnedSelection? Reviewer { get; set; }
    public PinnedSelection? Builder { get; set; }

    public PinnedSelection? For(ForgeRole role) => role == ForgeRole.Reviewer ? Reviewer : Builder;
}

internal sealed record AgentState
{
    public string? BuilderId { get; set; }
    public string? LastBuilderDispatchId { get; set; }
    public List<string> BuilderIds { get; set; } = [];
    public List<string> ReviewerIds { get; set; } = [];
    public string? LastReviewerId { get; set; }
    public string? LastReviewerDispatchId { get; set; }
}

internal sealed record DispatchState
{
    public string? Id { get; set; }
    public DispatchStage? Stage { get; set; }
    public int? TaskNumber { get; set; }
    public int Retry { get; set; }
    public bool Pending { get; set; }
    public bool? LastVerificationPassed { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Conflict { get; set; }
}

internal sealed record BaselinesState
{
    public string? Head { get; set; }
    public string? Worktree { get; set; }
    public List<BaselineEntry> Untracked { get; set; } = [];
}

internal sealed record ReviewState
{
    public string? Coverage { get; set; }
    public string? Verdict { get; set; }
    public int FixRound { get; set; }
    public List<string> AuthorizedPaths { get; set; } = [];
    public ReviewManifest? Manifest { get; set; }
    public string? CritiqueFile { get; set; }
    public string? VerdictFile { get; set; }
    public string? VerdictHash { get; set; }
    public List<CritiqueEntry> CritiqueFiles { get; set; } = [];
}

internal sealed record ForgeState
{
    public const int SchemaVersion = 1;
    public const int Version = 5;
    public const string Generation = "v4";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersionValue { get; set; } = SchemaVersion;
    public HostKind Host { get; set; } = HostKind.Codex;
    public string RepositoryScopeId { get; set; } = string.Empty;
    public RunIdentity? SourceRun { get; set; }
    public List<CursorModelWaiverAudit> ModelWaiverAudit { get; set; } = [];
    public ReviewerGuarantee? ReviewerGuarantee { get; set; }
    public ApprovalGuarantee? ApprovalGuarantee { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public WorkflowState Workflow { get; set; } = new();
    public ModelState Models { get; set; } = new();
    public AgentState Agents { get; set; } = new();
    public DispatchState Dispatch { get; set; } = new();
    public BaselinesState Baselines { get; set; } = new();
    public ReviewState Review { get; set; } = new();

    public static ForgeState CreateEmpty(HostKind host = HostKind.Codex, string? repositoryScopeId = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new ForgeState
        {
            Host = host,
            RepositoryScopeId = repositoryScopeId ?? "000000000000000000000000",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public ForgeState DeepCopy() => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(this, ForgeJsonContext.Default.ForgeState),
        ForgeJsonContext.Default.ForgeState) ?? throw new JsonException("JSON value is null");
}
