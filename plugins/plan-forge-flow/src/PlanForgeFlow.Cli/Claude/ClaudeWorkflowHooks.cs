using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Claude;

internal static class ClaudeWorkflowHooks
{
    private const int MaxInputBytes = 1024 * 1024;

    public static string Handle(string json)
    {
        ClaudeHookInput? input = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaxInputBytes) throw new CliFailure("usage", "Claude hook input exceeds the size bound");
            input = JsonSerializer.Deserialize(json, ClaudeJsonContext.Default.ClaudeHookInput) ?? throw new CliFailure("usage", "Claude hook input is empty");
            return input.HookEventName switch
            {
                "UserPromptExpansion" when input.CommandName == "plan-forge-flow:forge" => Arm(input),
                "PreToolUse" when input.ToolName == "Skill" => Arm(input),
                "PreToolUse" when input.ToolName == "ExitPlanMode" => GateExit(input),
                "Stop" => GateStop(input),
                _ => string.Empty,
            };
        }
        catch (Exception error)
        {
            var message = error is CliFailure ? error.Message : $"Claude Forge gate failed: {error.Message}";
            return Failure(input, message);
        }
    }

    public static int Run(string json)
    {
        Console.Out.Write(Handle(json));
        return 0;
    }

    private static string Arm(ClaudeHookInput input)
    {
        var repository = Repository(input);
        var sessionId = SessionId(input);
        var activation = ClaudeActivations.Begin(repository, sessionId);
        var context = $"Plan Forge run {activation.RunId} is armed for Claude session {sessionId}. " +
                      "Forge overrides the host's default planning workflow. Required order: Act 1 grill and canonical plan; " +
                      $"plan stage --host claude --run-id {activation.RunId}; Act 2 fresh reviewer and recorded verdict; " +
                      "plan finalize; only then call ExitPlanMode with the exact reviewed plan.";
        return Serialize(new ClaudeHookOutput(
            null,
            null,
            new ClaudeHookSpecificOutput(input.HookEventName!, null, null, context)));
    }

    private static string GateExit(ClaudeHookInput input)
    {
        var sessionId = SessionId(input);
        var activation = ClaudeActivations.TryLoadForSession(sessionId);
        if (activation is null) return string.Empty;
        if (activation.Lifecycle != ClaudeActivationLifecycle.Planning) return string.Empty;
        var repository = Repository(input);
        ClaudeActivations.RequirePlanning(repository, activation.RunId, sessionId);
        var run = PendingRuns.TryLoadForRun(repository, activation.RunId, HostKind.Claude);
        if (run is null) return Deny("Act 1 is not staged. Run plan stage --host claude with the armed run ID before Act 2 and ExitPlanMode.");
        var reason = run.Phase switch
        {
            PendingRunPhase.Reviewing => "Act 2 is incomplete: a fresh reviewer must return and record exactly one verdict before ExitPlanMode.",
            PendingRunPhase.RevisionRequired => "Act 2 requires revision: revise and restage the plan, then dispatch a new fresh reviewer before ExitPlanMode.",
            PendingRunPhase.ReviewApproved => "Act 2 is approved but not finalized. Run plan finalize --host claude and establish the builder hold before ExitPlanMode.",
            PendingRunPhase.Ready => null,
            PendingRunPhase.Abandoned => "This Claude Forge run was abandoned. Invoke /plan-forge-flow:forge to arm a new run.",
            _ => $"Claude Forge run is in phase {PendingRuns.PhaseName(run.Phase)} and cannot call ExitPlanMode.",
        };
        if (reason is not null) return Deny(reason);
        if (run.DraftText is null) return Deny("Claude Forge Ready state has no reviewed snapshot; abandon and restart the run.");
        if (input.ToolInput is not { ValueKind: JsonValueKind.Object } toolInput ||
            !toolInput.TryGetProperty("plan", out var planElement) || planElement.ValueKind != JsonValueKind.String)
            return Deny("ExitPlanMode did not provide a plan for comparison with the reviewed snapshot.");
        var supplied = CanonicalText.NormalizePlan(planElement.GetString()!);
        return string.Equals(supplied, run.DraftText, StringComparison.Ordinal)
                   ? string.Empty
                   : Deny("ExitPlanMode plan does not exactly match the reviewed snapshot. Restore the reviewed plan or invalidate and repeat Act 2.");
    }

    private static string GateStop(ClaudeHookInput input)
    {
        var sessionId = SessionId(input);
        var activation = ClaudeActivations.TryLoadForSession(sessionId);
        if (activation is null || activation.Lifecycle == ClaudeActivationLifecycle.Planning) return string.Empty;
        var repository = Repository(input);
        activation = ClaudeActivations.Status(repository, sessionId) ??
                     throw new CliFailure("state", "Claude execution lease disappeared during Stop evaluation", 3);
        var (progressHash, reason) = StopProgress(repository, activation);
        if (input.StopHookActive && string.Equals(activation.LastStopProgressHash, progressHash, StringComparison.Ordinal))
            return StopFailure($"Plan Forge cannot finish: {reason}");
        ClaudeActivations.RecordStopBlock(activation, progressHash);
        return Serialize(new ClaudeHookOutput("block", reason, null));
    }

    private static (string Hash, string Reason) StopProgress(RepositoryIdentity repository, ClaudeActivation activation)
    {
        string snapshot;
        string reason;
        switch (activation.Lifecycle)
        {
            case ClaudeActivationLifecycle.Materializing:
                var pending = PendingRuns.TryLoadForRun(repository, activation.RunId, HostKind.Claude);
                snapshot = pending is null ? "missing" : JsonSerializer.Serialize(pending, ForgeJsonContext.Default.PendingRun);
                reason = pending is null
                             ? "Act 3 materialization is incomplete and its pending run is missing. Recover the pending run or perform authorized risk cleanup."
                             : "Act 3 materialization is incomplete. Repeat plan materialize --host claude with the same run and exact reviewed plan; if recovery is impossible, perform authorized risk cleanup.";
                break;
            case ClaudeActivationLifecycle.Executing:
                var state = StateStore.Load(repository.WorkspaceRoot);
                ClaudeActivations.RequireExecution(repository, state, activation.SessionId);
                snapshot = JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState);
                reason = ExecutionStopReason(state);
                break;
            case ClaudeActivationLifecycle.Cleaning:
                var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
                var statePath = StateStore.StatePath(repository.WorkspaceRoot);
                if (File.Exists(statePath))
                {
                    var cleaningState = StateStore.Load(repository.WorkspaceRoot);
                    ClaudeActivations.RequireOwnedState(activation, repository, cleaningState);
                    snapshot = $"forge={Directory.Exists(forgeDirectory)};state=" +
                               JsonSerializer.Serialize(cleaningState, ForgeJsonContext.Default.ForgeState);
                }
                else snapshot = $"forge={Directory.Exists(forgeDirectory)};state=missing";
                reason = "Act 4 terminal cleanup did not finish. Repeat run cleanup --host claude to recover the cleaning transaction.";
                break;
            default:
                throw new CliFailure("state", $"Claude Stop gate cannot evaluate lifecycle {ClaudeActivations.LifecycleName(activation.Lifecycle)}", 3);
        }

        return (Hashing.Sha256Hex(ClaudeActivations.LifecycleName(activation.Lifecycle) + "\n" + snapshot), reason);
    }

    private static string ExecutionStopReason(ForgeState state)
    {
        if (state.Workflow.Phase == ForgePhase.Build)
        {
            if (state.Dispatch.Pending)
            {
                if (state.Dispatch.Stage != DispatchStage.Build)
                    throw new CliFailure("state", "build phase contains a non-build pending dispatch", 3);
                return string.Equals(state.Agents.LastBuilderDispatchId, state.Dispatch.Id, StringComparison.Ordinal)
                           ? $"Act 3 task {state.Dispatch.TaskNumber} is still pending. Have the persistent builder finish it, verify it, and run build complete."
                           : $"Act 3 task {state.Dispatch.TaskNumber} is dispatched but has no matching persistent builder registration. Resume/register the builder before completion.";
            }

            var taskCount = state.Workflow.TaskCount ?? 0;
            if (state.Workflow.NextTaskNumber <= taskCount)
                return $"Act 3 is incomplete. Dispatch the next locked task ({state.Workflow.NextTaskNumber} of {taskCount}) to the persistent builder.";
            throw new CliFailure("state", "build phase has no remaining task but has not entered code-review", 3);
        }

        if (state.Workflow.Phase == ForgePhase.CodeReview)
        {
            if (state.Dispatch.Pending)
            {
                return state.Dispatch.Stage switch
                {
                    DispatchStage.Code or DispatchStage.FixReview =>
                        string.Equals(state.Agents.LastReviewerDispatchId, state.Dispatch.Id, StringComparison.Ordinal)
                            ? $"Act 4 {state.Dispatch.Stage.Value.ToWireName()} review is pending a recorded verdict."
                            : $"Act 4 {state.Dispatch.Stage.Value.ToWireName()} review requires a fresh matching reviewer registration and verdict.",
                    DispatchStage.FixBuild =>
                        string.Equals(state.Agents.LastBuilderDispatchId, state.Dispatch.Id, StringComparison.Ordinal)
                            ? "Act 4 fix-build is pending completion by the persistent builder. Run build complete after verification."
                            : "Act 4 fix-build requires the matching persistent builder registration before completion.",
                    _ => throw new CliFailure("state", "code-review phase contains an invalid pending dispatch", 3),
                };
            }

            return string.Equals(state.Review.Verdict, "REVISE", StringComparison.Ordinal)
                       ? "Act 4 review requires fixes. Prepare and dispatch fix-build, then repeat fresh fix-review."
                       : "Act 4 is incomplete. Prepare the initial review evidence and dispatch a fresh full code reviewer.";
        }

        if (state.Workflow.Phase is ForgePhase.Done or ForgePhase.DoneWithFindings)
            return $"Forge reached terminal phase {state.Workflow.Phase.ToWireName()}, but Act 4 cleanup is still required. Run run cleanup --host claude.";
        throw new CliFailure("state", $"Claude execution lease contains unsupported phase {state.Workflow.Phase.ToWireName()}", 3);
    }

    private static RepositoryIdentity Repository(ClaudeHookInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Cwd)) throw new CliFailure("environment", "Claude hook cwd is required");
        return RepositoryPaths.Identify(input.Cwd);
    }

    private static string SessionId(ClaudeHookInput input)
        => string.IsNullOrWhiteSpace(input.SessionId) ? throw new CliFailure("environment", "Claude hook session_id is required") : input.SessionId;

    private static string Failure(ClaudeHookInput? input, string message)
    {
        if (input?.HookEventName == "UserPromptExpansion")
            return Serialize(new ClaudeHookOutput("block", message, null));
        if (input?.HookEventName == "PreToolUse") return Deny(message);
        if (input?.HookEventName == "Stop" && input.SessionId is { Length: > 0 } sessionId && ClaudeActivations.ExistsForSession(sessionId))
            return StopFailure($"Plan Forge Stop gate failed closed: {message}");
        return string.Empty;
    }

    private static string StopFailure(string reason)
        => Serialize(new ClaudeHookOutput(null, null, null, false, reason));

    private static string Deny(string reason)
        => Serialize(new ClaudeHookOutput(
            null,
            null,
            new ClaudeHookSpecificOutput("PreToolUse", "deny", reason, null)));

    private static string Serialize(ClaudeHookOutput output)
        => JsonSerializer.Serialize(output, ClaudeJsonContext.Default.ClaudeHookOutput);
}
