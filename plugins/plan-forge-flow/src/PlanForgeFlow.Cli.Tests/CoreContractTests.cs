using System.Text.Json;
using PlanForgeFlow;
using Xunit;

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
            var input = JsonSerialization.Serialize(
                new HookInput(workspace, "default-turn", transcript, "Implement the plan."),
                CodexJsonContext.Default.HookInput);
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
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, ForgeJsonContext.Default.ForgeState);
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
    public void StateStoreUsesUnversionedStrictState()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = StateStore.CreateEmpty();
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, ForgeJsonContext.Default.ForgeState);

            var serialized = JsonSerialization.Serialize(state, ForgeJsonContext.Default.ForgeState);
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
        var materialization = JsonSerialization.Serialize(
            new MaterializationRequest("# Review\n", 1, 5, new ModelSelection("reviewer", "high"), new ModelSelection("builder", "low")),
            ForgeJsonContext.Default.MaterializationRequest);
        var manifest = JsonSerialization.Serialize(new ReviewManifest { Version = 4, Generation = "v4", Coverage = "FULL" }, ForgeJsonContext.Default.ReviewManifest);
        var decision = JsonSerialization.Serialize(new ReviewDecision("APPROVED", "FULL"), ForgeJsonContext.Default.ReviewDecision);
        var pending = JsonSerialization.Serialize(new PendingPlanDocument("workspace", "# Plan\n"), ForgeJsonContext.Default.PendingPlanDocument);

        Assert.Throws<JsonException>(() => JsonSerialization.Deserialize(materialization[..^1] + ",\"unknown\":true}", ForgeJsonContext.Default.MaterializationRequest));
        Assert.Throws<JsonException>(() => JsonSerialization.Deserialize(manifest[..^1] + ",\"unknown\":true}", ForgeJsonContext.Default.ReviewManifest));
        Assert.Throws<JsonException>(() => JsonSerialization.Deserialize(decision[..^1] + ",\"unknown\":true}", ForgeJsonContext.Default.ReviewDecision));
        Assert.Throws<JsonException>(() => JsonSerialization.Deserialize(pending[..^1] + ",\"unknown\":true}", ForgeJsonContext.Default.PendingPlanDocument));

        var state = ForgeState.CreateEmpty();
        state.Workflow.Phase = ForgePhase.CodeReview;
        state.Dispatch.Stage = DispatchStage.FixBuild;
        var serialized = JsonSerialization.Serialize(state, ForgeJsonContext.Default.ForgeState);
        Assert.Contains("\"phase\":\"code-review\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"fix-build\"", serialized, StringComparison.Ordinal);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
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

    private static int RunMaterialize(string workspace, bool amendment = false)
    {
        var oldIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(JsonSerialization.Serialize(
                new MaterializationRequest(
                    "# Review\n",
                    1,
                    5,
                    new ModelSelection("reviewer-model", "high"),
                    new ModelSelection("builder-model", "low")),
                ForgeJsonContext.Default.MaterializationRequest)));
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
