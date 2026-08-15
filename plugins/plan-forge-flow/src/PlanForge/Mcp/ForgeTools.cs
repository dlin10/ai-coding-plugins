using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
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
    private const string ApprovalKey = "approve";

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
    /// Asks the user to approve, and shows any working-tree drift beside the plan. The client
    /// re-sends this call carrying the answer, so the elicitation branch runs exactly twice.
    /// </summary>
    [McpServerTool(Name = "forge.plan.approve"), Description("Presents the plan for approval and records the approved tasks.")]
    public static async Task<string> ApprovePlan(
        RequestContext<CallToolRequestParams> context,
        McpServer server,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The plan to approve, as markdown.")] string plan,
        CancellationToken ct)
    {
        var run = RunDirectory.Open(workspaceRoot, runId);
        var state = run.ReadState();
        var tasks = PlanTasks.Parse(plan);

        if (context.Params?.InputResponses?.TryGetValue(ApprovalKey, out var answer) is true)
        {
            if (!WasApproved(answer))
                return JsonSerializer.Serialize(new ApproveResult(false, 0, []), ForgeToolJson.Default.ApproveResult);

            run.WritePlan(plan);
            run.WriteState(state with { Approved = true });
            return JsonSerializer.Serialize(new ApproveResult(true, tasks.Count, []), ForgeToolJson.Default.ApproveResult);
        }

        var drifted = await run.ReadBaseline(state.BaselineHead)
                               .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

        var orchestrator = new NegotiatedOrchestrator(server.ClientCapabilities);
        if (!orchestrator.CanElicitApproval)
            throw new VendorException("this host cannot be asked for approval in-band");

        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                [ApprovalKey] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Message = orchestrator.Present(plan, tasks.Count, drifted).Body,
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            [ApprovalKey] = new ElicitRequestParams.BooleanSchema
                            {
                                Description = "Approve this plan and start building?"
                            }
                        }
                    }
                })
            },
            requestState: runId);
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

    [McpServerTool(Name = "forge.status"), Description("Reports where the run stands.")]
    public static string Status(
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId)
    {
        var state = RunDirectory.Open(workspaceRoot, runId).ReadState();
        return JsonSerializer.Serialize(state, ForgeJson.Default.RunState);
    }

    private static bool WasApproved(InputResponse answer)
    {
        var raw = answer.RawValue;
        if (raw.ValueKind is not JsonValueKind.Object) return false;
        if (!raw.TryGetProperty("content", out var content)) return false;

        return content.TryGetProperty(ApprovalKey, out var flag)
            && flag.ValueKind is JsonValueKind.True;
    }

    // Sortable and collision-free enough for a per-workspace run folder.
    private static string NewRunId() =>
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}";
}

internal sealed record BeginResult(string RunId, string RunPath, string Profile, string BaselineHead);

internal sealed record ApproveResult(bool Approved, int TaskCount, IReadOnlyList<string> DriftedFiles);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BeginResult))]
[JsonSerializable(typeof(ApproveResult))]
[JsonSerializable(typeof(BuildOutcome))]
[JsonSerializable(typeof(CodeReviewOutcome))]
internal sealed partial class ForgeToolJson : JsonSerializerContext;
