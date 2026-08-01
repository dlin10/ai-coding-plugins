using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal static class Materializer
{
    private const string ExcludeBegin = "# >>> plan-forge-flow (managed) >>>";
    private const string ExcludeEnd = "# <<< plan-forge-flow (managed) <<<";
    private const string ExcludeBlock = ExcludeBegin + "\n.forge/\n" + ExcludeEnd;

    public static JsonObject Materialize(RepositoryIdentity repository, string wrapper)
    {
        var parsed = ResumeEnvelope.Parse(wrapper);
        var envelopeRepository = parsed.Envelope["repository"]!.AsObject();
        if (!string.Equals(envelopeRepository["workspaceRoot"]?.GetValue<string>(), repository.WorkspaceRoot, PathComparison()) ||
            !string.Equals(envelopeRepository["gitCommonDir"]?.GetValue<string>(), repository.GitCommonDir, PathComparison()))
        {
            throw new CliFailure("state", "approved envelope belongs to a different repository", 3);
        }

        var nonce = parsed.Envelope["nonce"]!.GetValue<string>();
        var plan = parsed.Envelope["plan"]!.AsObject();
        var selections = parsed.Envelope["selections"]!.AsObject();
        var planRevision = plan["planRevision"]!.GetValue<int>();
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);

        var lastNoncePath = RepositoryPaths.LastNoncePath(repository);
        if (File.Exists(lastNoncePath))
        {
            OwnershipGuards.EnsureRegularFile(lastNoncePath, "last materialization nonce");
            if (string.Equals(File.ReadAllText(lastNoncePath).Trim(), nonce, StringComparison.Ordinal)) throw new CliFailure("state", "approval nonce has already been used", 3);
        }

        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        OwnershipGuards.EnsureDirectory(forgeDirectory);
        ValidateManagedExclude(repository);
        var planPath = Path.Combine(repository.WorkspaceRoot, "PLAN.md");
        var reviewPath = Path.Combine(repository.WorkspaceRoot, "PLAN-REVIEW-LOG.md");
        OwnershipGuards.EnsureOwnedArtifact(planPath);
        OwnershipGuards.EnsureOwnedArtifact(reviewPath);

        ForgeState? existingState = null;
        var statePath = StateStore.StatePath(repository.WorkspaceRoot);
        if (File.Exists(statePath)) existingState = StateStore.Load(repository.WorkspaceRoot);
        var amendment = existingState is not null;
        if (amendment) ValidateAmendment(existingState!, parsed.HumanPlan, planRevision);

        DurableFiles.WriteAtomic(planPath, parsed.HumanPlan);
        DurableFiles.WriteAtomic(reviewPath, plan["reviewLog"]!.GetValue<string>());

        var state = existingState?.DeepCopy() ?? StateStore.CreateEmpty(plan["humanPlanHash"]!.GetValue<string>());
        if (amendment)
        {
            state.Workflow.Amendment = true;
            state.Dispatch = ForgeStateSchema.CreateDispatch();
            state.Agents.BuilderId = null;
            state.Agents.LastBuilderDispatchId = null;
            state.Review = ForgeStateSchema.CreateReview(existingState!.Review.CritiqueFiles);
        }
        state.Workflow.Phase = amendment ? existingState!.Workflow.Phase : ForgePhase.Materialized;
        state.Workflow.Round = plan["completedReviewRounds"]!.GetValue<int>();
        state.Workflow.MaxRounds = plan["maxRounds"]!.GetValue<int>();
        state.Models.Reviewer = PinnedSelection.FromJson(selections["reviewer"], "reviewer");
        state.Models.Builder = PinnedSelection.FromJson(selections["builder"], "builder");
        state.Approval.PlanHash = plan["humanPlanHash"]!.GetValue<string>();
        state.Approval.Nonce = nonce;
        state.Approval.Revision = planRevision;
        state.Materialization.Committed = true;
        state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        DurableFiles.WriteJson(statePath, state);
        UpsertManagedExclude(repository);
        DurableFiles.WriteAtomic(lastNoncePath, nonce + "\n");

        return new JsonObject
        {
            ["action"] = amendment ? "forge-amendment-materialized" : "forge-materialized",
            ["phase"] = "materialized",
            ["planRevision"] = planRevision,
            ["reviewer"] = selections["reviewer"]!.DeepClone(),
            ["builder"] = selections["builder"]!.DeepClone(),
        };
    }

    internal static int NextPlanRevision(RepositoryIdentity repository)
    {
        var statePath = StateStore.StatePath(repository.WorkspaceRoot);
        var highest = File.Exists(statePath) ? StateStore.Load(repository.WorkspaceRoot).Approval.Revision : 0;
        if (highest == int.MaxValue) throw new CliFailure("state", "approval plan revision space is exhausted", 3);
        return highest + 1;
    }

    public static void Cleanup(string workspace, bool deleteArtifacts, bool purgeAgents = false)
    {
        var forgeDirectory = Path.Combine(workspace, ".forge");
        var hadForgeDirectory = Directory.Exists(forgeDirectory);
        using (ForgeStateLock.Acquire(workspace))
        {
            OwnershipGuards.EnsureSafeDirectory(forgeDirectory);
            if (hadForgeDirectory)
            {
                foreach (var path in CollectOwnedForgeFiles(workspace, forgeDirectory)) File.Delete(path);
            }

            var critiques = Path.Combine(forgeDirectory, "critiques");
            if (Directory.Exists(critiques))
            {
                OwnershipGuards.EnsureSafeDirectory(critiques);
                if (Directory.GetFileSystemEntries(critiques).Length == 0) Directory.Delete(critiques, false);
            }
        }
        if (Directory.Exists(forgeDirectory) && Directory.GetFileSystemEntries(forgeDirectory).Length == 0) Directory.Delete(forgeDirectory, false);

        var repository = RepositoryPaths.Identify(workspace);
        RemoveLastNonce(repository);
        RemoveManagedExclude(repository);
        foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
        {
            var existing = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (existing.ExitCode == 0)
            {
                var removed = new GitClient(workspace).Run(["update-ref", "-d", reference]);
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

        if (purgeAgents)
        {
            var agents = RepositoryPaths.AgentsDirectory();
            OwnershipGuards.EnsureSafeDirectory(agents);
            foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
            {
                var path = Path.Combine(agents, name);
                if (!File.Exists(path)) continue;
                if (OwnershipGuards.IsOwnedAgentFile(path)) File.Delete(path);
            }
        }
    }

    private static IReadOnlyList<string> CollectOwnedForgeFiles(string workspace, string forgeDirectory)
    {
        var statePath = Path.Combine(forgeDirectory, "state.json");
        if (!File.Exists(statePath)) return [];
        OwnershipGuards.EnsureOwnedForgeFile(statePath);
        var state = StateStore.Load(workspace);
        var materialization = state.Materialization;
        if (materialization.Generation != ForgeState.Generation || !materialization.Committed)
            throw new CliFailure("state", "cleanup requires a committed materialization", 3);

        var owned = new List<string> { statePath };
        foreach (var entry in state.Review.CritiqueFiles)
        {
            var path = entry.Path;
            var hash = entry.Hash;
            var absolute = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !IsPathWithin(workspace, absolute) || !IsPathWithin(forgeDirectory, absolute) || !HashingPattern(hash)) throw new CliFailure("state", "state.review.critiqueFiles contains an unauthorized path or hash", 3);
            OwnershipGuards.EnsureSafeDirectory(Path.GetDirectoryName(absolute)!);
            OwnershipGuards.EnsureOwnedForgeFile(absolute);
            if (Hashing.Sha256File(absolute) != hash) throw new CliFailure("state", $"owned critique changed outside Forge: {absolute}", 3);
            if (!owned.Any(s => string.Equals(s, absolute, PathComparison()))) owned.Add(absolute);
        }

        var verdictFile = state.Review.VerdictFile;
        var verdictHash = state.Review.VerdictHash;
        if (!string.IsNullOrWhiteSpace(verdictFile))
        {
            var absolute = Path.GetFullPath(verdictFile);
            if (!IsPathWithin(workspace, absolute) || !IsPathWithin(forgeDirectory, absolute) || !HashingPattern(verdictHash)) throw new CliFailure("state", "state.review.verdictFile is unauthorized", 3);
            OwnershipGuards.EnsureOwnedForgeFile(absolute);
            if (Hashing.Sha256File(absolute) != verdictHash) throw new CliFailure("state", "owned verdict file changed outside Forge", 3);
            if (!owned.Any(item => string.Equals(item, absolute, PathComparison()))) owned.Add(absolute);
        }

        if (state.Review.Manifest is JsonObject manifest)
        {
            var reviewManifestPath = Path.Combine(forgeDirectory, "review-manifest.json");
            var onDisk = ReadOwnedForgeObject(reviewManifestPath, "review manifest");
            if (!string.Equals(onDisk.ToJsonString(), manifest.ToJsonString(), StringComparison.Ordinal)) throw new CliFailure("state", "review manifest is not owned by the current state", 3);
            if (manifest["evidenceHashes"] is not JsonObject hashes) throw new CliFailure("state", "review manifest lacks evidence ownership hashes", 3);
            var expectedNames = ReviewEvidence.Files.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var actualNames = hashes.Select(item => item.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal)) throw new CliFailure("state", "review manifest evidence ownership fields are not exact", 3);
            foreach (var name in ReviewEvidence.Files)
            {
                var path = Path.Combine(forgeDirectory, name);
                OwnershipGuards.EnsureOwnedForgeFile(path);
                var hash = hashes[name]?.GetValue<string>();
                if (!HashingPattern(hash) || Hashing.Sha256File(path) != hash) throw new CliFailure("state", $"review evidence is not owned or has changed: {name}", 3);
                if (!owned.Contains(path, StringComparer.Ordinal)) owned.Add(path);
            }
            owned.Add(reviewManifestPath);
        }
        else if (ReviewEvidence.Files.Any(name => File.Exists(Path.Combine(forgeDirectory, name)) || File.Exists(Path.Combine(forgeDirectory, "review-manifest.json"))))
        {
            throw new CliFailure("state", "review evidence is not bound to a valid state manifest; refusing cleanup", 3);
        }

        var changedFilesPath = Path.Combine(forgeDirectory, "changed-files.txt");
        if (File.Exists(changedFilesPath) && !owned.Contains(changedFilesPath, StringComparer.Ordinal))
        {
            OwnershipGuards.EnsureOwnedForgeFile(changedFilesPath);
            owned.Add(changedFilesPath);
        }
        return owned;
    }

    private static JsonObject ReadOwnedForgeObject(string path, string label)
    {
        OwnershipGuards.EnsureOwnedForgeFile(path);
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("object expected");
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"{label} is foreign or malformed: {error.Message}", 3); }
    }

    private static bool HashingPattern(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    private static bool IsPathWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative is not (".." or "") && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateAmendment(ForgeState state, string humanPlan, int revision)
    {
        var phase = state.Workflow.Phase;
        if (phase is not (ForgePhase.Build or ForgePhase.CodeReview)) throw new CliFailure("state", "an approval resume may amend only an active build or code-review run", 3);
        var oldRevision = state.Approval.Revision;
        if (revision <= oldRevision) throw new CliFailure("state", $"amendment revision {revision} is not newer than the active revision {oldRevision}", 3);
        var oldHash = state.Approval.PlanHash;
        var newHash = Hashing.Sha256Hex(humanPlan);
        if (string.Equals(oldHash, newHash, StringComparison.Ordinal)) throw new CliFailure("state", "amendment plan is byte-identical to the active plan", 3);
        var completed = Math.Max(0, state.Workflow.NextTaskNumber - 1);
        var oldTasks = state.Workflow.Tasks;
        var newTasks = CanonicalText.ParseTasks(humanPlan);
        if (completed > newTasks.Count) throw new CliFailure("state", "amendment removes already completed tasks", 3);
        for (var index = 0; index < completed; index++)
        {
            var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index] : null;
            if (oldTask is null || oldTask.Hash != newTasks[index].Hash) throw new CliFailure("state", $"amendment changes completed task {index + 1}", 3);
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
        if ((begin >= 0) != (end >= 0) || (begin >= 0 && end < begin) || beginCount > 1 || endCount > 1) throw new CliFailure("state", "Git exclude contains a malformed Forge managed block", 3);
    }

    private static string ExcludePath(RepositoryIdentity repository)
    {
        var result = new GitClient(repository.WorkspaceRoot).Run(["rev-parse", "--git-path", "info/exclude"]);
        var raw = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout.Trim() : Path.Combine(repository.GitCommonDir, "info", "exclude");
        var path = Path.IsPathRooted(raw) ? raw : Path.Combine(repository.WorkspaceRoot, raw);
        var full = Path.GetFullPath(path);
        OwnershipGuards.EnsureSafeDirectory(Path.GetDirectoryName(full)!);
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

    private static void RemoveLastNonce(RepositoryIdentity repository)
    {
        var path = RepositoryPaths.LastNoncePath(repository);
        if (!File.Exists(path)) return;
        OwnershipGuards.EnsureRegularFile(path, "last materialization nonce");
        File.Delete(path);
    }
}
