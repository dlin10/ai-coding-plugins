using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;
using Xunit;
using ForgeWorkflow = PlanForgeFlow.Workflow.ForgeWorkflow;

namespace PlanForgeFlow.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void CanonicalPlanAndReviewLogAreMarkerlessAndStable()
    {
        var plan = CanonicalText.NormalizePlan("\uFEFF# Plan\r\n\r\n## Approach\r\n1. Implement the slice.\r\n");
        var review = CanonicalText.NormalizeReviewLog("# Review\r\nApproved\r\n");

        Assert.Equal("# Plan\n\n## Approach\n1. Implement the slice.\n", plan);
        Assert.Equal("# Review\nApproved\n", review);
        Assert.Throws<CliFailure>(() => CanonicalText.NormalizePlan("<proposed_plan>\n# Plan\n</proposed_plan>"));
    }

    [Fact]
    public void Base64UrlAndNonceRoundTripWithExpectedShape()
    {
        const string value = "plan / рус中文";

        Assert.Equal(value, Hashing.Base64UrlDecode(Hashing.Base64UrlEncode(value)));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Hashing.Nonce());
    }

    [Fact]
    public void ModelSelectionValidationDoesNotUseCatalogAndRejectsUltra()
    {
        var selection = ModelSelections.Validate("reviewer", "gpt-5.6-sol", "HIGH");

        Assert.Equal("gpt-5.6-sol", selection.Model);
        Assert.Equal("high", selection.Effort);
        Assert.Throws<CliFailure>(() => ModelSelections.Validate("reviewer", "gpt-5.6-sol", "ultra"));
    }

    [Fact]
    public void ForgeWorkflowRequiresEvidenceForStateTransitions()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var forge = Path.Combine(workspace, ".forge");
            Directory.CreateDirectory(forge);
            File.WriteAllText(Path.Combine(forge, "PLAN.md"), "# Plan\n");
            var state = ForgeState.CreateEmpty();
            state.Workflow.Tasks = [new PlanTask(1, "hash", "Implement the slice.")];
            state.Workflow.TaskCount = 1;
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);

            var locked = ForgeWorkflow.Set(WorkflowContext(workspace, state, "locked"));

            Assert.Equal(ForgePhase.Locked, locked.Workflow.Phase);
            var error = Assert.Throws<CliFailure>(() => ForgeWorkflow.Set(WorkflowContext(workspace, StateStore.Load(workspace), "done")));
            Assert.Equal(3, error.ExitCode);
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void ReviewDecisionReaderEnforcesStageSpecificCoverage()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var forge = Path.Combine(workspace, ".forge");
            Directory.CreateDirectory(forge);
            var critique = Path.Combine(forge, "critique.md");
            File.WriteAllText(critique, "Approved.");
            File.WriteAllText(critique + ".json", "{\"verdict\":\"APPROVED\",\"coverage\":\"FULL\"}");

            var decision = ReviewDecisionReader.Read(critique, workspace, DispatchStage.Code);

            Assert.Equal("APPROVED", decision.Verdict);
            Assert.Equal("FULL", decision.Coverage);
            Assert.Throws<CliFailure>(() => ReviewDecisionReader.Read(critique, workspace, DispatchStage.Plan));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void ForgeReviewRequiresFreshManifestBeforeReviewDispatch()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var error = Assert.Throws<CliFailure>(() => ForgeReview.RequireFreshReviewManifest(workspace, ForgeState.CreateEmpty()));

            Assert.Equal(3, error.ExitCode);
            Assert.Contains("review prepare manifest", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void HookCapturesLatestPlanModeProposedPlan()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            var plan = "# Plan\n\n## Approach\n1. Implement the slice.\n";
            File.WriteAllText(transcript,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"session\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\\n" + JsonEncoded(plan) + "</proposed_plan>\"}]}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"default-turn\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Implement the plan.\"}]}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, "default-turn", transcript, "Implement the plan."),
                Serialization.CodexJsonContext.Default.HookInput);
            input = input[..^1] + ",\"future_hook_field\":true}";

            Console.SetOut(output);
            Assert.Equal(0, HookService.Run(input));

            Assert.Equal(plan, PendingPlan.Read(workspace).Plan);
            Assert.Contains("plan materialize", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, HookService.Run("not-json"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void MaterializeCreatesSessionArtifactsAndClearsLegacyArtifacts()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace, commit: true);
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            File.WriteAllText(Path.Combine(workspace, ".forge", "stale.txt"), "stale");
            var plan = "# Plan\n\n## Approach\n1. Implement the slice.\n";
            PendingPlan.Write(workspace, plan);

            var exitCode = RunMaterialize(workspace);

            Assert.Equal(0, exitCode);
            Assert.Equal(plan, File.ReadAllText(Path.Combine(workspace, ".forge", "PLAN.md")));
            Assert.Equal("# Review\n", File.ReadAllText(Path.Combine(workspace, ".forge", "PLAN-REVIEW-LOG.md")));
            Assert.False(File.Exists(Path.Combine(workspace, ".forge", "stale.txt")));
            Assert.False(File.Exists(RepositoryPaths.PendingPlanPath(workspace)));
            var state = StateStore.Load(workspace);
            Assert.Equal(ForgePhase.Materialized, state.Workflow.Phase);
            Assert.Null(state.Review.Manifest);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void InitialMaterializationPreservesMarkerlessRootPlan()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace);
            File.WriteAllText(Path.Combine(workspace, "PLAN.md"), "# User plan\n");
            PendingPlan.Write(workspace, "# Forge plan\n\n## Approach\n1. Implement.\n");

            Assert.Equal(0, RunMaterialize(workspace));

            Assert.Equal("# User plan\n", File.ReadAllText(Path.Combine(workspace, "PLAN.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void LockSnapshotsTasksAndIgnoresSubsequentPlanFileEdits()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace, commit: true);
            PendingPlan.Write(workspace, "# Plan\n\n## Approach\n1. Implement the original slice.\n");
            Assert.Equal(0, RunMaterialize(workspace));
            Assert.Equal(0, new CliApplication().Run(["plan", "lock", "--workspace", workspace]));
            File.WriteAllText(Path.Combine(workspace, ".forge", "PLAN.md"), "# Plan\n\n## Approach\n1. A later edit.\n");

            Assert.Equal(0, new CliApplication().Run(["build", "begin", "--workspace", workspace]));
            Assert.Equal(0, new CliApplication().Run(["build", "dispatch", "--workspace", workspace, "--stage", "build", "--task-number", "1"]));

            Assert.Equal("Implement the original slice.", StateStore.Load(workspace).Workflow.Tasks![0].Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void AmendmentCannotChangeCompletedTasks()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace, commit: true);
            PendingPlan.Write(workspace, "# Plan\n\n## Approach\n1. Keep this task.\n2. Changeable task.\n");
            Assert.Equal(0, RunMaterialize(workspace));
            Assert.Equal(0, new CliApplication().Run(["plan", "lock", "--workspace", workspace]));
            var state = StateStore.Load(workspace);
            state.Workflow.Phase = ForgePhase.Build;
            state.Workflow.NextTaskNumber = 2;
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
            PendingPlan.Write(workspace, "# Plan\n\n## Approach\n1. Changed completed task.\n2. Changeable task.\n");

            Assert.Equal(3, RunMaterialize(workspace, amendment: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void CleanupDeletesForgeAndPendingPlan()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace);
            PendingPlan.Write(workspace, "# Plan\n\n## Approach\n1. Implement.\n");
            Assert.Equal(0, RunMaterialize(workspace));
            PendingPlan.Write(workspace, "# Pending\n\n## Approach\n1. Later.\n");

            Assert.Equal(0, new CliApplication().Run(["run", "cleanup", "--workspace", workspace]));

            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
            Assert.False(File.Exists(RepositoryPaths.PendingPlanPath(workspace)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void RootLockPathBlocksAnotherOperation()
    {
        var workspace = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".forge.lock"), "another-operation");

            Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void RootLockSurvivesForgeDirectoryDeletion()
    {
        var workspace = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            using var stateLock = ForgeStateLock.Acquire(workspace);

            Directory.Delete(Path.Combine(workspace, ".forge"), recursive: true);

            Assert.True(File.Exists(Path.Combine(workspace, ".forge.lock")));
            Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Theory]
    [InlineData("fix-build")]
    [InlineData("fix-review")]
    public void FixDispatchRejectsRoundAtCap(string stage)
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = ForgeState.CreateEmpty();
            state.Workflow.Phase = ForgePhase.CodeReview;
            state.Workflow.MaxFixRounds = 1;
            state.Review.FixRound = 1;
            state.Models.Builder = new PinnedSelection("builder", "low");
            state.Models.Reviewer = new PinnedSelection("reviewer", "low");
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);

            var context = new CommandContext("build dispatch", workspace, ParsedArgs.Parse(["--stage", stage]), state);
            var error = Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.ForgeWorkflow.Dispatch(context));

            Assert.Equal(3, error.ExitCode);
            Assert.Contains("fix retry cap reached", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Theory]
    [InlineData("password=Abcdefghijklmnop1234", "# Review\n")]
    [InlineData("# Plan\n", "api_key=Abcdefghijklmnop1234")]
    public void MaterializeRejectsSensitivePlanAndReviewLog(string plan, string reviewLog)
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace);
            var forge = Path.Combine(workspace, ".forge");
            Directory.CreateDirectory(forge);
            File.WriteAllText(Path.Combine(forge, "preserve.txt"), "existing");

            var error = Assert.Throws<CliFailure>(() => Materializer.Materialize(
                RepositoryPaths.Identify(workspace),
                plan,
                reviewLog,
                0,
                5,
                new ModelSelection("reviewer", "low"),
                new ModelSelection("builder", "low"),
                amendment: false));

            Assert.Equal("usage", error.Code);
            Assert.True(File.Exists(Path.Combine(forge, "preserve.txt")));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void PlanStartIsNotAnExposedCommand()
    {
        var workspace = CreateTempDirectory();
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, new CliApplication().Run(["plan", "start", "--workspace", workspace]));
            output.GetStringBuilder().Clear();
            Assert.Equal(0, new CliApplication().Run(["--help"]));
            Assert.DoesNotContain("plan start", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(oldOut);
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void StateStoreUsesUnversionedStrictState()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = StateStore.CreateEmpty();
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);

            Assert.NotEmpty(state.CreatedAt);
            Assert.Equal(state.CreatedAt, state.UpdatedAt);
            var serialized = JsonSerializer.Serialize(state, Serialization.ForgeJsonContext.Default.ForgeState);
            Assert.DoesNotContain("\"version\"", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"generation\"", serialized, StringComparison.Ordinal);
            DurableFiles.WriteAtomic(StateStore.StatePath(workspace), serialized[..^1] + ",\"unknown\":true}");

            Assert.Throws<CliFailure>(() => StateStore.Load(workspace));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void OwnedJsonSchemasAreStrictAndEnumWireNamesAreStable()
    {
        var materialization = JsonSerializer.Serialize(
            new MaterializationRequest("# Review\n", 1, 5, new ModelSelection("reviewer", "high"), new ModelSelection("builder", "low")),
            Serialization.ForgeJsonContext.Default.MaterializationRequest);
        var manifest = JsonSerializer.Serialize(new ReviewManifest { Version = 4, Generation = "v4", Coverage = "FULL" }, Serialization.ForgeJsonContext.Default.ReviewManifest);
        var decision = JsonSerializer.Serialize(new ReviewDecision("APPROVED", "FULL"), Serialization.ForgeJsonContext.Default.ReviewDecision);
        var pending = JsonSerializer.Serialize(new PendingPlanDocument("workspace", "# Plan\n"), Serialization.ForgeJsonContext.Default.PendingPlanDocument);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(materialization[..^1] + ",\"unknown\":true}", Serialization.ForgeJsonContext.Default.MaterializationRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(manifest[..^1] + ",\"unknown\":true}", Serialization.ForgeJsonContext.Default.ReviewManifest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(decision[..^1] + ",\"unknown\":true}", Serialization.ForgeJsonContext.Default.ReviewDecision));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(pending[..^1] + ",\"unknown\":true}", Serialization.ForgeJsonContext.Default.PendingPlanDocument));

        var state = ForgeState.CreateEmpty();
        state.Workflow.Phase = ForgePhase.CodeReview;
        state.Dispatch.Stage = DispatchStage.FixBuild;
        var serialized = JsonSerializer.Serialize(state, Serialization.ForgeJsonContext.Default.ForgeState);
        Assert.Contains("\"phase\":\"code-review\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"fix-build\"", serialized, StringComparison.Ordinal);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("Plan", "plan")]
    [InlineData("Code", "code")]
    [InlineData("Build", "build")]
    [InlineData("FixBuild", "fix-build")]
    [InlineData("FixReview", "fix-review")]
    public void DispatchStageWireNamesRoundTrip(string stageName, string wireName)
    {
        var stage = Enum.Parse<DispatchStage>(stageName);

        Assert.Equal(wireName, stage.ToWireName());
        Assert.Equal(stage, DispatchStages.Parse(wireName));
        Assert.Equal(stage, DispatchStages.Parse(wireName.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("Materialized", "materialized")]
    [InlineData("Locked", "locked")]
    [InlineData("Build", "build")]
    [InlineData("CodeReview", "code-review")]
    [InlineData("Done", "done")]
    [InlineData("DoneWithFindings", "done-with-findings")]
    public void ForgePhaseWireNamesRoundTrip(string phaseName, string wireName)
    {
        var phase = Enum.Parse<ForgePhase>(phaseName);

        Assert.Equal(wireName, phase.ToWireName());
        Assert.Equal(phase, ForgePhases.Parse(wireName));
    }

    [Fact]
    public void RunSetRejectsUnsupportedPhaseWireName()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = ForgeState.CreateEmpty();
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);

            var error = Assert.Throws<CliFailure>(() => ForgeWorkflow.Set(WorkflowContext(workspace, state, "review")));

            Assert.Equal("usage", error.Code);
            Assert.Equal("phase is unsupported", error.Message);
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void FixReviewVerdictAcceptsPendingFixReviewDispatch()
    {
        var dispatch = new DispatchState { Id = "fix-review-dispatch", Pending = true, Stage = DispatchStage.FixReview };

        Assert.Equal(DispatchStage.FixReview, DispatchStages.RequirePendingReviewVerdict(dispatch, "fix-review"));
        Assert.Equal(DispatchStage.FixReview, DispatchStages.RequirePendingReviewVerdict(dispatch, null));
        Assert.Throws<CliFailure>(() => DispatchStages.RequirePendingReviewVerdict(dispatch with { Stage = DispatchStage.FixBuild }, "fix-build"));
    }

    [Fact]
    public void SensitiveInputClassifiersRejectRiskyValues()
    {
        Assert.True(SensitiveInput.IsSensitivePath("src/.env.production"));
        Assert.True(SensitiveInput.IsSensitivePath("config/appsettings.Production.json"));
        Assert.True(SensitiveInput.IsSensitiveContent("-----BEGIN RSA PRIVATE KEY-----"));
        Assert.False(SensitiveInput.IsSensitivePath("src/Program.cs"));
    }

    [Fact]
    public void RemovedProvenanceCommandsAndOptionsAreRejected()
    {
        var workspace = CreateTempDirectory();
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, new CliApplication().Run(["approval", "resume", "--workspace", workspace]));
            output.GetStringBuilder().Clear();
            Assert.Equal(1, new CliApplication().Run(["build", "dispatch", "--workspace", workspace, "--plan-sha256", "abc"]));
        }
        finally
        {
            Console.SetOut(oldOut);
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void TranscriptReaderRejectsMalformedLinesWithStateExitCode()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "transcript.jsonl");
        try
        {
            File.WriteAllText(path, "{not-json}\n");

            var error = Assert.Throws<CliFailure>(() => TranscriptReader.ReadDocument(path));

            Assert.Equal("state", error.Code);
            Assert.Equal(3, error.ExitCode);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ReleaseLayoutDeclaresRidAwareHookLauncher()
    {
        var pluginRoot = FindPluginRoot();
        var package = File.ReadAllText(Path.Combine(pluginRoot, "build", "package.ps1"));
        var attributes = File.ReadAllText(Path.Combine(Directory.GetParent(pluginRoot)!.Parent!.FullName, ".gitattributes"));
        var hooks = File.ReadAllText(Path.Combine(pluginRoot, "hooks", "hooks.json"));

        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" }) Assert.Contains(rid, package, StringComparison.Ordinal);
        Assert.Contains("plugins/plan-forge-flow/bin/**/planforge", attributes, StringComparison.Ordinal);
        Assert.Contains("hook capture-context", hooks, StringComparison.Ordinal);
        Assert.Contains("planforge-launcher.ps1", hooks, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSourcesAreOrganizedByModule()
    {
        var project = Path.Combine(FindPluginRoot(), "src", "PlanForgeFlow.Cli");

        foreach (var module in new[] { "Cli", "Codex", "Infrastructure", "Review", "Serialization", "Workflow" })
        {
            Assert.True(Directory.Exists(Path.Combine(project, module)), $"missing module directory: {module}");
        }

        Assert.Empty(Directory.EnumerateFiles(project, "*.cs", SearchOption.TopDirectoryOnly));
    }

    private static int RunMaterialize(string workspace, bool amendment = false)
    {
        var oldIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(JsonSerializer.Serialize(
                new MaterializationRequest(
                    "# Review\n",
                    1,
                    5,
                    new ModelSelection("reviewer-model", "high"),
                    new ModelSelection("builder-model", "low")),
                Serialization.ForgeJsonContext.Default.MaterializationRequest)));
            var args = amendment
                           ? new[] { "plan", "materialize", "--workspace", workspace, "--amendment" }
                           : new[] { "plan", "materialize", "--workspace", workspace };
            return new CliApplication().Run(args);
        }
        finally
        {
            Console.SetIn(oldIn);
        }
    }

    private static CommandContext WorkflowContext(string workspace, ForgeState state, string phase)
        => new("run set", workspace, ParsedArgs.Parse(["--key", "phase", "--value", phase]), state);

    private static void InitializeRepository(string workspace, bool commit = false)
    {
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
        if (!commit) return;
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "-c", "user.name=Plan Forge", "-c", "user.email=forge@example.test", "commit", "--allow-empty", "-m", "initial"]).ExitCode);
    }

    private static string JsonEncoded(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "planforge-flow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(entry => entry.Length))
                {
                    try { File.SetAttributes(entry, FileAttributes.Normal); }
                    catch { }
                }
                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException && attempt < 2) { Thread.Sleep(100); }
        }
    }

    private static string FindPluginRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".codex-plugin", "plugin.json"))) return current.FullName;
        }

        throw new DirectoryNotFoundException("plugin root");
    }
}
