using System.Globalization;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Cursor;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow;

internal static partial class ForgeWorkflow
{
    internal static ForgeState BeginBuild(CommandContext context)
    {
        var workspace = context.Workspace;
        var repository = RepositoryPaths.Identify(workspace);
        using var repositoryLock = RepositoryRunLock.Acquire(repository, context.Host);
        return BeginBuild(context, repositoryLock);
    }

    internal static ForgeState BeginBuild(CommandContext context, RepositoryRunLock repositoryLock)
    {
        var original = context.RequireState();
        var prepared = PrepareBuild(context, repositoryLock);
        return StateStore.Update(context.Workspace, original, current =>
        {
            current.Workflow.Phase = prepared.Workflow.Phase;
            current.Baselines.Head = prepared.Baselines.Head;
            current.Baselines.Worktree = prepared.Baselines.Worktree;
            current.Baselines.Untracked = prepared.Baselines.Untracked;
        });
    }

    internal static ForgeState PrepareBuild(CommandContext context, RepositoryRunLock repositoryLock)
    {
        var workspace = context.Workspace;
        var repository = RepositoryPaths.Identify(workspace);
        repositoryLock.Require(repository, context.Host);
        var parsed = context.Args;
        var state = context.RequireState().DeepCopy();
        var phase = state.Workflow.Phase;
        if (phase != ForgePhase.Locked) throw new CliFailure("state", $"build begin requires a locked plan (current phase: {phase.ToWireName()})", 3);
        if (state.Dispatch.Pending) throw new CliFailure("state", "a dispatch is already pending", 3);
        var scope = state.RepositoryScopeId;
        if (string.IsNullOrWhiteSpace(scope)) throw new CliFailure("unsupported-state-schema", "Forge state repositoryScopeId is missing", 3);
        var ownerRef = $"refs/plan-forge/{scope}/owner";
        var headRef = $"refs/plan-forge/{scope}/head-base";
        var worktreeRef = $"refs/plan-forge/{scope}/worktree-base";
        var reuseBaseline = parsed.Has("amendment") || parsed.Has("relock");
        var existingRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in new[] { ownerRef, headRef, worktreeRef })
        {
            var existing = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (existing.ExitCode == 0 && !string.IsNullOrWhiteSpace(existing.Stdout)) existingRefs[reference] = existing.Stdout.Trim();
        }
        var refsExist = existingRefs.Count == 3;
        var cursorRecovery = context.Host == HostKind.Cursor && existingRefs.Count > 0;
        if (cursorRecovery && (!existingRefs.TryGetValue(ownerRef, out var owner) || owner != state.Baselines.Head || existingRefs.TryGetValue(headRef, out var existingHead) && existingHead != owner)) throw new CliFailure("state", "Cursor baseline refs conflict with the materialization transaction", 3);
        if (refsExist && !reuseBaseline && !cursorRecovery) throw new CliFailure("state", "Git baseline refs already exist; cleanup the previous run or use amendment relock", 3);

        var head = existingRefs.TryGetValue(headRef, out var pinnedHead)
                       ? new ProcessResult(0, pinnedHead, string.Empty)
                       : new GitClient(workspace).Run(["rev-parse", "HEAD"]);
        if (head.ExitCode != 0 || string.IsNullOrWhiteSpace(head.Stdout)) throw new CliFailure("environment", "could not establish the build HEAD baseline");
        var worktree = head.Stdout.Trim();
        if (existingRefs.TryGetValue(worktreeRef, out var pinnedWorktree))
        {
            worktree = pinnedWorktree;
        }
        else
        {
            var status = new GitClient(workspace).Run(["status", "--porcelain"]);
            if (status.ExitCode != 0) throw new CliFailure("environment", "could not establish the build worktree baseline");
            var trackedDirty = status.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("??", StringComparison.Ordinal));
            if (trackedDirty)
            {
                var stash = new GitClient(workspace).Run(["stash", "create", "plan-forge-flow baseline"]);
                if (stash.ExitCode == 0 && !string.IsNullOrWhiteSpace(stash.Stdout)) worktree = stash.Stdout.Trim();
            }
        }
        if (!refsExist)
        {
            foreach (var pair in new[] { (ownerRef, head.Stdout.Trim(), "baseline-owner-written"), (headRef, head.Stdout.Trim(), "baseline-head-written"), (worktreeRef, worktree, "baseline-worktree-written") })
            {
                if (existingRefs.TryGetValue(pair.Item1, out var existingValue))
                {
                    if (existingValue != pair.Item2) throw new CliFailure("state", $"baseline ref conflicts with the materialization transaction: {pair.Item1}", 3);
                    continue;
                }
                var update = new GitClient(workspace).Run(["update-ref", pair.Item1, pair.Item2]);
                if (update.ExitCode != 0) throw new CliFailure("environment", $"could not pin Git baseline ref {pair.Item1}: {update.Stderr.Trim()}");
                if (context.Host == HostKind.Cursor) PendingRuns.Fault(pair.Item3);
            }
        }

        var untracked = cursorRecovery || refsExist ? state.Baselines.Untracked.ToList() : [];
        if (!cursorRecovery && !refsExist)
        {
            var untrackedPaths = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not record the untracked baseline");
            untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
        }

        state.Workflow.Phase = ForgePhase.Build;
        state.Baselines.Head = head.Stdout.Trim();
        state.Baselines.Worktree = worktree;
        state.Baselines.Untracked = untracked;
        return state;
    }

    internal static InstallAgentsData InstallAgents()
    {
        var target = RepositoryPaths.AgentsDirectory();
        OwnershipGuards.EnsureDirectory(target);
        var installed = new List<string>();
        foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
        {
            var source = FindPluginFile(Path.Combine("agents", name));
            var content = source is not null ? File.ReadAllText(source) : $"# plan-forge-flow generated agent\nname = \"{Path.GetFileNameWithoutExtension(name)}\"\n";
            var destination = Path.Combine(target, name);
            if (File.Exists(destination)) OwnershipGuards.EnsureOwnedAgentFile(destination);
            DurableFiles.WriteAtomic(destination, content);
            installed.Add(destination);
        }

        return new InstallAgentsData(true, installed);
    }

    internal static ForgeState ResolveBuild(CommandContext context)
    {
        var state = context.RequireState();
        if (!state.Dispatch.Pending || state.Dispatch.Stage != DispatchStage.Build) throw new CliFailure("state", "resolve requires a pending build dispatch", 3);
        if (string.IsNullOrWhiteSpace(context.Args.Get("conflict"))) throw new CliFailure("usage", "build resolve requires --conflict");
        return StateStore.Update(context.Workspace, state, current =>
        {
            current.Dispatch.Pending = false;
            current.Dispatch.Conflict = context.Args.Get("conflict");
        });
    }

    internal static DispatchState Dispatch(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var state = context.RequireState();
        var stage = DispatchStages.Parse(parsed.GetRequired("stage"));
        var taskText = parsed.Get("task-number");
        var taskNumber = 0;
        if (stage == DispatchStage.Build && !int.TryParse(taskText, NumberStyles.None, CultureInfo.InvariantCulture, out taskNumber)) throw new CliFailure("usage", "build dispatch requires numeric --task-number");
        var pinnedSelection = ResolvePinnedSelection(state, stage, parsed);
        var definition = stage.Definition();
        if (state.Workflow.Phase != definition.ExpectedPhase) throw new CliFailure("state", $"{stage.ToWireName()} dispatch requires phase {definition.ExpectedPhase.ToWireName()} (current: {state.Workflow.Phase.ToWireName()})", 3);
        if (parsed.Has("cancel"))
        {
            if (!state.Dispatch.Pending) throw new CliFailure("state", "cannot cancel without a pending dispatch", 3);
            if (parsed.Get("dispatch-id") is { } cancelId && !string.Equals(cancelId, state.Dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "cancel dispatch-id does not match the pending dispatch", 3);
            return StateStore.Update(workspace, state, value => value.Dispatch.Pending = false).Dispatch;
        }

        if (parsed.Has("retry"))
        {
            if (!state.Dispatch.Pending) throw new CliFailure("state", "cannot retry without a pending dispatch", 3);
            if (parsed.Get("dispatch-id") is { } retryId && !string.Equals(retryId, state.Dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "retry dispatch-id does not match the pending dispatch", 3);
            var retries = state.Dispatch.Retry;
            var cap = state.Workflow.MaxBuildRetries;
            if (retries >= cap)
            {
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "dispatch retry cap reached; require --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
            }

            state = StateStore.Update(workspace, state, value =>
            {
                value.Dispatch.Retry = retries + 1;
                if (context.Host != HostKind.Cursor) return;
                value.Dispatch.Id = "dispatch-" + Hashing.Nonce()[..16];
                if (stage.Definition().Role == ForgeRole.Builder)
                {
                    value.Agents.BuilderId = null;
                    value.Agents.LastBuilderDispatchId = null;
                }
                else
                {
                    value.Agents.LastReviewerId = null;
                    value.Agents.LastReviewerDispatchId = null;
                }
            });
            return state.Dispatch;
        }

        if (state.Dispatch.Pending) throw new CliFailure("state", "a dispatch is already pending; consume it, cancel it, or retry it", 3);
        if (stage is DispatchStage.FixBuild or DispatchStage.FixReview && state.Review.FixRound >= state.Workflow.MaxFixRounds) throw new CliFailure("state", "fix retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 3);
        if (stage == DispatchStage.Build)
        {
            var scope = state.RepositoryScopeId;
            if (string.IsNullOrWhiteSpace(scope)) throw new CliFailure("unsupported-state-schema", "Forge state repositoryScopeId is missing", 3);
            foreach (var reference in new[] { $"refs/plan-forge/{scope}/head-base", $"refs/plan-forge/{scope}/worktree-base" })
            {
                var baseline = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
                if (baseline.ExitCode != 0) throw new CliFailure("state", $"build dispatch requires the pinned baseline ref {reference}", 3);
            }
            var next = state.Workflow.NextTaskNumber;
            if (taskNumber != next) throw new CliFailure("state", $"the next locked task is {next}", 3);
            var total = state.Workflow.TaskCount ?? 0;
            if (taskNumber > total) throw new CliFailure("state", "task number is outside the locked plan", 3);
        }

        if (definition.RequiresFreshReview) ForgeReview.RequireFreshReviewManifest(workspace, state);

        var dispatchId = "dispatch-" + Hashing.Nonce()[..16];
        state = StateStore.Update(workspace, state, value =>
        {
            value.Dispatch.Id = dispatchId;
            value.Dispatch.Stage = stage;
            value.Dispatch.TaskNumber = stage == DispatchStage.Build ? taskNumber : null;
            value.Dispatch.Retry = 0;
            value.Dispatch.Pending = true;
            value.Dispatch.Model = pinnedSelection.Model;
            value.Dispatch.Effort = pinnedSelection.Effort;
            value.Dispatch.Conflict = null;
        });
        return state.Dispatch;
    }

    private static PinnedSelection ResolvePinnedSelection(ForgeState state, DispatchStage stage, ParsedArgs parsed)
    {
        var definition = stage.Definition();
        var selection = state.Models.For(definition.Role);
        var role = definition.Role.ToString().ToLowerInvariant();
        if (selection is null) throw new CliFailure("state", $"{stage.ToWireName()} dispatch requires a pinned {role} selection", 3);
        if (string.IsNullOrWhiteSpace(selection.Model) || string.IsNullOrWhiteSpace(selection.Effort) || selection.Effort == "ultra") throw new CliFailure("state", $"the pinned {role} selection is invalid", 3);

        ValidateObservedSelection(parsed, selection.Model, selection.Effort);
        return selection;
    }

    internal static DispatchState Complete(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var state = context.RequireState();
        var dispatch = state.Dispatch;
        if (!dispatch.Pending) throw new CliFailure("state", "complete requires a pending dispatch", 3);
        var stage = dispatch.Stage;
        if (stage is not (DispatchStage.Build or DispatchStage.FixBuild)) throw new CliFailure("state", "complete applies only to build or fix-build dispatches", 3);
        if (parsed.Get("dispatch-id") is { } suppliedDispatchId && !string.Equals(suppliedDispatchId, dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "complete dispatch-id does not match the pending dispatch", 3);
        if (string.IsNullOrWhiteSpace(state.Agents.BuilderId)) throw new CliFailure("state", "complete requires a registered builder session", 3);
        if (!string.Equals(state.Agents.LastBuilderDispatchId, dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "complete requires a builder session for the current dispatch", 3);
        var taskNumber = 0;
        if (stage == DispatchStage.Build && !int.TryParse(parsed.Get("task-number"), NumberStyles.None, CultureInfo.InvariantCulture, out taskNumber)) throw new CliFailure("usage", "complete requires numeric --task-number");
        if (stage == DispatchStage.Build && taskNumber != dispatch.TaskNumber) throw new CliFailure("state", "complete task does not match the pending dispatch", 3);
        var passed = !string.Equals(parsed.Get("verification-passed"), "false", StringComparison.OrdinalIgnoreCase);
        if (!passed && !parsed.Has("accept-risk")) throw new CliFailure("verdict", "verification failed; complete requires --accept-risk with --authorization-note", 2);
        if (!passed) RequireAuthorizationNote(parsed);
        state = StateStore.Update(workspace, state, value =>
        {
            value.Dispatch.Pending = false;
            value.Dispatch.LastVerificationPassed = passed;
            if (stage == DispatchStage.Build)
            {
                value.Workflow.NextTaskNumber = taskNumber + 1;
                if (taskNumber >= value.Workflow.TaskCount) value.Workflow.Phase = ForgePhase.CodeReview;
            }
        });
        return state.Dispatch;
    }
}
