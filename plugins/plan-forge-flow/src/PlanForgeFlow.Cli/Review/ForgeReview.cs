using System.Globalization;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Review;

internal static class ForgeReview
{
    internal static void RequireFreshReviewManifest(string workspace, ForgeState state)
    {
        var manifest = state.Review.Manifest;
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.TreeFingerprint)) throw new CliFailure("state", "a review prepare manifest is required before dispatch", 3);
        ReviewEvidence.Verify(workspace, manifest);
        var expected = manifest.TreeFingerprint;
        var actual = ReviewEvidence.TreeFingerprint(workspace, state);
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new CliFailure("state", "the review prepare manifest is stale; prepare review again before dispatch", 3);
    }

    internal static void RequireApprovedReviewEvidence(string workspace, ForgeState state)
    {
        var review = state.Review;
        if (review.Verdict != "APPROVED" || review.Coverage != "FULL") throw new CliFailure("state", "done requires a full approved review", 3);
        if (state.Dispatch.Pending) throw new CliFailure("state", "done requires a consumed review dispatch", 3);
        if (state.Agents.ReviewerIds.Count == 0 || !string.Equals(state.Agents.LastReviewerDispatchId, state.Dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "done requires a reviewer session for the current dispatch", 3);
        RequireFreshReviewManifest(workspace, state);
        if (review.Manifest?.Coverage != "FULL") throw new CliFailure("state", "done requires fresh full review evidence", 3);

        var critiquePath = review.CritiqueFile;
        if (string.IsNullOrWhiteSpace(critiquePath)) throw new CliFailure("state", "done requires the approved critique file", 3);
        var absolute = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        if (!ReviewEvidence.IsContained(workspace, absolute) || !File.Exists(absolute)) throw new CliFailure("state", "approved critique file is missing or outside the workspace", 3);
        if ((File.GetAttributes(absolute) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(absolute).Length > 100 * 1024) throw new CliFailure("state", "approved critique file is oversized or symlinked", 3);
        var critiqueHash = Hashing.Sha256File(absolute);
        if (!review.CritiqueFiles.Any(entry => string.Equals(entry.Path, absolute, WorkspacePathPolicy.Comparison) && string.Equals(entry.Hash, critiqueHash, StringComparison.Ordinal))) throw new CliFailure("state", "approved critique file is not bound to the recorded review history", 3);
        try
        {
            var decision = ReviewDecisionReader.Read(absolute, workspace, DispatchStage.Code);
            if (review.VerdictFile != decision.Path || review.VerdictHash != decision.Hash || decision.Verdict != "APPROVED" || decision.Coverage != "FULL") throw new FormatException("review decision is not an approved full verdict");
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

    internal static ReviewManifest Prepare(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var state = context.RequireState();
        if (state.Workflow.Phase != ForgePhase.CodeReview) throw new CliFailure("state", "review prepare requires code-review phase", 3);
        var forge = Path.Combine(workspace, ".forge");
        OwnershipGuards.EnsureDirectory(forge);
        var scope = state.RepositoryScopeId;
        foreach (var reference in new[] { $"refs/plan-forge/{scope}/head-base", $"refs/plan-forge/{scope}/worktree-base" })
        {
            var baseline = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (baseline.ExitCode != 0) throw new CliFailure("state", $"review prepare requires the pinned baseline ref {reference}", 3);
        }
        var allowPaths = parsed.GetPathArray("allow-paths");
        if (allowPaths.Count > 0) ForgeWorkflow.RequireAuthorizationNote(parsed);
        var startingFingerprint = ReviewEvidence.TreeFingerprint(workspace, state);
        var stateAllowed = state.Review.AuthorizedPaths
                                .Select(path => path.Replace('\\', '/'))
                                .Where(path => path.Length > 0)
                                .ToHashSet(StringComparer.Ordinal);
        var sensitiveAllowed = allowPaths
                              .Select(item => item.Replace('\\', '/'))
                              .ToHashSet(StringComparer.Ordinal);
        var allowed = stateAllowed.Concat(sensitiveAllowed).ToHashSet(StringComparer.Ordinal);
        var preExistingTracked = ReviewEvidence.PathList(workspace, ["diff", "--name-only", "-z", $"refs/plan-forge/{scope}/head-base", $"refs/plan-forge/{scope}/worktree-base", "--", "."], "could not prepare the pre-existing tracked review diff");
        var inRunTracked = ReviewEvidence.PathList(workspace, ["diff", "--name-only", "-z", $"refs/plan-forge/{scope}/worktree-base", "--", "."], "could not prepare the in-run tracked review diff");
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
        var withheld = new List<WithheldFile>();
        long aggregateBytes = 0;
        foreach (var relative in allPaths)
        {
            var decision = ReviewEvidence.Inspect(workspace, relative, preExisting, stateAllowed, sensitiveAllowed, untracked, baselineUntracked, aggregateBytes);
            if (decision.Reason is not null) withheld.Add(new WithheldFile(relative, decision.Reason));
            else aggregateBytes += decision.Bytes;
        }

        if (parsed.Has("full") && withheld.Count > 0) throw new CliFailure("verdict", "full review evidence is unavailable; resolve the withheld paths or use partial coverage", 2, withheld);
        var coverage = parsed.Has("full") ? "FULL" : allPaths.Length == 0 ? "FULL" : "PARTIAL";
        var withheldPaths = withheld
                           .Select(item => item.Path)
                           .ToHashSet(StringComparer.Ordinal);
        var headBase = $"refs/plan-forge/{scope}/head-base";
        var worktreeBase = $"refs/plan-forge/{scope}/worktree-base";
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
        var evidenceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ReviewEvidence.Files)
        {
            var path = Path.Combine(forge, name);
            ReviewEvidence.EnsureFile(path);
            evidenceHashes[name] = Hashing.Sha256File(path);
        }
        var finalFingerprint = ReviewEvidence.TreeFingerprint(workspace, state);
        if (!string.Equals(startingFingerprint, finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed while review evidence was being prepared; retry review prepare", 3);
        var manifest = new ReviewManifest
        {
            Version = 4,
            Generation = ForgeState.Generation,
            Coverage = coverage,
            AllowPaths = allowPaths,
            SensitiveAllowedPaths = sensitiveAllowed.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            PreExistingAuthorizedPaths = stateAllowed.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            AuthorizedPaths = allowed.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            AuthorizationNote = parsed.Get("authorization-note"),
            ChangedFiles = allPaths.Length,
            PreExistingFiles = preExisting.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            InRunFiles = inRun.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            UntrackedFiles = untracked.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            Withheld = withheld,
            AggregateBytes = aggregateBytes,
            TreeFingerprint = finalFingerprint,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            EvidenceHashes = evidenceHashes,
        };
        DurableFiles.WriteJson(Path.Combine(forge, "review-manifest.json"), manifest, ForgeJsonContext.Default.ReviewManifest);
        StateStore.Update(workspace, state, current =>
        {
            if (!string.Equals(ReviewEvidence.TreeFingerprint(workspace, current), finalFingerprint, StringComparison.Ordinal)) throw new CliFailure("state", "the review tree changed before the review manifest was committed; retry review prepare", 3);
            current.Review.Manifest = manifest;
        });
        return manifest;
    }

    internal static AuthorizationData AuthorizePreexisting(CommandContext context)
    {
        var paths = context.Args.GetRequiredPathArray("authorized-paths");
        if (paths.Count > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
        ForgeWorkflow.RequireAuthorizationNote(context.Args);
        List<string>? combined = null;
        StateStore.Update(context.Workspace, context.RequireState(), state =>
        {
            var existing = state.Review.AuthorizedPaths
                                .Concat(paths)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(path => path, StringComparer.Ordinal)
                                .ToList();
            if (existing.Count > 256) throw new CliFailure("usage", "authorized-paths contains too many entries");
            combined = existing;
            state.Review.AuthorizedPaths = existing;
        });
        return new AuthorizationData(combined ?? paths, context.Args.Get("authorization-note")!);
    }

    internal static VerdictData Verdict(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var state = context.RequireState();
        var dispatch = state.Dispatch;
        var expectedStage = DispatchStages.RequirePendingReviewVerdict(dispatch, parsed.Get("stage"));
        if (expectedStage.Definition().RequiresFreshReview) RequireFreshReviewManifest(workspace, state);
        if (state.Agents.ReviewerIds.Count == 0 || !string.Equals(state.Agents.LastReviewerDispatchId, dispatch.Id, StringComparison.Ordinal)) throw new CliFailure("state", "review verdict requires a registered fresh reviewer session for this dispatch", 3);
        if (expectedStage == DispatchStage.Plan && state.Workflow.Phase != ForgePhase.Locked) throw new CliFailure("state", "plan review verdict requires locked phase", 3);
        if (expectedStage != DispatchStage.Plan && state.Workflow.Phase != ForgePhase.CodeReview) throw new CliFailure("state", "code review verdict requires code-review phase", 3);
        var critiquePath = parsed.GetRequired("critique-file");
        var absoluteCritique = Path.GetFullPath(Path.IsPathRooted(critiquePath) ? critiquePath : Path.Combine(workspace, critiquePath));
        var forgeRoot = Path.Combine(workspace, ".forge");
        OwnershipGuards.EnsureSafeDirectory(forgeRoot);
        if (!ReviewEvidence.IsContained(forgeRoot, absoluteCritique) || !File.Exists(absoluteCritique)) throw new CliFailure("usage", "critique-file must point to a file inside .forge");
        if ((File.GetAttributes(absoluteCritique) & FileAttributes.ReparsePoint) != 0 || new FileInfo(absoluteCritique).Length > 100 * 1024) throw new CliFailure("usage", "critique-file is oversized or symlinked");
        var critique = File.ReadAllText(absoluteCritique);
        if (SensitiveInput.IsSensitiveContent(critique)) throw new CliFailure("verdict", "critique-file contains withheld sensitive content", 2);
        var decision = ReviewDecisionReader.Read(absoluteCritique, workspace, expectedStage);
        var verdict = decision.Verdict;
        var coverage = decision.Coverage;
        if (expectedStage.Definition().RequiresFreshReview && verdict == "APPROVED")
        {
            if (coverage != "FULL" || state.Review.Manifest?.Coverage != "FULL") throw new CliFailure("verdict", "approval requires a fresh FULL review manifest and FULL critique coverage", 2);
        }
        if (parsed.Has("accept-risk")) ForgeWorkflow.RequireAuthorizationNote(parsed);
        var critiqueHash = Hashing.Sha256File(absoluteCritique);
        var currentRound = expectedStage == DispatchStage.Plan ? state.Workflow.Round : state.Review.FixRound;
        var roundCap = expectedStage == DispatchStage.Plan ? state.Workflow.MaxRounds : state.Workflow.MaxFixRounds;
        var nextRound = currentRound + 1;
        if (verdict == "REVISE" && nextRound > roundCap)
        {
            if (expectedStage != DispatchStage.Plan) throw new CliFailure("verdict", $"{expectedStage.ToWireName()} review retry cap reached; extend it with run set --key max-fix-rounds --value <next> --accept-risk --authorization-note", 2);
            if (!parsed.Has("accept-risk")) throw new CliFailure("verdict", $"{expectedStage.ToWireName()} review retry cap reached; require --accept-risk with --authorization-note", 2);
        }
        state = StateStore.Update(workspace, state, value =>
        {
            if (Hashing.Sha256File(absoluteCritique) != critiqueHash) throw new CliFailure("state", "critique changed while the verdict was being recorded", 3);
            if (Hashing.Sha256File(decision.Path) != decision.Hash) throw new CliFailure("state", "review decision changed while the verdict was being recorded", 3);
            value.Dispatch.Pending = false;
            value.Review.Verdict = verdict;
            value.Review.Coverage = coverage;
            value.Review.CritiqueFile = absoluteCritique;
            value.Review.VerdictFile = decision.Path;
            value.Review.VerdictHash = decision.Hash;
            value.Review.CritiqueFiles.RemoveAll(item => string.Equals(item.Path, absoluteCritique, WorkspacePathPolicy.Comparison));
            if (value.Review.CritiqueFiles.Count >= 256) throw new CliFailure("state", "critique history exceeds the size bound", 3);
            value.Review.CritiqueFiles.Add(new CritiqueEntry(absoluteCritique, critiqueHash));
            if (expectedStage == DispatchStage.Plan) value.Workflow.Round = nextRound;
            else if (verdict == "REVISE") value.Review.FixRound = nextRound;
            if (expectedStage.Definition().RequiresFreshReview) value.Workflow.Phase = verdict == "APPROVED" ? ForgePhase.Done : ForgePhase.CodeReview;
        });
        return new VerdictData(
            verdict == "APPROVED" ? "approved" : nextRound > roundCap ? "deadlock" : "revise",
            expectedStage.ToWireName(),
            verdict,
            coverage,
            nextRound,
            state.Review);
    }
}
