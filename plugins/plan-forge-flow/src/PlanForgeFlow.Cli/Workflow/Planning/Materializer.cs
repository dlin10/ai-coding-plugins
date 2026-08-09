using System.Text.Json;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Cursor;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow.Planning;

internal static class Materializer
{
    private const string ExcludeBegin = "# >>> plan-forge-flow (managed) >>>";
    private const string ExcludeEnd = "# <<< plan-forge-flow (managed) <<<";
    private const string ExcludeBlock = ExcludeBegin + "\n.forge/\n" + ExcludeEnd;

    public static MaterializeData Materialize(RepositoryIdentity repository,
                                              string humanPlan,
                                              string reviewLog,
                                              int completedRounds,
                                              int maxRounds,
                                              ModelSelection reviewer,
                                              ModelSelection builder,
                                               bool amendment,
                                               HostKind host = HostKind.Codex,
                                               Action<ForgeState>? configureState = null,
                                               string? materializationTransactionId = null,
                                               bool repositoryLockHeld = false,
                                               ForgeState? materializationState = null)
    {
        var plan = CanonicalText.NormalizePlan(humanPlan);
        var normalizedReviewLog = CanonicalText.NormalizeReviewLog(reviewLog);
        if (SensitiveInput.IsSensitiveContent(plan)) throw new CliFailure("usage", "plan contains withheld sensitive content");
        if (SensitiveInput.IsSensitiveContent(normalizedReviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        var reviewerSelection = ToPinnedSelection("reviewer", reviewer);
        var builderSelection = ToPinnedSelection("builder", builder);

        using RepositoryRunLock? repositoryLock = repositoryLockHeld ? null : RepositoryRunLock.Acquire(repository, host);
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        var expectedScope = RepositoryPaths.ScopeId(repository);
        ForgeState? existingState = null;
        if (File.Exists(StateStore.StatePath(repository.WorkspaceRoot)))
        {
            existingState = StateStore.Load(repository.WorkspaceRoot);
            if (existingState.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
            if (existingState.RepositoryScopeId != expectedScope) throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
        }
        ForgeState state;
        if (amendment)
        {
            state = existingState ?? throw new CliFailure("state", "plan materialize --amendment requires existing Forge state", 3);
            ValidateAmendment(state, plan);
            state = state.DeepCopy();
            state.Workflow.Amendment = true;
            state.Dispatch = new DispatchState();
            state.Agents.BuilderId = null;
            state.Agents.LastBuilderDispatchId = null;
            state.Review = new ReviewState { CritiqueFiles = state.Review.CritiqueFiles.ToList() };
        }
        else
        {
            if (materializationTransactionId is null) ResetRun(repository);
            state = materializationState?.DeepCopy() ?? StateStore.CreateEmpty(host, expectedScope);
            state.Workflow.Phase = ForgePhase.Materialized;
        }

        state.Workflow.Round = completedRounds;
        state.Workflow.MaxRounds = maxRounds;
        state.Models.Reviewer = reviewerSelection;
        state.Models.Builder = builderSelection;
        configureState?.Invoke(state);
        if (state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
        state.RepositoryScopeId = expectedScope;

        var finalForgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        var forgeDirectory = finalForgeDirectory;
        if (materializationTransactionId is not null)
        {
            if (amendment || host != HostKind.Cursor) throw new CliFailure("state", "transactional materialization is Cursor-only", 3);
            if (Directory.Exists(finalForgeDirectory) || File.Exists(finalForgeDirectory)) throw new CliFailure("state", "Cursor materialization target already exists", 3);
            forgeDirectory = Path.Combine(repository.WorkspaceRoot, $".forge.materializing-{materializationTransactionId}");
            foreach (var candidate in Directory.EnumerateDirectories(repository.WorkspaceRoot, ".forge.materializing-*", SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(candidate, forgeDirectory, WorkspacePathPolicy.Comparison)) throw new CliFailure("state", "another Cursor materialization staging directory exists", 3);
            }
            OwnershipGuards.EnsureDirectory(forgeDirectory);
            var marker = Path.Combine(forgeDirectory, ".materialization-transaction");
            if (File.Exists(marker))
            {
                OwnershipGuards.EnsureRegularFile(marker, "Cursor materialization marker");
                if (File.ReadAllText(marker) != materializationTransactionId + "\n") throw new CliFailure("state", "Cursor materialization staging marker conflicts", 3);
                var expected = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [".materialization-transaction"] = materializationTransactionId + "\n",
                    ["PLAN.md"] = plan,
                    ["PLAN-REVIEW-LOG.md"] = normalizedReviewLog,
                    ["state.json"] = JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState) + "\n",
                };
                foreach (var entry in Directory.EnumerateFileSystemEntries(forgeDirectory))
                {
                    var name = Path.GetFileName(entry);
                    if (!expected.TryGetValue(name, out var expectedContent) || Directory.Exists(entry)) throw new CliFailure("state", "Cursor materialization staging directory contains an unowned artifact", 3);
                    OwnershipGuards.EnsureRegularFile(entry, "Cursor materialization staging artifact");
                    if (File.ReadAllText(entry) != expectedContent) throw new CliFailure("state", $"Cursor materialization staging artifact conflicts: {name}", 3);
                }
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(forgeDirectory).Any()) throw new CliFailure("state", "Cursor materialization staging directory is unowned", 3);
                DurableFiles.WriteAtomic(marker, materializationTransactionId + "\n");
            }
        }
        else OwnershipGuards.EnsureDirectory(forgeDirectory);
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN.md"), plan);
        if (materializationTransactionId is not null) PendingRuns.Fault("cursor-plan-written");
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN-REVIEW-LOG.md"), normalizedReviewLog);

        DurableFiles.WriteJson(Path.Combine(forgeDirectory, "state.json"), state, ForgeJsonContext.Default.ForgeState);
        PendingRuns.Fault("state-configured");
        UpsertManagedExclude(repository);
#if DEBUG
        if (Environment.GetEnvironmentVariable("FORGE_TEST_HOLD_AFTER_EXCLUDE") is { Length: > 0 } holdAfterExclude)
        {
            DurableFiles.WriteAtomic(holdAfterExclude, "ready\n");
            while (File.Exists(holdAfterExclude)) Thread.Sleep(10);
        }
#endif
        if (materializationTransactionId is not null)
        {
            PendingRuns.Fault("cursor-state-written");
            Directory.Move(forgeDirectory, finalForgeDirectory);
        }

        return new MaterializeData(
            amendment ? "forge-amendment-materialized" : "forge-materialized",
            state.Workflow.Phase.ToWireName(),
            reviewerSelection,
            builderSelection);
    }

    private static PinnedSelection ToPinnedSelection(string role, ModelSelection selection)
    {
        var valid = ModelSelections.Validate(role, selection.Model, selection.Effort);
        return new PinnedSelection(valid.Model, valid.Effort);
    }

    public static void Cleanup(string workspace, bool purgeAgents = false, HostKind host = HostKind.Codex)
    {
        var repository = RepositoryPaths.Identify(workspace);
        using (RepositoryRunLock.Acquire(repository, host))
        using (ForgeStateLock.Acquire(workspace))
        {
            var forgeDirectory = Path.Combine(workspace, ".forge");
            if (Directory.Exists(forgeDirectory))
            {
                var statePath = StateStore.StatePath(workspace);
                if (!File.Exists(statePath)) throw new CliFailure("state", "refusing to remove unowned .forge artifacts", 3);
                var state = StateStore.Load(workspace);
                if (state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
                if (state.RepositoryScopeId != RepositoryPaths.ScopeId(repository)) throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
            }
            DeleteForgeDirectory(workspace);
            RemoveBaselineRefs(repository);
            RemoveManagedExclude(repository);
        }
        if (host == HostKind.Codex) PendingPlan.Delete(workspace);

        if (!purgeAgents) return;
        var agents = RepositoryPaths.AgentsDirectory();
        OwnershipGuards.EnsureSafeDirectory(agents);
        foreach (var name in new[] { "forge_builder.toml", "forge_reviewer.toml" })
        {
            var path = Path.Combine(agents, name);
            if (!File.Exists(path)) continue;
            if (OwnershipGuards.IsOwnedAgentFile(path)) File.Delete(path);
        }
    }

    private static void ResetRun(RepositoryIdentity repository)
    {
        DeleteForgeDirectory(repository.WorkspaceRoot);
        RemoveBaselineRefs(repository);
        RemoveManagedExclude(repository);
    }

    private static void DeleteForgeDirectory(string workspace)
    {
        var forgeDirectory = Path.Combine(workspace, ".forge");
        if (!Directory.Exists(forgeDirectory))
        {
            if (File.Exists(forgeDirectory)) throw new CliFailure("state", ".forge must be a directory", 3);
            return;
        }
        OwnershipGuards.EnsureSafeDirectory(forgeDirectory);
        Directory.Delete(forgeDirectory, recursive: true);
    }

    private static void RemoveBaselineRefs(RepositoryIdentity repository)
    {
        var workspace = repository.WorkspaceRoot;
        var scope = RepositoryPaths.ScopeId(repository);
        foreach (var reference in new[] { $"refs/plan-forge/{scope}/owner", $"refs/plan-forge/{scope}/head-base", $"refs/plan-forge/{scope}/worktree-base" })
        {
            var existing = new GitClient(workspace).Run(["rev-parse", "--verify", reference]);
            if (existing.ExitCode != 0) continue;
            var removed = new GitClient(workspace).Run(["update-ref", "-d", reference]);
            if (removed.ExitCode != 0) throw new CliFailure("environment", $"could not remove Git baseline ref {reference}");
        }
    }

    private static void ValidateAmendment(ForgeState state, string humanPlan)
    {
        if (state.Workflow.Phase is not (ForgePhase.Build or ForgePhase.CodeReview)) throw new CliFailure("state", "plan materialize --amendment requires an active build or code-review run", 3);
        var completed = Math.Max(0, state.Workflow.NextTaskNumber - 1);
        var oldTasks = state.Workflow.Tasks;
        var newTasks = CanonicalText.ParseTasks(humanPlan);
        if (completed > newTasks.Count) throw new CliFailure("state", "amendment removes completed tasks", 3);
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
        ValidateManagedExclude(repository);
        var path = ExcludePath(repository);
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        var next = begin >= 0
                       ? text[..begin] + ExcludeBlock + text[(end + ExcludeEnd.Length)..]
                       : text.TrimEnd('\r', '\n') is { Length: > 0 } existing ? existing + "\n" + ExcludeBlock + "\n" : ExcludeBlock + "\n";
        DurableFiles.WriteAtomic(path, next);
    }

    internal static void VerifyManagedExclude(RepositoryIdentity repository)
    {
        ValidateManagedExclude(repository);
        var path = ExcludePath(repository);
        if (!File.Exists(path) || !File.ReadAllText(path).Replace("\r\n", "\n").Contains(ExcludeBlock, StringComparison.Ordinal))
        {
            throw new CliFailure("state", "Cursor materialization managed exclude is missing", 3);
        }
    }

    private static void RemoveManagedExclude(RepositoryIdentity repository)
    {
        var owners = new GitClient(repository.WorkspaceRoot).Run(["for-each-ref", "--format=%(refname)", "refs/plan-forge"]);
        if (owners.ExitCode == 0 && owners.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(reference => reference.EndsWith("/owner", StringComparison.Ordinal))) return;
        ValidateManagedExclude(repository);
        var path = ExcludePath(repository);
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        if (begin < 0) return;
        var next = (text[..begin] + text[(end + ExcludeEnd.Length)..]).TrimEnd('\r', '\n');
        DurableFiles.WriteAtomic(path, next.Length == 0 ? string.Empty : next + Environment.NewLine);
    }
}
