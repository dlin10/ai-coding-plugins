using System.Text.Json;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;
using Xunit;

namespace PlanForgeFlow.Tests;

[Collection("Process environment")]
public sealed class ClaudeTransactionContractTests
{
    [Theory]
    [InlineData("anthropic", "anthropic")]
    [InlineData("anthropic", "openai")]
    [InlineData("openai", "anthropic")]
    [InlineData("openai", "openai")]
    public void AllProviderPairingsCompleteClaudePlanTransaction(string reviewerProviderName, string builderProviderName)
    {
        var reviewerProvider = Enum.Parse<ProviderKind>(reviewerProviderName, ignoreCase: true);
        var builderProvider = Enum.Parse<ProviderKind>(builderProviderName, ignoreCase: true);
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousSession = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(workspace, "missing-codex"));
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            const string session = "provider-pairing-session";
            var activation = ClaudeActivations.Begin(repository, session);
            var runId = activation.RunId;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", session);
            const string plan = "# Plan\n\n## Approach\n1. Exercise provider pairing.\n";
            var planFile = Path.Combine(workspace, "approved.md");
            File.WriteAllText(planFile, plan);
            PendingRuns.StageClaude(repository, plan, runId, Selection(reviewerProvider));
            PendingRuns.RecordClaude(repository, runId, "fresh-review", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
            PendingRuns.FinalizeClaude(repository, runId, Selection(builderProvider), "persistent-hold", SupportedClaude);
            var context = new CommandContext("plan materialize", workspace,
                ParsedArgs.Parse(["--run-id", runId, "--plan-file", planFile]), null, HostKind.Claude);

            var result = ForgeWorkflow.MaterializeClaudePlan(context);

            Assert.Equal(reviewerProvider, result.Reviewer.Provider);
            Assert.Equal(builderProvider, result.Builder.Provider);
            Assert.Equal("persistent-hold", PendingRuns.Load(repository, runId, HostKind.Claude).BuilderHoldId);
            Assert.Equal(ClaudeActivationLifecycle.Executing, ClaudeActivations.LoadForSession(session).Lifecycle);
            Assert.True(PlanForgeFlow.Workflow.ForgeWorkflow.Doctor(workspace, HostKind.Claude, _ => null,
                                                                   () => new(true, "2.1.232", null), AbsentCodex).Git.Ok);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", previousSession);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    [Fact]
    public void ClaudeDoctorRejectsForeignForgeAndMissingRunErrorsNameClaude()
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var doctor = Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.ForgeWorkflow.Doctor(
                                                            workspace, HostKind.Claude, _ => null,
                                                            () => new(true, "2.1.232", null), AbsentCodex));
            Assert.Contains("Claude workspace already contains .forge", doctor.Message, StringComparison.Ordinal);

            var missing = Assert.Throws<CliFailure>(() => PendingRuns.Load(RepositoryPaths.Identify(workspace), "missing", HostKind.Claude));
            Assert.Contains("no Claude pending run exists", missing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Cursor", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    private static CodexReadinessData AbsentCodex() => new("absent", null, null, null, null, []);

    [Fact]
    public void AnthropicBuilderFinalizeRequiresResumeCapabilityButOpenAiDoesNot()
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            var reviewer = Selection(ProviderKind.Anthropic);
            PendingRuns.StageClaude(repository, "# Plan\n\n## Approach\n1. Check capability.\n", "anthropic-capability", reviewer);
            PendingRuns.RecordClaude(repository, "anthropic-capability", "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");

            Assert.Throws<CliFailure>(() => PendingRuns.FinalizeClaude(repository, "anthropic-capability", Selection(ProviderKind.Anthropic),
                                                                       "hold-one", () => new(false, "2.1.225", "unsupported")));

            var ready = PendingRuns.FinalizeClaude(repository, "anthropic-capability", Selection(ProviderKind.OpenAI),
                                                    "hold-two", () => throw new InvalidOperationException("OpenAI must not probe Claude"));
            Assert.Equal(PendingRunPhase.Ready, ready.Phase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    [Fact]
    public void ClaudePlanMismatchDoesNotWriteRepositoryOrAdvancePendingTransaction()
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            const string reviewed = "# Plan\n\n## Approach\n1. Implement.\n";
            var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
            var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");
            PendingRuns.StageClaude(repository, reviewed, "run-one", reviewer);
            PendingRuns.RecordClaude(repository, "run-one", "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
            PendingRuns.FinalizeClaude(repository, "run-one", builder, "hold-one");
            var pendingPath = PendingRuns.PathFor(repository, "run-one", HostKind.Claude);
            var pendingBefore = File.ReadAllText(pendingPath);
            var exclude = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--git-path", "info/exclude"]).Stdout.Trim();
            exclude = Path.IsPathRooted(exclude) ? exclude : Path.Combine(workspace, exclude);
            var excludeBefore = File.ReadAllText(exclude);
            var planFile = Path.Combine(workspace, "approved-plan.md");
            File.WriteAllText(planFile, reviewed.Replace("Implement.", "Changed.", StringComparison.Ordinal));
            var statusBefore = ProcessExecution.Run("git", ["-C", workspace, "status", "--porcelain=v1", "--untracked-files=all"]).Stdout;
            var context = new CommandContext("plan materialize", workspace,
                ParsedArgs.Parse(["--run-id", "run-one", "--plan-file", planFile]), null, HostKind.Claude);

            Assert.Throws<CliFailure>(() => ForgeWorkflow.MaterializeClaudePlan(context));
            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
            Assert.Equal(pendingBefore, File.ReadAllText(pendingPath));
            Assert.Equal(excludeBefore, File.ReadAllText(exclude));
            Assert.Equal(statusBefore, ProcessExecution.Run("git", ["-C", workspace, "status", "--porcelain=v1", "--untracked-files=all"]).Stdout);
            Assert.DoesNotContain("refs/plan-forge/", ProcessExecution.Run("git", ["-C", workspace, "for-each-ref", "--format=%(refname)", "refs/plan-forge"]).Stdout,
                                  StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    [Fact]
    public void ClaudeRevisionInvalidatesReviewsAndBuilderHold()
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
            var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");
            PendingRuns.StageClaude(repository, "# Plan\n\n## Approach\n1. First.\n", "revision", reviewer);
            PendingRuns.RecordClaude(repository, "revision", "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
            PendingRuns.FinalizeClaude(repository, "revision", builder, "hold-one");

            var revised = PendingRuns.StageClaude(repository, "# Plan\n\n## Approach\n1. Second.\n", "revision", reviewer);

            Assert.Equal(PendingRunPhase.Reviewing, revised.Phase);
            Assert.Empty(revised.Responses);
            Assert.Null(revised.BuilderSelection);
            Assert.Null(revised.BuilderHoldId);
            Assert.Equal(0, revised.ReviewRound);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    [Fact]
    public void IdenticalClaudeRestagePreservesApprovedReviewAndHold()
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            const string plan = "# Plan\n\n## Approach\n1. First.\n";
            var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
            var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");
            PendingRuns.StageClaude(repository, plan, "same", reviewer);
            PendingRuns.RecordClaude(repository, "same", "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
            var ready = PendingRuns.FinalizeClaude(repository, "same", builder, "hold-one");
            var reloadedSelection = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6",
                                                               ["claude-sonnet-4-6"], "high");

            var restaged = PendingRuns.StageClaude(repository, plan, "same", reloadedSelection);

            Assert.Equal(ready.Phase, restaged.Phase);
            Assert.Equal(ready.BuilderHoldId, restaged.BuilderHoldId);
            Assert.Single(restaged.Responses);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

#if DEBUG
    [Theory]
    [InlineData("baseline-owner-written")]
    [InlineData("baseline-head-written")]
    [InlineData("baseline-worktree-written")]
    [InlineData("state-configured")]
    [InlineData("forge-moved")]
    public void InterruptedClaudeMaterializationReplays(string faultPoint)
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        var previousSession = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            const string session = "materialization-replay-session";
            var activation = ClaudeActivations.Begin(repository, session);
            var runId = activation.RunId;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", session);
            const string plan = "# Plan\n\n## Approach\n1. Replay.\n";
            var planFile = Path.Combine(workspace, "approved.md");
            File.WriteAllText(planFile, plan);
            var reviewer = ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high");
            var builder = ProviderSelections.Validate(ProviderKind.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");
            PendingRuns.StageClaude(repository, plan, runId, reviewer);
            PendingRuns.RecordClaude(repository, runId, "review-one", "ROSLYN: NOT_APPLICABLE\nVERDICT: APPROVED\n");
            PendingRuns.FinalizeClaude(repository, runId, builder, "hold-one");
            var context = new CommandContext("plan materialize", workspace,
                ParsedArgs.Parse(["--run-id", runId, "--plan-file", planFile]), null, HostKind.Claude);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", faultPoint);
            Assert.Throws<CliFailure>(() => ForgeWorkflow.MaterializeClaudePlan(context));
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "foreign-replay-session");
            Assert.Throws<CliFailure>(() => ForgeWorkflow.MaterializeClaudePlan(context));
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", session);

            var result = ForgeWorkflow.MaterializeClaudePlan(context);

            Assert.Equal("build", result.Phase);
            Assert.Equal(PendingRunPhase.Consumed, PendingRuns.Load(repository, runId, HostKind.Claude).Phase);
            Assert.Equal(ClaudeActivationLifecycle.Executing, ClaudeActivations.LoadForSession(session).Lifecycle);
            Assert.Equal(ProviderKind.Anthropic, result.Reviewer.Provider);
            Assert.Equal("sonnet", result.Reviewer.RequestedModel);
            Assert.Contains(StateStore.Load(workspace).Baselines.Untracked,
                            entry => entry.Path.Replace('\\', '/') == "approved.md");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", previousSession);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }
#endif

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyCleanupRemovesOwnedSchemaOneStateRefsExcludeAndCursorPending(bool finalForgeExists)
    {
        var workspace = CreateRepository();
        var data = TestPaths.Unique("planforge-flow-external-tests");
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            var scope = RepositoryPaths.ScopeId(repository);
            var head = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "HEAD"]).Stdout.Trim();
            var state = ForgeState.CreateEmpty(HostKind.Cursor, scope);
            state.Baselines.Head = head;
            state.Baselines.Worktree = head;
            var legacyState = LegacyJson(state, ForgeJsonContext.Default.ForgeState, 2, 1);
            foreach (var suffix in new[] { "owner", "head-base", "worktree-base" })
                Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "update-ref", $"refs/plan-forge/{scope}/{suffix}", head]).ExitCode);
            var exclude = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--git-path", "info/exclude"]).Stdout.Trim();
            exclude = Path.IsPathRooted(exclude) ? exclude : Path.Combine(workspace, exclude);
            File.WriteAllText(exclude, "# >>> plan-forge-flow (managed) >>>\n.forge/\n.forge.materializing-*/\n# <<< plan-forge-flow (managed) <<<\n");
            var run = new PendingRun { Host = HostKind.Cursor, Workspace = workspace, ScopeId = scope, RunId = "legacy" };
            var pendingPath = PendingRuns.PathFor(repository, run.RunId);
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            File.WriteAllText(pendingPath, LegacyJson(run, ForgeJsonContext.Default.PendingRun, 4, 3));
            var staging = Path.Combine(workspace, ".forge.materializing-legacy-transaction");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, ".materialization-transaction"), "legacy-transaction\n");
            if (finalForgeExists)
            {
                Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
                File.WriteAllText(StateStore.StatePath(workspace), legacyState);
            }
            else File.WriteAllText(Path.Combine(staging, "state.json"), legacyState);

            Materializer.CleanupLegacy(workspace, host: HostKind.Cursor);

            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
            Assert.False(Directory.Exists(staging));
            Assert.False(File.Exists(pendingPath));
            Assert.DoesNotContain("plan-forge-flow", File.ReadAllText(exclude), StringComparison.Ordinal);
            Assert.Empty(ProcessExecution.Run("git", ["-C", workspace, "for-each-ref", "--format=%(refname)", "refs/plan-forge"]).Stdout.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            if (Directory.Exists(data)) DeleteDirectory(data);
        }
    }

    [Fact]
    public void LegacyCleanupRefusesConflictingRefWithoutDeletingState()
    {
        var workspace = CreateRepository();
        try
        {
            var repository = RepositoryPaths.Identify(workspace);
            var scope = RepositoryPaths.ScopeId(repository);
            var state = ForgeState.CreateEmpty(HostKind.Cursor, scope);
            state.Baselines.Head = new string('a', 40);
            state.Baselines.Worktree = new string('a', 40);
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            File.WriteAllText(StateStore.StatePath(workspace), LegacyJson(state, ForgeJsonContext.Default.ForgeState, 2, 1));
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "update-ref", $"refs/plan-forge/{scope}/owner", "HEAD"]).ExitCode);

            Assert.Throws<CliFailure>(() => Materializer.CleanupLegacy(workspace, host: HostKind.Cursor));
            Assert.True(File.Exists(StateStore.StatePath(workspace)));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Theory]
    [InlineData("\"provider\":\"anthropic\"", "\"provider\":\"unknown\"")]
    [InlineData("\"requestedModel\":\"sonnet\"", "\"requestedModel\":\"\"")]
    [InlineData("\"resolvedModel\":\"claude-sonnet-4-6\"", "\"resolvedModel\":\"claude-opus-4-6\"")]
    [InlineData("\"modelsUsed\":[\"claude-sonnet-4-6\"]", "\"modelsUsed\":[]")]
    public void SchemaTwoRejectsMalformedProviderQualifiedSelection(string before, string after)
    {
        var workspace = CreateRepository();
        try
        {
            var state = ForgeState.CreateEmpty(HostKind.Claude, RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace)));
            state.Models.Reviewer = PinnedSelection.From(
                ProviderSelections.Validate(ProviderKind.Anthropic, "sonnet", "claude-sonnet-4-6", null, "high"));
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var json = JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState).Replace(before, after, StringComparison.Ordinal);
            File.WriteAllText(StateStore.StatePath(workspace), json);

            Assert.Throws<CliFailure>(() => StateStore.Load(workspace));
        }
        finally { DeleteDirectory(workspace); }
    }

    private static string LegacyJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, int current, int legacy)
        => JsonSerializer.Serialize(value, typeInfo).Replace($"\"schemaVersion\":{current}", $"\"schemaVersion\":{legacy}", StringComparison.Ordinal);

    private static ProviderSelection Selection(ProviderKind provider)
        => provider == ProviderKind.Anthropic
               ? ProviderSelections.Validate(provider, "sonnet", "claude-sonnet-4-6", null, "high")
               : ProviderSelections.Validate(provider, "gpt-5.6-sol", "gpt-5.6-sol", null, "high");

    private static ToolCheckData SupportedClaude() => new(true, "2.1.232 (Claude Code)", null);

    private static string CreateRepository()
    {
        var workspace = TestPaths.Unique("planforge-flow-tests");
        Directory.CreateDirectory(workspace);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "config", "user.name", "Plan Forge"]).ExitCode);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "config", "user.email", "forge@example.test"]).ExitCode);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "commit", "--allow-empty", "-m", "initial"]).ExitCode);
        return workspace;
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length))
        {
            try { File.SetAttributes(entry, FileAttributes.Normal); }
            catch { }
        }
        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
