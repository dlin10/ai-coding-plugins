using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static void RequireFreshReviewManifest(string workspace, JsonObject state)
    {
        var manifest = state["review"]!["manifest"] as JsonObject;
        if (manifest is null || string.IsNullOrWhiteSpace(manifest["treeFingerprint"]?.GetValue<string>())) throw new CliFailure("state", "a review prepare manifest is required before dispatch", 3);
        ReviewEvidence.Verify(workspace, manifest);
        var expected = manifest["treeFingerprint"]!.GetValue<string>();
        var actual = ReviewEvidence.TreeFingerprint(workspace, state);
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new CliFailure("state", "the review prepare manifest is stale; prepare review again before dispatch", 3);
    }
    
    private static void RequireApprovedReviewEvidence(string workspace, JsonObject state)
    {
        var review = state["review"]!.AsObject();
        if (review["verdict"]?.GetValue<string>() != "APPROVED" || review["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("state", "done requires a full approved review", 3);
        if (state["dispatch"]!["pending"]?.GetValue<bool>() == true) throw new CliFailure("state", "done requires a consumed review dispatch", 3);
        if (state["agents"]!["reviewerIds"] is not JsonArray reviewerIds || reviewerIds.Count == 0 || !string.Equals(state["agents"]!["lastReviewerDispatchId"]?.GetValue<string>(), state["dispatch"]!["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "done requires a reviewer session for the current dispatch", 3);
        RequireFreshReviewManifest(workspace, state);
        if (review["manifest"] is not JsonObject manifest || manifest["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("state", "done requires fresh full review evidence", 3);
    
        var critiquePath = review["critiqueFile"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(critiquePath)) throw new CliFailure("state", "done requires the approved critique file", 3);
        var absolute = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        if (!ReviewEvidence.IsContained(workspace, absolute) || !File.Exists(absolute)) throw new CliFailure("state", "approved critique file is missing or outside the workspace", 3);
        if ((File.GetAttributes(absolute) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(absolute).Length > 100 * 1024) throw new CliFailure("state", "approved critique file is oversized or symlinked", 3);
        var critiqueHash = Hashing.Sha256File(absolute);
        if (review["critiqueFiles"] is not JsonArray critiqueFiles || !critiqueFiles.Any(item => item is JsonObject entry && string.Equals(entry["path"]?.GetValue<string>(), absolute, PathComparison()) && string.Equals(entry["hash"]?.GetValue<string>(), critiqueHash, StringComparison.Ordinal))) throw new CliFailure("state", "approved critique file is not bound to the recorded review history", 3);
        try
        {
            var decision = ReadReviewDecision(absolute, workspace, "code");
            if (review["verdictFile"]?.GetValue<string>() != decision.Path || review["verdictHash"]?.GetValue<string>() != decision.Hash || decision.Verdict != "APPROVED" || decision.Coverage != "FULL") throw new FormatException("review decision is not an approved full verdict");
        }
        catch (CliFailure error) when (error.Code is "usage" or "verdict")
        {
            throw new CliFailure("state", "approved critique decision is missing or invalid", 3);
        }
        catch (Exception error)
        {
            throw new CliFailure("state", $"approved critique decision is invalid: {error.Message}", 3);
        }
    }
    
    private static void ValidateObservedSelection(ParsedArgs parsed, string expectedModel, string expectedEffort, bool requireObservation = false)
    {
        var suppliedModel = parsed.Get("model");
        var suppliedEffort = parsed.Get("effort")?.ToLowerInvariant();
        if ((suppliedModel is null) != (suppliedEffort is null)) throw new CliFailure("usage", "--model and --effort must be supplied together");
        if (requireObservation && suppliedModel is null) throw new CliFailure("usage", "session registration requires --model and --effort");
        if (suppliedEffort == "ultra") throw new CliFailure("usage", "ultra reasoning effort is unsupported");
        if (suppliedModel is not null && (!string.Equals(suppliedModel, expectedModel, StringComparison.Ordinal) || !string.Equals(suppliedEffort, expectedEffort, StringComparison.Ordinal)))
        {
            throw new CliFailure("state", "the requested model and effort do not match the pinned selection", 3);
        }
    }
    
    
    private static JsonObject PrepareReview(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        RequirePlanHash(state, parsed);
        if (state["workflow"]!["phase"]?.GetValue<string>() != "code-review") throw new CliFailure("state", "review prepare requires code-review phase", 3);
        var forge = Path.Combine(workspace, ".forge");
        OwnershipGuards.EnsureSafeDirectory(forge);
        Directory.CreateDirectory(forge);
        foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
        {
            var baseline = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (baseline.ExitCode != 0) throw new CliFailure("state", $"review prepare requires the pinned baseline ref {reference}", 3);
        }
        var allowPaths = ParsePathArray(parsed.Get("allow-paths"), "allow-paths");
        if (allowPaths.Count > 0) RequireAuthorizationNote(parsed);
        var startingFingerprint = ReviewEvidence.TreeFingerprint(workspace, state);
        var stateAllowed = (state["review"]!["authorizedPaths"] as JsonArray ?? new JsonArray())
                          .Select(item => item?.GetValue<string>().Replace('\\', '/') ?? string.Empty)
                          .Where(path => path.Length > 0)
                          .ToHashSet(StringComparer.Ordinal);
        var sensitiveAllowed = allowPaths
                              .Select(item => item!.GetValue<string>().Replace('\\', '/'))
                              .ToHashSet(StringComparer.Ordinal);
        var allowed = stateAllowed.Concat(sensitiveAllowed).ToHashSet(StringComparer.Ordinal);
        var preExistingTracked = ReviewEvidence.PathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/head-base", "refs/plan-forge/worktree-base", "--", "."], "could not prepare the pre-existing tracked review diff");
        var inRunTracked = ReviewEvidence.PathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/worktree-base", "--", "."], "could not prepare the in-run tracked review diff");
        var currentUntracked = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not inspect untracked review files");
        var baselineUntracked = ReviewEvidence.BaselineUntracked(state);
        var currentUntrackedSet = currentUntracked.ToHashSet(StringComparer.Ordinal);
        var preExisting = preExistingTracked.ToHashSet(StringComparer.Ordinal);
        var inRun = inRunTracked.ToHashSet(StringComparer.Ordinal);
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        var untrackedEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in currentUntracked)
        {
            if (!baselineUntracked.TryGetValue(path, out var baselineHash))
            {
                inRun.Add(path);
                untracked.Add(path);
                untrackedEvidence.Add(path);
                continue;
            }
    
            var absolute = Path.GetFullPath(Path.Combine(workspace, path));
            if (!File.Exists(absolute) || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0 || !string.Equals(Hashing.Sha256File(absolute), baselineHash, StringComparison.Ordinal))
            {
                inRun.Add(path);
                untracked.Add(path);
                untrackedEvidence.Add(path);
            }
        }
        foreach (var path in baselineUntracked.Keys)
        {
            if (!currentUntrackedSet.Contains(path) && !preExisting.Contains(path)) inRun.Add(path);
        }
    
        var allPaths = preExisting.Concat(inRun).Concat(untracked).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var withheld = new JsonArray();
        long aggregateBytes = 0;
        foreach (var relative in allPaths)
        {
            var decision = ReviewEvidence.Inspect(workspace, relative, preExisting, stateAllowed, sensitiveAllowed, untracked, baselineUntracked, aggregateBytes);
            if (decision.Reason is not null) withheld.Add((JsonNode)new JsonObject { ["path"] = relative, ["reason"] = decision.Reason });
            else aggregateBytes += decision.Bytes;
        }
    
        if (parsed.Has("full") && withheld.Count > 0) throw new CliFailure("verdict", "full review evidence is unavailable; resolve the withheld paths or use partial coverage", 2, withheld.DeepClone());
        var coverage = parsed.Has("full") ? "FULL" : allPaths.Length == 0 ? "FULL" : "PARTIAL";
        var preExistingArray = new JsonArray(preExisting.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var inRunArray = new JsonArray(inRun.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var untrackedArray = new JsonArray(untracked.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray());
        var withheldPaths = withheld
                           .Select(item => item?.AsObject()["path"]?.GetValue<string>())
                           .Where(path => !string.IsNullOrWhiteSpace(path))
                           .ToHashSet(StringComparer.Ordinal);
        var manifest = new JsonObject
        {
            ["version"] = 3,
            ["generation"] = "v2",
            ["coverage"] = coverage,
            ["allowPaths"] = allowPaths,
            ["sensitiveAllowedPaths"] = new JsonArray(sensitiveAllowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["preExistingAuthorizedPaths"] = new JsonArray(stateAllowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["authorizedPaths"] = new JsonArray(allowed.OrderBy(path => path, StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
            ["authorizationNote"] = parsed.Get("authorization-note"),
            ["changedFiles"] = allPaths.Length,
            ["preExistingFiles"] = preExistingArray,
            ["inRunFiles"] = inRunArray,
            ["untrackedFiles"] = untrackedArray,
            ["withheld"] = withheld,
            ["aggregateBytes"] = aggregateBytes,
            ["treeFingerprint"] = null,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var headBase = "refs/plan-forge/head-base";
        var worktreeBase = "refs/plan-forge/worktree-base";
        var reviewablePreExisting = preExisting.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var reviewableInRun = inRun.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var reviewableUntracked = untrackedEvidence.Where(path => !withheldPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var preExistingPatch = ReviewEvidence.DiffOutput(workspace, ["diff", "--binary", headBase, worktreeBase], reviewablePreExisting, "could not capture pre-existing review evidence");
        var inRunPatch = ReviewEvidence.DiffOutput(workspace, ["diff", "--binary", worktreeBase], reviewableInRun, "could not capture in-run review evidence");
        var untrackedPatch = ReviewEvidence.BuildUntrackedEvidence(workspace, reviewableUntracked, sensitiveAllowed);
        DurableFiles.WriteAtomic(Path.Combine(forge, "pre-existing.patch"), preExistingPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "in-run.patch"), inRunPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "untracked-review.patch"), untrackedPatch);
        DurableFiles.WriteAtomic(Path.Combine(forge, "changed-files.txt"), string.Join(Environment.NewLine, allPaths) + (allPaths.Length == 0 ? string.Empty : Environment.NewLine));
        var evidenceHashes = new JsonObject();
        foreach (var name in ReviewEvidence.Files)
        {
            var path = Path.Combine(forge, name);
            ReviewEvidence.EnsureFile(path);
            evidenceHashes[name] = Hashing.Sha256File(path);
        }
        manifest["evidenceHashes"] = evidenceHashes;
        var finalFingerprint = ReviewEvidence.TreeFingerprint(workspace, state);
        if (!string.Equals(startingFingerprint, finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed while review evidence was being prepared; retry review prepare", 3);
        manifest["treeFingerprint"] = finalFingerprint;
        DurableFiles.WriteJson(Path.Combine(forge, "review-manifest.json"), manifest);
        StateStore.Update(workspace, state, current =>
        {
            if (!string.Equals(ReviewEvidence.TreeFingerprint(workspace, current), finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed before the review manifest was committed; retry review prepare", 3);
            current["review"]!["manifest"] = manifest.DeepClone();
        });
        return manifest;
    }
    
    
    private static JsonObject AuthorizePreexisting(string workspace, ParsedArgs parsed)
    {
        var raw = parsed.GetRequired("authorized-paths");
        var paths = ParsePathArray(raw, "authorized-paths");
        if (paths.Count > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
        RequireAuthorizationNote(parsed);
        JsonArray? combined = null;
        StateStore.Update(workspace, state =>
        {
            var existing = (state["review"]!["authorizedPaths"] as JsonArray ?? new JsonArray())
                          .Select(item => item?.GetValue<string>())
                          .Where(path => !string.IsNullOrWhiteSpace(path))
                          .Select(path => path!)
                          .Concat(paths.Select(item => item!.GetValue<string>()))
                          .Distinct(StringComparer.Ordinal)
                          .OrderBy(path => path, StringComparer.Ordinal)
                          .ToArray();
            if (existing.Length > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
            combined = new JsonArray(existing.Select(path => (JsonNode)path).ToArray());
            state["review"]!["authorizedPaths"] = combined.DeepClone();
        });
        return new JsonObject { ["authorizedPaths"] = combined ?? paths, ["authorizationNote"] = parsed.Get("authorization-note") };
    }
    
    private static JsonObject Verdict(string workspace, ParsedArgs parsed)
    {
        var state = StateStore.Load(workspace);
        var dispatch = state["dispatch"]!.AsObject();
        if (!dispatch["pending"]!.GetValue<bool>() || dispatch["stage"]?.GetValue<string>() is not ("plan" or "code" or "fix")) throw new CliFailure("state", "review verdict requires a pending plan, code, or fix dispatch", 3);
        var expectedStage = parsed.Get("stage") ?? dispatch["stage"]?.GetValue<string>();
        if (expectedStage is not ("plan" or "code" or "fix-review") || !string.Equals(expectedStage, dispatch["stage"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "review verdict stage does not match the pending dispatch", 3);
        if (expectedStage is "code" or "fix-review") RequireFreshReviewManifest(workspace, state);
        if (state["agents"]!["reviewerIds"] is not JsonArray reviewerIds || reviewerIds.Count == 0 || !string.Equals(state["agents"]!["lastReviewerDispatchId"]?.GetValue<string>(), dispatch["id"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "review verdict requires a registered fresh reviewer session for this dispatch", 3);
        if (expectedStage == "plan" && state["workflow"]!["phase"]?.GetValue<string>() != "locked") throw new CliFailure("state", "plan review verdict requires locked phase", 3);
        if (expectedStage is "code" or "fix-review" && state["workflow"]!["phase"]?.GetValue<string>() != "code-review") throw new CliFailure("state", "code review verdict requires code-review phase", 3);
        var critiquePath = parsed.GetRequired("critique-file");
        var absoluteCritique = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        var forgeRoot = Path.Combine(workspace, ".forge");
        OwnershipGuards.EnsureSafeDirectory(forgeRoot);
        if (!ReviewEvidence.IsContained(forgeRoot, absoluteCritique) || !File.Exists(absoluteCritique)) throw new CliFailure("usage", "critique-file must point to a file inside .forge");
        if ((File.GetAttributes(absoluteCritique) & FileAttributes.ReparsePoint) != 0 || new FileInfo(absoluteCritique).Length > 100 * 1024) throw new CliFailure("usage", "critique-file is oversized or symlinked");
        var critique = File.ReadAllText(absoluteCritique);
        if (SensitiveInput.IsSensitiveContent(critique)) throw new CliFailure("verdict", "critique-file contains withheld sensitive content", 2);
        var decision = ReadReviewDecision(absoluteCritique, workspace, expectedStage);
        var verdict = decision.Verdict;
        var coverage = decision.Coverage;
        if (expectedStage is "code" or "fix-review" && verdict == "APPROVED")
        {
            if (coverage != "FULL" || state["review"]!["manifest"]!["coverage"]?.GetValue<string>() != "FULL") throw new CliFailure("verdict", "approval requires a fresh FULL review manifest and FULL critique coverage", 2);
        }
        if (parsed.Has("accept-risk")) RequireAuthorizationNote(parsed);
        var critiqueHash = Hashing.Sha256File(absoluteCritique);
        var currentRound = expectedStage == "plan"
                               ? state["workflow"]!["round"]!.GetValue<int>()
                               : state["review"]!["fixRound"]!.GetValue<int>();
        var roundCap = expectedStage == "plan"
                           ? state["workflow"]!["maxRounds"]!.GetValue<int>()
                           : state["workflow"]!["maxFixRounds"]!.GetValue<int>();
        var nextRound = currentRound + 1;
        if (verdict == "REVISE" && nextRound > roundCap)
        {
            if (expectedStage != "plan") throw new CliFailure("verdict", $"{expectedStage} review retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 2);
            if (!parsed.Has("accept-risk")) throw new CliFailure("verdict", $"{expectedStage} review retry cap reached; require --accept-risk with --authorization-note", 2);
        }
        state = StateStore.Update(workspace, state, value =>
        {
            if (Hashing.Sha256File(absoluteCritique) != critiqueHash) throw new CliFailure("state", "critique changed while the verdict was being recorded", 3);
            if (Hashing.Sha256File(decision.Path) != decision.Hash) throw new CliFailure("state", "review decision changed while the verdict was being recorded", 3);
            value["dispatch"]!["pending"] = false;
            value["review"]!["verdict"] = verdict;
            value["review"]!["coverage"] = coverage;
            value["review"]!["critiqueFile"] = absoluteCritique;
            value["review"]!["verdictFile"] = decision.Path;
            value["review"]!["verdictHash"] = decision.Hash;
            var critiqueFiles = value["review"]!["critiqueFiles"]!.AsArray();
            for (var index = critiqueFiles.Count - 1; index >= 0; index--)
            {
                if (critiqueFiles[index] is JsonObject item && string.Equals(item["path"]?.GetValue<string>(), absoluteCritique, PathComparison())) critiqueFiles.RemoveAt(index);
            }
            if (critiqueFiles.Count >= 256) throw new CliFailure("state", "critique history exceeds the size bound", 3);
            critiqueFiles.Add((JsonNode)new JsonObject { ["path"] = absoluteCritique, ["hash"] = critiqueHash });
            if (expectedStage == "plan") value["workflow"]!["round"] = nextRound;
            else if (verdict == "REVISE") value["review"]!["fixRound"] = nextRound;
            if (expectedStage is "code" or "fix-review") value["workflow"]!["phase"] = verdict == "APPROVED" ? "done" : "code-review";
        });
        return new JsonObject { ["action"] = verdict == "APPROVED" ? "approved" : nextRound > roundCap ? "deadlock" : "revise", ["stage"] = expectedStage, ["verdict"] = verdict, ["coverage"] = coverage, ["round"] = nextRound, ["review"] = state["review"]!.DeepClone() };
    }
}
