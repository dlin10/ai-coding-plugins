using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Cursor;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow.Planning;

internal static class ForgeWorkflow
{
    internal static MaterializeData MaterializePlan(CommandContext context)
    {
        var input = JsonInput.Read(Console.In);
        if (Encoding.UTF8.GetByteCount(input) > 2 * 1024 * 1024) throw new CliFailure("usage", "materialization input exceeds the size bound");
        MaterializationRequest request;
        try
        {
            request = JsonSerializer.Deserialize(input, ForgeJsonContext.Default.MaterializationRequest) ?? throw new JsonException("JSON value is null");
        }
        catch (JsonException)
        {
            throw new CliFailure(
                "usage",
                "materialization input must be JSON with reviewLog as a string, completedReviewRounds and maxRounds as integers, and reviewer and builder as model/effort objects");
        }
        if (string.IsNullOrWhiteSpace(request.ReviewLog)) throw new CliFailure("usage", "materialization input reviewLog must be a non-empty string");
        if (request.CompletedReviewRounds < 0 || request.MaxRounds < 0) throw new CliFailure("usage", "materialization rounds must be non-negative integers");
        var pending = PendingPlan.Read(context.Workspace);
        var repository = RepositoryPaths.Identify(context.Workspace);
        var content = new MaterializationContent(
            pending.Plan, request.ReviewLog, request.CompletedReviewRounds, request.MaxRounds, request.Reviewer, request.Builder);
        var result = context.Args.Has("amendment")
                         ? Materializer.MaterializeCodexAmendment(repository, content)
                         : Materializer.MaterializeCodexFresh(repository, content);
        PendingPlan.Delete(context.Workspace);
        return result;
    }

    internal static MaterializeData MaterializeCursorPlan(CommandContext context)
    {
        if (context.Args.Has("amendment")) throw new CliFailure("usage", "Cursor amendments are unsupported");
        var repository = RepositoryPaths.Identify(context.Workspace);
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = PendingRuns.BeginMaterialize(repository, context.Args.GetRequired("run-id"), repositoryLock);
        if (run.ReviewerWaiver is null || run.BuilderWaiver is null)
            throw new CliFailure("state", "Cursor model waivers are missing", 3);
        ForgeState completed;
        if (run.Phase == PendingRunPhase.Consumed)
        {
            PendingRuns.VerifyMaterialization(repository, run);
            completed = StateStore.Load(context.Workspace);
        }
        else
        {
            var forgeDirectory = Path.Combine(context.Workspace, ".forge");
            if (Directory.Exists(forgeDirectory))
            {
                PendingRuns.VerifyMaterialization(repository, run);
                completed = StateStore.Load(context.Workspace);
            }
            else
            {
                var transaction = run.Materialization ?? throw new CliFailure("state", "Cursor materialization transaction is missing", 3);
                var plan = transaction.PlanText ?? throw new CliFailure("state", "Cursor materialization execution snapshot is missing", 3);
                var reviewLog = string.Join("\n", run.Responses.Select(response => response.Response));
                Materializer.EnsureManagedExclude(repository, repositoryLock);
                var state = CreateCursorMaterializationState(repository, run, transaction);
                state = PreparePlanLock(new CommandContext("plan lock", context.Workspace, ParsedArgs.Parse([]), state, HostKind.Cursor), plan);
                state = Workflow.ForgeWorkflow.PrepareBuild(
                    new CommandContext("build begin", context.Workspace, ParsedArgs.Parse([]), state, HostKind.Cursor), repositoryLock);
                state.UpdatedAt = transaction.Timestamp;
                run = PendingRuns.BindMaterializationState(repository, run.RunId, state, repositoryLock);
                PendingRuns.Fault("build-state-bound");
                transaction = run.Materialization!;
                var content = new MaterializationContent(
                    plan,
                    reviewLog,
                    run.ReviewRound,
                    run.ReviewCap,
                    new ModelSelection(transaction.ReviewerModel, transaction.ReviewerEffort),
                    new ModelSelection(transaction.BuilderModel, transaction.BuilderEffort));
                Materializer.MaterializeCursorTransaction(repository, repositoryLock, transaction, content, state);
                PendingRuns.Fault("forge-moved");
                PendingRuns.VerifyMaterialization(repository, run);
                completed = StateStore.Load(context.Workspace);
            }
        }
        if (run.Phase != PendingRunPhase.Consumed) PendingRuns.Consume(repository, run.RunId, repositoryLock);
        return new MaterializeData("forge-materialized", completed.Workflow.Phase.ToWireName(), completed.Models.Reviewer!, completed.Models.Builder!);
    }
    
    private static ForgeState CreateCursorMaterializationState(RepositoryIdentity repository, PendingRun run, MaterializationTransaction transaction)
    {
        var state = StateStore.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.CreatedAt = transaction.Timestamp; state.UpdatedAt = transaction.Timestamp; state.Workflow.Phase = ForgePhase.Materialized; state.Workflow.Round = run.ReviewRound; state.Workflow.MaxRounds = run.ReviewCap; state.Models.Reviewer = new PinnedSelection(transaction.ReviewerModel, transaction.ReviewerEffort); state.Models.Builder = new PinnedSelection(transaction.BuilderModel, transaction.BuilderEffort); state.SourceRun = new RunIdentity("cursor:" + run.RunId, transaction.Id); state.ModelWaiverAudit = [new CursorModelWaiverAudit(run.ReviewerWaiver!.Role, run.ReviewerWaiver.Model, run.ReviewerWaiver.Effort, run.ReviewerWaiver.CursorVersion, run.ReviewerWaiver.Observed, run.ReviewerWaiver.Consent, run.ReviewerWaiver.Timestamp, run.ReviewerWaiver.ModelGuarantee), new CursorModelWaiverAudit(run.BuilderWaiver!.Role, run.BuilderWaiver.Model, run.BuilderWaiver.Effort, run.BuilderWaiver.CursorVersion, run.BuilderWaiver.Observed, run.BuilderWaiver.Consent, run.BuilderWaiver.Timestamp, run.BuilderWaiver.ModelGuarantee)]; state.ReviewerGuarantee = new ReviewerGuarantee(run.ReviewerGuarantee); state.ApprovalGuarantee = new ApprovalGuarantee(run.ApprovalGuarantee); return state;
    }

    internal static ForgeState LockPlan(CommandContext context)
    {
        var workspace = context.Workspace;
        var planPath = Path.Combine(workspace, ".forge", "PLAN.md");
        if (!File.Exists(planPath)) throw new CliFailure("state", "PLAN.md is missing", 3);
        var plan = CanonicalText.NormalizePlan(File.ReadAllText(planPath));
        var original = context.RequireState();
        var prepared = PreparePlanLock(context, plan);
        return StateStore.Update(workspace, original, current =>
        {
            current.Workflow.Phase = prepared.Workflow.Phase;
            current.Workflow.Tasks = prepared.Workflow.Tasks;
            current.Workflow.TaskCount = prepared.Workflow.TaskCount;
            current.Workflow.NextTaskNumber = prepared.Workflow.NextTaskNumber;
            current.Workflow.Amendment = prepared.Workflow.Amendment;
            current.Baselines.Head = prepared.Baselines.Head;
            current.Baselines.Worktree = prepared.Baselines.Worktree;
            current.Baselines.Untracked = prepared.Baselines.Untracked;
        });
    }

    private static ForgeState PreparePlanLock(CommandContext context, string planText)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var plan = CanonicalText.NormalizePlan(planText);
        var state = context.RequireState().DeepCopy();
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
        state.Workflow.Phase = ForgePhase.Locked;
        state.Workflow.Tasks = tasks.ToList();
        state.Workflow.TaskCount = tasks.Count;
        state.Workflow.NextTaskNumber = completedTasks + 1;
        state.Workflow.Amendment = parsed.Has("amendment");
        if (!(parsed.Has("relock") && parsed.Has("amendment")))
        {
            state.Baselines.Head = head.Stdout.Trim();
            state.Baselines.Worktree = head.Stdout.Trim();
            state.Baselines.Untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
        }
        return state;
    }
}
