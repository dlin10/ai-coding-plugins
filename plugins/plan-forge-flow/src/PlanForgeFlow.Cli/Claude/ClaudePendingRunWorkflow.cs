using System.Text;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Pending;

internal static partial class PendingRuns
{
    public static PendingRun StageClaude(RepositoryIdentity repository, string draftText, string runId, ProviderSelection reviewer)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(draftText)) throw new CliFailure("usage", "Claude plan is required on stdin");
        var text = CanonicalText.NormalizePlan(draftText);
        if (SensitiveInput.IsSensitiveContent(text)) throw new CliFailure("usage", "Claude plan contains withheld sensitive content");
        CanonicalText.ParseTasks(text);
        EnsureNoOtherActiveRun(repository, runId, HostKind.Claude);
        var existing = TryLoad(repository, runId, HostKind.Claude);
        if (existing?.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed)
            throw new CliFailure("state", "Claude materializing or consumed run cannot be revised", 3);
        if (existing is not null && existing.Phase != PendingRunPhase.Abandoned && existing.DraftText == text &&
            SameSelection(existing.ReviewerSelection, reviewer)) return existing;

        var now = DateTimeOffset.UtcNow.ToString("O");
        var run = new PendingRun
        {
            Host = HostKind.Claude,
            Workspace = repository.WorkspaceRoot,
            ScopeId = RepositoryPaths.ScopeId(repository),
            RunId = runId,
            DraftText = text,
            ReviewerSelection = reviewer,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            InvalidationReason = existing is null or { Phase: PendingRunPhase.Abandoned } ? null : "plan revision invalidated reviews and builder hold",
        };
        Write(repository, run);
        return run;
    }

    public static PendingRun RecordClaude(RepositoryIdentity repository, string runId, string dispatchId, string response)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        var run = Load(repository, runId, HostKind.Claude);
        ValidateIdentity(dispatchId, "--dispatch-id");
        if (run.Phase != PendingRunPhase.Reviewing || run.ReviewerSelection is null)
            throw new CliFailure("state", "Claude review response is not legal", 3);
        if (run.Responses.Any(item => item.DispatchId == dispatchId))
            throw new CliFailure("state", "Claude reviewer identity must be fresh for every round", 3);
        var normalized = CanonicalText.Canonicalize(response);
        if (Encoding.UTF8.GetByteCount(normalized) > 256 * 1024) throw new CliFailure("usage", "review response exceeds the size bound");
        var verdictLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                     .Where(line => line.StartsWith("VERDICT:", StringComparison.Ordinal)).ToArray();
        var roslynLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Where(line => line.StartsWith("ROSLYN:", StringComparison.Ordinal)).ToArray();
        if (verdictLines.Length != 1 || verdictLines[0] is not ("VERDICT: APPROVED" or "VERDICT: REVISE") ||
            !string.Equals(normalized.TrimEnd('\n').Split('\n')[^1], verdictLines[0], StringComparison.Ordinal))
            throw new CliFailure("usage", "review response must end with exactly one VERDICT: APPROVED or VERDICT: REVISE line");
        if (roslynLines.Length != 1 || roslynLines[0] != "ROSLYN: NOT_APPLICABLE" &&
            !roslynLines[0].StartsWith("ROSLYN: USED — ", StringComparison.Ordinal) &&
            !roslynLines[0].StartsWith("ROSLYN: FALLBACK — ", StringComparison.Ordinal))
            throw new CliFailure("usage", "Claude review response must contain exactly one valid ROSLYN audit marker");
        if (run.ReviewRound >= run.ReviewCap) throw new CliFailure("state", "Claude review cap reached", 3);
        var verdict = verdictLines[0][9..];
        var next = run with
        {
            Phase = verdict == "APPROVED" ? PendingRunPhase.ReviewApproved : PendingRunPhase.RevisionRequired,
            ReviewRound = run.ReviewRound + 1,
            ActiveDispatchId = dispatchId,
            Responses = [.. run.Responses, new(dispatchId, "plan", verdict, Hashing.Sha256Hex(normalized), normalized,
                                                DateTimeOffset.UtcNow.ToString("O"))],
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        Write(repository, next);
        return next;
    }

    public static PendingRun FinalizeClaude(RepositoryIdentity repository,
                                            string runId,
                                            ProviderSelection builder,
                                            string builderHoldId,
                                            Func<ToolCheckData>? capabilityProbe = null)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        ValidateIdentity(builderHoldId, "--builder-hold-id");
        if (builder.Provider == ProviderKind.Anthropic) ClaudeCapabilities.RequirePersistentBuilder(capabilityProbe);
        var run = Load(repository, runId, HostKind.Claude);
        if (run.Phase != PendingRunPhase.ReviewApproved || run.DraftText is null)
            throw new CliFailure("state", "Claude plan is not review-approved", 3);
        var next = run with
        {
            Phase = PendingRunPhase.Ready,
            BuilderSelection = builder,
            BuilderHoldId = builderHoldId,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        Write(repository, next);
        return next;
    }

    public static PendingRun InvalidateClaude(RepositoryIdentity repository, string runId, string reason)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new CliFailure("usage", "--reason is required and bounded");
        var run = Load(repository, runId, HostKind.Claude);
        if (run.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed or PendingRunPhase.Abandoned)
            throw new CliFailure("state", "Claude run cannot be invalidated in its current phase", 3);
        var next = run with
        {
            Phase = PendingRunPhase.RevisionRequired,
            ReviewRound = 0,
            Responses = [],
            ActiveDispatchId = null,
            BuilderSelection = null,
            BuilderHoldId = null,
            InvalidationReason = reason,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        Write(repository, next);
        return next;
    }

    public static PendingRun AbandonClaude(RepositoryIdentity repository, string runId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        var run = Load(repository, runId, HostKind.Claude);
        if (run.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed)
            throw new CliFailure("state", "Claude materializing or consumed runs cannot be abandoned", 3);
        var next = run with
        {
            Phase = PendingRunPhase.Abandoned,
            DraftText = null,
            BuilderSelection = null,
            BuilderHoldId = null,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        Write(repository, next);
        return next;
    }

    internal static string ReadExactClaudePlan(RepositoryIdentity repository, PendingRun run, string planFile)
    {
        var expected = run.Phase switch
        {
            PendingRunPhase.Ready => run.DraftText,
            PendingRunPhase.Materializing => run.Materialization?.PlanText,
            _ => null,
        };
        if (expected is null || run.ReviewerSelection is null || run.BuilderSelection is null || string.IsNullOrWhiteSpace(run.BuilderHoldId))
            throw new CliFailure("state", "Claude pending run is not ready", 3);
        var path = Path.GetFullPath(planFile);
        if (!File.Exists(path)) throw new CliFailure("state", "Claude plan file does not exist", 3);
        OwnershipGuards.EnsureSafeDirectory(Path.GetDirectoryName(path)!);
        OwnershipGuards.EnsureRegularFile(path, "Claude plan");
        if (new FileInfo(path).Length > 256 * 1024) throw new CliFailure("usage", "Claude plan exceeds the size bound");
        string plan;
        try { plan = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException) { throw new CliFailure("usage", "Claude plan must be valid UTF-8"); }
        if (!string.Equals(plan, expected, StringComparison.Ordinal) || run.Materialization is { } transaction &&
            Hashing.Sha256Hex(plan) != transaction.PlanHash)
            throw new CliFailure("state", "Claude plan file does not exactly match the reviewed snapshot", 3);
        return plan;
    }

    private static void EnsureNoOtherActiveRun(RepositoryIdentity repository, string runId, HostKind host)
    {
        foreach (var candidate in LoadAll(repository, host))
        {
            if (candidate.RunId != runId && candidate.Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
                throw new CliFailure("state", "another Claude pending run is active for this workspace", 3);
        }
    }
}
