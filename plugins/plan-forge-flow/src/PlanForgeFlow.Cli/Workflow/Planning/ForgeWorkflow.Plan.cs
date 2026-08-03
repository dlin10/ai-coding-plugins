using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow.Planning;

internal static partial class ForgeWorkflow
{
    internal static MaterializeData MaterializePlan(CommandContext context)
    {
        var input = Console.In.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(input) > 2 * 1024 * 1024) throw new CliFailure("usage", "materialization input exceeds the size bound");
        var request = JsonSerializer.Deserialize(input, ForgeJsonContext.Default.MaterializationRequest) ?? throw new JsonException("JSON value is null");
        if (string.IsNullOrWhiteSpace(request.ReviewLog)) throw new CliFailure("usage", "materialization input reviewLog must be a non-empty string");
        if (request.CompletedReviewRounds < 0 || request.MaxRounds < 0) throw new CliFailure("usage", "materialization rounds must be non-negative integers");
        var pending = PendingPlan.Read(context.Workspace);
        var result = Materializer.Materialize(RepositoryPaths.Identify(context.Workspace), pending.Plan, request.ReviewLog, request.CompletedReviewRounds, request.MaxRounds, request.Reviewer, request.Builder, context.Args.Has("amendment"));
        PendingPlan.Delete(context.Workspace);
        return result;
    }
    
    internal static ForgeState LockPlan(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var planPath = Path.Combine(workspace, ".forge", "PLAN.md");
        if (!File.Exists(planPath)) throw new CliFailure("state", "PLAN.md is missing", 3);
        var plan = CanonicalText.NormalizePlan(File.ReadAllText(planPath));
        var state = context.RequireState();
        var phase = state.Workflow.Phase;
        if (phase is not (ForgePhase.Materialized or ForgePhase.Locked))
        {
            if (!(parsed.Has("relock") && parsed.Has("amendment") && phase is (ForgePhase.Build or ForgePhase.CodeReview)))
            {
                throw new CliFailure("state", $"plan lock is not legal in phase {phase.ToWireName()}", 3);
            }
        }
    
        var tasks = CanonicalText.ParseTasks(plan);
        var completedTasks = 0;
        if (parsed.Has("relock") && parsed.Has("amendment"))
        {
            completedTasks = Math.Max(0, state.Workflow.NextTaskNumber - 1);
            var oldTasks = state.Workflow.Tasks;
            if (completedTasks > tasks.Count) throw new CliFailure("state", "relock removes completed tasks", 3);
            for (var index = 0; index < completedTasks; index++)
            {
                var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index] : null;
                if (oldTask is null || oldTask.Hash != tasks[index].Hash) throw new CliFailure("state", $"relock changes completed task {index + 1}", 3);
            }
        }
        var head = new GitClient(workspace).Run(["rev-parse", "HEAD"]);
        if (head.ExitCode != 0) throw new CliFailure("environment", "could not establish the Git HEAD baseline");
        var untrackedPaths = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not establish the untracked plan baseline");
        return StateStore.Update(workspace, state, current =>
        {
            current.Workflow.Phase = ForgePhase.Locked;
            current.Workflow.Tasks = tasks.ToList();
            current.Workflow.TaskCount = tasks.Count;
            current.Workflow.NextTaskNumber = completedTasks + 1;
            current.Workflow.Amendment = parsed.Has("amendment");
            if (!(parsed.Has("relock") && parsed.Has("amendment")))
            {
                current.Baselines.Head = head.Stdout.Trim();
                current.Baselines.Worktree = head.Stdout.Trim();
                current.Baselines.Untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
            }
        });
    }
}
