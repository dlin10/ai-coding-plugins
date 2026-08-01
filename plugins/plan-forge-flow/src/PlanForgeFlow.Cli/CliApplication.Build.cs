using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static JsonObject BeginBuild(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var state = context.RequireState();
        var phase = state.Workflow.Phase;
        if (phase != ForgePhase.Locked) throw new CliFailure("state", $"build begin requires a locked plan (current phase: {phase.ToWireName()})", 3);
        if (state.Dispatch.Pending) throw new CliFailure("state", "a dispatch is already pending", 3);
        var headRef = "refs/plan-forge/head-base";
        var worktreeRef = "refs/plan-forge/worktree-base";
        var reuseBaseline = parsed.Has("amendment") || parsed.Has("relock");
        var refsExist = true;
        foreach (var reference in new[] { headRef, worktreeRef })
        {
            var existing = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (existing.ExitCode != 0 || string.IsNullOrWhiteSpace(existing.Stdout)) refsExist = false;
        }
        if (refsExist && !reuseBaseline) throw new CliFailure("state", "Git baseline refs already exist; cleanup the previous run or use amendment relock", 3);

        var head = refsExist
                       ? new GitClient(workspace).Run(["rev-parse", headRef])
                       : new GitClient(workspace).Run(["rev-parse", "HEAD"]);
        if (head.ExitCode != 0 || string.IsNullOrWhiteSpace(head.Stdout)) throw new CliFailure("environment", "could not establish the build HEAD baseline");
        var worktree = head.Stdout.Trim();
        if (refsExist)
        {
            var existingWorktree = new GitClient(workspace).Run(["rev-parse", worktreeRef]);
            if (existingWorktree.ExitCode != 0 || string.IsNullOrWhiteSpace(existingWorktree.Stdout)) throw new CliFailure("state", "the retained worktree baseline ref is invalid", 3);
            worktree = existingWorktree.Stdout.Trim();
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
            foreach (var pair in new[] { (headRef, head.Stdout.Trim()), (worktreeRef, worktree) })
            {
                var update = new GitClient(workspace).Run(["update-ref", pair.Item1, pair.Item2]);
                if (update.ExitCode != 0) throw new CliFailure("environment", $"could not pin Git baseline ref {pair.Item1}: {update.Stderr.Trim()}");
            }
        }

        var untracked = refsExist ? state.Baselines.Untracked.ToList() : [];
        if (!refsExist)
        {
            var untrackedPaths = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not record the untracked baseline");
            untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
        }

        return StateStore.Update(workspace, state, current =>
        {
            current.Workflow.Phase = ForgePhase.Build;
            current.Baselines.Head = head.Stdout.Trim();
            current.Baselines.Worktree = worktree;
            current.Baselines.Untracked = untracked;
        }).ToJson();
    }

    private static JsonObject InstallAgents()
    {
        var target = RepositoryPaths.AgentsDirectory();
        OwnershipGuards.EnsureDirectory(target);
        var installed = new JsonArray();
        foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
        {
            var source = FindPluginFile(Path.Combine("agents", name));
            var content = source is not null ? File.ReadAllText(source) : $"# plan-forge-flow generated agent\nname = \"{Path.GetFileNameWithoutExtension(name)}\"\n";
            var destination = Path.Combine(target, name);
            if (File.Exists(destination)) OwnershipGuards.EnsureOwnedAgentFile(destination);
            DurableFiles.WriteAtomic(destination, content);
            installed.Add((JsonNode)JsonValue.Create(destination));
        }

        return new JsonObject { ["installed"] = true, ["roles"] = installed };
    }

    private static JsonObject ResolveBuild(CommandContext context)
    {
        var state = context.RequireState();
        if (!state.Dispatch.Pending || state.Dispatch.Stage != DispatchStage.Build) throw new CliFailure("state", "resolve requires a pending build dispatch", 3);
        if (string.IsNullOrWhiteSpace(context.Args.Get("conflict"))) throw new CliFailure("usage", "build resolve requires --conflict");
        return StateStore.Update(context.Workspace, state, current =>
        {
            current.Dispatch.Pending = false;
            current.Dispatch.Conflict = context.Args.Get("conflict");
        }).ToJson();
    }

    private static JsonObject Dispatch(CommandContext context)
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
            return StateStore.Update(workspace, state, value => value.Dispatch.Pending = false).ToJson()["dispatch"]!.AsObject();
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

            state = StateStore.Update(workspace, state, value => value.Dispatch.Retry = retries + 1);
            return state.ToJson()["dispatch"]!.AsObject();
        }

        if (state.Dispatch.Pending) throw new CliFailure("state", "a dispatch is already pending; consume it, cancel it, or retry it", 3);
        if (stage is DispatchStage.FixBuild or DispatchStage.FixReview && state.Review.FixRound > state.Workflow.MaxFixRounds) throw new CliFailure("state", "fix retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 3);
        if (stage == DispatchStage.Build)
        {
            foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
            {
                var baseline = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
                if (baseline.ExitCode != 0) throw new CliFailure("state", $"build dispatch requires the pinned baseline ref {reference}", 3);
            }
            var next = state.Workflow.NextTaskNumber;
            if (taskNumber != next) throw new CliFailure("state", $"the next locked task is {next}", 3);
            var total = state.Workflow.TaskCount ?? 0;
            if (taskNumber > total) throw new CliFailure("state", "task number is outside the locked plan", 3);
        }

        if (definition.RequiresFreshReview) RequireFreshReviewManifest(workspace, state);

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
        return state.ToJson()["dispatch"]!.AsObject();
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

    private static JsonObject Complete(CommandContext context)
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
        return state.ToJson()["dispatch"]!.AsObject();
    }
}
