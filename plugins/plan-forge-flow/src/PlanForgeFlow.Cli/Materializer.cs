using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal static class Materializer
{
    private const string ExcludeBegin = "# >>> plan-forge-flow (managed) >>>";
    private const string ExcludeEnd = "# <<< plan-forge-flow (managed) <<<";
    private const string ExcludeBlock = ExcludeBegin + "\n.forge/\n" + ExcludeEnd;
    private static readonly IReadOnlyList<string> ReviewEvidenceFiles = ["pre-existing.patch", "in-run.patch", "untracked-review.patch", "changed-files.txt"];

    public static JsonObject Materialize(RepositoryIdentity repository, string wrapper, bool purgeReplayLedger = false)
    {
        if (Encoding.UTF8.GetByteCount(wrapper) > 4 * 1024 * 1024) throw new CliFailure("state", "approval wrapper exceeds the size bound", 3);
        var parsed = ResumeEnvelope.Parse(wrapper);
        var envelopeRepository = parsed.Envelope["repository"]!.AsObject();
        if (!string.Equals(envelopeRepository["workspaceRoot"]?.GetValue<string>(), repository.WorkspaceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !string.Equals(envelopeRepository["gitCommonDir"]?.GetValue<string>(), repository.GitCommonDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new CliFailure("state", "approved envelope belongs to a different repository", 3);
        }

        var nonce = parsed.Envelope["nonce"]!.GetValue<string>();
        var plan = parsed.Envelope["plan"]!.AsObject();
        var selections = parsed.Envelope["selections"]!.AsObject();
        var planRevision = plan["planRevision"]!.GetValue<int>();
        var transactionId = Hashing.Sha256Hex(nonce + "\n" + plan["humanPlanHash"]!.GetValue<string>())[..32];
        using var repositoryLock = RepositoryMaterializationLock.Acquire(repository);
        CheckReplayLedger(repository, purgeReplayLedger, transactionId);
        var journalDirectory = RepositoryPaths.JournalDirectory();
        var tombstoneDirectory = RepositoryPaths.TombstoneDirectory();
        EnsureSafeDirectory(journalDirectory);
        EnsureSafeDirectory(tombstoneDirectory);
        Directory.CreateDirectory(journalDirectory);
        Directory.CreateDirectory(tombstoneDirectory);
        EnsureSafeDirectory(journalDirectory);
        EnsureSafeDirectory(tombstoneDirectory);
        var tombstone = Path.Combine(tombstoneDirectory, Hashing.Sha256Hex(nonce) + ".json");
        EnsureRegularLedgerEntry(tombstone, "nonce tombstone");
        if (File.Exists(tombstone)) throw new CliFailure("state", "approval nonce has already been acknowledged", 3);

        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        EnsureSafeDirectory(forgeDirectory);
        Directory.CreateDirectory(forgeDirectory);
        EnsureSafeDirectory(forgeDirectory);
        ValidateManagedExclude(repository);
        var planPath = Path.Combine(repository.WorkspaceRoot, "PLAN.md");
        var reviewPath = Path.Combine(repository.WorkspaceRoot, "PLAN-REVIEW-LOG.md");
        EnsureOwnedArtifact(planPath);
        EnsureOwnedArtifact(reviewPath);
        var highestRevision = HighestCommittedRevision(tombstoneDirectory, repository);
        if (highestRevision >= planRevision) throw new CliFailure("state", $"approval plan revision {planRevision} is not newer than committed revision {highestRevision}", 3);
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        JsonObject? existingState = null;
        if (File.Exists(StateStore.StatePath(repository.WorkspaceRoot))) existingState = StateStore.Load(repository.WorkspaceRoot);
        var journalPath = Path.Combine(journalDirectory, transactionId + ".json");
        JsonObject? journal = null;
        if (File.Exists(journalPath)) journal = LoadAndValidateJournal(journalPath, repository, transactionId, nonce, planRevision, parsed.HumanPlan, plan["reviewLog"]!.GetValue<string>());
        var continuing = existingState is not null &&
                         string.Equals(existingState["materialization"]!["transactionId"]?.GetValue<string>(), transactionId, StringComparison.Ordinal) &&
                         string.Equals(existingState["approval"]!["nonce"]?.GetValue<string>(), nonce, StringComparison.Ordinal);
        var derivedAmendment = existingState is not null && !continuing;
        var amendment = journal?["amendment"]?.GetValue<bool>() ?? derivedAmendment;
        if (journal is not null && !continuing && amendment != derivedAmendment) throw new CliFailure("state", "materialization journal amendment state does not match the repository state", 3);
        if (amendment && existingState is null) throw new CliFailure("state", "amendment recovery requires the prior materialized state", 3);
        if (continuing) ValidateContinuingState(existingState!, plan, nonce, transactionId, planRevision, amendment);
        else if (amendment) ValidateAmendment(existingState!, parsed.HumanPlan, planRevision);

        var planStage = journal?["expected"]?["plan"]?["stagedPath"]?.GetValue<string>() ?? Path.Combine(journalDirectory, transactionId + ".PLAN.md.stage");
        var reviewStage = journal?["expected"]?["review"]?["stagedPath"]?.GetValue<string>() ?? Path.Combine(journalDirectory, transactionId + ".PLAN-REVIEW-LOG.md.stage");
        EnsureSafeDirectory(Path.GetDirectoryName(planStage)!);
        EnsureSafeDirectory(Path.GetDirectoryName(reviewStage)!);
        var planContent = parsed.HumanPlan;
        var reviewContent = plan["reviewLog"]!.GetValue<string>();
        var expected = new JsonObject
        {
            ["plan"] = ExpectedArtifact(planStage, planPath, planContent, ExistingHash(planPath)),
            ["review"] = ExpectedArtifact(reviewStage, reviewPath, reviewContent, ExistingHash(reviewPath)),
        };
        if (journal is null)
        {
            DurableFiles.WriteAtomic(planStage, planContent);
            DurableFiles.WriteAtomic(reviewStage, reviewContent);
            journal = new JsonObject
            {
                ["version"] = 2,
                ["generation"] = "v2",
                ["transactionId"] = transactionId,
                ["nonce"] = nonce,
                ["state"] = "staged",
                ["acknowledged"] = false,
                ["workspaceRoot"] = repository.WorkspaceRoot,
                ["gitCommonDir"] = repository.GitCommonDir,
                ["planRevision"] = planRevision,
                ["amendment"] = amendment,
                ["expected"] = expected,
            };
            DurableFiles.WriteJson(journalPath, journal);
        }
        else
        {
            VerifyJournalArtifacts(journal, repository, planContent, reviewContent, planPath, reviewPath);
        }

        PromoteJournalArtifact(journal, "plan", planPath);
        PromoteJournalArtifact(journal, "review", reviewPath);
        journal["state"] = "promoted";
        DurableFiles.WriteJson(journalPath, journal);
        if (Environment.GetEnvironmentVariable("FORGE_CRASH_AT") == "after-artifacts") throw new InvalidOperationException("materialization crash injection after-artifacts");
        var state = existingState?.DeepClone().AsObject() ?? StateStore.CreateEmpty(plan["humanPlanHash"]!.GetValue<string>());
        if (amendment)
        {
            state["workflow"]!["amendment"] = true;
            state["dispatch"] = new JsonObject { ["id"] = null, ["stage"] = null, ["taskNumber"] = null, ["retry"] = 0, ["pending"] = false, ["attempt"] = 0, ["lastVerificationPassed"] = null, ["model"] = null, ["effort"] = null, ["resolution"] = null };
            state["agents"]!["builderId"] = null;
            state["agents"]!["lastBuilderDispatchId"] = null;
            state["review"] = new JsonObject { ["coverage"] = null, ["verdict"] = null, ["fixRound"] = 0, ["authorizedPaths"] = new JsonArray(), ["manifest"] = null, ["critiqueFile"] = null, ["critiqueFiles"] = existingState!["review"]!["critiqueFiles"]?.DeepClone() ?? new JsonArray() };
        }
        state["workflow"]!["phase"] = amendment ? existingState!["workflow"]!["phase"]!.DeepClone() : "materialized";
        state["workflow"]!["round"] = plan["completedReviewRounds"]!.DeepClone();
        state["workflow"]!["maxRounds"] = plan["maxRounds"]!.DeepClone();
        state["models"]!["reviewer"] = selections["reviewer"]!.DeepClone();
        state["models"]!["builder"] = selections["builder"]!.DeepClone();
        state["approval"]!["planHash"] = plan["humanPlanHash"]!.GetValue<string>();
        state["approval"]!["nonce"] = nonce;
        state["approval"]!["revision"] = plan["planRevision"]!.GetValue<int>();
        state["materialization"]!["transactionId"] = transactionId;
        state["materialization"]!["committed"] = true;
        state["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        DurableFiles.WriteJson(StateStore.StatePath(repository.WorkspaceRoot), state);
        UpsertManagedExclude(repository);
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "active.json"), new JsonObject { ["version"] = 2, ["generation"] = "v2", ["workspaceRoot"] = repository.WorkspaceRoot, ["transactionId"] = transactionId }.ToJsonString() + "\n");
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "commit-marker.json"), new JsonObject { ["version"] = 2, ["generation"] = "v2", ["transactionId"] = transactionId }.ToJsonString() + "\n");
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "changed-files.txt"), string.Empty);
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "events.jsonl"), new JsonObject { ["version"] = 2, ["event"] = "materialized", ["transactionId"] = transactionId }.ToJsonString() + "\n");
        journal["state"] = "acknowledged";
        journal["acknowledged"] = true;
        DurableFiles.WriteJson(journalPath, journal);
        if (Environment.GetEnvironmentVariable("FORGE_CRASH_AT") == "after-journal-acknowledged") throw new InvalidOperationException("materialization crash injection after-journal-acknowledged");
        DurableFiles.WriteJson(tombstone, new JsonObject { ["version"] = 2, ["generation"] = "v2", ["nonce"] = nonce, ["transactionId"] = transactionId, ["workspaceRoot"] = repository.WorkspaceRoot, ["gitCommonDir"] = repository.GitCommonDir, ["planRevision"] = planRevision, ["acknowledged"] = true });
        if (Environment.GetEnvironmentVariable("FORGE_CRASH_AT") == "after-tombstone") throw new InvalidOperationException("materialization crash injection after-tombstone");
        return new JsonObject
        {
            ["action"] = amendment ? "forge-amendment-materialized" : "forge-materialized",
            ["phase"] = "materialized",
            ["planRevision"] = plan["planRevision"]!.GetValue<int>(),
            ["reviewer"] = selections["reviewer"]!.DeepClone(),
            ["builder"] = selections["builder"]!.DeepClone(),
        };
    }

    private static JsonObject ExpectedArtifact(string stagedPath, string finalPath, string content, string? priorHash)
        => new()
        {
            ["stagedPath"] = stagedPath,
            ["finalPath"] = finalPath,
            ["hash"] = Hashing.Sha256Hex(content),
            ["priorHash"] = priorHash,
            ["content"] = content,
        };

    private static string? ExistingHash(string path)
    {
        if (!File.Exists(path)) return null;
        EnsureRegularLedgerEntry(path, "materialization artifact");
        return Hashing.Sha256File(path);
    }

    private static JsonObject LoadAndValidateJournal(string path, RepositoryIdentity repository, string transactionId, string nonce, int planRevision, string planContent, string reviewContent)
    {
        EnsureRegularLedgerFile(path, "v2 materialization journal");
        try
        {
            var journal = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("journal is not an object");
            RequireJournalKeys(journal, ["version", "generation", "transactionId", "nonce", "state", "acknowledged", "workspaceRoot", "gitCommonDir", "planRevision", "amendment", "expected"], "journal");
            if (journal["version"]?.GetValue<int>() != 2 || journal["generation"]?.GetValue<string>() != "v2" || journal["transactionId"]?.GetValue<string>() != transactionId || journal["nonce"]?.GetValue<string>() != nonce || journal["planRevision"]?.GetValue<int>() != planRevision)
            {
                throw new FormatException("journal identity does not match the approval");
            }

            var journalState = journal["state"]?.GetValue<string>();
            var acknowledged = journal["acknowledged"]?.GetValue<bool>() == true;
            if (journalState is not ("staged" or "promoted" or "acknowledged") || (journalState == "acknowledged") != acknowledged || journal["amendment"]?.GetValue<bool>() is null)
            {
                throw new FormatException("journal state transition is invalid");
            }

            if (!RepositoryMatches(journal, repository) || journal["expected"] is not JsonObject expected || expected["plan"] is not JsonObject || expected["review"] is not JsonObject)
            {
                throw new FormatException("journal repository or expected artifacts do not match");
            }

            RequireJournalKeys(expected, ["plan", "review"], "journal.expected");

            var plan = expected["plan"]!.AsObject();
            var review = expected["review"]!.AsObject();
            ValidateJournalArtifact(plan, "plan", transactionId, repository, planPath: Path.Combine(repository.WorkspaceRoot, "PLAN.md"));
            ValidateJournalArtifact(review, "review", transactionId, repository, planPath: Path.Combine(repository.WorkspaceRoot, "PLAN-REVIEW-LOG.md"));
            if (plan["content"]?.GetValue<string>() != planContent || review["content"]?.GetValue<string>() != reviewContent ||
                plan["hash"]?.GetValue<string>() != Hashing.Sha256Hex(planContent) || review["hash"]?.GetValue<string>() != Hashing.Sha256Hex(reviewContent))
            {
                throw new FormatException("journal content does not match the approval");
            }

            return journal;
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"materialization journal is malformed: {error.Message}", 3); }
    }

    private static void RequireJournalKeys(JsonObject value, IReadOnlyCollection<string> expected, string label)
    {
        var actual = value.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw new FormatException($"{label} fields are not exact");
    }

    private static void ValidateJournalArtifact(JsonObject artifact, string name, string transactionId, RepositoryIdentity repository, string planPath)
    {
        RequireJournalKeys(artifact, ["stagedPath", "finalPath", "hash", "priorHash", "content"], $"journal.expected.{name}");
        var expectedStaged = Path.Combine(RepositoryPaths.JournalDirectory(), transactionId + (name == "plan" ? ".PLAN.md.stage" : ".PLAN-REVIEW-LOG.md.stage"));
        var staged = artifact["stagedPath"]?.GetValue<string>();
        var final = artifact["finalPath"]?.GetValue<string>();
        var hash = artifact["hash"]?.GetValue<string>();
        var priorHash = artifact["priorHash"]?.GetValue<string>();
        var content = artifact["content"]?.GetValue<string>();
        if (!string.Equals(staged, expectedStaged, PathComparison()) || !string.Equals(final, planPath, PathComparison()) || !HashingPattern(hash) || (priorHash is not null && !HashingPattern(priorHash)) || content is null || Hashing.Sha256Hex(content) != hash)
        {
            throw new FormatException($"journal.expected.{name} is not an authorized artifact");
        }
        EnsureSafeDirectory(Path.GetDirectoryName(expectedStaged)!);
    }

    private static bool HashingPattern(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    private static void VerifyJournalArtifacts(JsonObject journal, RepositoryIdentity repository, string planContent, string reviewContent, string planPath, string reviewPath)
    {
        var expected = journal["expected"]?.AsObject() ?? throw new CliFailure("state", "materialization journal lacks expected artifacts", 3);
        foreach (var (name, finalPath, content) in new[] { ("plan", planPath, planContent), ("review", reviewPath, reviewContent) })
        {
            var artifact = expected[name]?.AsObject() ?? throw new CliFailure("state", $"materialization journal lacks the {name} artifact", 3);
            if (!string.Equals(artifact["finalPath"]?.GetValue<string>(), finalPath, PathComparison()) || artifact["content"]?.GetValue<string>() != content || artifact["hash"]?.GetValue<string>() != Hashing.Sha256Hex(content))
            {
                throw new CliFailure("state", $"materialization journal {name} artifact identity is incompatible", 3);
            }
            var staged = artifact["stagedPath"]?.GetValue<string>() ?? string.Empty;
            var expectedRoot = Path.GetFullPath(RepositoryPaths.PluginData());
            if (!IsPathWithin(expectedRoot, staged)) throw new CliFailure("state", $"materialization journal {name} staging path escapes plugin data", 3);
            EnsureSafeDirectory(Path.GetDirectoryName(staged)!);
        }
    }

    private static void PromoteJournalArtifact(JsonObject journal, string name, string finalPath)
    {
        var artifact = journal["expected"]![name]!.AsObject();
        var staged = artifact["stagedPath"]!.GetValue<string>();
        var expectedHash = artifact["hash"]!.GetValue<string>();
        var priorHash = artifact["priorHash"]?.GetValue<string>();
        if (!string.Equals(artifact["finalPath"]?.GetValue<string>(), finalPath, PathComparison())) throw new CliFailure("state", $"materialization journal {name} destination changed", 3);
        EnsureRegularLedgerEntry(staged, $"staged {name} artifact");
        EnsureRegularLedgerEntry(finalPath, $"materialization {name} artifact");

        if (File.Exists(finalPath))
        {
            var currentHash = Hashing.Sha256File(finalPath);
            if (currentHash == expectedHash)
            {
                if (File.Exists(staged)) File.Delete(staged);
                return;
            }
            if (priorHash is null || currentHash != priorHash) throw new CliFailure("state", $"materialization {name} artifact was changed outside the transaction", 3);
        }
        else if (priorHash is not null)
        {
            throw new CliFailure("state", $"materialization {name} artifact disappeared during recovery", 3);
        }

        if (!File.Exists(staged) || Hashing.Sha256File(staged) != expectedHash) throw new CliFailure("state", $"staged {name} artifact is missing or corrupt", 3);
        DurableFiles.WriteAtomic(finalPath, File.ReadAllText(staged));
        if (Hashing.Sha256File(finalPath) != expectedHash) throw new CliFailure("state", $"materialization {name} artifact failed verification", 3);
        File.Delete(staged);
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static int NextPlanRevision(RepositoryIdentity repository)
    {
        var highest = HighestCommittedRevision(RepositoryPaths.TombstoneDirectory(), repository);
        var statePath = StateStore.StatePath(repository.WorkspaceRoot);
        if (File.Exists(statePath)) highest = Math.Max(highest, StateStore.Load(repository.WorkspaceRoot)["approval"]!["revision"]?.GetValue<int>() ?? 0);
        if (highest == int.MaxValue) throw new CliFailure("state", "approval plan revision space is exhausted", 3);
        return highest + 1;
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative is not (".." or "") && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool RepositoryMatches(JsonObject value, RepositoryIdentity repository)
        => string.Equals(value["workspaceRoot"]?.GetValue<string>(), repository.WorkspaceRoot, PathComparison()) && string.Equals(value["gitCommonDir"]?.GetValue<string>(), repository.GitCommonDir, PathComparison());

    public static void Cleanup(string workspace, bool deleteArtifacts, bool purgeReplayLedger = false, bool purgeAgents = false)
    {
        var forgeDirectory = Path.Combine(workspace, ".forge");
        var hadForgeDirectory = Directory.Exists(forgeDirectory);
        using (var stateLock = ForgeStateLock.Acquire(workspace))
        {
            EnsureSafeDirectory(forgeDirectory);
            if (hadForgeDirectory)
            {
                foreach (var path in CollectOwnedForgeFiles(workspace, forgeDirectory)) File.Delete(path);
            }

            var critiques = Path.Combine(forgeDirectory, "critiques");
            if (Directory.Exists(critiques))
            {
                EnsureSafeDirectory(critiques);
                if (Directory.GetFileSystemEntries(critiques).Length == 0) Directory.Delete(critiques, false);
            }
        }
        if (Directory.Exists(forgeDirectory) && Directory.GetFileSystemEntries(forgeDirectory).Length == 0) Directory.Delete(forgeDirectory, false);
        var repository = RepositoryPaths.Identify(workspace);
        RemoveManagedExclude(repository);
        foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
        {
            var existing = ProcessExecution.Run("git", ["-C", workspace, "rev-parse", "--verify", reference]);
            if (existing.ExitCode == 0)
            {
                var removed = ProcessExecution.Run("git", ["-C", workspace, "update-ref", "-d", reference]);
                if (removed.ExitCode != 0) throw new CliFailure("environment", $"could not remove Git baseline ref {reference}");
            }
        }

        if (deleteArtifacts)
        {
            var artifacts = new[] { Path.Combine(workspace, "PLAN.md"), Path.Combine(workspace, "PLAN-REVIEW-LOG.md") };
            var canDelete = artifacts.All(path => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && File.ReadLines(path).FirstOrDefault() == CanonicalText.OwnedMarker);
            if (canDelete)
            {
                foreach (var path in artifacts) File.Delete(path);
            }
        }

        if (purgeReplayLedger) PurgeLedgerForRepository(repository);

        if (purgeAgents)
        {
            var agents = RepositoryPaths.AgentsDirectory();
            EnsureSafeDirectory(agents);
            foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
            {
                var path = Path.Combine(agents, name);
                if (!File.Exists(path)) continue;
                if (Materializer.IsOwnedAgentFile(path)) File.Delete(path);
            }
        }
    }

    private static IReadOnlyList<string> CollectOwnedForgeFiles(string workspace, string forgeDirectory)
    {
        var statePath = Path.Combine(forgeDirectory, "state.json");
        if (!File.Exists(statePath)) return [];
        EnsureOwnedForgeFile(statePath);
        var state = StateStore.Load(workspace);
        var materialization = state["materialization"]!.AsObject();
        var transactionId = materialization["transactionId"]?.GetValue<string>();
        if (materialization["generation"]?.GetValue<string>() != "v2" || materialization["committed"]?.GetValue<bool>() != true || !Regex.IsMatch(transactionId ?? string.Empty, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new CliFailure("state", "cleanup requires a committed v2 materialization transaction", 3);

        var owned = new List<string> { statePath };
        var activePath = Path.Combine(forgeDirectory, "active.json");
        var commitPath = Path.Combine(forgeDirectory, "commit-marker.json");
        var eventsPath = Path.Combine(forgeDirectory, "events.jsonl");
        var active = ReadOwnedForgeObject(activePath, "active marker");
        var commit = ReadOwnedForgeObject(commitPath, "commit marker");
        if (active["version"]?.GetValue<int>() != 2 || active["generation"]?.GetValue<string>() != "v2" || active["transactionId"]?.GetValue<string>() != transactionId || !string.Equals(active["workspaceRoot"]?.GetValue<string>(), Path.GetFullPath(workspace), PathComparison())) throw new CliFailure("state", "active marker is not owned by the current materialization", 3);
        if (commit["version"]?.GetValue<int>() != 2 || commit["generation"]?.GetValue<string>() != "v2" || commit["transactionId"]?.GetValue<string>() != transactionId) throw new CliFailure("state", "commit marker is not owned by the current materialization", 3);
        owned.Add(activePath);
        owned.Add(commitPath);

        EnsureOwnedForgeFile(eventsPath);
        var eventLines = File.ReadAllLines(eventsPath);
        if (eventLines.Length == 0) throw new CliFailure("state", "events ledger is empty for the current materialization", 3);
        foreach (var line in eventLines)
        {
            try
            {
                var item = JsonNode.Parse(line)?.AsObject();
                if (item?["version"]?.GetValue<int>() != 2 || item["transactionId"]?.GetValue<string>() != transactionId || string.IsNullOrWhiteSpace(item["event"]?.GetValue<string>())) throw new FormatException("event is not owned by the current transaction");
            }
            catch (Exception error) when (error is not CliFailure)
            {
                throw new CliFailure("state", $"events ledger is foreign or malformed: {error.Message}", 3);
            }
        }
        owned.Add(eventsPath);

        if (state["review"]!["critiqueFiles"] is not JsonArray critiqueFiles) throw new CliFailure("state", "state.review.critiqueFiles is malformed", 3);
        foreach (var entry in critiqueFiles)
        {
            if (entry is not JsonObject item || item.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(new[] { "hash", "path" }, StringComparer.Ordinal) is false) throw new CliFailure("state", "state.review.critiqueFiles contains a malformed entry", 3);
            var path = item["path"]?.GetValue<string>();
            var hash = item["hash"]?.GetValue<string>();
            var absolute = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !IsPathWithin(workspace, absolute) || !IsPathWithin(forgeDirectory, absolute) || !HashingPattern(hash)) throw new CliFailure("state", "state.review.critiqueFiles contains an unauthorized path or hash", 3);
            EnsureSafeDirectory(Path.GetDirectoryName(absolute)!);
            EnsureOwnedForgeFile(absolute);
            if (Hashing.Sha256File(absolute) != hash) throw new CliFailure("state", $"owned critique changed outside Forge: {absolute}", 3);
            if (!owned.Any(item => string.Equals(item, absolute, PathComparison()))) owned.Add(absolute);
        }

        var changedFilesPath = Path.Combine(forgeDirectory, "changed-files.txt");
        if (File.Exists(changedFilesPath))
        {
            EnsureOwnedForgeFile(changedFilesPath);
            var manifest = state["review"]!["manifest"] as JsonObject;
            var expectedChanged = manifest is null ? string.Empty : ReviewChangedFiles(manifest);
            var actualChanged = File.ReadAllText(changedFilesPath);
            if (!string.Equals(actualChanged, expectedChanged, StringComparison.Ordinal)) throw new CliFailure("state", "changed-files evidence is not owned by the current review manifest", 3);
            owned.Add(changedFilesPath);
        }

        var reviewManifestPath = Path.Combine(forgeDirectory, "review-manifest.json");
        var evidencePresent = ReviewEvidenceFiles.Any(name => File.Exists(Path.Combine(forgeDirectory, name)) && name != "changed-files.txt");
        var manifestState = state["review"]!["manifest"] as JsonObject;
        if (manifestState is null)
        {
            if (evidencePresent || File.Exists(reviewManifestPath)) throw new CliFailure("state", "review evidence is not bound to a valid state manifest; refusing cleanup", 3);
        }
        else
        {
            var onDisk = ReadOwnedForgeObject(reviewManifestPath, "review manifest");
            if (onDisk["version"]?.GetValue<int>() != 3 || onDisk["generation"]?.GetValue<string>() != "v2" || !string.Equals(onDisk.ToJsonString(), manifestState.ToJsonString(), StringComparison.Ordinal)) throw new CliFailure("state", "review manifest is not owned by the current state", 3);
            if (manifestState["evidenceHashes"] is not JsonObject hashes) throw new CliFailure("state", "review manifest lacks evidence ownership hashes", 3);
            var expectedNames = ReviewEvidenceFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var actualNames = hashes.Select(item => item.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal)) throw new CliFailure("state", "review evidence ownership fields are not exact", 3);
            foreach (var name in ReviewEvidenceFiles)
            {
                var path = Path.Combine(forgeDirectory, name);
                EnsureOwnedForgeFile(path);
                var hash = hashes[name]?.GetValue<string>();
                if (!HashingPattern(hash) || Hashing.Sha256File(path) != hash) throw new CliFailure("state", $"review evidence is not owned or has changed: {name}", 3);
                if (!owned.Contains(path, StringComparer.Ordinal)) owned.Add(path);
            }
            owned.Add(reviewManifestPath);
        }

        return owned;
    }

    private static JsonObject ReadOwnedForgeObject(string path, string label)
    {
        EnsureOwnedForgeFile(path);
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("object expected");
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"{label} is foreign or malformed: {error.Message}", 3); }
    }

    private static string ReviewChangedFiles(JsonObject manifest)
    {
        var paths = new[] { "preExistingFiles", "inRunFiles", "untrackedFiles" }
                   .SelectMany(name => manifest[name] is JsonArray values ? values.Select(item => item?.GetValue<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!) : [])
                   .Distinct(StringComparer.Ordinal)
                   .OrderBy(path => path, StringComparer.Ordinal)
                   .ToArray();
        return paths.Length == 0 ? string.Empty : string.Join(Environment.NewLine, paths) + Environment.NewLine;
    }

    private static void CheckReplayLedger(RepositoryIdentity repository, bool purge, string currentTransactionId)
    {
        var legacy = Path.Combine(RepositoryPaths.PluginData(), "materialization-journal");
        if (Directory.Exists(legacy))
        {
            EnsureSafeDirectory(legacy);
            foreach (var path in Directory.EnumerateFiles(legacy, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureRegularLedgerFile(path, "v1 materialization journal");
                var value = ReadLedgerObject(path, "v1 materialization journal");
                if (IsAcknowledged(value) || !RepositoryMatchesLegacy(value, repository)) continue;
                if (purge) File.Delete(path);
                else
                {
                    throw new CliFailure("state", "an unacknowledged v1 materialization journal blocks this run; recover it or explicitly use --purge-replay-ledger", 3);
                }
            }
        }

        var currentJournal = RepositoryPaths.JournalDirectory();
        if (Directory.Exists(currentJournal))
        {
            EnsureSafeDirectory(currentJournal);
            foreach (var path in Directory.EnumerateFiles(currentJournal, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    EnsureRegularLedgerFile(path, "v2 materialization journal");
                    var journal = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                    if (journal?["version"]?.GetValue<int>() != 2 || journal["generation"]?.GetValue<string>() != "v2") throw new CliFailure("state", "foreign materialization journal detected", 3);
                    var transactionId = journal["transactionId"]?.GetValue<string>() ?? string.Empty;
                    var nonce = journal["nonce"]?.GetValue<string>() ?? string.Empty;
                    var journalState = journal["state"]?.GetValue<string>();
                    var acknowledgedFlag = journal["acknowledged"]?.GetValue<bool>() == true;
                    if (journalState is not ("staged" or "promoted" or "acknowledged") || (journalState == "acknowledged") != acknowledgedFlag) throw new CliFailure("state", "materialization journal state transition is invalid", 3);
                    if (!Regex.IsMatch(transactionId, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || !Regex.IsMatch(nonce, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant) || !string.Equals(Path.GetFileNameWithoutExtension(path), transactionId, StringComparison.Ordinal)) throw new CliFailure("state", "materialization journal identity is malformed", 3);
                    var workspace = journal["workspaceRoot"]?.GetValue<string>();
                    var common = journal["gitCommonDir"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(common) || !Path.IsPathRooted(workspace) || !Path.IsPathRooted(common)) throw new CliFailure("state", "materialization journal repository identity is malformed", 3);
                    var sameRepository = string.Equals(workspace, repository.WorkspaceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && string.Equals(common, repository.GitCommonDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    if (!sameRepository) continue;
                    var acknowledged = journal["state"]?.GetValue<string>() == "acknowledged" || journal["acknowledged"]?.GetValue<bool>() == true;
                    var tombstone = Path.Combine(RepositoryPaths.TombstoneDirectory(), Hashing.Sha256Hex(nonce) + ".json");
                    EnsureRegularLedgerEntry(tombstone, "nonce tombstone");
                    var complete = acknowledged && File.Exists(tombstone);
                    if (!complete && transactionId != currentTransactionId)
                    {
                        if (purge && !acknowledged)
                        {
                            File.Delete(path);
                            DeleteJournalSidecars(path);
                        }
                        else throw new CliFailure("state", acknowledged
                                                               ? "an acknowledged v2 materialization journal has no tombstone; retry the matching approval before starting another run"
                                                               : "an unacknowledged v2 materialization journal requires recovery before a new run; retry the matching approval or explicitly use --purge-replay-ledger", 3);
                    }
                }
                catch (CliFailure) { throw; }
                catch (Exception error) { throw new CliFailure("state", $"materialization journal is malformed: {error.Message}", 3); }
            }
        }
    }

    private static JsonObject ReadLedgerObject(string path, string label)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("record is not an object"); }
        catch (Exception error) { throw new CliFailure("state", $"{label} is malformed: {error.Message}", 3); }
    }

    private static bool IsAcknowledged(JsonObject value) => value["acknowledged"]?.GetValue<bool>() == true || value["state"]?.GetValue<string>() == "acknowledged";

    private static bool RepositoryMatchesLegacy(JsonObject value, RepositoryIdentity repository)
    {
        var nested = value["repository"] as JsonObject;
        var workspace = value["workspaceRoot"]?.GetValue<string>() ?? nested?["workspaceRoot"]?.GetValue<string>();
        var common = value["gitCommonDir"]?.GetValue<string>() ?? nested?["gitCommonDir"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(common)) throw new CliFailure("state", "an unacknowledged v1 materialization journal has no repository identity; recover it or explicitly use --purge-replay-ledger", 3);
        return string.Equals(workspace, repository.WorkspaceRoot, PathComparison()) && string.Equals(common, repository.GitCommonDir, PathComparison());
    }

    private static void PurgeLedgerForRepository(RepositoryIdentity repository)
    {
        foreach (var directory in new[] { Path.Combine(RepositoryPaths.PluginData(), "materialization-journal"), RepositoryPaths.JournalDirectory() })
        {
            if (!Directory.Exists(directory)) continue;
            EnsureSafeDirectory(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureRegularLedgerFile(path, "materialization ledger");
                JsonObject value;
                try { value = ReadLedgerObject(path, "materialization ledger"); }
                catch (CliFailure) { continue; }
                if (IsAcknowledged(value)) continue;
                var matches = directory.EndsWith("materialization-journal", StringComparison.Ordinal)
                                  ? RepositoryMatchesLegacy(value, repository)
                                  : RepositoryMatches(value, repository);
                if (matches)
                {
                    File.Delete(path);
                    if (directory.EndsWith("materialization-journal-v2", StringComparison.Ordinal)) DeleteJournalSidecars(path);
                }
            }
        }
    }

    private static void DeleteJournalSidecars(string journalPath)
    {
        var prefix = Path.GetFileNameWithoutExtension(journalPath);
        var directory = Path.GetDirectoryName(journalPath)!;
        foreach (var path in Directory.EnumerateFiles(directory, prefix + ".*.stage", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularLedgerFile(path, "materialization journal staging file");
            File.Delete(path);
        }
    }

    private static void EnsureRegularLedgerEntry(string path, string label)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0) throw new CliFailure("state", $"{label} must be a regular file: {path}", 3);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static int HighestCommittedRevision(string directory, RepositoryIdentity repository)
    {
        var highest = 0;
        if (!Directory.Exists(directory)) return highest;
        EnsureSafeDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                EnsureRegularLedgerFile(path, "nonce tombstone");
                var value = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (value?["version"]?.GetValue<int>() != 2 || value["generation"]?.GetValue<string>() != "v2" || value["acknowledged"]?.GetValue<bool>() != true) continue;
                var tombstoneNonce = value["nonce"]?.GetValue<string>() ?? string.Empty;
                var tombstoneTransaction = value["transactionId"]?.GetValue<string>() ?? string.Empty;
                if (!Regex.IsMatch(tombstoneNonce, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant) || !Regex.IsMatch(tombstoneTransaction, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || Path.GetFileNameWithoutExtension(path) != Hashing.Sha256Hex(tombstoneNonce)) throw new CliFailure("state", "nonce tombstone identity is malformed", 3);
                var workspace = value["workspaceRoot"]?.GetValue<string>();
                var common = value["gitCommonDir"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(common) || !Path.IsPathRooted(workspace) || !Path.IsPathRooted(common) || value["planRevision"]?.GetValue<int>() is not > 0) throw new CliFailure("state", "nonce tombstone repository identity is malformed", 3);
                if (!string.Equals(workspace, repository.WorkspaceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !string.Equals(common, repository.GitCommonDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                highest = Math.Max(highest, value["planRevision"]?.GetValue<int>() ?? 0);
            }
            catch (Exception error)
            {
                throw new CliFailure("state", $"nonce tombstone is malformed: {error.Message}", 3);
            }
        }

        return highest;
    }

    private static void EnsureRegularLedgerFile(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"{label} must not be a symlink: {path}", 3);
    }

    private const string GeneratedAgentMarker = "# plan-forge-flow:generated";

    private static void EnsureOwnedForgeFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new CliFailure("state", $"Forge-owned cleanup target must be a regular file: {path}", 3);
            if (new FileInfo(path).LinkTarget is not null) throw new CliFailure("state", $"Forge-owned cleanup target must not be a symlink: {path}", 3);
        }
        catch (PlatformNotSupportedException) { }
        catch (FileNotFoundException) { throw new CliFailure("state", $"Forge-owned cleanup target disappeared: {path}", 3); }
        catch (DirectoryNotFoundException) { throw new CliFailure("state", $"Forge-owned cleanup target disappeared: {path}", 3); }
    }

    internal static bool IsOwnedAgentFile(string path)
    {
        EnsureOwnedForgeFile(path);
        return File.ReadLines(path).FirstOrDefault() == GeneratedAgentMarker;
    }

    internal static void EnsureOwnedAgentFile(string path)
    {
        if (!IsOwnedAgentFile(path)) throw new CliFailure("state", $"refusing to overwrite a foreign generated agent: {path}", 3);
    }

    private static void ValidateAmendment(JsonObject state, string humanPlan, int revision)
    {
        var phase = state["workflow"]!["phase"]?.GetValue<string>();
        if (phase is not ("build" or "code-review")) throw new CliFailure("state", "an approval resume may amend only an active build or code-review run", 3);
        var oldRevision = state["approval"]!["revision"]?.GetValue<int>() ?? 0;
        if (revision <= oldRevision) throw new CliFailure("state", $"amendment revision {revision} is not newer than the active revision {oldRevision}", 3);
        var oldHash = state["approval"]!["planHash"]?.GetValue<string>();
        var newHash = Hashing.Sha256Hex(humanPlan);
        if (string.Equals(oldHash, newHash, StringComparison.Ordinal)) throw new CliFailure("state", "amendment plan is byte-identical to the active plan", 3);
        var completed = Math.Max(0, (state["workflow"]!["nextTaskNumber"]?.GetValue<int>() ?? 1) - 1);
        var oldTasks = state["workflow"]!["tasks"] as JsonArray;
        var newTasks = CanonicalText.ParseTasks(humanPlan);
        if (completed > newTasks.Count) throw new CliFailure("state", "amendment removes already completed tasks", 3);
        for (var index = 0; index < completed; index++)
        {
            var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index]?.AsObject() : null;
            if (oldTask is null || oldTask["hash"]?.GetValue<string>() != newTasks[index].Hash) throw new CliFailure("state", $"amendment changes completed task {index + 1}", 3);
        }
    }

    private static void ValidateContinuingState(JsonObject state, JsonObject plan, string nonce, string transactionId, int planRevision, bool amendment)
    {
        if (state["materialization"]!["generation"]?.GetValue<string>() != "v2" || state["materialization"]!["committed"]?.GetValue<bool>() != true || state["materialization"]!["transactionId"]?.GetValue<string>() != transactionId || state["approval"]!["nonce"]?.GetValue<string>() != nonce || state["approval"]!["revision"]?.GetValue<int>() != planRevision || state["approval"]!["planHash"]?.GetValue<string>() != plan["humanPlanHash"]?.GetValue<string>() || amendment && state["workflow"]!["amendment"]?.GetValue<bool>() != true)
        {
            throw new CliFailure("state", "materialization state does not match the recoverable approval journal", 3);
        }
    }

    private static void ValidateManagedExclude(RepositoryIdentity repository)
    {
        var path = ExcludePath(repository);
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "refusing to edit a symlinked Git exclude", 3);
        var text = File.ReadAllText(path);
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        var beginCount = Regex.Matches(text, Regex.Escape(ExcludeBegin), RegexOptions.CultureInvariant).Count;
        var endCount = Regex.Matches(text, Regex.Escape(ExcludeEnd), RegexOptions.CultureInvariant).Count;
        if ((begin >= 0) != (end >= 0) || (begin >= 0 && end < begin) || beginCount > 1 || endCount > 1)
        {
            throw new CliFailure("state", "Git exclude contains a malformed Forge managed block", 3);
        }
    }

    internal static void EnsureSafeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"refusing to materialize through a symlinked directory: {current.FullName}", 3);
            try
            {
                if (current.LinkTarget is not null) throw new CliFailure("state", $"refusing to materialize through a symlinked directory: {current.FullName}", 3);
            }
            catch (PlatformNotSupportedException) { }
        }
    }

    private static void EnsureOwnedArtifact(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0) throw new CliFailure("state", $"refusing to overwrite a symlinked or non-file artifact: {path}", 3);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (File.ReadLines(path).FirstOrDefault() != CanonicalText.OwnedMarker) throw new CliFailure("state", $"existing artifact is not Forge-owned: {path}", 3);
    }

    private static string ExcludePath(RepositoryIdentity repository)
    {
        var result = ProcessExecution.Run("git", ["-C", repository.WorkspaceRoot, "rev-parse", "--git-path", "info/exclude"]);
        var raw = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout)
                      ? result.Stdout.Trim()
                      : Path.Combine(repository.GitCommonDir, "info", "exclude");
        var path = Path.IsPathRooted(raw) ? raw : Path.Combine(repository.WorkspaceRoot, raw);
        var full = Path.GetFullPath(path);
        EnsureSafeDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static void UpsertManagedExclude(RepositoryIdentity repository)
    {
        var path = ExcludePath(repository);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "refusing to edit a symlinked Git exclude", 3);
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        if ((begin >= 0) != (end >= 0) || (begin >= 0 && end < begin)) throw new CliFailure("state", "Git exclude contains a malformed Forge managed block", 3);
        string next;
        if (begin >= 0)
        {
            var after = end + ExcludeEnd.Length;
            next = text[..begin] + ExcludeBlock + text[after..];
        }
        else
        {
            next = text.TrimEnd('\r', '\n');
            next = next.Length == 0 ? ExcludeBlock + "\n" : next + "\n" + ExcludeBlock + "\n";
        }
        DurableFiles.WriteAtomic(path, next);
    }

    private static void RemoveManagedExclude(RepositoryIdentity repository)
    {
        var path = ExcludePath(repository);
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "refusing to edit a symlinked Git exclude", 3);
        var text = File.ReadAllText(path);
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        if (begin < 0 && end < 0) return;
        if (begin < 0 || end < begin) throw new CliFailure("state", "Git exclude contains a malformed Forge managed block", 3);
        var after = end + ExcludeEnd.Length;
        var next = (text[..begin] + text[after..]).TrimEnd('\r', '\n');
        DurableFiles.WriteAtomic(path, next.Length == 0 ? string.Empty : next + Environment.NewLine);
    }
}
