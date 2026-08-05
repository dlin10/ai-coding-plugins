using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Codex;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
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
                                              bool amendment)
    {
        var plan = CanonicalText.NormalizePlan(humanPlan);
        var normalizedReviewLog = CanonicalText.NormalizeReviewLog(reviewLog);
        var reviewerSelection = ToPinnedSelection("reviewer", reviewer);
        var builderSelection = ToPinnedSelection("builder", builder);

        using var stateLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        ForgeState state;
        if (amendment)
        {
            state = StateStore.Load(repository.WorkspaceRoot);
            ValidateAmendment(state, plan);
            state = state.DeepCopy();
            state.Workflow.Amendment = true;
            state.Dispatch = ForgeStateSchema.CreateDispatch();
            state.Agents.BuilderId = null;
            state.Agents.LastBuilderDispatchId = null;
            state.Review = ForgeStateSchema.CreateReview(state.Review.CritiqueFiles);
        }
        else
        {
            ResetRun(repository);
            state = StateStore.CreateEmpty();
            state.Workflow.Phase = ForgePhase.Materialized;
        }

        var forgeDirectory = Path.Combine(repository.WorkspaceRoot, ".forge");
        OwnershipGuards.EnsureDirectory(forgeDirectory);
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN.md"), plan);
        DurableFiles.WriteAtomic(Path.Combine(forgeDirectory, "PLAN-REVIEW-LOG.md"), normalizedReviewLog);

        state.Workflow.Round = completedRounds;
        state.Workflow.MaxRounds = maxRounds;
        state.Models.Reviewer = reviewerSelection;
        state.Models.Builder = builderSelection;
        DurableFiles.WriteJson(StateStore.StatePath(repository.WorkspaceRoot), state, ForgeJsonContext.Default.ForgeState);
        UpsertManagedExclude(repository);

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

    public static void Cleanup(string workspace, bool purgeAgents = false)
    {
        var repository = RepositoryPaths.Identify(workspace);
        using (ForgeStateLock.Acquire(workspace))
        {
            DeleteForgeDirectory(workspace);
            RemoveManagedExclude(repository);
            RemoveBaselineRefs(workspace);
        }
        PendingPlan.Delete(workspace);

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
        RemoveManagedExclude(repository);
        RemoveBaselineRefs(repository.WorkspaceRoot);
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

    private static void RemoveBaselineRefs(string workspace)
    {
        foreach (var reference in new[] { "refs/plan-forge/head-base", "refs/plan-forge/worktree-base" })
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

    private static void RemoveManagedExclude(RepositoryIdentity repository)
    {
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
