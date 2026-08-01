using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal enum ForgePhase
{
    Materialized,
    Locked,
    Build,
    CodeReview,
    Done,
    DoneWithFindings,
}

internal enum ForgeRole
{
    Reviewer,
    Builder,
}

internal enum DispatchStage
{
    Plan,
    Code,
    Build,
    FixBuild,
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

    public static DispatchStage? ParseNullable(JsonNode? value)
    {
        var text = value?.GetValue<string>();
        return text is null ? null : ParseState(text);
    }

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

    private static DispatchStage ParseState(string value)
    {
        try { return Parse(value); }
        catch (CliFailure) { throw new CliFailure("state", $"state dispatch stage is unsupported: {value}", 3); }
    }
}

internal static class ForgePhases
{
    public static ForgePhase Parse(string value) => value switch
    {
        "materialized" => ForgePhase.Materialized,
        "locked" => ForgePhase.Locked,
        "build" => ForgePhase.Build,
        "code-review" => ForgePhase.CodeReview,
        "done" => ForgePhase.Done,
        "done-with-findings" => ForgePhase.DoneWithFindings,
        _ => throw new CliFailure("state", $"state phase is unsupported: {value}", 3),
    };

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

internal sealed record PinnedSelection(string Model, string Effort)
{
    public JsonObject ToJson() => new() { ["model"] = Model, ["effort"] = Effort };

    public static PinnedSelection? FromJson(JsonNode? value, string label)
    {
        if (value is null) return null;
        if (value is not JsonObject selection) throw new CliFailure("state", $"state {label} selection is malformed", 3);
        var model = selection["model"]?.GetValue<string>();
        var effort = selection["effort"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort)) throw new CliFailure("state", $"state {label} selection is malformed", 3);
        return new PinnedSelection(model, effort);
    }
}

internal sealed record BaselineEntry(string Path, string Hash)
{
    public JsonObject ToJson() => new() { ["path"] = Path, ["hash"] = Hash };
}

internal sealed record CritiqueEntry(string Path, string Hash)
{
    public JsonObject ToJson() => new() { ["path"] = Path, ["hash"] = Hash };
}

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
    public JsonObject? Manifest { get; set; }
    public string? CritiqueFile { get; set; }
    public string? VerdictFile { get; set; }
    public string? VerdictHash { get; set; }
    public List<CritiqueEntry> CritiqueFiles { get; set; } = [];
}

internal sealed record ApprovalState
{
    public string? PlanHash { get; set; }
    public string? Nonce { get; set; }
    public int Revision { get; set; }
}

internal sealed record MaterializationState
{
    public string Generation { get; set; } = ForgeState.Generation;
    public bool Committed { get; set; }
}

internal sealed record ForgeState
{
    public const int Version = 4;
    public const string Generation = "v3";

    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public WorkflowState Workflow { get; set; } = new();
    public ModelState Models { get; set; } = new();
    public AgentState Agents { get; set; } = new();
    public DispatchState Dispatch { get; set; } = new();
    public BaselinesState Baselines { get; set; } = new();
    public ReviewState Review { get; set; } = new();
    public ApprovalState Approval { get; set; } = new();
    public MaterializationState Materialization { get; set; } = new();

    public static ForgeState CreateEmpty(string? planHash = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new ForgeState
        {
            CreatedAt = now,
            UpdatedAt = now,
            Approval = new ApprovalState { PlanHash = planHash },
        };
    }

    public ForgeState DeepCopy() => FromJson(ToJson());

    public JsonObject ToJson() => new()
    {
        ["version"] = Version,
        ["generation"] = Generation,
        ["createdAt"] = CreatedAt,
        ["updatedAt"] = UpdatedAt,
        ["workflow"] = new JsonObject
        {
            ["phase"] = Workflow.Phase.ToWireName(), ["round"] = Workflow.Round, ["maxRounds"] = Workflow.MaxRounds,
            ["maxFixRounds"] = Workflow.MaxFixRounds, ["maxBuildRetries"] = Workflow.MaxBuildRetries,
            ["taskCount"] = Workflow.TaskCount, ["nextTaskNumber"] = Workflow.NextTaskNumber,
            ["amendment"] = Workflow.Amendment,
            ["tasks"] = Workflow.Tasks is null ? null : new JsonArray(Workflow.Tasks.Select(task => (JsonNode)task.ToJson()).ToArray()),
        },
        ["models"] = new JsonObject { ["reviewer"] = Models.Reviewer?.ToJson(), ["builder"] = Models.Builder?.ToJson() },
        ["agents"] = new JsonObject
        {
            ["builderId"] = Agents.BuilderId, ["lastBuilderDispatchId"] = Agents.LastBuilderDispatchId,
            ["reviewerIds"] = new JsonArray(Agents.ReviewerIds.Select(id => (JsonNode)id).ToArray()),
            ["lastReviewerId"] = Agents.LastReviewerId, ["lastReviewerDispatchId"] = Agents.LastReviewerDispatchId,
        },
        ["dispatch"] = new JsonObject
        {
            ["id"] = Dispatch.Id, ["stage"] = Dispatch.Stage?.ToWireName(), ["taskNumber"] = Dispatch.TaskNumber,
            ["retry"] = Dispatch.Retry, ["pending"] = Dispatch.Pending, ["lastVerificationPassed"] = Dispatch.LastVerificationPassed,
            ["model"] = Dispatch.Model, ["effort"] = Dispatch.Effort, ["conflict"] = Dispatch.Conflict,
        },
        ["baselines"] = new JsonObject
        {
            ["head"] = Baselines.Head, ["worktree"] = Baselines.Worktree,
            ["untracked"] = new JsonArray(Baselines.Untracked.Select(entry => (JsonNode)entry.ToJson()).ToArray()),
        },
        ["review"] = new JsonObject
        {
            ["coverage"] = Review.Coverage, ["verdict"] = Review.Verdict, ["fixRound"] = Review.FixRound,
            ["authorizedPaths"] = new JsonArray(Review.AuthorizedPaths.Select(path => (JsonNode)path).ToArray()),
            ["manifest"] = Review.Manifest?.DeepClone(), ["critiqueFile"] = Review.CritiqueFile,
            ["verdictFile"] = Review.VerdictFile, ["verdictHash"] = Review.VerdictHash,
            ["critiqueFiles"] = new JsonArray(Review.CritiqueFiles.Select(entry => (JsonNode)entry.ToJson()).ToArray()),
        },
        ["approval"] = new JsonObject { ["planHash"] = Approval.PlanHash, ["nonce"] = Approval.Nonce, ["revision"] = Approval.Revision },
        ["materialization"] = new JsonObject { ["generation"] = Materialization.Generation, ["committed"] = Materialization.Committed },
    };

    public static ForgeState FromJson(JsonObject value)
    {
        Validate(value);
        var workflow = Object(value, "workflow");
        var models = Object(value, "models");
        var agents = Object(value, "agents");
        var dispatch = Object(value, "dispatch");
        var baselines = Object(value, "baselines");
        var review = Object(value, "review");
        var approval = Object(value, "approval");
        var materialization = Object(value, "materialization");
        return new ForgeState
        {
            CreatedAt = RequiredString(value, "createdAt", "state"),
            UpdatedAt = RequiredString(value, "updatedAt", "state"),
            Workflow = new WorkflowState
            {
                Phase = ForgePhases.Parse(RequiredString(workflow, "phase", "workflow")), Round = RequiredInt(workflow, "round", "workflow"),
                MaxRounds = RequiredInt(workflow, "maxRounds", "workflow"), MaxFixRounds = RequiredInt(workflow, "maxFixRounds", "workflow"),
                MaxBuildRetries = RequiredInt(workflow, "maxBuildRetries", "workflow"), TaskCount = OptionalInt(workflow, "taskCount", "workflow"),
                NextTaskNumber = RequiredInt(workflow, "nextTaskNumber", "workflow"), Amendment = RequiredBool(workflow, "amendment", "workflow"),
                Tasks = OptionalArray(workflow, "tasks", "workflow")?.Select(ParseTask).ToList(),
            },
            Models = new ModelState { Reviewer = PinnedSelection.FromJson(models["reviewer"], "reviewer"), Builder = PinnedSelection.FromJson(models["builder"], "builder") },
            Agents = new AgentState
            {
                BuilderId = OptionalString(agents, "builderId", "agents"), LastBuilderDispatchId = OptionalString(agents, "lastBuilderDispatchId", "agents"),
                ReviewerIds = RequiredArray(agents, "reviewerIds", "agents").Select(item => RequiredString(item, "reviewer id")).ToList(),
                LastReviewerId = OptionalString(agents, "lastReviewerId", "agents"), LastReviewerDispatchId = OptionalString(agents, "lastReviewerDispatchId", "agents"),
            },
            Dispatch = new DispatchState
            {
                Id = OptionalString(dispatch, "id", "dispatch"), Stage = DispatchStages.ParseNullable(dispatch["stage"]),
                TaskNumber = OptionalInt(dispatch, "taskNumber", "dispatch"), Retry = RequiredInt(dispatch, "retry", "dispatch"),
                Pending = RequiredBool(dispatch, "pending", "dispatch"), LastVerificationPassed = OptionalBool(dispatch, "lastVerificationPassed", "dispatch"),
                Model = OptionalString(dispatch, "model", "dispatch"), Effort = OptionalString(dispatch, "effort", "dispatch"), Conflict = OptionalString(dispatch, "conflict", "dispatch"),
            },
            Baselines = new BaselinesState
            {
                Head = OptionalString(baselines, "head", "baselines"), Worktree = OptionalString(baselines, "worktree", "baselines"),
                Untracked = RequiredArray(baselines, "untracked", "baselines").Select(ParseBaseline).ToList(),
            },
            Review = new ReviewState
            {
                Coverage = OptionalString(review, "coverage", "review"), Verdict = OptionalString(review, "verdict", "review"),
                FixRound = RequiredInt(review, "fixRound", "review"),
                AuthorizedPaths = RequiredArray(review, "authorizedPaths", "review").Select(item => RequiredString(item, "authorized path")).ToList(),
                Manifest = review["manifest"] is null ? null : Object(review, "manifest").DeepClone().AsObject(),
                CritiqueFile = OptionalString(review, "critiqueFile", "review"), VerdictFile = OptionalString(review, "verdictFile", "review"),
                VerdictHash = OptionalString(review, "verdictHash", "review"),
                CritiqueFiles = RequiredArray(review, "critiqueFiles", "review").Select(ParseCritique).ToList(),
            },
            Approval = new ApprovalState
            {
                PlanHash = OptionalString(approval, "planHash", "approval"), Nonce = OptionalString(approval, "nonce", "approval"), Revision = RequiredInt(approval, "revision", "approval"),
            },
            Materialization = new MaterializationState
            {
                Generation = RequiredString(materialization, "generation", "materialization"), Committed = RequiredBool(materialization, "committed", "materialization"),
            },
        };
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GroupFields =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["workflow"] = ["phase", "round", "maxRounds", "maxFixRounds", "maxBuildRetries", "taskCount", "nextTaskNumber", "amendment", "tasks"],
            ["models"] = ["reviewer", "builder"],
            ["agents"] = ["builderId", "lastBuilderDispatchId", "reviewerIds", "lastReviewerId", "lastReviewerDispatchId"],
            ["dispatch"] = ["id", "stage", "taskNumber", "retry", "pending", "lastVerificationPassed", "model", "effort", "conflict"],
            ["baselines"] = ["head", "worktree", "untracked"],
            ["review"] = ["coverage", "verdict", "fixRound", "authorizedPaths", "manifest", "critiqueFile", "verdictFile", "verdictHash", "critiqueFiles"],
            ["approval"] = ["planHash", "nonce", "revision"],
            ["materialization"] = ["generation", "committed"],
        };

    private static void Validate(JsonObject value)
    {
        if (value["version"]?.GetValue<int>() != Version || value["generation"]?.GetValue<string>() != Generation) throw new CliFailure("state", "unsupported Forge state version; expected nested state v4 generation v3", 3);
        RequireExact(value, ["version", "generation", "createdAt", "updatedAt", .. GroupFields.Keys], "state");
        foreach (var (group, fields) in GroupFields)
        {
            var groupValue = Object(value, group);
            RequireExact(groupValue, fields, $"state.{group}");
        }

        if (Object(value, "materialization")["generation"]?.GetValue<string>() != Generation) throw new CliFailure("state", "state materialization generation is unsupported", 3);
    }

    private static PlanTask ParseTask(JsonNode? node)
    {
        var value = Object(node, "task");
        RequireExact(value, ["number", "hash", "text"], "state workflow task");
        return new PlanTask(RequiredInt(value, "number", "task"), RequiredString(value, "hash", "task"), RequiredString(value, "text", "task"));
    }

    private static BaselineEntry ParseBaseline(JsonNode? node)
    {
        var value = Object(node, "baseline entry");
        RequireExact(value, ["path", "hash"], "state baseline entry");
        return new BaselineEntry(RequiredString(value, "path", "baseline entry"), RequiredString(value, "hash", "baseline entry"));
    }

    private static CritiqueEntry ParseCritique(JsonNode? node)
    {
        var value = Object(node, "critique entry");
        RequireExact(value, ["path", "hash"], "state critique entry");
        return new CritiqueEntry(RequiredString(value, "path", "critique entry"), RequiredString(value, "hash", "critique entry"));
    }

    private static JsonObject Object(JsonNode? value, string label)
        => value as JsonObject ?? throw new CliFailure("state", $"state {label} is malformed", 3);

    private static JsonObject Object(JsonObject value, string key) => Object(value[key], key);

    private static JsonArray RequiredArray(JsonObject value, string key, string label)
        => value[key] as JsonArray ?? throw new CliFailure("state", $"state {label}.{key} is malformed", 3);

    private static JsonArray? OptionalArray(JsonObject value, string key, string label)
        => value[key] is null ? null : RequiredArray(value, key, label);

    private static string RequiredString(JsonObject value, string key, string label)
        => RequiredString(value[key], $"state {label}.{key}");

    private static string RequiredString(JsonNode? value, string label)
    {
        var text = value?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(text) ? text : throw new CliFailure("state", $"{label} is malformed", 3);
    }

    private static string? OptionalString(JsonObject value, string key, string label)
        => value[key] is null ? null : RequiredString(value, key, label);

    private static int RequiredInt(JsonObject value, string key, string label)
    {
        try { return value[key]?.GetValue<int>() ?? throw new FormatException(); }
        catch (Exception) { throw new CliFailure("state", $"state {label}.{key} is malformed", 3); }
    }

    private static int? OptionalInt(JsonObject value, string key, string label)
        => value[key] is null ? null : RequiredInt(value, key, label);

    private static bool RequiredBool(JsonObject value, string key, string label)
    {
        try { return value[key]?.GetValue<bool>() ?? throw new FormatException(); }
        catch (Exception) { throw new CliFailure("state", $"state {label}.{key} is malformed", 3); }
    }

    private static bool? OptionalBool(JsonObject value, string key, string label)
        => value[key] is null ? null : RequiredBool(value, key, label);

    private static void RequireExact(JsonObject value, IReadOnlyCollection<string> expected, string label)
    {
        var actual = value.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw new CliFailure("state", $"{label} fields are not exact", 3);
    }
}

internal static class ForgeStateSchema
{
    public static ForgeState CreateEmpty(string? planHash = null) => ForgeState.CreateEmpty(planHash);
    public static DispatchState CreateDispatch() => new();
    public static ReviewState CreateReview(IEnumerable<CritiqueEntry>? critiqueFiles = null) => new() { CritiqueFiles = critiqueFiles?.ToList() ?? [] };
    public static void Validate(JsonObject value) => ForgeState.FromJson(value);
}
