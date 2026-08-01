using System.Text.Json.Nodes;
using PlanForgeFlow;
using Xunit;

namespace PlanForgeFlow.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void CanonicalPlanUsesOwnedLfDomainAndStableHash()
    {
        var source = "\uFEFF<!-- plan-forge-flow:owned -->\r\n# Plan\r\n\r\n";

        var normalized = CanonicalText.NormalizePlan(source);

        Assert.Equal("<!-- plan-forge-flow:owned -->\n# Plan\n", normalized);
        Assert.Equal(Hashing.Sha256Hex(normalized), Hashing.Sha256Hex(normalized));
        Assert.Throws<CliFailure>(() => CanonicalText.NormalizePlan("# unowned\n"));
    }

    [Fact]
    public void Base64UrlAndNonceRoundTripWithExpectedShape()
    {
        const string value = "plan / рус中文";

        var encoded = Hashing.Base64UrlEncode(value);

        Assert.Equal(value, Hashing.Base64UrlDecode(encoded));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Hashing.Nonce());
    }

    [Fact]
    public void ModelSelectionValidationDoesNotUseCatalogAndRejectsUltra()
    {
        var selection = ModelSelections.Validate("reviewer", "gpt-5.6-sol", "HIGH");

        Assert.Equal("gpt-5.6-sol", selection["model"]!.GetValue<string>());
        Assert.Equal("high", selection["effort"]!.GetValue<string>());
        Assert.Throws<CliFailure>(() => ModelSelections.Validate("reviewer", "gpt-5.6-sol", "ultra"));
        Assert.Throws<CliFailure>(() => ModelSelections.Validate("reviewer", "bad model", "high"));
    }

    [Fact]
    public void ResumeEnvelopeIsNestedV3WithoutCatalogAndRejectsV2()
    {
        var plan = "<!-- plan-forge-flow:owned -->\n# Plan\n";
        var reviewLog = "<!-- plan-forge-flow:owned -->\n# Review\n";
        var repository = new RepositoryIdentity(Path.Combine(Path.GetTempPath(), "planforge-repo"), Path.Combine(Path.GetTempPath(), "planforge-repo", ".git"));
        var capture = new SessionCapture(2, "session", repository.WorkspaceRoot, "turn", Path.Combine(repository.WorkspaceRoot, "transcript.jsonl"), "model", "plan", "high", true, "plan", null, "same-context", false, null, null, "2026-08-01T00:00:00Z");
        var envelope = ResumeEnvelope.Create(plan, reviewLog, 1, 5,
            new JsonObject { ["model"] = "reviewer-model", ["effort"] = "high" },
            new JsonObject { ["model"] = "builder-model", ["effort"] = "low" },
            repository, capture, 1);

        var wrapper = ResumeEnvelope.Build(plan, envelope);
        var parsed = ResumeEnvelope.Parse(wrapper);

        Assert.Equal(3, parsed.Envelope["version"]!.GetValue<int>());
        Assert.Equal(Hashing.Sha256Hex(parsed.HumanPlan), parsed.Envelope["plan"]!["humanPlanHash"]!.GetValue<string>());
        Assert.DoesNotContain("catalogObservation", parsed.Envelope.Select(item => item.Key));
        Assert.Equal("forge-" + Hashing.Sha256Hex($"session\n{Path.Combine(repository.WorkspaceRoot, "transcript.jsonl")}\nturn\n{parsed.Envelope["nonce"]!.GetValue<string>()}\n{Hashing.Sha256Hex(plan)}")[..48], parsed.Envelope["origin"]!["itemId"]!.GetValue<string>());
        Assert.Contains("plan-forge-resume:v3", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializerWritesOwnedArtifactsStateAndRejectsNonceReplay()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            var repository = new RepositoryIdentity(workspace, Path.Combine(workspace, ".git"));
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            File.WriteAllText(transcript, "transcript");
            var plan = "<!-- plan-forge-flow:owned -->\n## Approach\n1. Implement the slice.\n";
            var review = "<!-- plan-forge-flow:owned -->\n# Review\n";
            var capture = new SessionCapture(2, "session", workspace, "turn", transcript, "model", "plan", "high", true, "plan", null, "same-context", false, null, null, "2026-08-01T00:00:00Z");
            var envelope = ResumeEnvelope.Create(plan, review, 1, 5,
                new JsonObject { ["model"] = "reviewer-model", ["effort"] = "high" },
                new JsonObject { ["model"] = "builder-model", ["effort"] = "low" },
                repository, capture, 1);
            var wrapper = ResumeEnvelope.Build(plan, envelope);

            var materialized = Materializer.Materialize(repository, wrapper);

            Assert.Equal("forge-materialized", materialized["action"]!.GetValue<string>());
            Assert.Equal(ForgeState.Version, StateStore.Load(workspace).ToJson()["version"]!.GetValue<int>());
            Assert.Equal("<!-- plan-forge-flow:owned -->", File.ReadLines(Path.Combine(workspace, "PLAN.md")).First());
            Assert.True(File.Exists(RepositoryPaths.LastNoncePath(repository)));
            Assert.Throws<CliFailure>(() => Materializer.Materialize(repository, wrapper));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void StateStoreCreatesV4GroupsAndRejectsPriorState()
    {
        var workspace = CreateTempDirectory();
        try
        {
            var state = StateStore.CreateEmpty("hash");
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state);

            var serialized = state.ToJson();
            Assert.Equal(ForgeState.Version, serialized["version"]!.GetValue<int>());
            foreach (var group in new[] { "workflow", "models", "agents", "dispatch", "baselines", "review", "approval", "materialization" })
            {
                Assert.IsType<JsonObject>(serialized[group]);
            }
            Assert.Equal(["builder", "reviewer"], serialized["models"]!.AsObject().Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray());

            serialized["models"]!["catalogObservation"] = new JsonObject { ["generatedAt"] = "old" };
            DurableFiles.WriteJson(StateStore.StatePath(workspace), serialized);
            var catalogStateError = Assert.Throws<CliFailure>(() => StateStore.Load(workspace));
            Assert.Equal("state", catalogStateError.Code);

            serialized["models"]!.AsObject().Remove("catalogObservation");
            serialized["version"] = 3;
            serialized["generation"] = "v2";
            DurableFiles.WriteJson(StateStore.StatePath(workspace), serialized);
            var v3Error = Assert.Throws<CliFailure>(() => StateStore.Load(workspace));
            Assert.Equal("state", v3Error.Code);

            var old = new JsonObject { ["version"] = 2, ["phase"] = "materialized" };
            DurableFiles.WriteJson(StateStore.StatePath(workspace), old);
            var error = Assert.Throws<CliFailure>(() => StateStore.Load(workspace));
            Assert.Equal("state", error.Code);
            Assert.Equal(3, error.ExitCode);
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void ExistingStateLockBlocksAnotherOperation()
    {
        var workspace = CreateTempDirectory();
        var forge = Path.Combine(workspace, ".forge");
        var gitCommon = Path.Combine(workspace, ".git");
        Directory.CreateDirectory(forge);
        Directory.CreateDirectory(gitCommon);
        try
        {
            var stateLockPath = Path.Combine(forge, "lock");
            File.WriteAllText(stateLockPath, "another-operation");
            Assert.Throws<CliFailure>(() => ForgeStateLock.Acquire(workspace));
        }
        finally
        {
            DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void FixRoundRequiresBuilderCompletionBeforeReviewerDispatch()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            var capture = new SessionCapture(2, "session", workspace, "turn", Path.Combine(workspace, "transcript.jsonl"), "model", "default", "medium", true, "default", null, "other", false, null, null, DateTimeOffset.UtcNow.ToString("O"));
            DurableFiles.WriteJson(RepositoryPaths.SessionCapturePath(workspace), capture.ToJson());
            var state = StateStore.CreateEmpty("hash");
            state.Workflow.Phase = ForgePhase.CodeReview;
            state.Models.Reviewer = new PinnedSelection("reviewer-model", "high");
            state.Models.Builder = new PinnedSelection("builder-model", "low");
            DurableFiles.WriteJson(StateStore.StatePath(workspace), state);

            using var output = new StringWriter();
            Console.SetOut(output);
            var firstCode = new CliApplication().Run(["build", "dispatch", "--workspace", workspace, "--stage", "fix-build", "--model", "builder-model", "--effort", "low"]);
            Assert.True(firstCode == 0, output.ToString());
            var firstDispatch = JsonNode.Parse(output.ToString())!["data"]!["id"]!.GetValue<string>();
            output.GetStringBuilder().Clear();
            Assert.Equal(0, new CliApplication().Run(["session", "builder", "--workspace", workspace, "--id", "builder-1", "--dispatch-id", firstDispatch, "--model", "builder-model", "--effort", "low"]));
            output.GetStringBuilder().Clear();
            Assert.Equal(0, new CliApplication().Run(["build", "complete", "--workspace", workspace, "--dispatch-id", firstDispatch]));
            output.GetStringBuilder().Clear();
            Assert.Equal(3, new CliApplication().Run(["build", "dispatch", "--workspace", workspace, "--stage", "fix-review", "--model", "reviewer-model", "--effort", "high"]));
            Assert.Equal(DispatchStage.FixBuild, StateStore.Load(workspace).Dispatch.Stage);
            var capped = StateStore.Load(workspace);
            capped.Review.FixRound = 3;
            capped.Review.Verdict = "REVISE";
            DurableFiles.WriteJson(StateStore.StatePath(workspace), capped);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, new CliApplication().Run(["run", "set", "--workspace", workspace, "--key", "max-fix-rounds", "--value", "4", "--accept-risk", "--authorization-note", "explicit cap extension"]));
            Assert.Equal(4, StateStore.Load(workspace).Workflow.MaxFixRounds);
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
    public void FixReviewVerdictAcceptsPendingFixReviewDispatch()
    {
        var dispatch = new DispatchState { Id = "fix-review-dispatch", Pending = true, Stage = DispatchStage.FixReview };

        Assert.Equal(DispatchStage.FixReview, DispatchStages.RequirePendingReviewVerdict(dispatch, "fix-review"));
        Assert.Equal(DispatchStage.FixReview, DispatchStages.RequirePendingReviewVerdict(dispatch, null));
        Assert.Throws<CliFailure>(() => DispatchStages.RequirePendingReviewVerdict(dispatch with { Stage = DispatchStage.FixBuild }, "fix-build"));
    }

    [Fact]
    public void PromptAndSensitiveInputClassifiersFailClosedOnRiskyValues()
    {
        Assert.Equal("same-context", PromptClassifier.Classify("Implement the plan.\r\n"));
        Assert.Equal("same-context-embedded", PromptClassifier.Classify("PLEASE IMPLEMENT THIS PLAN:\n# Plan"));
        Assert.Equal("clear-context", PromptClassifier.Classify(PromptClassifier.ClearContextPrefix + "\n\n<proposed_plan>"));
        Assert.True(SensitiveInput.IsSensitivePath("src/.env.production"));
        Assert.True(SensitiveInput.IsSensitivePath("config/appsettings.Production.json"));
        Assert.True(SensitiveInput.IsSensitivePath("keys/service.p12"));
        Assert.True(SensitiveInput.IsSensitiveContent("-----BEGIN RSA PRIVATE KEY-----"));
        Assert.True(SensitiveInput.IsSensitiveContent("internal static readonly string Token = \"Abcdef1234567890-_xy\";"));
        Assert.True(SensitiveInput.IsSensitiveContent("public const string ApiKey = \"Abcdef1234567890-_xy\";"));
        Assert.False(SensitiveInput.IsSensitivePath("src/Program.cs"));
    }

    [Fact]
    public void HookCaptureIsVersionTwoAndMalformedInputIsFailOpen()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            File.WriteAllText(transcript, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"session\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"turn\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n");
            var input = new JsonObject
            {
                ["cwd"] = workspace,
                ["session_id"] = "session",
                ["turn_id"] = "turn",
                ["transcript_path"] = transcript,
                ["permission_mode"] = "default",
                ["prompt"] = "Implement the plan.",
                ["reasoning_effort"] = "medium",
            }.ToJsonString();

            Assert.Equal(0, HookService.Run(input));
            var capture = SessionCapture.Read(workspace);
            Assert.NotNull(capture);
            Assert.Equal(2, capture!.Version);
            Assert.Equal("default", capture.CollaborationMode);
            Assert.Equal(0, HookService.Run("not-json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void HookEmitsUnwrappedNativeContextOnlyForTranscriptBoundImplementation()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            Console.SetOut(output);
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            var repository = new RepositoryIdentity(workspace, Path.Combine(workspace, ".git"));
            var capture = new SessionCapture(2, "session", workspace, "plan-turn", transcript, "model", "plan", "high", true, "plan", null, "same-context", false, null, null, DateTimeOffset.UtcNow.ToString("O"));
            var plan = "<!-- plan-forge-flow:owned -->\n## Approach\n1. Implement the slice.\n";
            var wrapper = ResumeEnvelope.Build(plan, ResumeEnvelope.Create(plan, "<!-- plan-forge-flow:owned -->\n# Review\n", 1, 5,
                new JsonObject { ["model"] = "reviewer-model", ["effort"] = "high" },
                new JsonObject { ["model"] = "builder-model", ["effort"] = "low" }, repository, capture, 1));
            File.WriteAllText(transcript,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"session\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"plan-turn\",\"collaboration_mode\":{\"mode\":\"plan\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\\n" + JsonEncoded(wrapper) + "</proposed_plan>\"}]}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"impl-turn\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n" +
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Implement the plan.\"}]}}\n");
            var input = new JsonObject { ["cwd"] = workspace, ["session_id"] = "session", ["turn_id"] = "impl-turn", ["transcript_path"] = transcript, ["prompt"] = "Implement the plan.", ["permission_mode"] = "default" }.ToJsonString();

            Assert.Equal(0, HookService.Run(input));
            var root = JsonNode.Parse(output.ToString())!.AsObject();
            Assert.NotNull(root["hookSpecificOutput"]);
            Assert.Null(root["ok"]);
            Assert.True(SessionCapture.Read(workspace)!.ImplementationCandidate);
        }
        finally
        {
            Console.SetOut(oldOut);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previous);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void GroupedCliReturnsStableJsonErrorEnvelope()
    {
        var oldOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = new CliApplication().Run(["unknown", "command"]);
            var json = JsonNode.Parse(output.ToString())!.AsObject();

            Assert.Equal(1, exitCode);
            Assert.False(json["ok"]!.GetValue<bool>());
            Assert.Equal("unknown command 'unknown command'", json["error"]!["message"]!.GetValue<string>());
            Assert.Equal("usage", json["error"]!["code"]!.GetValue<string>());
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    [Fact]
    public void ApprovalIssueDoesNotLaunchCodexOrEmbedCatalog()
    {
        var workspace = CreateTempDirectory();
        var data = CreateTempDirectory();
        var previousData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var previousCodex = Environment.GetEnvironmentVariable("FORGE_CODEX_PATH");
        var oldOut = Console.Out;
        var oldIn = Console.In;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
            Environment.SetEnvironmentVariable("FORGE_CODEX_PATH", Path.Combine(workspace, "missing-codex"));
            Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
            var transcript = Path.Combine(workspace, "transcript.jsonl");
            var capture = new SessionCapture(2, "session", workspace, "turn", transcript, "model", "plan", "high", true, "plan", null, "same-context", false, null, null, DateTimeOffset.UtcNow.ToString("O"));
            DurableFiles.WriteJson(RepositoryPaths.SessionCapturePath(workspace), capture.ToJson());
            var request = new JsonObject
            {
                ["humanPlan"] = "<!-- plan-forge-flow:owned -->\n# Plan\n",
                ["reviewLog"] = "<!-- plan-forge-flow:owned -->\n# Review\n",
                ["completedReviewRounds"] = 1,
                ["maxRounds"] = 5,
                ["reviewer"] = new JsonObject { ["model"] = "reviewer-model", ["effort"] = "high" },
                ["builder"] = new JsonObject { ["model"] = "builder-model", ["effort"] = "low" },
            };
            Console.SetOut(output);
            Console.SetIn(new StringReader(request.ToJsonString()));

            var exitCode = new CliApplication().Run(["approval", "issue", "--workspace", workspace]);
            var root = JsonNode.Parse(output.ToString())!.AsObject();
            var proposed = root["data"]!["proposedPlanOutput"]!.GetValue<string>();

            Assert.Equal(0, exitCode);
            Assert.Contains("plan-forge-resume:v3", proposed, StringComparison.Ordinal);
            Assert.DoesNotContain("catalogObservation", proposed, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetIn(oldIn);
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", previousData);
            Environment.SetEnvironmentVariable("FORGE_CODEX_PATH", previousCodex);
            DeleteDirectory(workspace);
            DeleteDirectory(data);
        }
    }

    [Fact]
    public void RemovedCatalogCommandsAreRejectedAndDoctorHasNoCodexField()
    {
        var workspace = CreateTempDirectory();
        var oldOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var catalogExit = new CliApplication().Run(["catalog", "models", "--workspace", workspace]);
            var catalogError = JsonNode.Parse(output.ToString())!.AsObject();
            Assert.Equal(1, catalogExit);
            Assert.Equal("usage", catalogError["error"]!["code"]!.GetValue<string>());

            output.GetStringBuilder().Clear();
            var doctorExit = new CliApplication().Run(["run", "doctor", "--workspace", workspace]);
            var doctor = JsonNode.Parse(output.ToString())!.AsObject();
            Assert.Equal(0, doctorExit);
            Assert.Null(doctor["data"]!["codex"]);
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
        var path = Path.Combine(CreateTempDirectory(), "transcript.jsonl");
        try
        {
            File.WriteAllText(path, "{not-json}\n");

            var error = Assert.Throws<CliFailure>(() => TranscriptReader.ReadDocument(path));

            Assert.Equal("state", error.Code);
            Assert.Equal(3, error.ExitCode);
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReleaseLayoutDeclaresSixRidsLfsAndRidAwareLauncher()
    {
        var pluginRoot = FindPluginRoot();
        var package = File.ReadAllText(Path.Combine(pluginRoot, "build", "package.ps1"));
        var attributes = File.ReadAllText(Path.Combine(Directory.GetParent(pluginRoot)!.Parent!.FullName, ".gitattributes"));
        var hooks = File.ReadAllText(Path.Combine(pluginRoot, "hooks", "hooks.json"));

        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" }) Assert.Contains(rid, package, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", package, StringComparison.Ordinal);
        Assert.Contains("plugins/plan-forge-flow/bin/**/planforge", attributes, StringComparison.Ordinal);
        Assert.Contains("planforge-launcher.sh", hooks, StringComparison.Ordinal);
        Assert.Contains("planforge-launcher.ps1", hooks, StringComparison.Ordinal);
        Assert.Contains("bin/$rid", package, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(pluginRoot, "bin", "planforge-launcher.sh")));
        Assert.True(File.Exists(Path.Combine(pluginRoot, "bin", "planforge-launcher.ps1")));
        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            var executable = rid.StartsWith("win-", StringComparison.Ordinal) ? "planforge.exe" : "planforge";
            Assert.True(File.Exists(Path.Combine(pluginRoot, "bin", rid, executable)), $"Missing {rid} executable");
        }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(pluginRoot, "src"), "*.csproj", SearchOption.AllDirectories).Length);
    }

    private static string JsonEncoded(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

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
                Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
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
