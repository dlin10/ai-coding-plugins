using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Cursor;

internal static partial class PendingRuns
{
    public static PendingRun BeginMaterialize(RepositoryIdentity repository, string runId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        return BeginMaterialize(repository, runId, repositoryLock);
    }

    public static PendingRun BeginMaterialize(RepositoryIdentity repository, string runId, RepositoryRunLock repositoryLock)
    {
        repositoryLock.Require(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase is PendingRunPhase.Consumed or PendingRunPhase.Materializing) return run;
        if (run.Phase != PendingRunPhase.Ready || run.ReviewerWaiver is null || run.BuilderWaiver is null)
            throw new CliFailure("state", "Cursor plan is not ready for materialization", 3);
        var plan = DiscoverNativePlan(repository, run);
        var reviewer = ModelSelections.Validate("reviewer", run.ReviewerWaiver.Model, run.ReviewerWaiver.Effort);
        var builder = ModelSelections.Validate("builder", run.BuilderWaiver.Model, run.BuilderWaiver.Effort);
        var id = Hashing.Nonce();
        var review = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(response => response.Response)));
        var refs = new[] { $"refs/plan-forge/{run.ScopeId}/owner", $"refs/plan-forge/{run.ScopeId}/head-base", $"refs/plan-forge/{run.ScopeId}/worktree-base" };
        var transaction = new MaterializationTransaction(id,
                                                         run.RunId,
                                                         run.ScopeId,
                                                         plan,
                                                         Hashing.Sha256Hex(plan),
                                                         Hashing.Sha256Hex(review),
                                                         reviewer.Model,
                                                         reviewer.Effort,
                                                         builder.Model,
                                                         builder.Effort,
                                                         refs,
                                                         [".materialization-transaction", "PLAN.md", "PLAN-REVIEW-LOG.md", "state.json"],
                                                         DateTimeOffset.UtcNow.ToString("O"));
        var next = run with
        {
            Phase = PendingRunPhase.Materializing, TransactionId = id, Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        Write(repository, next);
        Fault("snapshot-written");
        return next;
    }

    public static PendingRun BindMaterializationState(RepositoryIdentity repository,
                                                      string runId,
                                                      ForgeState state,
                                                      RepositoryRunLock repositoryLock)
    {
        repositoryLock.Require(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase != PendingRunPhase.Materializing || run.Materialization is null)
            throw new CliFailure("state", "Cursor materialization state cannot be bound in the current phase", 3);
        if (state.Workflow.Phase != ForgePhase.Build)
            throw new CliFailure("state", "Cursor materialization can bind only its final build state", 3);
        var stateHash = Hashing.Sha256Hex(JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState) + "\n");
        if (run.Materialization.StateHash is { } existingHash)
        {
            if (existingHash != stateHash) throw new CliFailure("state", "Cursor materialization state conflicts with its transaction", 3);
            return run;
        }
        var transaction = run.Materialization with { StateHash = stateHash };
        var next = run with { Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next);
        return next;
    }

    public static void Consume(RepositoryIdentity repository, string runId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        Consume(repository, runId, repositoryLock);
    }

    public static void Consume(RepositoryIdentity repository, string runId, RepositoryRunLock repositoryLock)
    {
        repositoryLock.Require(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        var transaction = run.Materialization is null ? null : run.Materialization with { PlanText = null };
        var next = run with { Phase = PendingRunPhase.Consumed, Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next);
    }

    public static void VerifyMaterialization(RepositoryIdentity repository, PendingRun run)
    {
        var transaction = run.Materialization ?? throw new CliFailure("state", "Cursor materialization transaction is missing", 3);
        var forge = Path.Combine(repository.WorkspaceRoot, ".forge");
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != HostKind.Cursor || state.RepositoryScopeId != transaction.ScopeId || state.SourceRun?.Source != "cursor:" + transaction.RunId ||
            state.SourceRun.TransactionId != transaction.Id || state.Models.Reviewer is not { } reviewer || reviewer.Model != transaction.ReviewerModel ||
            reviewer.Effort != transaction.ReviewerEffort || state.Models.Builder is not { } builder || builder.Model != transaction.BuilderModel ||
            builder.Effort != transaction.BuilderEffort || state.ReviewerGuarantee?.Kind != "advisory" ||
            state.ApprovalGuarantee?.Kind != "advisory") throw new CliFailure("state", "Cursor materialization state does not match its transaction", 3);
        if (transaction.StateHash is null || Hashing.Sha256File(StateStore.StatePath(repository.WorkspaceRoot)) != transaction.StateHash)
            throw new CliFailure("state", "Cursor materialization state artifact is not bound to its transaction", 3);
        if (run.Phase == PendingRunPhase.Materializing)
        {
            var actualArtifacts = Directory.EnumerateFileSystemEntries(forge).Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var expectedArtifacts = transaction.ExpectedArtifacts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!actualArtifacts.SequenceEqual(expectedArtifacts, StringComparer.Ordinal))
                throw new CliFailure("state", "Cursor materialization directory contains conflicting artifacts", 3);
        }

        foreach (var artifact in transaction.ExpectedArtifacts)
        {
            var path = Path.Combine(forge, artifact);
            if (!File.Exists(path)) throw new CliFailure("state", $"Cursor materialization artifact is missing: {artifact}", 3);
        }
        if (File.ReadAllText(Path.Combine(forge, ".materialization-transaction")) != transaction.Id + "\n")
            throw new CliFailure("state", "Cursor materialization marker does not match its transaction", 3);
        if (Hashing.Sha256Hex(File.ReadAllText(Path.Combine(forge, "PLAN.md"))) != transaction.PlanHash ||
            Hashing.Sha256Hex(File.ReadAllText(Path.Combine(forge, "PLAN-REVIEW-LOG.md"))) != transaction.ReviewHash)
            throw new CliFailure("state", "Cursor materialization artifacts do not match their transaction", 3);
        if (state.Workflow.Phase != ForgePhase.Build) throw new CliFailure("state", "Cursor materialization did not reach build phase", 3);
        foreach (var reference in transaction.ExpectedRefs)
        {
            var result = new GitClient(repository.WorkspaceRoot).Run(["rev-parse", "--verify", reference]);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                throw new CliFailure("state", $"Cursor materialization ref is missing: {reference}", 3);
            var expected = reference.EndsWith("/worktree-base", StringComparison.Ordinal) ? state.Baselines.Worktree : state.Baselines.Head;
            if (!string.Equals(result.Stdout.Trim(), expected, StringComparison.Ordinal))
                throw new CliFailure("state", $"Cursor materialization ref conflicts: {reference}", 3);
        }
        Materializer.VerifyManagedExclude(repository);
    }

    internal static string ExpectedPreamble(RepositoryIdentity repository, string runId) =>
        $"> **Plan Forge execution gate (advisory):** Before any repository write, run Plan Forge `{ExpectedMaterializeCommand(repository, runId)}` using the installed launcher. Stop if it does not succeed. Cursor can bypass this instruction; approval enforcement is advisory.";

    private static string DiscoverNativePlan(RepositoryIdentity repository, PendingRun run)
    {
        var cursorHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
        var nativeRoot = Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(cursorHome)
                                                           ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor")
                                                           : cursorHome,
                                                       "plans"));
        var roots = new List<string> { nativeRoot };
#if DEBUG
        roots.Add(repository.WorkspaceRoot);
#endif
        var runMarker = $"<!-- plan-forge-flow:run={run.RunId};";
        var matches = new List<(string Path, string Plan)>();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var root in roots.Distinct(pathComparer))
        {
            if (!Directory.Exists(root)) continue;
            OwnershipGuards.EnsureSafeDirectory(root);
            foreach (var candidate in Directory.EnumerateFiles(root, "*.plan.md", SearchOption.TopDirectoryOnly))
            {
                var path = Path.GetFullPath(candidate);
                if (!IsUnder(path, root)) throw new CliFailure("state", "Cursor native plan path escaped its storage root", 3);
                OwnershipGuards.EnsureRegularFile(path, "Cursor native plan");
                var oversized = new FileInfo(path).Length > 256 * 1024;
                var text = oversized ? ReadPrefix(path, 256 * 1024) : File.ReadAllText(path, Encoding.UTF8);
                if (!text.Contains(runMarker, StringComparison.Ordinal)) continue;
                if (oversized) throw new CliFailure("usage", "Cursor native plan exceeds the size bound");
                matches.Add((path, CanonicalText.NormalizePlan(text)));
            }
        }
        if (matches.Count == 0) throw new CliFailure("state", "no Cursor native plan matches this run and workspace marker", 3);
        if (matches.Count > 1)
            throw new CliFailure("state", "multiple Cursor native plans match this run and workspace marker:\n" +
                                          string.Join("\n", matches.Select(match => match.Path)), 3);
        var plan = matches[0].Plan;
        var marker = $"<!-- plan-forge-flow:run={run.RunId};workspace={run.ScopeId} -->";
        var preamble = ExpectedPreamble(repository, run.RunId);
        if (Count(plan, "<!-- plan-forge-flow:run=") != 1 || Count(plan, marker) != 1)
            throw new CliFailure("state", "Cursor native plan marker is missing or invalid", 3);
        if (Count(plan, preamble) != 1 || Count(plan, marker + "\n" + preamble) != 1)
            throw new CliFailure("state", "Cursor native plan preamble is missing, duplicated, misplaced, or invalid", 3);
        if (SensitiveInput.IsSensitiveContent(plan)) throw new CliFailure("usage", "Cursor native plan contains withheld sensitive content");
        CanonicalText.ParseTasks(plan);
        return plan;
    }

    private static string ExpectedMaterializeCommand(RepositoryIdentity repository, string runId) =>
        $"plan materialize --host cursor --workspace \"{repository.WorkspaceRoot}\" --run-id \"{runId}\"";

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        WorkspacePathPolicy.Comparison);

    private static string ReadPrefix(string path, int maxChars)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private static int Count(string value, string needle) => value.Split(needle).Length - 1;
}
