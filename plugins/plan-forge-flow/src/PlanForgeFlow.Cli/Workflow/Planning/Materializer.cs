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

internal sealed record MaterializationContent(string Plan,
                                              string ReviewLog,
                                              int CompletedRounds,
                                              int MaxRounds,
                                              ModelSelection Reviewer,
                                              ModelSelection Builder);

internal static class Materializer
{
    private const string ExcludeBegin = "# >>> plan-forge-flow (managed) >>>";
    private const string ExcludeEnd = "# <<< plan-forge-flow (managed) <<<";
    private const string ExcludeBlock = ExcludeBegin + "\n.forge/\n.forge.materializing-*/\n" + ExcludeEnd;
    private const string MaterializingPrefix = ".forge.materializing-";

    public static MaterializeData MaterializeCodexFresh(RepositoryIdentity repository, MaterializationContent content)
    {
        var materialization = Prepare(content);
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Codex);
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        ValidateExistingState(repository, HostKind.Codex);
        ResetRun(repository);

        var state = StateStore.CreateEmpty(HostKind.Codex, RepositoryPaths.ScopeId(repository));
        state.Workflow.Phase = ForgePhase.Materialized;
        ApplyContent(state, repository, HostKind.Codex, materialization);
        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        OwnershipGuards.EnsureDirectory(forgeDirectory);
        WriteCatalog(forgeDirectory, materialization, state);
        UpsertManagedExclude(repository);
        return Result("forge-materialized", state, materialization);
    }

    public static MaterializeData MaterializeCodexAmendment(RepositoryIdentity repository, MaterializationContent content)
    {
        var materialization = Prepare(content);
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Codex);
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        var existing = ValidateExistingState(repository, HostKind.Codex) ??
                       throw new CliFailure("state", "plan materialize --amendment requires existing Forge state", 3);
        ValidateAmendment(existing, materialization.Plan);

        var state = existing.DeepCopy();
        state.Workflow.Amendment = true;
        state.Dispatch = new DispatchState();
        state.Agents.BuilderId = null;
        state.Agents.LastBuilderDispatchId = null;
        state.Review = new ReviewState { CritiqueFiles = state.Review.CritiqueFiles.ToList() };
        ApplyContent(state, repository, HostKind.Codex, materialization);
        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        OwnershipGuards.EnsureDirectory(forgeDirectory);
        WriteCatalog(forgeDirectory, materialization, state);
        UpsertManagedExclude(repository);
        return Result("forge-amendment-materialized", state, materialization);
    }

    public static MaterializeData MaterializeCursorTransaction(RepositoryIdentity repository,
                                                               RepositoryRunLock repositoryLock,
                                                               MaterializationTransaction transaction,
                                                               MaterializationContent content,
                                                               ForgeState materializationState)
    {
        repositoryLock.Require(repository, HostKind.Cursor);
        var materialization = Prepare(content);
        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        var state = materializationState.DeepCopy();
        ApplyContent(state, repository, HostKind.Cursor, materialization);
        ValidateCursorState(transaction, state, materialization);

        var finalForgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        if (Directory.Exists(finalForgeDirectory) || File.Exists(finalForgeDirectory))
            throw new CliFailure("state", "Cursor materialization target already exists", 3);
        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, MaterializingPrefix + transaction.Id);
        foreach (var candidate in Directory.EnumerateFileSystemEntries(repository.WorkspaceRoot, MaterializingPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(candidate, forgeDirectory, WorkspacePathPolicy.Comparison))
                throw new CliFailure("state", "another Cursor materialization staging directory exists", 3);
        }

        OwnershipGuards.EnsureDirectory(forgeDirectory);
        var marker = Path.Combine(forgeDirectory, ".materialization-transaction");
        var expected = ExpectedCatalog(transaction.Id, materialization, state);
        if (File.Exists(marker))
        {
            OwnershipGuards.EnsureRegularFile(marker, "Cursor materialization marker");
            if (File.ReadAllText(marker) != transaction.Id + "\n")
                throw new CliFailure("state", "Cursor materialization staging marker conflicts", 3);
            foreach (var entry in Directory.EnumerateFileSystemEntries(forgeDirectory))
            {
                var name = Path.GetFileName(entry);
                if (!expected.TryGetValue(name, out var expectedContent) || Directory.Exists(entry))
                    throw new CliFailure("state", "Cursor materialization staging directory contains an unowned artifact", 3);
                OwnershipGuards.EnsureRegularFile(entry, "Cursor materialization staging artifact");
                if (File.ReadAllText(entry) != expectedContent)
                    throw new CliFailure("state", $"Cursor materialization staging artifact conflicts: {name}", 3);
            }
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(forgeDirectory).Any())
                throw new CliFailure("state", "Cursor materialization staging directory is unowned", 3);
            DurableFiles.WriteAtomic(marker, transaction.Id + "\n");
        }

        WriteCatalog(forgeDirectory, materialization, state);
        PendingRuns.Fault("state-configured");
        UpsertManagedExclude(repository);
#if DEBUG
        if (Environment.GetEnvironmentVariable("FORGE_TEST_HOLD_AFTER_EXCLUDE") is { Length: > 0 } holdAfterExclude)
        {
            DurableFiles.WriteAtomic(holdAfterExclude, "ready\n");
            while (File.Exists(holdAfterExclude)) Thread.Sleep(10);
        }
#endif
        PendingRuns.Fault("cursor-state-written");
        Directory.Move(forgeDirectory, finalForgeDirectory);
        return Result("forge-materialized", state, materialization);
    }

    public static void Cleanup(string workspace, bool purgeAgents = false, HostKind host = HostKind.Codex)
    {
        var repository = RepositoryPaths.Identify(workspace);
        using (RepositoryRunLock.Acquire(repository, host))
            using (ForgeStateLock.Acquire(workspace))
            {
                var materializationDirectories = OwnedMaterializationDirectories(workspace);
                var forgeDirectory = Path.Combine(workspace, ".forge");
                if (Directory.Exists(forgeDirectory))
                {
                    var statePath = StateStore.StatePath(workspace);
                    if (!File.Exists(statePath)) throw new CliFailure("state", "refusing to remove unowned .forge artifacts", 3);
                    var state = StateStore.Load(workspace);
                    if (state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
                    if (state.RepositoryScopeId != RepositoryPaths.ScopeId(repository))
                        throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
                }

                DeleteForgeDirectory(workspace);
                foreach (var directory in materializationDirectories) Directory.Delete(directory, recursive: true);
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

    internal static void VerifyManagedExclude(RepositoryIdentity repository)
    {
        ValidateManagedExclude(repository);
        var path = ExcludePath(repository);
        if (!File.Exists(path) || !File.ReadAllText(path).Replace("\r\n", "\n").Contains(ExcludeBlock, StringComparison.Ordinal))
            throw new CliFailure("state", "Cursor materialization managed exclude is missing", 3);
    }

    internal static void EnsureManagedExclude(RepositoryIdentity repository, RepositoryRunLock repositoryLock)
    {
        repositoryLock.Require(repository, HostKind.Cursor);
        UpsertManagedExclude(repository);
    }

    private static PreparedMaterialization Prepare(MaterializationContent content)
    {
        var plan = CanonicalText.NormalizePlan(content.Plan);
        var reviewLog = CanonicalText.NormalizeReviewLog(content.ReviewLog);
        if (SensitiveInput.IsSensitiveContent(plan)) throw new CliFailure("usage", "plan contains withheld sensitive content");
        if (SensitiveInput.IsSensitiveContent(reviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        return new PreparedMaterialization(plan,
                                           reviewLog,
                                           content.CompletedRounds,
                                           content.MaxRounds,
                                           ToPinnedSelection("reviewer", content.Reviewer),
                                           ToPinnedSelection("builder", content.Builder));
    }

    private static void ApplyContent(ForgeState state, RepositoryIdentity repository, HostKind host, PreparedMaterialization materialization)
    {
        if (state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
        state.RepositoryScopeId = RepositoryPaths.ScopeId(repository);
        state.Workflow.Round = materialization.CompletedRounds;
        state.Workflow.MaxRounds = materialization.MaxRounds;
        state.Models.Reviewer = materialization.Reviewer;
        state.Models.Builder = materialization.Builder;
    }

    private static void ValidateCursorState(MaterializationTransaction transaction, ForgeState state, PreparedMaterialization materialization)
    {
        var stateJson = JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState) + "\n";
        if (state.Workflow.Phase != ForgePhase.Build || state.SourceRun?.TransactionId != transaction.Id ||
            Hashing.Sha256Hex(materialization.Plan) != transaction.PlanHash || Hashing.Sha256Hex(materialization.ReviewLog) != transaction.ReviewHash ||
            materialization.Reviewer.Model != transaction.ReviewerModel || materialization.Reviewer.Effort != transaction.ReviewerEffort ||
            materialization.Builder.Model != transaction.BuilderModel || materialization.Builder.Effort != transaction.BuilderEffort ||
            transaction.StateHash is null || Hashing.Sha256Hex(stateJson) != transaction.StateHash)
            throw new CliFailure("state", "Cursor materialization state does not match its transaction", 3);
    }

    private static Dictionary<string, string> ExpectedCatalog(string transactionId, PreparedMaterialization materialization, ForgeState state) =>
        new(StringComparer.Ordinal)
        {
            [".materialization-transaction"] = transactionId + "\n",
            ["PLAN.md"] = materialization.Plan,
            ["PLAN-REVIEW-LOG.md"] = materialization.ReviewLog,
            ["state.json"] = JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState) + "\n",
        };

    private static void WriteCatalog(string forgeDirectory, PreparedMaterialization materialization, ForgeState state)
    {
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN.md"), materialization.Plan);
        if (state.Host == HostKind.Cursor) PendingRuns.Fault("cursor-plan-written");
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN-REVIEW-LOG.md"), materialization.ReviewLog);
        DurableFiles.WriteJson(Path.Combine(forgeDirectory, "state.json"), state, ForgeJsonContext.Default.ForgeState);
    }

    private static MaterializeData Result(string action, ForgeState state, PreparedMaterialization materialization) =>
        new(action, state.Workflow.Phase.ToWireName(), materialization.Reviewer, materialization.Builder);

    private static PinnedSelection ToPinnedSelection(string role, ModelSelection selection)
    {
        var valid = ModelSelections.Validate(role, selection.Model, selection.Effort);
        return new PinnedSelection(valid.Model, valid.Effort);
    }

    private static ForgeState? ValidateExistingState(RepositoryIdentity repository, HostKind host)
    {
        if (!File.Exists(StateStore.StatePath(repository.WorkspaceRoot))) return null;
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
        if (state.RepositoryScopeId != RepositoryPaths.ScopeId(repository))
            throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
        return state;
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

    private static IReadOnlyList<string> OwnedMaterializationDirectories(string workspace)
    {
        var directories = new List<string>();
        foreach (var candidate in Directory.EnumerateFileSystemEntries(workspace, MaterializingPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (!Directory.Exists(candidate) || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new CliFailure("state", "refusing to remove an unowned Cursor materialization staging artifact", 3);
            OwnershipGuards.EnsureSafeDirectory(candidate);
            var transactionId = Path.GetFileName(candidate)[MaterializingPrefix.Length..];
            var marker = Path.Combine(candidate, ".materialization-transaction");
            if (transactionId.Length == 0 || !File.Exists(marker))
                throw new CliFailure("state", "refusing to remove an unowned Cursor materialization staging directory", 3);
            OwnershipGuards.EnsureRegularFile(marker, "Cursor materialization marker");
            if (File.ReadAllText(marker) != transactionId + "\n")
                throw new CliFailure("state", "refusing to remove a conflicting Cursor materialization staging directory", 3);
            directories.Add(candidate);
        }
        return directories;
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
        if (state.Workflow.Phase is not (ForgePhase.Build or ForgePhase.CodeReview))
            throw new CliFailure("state", "plan materialize --amendment requires an active build or code-review run", 3);
        var completed = Math.Max(0, state.Workflow.NextTaskNumber - 1);
        var oldTasks = state.Workflow.Tasks;
        var newTasks = CanonicalText.ParseTasks(humanPlan);
        if (completed > newTasks.Count) throw new CliFailure("state", "amendment removes completed tasks", 3);
        for (var index = 0; index < completed; index++)
        {
            var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index] : null;
            if (oldTask is null || oldTask.Hash != newTasks[index].Hash)
                throw new CliFailure("state", $"amendment changes completed task {index + 1}", 3);
        }
    }

    private static void ValidateManagedExclude(RepositoryIdentity repository)
    {
        var path = ExcludePath(repository);
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new CliFailure("state", "refusing to edit a symlinked Git exclude", 3);
        var text = File.ReadAllText(path);
        var begin = text.IndexOf(ExcludeBegin, StringComparison.Ordinal);
        var end = text.IndexOf(ExcludeEnd, StringComparison.Ordinal);
        var beginCount = Regex.Matches(text, Regex.Escape(ExcludeBegin), RegexOptions.CultureInvariant).Count;
        var endCount = Regex.Matches(text, Regex.Escape(ExcludeEnd), RegexOptions.CultureInvariant).Count;
        if ((begin >= 0) != (end >= 0) || (begin >= 0 && end < begin) || beginCount > 1 || endCount > 1)
            throw new CliFailure("state", "Git exclude contains a malformed Forge managed block", 3);
    }

    private static string ExcludePath(RepositoryIdentity repository)
    {
        var result = new GitClient(repository.WorkspaceRoot).Run(["rev-parse", "--git-path", "info/exclude"]);
        var raw = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout)
                      ? result.Stdout.Trim()
                      : Path.Combine(repository.GitCommonDir, "info", "exclude");
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
                       : text.TrimEnd('\r', '\n') is { Length: > 0 } existing
                           ? existing + "\n" + ExcludeBlock + "\n"
                           : ExcludeBlock + "\n";
        DurableFiles.WriteAtomic(path, next);
    }

    private static void RemoveManagedExclude(RepositoryIdentity repository)
    {
        var owners = new GitClient(repository.WorkspaceRoot).Run(["for-each-ref", "--format=%(refname)", "refs/plan-forge"]);
        if (owners.ExitCode == 0 && owners.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                                                   .Any(reference => reference.EndsWith("/owner", StringComparison.Ordinal))) return;
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

    private sealed record PreparedMaterialization(string Plan,
                                                  string ReviewLog,
                                                  int CompletedRounds,
                                                  int MaxRounds,
                                                  PinnedSelection Reviewer,
                                                  PinnedSelection Builder);
}
