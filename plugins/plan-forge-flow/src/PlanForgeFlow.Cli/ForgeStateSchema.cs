using System.Globalization;
using System.Text.Json.Serialization;

namespace PlanForgeFlow;

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
    [JsonStringEnumMemberName("plan")]
    Plan,
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
    private static readonly IReadOnlyDictionary<DispatchStage, DispatchStageDefinition> Definitions =
        new Dictionary<DispatchStage, DispatchStageDefinition>
        {
            [DispatchStage.Plan] = new(ForgeRole.Reviewer, ForgePhase.Locked, false, false),
            [DispatchStage.Code] = new(ForgeRole.Reviewer, ForgePhase.CodeReview, true, false),
            [DispatchStage.Build] = new(ForgeRole.Builder, ForgePhase.Build, false, true),
            [DispatchStage.FixBuild] = new(ForgeRole.Builder, ForgePhase.CodeReview, false, true),
            [DispatchStage.FixReview] = new(ForgeRole.Reviewer, ForgePhase.CodeReview, true, false),
        };

    public static DispatchStage Parse(string value)
        => value.ToLowerInvariant() switch
        {
            "plan" => DispatchStage.Plan,
            "code" => DispatchStage.Code,
            "build" => DispatchStage.Build,
            "fix-build" => DispatchStage.FixBuild,
            "fix-review" => DispatchStage.FixReview,
            _ => throw new CliFailure("usage", "--stage must be plan|code|build|fix-build|fix-review"),
        };

    public static DispatchStageDefinition Definition(this DispatchStage stage) => Definitions[stage];

    public static bool IsReview(this DispatchStage stage) => !stage.Definition().CompletedByBuilder;

    public static DispatchStage RequirePendingReviewVerdict(DispatchState dispatch, string? requestedStage)
    {
        if (!dispatch.Pending || dispatch.Stage is not { } dispatchStage || !dispatchStage.IsReview()) throw new CliFailure("state", "review verdict requires a pending review dispatch", 3);
        var expectedStage = requestedStage is null ? dispatchStage : Parse(requestedStage);
        if (!expectedStage.IsReview() || expectedStage != dispatchStage) throw new CliFailure("state", "review verdict stage does not match the pending dispatch", 3);
        return expectedStage;
    }

    public static string ToWireName(this DispatchStage stage) => stage switch
    {
        DispatchStage.Plan => "plan",
        DispatchStage.Code => "code",
        DispatchStage.Build => "build",
        DispatchStage.FixBuild => "fix-build",
        DispatchStage.FixReview => "fix-review",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

}

internal static class ForgePhases
{
    public static string ToWireName(this ForgePhase phase) => phase switch
    {
        ForgePhase.Materialized => "materialized",
        ForgePhase.Locked => "locked",
        ForgePhase.Build => "build",
        ForgePhase.CodeReview => "code-review",
        ForgePhase.Done => "done",
        ForgePhase.DoneWithFindings => "done-with-findings",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}

internal sealed record PinnedSelection(string Model, string Effort);

internal sealed record BaselineEntry(string Path, string Hash);

internal sealed record CritiqueEntry(string Path, string Hash);

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
    public const int Version = 5;
    public const string Generation = "v4";

    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public WorkflowState Workflow { get; set; } = new();
    public ModelState Models { get; set; } = new();
    public AgentState Agents { get; set; } = new();
    public DispatchState Dispatch { get; set; } = new();
    public BaselinesState Baselines { get; set; } = new();
    public ReviewState Review { get; set; } = new();

    public static ForgeState CreateEmpty()
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new ForgeState
        {
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public ForgeState DeepCopy() => JsonSerialization.Deserialize(
        JsonSerialization.Serialize(this, ForgeJsonContext.Default.ForgeState),
        ForgeJsonContext.Default.ForgeState);
}

internal static class ForgeStateSchema
{
    public static ForgeState CreateEmpty() => ForgeState.CreateEmpty();
    public static DispatchState CreateDispatch() => new();
    public static ReviewState CreateReview(IEnumerable<CritiqueEntry>? critiqueFiles = null) => new() { CritiqueFiles = critiqueFiles?.ToList() ?? [] };
}
