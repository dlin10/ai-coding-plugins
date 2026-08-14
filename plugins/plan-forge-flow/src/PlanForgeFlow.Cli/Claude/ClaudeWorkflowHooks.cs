using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
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
        var repository = Repository(input);
        ClaudeActivations.Require(repository, activation.RunId, sessionId);
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
        return string.Empty;
    }

    private static string Deny(string reason)
        => Serialize(new ClaudeHookOutput(
            null,
            null,
            new ClaudeHookSpecificOutput("PreToolUse", "deny", reason, null)));

    private static string Serialize(ClaudeHookOutput output)
        => JsonSerializer.Serialize(output, ClaudeJsonContext.Default.ClaudeHookOutput);
}
