using System.Text;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Review;

internal static class CursorReviewEvidence
{
    public static PendingRun Record(RepositoryIdentity repository, string dispatchId, string stage, string response)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        PendingRuns.ValidateIdentity(dispatchId, "--dispatch-id");
        var (normalized, verdict, coverage) = ParseResponse(response);
        var run = ResolveMaterializedDispatch(repository, dispatchId, stage);
        if (run.Responses.Any(item => item.DispatchId == dispatchId))
            throw new CliFailure("state", "Cursor code review response has already been recorded for this dispatch", 3);
        var forge = Path.Combine(repository.WorkspaceRoot, ".forge");
        var statePath = StateStore.StatePath(repository.WorkspaceRoot);
        if (!File.Exists(statePath)) throw new CliFailure("state", "Cursor code evidence requires matching materialized state", 3);
        using var workspaceLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != HostKind.Cursor || !state.Dispatch.Pending || state.Dispatch.Id != dispatchId || state.Dispatch.Stage?.ToWireName() != stage ||
            state.SourceRun?.TransactionId != run.TransactionId)
            throw new CliFailure("state", "Cursor code evidence dispatch does not match materialized state", 3);
        var evidence = Path.Combine(forge, $"cursor-review-{dispatchId}.md");
        DurableFiles.WriteAtomic(evidence, normalized);
        DurableFiles.WriteJson(evidence + ".json", new ReviewDecision(verdict, coverage), ForgeJsonContext.Default.ReviewDecision);
        var next = run with
        {
            ActiveDispatchId = dispatchId,
            Responses = [.. run.Responses, new(dispatchId, stage, verdict, Hashing.Sha256Hex(normalized), normalized, DateTimeOffset.UtcNow.ToString("O"))],
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        PendingRuns.Save(repository, next);
        return next;
    }

    private static PendingRun ResolveMaterializedDispatch(RepositoryIdentity repository, string dispatchId, string stage)
    {
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != HostKind.Cursor || !state.Dispatch.Pending || state.Dispatch.Id != dispatchId || state.Dispatch.Stage?.ToWireName() != stage ||
            state.SourceRun is not { Source: var source } identity || !source.StartsWith("cursor:", StringComparison.Ordinal))
            throw new CliFailure("state", "Cursor dispatch does not match materialized state", 3);
        var run = PendingRuns.Load(repository, source[7..]);
        return run.Phase == PendingRunPhase.Consumed && run.TransactionId == identity.TransactionId
                   ? run
                   : throw new CliFailure("state", "Cursor run is not consumed", 3);
    }

    private static (string Text, string Verdict, string Coverage) ParseResponse(string response)
    {
        var normalized = CanonicalText.Canonicalize(response);
        if (Encoding.UTF8.GetByteCount(normalized) > 512 * 1024) throw new CliFailure("usage", "review response exceeds the size bound");
        var verdicts = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("VERDICT:", StringComparison.Ordinal))
                                 .ToArray();
        var coverage = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("COVERAGE:", StringComparison.Ordinal))
                                 .ToArray();
        if (verdicts.Length != 1 || verdicts[0] is not ("VERDICT: APPROVED" or "VERDICT: REVISE") || coverage.Length != 1 ||
            coverage[0] is not ("COVERAGE: FULL" or "COVERAGE: PARTIAL") || normalized.TrimEnd('\n').Split('\n')[^1] != verdicts[0])
            throw new CliFailure("usage", "code review response has invalid terminal evidence");
        return (normalized, verdicts[0][9..], coverage[0][10..]);
    }

}
