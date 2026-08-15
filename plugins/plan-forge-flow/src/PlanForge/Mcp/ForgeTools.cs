using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Orchestration;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Mcp;

[McpServerToolType]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
internal sealed class ForgeTools
{
    private const int DefaultReviewRoundCap = 5;
    private const int DefaultCodeReviewCap = 3;

    [McpServerTool(Name = "forge.begin"), Description("Starts a run, takes a working-tree baseline, and returns the run id and capability profile.")]
    public static async Task<string> Begin(McpServer server,
                                           [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                           CancellationToken ct)
    {
        var profile = CapabilityProfileDetector.Detect(server.ClientCapabilities);
        var runId = NewRunId();
        var run = RunDirectory.Create(workspaceRoot, runId);

        var baseline = await Baseline.CaptureAsync(new GitClient(workspaceRoot), ct);
        run.WriteBaseline(baseline);
        run.WriteState(new RunState(runId, workspaceRoot, profile.ToString(), DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: DefaultReviewRoundCap, BaselineHead: baseline.Head));

        return JsonSerializer.Serialize(new BeginResult(runId, run.Path, profile.ToString(), baseline.Head),
            ForgeToolJson.Default.BeginResult);
    }

    /// <summary>
    /// One round only. The critic judges the draft; revising it and calling again is the
    /// orchestrator's job, because the revision needs the interview context.
    /// </summary>
    [McpServerTool(Name = "forge.plan.review"), Description("Runs one round of plan review and returns the critique.")]
    public static async Task<string> ReviewPlan(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The current plan draft, as markdown.")] string planDraft,
        [Description("Model for the critic.")] string model,
        [Description("Optional effort level.")] string? effort,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var act = new PlanReview(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary());
        var critique = await act.ReviewAsync(run, planDraft, new Selection(model, effort), ct);

        return JsonSerializer.Serialize(critique, ContractJson.Default.Critique);
    }

    /// <summary>
    /// The only approval route. It records a decision the orchestrator collected through the host's
    /// own UI, rather than asking through MCP elicitation, because elicitation could not tell a user
    /// saying no from a host that answered on their behalf without rendering anything. Nothing here
    /// is enforced — see docs/adr/0003.
    /// </summary>
    [McpServerTool(Name = "forge.plan.confirm"), Description("Records the user's decision on the plan, and records the approved tasks when it is yes.")]
    public static async Task<string> ConfirmPlan(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The plan to approve, as markdown.")] string plan,
        [Description("What the user answered. Show them the plan and the drift, ask, and pass what they say; never decide this yourself.")] bool approved,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var state = run.ReadState();
        var tasks = PlanTasks.Parse(plan);

        var drifted = await run.ReadBaseline(state.BaselineHead)
                               .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

        if (!approved) return Serialized(new ApproveResult(false, 0, drifted));

        run.WritePlan(plan);
        run.WriteState(state with { Approved = true });

        return Serialized(new ApproveResult(true, tasks.Count, drifted));
    }

    [McpServerTool(Name = "forge.build.next"), Description("Builds the next unfinished task of the approved plan.")]
    public static async Task<string> BuildNext(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Model for the builder.")] string model,
        [Description("Optional effort level.")] string? effort,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var act = new Build(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary());
        var outcome = await act.NextAsync(run, new Selection(model, effort), ct);

        return JsonSerializer.Serialize(outcome, ForgeToolJson.Default.BuildOutcome);
    }

    /// <summary>
    /// Runs the entire critic-to-builder loop; the orchestrator does not take a turn between rounds.
    /// </summary>
    [McpServerTool(Name = "forge.review.code"), Description("Reviews the working diff and has the builder fix findings until the verdict settles.")]
    public static async Task<string> ReviewCode(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Model for the critic.")] string criticModel,
        [Description("Model for the builder.")] string builderModel,
        [Description("Optional effort level for the critic.")] string? criticEffort,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var act = new CodeReview(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary(),
            new GitClient(workspaceRoot));

        var outcome = await act.RunAsync(run, new Selection(criticModel, criticEffort),
            new Selection(builderModel, null), DefaultCodeReviewCap, ct);

        return JsonSerializer.Serialize(outcome, ForgeToolJson.Default.CodeReviewOutcome);
    }

    [McpServerTool(Name = "forge.status"), Description("Reports where the run stands, with any working-tree drift since the baseline.")]
    public static async Task<string> Status(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var state = run.ReadState();
        var drifted = await run.ReadBaseline(state.BaselineHead)
                               .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

        return JsonSerializer.Serialize(new StatusResult(state, drifted), ForgeToolJson.Default.StatusResult);
    }

    private static string Serialized(ApproveResult result) =>
        JsonSerializer.Serialize(result, ForgeToolJson.Default.ApproveResult);

    // Sortable and collision-free enough for a per-workspace run folder.
    private static string NewRunId() =>
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}";
}

internal sealed record BeginResult(string RunId, string RunPath, string Profile, string BaselineHead);

internal sealed record ApproveResult(bool Approved, int TaskCount, IReadOnlyList<string> DriftedFiles);

/// <summary>
/// Drift travels with the status rather than only with the decision, because the orchestrator has
/// to show it to the user <em>before</em> asking, and the decision call is where it would arrive
/// too late to matter.
/// </summary>
internal sealed record StatusResult(RunState Run, IReadOnlyList<string> DriftedFiles);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BeginResult))]
[JsonSerializable(typeof(ApproveResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(BuildOutcome))]
[JsonSerializable(typeof(CodeReviewOutcome))]
internal sealed partial class ForgeToolJson : JsonSerializerContext;
