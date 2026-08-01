using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal sealed class CliApplication
{
    private static readonly RootCommand CommandTree = CliCommandTree.Build();
    private static readonly IReadOnlyList<string> ReviewEvidenceFiles = ["pre-existing.patch", "in-run.patch", "untracked-review.patch", "changed-files.txt"];
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedOptions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["plan start"] = Set("workspace", "session-context"),
            ["plan lock"] = Set("workspace", "relock", "amendment"),
            ["approval issue"] = Set("workspace"),
            ["approval resume"] = Set("workspace", "purge-replay-ledger"),
            ["agents install"] = Set("workspace"),
            ["build dispatch"] = Set("workspace", "stage", "task-number", "retry", "cancel", "fix", "dispatch-id", "model", "effort", "plan-sha256", "amendment", "conflict", "authorization-note", "accept-risk"),
            ["build complete"] = Set("workspace", "task-number", "dispatch-id", "fix", "verification-passed", "authorization-note", "accept-risk"),
            ["build resolve"] = Set("workspace", "conflict", "dispatch-id"),
            ["build begin"] = Set("workspace", "amendment", "relock"),
            ["review prepare"] = Set("workspace", "allow-paths", "full", "critique-file", "plan-sha256", "authorization-note"),
            ["review authorize-preexisting"] = Set("workspace", "authorized-paths", "authorization-note", "accept-risk"),
            ["review verdict"] = Set("workspace", "stage", "verdict", "coverage", "critique-file", "fix", "retry", "accept-risk", "authorization-note"),
            ["session builder"] = Set("workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session reviewer"] = Set("workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["run doctor"] = Set("workspace"),
            ["run status"] = Set("workspace", "full"),
            ["run set"] = Set("workspace", "key", "value", "amendment", "accept-risk", "authorization-note"),
            ["run cleanup"] = Set("workspace", "delete-owned-artifacts", "purge-generated-agents", "purge-replay-ledger", "accept-risk", "authorization-note"),
        };

    public async Task<int> RunAsync(string[] args)
    {
        var command = ResolveCommand(args, out var optionOffset);
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            {
                PrintHelp(command);
                return 0;
            }

            if (!AllowedOptions.ContainsKey(command))
            {
                throw new CliFailure("usage", $"unknown command '{command}'", 1);
            }

            var typedParse = CommandTree.Parse(args);
            if (typedParse.Errors.Count > 0) throw new CliFailure("usage", typedParse.Errors[0].Message);
            var parsed = ParsedArgs.Parse(args.Skip(optionOffset));
            ValidateOptions(command, parsed);
            var workspace = RepositoryPaths.CanonicalWorkspaceRoot(parsed.Get("workspace") ?? Directory.GetCurrentDirectory());
            switch (command)
            {
                case "plan start":
                    RequireMode(workspace, "plan", parsed.Get("session-context"));
                    JsonOutput.Success(command, new JsonObject { ["mode"] = "plan" });
                    break;
                case "plan lock":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, LockPlan(workspace, parsed));
                    break;
                case "approval issue":
                    RequireMode(workspace, "plan");
                    JsonOutput.Success(command, IssueApproval(parsed, workspace));
                    break;
                case "approval resume":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Materializer.Materialize(RepositoryPaths.Identify(workspace), ResolveApprovalWrapper(workspace), parsed.Has("purge-replay-ledger")));
                    break;
                case "agents install":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, InstallAgents());
                    break;
                case "build dispatch":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Dispatch(workspace, parsed));
                    break;
                case "build complete":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Complete(workspace, parsed));
                    break;
                case "build resolve":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, ResolveBuild(workspace, parsed));
                    break;
                case "build begin":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, BeginBuild(workspace, parsed));
                    break;
                case "review prepare":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, PrepareReview(workspace, parsed));
                    break;
                case "review authorize-preexisting":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, AuthorizePreexisting(workspace, parsed));
                    break;
                case "review verdict":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Verdict(workspace, parsed));
                    break;
                case "session builder":
                case "session reviewer":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Session(workspace, parsed, command.EndsWith("builder", StringComparison.Ordinal) ? "builder" : "reviewer"));
                    break;
                case "run doctor":
                    JsonOutput.Success(command, Doctor(workspace));
                    break;
                case "run status":
                    if (!File.Exists(StateStore.StatePath(workspace)))
                    {
                        JsonOutput.Success(command, new JsonObject { ["exists"] = false });
                    }
                    else
                    {
                        var status = StateStore.Load(workspace);
                        status["exists"] = true;
                        JsonOutput.Success(command, status);
                    }
                    break;
                case "run set":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Set(workspace, parsed));
                    break;
                case "run cleanup":
                    RequireMode(workspace, "default");
                    Materializer.Cleanup(workspace, parsed.Has("delete-owned-artifacts"), parsed.Has("purge-replay-ledger"), parsed.Has("purge-generated-agents"));
                    JsonOutput.Success(command, new JsonObject { ["cleaned"] = true, ["deletedArtifacts"] = parsed.Has("delete-owned-artifacts"), ["purgedReplayLedger"] = parsed.Has("purge-replay-ledger"), ["purgedAgents"] = parsed.Has("purge-generated-agents") });
                    break;
                default:
                    throw new CliFailure("usage", $"unknown command '{command}'", 1);
            }
            return 0;
        }
        catch (CliFailure failure)
        {
            JsonOutput.Error(command, failure);
            return failure.ExitCode;
        }
        catch (Exception error)
        {
            JsonOutput.Error(command, new CliFailure("unexpected", error.Message));
            return 1;
        }
    }

    private static string ResolveCommand(string[] args, out int optionOffset)
    {
        optionOffset = 0;
        if (args.Length == 0) return "help";
        if (args[0].StartsWith("--", StringComparison.Ordinal)) return "help";
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            optionOffset = 2;
            return $"{args[0]} {args[1]}";
        }

        optionOffset = 1;
        return args[0];
    }

    private static void ValidateOptions(string command, ParsedArgs parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new CliFailure("usage", $"{command} does not accept positional arguments");
        }

        foreach (var name in parsed.Names)
        {
            if (!AllowedOptions[command].Contains(name)) throw new CliFailure("usage", $"unknown option --{name}");
            if (!BooleanOptionNames.Contains(name) && string.Equals(parsed.Get(name), "true", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{name} requires a value");
        }
    }

    private static readonly IReadOnlySet<string> BooleanOptionNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel", "retry", "fix", "relock", "amendment", "full", "accept-risk", "delete-owned-artifacts", "purge-generated-agents", "purge-replay-ledger", "verification-passed",
    };

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);

    private static void PrintHelp(string command)
    {
        JsonOutput.Success(command, new JsonObject
        {
            ["usage"] = $"planforge {command} [options]",
            ["commands"] = new JsonArray(
                                         "plan start", "plan lock", "approval issue", "approval resume",
                                         "agents install", "build dispatch", "build complete", "build resolve", "build begin",
                                         "review prepare", "review authorize-preexisting", "review verdict",
                                         "session builder", "session reviewer", "run doctor", "run status", "run set", "run cleanup",
                                         "hook capture-context"),
        });
    }

    private static void RequireMode(string workspace, string expected, string? explicitCapturePath = null)
    {
        var capture = SessionCapture.Read(workspace, explicitCapturePath);
        if (capture?.CollaborationMode != expected) throw new CliFailure("environment", $"command requires a fresh {expected}-mode capture", 1);
    }

    private static JsonObject IssueApproval(ParsedArgs parsed, string workspace)
    {
        var input = Console.In.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(input) > 2 * 1024 * 1024) throw new CliFailure("usage", "approval input exceeds the size bound");
        var request = JsonNode.Parse(input)?.AsObject() ?? throw new CliFailure("usage", "approval input must be a JSON object");
        var keys = request.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!keys.SequenceEqual(new[] { "builder", "completedReviewRounds", "humanPlan", "maxRounds", "reviewLog", "reviewer" }, StringComparer.Ordinal)) throw new CliFailure("usage", "approval input keys are not exact");
        var repository = RepositoryPaths.Identify(workspace);
        var capture = SessionCapture.Read(workspace);
        var humanPlan = RequestString(request, "humanPlan");
        var reviewLog = RequestString(request, "reviewLog");
        var completedRounds = RequestInt(request, "completedReviewRounds");
        var maxRounds = RequestInt(request, "maxRounds");
        if (request["reviewer"] is not JsonObject reviewer || request["builder"] is not JsonObject builder) throw new CliFailure("usage", "approval reviewer and builder must be JSON objects");
        var envelope = ResumeEnvelope.Create(
                                             humanPlan,
                                             reviewLog,
                                             completedRounds,
                                             maxRounds,
                                             reviewer,
                                             builder,
                                             repository,
                                             capture,
                                             Materializer.NextPlanRevision(repository));
        var wrapper = ResumeEnvelope.Build(humanPlan, envelope);
        return new JsonObject
        {
            ["planRevision"] = envelope["plan"]!["planRevision"]!.DeepClone(),
            ["humanPlanHash"] = envelope["plan"]!["humanPlanHash"]!.DeepClone(),
            ["proposedPlanOutput"] = "<proposed_plan>\n" + wrapper + "</proposed_plan>",
        };
    }

    private static string RequestString(JsonObject request, string key)
    {
        try
        {
            var value = request[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("must be a non-empty string");
            return value;
        }
        catch (Exception error)
        {
            throw new CliFailure("usage", $"approval input {key} {error.Message}");
        }
    }

    private static int RequestInt(JsonObject request, string key)
    {
        try
        {
            var value = request[key]?.GetValue<int>() ?? throw new FormatException("must be an integer");
            return value;
        }
        catch (Exception error)
        {
            throw new CliFailure("usage", $"approval input {key} {error.Message}");
        }
    }

    private static string ResolveApprovalWrapper(string workspace)
    {
        var capture = SessionCapture.Read(workspace) ?? throw new CliFailure("state", "no fresh session capture is available for approval resume", 3);
        if (string.IsNullOrWhiteSpace(capture.TranscriptPath) || string.IsNullOrWhiteSpace(capture.TurnId) || string.IsNullOrWhiteSpace(capture.SessionId)) throw new CliFailure("state", "session capture lacks transcript provenance", 3);
        return TranscriptAuthorizer.AuthorizeCurrent(capture.TranscriptPath, capture.TurnId, capture.SessionId).Wrapper;
    }

    private static JsonObject LockPlan(string workspace, ParsedArgs parsed)
    {
        var planPath = Path.Combine(workspace, "PLAN.md");
        if (!File.Exists(planPath)) throw new CliFailure("state", "PLAN.md is missing", 3);
        var plan = CanonicalText.NormalizePlan(File.ReadAllText(planPath));
        var state = StateStore.Load(workspace);
        var phase = state["workflow"]!["phase"]!.GetValue<string>();
        if (phase is not "materialized" and not "locked")
        {
            if (!(parsed.Has("relock") && parsed.Has("amendment") && phase is ("build" or "code-review")))
            {
                throw new CliFailure("state", $"plan lock is not legal in phase {phase}", 3);
            }
        }

        var expectedHash = state["approval"]!["planHash"]?.GetValue<string>();
        var actualHash = Hashing.Sha256Hex(plan);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal)) throw new CliFailure("state", "PLAN.md changed since approval; implementation approval is stale", 3);
        var tasks = CanonicalText.ParseTasks(plan);
        var completedTasks = 0;
        if (parsed.Has("relock") && parsed.Has("amendment"))
        {
            completedTasks = Math.Max(0, (state["workflow"]!["nextTaskNumber"]?.GetValue<int>() ?? 1) - 1);
            var oldTasks = state["workflow"]!["tasks"] as JsonArray;
            if (completedTasks > tasks.Count) throw new CliFailure("state", "relock removes completed tasks", 3);
            for (var index = 0; index < completedTasks; index++)
            {
                var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index]?.AsObject() : null;
                if (oldTask is null || oldTask["hash"]?.GetValue<string>() != tasks[index].Hash) throw new CliFailure("state", $"relock changes completed task {index + 1}", 3);
            }
        }
        var head = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "HEAD"]);
        if (head.ExitCode != 0) throw new CliFailure("environment", "could not establish the Git HEAD baseline");
        var untrackedPaths = GitPathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not establish the untracked plan baseline");
        return StateStore.Update(workspace, state, current =>
        {
            var workflow = current["workflow"]!.AsObject();
            workflow["phase"] = "locked";
            workflow["tasks"] = new JsonArray(tasks.Select(task => (JsonNode)task.ToJson()).ToArray());
            workflow["taskCount"] = tasks.Count;
            workflow["nextTaskNumber"] = completedTasks + 1;
            workflow["amendment"] = parsed.Has("amendment");
            if (!(parsed.Has("relock") && parsed.Has("amendment")))
            {
                current["baselines"]!["head"] = head.Stdout.Trim();
                current["baselines"]!["worktree"] = head.Stdout.Trim();
                current["baselines"]!["untracked"] = BaselineEntries(workspace, untrackedPaths);
            }
        });
    }

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
            var existing = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--verify", reference]);
            if (existing.ExitCode != 0 || string.IsNullOrWhiteSpace(existing.Stdout)) refsExist = false;
        }
        if (refsExist && !reuseBaseline) throw new CliFailure("state", "Git baseline refs already exist; cleanup the previous run or use amendment relock", 3);

        var head = refsExist
                       ? ProcessExecution.Run("git", ["-C", workspace, "rev-parse", headRef])
                       : ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "HEAD"]);
        if (head.ExitCode != 0 || string.IsNullOrWhiteSpace(head.Stdout)) throw new CliFailure("environment", "could not establish the build HEAD baseline");
        var worktree = head.Stdout.Trim();
        if (refsExist)
        {
            var existingWorktree = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", worktreeRef]);
            if (existingWorktree.ExitCode != 0 || string.IsNullOrWhiteSpace(existingWorktree.Stdout)) throw new CliFailure("state", "the retained worktree baseline ref is invalid", 3);
            worktree = existingWorktree.Stdout.Trim();
        }
        else
        {
            var status = ProcessExecution.Run("git", ["-C", workspace, "status", "--porcelain"]);
            if (status.ExitCode != 0) throw new CliFailure("environment", "could not establish the build worktree baseline");
            var trackedDirty = status.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("??", StringComparison.Ordinal));
            if (trackedDirty)
            {
                var stash = ProcessExecution.Run("git", ["-C", workspace, "stash", "create", "plan-forge-flow baseline"]);
                if (stash.ExitCode == 0 && !string.IsNullOrWhiteSpace(stash.Stdout)) worktree = stash.Stdout.Trim();
            }
            foreach (var pair in new[] { (headRef, head.Stdout.Trim()), (worktreeRef, worktree) })
            {
                var update = ProcessExecution.Run("git", ["-C", workspace, "update-ref", pair.Item1, pair.Item2]);
                if (update.ExitCode != 0) throw new CliFailure("environment", $"could not pin Git baseline ref {pair.Item1}: {update.Stderr.Trim()}");
            }
        }

        var untracked = refsExist ? state["baselines"]!["untracked"]!.DeepClone() : new JsonArray();
        if (!refsExist)
        {
            var untrackedPaths = GitPathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not record the untracked baseline");
            untracked = BaselineEntries(workspace, untrackedPaths);
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
        Materializer.EnsureSafeDirectory(target);
        Directory.CreateDirectory(target);
        Materializer.EnsureSafeDirectory(target);
        var installed = new JsonArray();
        foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
        {
            var source = FindPluginFile(Path.Combine("agents", name));
            var content = source is not null ? File.ReadAllText(source) : $"# plan-forge-flow generated agent\nname = \"{Path.GetFileNameWithoutExtension(name)}\"\n";
            var destination = Path.Combine(target, name);
            if (File.Exists(destination)) Materializer.EnsureOwnedAgentFile(destination);
            DurableFiles.WriteAtomic(destination, content);
            installed.Add((JsonNode)JsonValue.Create(destination)!);
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
            current["dispatch"]!["resolution"] = parsed.Get("conflict");
        });
    }

    private static JsonObject Dispatch(string workspace, ParsedArgs parsed)
    {
        var stage = parsed.Has("fix") ? "fix" : parsed.GetRequired("stage").ToLowerInvariant();
        if (stage is not ("plan" or "code" or "build" or "fix")) throw new CliFailure("usage", "--stage must be plan|code|build|fix");
        var taskText = parsed.Get("task-number");
        var taskNumber = 0;
        if (stage == "build" && !int.TryParse(taskText, NumberStyles.None, CultureInfo.InvariantCulture, out taskNumber)) throw new CliFailure("usage", "build dispatch requires numeric --task-number");
        var state = StateStore.Load(workspace);
        RequirePlanHash(state, parsed);
        var pinnedSelection = ResolvePinnedSelection(state, stage, parsed);
        var currentPhase = state["workflow"]!["phase"]!.GetValue<string>();
        var expectedPhase = stage == "build" ? "build" : stage is "code" or "fix" ? "code-review" : "locked";
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
        var fixReviewDispatch = stage == "fix" && string.Equals(state["dispatch"]!["resolution"]?.GetValue<string>(), "builder-complete", StringComparison.Ordinal);
        if (stage == "fix" && state["review"]!["fixRound"]!.GetValue<int>() > state["workflow"]!["maxFixRounds"]!.GetValue<int>()) throw new CliFailure("state", "fix retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 3);
        if (stage == "build")
        {
            foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
            {
                var baseline = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--verify", reference]);
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
            value["dispatch"]!["resolution"] = fixReviewDispatch ? "review-pending" : null;
        });
        return state["dispatch"]!.AsObject();
    }

    private static JsonObject ResolvePinnedSelection(JsonObject state, string stage, ParsedArgs parsed)
    {
        var fixReviewDispatch = stage == "fix" && string.Equals(state["dispatch"]!["resolution"]?.GetValue<string>(), "builder-complete", StringComparison.Ordinal);
        var role = stage is "plan" or "code" || fixReviewDispatch ? "reviewer" : "builder";
        var selection = state["models"]![role] as JsonObject;
        if (selection is null) throw new CliFailure("state", $"{stage} dispatch requires a pinned {role} selection", 3);
        var model = selection["model"]?.GetValue<string>();
        var effort = selection["effort"]?.GetValue<string>()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort) || effort == "ultra")
        {
            throw new CliFailure("state", $"the pinned {role} selection is invalid", 3);
        }

        ValidateObservedSelection(parsed, model, effort);
        return new JsonObject { ["model"] = model, ["effort"] = effort };
    }

    private static void RequireFreshReviewManifest(string workspace, JsonObject state)
    {
        var manifest = state["review"]!["manifest"] as JsonObject;
        if (manifest is null || string.IsNullOrWhiteSpace(manifest["treeFingerprint"]?.GetValue<string>())) throw new CliFailure("state", "a review prepare manifest is required before dispatch", 3);
        VerifyReviewEvidence(workspace, manifest);
        var expected = manifest["treeFingerprint"]!.GetValue<string>();
        var actual = ReviewTreeFingerprint(workspace, state);
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new CliFailure("state", "the review prepare manifest is stale; prepare review again before dispatch", 3);
    }

    private static void EnsureReviewEvidenceFile(string path)
    {
        if (!File.Exists(path)) throw new CliFailure("state", $"review evidence is missing: {path}", 3);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new CliFailure("state", $"review evidence must be a regular file: {path}", 3);
        try
        {
            if (new FileInfo(path).LinkTarget is not null) throw new CliFailure("state", $"review evidence must not be a symlink: {path}", 3);
        }
        catch (PlatformNotSupportedException) { }
    }

    private static void VerifyReviewEvidence(string workspace, JsonObject manifest)
    {
        if (manifest["evidenceHashes"] is not JsonObject hashes) throw new CliFailure("state", "review manifest lacks generated evidence hashes", 3);
        var actualKeys = hashes.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var expectedKeys = ReviewEvidenceFiles.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actualKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal)) throw new CliFailure("state", "review manifest evidence hash fields are not exact", 3);

        var forge = Path.Combine(workspace, ".forge");
        Materializer.EnsureSafeDirectory(forge);
        foreach (var name in ReviewEvidenceFiles)
        {
            var expected = hashes[name]?.GetValue<string>();
            if (!Regex.IsMatch(expected ?? string.Empty, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new CliFailure("state", $"review evidence hash is malformed: {name}", 3);
            var path = Path.Combine(forge, name);
            EnsureReviewEvidenceFile(path);
            if (!string.Equals(Hashing.Sha256File(path), expected, StringComparison.Ordinal)) throw new CliFailure("state", $"review evidence changed after preparation: {name}", 3);
        }

        var manifestPath = Path.Combine(forge, "review-manifest.json");
        EnsureReviewEvidenceFile(manifestPath);
        try
        {
            var onDisk = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            if (onDisk is null || !string.Equals(onDisk.ToJsonString(), manifest.ToJsonString(), StringComparison.Ordinal)) throw new CliFailure("state", "review manifest does not match the state evidence", 3);
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"review manifest is malformed: {error.Message}", 3); }
    }

    private static void RequireApprovedReviewEvidence(string workspace, JsonObject state)
    {
        var review = state["review"]!.AsObject();
        if (review["verdict"]?.GetValue<string>() != "APPROVED" || review["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("state", "done requires a full approved review", 3);
        if (state["dispatch"]!["pending"]?.GetValue<bool>() == true) throw new CliFailure("state", "done requires a consumed review dispatch", 3);
        if (state["agents"]!["reviewerIds"] is not JsonArray reviewerIds || reviewerIds.Count == 0 || !string.Equals(state["agents"]!["lastReviewerDispatchId"]?.GetValue<string>(), state["dispatch"]!["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "done requires a reviewer session for the current dispatch", 3);
        RequireFreshReviewManifest(workspace, state);
        if (review["manifest"] is not JsonObject manifest || manifest["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("state", "done requires fresh full review evidence", 3);

        var critiquePath = review["critiqueFile"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(critiquePath)) throw new CliFailure("state", "done requires the approved critique file", 3);
        var absolute = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        if (!IsContained(workspace, absolute) || !File.Exists(absolute)) throw new CliFailure("state", "approved critique file is missing or outside the workspace", 3);
        if ((File.GetAttributes(absolute) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(absolute).Length > 100 * 1024) throw new CliFailure("state", "approved critique file is oversized or symlinked", 3);
        var critiqueHash = Hashing.Sha256File(absolute);
        if (review["critiqueFiles"] is not JsonArray critiqueFiles || !critiqueFiles.Any(item => item is JsonObject entry && string.Equals(entry["path"]?.GetValue<string>(), absolute, PathComparison()) && string.Equals(entry["hash"]?.GetValue<string>(), critiqueHash, StringComparison.Ordinal))) throw new CliFailure("state", "approved critique file is not bound to the recorded review history", 3);
        try
        {
            var critique = File.ReadAllText(absolute);
            if (ParseVerdict(critique, null) != "APPROVED" || ParseCoverage(critique, null) != "FULL") throw new FormatException("critique does not contain an approved full verdict");
        }
        catch (CliFailure error) when (error.Code == "verdict")
        {
            throw new CliFailure("state", "approved critique file no longer contains an approved full verdict", 3);
        }
        catch (Exception error)
        {
            throw new CliFailure("state", $"approved critique file is invalid: {error.Message}", 3);
        }
    }

    private static void ValidateObservedSelection(ParsedArgs parsed, string expectedModel, string expectedEffort, bool requireObservation = false)
    {
        var suppliedModel = parsed.Get("model");
        var suppliedEffort = parsed.Get("effort")?.ToLowerInvariant();
        if ((suppliedModel is null) != (suppliedEffort is null)) throw new CliFailure("usage", "--model and --effort must be supplied together");
        if (requireObservation && suppliedModel is null) throw new CliFailure("usage", "session registration requires --model and --effort");
        if (suppliedEffort == "ultra") throw new CliFailure("usage", "ultra reasoning effort is unsupported");
        if (suppliedModel is not null && (!string.Equals(suppliedModel, expectedModel, StringComparison.Ordinal) || !string.Equals(suppliedEffort, expectedEffort, StringComparison.Ordinal)))
        {
            throw new CliFailure("state", "the requested model and effort do not match the pinned selection", 3);
        }
    }

    private static JsonObject Complete(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>()) throw new CliFailure("state", "complete requires a pending dispatch", 3);
        var stage = dispatch["stage"]?.GetValue<string>();
        if (stage is not ("build" or "fix")) throw new CliFailure("state", "complete applies only to build or fix dispatches", 3);
        if (stage == "fix" && dispatch["resolution"]?.GetValue<string>() is not null) throw new CliFailure("state", "fix completion requires the builder dispatch, before reviewer dispatch", 3);
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
            else
            {
                value["dispatch"]!["resolution"] = "builder-complete";
            }
        });
        return state["dispatch"]!.AsObject();
    }

    private static JsonObject PrepareReview(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        RequirePlanHash(state, parsed);
        if (state["workflow"]!["phase"]?.GetValue<string>() != "code-review") throw new CliFailure("state", "review prepare requires code-review phase", 3);
        var forge = Path.Combine(workspace, ".forge");
        Materializer.EnsureSafeDirectory(forge);
        Directory.CreateDirectory(forge);
        foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
        {
            var baseline = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--verify", reference]);
            if (baseline.ExitCode != 0) throw new CliFailure("state", $"review prepare requires the pinned baseline ref {reference}", 3);
        }
        var allowPaths = ParsePathArray(parsed.Get("allow-paths"), "allow-paths");
        if (allowPaths.Count > 0) RequireAuthorizationNote(parsed);
        var startingFingerprint = ReviewTreeFingerprint(workspace, state);
        var stateAllowed = (state["review"]!["authorizedPaths"] as JsonArray ?? new JsonArray())
                          .Select(item => item?.GetValue<string>()?.Replace('\\', '/') ?? string.Empty)
                          .Where(path => path.Length > 0)
                          .ToHashSet(StringComparer.Ordinal);
        var sensitiveAllowed = allowPaths
                              .Select(item => item!.GetValue<string>().Replace('\\', '/'))
                              .ToHashSet(StringComparer.Ordinal);
        var allowed = stateAllowed.Concat(sensitiveAllowed).ToHashSet(StringComparer.Ordinal);
        var preExistingTracked = GitPathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/head-base", "refs/plan-forge/worktree-base", "--", "."], "could not prepare the pre-existing tracked review diff");
        var inRunTracked = GitPathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/worktree-base", "--", "."], "could not prepare the in-run tracked review diff");
        var currentUntracked = GitPathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not inspect untracked review files");
        var baselineUntracked = BaselineUntracked(state);
        var currentUntrackedSet = currentUntracked.ToHashSet(StringComparer.Ordinal);
        var preExisting = preExistingTracked.ToHashSet(StringComparer.Ordinal);
        var inRun = inRunTracked.ToHashSet(StringComparer.Ordinal);
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        var untrackedEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in currentUntracked)
        {
            if (!baselineUntracked.TryGetValue(path, out var baselineHash))
            {
                inRun.Add(path);
                untracked.Add(path);
                untrackedEvidence.Add(path);
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(workspace, path));
            if (!File.Exists(absolute) || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0 || !string.Equals(Hashing.Sha256File(absolute), baselineHash, StringComparison.Ordinal))
            {
                inRun.Add(path);
                untracked.Add(path);
                untrackedEvidence.Add(path);
            }
        }
        foreach (var path in baselineUntracked.Keys)
        {
            if (!currentUntrackedSet.Contains(path) && !preExisting.Contains(path) && !inRun.Contains(path)) inRun.Add(path);
        }

        var allPaths = preExisting.Concat(inRun).Concat(untracked).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var withheld = new JsonArray();
        long aggregateBytes = 0;
        foreach (var relative in allPaths)
        {
            ValidateReviewPath(relative);
            var sensitiveAuthorized = sensitiveAllowed.Contains(relative);
            if (preExisting.Contains(relative) && !stateAllowed.Contains(relative))
            {
                withheld.Add((JsonNode)new JsonObject { ["path"] = relative, ["reason"] = "pre-existing change requires explicit authorization" });
                continue;
            }

            if (SensitiveInput.IsSensitivePath(relative) && !sensitiveAuthorized)
            {
                throw new CliFailure("verdict", $"review evidence includes a sensitive path; explicitly allow it with --allow-paths or review authorize-preexisting: {relative}", 2);
            }

            var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
            if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"review path escapes the workspace: {relative}", 3);
            if (!File.Exists(absolute))
            {
                if (untracked.Contains(relative) || baselineUntracked.ContainsKey(relative))
                {
                    withheld.Add((JsonNode)new JsonObject { ["path"] = relative, ["reason"] = "untracked evidence is unavailable" });
                }
                continue;
            }
            var attributes = File.GetAttributes(absolute);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"review evidence includes a symlinked path: {relative}", 3);
            var length = new FileInfo(absolute).Length;
            if (length > 100 * 1024 || aggregateBytes + length > 1024 * 1024)
            {
                withheld.Add((JsonNode)new JsonObject { ["path"] = relative, ["reason"] = "evidence size bound" });
                continue;
            }

            if (IsBinaryFile(absolute))
            {
                if (!sensitiveAuthorized) withheld.Add((JsonNode)new JsonObject { ["path"] = relative, ["reason"] = "binary evidence requires explicit authorization" });
                else aggregateBytes += length;
                continue;
            }

            var content = File.ReadAllText(absolute);
            if (SensitiveInput.IsSensitiveContent(content) && !sensitiveAuthorized)
            {
                throw new CliFailure("verdict", $"review evidence includes withheld sensitive content: {relative}", 2);
            }
            aggregateBytes += length;
        }

        if (parsed.Has("full") && withheld.Count > 0) throw new CliFailure("verdict", "full review evidence is unavailable; resolve the withheld paths or use partial coverage", 2, withheld.DeepClone());
        var coverage = parsed.Has("full") ? "FULL" : allPaths.Length == 0 ? "FULL" : "PARTIAL";
        var preExistingArray = new JsonArray(preExisting.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var inRunArray = new JsonArray(inRun.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var untrackedArray = new JsonArray(untracked.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var withheldPaths = withheld
                           .Select(item => item?.AsObject()["path"]?.GetValue<string>())
                           .Where(path => !string.IsNullOrWhiteSpace(path))
                           .ToHashSet(StringComparer.Ordinal)!;
        var manifest = new JsonObject
        {
            ["version"] = 3,
            ["generation"] = "v2",
            ["coverage"] = coverage,
            ["allowPaths"] = allowPaths,
            ["sensitiveAllowedPaths"] = new JsonArray(sensitiveAllowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["preExistingAuthorizedPaths"] = new JsonArray(stateAllowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["authorizedPaths"] = new JsonArray(allowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["authorizationNote"] = parsed.Get("authorization-note"),
            ["changedFiles"] = allPaths.Length,
            ["preExistingFiles"] = preExistingArray,
            ["inRunFiles"] = inRunArray,
            ["untrackedFiles"] = untrackedArray,
            ["withheld"] = withheld,
            ["aggregateBytes"] = aggregateBytes,
            ["treeFingerprint"] = null,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var headBase = "refs/plan-forge/head-base";
        var worktreeBase = "refs/plan-forge/worktree-base";
        var reviewablePreExisting = preExisting.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var reviewableInRun = inRun.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var reviewableUntracked = untrackedEvidence.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var preExistingPatch = GitDiffOutput(workspace, ["diff", "--binary", headBase, worktreeBase], reviewablePreExisting, "could not capture pre-existing review evidence");
        var inRunPatch = GitDiffOutput(workspace, ["diff", "--binary", worktreeBase], reviewableInRun, "could not capture in-run review evidence");
        var untrackedPatch = BuildUntrackedEvidence(workspace, reviewableUntracked, sensitiveAllowed);
        RequireSensitivePatchAuthorization(preExistingPatch, reviewablePreExisting, sensitiveAllowed, "pre-existing");
        RequireSensitivePatchAuthorization(inRunPatch, reviewableInRun, sensitiveAllowed, "in-run");
        RequireSensitivePatchAuthorization(untrackedPatch, reviewableUntracked, sensitiveAllowed, "untracked");
        DurableFiles.WriteAtomic(Path.Combine(forge, "pre-existing.patch"), preExistingPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "in-run.patch"), inRunPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "untracked-review.patch"), untrackedPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "changed-files.txt"), string.Join(Environment.NewLine, allPaths) + (allPaths.Length == 0 ? string.Empty : Environment.NewLine));
        var evidenceHashes = new JsonObject();
        foreach (var name in ReviewEvidenceFiles)
        {
            var path = Path.Combine(forge, name);
            EnsureReviewEvidenceFile(path);
            evidenceHashes[name] = Hashing.Sha256File(path);
        }
        manifest["evidenceHashes"] = evidenceHashes;
        var finalFingerprint = ReviewTreeFingerprint(workspace, state);
        if (!string.Equals(startingFingerprint, finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed while review evidence was being prepared; retry review prepare", 3);
        manifest["treeFingerprint"] = finalFingerprint;
        DurableFiles.WriteJson(Path.Combine(forge, "review-manifest.json"), manifest);
        StateStore.Update(workspace, state, current =>
        {
            if (!string.Equals(ReviewTreeFingerprint(workspace, current), finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed before the review manifest was committed; retry review prepare", 3);
            current["review"]!["manifest"] = manifest.DeepClone();
        });
        return manifest;
    }

    private static string[] GitPathList(string workspace, IReadOnlyList<string> args, string errorMessage)
    {
        var result = ProcessExecution.Run("git", ["-C", workspace, .. args]);
        if (result.ExitCode != 0) throw new CliFailure("environment", errorMessage);
        return result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(NormalizeReviewPath).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonArray BaselineEntries(string workspace, IEnumerable<string> paths)
    {
        var entries = new JsonArray();
        foreach (var relative in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
            if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"untracked baseline path escapes the workspace: {relative}", 3);
            if (!File.Exists(absolute)) continue;
            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"untracked baseline path is symlinked: {relative}", 3);
            entries.Add((JsonNode)new JsonObject { ["path"] = relative, ["hash"] = Hashing.Sha256File(absolute) });
        }

        return entries;
    }

    private static string ReviewTreeFingerprint(string workspace, JsonObject state)
    {
        var paths = GitPathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/head-base", "--", "."], "could not fingerprint the tracked review tree")
                   .Concat(GitPathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not fingerprint the untracked review tree"))
                   .Concat(BaselineUntracked(state).Keys)
                   .Distinct(StringComparer.Ordinal)
                   .OrderBy(path => path, StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var relative in paths)
        {
            ValidateReviewPath(relative);
            var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
            builder.Append(relative).Append('\0');
            if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"review fingerprint path escapes the workspace: {relative}", 3);
            if (!File.Exists(absolute))
            {
                builder.Append("<missing>\0");
                continue;
            }

            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"review fingerprint path is symlinked: {relative}", 3);
            builder.Append(Hashing.Sha256File(absolute)).Append('\0');
        }

        return Hashing.Sha256Hex(builder.ToString());
    }

    private static string GitOutput(string workspace, IReadOnlyList<string> args, string errorMessage)
    {
        var result = ProcessExecution.Run("git", ["-C", workspace, .. args]);
        if (result.ExitCode != 0) throw new CliFailure("environment", $"{errorMessage}: {result.Stderr.Trim()}");
        return result.Stdout;
    }

    private static string GitDiffOutput(string workspace, IReadOnlyList<string> args, IReadOnlyList<string> paths, string errorMessage)
        => paths.Count == 0 ? string.Empty : GitOutput(workspace, [.. args, "--", .. paths], errorMessage);

    private static string NormalizeReviewPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        ValidateReviewPath(normalized);
        return normalized;
    }

    private static void ValidateReviewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0')) throw new CliFailure("state", "review path is malformed", 3);
        if (path == ".." || path.StartsWith("../", StringComparison.Ordinal)) throw new CliFailure("state", $"review path contains traversal: {path}", 3);
    }

    private static Dictionary<string, string> BaselineUntracked(JsonObject state)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state["baselines"]!["untracked"] is not JsonArray entries) return result;
        foreach (var entry in entries)
        {
            if (entry is not JsonObject item) continue;
            var path = item["path"]?.GetValue<string>()?.Replace('\\', '/');
            var hash = item["hash"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(path) && Regex.IsMatch(hash ?? string.Empty, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) result[path] = hash!;
        }

        return result;
    }

    private static string BuildUntrackedEvidence(string workspace, IEnumerable<string> paths, ISet<string> allowed)
    {
        var builder = new StringBuilder();
        foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var absolute = Path.GetFullPath(Path.Combine(workspace, path));
            builder.Append(path).Append('\t');
            if (!File.Exists(absolute) || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
            {
                builder.Append("withheld").Append('\n');
                continue;
            }

            var bytes = new FileInfo(absolute).Length;
            if (bytes > 100 * 1024 || SensitiveInput.IsSensitivePath(path) && !allowed.Contains(path))
            {
                builder.Append("withheld\t").Append(Hashing.Sha256File(absolute)).Append('\n');
                continue;
            }

            var raw = File.ReadAllBytes(absolute);
            var rawHash = Hashing.Sha256Hex(raw);
            if (IsBinaryBytes(raw))
            {
                if (!allowed.Contains(path))
                {
                    builder.Append("withheld\t").Append(rawHash).Append('\n');
                    continue;
                }

                builder.Append("binary-base64\t")
                       .Append(rawHash)
                       .Append('\t')
                       .Append(Convert.ToBase64String(raw))
                       .Append('\n');
                continue;
            }

            var content = new UTF8Encoding(false, true).GetString(raw);
            if (SensitiveInput.IsSensitiveContent(content) && !allowed.Contains(path))
            {
                builder.Append("withheld\t").Append(rawHash).Append('\n');
                continue;
            }

            builder.Append("content-base64\t")
                   .Append(rawHash)
                   .Append('\t')
                   .Append(Convert.ToBase64String(raw))
                   .Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsBinaryFile(string path) => IsBinaryBytes(File.ReadAllBytes(path));

    private static bool IsBinaryBytes(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return true;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return !bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text));
        }
        catch (DecoderFallbackException) { return true; }
    }

    private static void RequireSensitivePatchAuthorization(string patch, IEnumerable<string> paths, ISet<string> sensitiveAllowed, string label)
    {
        var binaryPatch = patch.Contains("GIT binary patch", StringComparison.Ordinal) || patch.Contains("Binary files ", StringComparison.Ordinal);
        if (!binaryPatch && !SensitiveInput.IsSensitiveContent(patch)) return;
        var unauthorized = paths.Where(path => !sensitiveAllowed.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (unauthorized.Length > 0) throw new CliFailure("verdict", $"{label} review evidence contains sensitive or binary content; explicitly allow every affected path with --allow-paths", 2, new JsonArray(unauthorized.Select(path => (JsonNode)path).ToArray()));
    }

    private static JsonObject AuthorizePreexisting(string workspace, ParsedArgs parsed)
    {
        var raw = parsed.GetRequired("authorized-paths");
        var paths = ParsePathArray(raw, "authorized-paths");
        if (paths.Count > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
        RequireAuthorizationNote(parsed);
        JsonArray? combined = null;
        StateStore.Update(workspace, state =>
        {
            var existing = (state["review"]!["authorizedPaths"] as JsonArray ?? new JsonArray())
                          .Select(item => item?.GetValue<string>())
                          .Where(path => !string.IsNullOrWhiteSpace(path))
                          .Select(path => path!)
                          .Concat(paths.Select(item => item!.GetValue<string>()))
                          .Distinct(StringComparer.Ordinal)
                          .OrderBy(path => path, StringComparer.Ordinal)
                          .ToArray();
            if (existing.Length > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
            combined = new JsonArray(existing.Select(path => (JsonNode)path).ToArray());
            state["review"]!["authorizedPaths"] = combined.DeepClone();
        });
        return new JsonObject { ["authorizedPaths"] = combined ?? paths, ["authorizationNote"] = parsed.Get("authorization-note") };
    }

    private static JsonObject Verdict(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>() || dispatch["stage"]?.GetValue<string>() is not ("plan" or "code" or "fix")) throw new CliFailure("state", "review verdict requires a pending plan, code, or fix dispatch", 3);
        var expectedStage = parsed.Has("fix") ? "fix" : parsed.Get("stage") ?? dispatch["stage"]?.GetValue<string>();
        if (expectedStage is not ("plan" or "code" or "fix") || !string.Equals(expectedStage, dispatch["stage"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "review verdict stage does not match the pending dispatch", 3);
        if (expectedStage == "fix" && !string.Equals(dispatch["resolution"]?.GetValue<string>(), "review-pending", StringComparison.Ordinal)) throw new CliFailure("state", "fix review verdict requires a completed builder dispatch", 3);
        if (expectedStage is "code" or "fix") RequireFreshReviewManifest(workspace, state);
        if (state["agents"]!["reviewerIds"] is not JsonArray reviewerIds || reviewerIds.Count == 0 || !string.Equals(state["agents"]!["lastReviewerDispatchId"]?.GetValue<string>(), dispatch["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "review verdict requires a registered fresh reviewer session for this dispatch", 3);
        if (expectedStage == "plan" && state["workflow"]!["phase"]?.GetValue<string>() != "locked") throw new CliFailure("state", "plan review verdict requires locked phase", 3);
        if (expectedStage is "code" or "fix" && state["workflow"]!["phase"]?.GetValue<string>() != "code-review") throw new CliFailure("state", "code review verdict requires code-review phase", 3);
        var critiquePath = parsed.GetRequired("critique-file");
        var absoluteCritique = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        var forgeRoot = Path.Combine(workspace, ".forge");
        Materializer.EnsureSafeDirectory(forgeRoot);
        if (!IsContained(forgeRoot, absoluteCritique) || !File.Exists(absoluteCritique)) throw new CliFailure("usage", "critique-file must point to a file inside .forge");
        if ((File.GetAttributes(absoluteCritique) & FileAttributes.ReparsePoint) != 0 || new FileInfo(absoluteCritique).Length > 100 * 1024) throw new CliFailure("usage", "critique-file is oversized or symlinked");
        var critique = File.ReadAllText(absoluteCritique);
        if (SensitiveInput.IsSensitiveContent(critique)) throw new CliFailure("verdict", "critique-file contains withheld sensitive content", 2);
        var verdict = ParseVerdict(critique, parsed.Get("verdict"));
        var coverage = expectedStage == "plan" ? null : ParseCoverage(critique, parsed.Get("coverage"));
        if (expectedStage is "code" or "fix" && verdict == "APPROVED")
        {
            if (coverage != "FULL" || state["review"]!["manifest"]!["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("verdict", "approval requires a fresh FULL review manifest and FULL critique coverage", 2);
        }
        if (parsed.Has("accept-risk")) RequireAuthorizationNote(parsed);
        var critiqueHash = Hashing.Sha256File(absoluteCritique);
        var currentRound = expectedStage == "plan"
                               ? state["workflow"]!["round"]!.GetValue<int>()
                               : state["review"]!["fixRound"]!.GetValue<int>();
        var roundCap = expectedStage == "plan"
                           ? state["workflow"]!["maxRounds"]!.GetValue<int>()
                           : state["workflow"]!["maxFixRounds"]!.GetValue<int>();
        var nextRound = currentRound + 1;
        if (verdict == "REVISE" && nextRound > roundCap)
        {
            if (expectedStage != "plan") throw new CliFailure("verdict", $"{expectedStage} review retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 2);
            if (!parsed.Has("accept-risk")) throw new CliFailure("verdict", $"{expectedStage} review retry cap reached; require --accept-risk with --authorization-note", 2);
        }
        state = StateStore.Update(workspace, state, value =>
        {
            if (Hashing.Sha256File(absoluteCritique) != critiqueHash) throw new CliFailure("state", "critique changed while the verdict was being recorded", 3);
            value["dispatch"]!["pending"] = false;
            value["review"]!["verdict"] = verdict;
            value["review"]!["coverage"] = coverage;
            value["review"]!["critiqueFile"] = absoluteCritique;
            var critiqueFiles = value["review"]!["critiqueFiles"]!.AsArray();
            for (var index = critiqueFiles.Count - 1; index >= 0; index--)
            {
                if (critiqueFiles[index] is JsonObject item && string.Equals(item["path"]?.GetValue<string>(), absoluteCritique, PathComparison())) critiqueFiles.RemoveAt(index);
            }
            if (critiqueFiles.Count >= 256) throw new CliFailure("state", "critique history exceeds the size bound", 3);
            critiqueFiles.Add((JsonNode)new JsonObject { ["path"] = absoluteCritique, ["hash"] = critiqueHash });
            if (expectedStage == "fix") value["dispatch"]!["resolution"] = null;
            if (expectedStage == "plan") value["workflow"]!["round"] = nextRound;
            else if (verdict == "REVISE") value["review"]!["fixRound"] = nextRound;
            if (expectedStage is "code" or "fix") value["workflow"]!["phase"] = verdict == "APPROVED" ? "done" : "code-review";
        });
        return new JsonObject { ["action"] = verdict == "APPROVED" ? "approved" : nextRound > roundCap ? "deadlock" : "revise", ["stage"] = expectedStage, ["verdict"] = verdict, ["coverage"] = coverage, ["round"] = nextRound, ["review"] = state["review"]!.DeepClone() };
    }

    private static JsonObject Session(string workspace, ParsedArgs parsed, string role)
    {
        var id = parsed.GetRequired("id");
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>() || string.IsNullOrWhiteSpace(parsed.Get("dispatch-id")) || parsed.Get("dispatch-id") != dispatch["id"]?.GetValue<string>()) throw new CliFailure("state", "session dispatch-id does not match the pending dispatch", 3);
        var stage = dispatch["stage"]?.GetValue<string>();
        if ((role == "builder" && (stage is not ("build" or "fix") || stage == "fix" && dispatch["resolution"]?.GetValue<string>() is not null)) ||
            (role == "reviewer" && (stage is not ("plan" or "code" or "fix") || stage == "fix" && !string.Equals(dispatch["resolution"]?.GetValue<string>(), "review-pending", StringComparison.Ordinal))))
        {
            throw new CliFailure("state", $"{role} session does not match pending {stage} dispatch", 3);
        }
        var expectedModel = dispatch["model"]?.GetValue<string>();
        var expectedEffort = dispatch["effort"]?.GetValue<string>()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expectedModel) || string.IsNullOrWhiteSpace(expectedEffort) || expectedEffort == "ultra") throw new CliFailure("state", "pending dispatch has no valid pinned model selection", 3);
        ValidateObservedSelection(parsed, expectedModel, expectedEffort, requireObservation: true);
        state = StateStore.Update(workspace, state, value =>
        {
            if (role == "builder")
            {
                var previous = value["agents"]!["builderId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, id, StringComparison.Ordinal)) RequireAuthorizationNote(parsed);
                value["agents"]!["builderId"] = id;
                value["agents"]!["lastBuilderDispatchId"] = dispatch["id"]?.DeepClone();
            }
            else
            {
                var ids = value["agents"]!["reviewerIds"]!.AsArray();
                if (ids.Any(item => item?.GetValue<string>() == id)) throw new CliFailure("state", "reviewer id has already been used", 3);
                ids.Add((JsonNode)JsonValue.Create(id)!);
                value["agents"]!["lastReviewerId"] = id;
                value["agents"]!["lastReviewerDispatchId"] = dispatch["id"]?.DeepClone();
            }
        });
        return state["agents"]!.AsObject();
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static JsonArray ParsePathArray(string? raw, string option)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonArray();
        if (Encoding.UTF8.GetByteCount(raw) > 256 * 1024) throw new CliFailure("usage", $"--{option} exceeds the size bound");
        JsonArray paths;
        try { paths = JsonNode.Parse(raw)!.AsArray(); }
        catch (Exception error) { throw new CliFailure("usage", $"--{option} must be a JSON array: {error.Message}"); }
        foreach (var item in paths)
        {
            var path = item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0')) throw new CliFailure("usage", $"--{option} must contain bounded relative path strings");
            var normalized = path.Replace('\\', '/');
            if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{option} contains a traversal path");
        }

        return paths;
    }

    private static bool IsContained(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative is ".." or "" || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return false;
        for (var current = new DirectoryInfo(Path.GetDirectoryName(fullCandidate)!); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }

        return true;
    }

    private static string ParseVerdict(string content, string? explicitValue)
    {
        var value = explicitValue?.ToUpperInvariant();
        if (value is not (null or "APPROVED" or "REVISE")) throw new CliFailure("usage", "--verdict must be APPROVED or REVISE");
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var last = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        var match = last is null ? null : Regex.Match(last.Replace("**", string.Empty, StringComparison.Ordinal).TrimEnd('.', '!'), "^VERDICT:\\s*(APPROVED|REVISE)$");
        var observed = match?.Success == true ? match.Groups[1].Value : null;
        if (value is null && observed is null) throw new CliFailure("verdict", "critique must end with exactly VERDICT: APPROVED or VERDICT: REVISE", 2);
        if (value is not null && observed is not null && !string.Equals(value, observed, StringComparison.Ordinal)) throw new CliFailure("verdict", "explicit verdict does not match critique", 2);
        if (value is null) value = observed;
        return value!;
    }

    private static string ParseCoverage(string content, string? explicitValue)
    {
        var value = explicitValue?.ToUpperInvariant();
        if (value is not (null or "FULL" or "PARTIAL")) throw new CliFailure("usage", "--coverage must be FULL or PARTIAL");
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var verdictIndex = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var hits = lines.Select((line, index) => new { Line = line.Trim(), Index = index })
                        .Where(item => item.Line.StartsWith("COVERAGE:", StringComparison.Ordinal))
                        .Select(item => new { item.Index, Match = Regex.Match(item.Line, "^COVERAGE:\\s*(FULL|PARTIAL)(?:\\s+[—-].*)?$") })
                        .Where(item => item.Match.Success)
                        .ToArray();
        if (hits.Length != 1 || hits[0].Index >= verdictIndex) throw new CliFailure("verdict", "critique must contain exactly one COVERAGE line before the final verdict", 2);
        var observed = hits[0].Match.Groups[1].Value;
        if (value is not null && observed is not null && !string.Equals(value, observed, StringComparison.Ordinal)) throw new CliFailure("verdict", "explicit coverage does not match critique", 2);
        return value ?? observed!;
    }

    private static void RequireAuthorizationNote(ParsedArgs parsed)
    {
        var note = parsed.Get("authorization-note");
        if (string.IsNullOrWhiteSpace(note) || note.Length > 16 * 1024) throw new CliFailure("usage", "accepting risk requires a bounded --authorization-note");
    }

    private static void RequirePlanHash(JsonObject state, ParsedArgs parsed)
    {
        var supplied = parsed.Get("plan-sha256");
        if (supplied is null) return;
        if (!Regex.IsMatch(supplied, "^[a-f0-9]{64}$") || !string.Equals(supplied, state["approval"]!["planHash"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "--plan-sha256 does not match the materialized plan", 3);
    }

    private static string? FindPluginFile(string relative)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(Path.Combine(current.FullName, ".codex-plugin"))) break;
        }

        return null;
    }

    private static JsonObject Doctor(string workspace)
    {
        return new JsonObject
        {
            ["workspace"] = workspace,
            ["git"] = ToolCheck("git", ["--version"]),
            ["dotnet"] = ToolCheck("dotnet", ["--version"]),
            ["state"] = File.Exists(StateStore.StatePath(workspace)),
        };
    }

    private static JsonObject ToolCheck(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            var result = ProcessExecution.Run(fileName, args);
            return new JsonObject { ["ok"] = result.ExitCode == 0, ["version"] = result.Stdout.Trim(), ["error"] = result.ExitCode == 0 ? null : result.Stderr.Trim() };
        }
        catch (CliFailure error)
        {
            return new JsonObject { ["ok"] = false, ["version"] = null, ["error"] = error.Message };
        }
    }

    private static JsonObject Set(string workspace, ParsedArgs parsed)
    {
        var key = parsed.GetRequired("key");
        var value = parsed.GetRequired("value");
        var state = StateStore.Update(workspace, current =>
        {
            if (key == "phase")
            {
                if (value is not ("materialized" or "locked" or "build" or "code-review" or "done" or "done-with-findings")) throw new CliFailure("usage", "phase is unsupported");
                var old = current["workflow"]!["phase"]!.GetValue<string>();
                if (old != value)
                {
                    var legal = (old, value) switch
                                {
                                    ("materialized", "locked") => HasLockEvidence(workspace, current),
                                    ("locked", "build") => HasBuildEvidence(current),
                                    ("build", "code-review") => !current["dispatch"]!["pending"]!.GetValue<bool>() && (current["workflow"]!["taskCount"]?.GetValue<int>() ?? 0) > 0 && current["workflow"]!["nextTaskNumber"]!.GetValue<int>() > current["workflow"]!["taskCount"]!.GetValue<int>(),
                                    ("code-review", "done") => current["review"]!["verdict"]?.GetValue<string>() == "APPROVED" && current["review"]!["coverage"]?.GetValue<string>() == "FULL",
                                    ("code-review", "done-with-findings") => parsed.Has("accept-risk"),
                                    ("code-review", "build") => parsed.Has("amendment"),
                                    _ => false,
                                };
                    if (!legal) throw new CliFailure("state", $"illegal phase transition {old} -> {value}", 3);
                }
                if (value == "done")
                {
                    RequireApprovedReviewEvidence(workspace, current);
                }
                if (value == "code-review" && old == "build" && current["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "cannot enter code-review with a pending dispatch", 3);
                if (value == "done-with-findings") RequireAuthorizationNote(parsed);
                if (old == "code-review" && value == "build")
                {
                    RequireAuthorizationNote(parsed);
                    current["workflow"]!["amendment"] = true;
                    current["review"]!["verdict"] = null;
                    current["review"]!["coverage"] = null;
                    current["models"]!["builder"] = null;
                    current["agents"]!["builderId"] = null;
                }
                current["workflow"]!["phase"] = value;
            }
            else if (key == "round" || key == "fix-round")
            {
                throw new CliFailure("state", $"{key} is advanced only by a review verdict", 3);
            }
            else if (key == "max-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-rounds must be a non-negative integer");
                var workflow = current["workflow"]!.AsObject();
                var currentCap = workflow["maxRounds"]!.GetValue<int>();
                if (number != currentCap + 1) throw new CliFailure("state", $"max-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (workflow["round"]!.GetValue<int>() < currentCap || current["review"]!["verdict"]?.GetValue<string>() != "REVISE") throw new CliFailure("state", "max-rounds may only be extended after the current review cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                workflow["maxRounds"] = number;
            }
            else if (key == "max-fix-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-fix-rounds must be a non-negative integer");
                var workflow = current["workflow"]!.AsObject();
                var currentCap = workflow["maxFixRounds"]!.GetValue<int>();
                if (number != currentCap + 1) throw new CliFailure("state", $"max-fix-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (current["review"]!["fixRound"]!.GetValue<int>() < currentCap || current["review"]!["verdict"]?.GetValue<string>() != "REVISE") throw new CliFailure("state", "max-fix-rounds may only be extended after the current fix cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-fix-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                workflow["maxFixRounds"] = number;
            }
            else if (key == "coverage")
            {
                throw new CliFailure("state", "coverage is managed only by review verdict", 3);
            }
            else if (key == "verdict")
            {
                throw new CliFailure("state", "verdict is managed only by review verdict", 3);
            }
            else throw new CliFailure("usage", $"unsupported state key: {key}");
        });
        return state;
    }

    private static bool HasLockEvidence(string workspace, JsonObject state)
    {
        var planPath = Path.Combine(workspace, "PLAN.md");
        if (!File.Exists(planPath) || (File.GetAttributes(planPath) & FileAttributes.ReparsePoint) != 0) return false;
        if (File.ReadLines(planPath).FirstOrDefault() != CanonicalText.OwnedMarker) return false;
        var expectedHash = state["approval"]!["planHash"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, Hashing.Sha256Hex(CanonicalText.NormalizePlan(File.ReadAllText(planPath))), StringComparison.Ordinal)) return false;
        var tasks = state["workflow"]!["tasks"] as JsonArray;
        return tasks is { Count: > 0 } && state["workflow"]!["taskCount"]?.GetValue<int>() == tasks.Count;
    }

    private static bool HasBuildEvidence(JsonObject state)
    {
        return !string.IsNullOrWhiteSpace(state["baselines"]!["head"]?.GetValue<string>()) &&
               !string.IsNullOrWhiteSpace(state["baselines"]!["worktree"]?.GetValue<string>()) &&
               state["models"]!["builder"] is JsonObject;
    }
}
