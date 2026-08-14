using System.Text.Json;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;
using Xunit;

namespace PlanForgeFlow.Tests;

[Collection("Process environment")]
public sealed class ClaudeActivationContractTests
{
    [Theory]
    [InlineData("UserPromptExpansion", null, "plan-forge-flow:forge")]
    [InlineData("PreToolUse", "Skill", null)]
    public void EntryHooksArmRunAndImmediateExitPlanModeIsDenied(string hookEvent, string? toolName, string? commandName)
    {
        using var environment = new ClaudeActivationTestEnvironment();
        var entry = HookInput(environment.Workspace, "session-one", hookEvent, toolName, commandName);

        var entryOutput = ClaudeWorkflowHooks.Handle(entry);
        var activation = ClaudeActivations.LoadForSession("session-one");
        var exitOutput = ClaudeWorkflowHooks.Handle(HookInput(environment.Workspace, "session-one", "PreToolUse", "ExitPlanMode",
                                                              plan: "# Plan\n\n## Approach\n1. Skip review.\n"));

        Assert.Contains(activation.RunId, entryOutput, StringComparison.Ordinal);
        Assert.Equal(environment.Workspace, activation.Workspace);
        Assert.Equal("deny", JsonValue(exitOutput, "hookSpecificOutput", "permissionDecision"));
        Assert.Contains("plan stage", JsonValue(exitOutput, "hookSpecificOutput", "permissionDecisionReason"), StringComparison.Ordinal);
    }

    [Fact]
    public void ExitGateTracksReviewPhasesAndAllowsOnlyExactReadyPlan()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        const string session = "phase-session";
        const string plan = "# Plan\n\n## Approach\n1. Implement gate.\n";
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var activation = ClaudeActivations.Begin(repository, session);
        var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
        var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");

        PendingRuns.StageClaude(repository, plan, activation.RunId, reviewer);
        Assert.Contains("fresh reviewer", ExitReason(environment.Workspace, session, plan), StringComparison.OrdinalIgnoreCase);
        PendingRuns.RecordClaude(repository, activation.RunId, "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: REVISE\n");
        Assert.Contains("revision", ExitReason(environment.Workspace, session, plan), StringComparison.OrdinalIgnoreCase);
        PendingRuns.StageClaude(repository, plan.Replace("gate", "gate safely", StringComparison.Ordinal), activation.RunId, reviewer);
        PendingRuns.RecordClaude(repository, activation.RunId, "review-two", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
        Assert.Contains("plan finalize", ExitReason(environment.Workspace, session, plan), StringComparison.Ordinal);
        PendingRuns.FinalizeClaude(repository, activation.RunId, builder, "builder-hold");

        Assert.Contains("reviewed snapshot", ExitReason(environment.Workspace, session, plan), StringComparison.OrdinalIgnoreCase);
        var reviewed = plan.Replace("gate", "gate safely", StringComparison.Ordinal);
        Assert.Equal(string.Empty, ClaudeWorkflowHooks.Handle(HookInput(environment.Workspace, session, "PreToolUse", "ExitPlanMode", plan: reviewed)));
        Assert.Equal(string.Empty, ClaudeWorkflowHooks.Handle(HookInput(environment.Workspace, session, "PreToolUse", "ExitPlanMode", plan: reviewed.Replace("\n", "\r\n", StringComparison.Ordinal))));
    }

    [Fact]
    public void ExitGateIsSessionScopedAndCorruptActiveStateFailsClosed()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var activation = ClaudeActivations.Begin(repository, "armed-session");

        Assert.Equal(string.Empty, ClaudeWorkflowHooks.Handle(HookInput(environment.Workspace, "ordinary-session", "PreToolUse", "ExitPlanMode",
                                                                        plan: "# Ordinary plan")));
        var conflict = Assert.Throws<PlanForgeFlow.Cli.CliFailure>(() => ClaudeActivations.Begin(repository, "second-forge-session"));
        Assert.Contains(activation.RunId, conflict.Message, StringComparison.Ordinal);
        Assert.Contains("armed-session", conflict.Message, StringComparison.Ordinal);

        File.WriteAllText(ClaudeActivations.PathForSession("armed-session"), "{");
        var output = ClaudeWorkflowHooks.Handle(HookInput(environment.Workspace, "armed-session", "PreToolUse", "ExitPlanMode",
                                                          plan: "# Plan"));
        Assert.Equal("deny", JsonValue(output, "hookSpecificOutput", "permissionDecision"));
    }

    [Fact]
    public void BeginIsIdempotentAndAbandonRequiresAuthorizationAcrossSessions()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var first = ClaudeActivations.Begin(repository, "owner-session");

        Assert.Equal(first, ClaudeActivations.Begin(repository, "owner-session"));
        Assert.Throws<PlanForgeFlow.Cli.CliFailure>(() => ClaudeActivations.Abandon(repository, first.RunId, "foreign-session", false, null));
        Assert.Throws<PlanForgeFlow.Cli.CliFailure>(() => ClaudeActivations.Abandon(repository, first.RunId, "foreign-session", true, null));

        ClaudeActivations.Abandon(repository, first.RunId, "foreign-session", true, "User authorized takeover.");

        Assert.False(File.Exists(ClaudeActivations.PathForSession("owner-session")));
        Assert.Null(ClaudeActivations.TryLoadForSession("foreign-session"));
    }

    [Fact]
    public void UnarmedPendingRunIsRejectedWithoutMigration()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
        PendingRuns.StageClaude(repository, "# Plan\n\n## Approach\n1. Legacy run.\n", "legacy-unarmed", reviewer);

        var error = Assert.Throws<PlanForgeFlow.Cli.CliFailure>(() => ClaudeActivations.Begin(repository, "new-session"));

        Assert.Equal("unsupported-state-schema", error.Code);
        Assert.Contains("legacy-unarmed", error.Message, StringComparison.Ordinal);
        Assert.Contains("session unarmed", error.Message, StringComparison.Ordinal);
        Assert.Contains("phase reviewing", error.Message, StringComparison.Ordinal);
        Assert.Null(ClaudeActivations.TryLoadForSession("new-session"));
    }

    [Fact]
    public void ClaudePlanningCommandsRequireTheMatchingActivationSession()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var activation = ClaudeActivations.Begin(repository, "owner-session");
        var args = ParsedArgs.Parse([
            "--run-id", activation.RunId,
            "--provider", "anthropic",
            "--requested-model", "sonnet",
            "--resolved-model", "claude-sonnet-4-6",
            "--models-used", "[\"claude-sonnet-4-6\"]",
            "--effort", "high",
        ]);
        var context = new CommandContext("plan stage", environment.Workspace, args, null, HostKind.Claude);

        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "foreign-session");
        Assert.Throws<PlanForgeFlow.Cli.CliFailure>(() => ClaudeCommandHandlers.Stage(context, new StringReader("# Plan\n\n## Approach\n1. Reject.\n")));
        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "owner-session");

        var staged = ClaudeCommandHandlers.Stage(context, new StringReader("# Plan\n\n## Approach\n1. Accept.\n"));

        Assert.Equal(activation.RunId, staged.RunId);
        Assert.Equal(PendingRunPhase.Reviewing, staged.Phase);
    }

    [Fact]
    public void SuccessfulClaudeMaterializationRemovesActivation()
    {
        using var environment = new ClaudeActivationTestEnvironment();
        const string session = "materialize-session";
        const string plan = "# Plan\n\n## Approach\n1. Materialize.\n";
        var repository = RepositoryPaths.Identify(environment.Workspace);
        var activation = ClaudeActivations.Begin(repository, session);
        var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
        var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");
        var planFile = Path.Combine(environment.Workspace, "approved-plan.md");
        File.WriteAllText(planFile, plan);
        PendingRuns.StageClaude(repository, plan, activation.RunId, reviewer);
        PendingRuns.RecordClaude(repository, activation.RunId, "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
        PendingRuns.FinalizeClaude(repository, activation.RunId, builder, "builder-hold");
        var context = new CommandContext("plan materialize", environment.Workspace,
                                         ParsedArgs.Parse(["--run-id", activation.RunId, "--plan-file", planFile]), null, HostKind.Claude);
        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", session);

        PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeClaudePlan(context);

        Assert.False(File.Exists(ClaudeActivations.PathForSession(session)));
        Assert.Equal(PendingRunPhase.Consumed, PendingRuns.Load(repository, activation.RunId, HostKind.Claude).Phase);
    }

    private static string ExitReason(string workspace, string session, string plan)
    {
        var output = ClaudeWorkflowHooks.Handle(HookInput(workspace, session, "PreToolUse", "ExitPlanMode", plan: plan));
        return JsonValue(output, "hookSpecificOutput", "permissionDecisionReason");
    }

    private static string HookInput(string workspace,
                                    string session,
                                    string hookEvent,
                                    string? toolName = null,
                                    string? commandName = null,
                                    string? plan = null)
        => $"{{\"session_id\":{Quote(session)},\"cwd\":{Quote(workspace)},\"hook_event_name\":{Quote(hookEvent)}," +
           $"\"tool_name\":{Quote(toolName)},\"command_name\":{Quote(commandName)}," +
           $"\"tool_input\":{(plan is null ? "null" : $"{{\"plan\":{Quote(plan)}}}")}}}";

    private static string Quote(string? value)
        => value is null ? "null" : $"\"{JsonEncodedText.Encode(value)}\"";

    private static string JsonValue(string json, params string[] path)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        foreach (var segment in path) value = value.GetProperty(segment);
        return value.GetString()!;
    }

    private sealed class ClaudeActivationTestEnvironment : IDisposable
    {
        private readonly string? _previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        private readonly string? _previousSession = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");

        public ClaudeActivationTestEnvironment()
        {
            Workspace = TestPaths.Unique("planforge-flow-activation-tests");
            Data = TestPaths.Unique("planforge-flow-external-tests");
            Directory.CreateDirectory(Workspace);
            Assert.Equal(0, PlanForgeFlow.Infrastructure.Process.ProcessExecution.Run("git", ["-C", Workspace, "init"]).ExitCode);
            Assert.Equal(0, PlanForgeFlow.Infrastructure.Process.ProcessExecution.Run("git", ["-C", Workspace, "config", "user.name", "Plan Forge"]).ExitCode);
            Assert.Equal(0, PlanForgeFlow.Infrastructure.Process.ProcessExecution.Run("git", ["-C", Workspace, "config", "user.email", "forge@example.test"]).ExitCode);
            Assert.Equal(0, PlanForgeFlow.Infrastructure.Process.ProcessExecution.Run("git", ["-C", Workspace, "commit", "--allow-empty", "-m", "initial"]).ExitCode);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", Data);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", null);
        }

        public string Workspace { get; }
        public string Data { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", _previousData);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", _previousSession);
            Delete(Workspace);
            Delete(Data);
        }

        private static void Delete(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length))
            {
                try { File.SetAttributes(entry, FileAttributes.Normal); }
                catch { }
            }
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }
}
