using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Cursor;
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
    public void ParsedArgsReadsOptionalAndRequiredPathArrays()
    {
        var parsed = ParsedArgs.Parse([
            "--allow-paths", "[\"src/a.cs\",\"docs/b.md\"]",
            "--authorized-paths", "[\"existing.txt\"]",
        ]);

        Assert.Equal(new[] { "src/a.cs", "docs/b.md" }, parsed.GetPathArray("allow-paths"));
        Assert.Equal(new[] { "existing.txt" }, parsed.GetRequiredPathArray("authorized-paths"));
        Assert.Empty(ParsedArgs.Parse([]).GetPathArray("allow-paths"));
        var required = Assert.Throws<CliFailure>(() => ParsedArgs.Parse([]).GetRequiredPathArray("authorized-paths"));
        Assert.Equal("usage", required.Code);
        Assert.Equal("--authorized-paths is required", required.Message);
    }

    [Fact]
    public void ParsedArgsRejectsMalformedAbsoluteAndTraversalPathArrays()
    {
        var malformed = Assert.Throws<CliFailure>(() => ParsedArgs.Parse(["--allow-paths", "{not-json}"]).GetPathArray("allow-paths"));
        Assert.Contains("--allow-paths must be a JSON array", malformed.Message, StringComparison.Ordinal);

        var absolute = JsonSerializer.Serialize(new[] { Path.GetFullPath("absolute.txt") }, Serialization.ForgeJsonContext.Default.StringArray);
        var rooted = Assert.Throws<CliFailure>(() => ParsedArgs.Parse(["--allow-paths", absolute]).GetPathArray("allow-paths"));
        Assert.Equal("--allow-paths must contain bounded relative path strings", rooted.Message);

        var traversal = Assert.Throws<CliFailure>(() => ParsedArgs.Parse(["--allow-paths", "[\"../secret.txt\"]"]).GetPathArray("allow-paths"));
        Assert.Equal("--allow-paths contains a traversal path", traversal.Message);
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

    [Theory]
    [InlineData("default")]
    [InlineData("bypassPermissions")]
    [InlineData("plan")]
    public void HookStagesLatestPlanModeProposedPlanRegardlessOfPermissionMode(string permissionMode)
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
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"follow-up-plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);
            input = input[..^1] + ",\"permission_mode\":\"" + permissionMode + "\",\"future_hook_field\":true}";

            Console.SetOut(output);
            Assert.Equal(0, HookService.Run(input));

            Assert.Equal(plan, PendingPlan.Read(workspace).Plan);
            Assert.Contains("staged the latest proposed plan", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("first Default-mode implementation turn", output.ToString(), StringComparison.Ordinal);

            var revisedPlan = "# Revised plan\n\n## Approach\n1. Implement the revised slice.\n";
            File.AppendAllText(transcript,
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"revised-plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\\n" + JsonEncoded(revisedPlan) + "</proposed_plan>\"}]}}\n");
            Assert.Equal(0, HookService.Run(input));
            Assert.Equal(revisedPlan, PendingPlan.Read(workspace).Plan);
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
    public void HookStagesPlanWhileTranscriptIsOpenForWriting()
    {
        if (!OperatingSystem.IsWindows()) return;

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
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\\n" + JsonEncoded(plan) + "</proposed_plan>\"}]}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);

            Console.SetOut(output);
            using (File.Open(transcript, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Assert.Equal(0, HookService.Run(input));
                Assert.Equal(plan, PendingPlan.Read(workspace).Plan);
            }

            Assert.Contains("staged the latest proposed plan", output.ToString(), StringComparison.Ordinal);
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
    public void HookAcceptsUtf8BomFromStandardInput()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var oldIn = Console.In;
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            var plan = "# Plan\n\n## Approach\n1. Implement the slice.\n";
            File.WriteAllText(transcript,
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\\n" + JsonEncoded(plan) + "</proposed_plan>\"}]}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);

            Console.SetIn(new StringReader("\uFEFF" + input));
            Console.SetOut(output);
            Assert.Equal(0, Program.Main(["hook", "capture-context"]));

            Assert.Equal(plan, PendingPlan.Read(workspace).Plan);
            Assert.Contains("staged the latest proposed plan", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(oldIn);
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
            Assert.Contains(".forge/\n", File.ReadAllText(Path.Combine(workspace, ".git", "info", "exclude")), StringComparison.Ordinal);
            Assert.DoesNotContain(".forge.lock", File.ReadAllText(Path.Combine(workspace, ".git", "info", "exclude")), StringComparison.Ordinal);
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
    public void StaleExternalWorkspaceLockFileDoesNotBlockAnotherOperation()
    {
        var workspace = CreateTempDirectory(); var locks = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_LOCK_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_LOCK_DATA", locks);
            using (var initialLock = ForgeStateLock.Acquire(workspace)) { }
            using var stateLock = ForgeStateLock.Acquire(workspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_LOCK_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(locks);
        }
    }

    [Theory]
    [InlineData("$forge Plan this change.")]
    [InlineData("[$plan-forge-flow:forge](C:\\\\Users\\\\Admin\\\\.codex\\\\plugins\\\\plan-forge-flow\\\\SKILL.md) Plan this change.")]
    public void HookDoesNotBlockForgeInvocation(string prompt)
    {
        var workspace = CreateTempDirectory();
        var transcript = Path.Combine(workspace, "transcript.jsonl");
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            File.WriteAllText(transcript, "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"default-turn\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);
            input = input[..^1] + ",\"turn_id\":\"default-turn\",\"prompt\":\"" + JsonEncoded(prompt) + "\"}";

            Console.SetOut(output);
            Assert.Equal(0, HookService.Run(input));

            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void MaterializeAcceptsUtf8BomFromStandardInput()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace);
            PendingPlan.Write(workspace, "# Plan\n\n## Approach\n1. Implement the slice.\n");

            Assert.Equal(0, RunMaterialize(workspace, inputPrefix: "\uFEFF"));
            Assert.True(File.Exists(Path.Combine(workspace, ".forge", "PLAN.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void MaterializeReportsReviewLogArrayAsUsageError()
    {
        var workspace = CreateTempDirectory();
        var oldIn = Console.In;
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            InitializeRepository(workspace);
            Console.SetIn(new StringReader(
                "{\"reviewLog\":[],\"completedReviewRounds\":3,\"maxRounds\":5," +
                "\"reviewer\":{\"model\":\"reviewer-model\",\"effort\":\"high\"}," +
                "\"builder\":{\"model\":\"builder-model\",\"effort\":\"medium\"}}"));
            Console.SetOut(output);

            var exitCode = new CliApplication().Run(["plan", "materialize", "--workspace", workspace]);

            Assert.Equal(1, exitCode);
            Assert.Contains("\"code\":\"usage\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("reviewLog as a string", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void HookAllowsPlanModeForgeInvocationWithBypassPermissions()
    {
        var workspace = CreateTempDirectory();
        var transcript = Path.Combine(workspace, "transcript.jsonl");
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            File.WriteAllText(transcript,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"session\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n");
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);
            input = input[..^1] + ",\"turn_id\":\"plan-turn\",\"prompt\":\"$forge Plan this change.\",\"permission_mode\":\"bypassPermissions\"}";

            Console.SetOut(output);
            Assert.Equal(0, HookService.Run(input));

            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            DeleteDirectory(workspace);
        }
    }

    [Theory]
    [InlineData("{not-json}\n")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"id\":\"session\"}}\n")]
    public void HookIgnoresTranscriptWithoutProposedPlan(string transcriptContent)
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
            File.WriteAllText(transcript, transcriptContent);
            var input = JsonSerializer.Serialize(
                new HookInput(workspace, transcript),
                Serialization.CodexJsonContext.Default.HookInput);

            Console.SetOut(output);
            Assert.Equal(0, HookService.Run(input));

            Assert.Equal(string.Empty, output.ToString());
            Assert.False(File.Exists(RepositoryPaths.PendingPlanPath(workspace)));
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
    public void AmendmentPreservesCritiqueHistory()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true);
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var critique = new CritiqueEntry(Path.Combine(workspace, ".forge", "previous-review.md"), new string('a', 64));
            var state = ForgeState.CreateEmpty(HostKind.Codex, RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace)));
            state.Workflow.Phase = ForgePhase.CodeReview;
            state.Review.CritiqueFiles.Add(critique);
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);

            Materializer.Materialize(
                RepositoryPaths.Identify(workspace),
                "# Plan\n\n## Approach\n1. Implement the slice.\n",
                "# Review\n",
                1,
                5,
                new ModelSelection("reviewer", "high"),
                new ModelSelection("builder", "low"),
                amendment: true);

            Assert.Equal([critique], StateStore.Load(workspace).Review.CritiqueFiles);
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

            Assert.False(File.Exists(Path.Combine(workspace, ".forge.lock")));
            Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void ReviewTreeIgnoresRootLockWithLegacyExclude()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = InitializeReviewWorkspace(workspace);
            var fingerprint = ReviewEvidence.TreeFingerprint(workspace, state);

            using var stateLock = ForgeStateLock.Acquire(workspace);
            var untracked = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not inspect untracked review files");

            Assert.Empty(untracked);
            Assert.Equal(fingerprint, ReviewEvidence.TreeFingerprint(workspace, state));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void ReviewPrepareAndDoneIgnoreInternalStateLock()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = InitializeReviewWorkspace(workspace);

            var manifest = ForgeReview.Prepare(new CommandContext("review prepare", workspace, ParsedArgs.Parse(["--full"]), state));

            var prepared = StateStore.Load(workspace);
            Assert.Equal(manifest.TreeFingerprint, prepared.Review.Manifest?.TreeFingerprint);
            var critiquePath = Path.Combine(workspace, ".forge", "code-review.md");
            File.WriteAllText(critiquePath, "Approved.\n");
            File.WriteAllText(critiquePath + ".json", "{\"verdict\":\"APPROVED\",\"coverage\":\"FULL\"}");
            var decision = ReviewDecisionReader.Read(critiquePath, workspace, DispatchStage.Code);
            var approved = StateStore.Update(workspace, prepared, current =>
            {
                current.Dispatch.Id = "review-dispatch";
                current.Dispatch.Stage = DispatchStage.Code;
                current.Dispatch.Pending = false;
                current.Agents.ReviewerIds.Add("reviewer-session");
                current.Agents.LastReviewerDispatchId = current.Dispatch.Id;
                current.Review.Verdict = "APPROVED";
                current.Review.Coverage = "FULL";
                current.Review.CritiqueFile = critiquePath;
                current.Review.VerdictFile = decision.Path;
                current.Review.VerdictHash = decision.Hash;
                current.Review.CritiqueFiles.Add(new CritiqueEntry(critiquePath, Hashing.Sha256File(critiquePath)));
            });

            var done = ForgeWorkflow.Set(WorkflowContext(workspace, approved, "done"));

            Assert.Equal(ForgePhase.Done, done.Workflow.Phase);
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
            Assert.Contains("\"schemaVersion\":1", serialized, StringComparison.Ordinal);
            Assert.Contains("\"host\":\"codex\"", serialized, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":0,\"host\":\"codex\"}")]
    [InlineData("{\"schemaVersion\":2,\"host\":\"codex\"}")]
    [InlineData("{\"schemaVersion\":1,\"host\":\"unknown\"}")]
    public void StateStoreRejectsUnsupportedSchemaWithoutMutation(string json)
    {
        var workspace = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var path = StateStore.StatePath(workspace);
            DurableFiles.WriteAtomic(path, json);
            var before = File.ReadAllText(path);

            var error = Assert.Throws<CliFailure>(() => StateStore.Load(workspace));

            Assert.Equal("unsupported-state-schema", error.Code);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void CleanupRejectsUnsupportedStateWithoutRemovingArtifacts()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true);
            var forge = Path.Combine(workspace, ".forge"); Directory.CreateDirectory(forge); File.WriteAllText(Path.Combine(forge, "PLAN.md"), "user data"); File.WriteAllText(StateStore.StatePath(workspace), "{}");
            var stateBefore = File.ReadAllText(StateStore.StatePath(workspace));

            Assert.Equal(3, new CliApplication().Run(["run", "cleanup", "--workspace", workspace]));

            Assert.Equal(stateBefore, File.ReadAllText(StateStore.StatePath(workspace)));
            Assert.Equal("user data", File.ReadAllText(Path.Combine(forge, "PLAN.md")));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorStageReadsCanonicalChatDraftFromStdin()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var oldIn = Console.In; var oldOut = Console.Out; using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true);
            Console.SetIn(new StringReader("# Chat plan\r\n\r\n## Approach\r\n1. Implement.\r\n")); Console.SetOut(output);

            var exitCode = new CliApplication().Run(["plan", "stage", "--host", "cursor", "--workspace", workspace, "--run-id", "stdin-run", "--model", "reviewer", "--effort", "xhigh", "--cursor-version", "3.15.6", "--observed-model", "Auto", "--waiver-reason", "consent"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"schemaVersion\":2", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"draftText\":\"# Chat plan\\n\\n## Approach\\n1. Implement.\\n\"", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sourcePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
            output.GetStringBuilder().Clear();
            Assert.Equal(1, new CliApplication().Run(["plan", "stage", "--host", "cursor", "--workspace", workspace, "--source", "old.plan.md"]));
            Assert.Contains("unknown option --source", output.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetIn(oldIn); Console.SetOut(oldOut); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorPendingRunStagesReviewsAndFinalizesExactCanonicalPlan()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace, commit: true);
            var repository = RepositoryPaths.Identify(workspace);
            var runId = "cursor-run";
            var source = Path.Combine(workspace, "cursor.plan.md");
            File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\r\n{PendingRuns.ExpectedPreamble(repository, runId)}\r\n# Plan\r\n\r\n## Approach\r\n1. Implement.\r\n");

            var reviewerWaiver = new CursorModelWaiver("reviewer", "gpt-5.6-sol", "xhigh", "3.15.6", "Auto", "user consent", DateTimeOffset.UtcNow.ToString("O"));
            var builderWaiver = new CursorModelWaiver("builder", "gpt-5.6-terra", "medium", "3.15.6", "unavailable", "user consent", DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal($"> **Plan Forge execution gate (advisory):** Before any repository write, run Plan Forge `plan materialize --host cursor --workspace \"{repository.WorkspaceRoot}\" --run-id \"{runId}\"` using the installed launcher. Stop if it does not succeed. Cursor can bypass this instruction; approval enforcement is advisory.", PendingRuns.ExpectedPreamble(repository, runId));
            var staged = PendingRuns.Stage(repository, "# Plan\r\n\r\n## Approach\r\n1. Implement.\r\n", runId, reviewerWaiver);
            var reviewed = PendingRuns.Record(repository, runId, "review-1", "plan", "Reviewer analysis.\nVERDICT: APPROVED\n");
            var ready = PendingRuns.Finalize(repository, runId, builderWaiver);

            Assert.Equal(PendingRunPhase.Reviewing, staged.Phase);
            Assert.Equal(PendingRunPhase.ReviewApproved, reviewed.Phase);
            Assert.Equal(PendingRunPhase.Ready, ready.Phase);
            Assert.Equal("codex", JsonDocument.Parse(JsonSerializer.Serialize(ForgeState.CreateEmpty(), Serialization.ForgeJsonContext.Default.ForgeState)).RootElement.GetProperty("host").GetString());
            Assert.Null(ready.DraftText);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void DoctorReportsRepositoryScopeAndRejectsNonRepository()
    {
        var workspace = CreateTempDirectory(); var nonRepository = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true);
            var doctor = ForgeWorkflow.Doctor(workspace, HostKind.Codex);
            Assert.True(doctor.Git.Ok);
            Assert.Equal(RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace)), doctor.RepositoryScopeId);

            var invalid = ForgeWorkflow.Doctor(nonRepository, HostKind.Codex);
            Assert.False(invalid.Git.Ok);
            Assert.Null(invalid.RepositoryScopeId);
        }
        finally { DeleteDirectory(workspace); DeleteDirectory(nonRepository); }
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("legacy")]
    [InlineData("unowned")]
    public void CursorDoctorRejectsExistingForgeTargetWithoutMutation(string kind)
    {
        var workspace = CreateTempDirectory(); var oldOut = Console.Out; using var output = new StringWriter();
        try
        {
            InitializeRepository(workspace, commit: true);
            var forge = Path.Combine(workspace, ".forge"); Directory.CreateDirectory(forge);
            var marker = Path.Combine(forge, kind + ".txt"); File.WriteAllText(marker, "preserve");
            if (kind == "owned") DurableFiles.WriteJson(StateStore.StatePath(workspace), ForgeState.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace))), Serialization.ForgeJsonContext.Default.ForgeState);
            if (kind == "legacy") File.WriteAllText(StateStore.StatePath(workspace), "{\"version\":2,\"phase\":\"done\"}\n");
            var before = Directory.GetFiles(forge).ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal);
            Console.SetOut(output);

            var exitCode = new CliApplication().Run(["run", "doctor", "--host", "cursor", "--workspace", workspace]);

            Assert.Equal(3, exitCode);
            Assert.Contains("already contains .forge", output.ToString(), StringComparison.Ordinal);
            var after = Directory.GetFiles(forge).ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, new CliApplication().Run(["run", "doctor", "--host", "codex", "--workspace", workspace]));
        }
        finally { Console.SetOut(oldOut); DeleteDirectory(workspace); }
    }

    [Fact]
    public void CursorMaterializeWithoutPendingRunRequiresNewForgeWithoutRepositoryWrites()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var oldOut = Console.Out; using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); Console.SetOut(output);
            var excludePath = Path.Combine(RepositoryPaths.Identify(workspace).GitCommonDir, "info", "exclude"); var excludeBefore = File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;

            var exitCode = new CliApplication().Run(["plan", "materialize", "--host", "cursor", "--workspace", workspace, "--run-id", "missing-run"]);

            Assert.Equal(3, exitCode);
            Assert.Contains("new /forge run", output.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
            Assert.Empty(new GitClient(workspace).Run(["for-each-ref", "--format=%(refname)", "refs/plan-forge"]).Stdout.Trim());
            Assert.Equal(excludeBefore, File.Exists(excludePath) ? File.ReadAllText(excludePath) : null);
        }
        finally { Console.SetOut(oldOut); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorReadyRejectsUnbuildablePlanAndMalformedModels()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "invalid-ready";
            var source = Path.Combine(workspace, "invalid.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "bad/model", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n");
            Assert.Throws<CliFailure>(() => PendingRuns.Finalize(repository, runId, builder));
            Assert.Equal(PendingRunPhase.ReviewApproved, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorFinalizeRejectsPlanWithoutApproach()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "no-approach";
            var source = Path.Combine(workspace, "no-approach.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n");
            Assert.Throws<CliFailure>(() => PendingRuns.Finalize(repository, runId, builder));
            Assert.Equal(PendingRunPhase.ReviewApproved, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("## Approaches\n1. Wrong prefix.\n")]
    [InlineData("## Approach\n1. First.\n\n## Approach\n1. Duplicate.\n")]
    public void CursorFinalizeRequiresOneExactApproachHeading(string planBody)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "exact-approach";
            var source = Path.Combine(workspace, "exact-approach.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n{planBody}");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n");
            Assert.Throws<CliFailure>(() => PendingRuns.Finalize(repository, runId, builder)); Assert.Equal(PendingRunPhase.ReviewApproved, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorPlanReviewRejectsCumulativeLogBeyondMaterializationBound()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "review-bound";
            var source = Path.Combine(workspace, "review-bound.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var waiver = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            PendingRuns.Record(repository, runId, "review-1", "plan", new string('a', 140 * 1024) + "\nVERDICT: REVISE\n");
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            Assert.Throws<CliFailure>(() => PendingRuns.Record(repository, runId, "review-2", "plan", new string('b', 140 * 1024) + "\nVERDICT: APPROVED\n"));
            Assert.Equal(PendingRunPhase.Reviewing, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorReviewCapExtendsOneRoundOnlyWithPersistedAuthorization()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "cap-run";
            var source = Path.Combine(workspace, "cap.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var waiver = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            for (var round = 1; round <= 5; round++)
            {
                PendingRuns.Record(repository, runId, $"review-{round}", "plan", "VERDICT: REVISE\n");
                if (round < 5) PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            }
            Assert.Throws<CliFailure>(() => PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver));
            var extended = PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver, acceptRisk: true, authorizationNote: "User authorizes one additional review round.");
            Assert.Equal(6, extended.ReviewCap); Assert.Single(extended.CapAudits); Assert.Equal(5, extended.CapAudits[0].PreviousCap); Assert.Equal(6, extended.CapAudits[0].NewCap);
            var approved = PendingRuns.Record(repository, runId, "review-6", "plan", "VERDICT: APPROVED\n");
            Assert.Equal(6, approved.ReviewRound); Assert.Equal(PendingRunPhase.ReviewApproved, approved.Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorPlanMaterializesThroughCliRunIdContract()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "cli-materialize";
            var source = Path.Combine(workspace, "cli.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "XHIGH", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "Medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, "# Reviewed chat draft\n\n## Approach\n1. Review-only task.\n", runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            using var output = new StringWriter(); Console.SetOut(output);
            Assert.Equal(0, new CliApplication().Run(["plan", "materialize", "--host", "cursor", "--workspace", workspace, "--run-id", runId]));
            Assert.Contains("\"phase\":\"build\"", output.ToString(), StringComparison.Ordinal);
            var consumed = PendingRuns.Load(repository, runId);
            Assert.Equal(PendingRunPhase.Consumed, consumed.Phase);
            Assert.Null(consumed.Materialization!.PlanText);
            Assert.Equal(Hashing.Sha256Hex(CanonicalText.NormalizePlan(File.ReadAllText(source))), consumed.Materialization.PlanHash);
            Assert.Equal(CanonicalText.NormalizePlan(File.ReadAllText(source)), File.ReadAllText(Path.Combine(workspace, ".forge", "PLAN.md")));
            var state = StateStore.Load(workspace); Assert.Equal("xhigh", state.Models.Reviewer!.Effort); Assert.Equal("medium", state.Models.Builder!.Effort);
        }
        finally { Console.SetOut(oldOut); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("\"schemaVersion\":2,")]
    [InlineData("\"host\":\"cursor\",")]
    public void CursorPendingRunRequiresRawSchemaAndHostAndUsesWirePhases(string token)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "schema-run";
            var source = Path.Combine(workspace, "schema.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var waiver = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n");
            var path = PendingRuns.PathFor(repository, runId); var json = File.ReadAllText(path);
            Assert.Contains("\"phase\":\"review-approved\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("ReviewApproved", json, StringComparison.Ordinal);
            Assert.Contains(token, json, StringComparison.Ordinal);
            File.WriteAllText(path, json.Replace(token, string.Empty, StringComparison.Ordinal));
            var error = Assert.Throws<CliFailure>(() => PendingRuns.Load(repository, runId));
            Assert.Equal("unsupported-state-schema", error.Code);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorRejectsUnfinishedSchemaOnePendingRun()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "legacy-run";
            var path = PendingRuns.PathFor(repository, runId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"host\":\"cursor\",\"workspace\":\"{JsonEncoded(repository.WorkspaceRoot)}\",\"scopeId\":\"{RepositoryPaths.ScopeId(repository)}\",\"runId\":\"{runId}\",\"phase\":\"reviewing\"}}\n");

            var error = Assert.Throws<CliFailure>(() => PendingRuns.Load(repository, runId));

            Assert.Equal("unsupported-state-schema", error.Code);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void HostMismatchFailsBeforeStatusOrAmendmentMutation()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); var forge = Path.Combine(workspace, ".forge"); Directory.CreateDirectory(forge);
            var planPath = Path.Combine(forge, "PLAN.md"); File.WriteAllText(planPath, "original");
            var state = ForgeState.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.Workflow.Phase = ForgePhase.CodeReview; DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
            Assert.Equal(3, new CliApplication().Run(["run", "status", "--host", "codex", "--workspace", workspace]));
            Assert.Throws<CliFailure>(() => Materializer.Materialize(repository, "# Plan\n\n## Approach\n1. Replace.\n", "review\n", 1, 5, new ModelSelection("reviewer", "high"), new ModelSelection("builder", "medium"), true, HostKind.Codex));
            Assert.Equal("original", File.ReadAllText(planPath));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Theory]
    [InlineData("build dispatch")]
    [InlineData("review prepare")]
    public void StatefulCommandsRejectRepositoryScopeMismatchBeforeMutation(string commandName)
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var state = ForgeState.CreateEmpty(HostKind.Codex, "aaaaaaaaaaaaaaaaaaaaaaaa"); state.Workflow.Phase = ForgePhase.Build;
            var statePath = StateStore.StatePath(workspace); DurableFiles.WriteJson(statePath, state, Serialization.ForgeJsonContext.Default.ForgeState); var before = File.ReadAllText(statePath);
            Assert.True(CliCommands.TryGet(commandName, out var definition));
            var error = Assert.Throws<CliFailure>(() => CommandContext.Create(definition, ParsedArgs.Parse(["--workspace", workspace])));
            Assert.Equal("state", error.Code); Assert.Equal(before, File.ReadAllText(statePath));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void CursorPlanReviewRejectsReusedDispatchIdentity()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "fresh-reviewer";
            var source = Path.Combine(workspace, "fresh.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var waiver = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver); PendingRuns.Record(repository, runId, "reviewer-a", "plan", "VERDICT: REVISE\n"); PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            var error = Assert.Throws<CliFailure>(() => PendingRuns.Record(repository, runId, "reviewer-a", "plan", "VERDICT: APPROVED\n"));
            Assert.Equal("state", error.Code); Assert.Equal(PendingRunPhase.Reviewing, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorRequiresFreshBuilderForEveryDispatchWithoutAuthorizationOverride()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var state = ForgeState.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.Workflow.Phase = ForgePhase.Build; state.Models.Builder = new PinnedSelection("builder", "medium");
            AgentState Register(string id, string dispatchId)
            {
                state.Dispatch = new DispatchState { Id = dispatchId, Stage = DispatchStage.Build, Pending = true, Model = "builder", Effort = "medium", TaskNumber = 1 };
                DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
                var args = ParsedArgs.Parse(["--id", id, "--dispatch-id", dispatchId, "--model", "builder", "--effort", "medium"]);
                var result = ForgeWorkflow.RegisterSession(new CommandContext("session builder", workspace, args, state, HostKind.Cursor), ForgeRole.Builder);
                state = StateStore.Load(workspace); return result;
            }
            Assert.Equal("builder-a", Register("builder-a", "dispatch-a").BuilderId);
            Assert.Equal("builder-b", Register("builder-b", "dispatch-b").BuilderId);
            Assert.Throws<CliFailure>(() => Register("builder-a", "dispatch-c"));
            Assert.Equal(["builder-a", "builder-b"], StateStore.Load(workspace).Agents.BuilderIds);
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void CursorRetryIssuesFreshDispatchAndRequiresFreshBuilder()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
            var state = ForgeState.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.Workflow.Phase = ForgePhase.Build; state.Models.Builder = new PinnedSelection("builder", "medium");
            state.Dispatch = new DispatchState { Id = "dispatch-old", Stage = DispatchStage.Build, Pending = true, Model = "builder", Effort = "medium", TaskNumber = 1 };
            state.Agents.BuilderId = "builder-old"; state.Agents.LastBuilderDispatchId = "dispatch-old"; state.Agents.BuilderIds.Add("builder-old"); DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
            var retry = ForgeWorkflow.Dispatch(new CommandContext("build dispatch", workspace, ParsedArgs.Parse(["--stage", "build", "--task-number", "1", "--retry", "--dispatch-id", "dispatch-old"]), state, HostKind.Cursor));
            Assert.NotEqual("dispatch-old", retry.Id); state = StateStore.Load(workspace); Assert.Null(state.Agents.BuilderId); Assert.Null(state.Agents.LastBuilderDispatchId);
            var reused = new CommandContext("session builder", workspace, ParsedArgs.Parse(["--id", "builder-old", "--dispatch-id", retry.Id!, "--model", "builder", "--effort", "medium"]), state, HostKind.Cursor);
            Assert.Throws<CliFailure>(() => ForgeWorkflow.RegisterSession(reused, ForgeRole.Builder));
            var fresh = new CommandContext("session builder", workspace, ParsedArgs.Parse(["--id", "builder-new", "--dispatch-id", retry.Id!, "--model", "builder", "--effort", "medium"]), state, HostKind.Cursor);
            Assert.Equal("builder-new", ForgeWorkflow.RegisterSession(fresh, ForgeRole.Builder).BuilderId);
        }
        finally { DeleteDirectory(workspace); }
    }

    [Theory]
    [InlineData("inherited")]
    [InlineData("AUTO")]
    [InlineData("")]
    [InlineData("manual")]
    public void CursorStageRejectsInvalidModelWaiver(string observed)
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            InitializeRepository(workspace, commit: true);
            var repository = RepositoryPaths.Identify(workspace);
            const string runId = "waiver-run";
            var source = Path.Combine(workspace, "waiver.plan.md");
            File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n");
            var waiver = new CursorModelWaiver("reviewer", "model", "low", "3.15.6", observed, "consent", DateTimeOffset.UtcNow.ToString("O"));

            Assert.Throws<CliFailure>(() => PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("duplicate-marker")]
    [InlineData("wrong-workspace")]
    [InlineData("missing-preamble")]
    [InlineData("wrong-preamble")]
    [InlineData("launcher-path")]
    [InlineData("separated-preamble")]
    [InlineData("wrong-suffix")]
    [InlineData("sensitive")]
    [InlineData("oversize")]
    [InlineData("malformed")]
    [InlineData("multiple")]
    public void CursorMaterializeRejectsInvalidNativePlanContractsWithoutRepositoryWrites(string scenario)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true);
            var repository = RepositoryPaths.Identify(workspace); const string runId = "contract-run";
            var reviewer = new CursorModelWaiver("reviewer", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            var builder = new CursorModelWaiver("builder", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, "# Chat plan\n\n## Approach\n1. Implement.\n", runId, reviewer);
            PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n");
            PendingRuns.Finalize(repository, runId, builder);
            var source = Path.Combine(workspace, scenario == "wrong-suffix" ? "plan.md" : "contract.plan.md");
            var marker = scenario == "missing-marker" ? string.Empty : $"<!-- plan-forge-flow:run={runId};workspace={(scenario == "wrong-workspace" ? "000000000000000000000000" : RepositoryPaths.ScopeId(repository))} -->\n";
            if (scenario == "duplicate-marker") marker += marker;
            if (scenario == "separated-preamble") marker += "Intervening content.\n";
            var preamble = scenario == "missing-preamble" ? string.Empty : PendingRuns.ExpectedPreamble(repository, scenario == "wrong-preamble" ? "other" : runId) + "\n";
            if (scenario == "launcher-path") preamble = preamble.Replace("using the installed launcher.", "using the installed launcher at C:\\forge\\planforge-launcher.ps1.", StringComparison.Ordinal);
            var body = scenario == "sensitive" ? "# Plan\n\napi_key=Abcdefghijklmnop1234_=\n\n## Approach\n1. Implement.\n" : scenario == "oversize" ? "# Plan\n\n## Approach\n1. " + new string('x', 256 * 1024 + 1) : scenario == "malformed" ? "# Plan\n" : "# Plan\n\n## Approach\n1. Implement.\n";
            File.WriteAllText(source, marker + preamble + body);
            if (scenario == "multiple") File.Copy(source, Path.Combine(workspace, "duplicate.plan.md"));
            var excludePath = Path.Combine(repository.GitCommonDir, "info", "exclude"); var excludeBefore = File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;

            Assert.Throws<CliFailure>(() => PendingRuns.BeginMaterialize(repository, runId));

            Assert.Equal(PendingRunPhase.Ready, PendingRuns.Load(repository, runId).Phase);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
            Assert.Empty(new GitClient(workspace).Run(["for-each-ref", "--format=%(refname)", "refs/plan-forge"]).Stdout.Trim());
            Assert.Equal(excludeBefore, File.Exists(excludePath) ? File.ReadAllText(excludePath) : null);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorMaterializeRejectsSymlinkedNativePlanWithoutRepositoryWrites()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "symlink-run";
            var reviewer = new CursorModelWaiver("reviewer", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, "# Chat plan\n\n## Approach\n1. Implement.\n", runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            var target = Path.Combine(workspace, "native-target.txt"); File.WriteAllText(target, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            try { File.CreateSymbolicLink(Path.Combine(workspace, "unsafe.plan.md"), target); }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && error.Message.Contains("required privilege", StringComparison.OrdinalIgnoreCase)) { return; }

            Assert.Throws<CliFailure>(() => PendingRuns.BeginMaterialize(repository, runId));

            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge"))); Assert.Empty(new GitClient(workspace).Run(["for-each-ref", "--format=%(refname)", "refs/plan-forge"]).Stdout.Trim());
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorFinalizeDoesNotRequireNativePlan()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "edit-run";
            var reviewer = new CursorModelWaiver("reviewer", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, "# Chat plan\n\n## Approach\n1. Implement.\n", runId, reviewer); PendingRuns.Record(repository, runId, "dispatch", "plan", "VERDICT: APPROVED\n");

            var ready = PendingRuns.Finalize(repository, runId, builder);

            Assert.Equal(PendingRunPhase.Ready, ready.Phase); Assert.Null(ready.DraftText);
            Assert.Throws<CliFailure>(() => PendingRuns.BeginMaterialize(repository, runId));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("reviewing")]
    [InlineData("revision-required")]
    [InlineData("review-approved")]
    [InlineData("ready")]
    [InlineData("materializing")]
    [InlineData("consumed")]
    [InlineData("abandoned")]
    public void PendingRunPhaseWireNamesAreExplicit(string phase)
    {
        var parsed = Enum.Parse<PendingRunPhase>(string.Concat(phase.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..])));
        Assert.True(Enum.IsDefined(parsed));
    }

    [Fact]
    public void WorkspaceAndRepositoryLocksRejectContentionAndReleaseOnDispose()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true);
            var repository = RepositoryPaths.Identify(workspace);
            using (ForgeStateLock.Acquire(workspace)) Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
            using (RepositoryRunLock.Acquire(repository, HostKind.Codex)) Assert.Throws<CliFailure>(() => RepositoryRunLock.Acquire(repository, HostKind.Cursor));
            using var workspaceLock = ForgeStateLock.Acquire(workspace);
            using var repositoryLock = RepositoryRunLock.Acquire(repository);
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void NoRunIdPlanDispatchResolvesTheUniqueReviewingRun()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "dispatch-run";
            var source = Path.Combine(workspace, "dispatch.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n");
            var waiver = new CursorModelWaiver("reviewer", "model", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            Assert.Equal(runId, PendingRuns.ResolvePlanDispatch(repository, "dispatch-1").RunId);
            Assert.Equal("dispatch-1", PendingRuns.Record(repository, runId, "dispatch-1", "plan", "analysis\nVERDICT: APPROVED\n").ActiveDispatchId);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorReviewRejectsUnsafeIdentitiesAndWrongStageAndAcceptsNewerClient()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "safe-run";
            var source = Path.Combine(workspace, "safe.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n");
            var waiver = new CursorModelWaiver("reviewer", "model", "low", "3.16.0", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, waiver);
            Assert.Throws<CliFailure>(() => PendingRuns.Record(repository, runId, "../escape", "plan", "VERDICT: APPROVED\n"));
            Assert.Throws<CliFailure>(() => PendingRuns.Record(repository, runId, "dispatch-1", "code", "COVERAGE: FULL\nVERDICT: APPROVED\n"));
            Assert.Equal(PendingRunPhase.Reviewing, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("code", "FULL")]
    [InlineData("fix-review", "PARTIAL")]
    public void CursorCodeEvidenceWritesExactCliOwnedDecision(string stage, string coverage)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "evidence-run"; const string dispatchId = "code-dispatch";
            var source = Path.Combine(workspace, "evidence.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "review", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "build", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "plan-dispatch", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder); var materializing = PendingRuns.BeginMaterialize(repository, runId); PendingRuns.Consume(repository, runId);
            var forge = Path.Combine(workspace, ".forge"); Directory.CreateDirectory(forge);
            var state = ForgeState.CreateEmpty(HostKind.Cursor, RepositoryPaths.ScopeId(repository)); state.SourceRun = new RunIdentity("cursor:" + runId, materializing.TransactionId!); state.Models.Reviewer = new PinnedSelection("review", "low"); state.Models.Builder = new PinnedSelection("build", "low"); state.ReviewerGuarantee = new ReviewerGuarantee("advisory"); state.ApprovalGuarantee = new ApprovalGuarantee("advisory"); state.Dispatch.Id = dispatchId; state.Dispatch.Stage = DispatchStages.Parse(stage); state.Dispatch.Pending = true; DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
            var recorded = PendingRuns.RecordCodeEvidence(repository, dispatchId, stage, $"analysis\nCOVERAGE: {coverage}\nVERDICT: APPROVED\n");
            var evidence = Path.Combine(forge, $"cursor-review-{dispatchId}.md");
            Assert.Equal($"analysis\nCOVERAGE: {coverage}\nVERDICT: APPROVED\n", File.ReadAllText(evidence));
            var decision = JsonSerializer.Deserialize(File.ReadAllText(evidence + ".json"), Serialization.ForgeJsonContext.Default.ReviewDecision)!;
            Assert.Equal(coverage, decision.Coverage); Assert.Equal(dispatchId, recorded.ActiveDispatchId);
            Assert.Throws<CliFailure>(() => PendingRuns.RecordCodeEvidence(repository, dispatchId, stage, $"replacement\nCOVERAGE: {coverage}\nVERDICT: APPROVED\n"));
            Assert.Equal($"analysis\nCOVERAGE: {coverage}\nVERDICT: APPROVED\n", File.ReadAllText(evidence));
            Assert.Throws<CliFailure>(() => PendingRuns.RecordCodeEvidence(repository, "stale", stage, "COVERAGE: FULL\nVERDICT: APPROVED\n"));
            Assert.False(File.Exists(Path.Combine(forge, "cursor-review-stale.md")));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("snapshot-written")]
    [InlineData("cursor-plan-written")]
    [InlineData("state-configured")]
    [InlineData("cursor-state-written")]
    [InlineData("materialized-successor-journaled")]
    [InlineData("forge-moved-before-reconcile")]
    [InlineData("forge-artifacts-written")]
    [InlineData("locked-successor-journaled")]
    [InlineData("locked-state-written")]
    [InlineData("build-successor-journaled")]
    [InlineData("build-state-written")]
    [InlineData("baseline-owner-written")]
    [InlineData("baseline-head-written")]
    [InlineData("baseline-worktree-written")]
    public void CursorMaterializationFaultsRemainReplayable(string faultPoint)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "fault-run";
            var source = Path.Combine(workspace, "fault.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement the slice.\n");
            var reviewer = new CursorModelWaiver("reviewer", "review", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "build", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "plan", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", faultPoint);
            var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context));
            var materializing = PendingRuns.Load(repository, runId); Assert.Equal(PendingRunPhase.Materializing, materializing.Phase); Assert.NotNull(materializing.Materialization);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            var result = PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context);
            Assert.Equal("forge-materialized", result.Action); Assert.Equal("build", result.Phase); Assert.Equal(PendingRunPhase.Consumed, PendingRuns.Load(repository, runId).Phase); Assert.True(File.Exists(Path.Combine(workspace, ".forge", "PLAN.md")));
            var state = StateStore.Load(workspace); Assert.Equal(ForgePhase.Build, state.Workflow.Phase);
            foreach (var reference in materializing.Materialization!.ExpectedRefs) Assert.Equal(0, new GitClient(workspace).Run(["rev-parse", "--verify", reference]).ExitCode);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("snapshot-written")]
    [InlineData("forge-artifacts-written")]
    public void CursorMaterializationReplayRefusesConflictingArtifacts(string faultPoint)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "conflict-run";
            var source = Path.Combine(workspace, "conflict.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement the slice.\n");
            var reviewer = new CursorModelWaiver("reviewer", "review", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "build", "low", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "plan", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder); var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", faultPoint); Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            var forge = Path.Combine(workspace, ".forge"); Directory.CreateDirectory(forge); var conflict = Path.Combine(forge, faultPoint == "snapshot-written" ? "user.txt" : "PLAN.md"); File.WriteAllText(conflict, "user-preserved");
            Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context));
            Assert.Equal("user-preserved", File.ReadAllText(conflict)); Assert.Equal(PendingRunPhase.Materializing, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorMaterializationReplayRejectsUnexpectedStagingArtifact()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "staging-inventory";
            var source = Path.Combine(workspace, "staging.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", "cursor-plan-written"); Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            var transaction = PendingRuns.Load(repository, runId).TransactionId!; var staging = Path.Combine(workspace, $".forge.materializing-{transaction}"); var foreign = Path.Combine(staging, "user-file.txt"); File.WriteAllText(foreign, "preserve");
            Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Assert.Equal("preserve", File.ReadAllText(foreign)); Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorMaterializationRejectsMutatedTransactionMarkerBeforeFurtherMutation()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "marker-conflict";
            var source = Path.Combine(workspace, "marker-conflict.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", "forge-artifacts-written"); Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            File.WriteAllText(Path.Combine(workspace, ".forge", ".materialization-transaction"), "conflict\n");
            var stateBefore = File.ReadAllText(StateStore.StatePath(workspace)); var refsBefore = new GitClient(workspace).Run(["for-each-ref", "--format=%(refname):%(objectname)", "refs/plan-forge"]).Stdout; var excludePath = Path.Combine(repository.GitCommonDir, "info", "exclude"); var excludeBefore = File.ReadAllText(excludePath);
            Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context));
            Assert.Equal(stateBefore, File.ReadAllText(StateStore.StatePath(workspace))); Assert.Equal(refsBefore, new GitClient(workspace).Run(["for-each-ref", "--format=%(refname):%(objectname)", "refs/plan-forge"]).Stdout); Assert.Equal(excludeBefore, File.ReadAllText(excludePath));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Fact]
    public void CursorMaterializationReplayRejectsUnboundStateMutation()
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "state-conflict";
            var source = Path.Combine(workspace, "state-conflict.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", "forge-artifacts-written"); Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            var state = StateStore.Load(workspace); state.ModelWaiverAudit.Clear(); DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState); var before = File.ReadAllText(StateStore.StatePath(workspace));
            Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Assert.Equal(before, File.ReadAllText(StateStore.StatePath(workspace))); Assert.Equal(PendingRunPhase.Materializing, PendingRuns.Load(repository, runId).Phase);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    public void CursorMaterializationReplayUsesSnapshotWhenNativePlanChanges(string mutation)
    {
        var workspace = CreateTempDirectory(); var data = CreateTempDirectory(); var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); var previousFault = Environment.GetEnvironmentVariable("FORGE_FAULT_POINT");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(workspace, commit: true); var repository = RepositoryPaths.Identify(workspace); const string runId = "edited-replay";
            var source = Path.Combine(workspace, "edited-replay.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);
            var context = new CommandContext("plan materialize", workspace, ParsedArgs.Parse(["--run-id", runId]), null, HostKind.Cursor);
            Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", "snapshot-written"); Assert.Throws<CliFailure>(() => PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context)); Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", null);
            var snapshot = PendingRuns.Load(repository, runId).Materialization!.PlanText!;
            if (mutation == "edit") File.AppendAllText(source, "Edited after snapshot.\n"); else File.Delete(source);

            var result = PlanForgeFlow.Workflow.Planning.ForgeWorkflow.MaterializeCursorPlan(context);

            Assert.Equal("build", result.Phase); Assert.Equal(snapshot, File.ReadAllText(Path.Combine(workspace, ".forge", "PLAN.md")));
            Assert.Null(PendingRuns.Load(repository, runId).Materialization!.PlanText);
            Assert.Throws<CliFailure>(() => PendingRuns.Invalidate(repository, runId, "changed")); Assert.Throws<CliFailure>(() => PendingRuns.Abandon(repository, runId));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_FAULT_POINT", previousFault); Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(workspace); DeleteDirectory(data); }
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("delete")]
    [InlineData("rename")]
    public void TreeFingerprintChangesForGitStateTransitions(string transition)
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true);
            File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "before\n");
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "add", "tracked.txt"]).ExitCode);
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "commit", "-m", "tracked"]).ExitCode);
            var state = InitializeReviewWorkspace(workspace);
            var before = ReviewEvidence.TreeFingerprint(workspace, state);
            if (transition == "staged") { File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "after\n"); Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "add", "tracked.txt"]).ExitCode); }
            else if (transition == "delete") File.Delete(Path.Combine(workspace, "tracked.txt"));
            else { Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "mv", "tracked.txt", "renamed.txt"]).ExitCode); }
            Assert.NotEqual(before, ReviewEvidence.TreeFingerprint(workspace, state));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void TreeFingerprintChangesForExecutableIndexMode()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); File.WriteAllText(Path.Combine(workspace, "mode.sh"), "echo test\n"); Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "add", "mode.sh"]).ExitCode); Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "commit", "-m", "mode"]).ExitCode);
            var state = InitializeReviewWorkspace(workspace); var before = ReviewEvidence.TreeFingerprint(workspace, state);
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "update-index", "--chmod=+x", "mode.sh"]).ExitCode);
            Assert.NotEqual(before, ReviewEvidence.TreeFingerprint(workspace, state));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void TreeFingerprintChangesForSyntheticGitlink()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); var state = InitializeReviewWorkspace(workspace); var before = ReviewEvidence.TreeFingerprint(workspace, state);
            var head = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "HEAD"]).Stdout.Trim();
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "update-index", "--add", "--cacheinfo", $"160000,{head},vendor/submodule"]).ExitCode);
            Assert.NotEqual(before, ReviewEvidence.TreeFingerprint(workspace, state));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void TreeFingerprintChangesForSymlinkTargetWhenSupported()
    {
        var workspace = CreateTempDirectory();
        try
        {
            InitializeRepository(workspace, commit: true); File.WriteAllText(Path.Combine(workspace, "target-a.txt"), "a"); File.WriteAllText(Path.Combine(workspace, "target-b.txt"), "b");
            try { File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), "target-a.txt"); } catch (UnauthorizedAccessException) { return; } catch (PlatformNotSupportedException) { return; }
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "add", "link.txt"]).ExitCode); Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "commit", "-m", "link"]).ExitCode);
            var state = InitializeReviewWorkspace(workspace); var before = ReviewEvidence.TreeFingerprint(workspace, state); File.Delete(Path.Combine(workspace, "link.txt")); File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), "target-b.txt");
            Assert.NotEqual(before, ReviewEvidence.TreeFingerprint(workspace, state));
        }
        finally { DeleteDirectory(workspace); }
    }

    [Fact]
    public void CleanupIsolatesLinkedWorktreeScopesAndRemovesExcludeAfterLastOwner()
    {
        var main = CreateTempDirectory(); var sibling = CreateTempDirectory();
        try
        {
            InitializeRepository(main, commit: true); Directory.Delete(sibling); Assert.Equal(0, ProcessExecution.Run("git", ["-C", main, "worktree", "add", sibling]).ExitCode);
            var first = RepositoryPaths.Identify(main); var second = RepositoryPaths.Identify(sibling);
            foreach (var repository in new[] { first, second })
            {
                Directory.CreateDirectory(Path.Combine(repository.WorkspaceRoot, ".forge")); File.WriteAllText(Path.Combine(repository.WorkspaceRoot, ".forge", "owned.txt"), "owned");
                var state = ForgeState.CreateEmpty(HostKind.Codex, RepositoryPaths.ScopeId(repository)); DurableFiles.WriteJson(StateStore.StatePath(repository.WorkspaceRoot), state, Serialization.ForgeJsonContext.Default.ForgeState);
                foreach (var name in new[] { "owner", "head-base", "worktree-base" }) Assert.Equal(0, ProcessExecution.Run("git", ["-C", repository.WorkspaceRoot, "update-ref", $"refs/plan-forge/{RepositoryPaths.ScopeId(repository)}/{name}", "HEAD"]).ExitCode);
            }
            var exclude = Path.Combine(first.GitCommonDir, "info", "exclude"); File.WriteAllText(exclude, "user-rule\n# >>> plan-forge-flow (managed) >>>\n.forge/\n# <<< plan-forge-flow (managed) <<<\n");
            Materializer.Cleanup(main);
            Assert.False(Directory.Exists(Path.Combine(main, ".forge"))); Assert.True(Directory.Exists(Path.Combine(sibling, ".forge"))); Assert.Contains("plan-forge-flow", File.ReadAllText(exclude));
            Materializer.Cleanup(sibling);
            Assert.False(Directory.Exists(Path.Combine(sibling, ".forge"))); Assert.DoesNotContain("plan-forge-flow", File.ReadAllText(exclude)); Assert.Contains("user-rule", File.ReadAllText(exclude));
        }
        finally { DeleteDirectory(main); DeleteDirectory(sibling); }
    }

    [Fact]
    public void CursorMaterializationHoldsRepositoryLockUntilOwnerRefExists()
    {
        var main = CreateTempDirectory(); var sibling = CreateTempDirectory(); var data = CreateTempDirectory(); var hold = Path.Combine(data, "after-exclude.hold");
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA"); Process? child = null;
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data); InitializeRepository(main, commit: true); Directory.Delete(sibling); Assert.Equal(0, ProcessExecution.Run("git", ["-C", main, "worktree", "add", sibling]).ExitCode);
            var repository = RepositoryPaths.Identify(main); var siblingRepository = RepositoryPaths.Identify(sibling); const string runId = "exclude-owner-race";
            Directory.CreateDirectory(Path.Combine(sibling, ".forge")); DurableFiles.WriteJson(StateStore.StatePath(sibling), ForgeState.CreateEmpty(HostKind.Codex, RepositoryPaths.ScopeId(siblingRepository)), Serialization.ForgeJsonContext.Default.ForgeState);
            foreach (var name in new[] { "owner", "head-base", "worktree-base" }) Assert.Equal(0, ProcessExecution.Run("git", ["-C", sibling, "update-ref", $"refs/plan-forge/{RepositoryPaths.ScopeId(siblingRepository)}/{name}", "HEAD"]).ExitCode);
            var source = Path.Combine(main, "race.plan.md"); File.WriteAllText(source, $"<!-- plan-forge-flow:run={runId};workspace={RepositoryPaths.ScopeId(repository)} -->\n{PendingRuns.ExpectedPreamble(repository, runId)}\n# Plan\n\n## Approach\n1. Implement.\n");
            var reviewer = new CursorModelWaiver("reviewer", "reviewer", "xhigh", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O")); var builder = new CursorModelWaiver("builder", "builder", "medium", "3.15.6", "Auto", "consent", DateTimeOffset.UtcNow.ToString("O"));
            PendingRuns.Stage(repository, File.ReadAllText(source), runId, reviewer); PendingRuns.Record(repository, runId, "review", "plan", "VERDICT: APPROVED\n"); PendingRuns.Finalize(repository, runId, builder);

            var assembly = Path.Combine(AppContext.BaseDirectory, "planforge.dll"); var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { assembly, "plan", "materialize", "--host", "cursor", "--workspace", main, "--run-id", runId }) start.ArgumentList.Add(argument);
            start.Environment["FORGE_PLUGIN_DATA"] = data; start.Environment["FORGE_TEST_HOLD_AFTER_EXCLUDE"] = hold; child = Process.Start(start)!;
            for (var attempt = 0; attempt < 1000 && !File.Exists(hold); attempt++) Thread.Sleep(10);
            Assert.True(File.Exists(hold)); Assert.Throws<CliFailure>(() => Materializer.Cleanup(sibling)); Assert.True(Directory.Exists(Path.Combine(sibling, ".forge")));
            File.Delete(hold); Assert.True(child.WaitForExit(30000)); var stdout = child.StandardOutput.ReadToEnd(); var stderr = child.StandardError.ReadToEnd(); Assert.True(child.ExitCode == 0, stdout + stderr); child.Dispose(); child = null;
            Assert.Equal(0, new GitClient(main).Run(["rev-parse", "--verify", $"refs/plan-forge/{RepositoryPaths.ScopeId(repository)}/owner"]).ExitCode);
            Materializer.Cleanup(sibling); var exclude = Path.Combine(repository.GitCommonDir, "info", "exclude"); Assert.Contains("plan-forge-flow", File.ReadAllText(exclude));
        }
        finally
        {
            if (File.Exists(hold)) File.Delete(hold);
            if (child is not null) { try { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); } catch { } child.Dispose(); }
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData); DeleteDirectory(main); DeleteDirectory(sibling); DeleteDirectory(data);
        }
    }

    [Fact]
    public void ChildProcessLockReleaseAllowsBothLocksToBeReacquired()
    {
        var workspace = CreateTempDirectory(); var marker = Path.Combine(workspace, "held.marker"); Process? child = null;
        try
        {
            InitializeRepository(workspace, commit: true); var assembly = Path.Combine(AppContext.BaseDirectory, "planforge.dll");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false }; start.ArgumentList.Add(assembly); start.ArgumentList.Add("run"); start.ArgumentList.Add("doctor"); start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace); start.Environment["FORGE_TEST_HOLD_LOCK"] = marker;
            child = Process.Start(start)!;
            for (var attempt = 0; attempt < 100 && !File.Exists(marker); attempt++) Thread.Sleep(25);
            Assert.True(File.Exists(marker)); var repository = RepositoryPaths.Identify(workspace);
            Assert.Throws<CliFailure>(() => RepositoryRunLock.Acquire(repository)); Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
            child.Kill(entireProcessTree: true); child.WaitForExit(); child.Dispose(); child = null;
            using var repositoryLock = RepositoryRunLock.Acquire(repository); using var stateLock = ForgeStateLock.Acquire(workspace);
        }
        finally { if (child is not null) { try { child.Kill(entireProcessTree: true); child.WaitForExit(); } catch { } child.Dispose(); } DeleteDirectory(workspace); }
    }

    [Fact]
    public void OwnedJsonSchemasAreStrictAndEnumWireNamesAreStable()
    {
        var materialization = JsonSerializer.Serialize(
            new MaterializationRequest("# Review\n", 1, 5, new ModelSelection("reviewer", "high"), new ModelSelection("builder", "low")),
            Serialization.ForgeJsonContext.Default.MaterializationRequest);
        var manifest = JsonSerializer.Serialize(new ReviewManifest { Version = 4, Generation = "v4", Coverage = "FULL" }, Serialization.ForgeJsonContext.Default.ReviewManifest);
        var decision = JsonSerializer.Serialize(new ReviewDecision("APPROVED", "FULL"), Serialization.ForgeJsonContext.Default.ReviewDecision);
        var pending = JsonSerializer.Serialize(new PendingPlanDocument(ForgeState.SchemaVersion, HostKind.Codex, "workspace", "# Plan\n"), Serialization.ForgeJsonContext.Default.PendingPlanDocument);

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
    public void WorkspacePathComparisonMatchesTheHostFileSystem()
        => Assert.Equal(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, WorkspacePathPolicy.Comparison);

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

    [Fact]
    public void WindowsLauncherForwardsPowerShellPipelineInput()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pluginRoot = CreateTempDirectory();
        try
        {
            var binDirectory = Path.Combine(pluginRoot, "bin");
            var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var executableDirectory = Path.Combine(binDirectory, rid);
            Directory.CreateDirectory(executableDirectory);
            var launcher = Path.Combine(binDirectory, "planforge-launcher.ps1");
            File.Copy(Path.Combine(FindPluginRoot(), "bin", "planforge-launcher.ps1"), launcher);
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "findstr.exe"),
                      Path.Combine(executableDirectory, "planforge.exe"));

            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"'pipeline-sentinel' | & '{launcher}' pipeline-sentinel");

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, error);
            Assert.Contains("pipeline-sentinel", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(pluginRoot);
        }
    }

    private static int RunMaterialize(string workspace, bool amendment = false, string inputPrefix = "")
    {
        var oldIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(inputPrefix + JsonSerializer.Serialize(
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

    private static ForgeState InitializeReviewWorkspace(string workspace)
    {
        InitializeRepository(workspace, commit: true);
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
        File.WriteAllText(Path.Combine(workspace, ".git", "info", "exclude"), "# >>> plan-forge-flow (managed) >>>\n.forge/\n# <<< plan-forge-flow (managed) <<<\n");
        var state = ForgeState.CreateEmpty();
        state.RepositoryScopeId = RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace));
        state.Workflow.Phase = ForgePhase.CodeReview;
        DurableFiles.WriteJson(StateStore.StatePath(workspace), state, Serialization.ForgeJsonContext.Default.ForgeState);
        foreach (var reference in new[] { $"refs/plan-forge/{state.RepositoryScopeId}/head-base", $"refs/plan-forge/{state.RepositoryScopeId}/worktree-base" })
        {
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "update-ref", reference, "HEAD"]).ExitCode);
        }

        return state;
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
