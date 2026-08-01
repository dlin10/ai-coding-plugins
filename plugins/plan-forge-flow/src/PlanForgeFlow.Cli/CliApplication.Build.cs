using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static JsonObject BeginBuild(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        var phase = state["workflow"]!["phase"]!.GetValue<string>();
        if (phase != "locked") throw new CliFailure("state", $"build begin requires a locked plan (current phase: {phase})", 3);
        if (state["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "a dispatch is already pending", 3);
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
    
        var untracked = refsExist ? state["baselines"]!["untracked"]!.DeepClone() : new JsonArray();
        if (!refsExist)
        {
            var untrackedPaths = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not record the untracked baseline");
            untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
        }
    
        return StateStore.Update(workspace, state, current =>
        {
            current["workflow"]!["phase"] = "build";
            current["baselines"]!["head"] = head.Stdout.Trim();
            current["baselines"]!["worktree"] = worktree;
            current["baselines"]!["untracked"] = untracked;
        });
    }
    
    private static JsonObject InstallAgents()
    {
        var target = RepositoryPaths.AgentsDirectory();
        OwnershipGuards.EnsureSafeDirectory(target);
        Directory.CreateDirectory(target);
        OwnershipGuards.EnsureSafeDirectory(target);
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
    
    private static JsonObject ResolveBuild(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        if (!state["dispatch"]!["pending"]!.GetValue<bool>() || state["dispatch"]!["stage"]?.GetValue<string>() != "build") throw new CliFailure("state", "resolve requires a pending build dispatch", 3);
        if (string.IsNullOrWhiteSpace(parsed.Get("conflict"))) throw new CliFailure("usage", "build resolve requires --conflict");
        return StateStore.Update(workspace, state, current =>
        {
            current["dispatch"]!["pending"] = false;
            current["dispatch"]!["conflict"] = parsed.Get("conflict");
        });
    }
    
    private static JsonObject Dispatch(string workspace, ParsedArgs parsed)
    {
        var stage = parsed.GetRequired("stage").ToLowerInvariant();
        if (stage is not ("plan" or "code" or "build" or "fix-build" or "fix-review")) throw new CliFailure("usage", "--stage must be plan|code|build|fix-build|fix-review");
        var taskText = parsed.Get("task-number");
        var taskNumber = 0;
        if (stage == "build" && !int.TryParse(taskText, NumberStyles.None, CultureInfo.InvariantCulture, out taskNumber)) throw new CliFailure("usage", "build dispatch requires numeric --task-number");
        var state = StateStore.Load(workspace);
        RequirePlanHash(state, parsed);
        var pinnedSelection = ResolvePinnedSelection(state, stage, parsed);
        var currentPhase = state["workflow"]!["phase"]!.GetValue<string>();
        var expectedPhase = stage == "build" ? "build" : stage is "code" or "fix-build" or "fix-review" ? "code-review" : "locked";
        if (currentPhase != expectedPhase) throw new CliFailure("state", $"{stage} dispatch requires phase {expectedPhase} (current: {currentPhase})", 3);
        if (parsed.Has("cancel"))
        {
            if (!state["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "cannot cancel without a pending dispatch", 3);
            if (parsed.Get("dispatch-id") is { } cancelId && !string.Equals(cancelId, state["dispatch"]!["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "cancel dispatch-id does not match the pending dispatch", 3);
            return StateStore.Update(workspace, state, value => value["dispatch"]!["pending"] = false)["dispatch"]!.AsObject();
        }
    
        if (parsed.Has("retry"))
        {
            if (!state["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "cannot retry without a pending dispatch", 3);
            if (parsed.Get("dispatch-id") is { } retryId && !string.Equals(retryId, state["dispatch"]!["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "retry dispatch-id does not match the pending dispatch", 3);
            var retries = state["dispatch"]!["retry"]!.GetValue<int>();
            var cap = state["workflow"]!["maxBuildRetries"]!.GetValue<int>();
            if (retries >= cap)
            {
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "dispatch retry cap reached; require --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
            }
    
            state = StateStore.Update(workspace, state, value => value["dispatch"]!["retry"] = retries + 1);
            return state["dispatch"]!.AsObject();
        }
    
        if (state["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "a dispatch is already pending; consume it, cancel it, or retry it", 3);
        var fixReviewDispatch = stage == "fix-review";
        if (stage is "fix-build" or "fix-review" && state["review"]!["fixRound"]!.GetValue<int>() > state["workflow"]!["maxFixRounds"]!.GetValue<int>()) throw new CliFailure("state", "fix retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 3);
        if (stage == "build")
        {
            foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
            {
                var baseline = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
                if (baseline.ExitCode != 0) throw new CliFailure("state", $"build dispatch requires the pinned baseline ref {reference}", 3);
            }
            var next = state["workflow"]!["nextTaskNumber"]!.GetValue<int>();
            if (taskNumber != next) throw new CliFailure("state", $"the next locked task is {next}", 3);
            var total = state["workflow"]!["taskCount"]?.GetValue<int>() ?? 0;
            if (taskNumber > total) throw new CliFailure("state", "task number is outside the locked plan", 3);
        }
    
        if (stage == "code" || fixReviewDispatch) RequireFreshReviewManifest(workspace, state);
    
        var dispatchId = "dispatch-" + Hashing.Nonce()[..16];
        state = StateStore.Update(workspace, state, value =>
        {
            value["dispatch"]!["id"] = dispatchId;
            value["dispatch"]!["stage"] = stage;
            value["dispatch"]!["taskNumber"] = stage == "build" ? taskNumber : null;
            value["dispatch"]!["retry"] = 0;
            value["dispatch"]!["attempt"] = 1;
            value["dispatch"]!["pending"] = true;
            value["dispatch"]!["model"] = pinnedSelection["model"]!.DeepClone();
            value["dispatch"]!["effort"] = pinnedSelection["effort"]!.DeepClone();
            value["dispatch"]!["conflict"] = null;
        });
        return state["dispatch"]!.AsObject();
    }
    
    private static JsonObject ResolvePinnedSelection(JsonObject state, string stage, ParsedArgs parsed)
    {
        var role = stage is "plan" or "code" or "fix-review" ? "reviewer" : "builder";
        var selection = state["models"]![role] as JsonObject;
        if (selection is null) throw new CliFailure("state", $"{stage} dispatch requires a pinned {role} selection", 3);
        var model = selection["model"]?.GetValue<string>();
        var effort = selection["effort"]?.GetValue<string>().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort) || effort == "ultra")
        {
            throw new CliFailure("state", $"the pinned {role} selection is invalid", 3);
        }
    
        ValidateObservedSelection(parsed, model, effort);
        return new JsonObject { ["model"] = model, ["effort"] = effort };
    }
    
    private static JsonObject Complete(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>()) throw new CliFailure("state", "complete requires a pending dispatch", 3);
        var stage = dispatch["stage"]?.GetValue<string>();
        if (stage is not ("build" or "fix-build")) throw new CliFailure("state", "complete applies only to build or fix-build dispatches", 3);
        if (parsed.Get("dispatch-id") is { } suppliedDispatchId && !string.Equals(suppliedDispatchId, dispatch["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "complete dispatch-id does not match the pending dispatch", 3);
        if (string.IsNullOrWhiteSpace(state["agents"]!["builderId"]?.GetValue<string>())) throw new CliFailure("state", "complete requires a registered builder session", 3);
        if (!string.Equals(state["agents"]!["lastBuilderDispatchId"]?.GetValue<string>(), dispatch["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "complete requires a builder session for the current dispatch", 3);
        var taskNumber = 0;
        if (stage == "build" && !int.TryParse(parsed.Get("task-number"), NumberStyles.None, CultureInfo.InvariantCulture, out taskNumber)) throw new CliFailure("usage", "complete requires numeric --task-number");
        if (stage == "build" && taskNumber != dispatch["taskNumber"]!.GetValue<int>()) throw new CliFailure("state", "complete task does not match the pending dispatch", 3);
        var passed = !string.Equals(parsed.Get("verification-passed"), "false", StringComparison.OrdinalIgnoreCase);
        if (!passed && !parsed.Has("accept-risk")) throw new CliFailure("verdict", "verification failed; complete requires --accept-risk with --authorization-note", 2);
        if (!passed) RequireAuthorizationNote(parsed);
        state = StateStore.Update(workspace, state, value =>
        {
            value["dispatch"]!["pending"] = false;
            value["dispatch"]!["lastVerificationPassed"] = passed;
            if (stage == "build")
            {
                value["workflow"]!["nextTaskNumber"] = taskNumber + 1;
                if (taskNumber >= value["workflow"]!["taskCount"]!.GetValue<int>()) value["workflow"]!["phase"] = "code-review";
            }
        });
        return state["dispatch"]!.AsObject();
    }
}
