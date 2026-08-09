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

internal static partial class ForgeWorkflow
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
        var result = Materializer.Materialize(RepositoryPaths.Identify(context.Workspace), pending.Plan, request.ReviewLog, request.CompletedReviewRounds, request.MaxRounds, request.Reviewer, request.Builder, context.Args.Has("amendment"), context.Host);
        PendingPlan.Delete(context.Workspace);
        return result;
    }

    internal static MaterializeData MaterializeCursorPlan(CommandContext context)
    {
        if (context.Args.Has("amendment")) throw new CliFailure("usage", "Cursor amendments are unsupported");
        var repository = RepositoryPaths.Identify(context.Workspace);
        var run = PendingRuns.BeginMaterialize(repository, context.Args.GetRequired("run-id"));
        if (run.ReviewerWaiver is null || run.BuilderWaiver is null) throw new CliFailure("state", "Cursor model waivers are missing", 3);
        ForgeState completed;
        using (RepositoryRunLock.Acquire(repository, HostKind.Cursor))
        {
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
                    var statePath = StateStore.StatePath(context.Workspace);
                    if (!File.Exists(statePath) || StateStore.Load(context.Workspace).Host != HostKind.Cursor) throw new CliFailure("state", "refusing to replace unowned .forge artifacts", 3);
                    var state = StateStore.Load(context.Workspace);
                    if (!File.Exists(Path.Combine(forgeDirectory, "PLAN.md")) || state.Models.Reviewer is null || state.Models.Builder is null) throw new CliFailure("state", "Cursor materialization artifacts conflict with the pending transaction", 3);
                    completed = CompleteCursorMaterialization(context, repository, run, repositoryLockHeld: true);
                }
                else
                {
                    var transaction = run.Materialization ?? throw new CliFailure("state", "Cursor materialization transaction is missing", 3);
                    var reviewLog = string.Join("\n", run.Responses.Select(response => response.Response)); var plannedState = CreateCursorMaterializationState(repository, run, transaction);
                    run = PendingRuns.JournalMaterializationSuccessor(repository, run.RunId, plannedState, repositoryLockHeld: true);
                    PendingRuns.Fault("materialized-successor-journaled");
                    var plan = transaction.PlanText ?? throw new CliFailure("state", "Cursor materialization execution snapshot is missing", 3);
                    Materializer.Materialize(repository, plan, reviewLog, run.ReviewRound, run.ReviewCap, new ModelSelection(transaction.ReviewerModel, transaction.ReviewerEffort), new ModelSelection(transaction.BuilderModel, transaction.BuilderEffort), false, HostKind.Cursor, materializationTransactionId: run.TransactionId, repositoryLockHeld: true, materializationState: plannedState);
                    PendingRuns.Fault("forge-moved-before-reconcile");
                    run = PendingRuns.ReconcileMaterializationState(repository, run.RunId, repositoryLockHeld: true);
                    PendingRuns.Fault("forge-artifacts-written");
                    completed = CompleteCursorMaterialization(context, repository, run, repositoryLockHeld: true);
                }
            }
            if (run.Phase != PendingRunPhase.Consumed) PendingRuns.Consume(repository, run.RunId, repositoryLockHeld: true);
        }
        return new MaterializeData("forge-materialized", completed.Workflow.Phase.ToWireName(), completed.Models.Reviewer!, completed.Models.Builder!);
    }

    private static ForgeState CompleteCursorMaterialization(CommandContext context, RepositoryIdentity repository, PendingRun run, bool repositoryLockHeld = false)
    {
        run = PendingRuns.ReconcileMaterializationState(repository, run.RunId, repositoryLockHeld);
        PendingRuns.VerifyMaterialization(repository, run, requireComplete: false);
        Materializer.VerifyManagedExclude(repository);
        var state = StateStore.Load(context.Workspace);
        if (state.Workflow.Phase == ForgePhase.Materialized)
        {
            state = LockPlan(new CommandContext("plan lock", context.Workspace, ParsedArgs.Parse([]), state, HostKind.Cursor), successor => { PendingRuns.JournalMaterializationSuccessor(repository, run.RunId, successor, repositoryLockHeld); PendingRuns.Fault("locked-successor-journaled"); });
            PendingRuns.Fault("locked-state-written");
            run = PendingRuns.ReconcileMaterializationState(repository, run.RunId, repositoryLockHeld);
        }
        if (state.Workflow.Phase == ForgePhase.Locked)
        {
            state = Workflow.ForgeWorkflow.BeginBuild(new CommandContext("build begin", context.Workspace, ParsedArgs.Parse([]), state, HostKind.Cursor), repositoryLockHeld, successor => { PendingRuns.JournalMaterializationSuccessor(repository, run.RunId, successor, repositoryLockHeld); PendingRuns.Fault("build-successor-journaled"); });
            PendingRuns.Fault("build-state-written");
            run = PendingRuns.ReconcileMaterializationState(repository, run.RunId, repositoryLockHeld);
        }
        if (state.Workflow.Phase != ForgePhase.Build) throw new CliFailure("state", "Cursor materialization is in an unsupported partial phase", 3);
        run = PendingRuns.Load(repository, run.RunId);
        PendingRuns.VerifyMaterialization(repository, run);
        return state;
    }
    
    private static ForgeState CreateCursorMaterializationState(RepositoryIdentity repository, PendingRun run, MaterializationTransaction transaction)
    {
        var state = StateStore.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.CreatedAt = transaction.Timestamp; state.UpdatedAt = transaction.Timestamp; state.Workflow.Phase = ForgePhase.Materialized; state.Workflow.Round = run.ReviewRound; state.Workflow.MaxRounds = run.ReviewCap; state.Models.Reviewer = new PinnedSelection(transaction.ReviewerModel, transaction.ReviewerEffort); state.Models.Builder = new PinnedSelection(transaction.BuilderModel, transaction.BuilderEffort); state.SourceRun = new RunIdentity("cursor:" + run.RunId, transaction.Id); state.ModelWaiver = new ModelWaiver("cursor", run.ReviewerWaiver!.Consent + " | " + run.BuilderWaiver!.Consent); state.ModelWaiverAudit = [new CursorModelWaiverAudit(run.ReviewerWaiver.Role, run.ReviewerWaiver.Model, run.ReviewerWaiver.Effort, run.ReviewerWaiver.CursorVersion, run.ReviewerWaiver.Observed, run.ReviewerWaiver.Consent, run.ReviewerWaiver.Timestamp, run.ReviewerWaiver.ModelGuarantee), new CursorModelWaiverAudit(run.BuilderWaiver.Role, run.BuilderWaiver.Model, run.BuilderWaiver.Effort, run.BuilderWaiver.CursorVersion, run.BuilderWaiver.Observed, run.BuilderWaiver.Consent, run.BuilderWaiver.Timestamp, run.BuilderWaiver.ModelGuarantee)]; state.ReviewerGuarantee = new ReviewerGuarantee(run.ReviewerGuarantee); state.ApprovalGuarantee = new ApprovalGuarantee(run.ApprovalGuarantee); return state;
    }

    internal static ForgeState LockPlan(CommandContext context, Action<ForgeState>? beforePersist = null)
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
        }, beforePersist);
    }
}
