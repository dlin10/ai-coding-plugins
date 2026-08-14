using System.Text;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Pending;

internal static partial class PendingRuns
{
    public static PendingRun Stage(RepositoryIdentity repository, string draftText, string runId, CursorModelWaiver? reviewerWaiver = null,
                                   bool acceptRisk = false, string? authorizationNote = null)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(draftText)) throw new CliFailure("usage", "Cursor chat plan is required on stdin");
        var text = CanonicalText.NormalizePlan(draftText);
        if (SensitiveInput.IsSensitiveContent(text)) throw new CliFailure("usage", "Cursor chat plan contains withheld sensitive content");
        CanonicalText.ParseTasks(text);
        var scope = RepositoryPaths.ScopeId(repository);
        EnsureNoOtherActiveRun(repository, runId);
        var existing = TryLoad(repository, runId);
        if (existing is not null && existing.Phase == PendingRunPhase.RevisionRequired)
        {
            var reviewCap = existing.ReviewCap;
            var capAudits = existing.CapAudits;
            if (existing.ReviewRound >= existing.ReviewCap)
            {
                if (!acceptRisk || string.IsNullOrWhiteSpace(authorizationNote) || authorizationNote.Length > 16 * 1024)
                    throw new CliFailure("state", "Cursor review cap reached; /forge resume requires --accept-risk with a bounded --authorization-note", 3);
                reviewCap++;
                capAudits = [.. existing.CapAudits, new CursorCapAudit(existing.ReviewCap, reviewCap, authorizationNote, DateTimeOffset.UtcNow.ToString("O"))];
            }
            else if (acceptRisk || authorizationNote is not null)
                throw new CliFailure("usage", "Cursor review cap authorization is only valid after the cap is reached");

            var restaged = existing with
            {
                DraftText = text, Phase = PendingRunPhase.Reviewing, ReviewCap = reviewCap, CapAudits = capAudits, ActiveDispatchId = null,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            Write(repository, restaged);
            return restaged;
        }

        if (existing is not null && existing.Phase is not (PendingRunPhase.Abandoned or PendingRunPhase.Consumed) && existing.DraftText == text) return existing;
        if (existing is not null && existing.Phase is not (PendingRunPhase.Abandoned or PendingRunPhase.Consumed))
            throw new CliFailure("state", "Cursor plan changed; finalize or abandon the existing run first", 3);
        if (reviewerWaiver is null) throw new CliFailure("usage", "Cursor stage requires a reviewer model waiver");
        ValidateWaiver(reviewerWaiver, "reviewer");
        var run = new PendingRun { Workspace = repository.WorkspaceRoot, ScopeId = scope, RunId = runId, DraftText = text, ReviewerWaiver = reviewerWaiver };
        Write(repository, run);
        return run;
    }

    public static PendingRun Record(RepositoryIdentity repository, string runId, string dispatchId, string stage, string response)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        ValidateIdentity(dispatchId, "--dispatch-id");
        if (stage != "plan" || run.Phase != PendingRunPhase.Reviewing || run.ReviewerWaiver is null)
            throw new CliFailure("state", "Cursor review response is not legal", 3);
        if (run.Responses.Any(item => item.DispatchId == dispatchId))
            throw new CliFailure("state", "Cursor reviewer dispatch identity must be fresh for every round", 3);
        var normalized = CanonicalText.Canonicalize(response);
        if (Encoding.UTF8.GetByteCount(normalized) > 256 * 1024) throw new CliFailure("usage", "review response exceeds the size bound");
        var verdictLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("VERDICT:", StringComparison.Ordinal))
                                     .ToArray();
        if (verdictLines.Length != 1 || verdictLines[0] is not ("VERDICT: APPROVED" or "VERDICT: REVISE"))
            throw new CliFailure("usage", "review response must contain exactly one terminal VERDICT line");
        if (!string.Equals(normalized.TrimEnd('\n').Split('\n')[^1], verdictLines[0], StringComparison.Ordinal))
            throw new CliFailure("usage", "review response verdict must be terminal");
        if (normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line.StartsWith("COVERAGE:", StringComparison.Ordinal)))
            throw new CliFailure("usage", "plan review response must not contain a COVERAGE line");
        var verdict = verdictLines[0][9..];
        if (run.ReviewRound >= run.ReviewCap) throw new CliFailure("state", "Cursor review cap reached", 3);
        var reviewLog = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(value => value.Response).Append(normalized)));
        if (SensitiveInput.IsSensitiveContent(reviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        var next = run with
        {
            Phase = verdict == "APPROVED" ? PendingRunPhase.ReviewApproved : PendingRunPhase.RevisionRequired,
            ReviewRound = run.ReviewRound + 1,
            ActiveDispatchId = dispatchId,
            Responses = [.. run.Responses, new(dispatchId, stage, verdict, Hashing.Sha256Hex(normalized), normalized, DateTimeOffset.UtcNow.ToString("O"))],
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        Write(repository, next);
        return next;
    }

    public static PendingRun Finalize(RepositoryIdentity repository, string runId, CursorModelWaiver? builderWaiver = null)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase != PendingRunPhase.ReviewApproved || builderWaiver is null)
            throw new CliFailure("state", "Cursor plan is not review-approved or builder waiver is missing", 3);
        var reviewerWaiver = run.ReviewerWaiver ?? throw new CliFailure("state", "Cursor reviewer waiver is missing", 3);
        ValidateWaiver(builderWaiver, "builder");
        var draft = run.DraftText ?? throw new CliFailure("state", "Cursor reviewed chat plan is unavailable; abandon and restart the run", 3);
        ModelSelections.Validate("reviewer", reviewerWaiver.Model, reviewerWaiver.Effort);
        ModelSelections.Validate("builder", builderWaiver.Model, builderWaiver.Effort);
        if (SensitiveInput.IsSensitiveContent(draft)) throw new CliFailure("usage", "plan contains withheld sensitive content");
        var reviewLog = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(response => response.Response)));
        if (SensitiveInput.IsSensitiveContent(reviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        var next = run with { Phase = PendingRunPhase.Ready, DraftText = null, BuilderWaiver = builderWaiver, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next);
        return next;
    }

    public static PendingRun Abandon(RepositoryIdentity repository, string runId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var current = Load(repository, runId);
        if (current.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed)
            throw new CliFailure("state", "Cursor materializing or consumed runs cannot be abandoned", 3);
        var run = current with { Phase = PendingRunPhase.Abandoned, DraftText = null, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, run);
        return run;
    }

    public static PendingRun Invalidate(RepositoryIdentity repository, string runId, string reason)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new CliFailure("usage", "--reason is required and bounded");
        var current = Load(repository, runId);
        if (current.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed or PendingRunPhase.Abandoned)
            throw new CliFailure("state", "Cursor run cannot be invalidated in its current phase", 3);
        var run = current with
        {
            Phase = PendingRunPhase.RevisionRequired, ActiveDispatchId = null, InvalidationReason = reason, UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        Write(repository, run);
        return run;
    }

    public static PendingRun ResolvePlanDispatch(RepositoryIdentity repository, string dispatchId)
    {
        ValidateIdentity(dispatchId, "--dispatch-id");
        var matches = LoadAll(repository)
                     .Where(run => run.Phase == PendingRunPhase.Reviewing && (run.ActiveDispatchId is null || run.ActiveDispatchId == dispatchId)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new CliFailure("state", "Cursor dispatch does not resolve to one reviewing run", 3);
    }

    private static void EnsureNoOtherActiveRun(RepositoryIdentity repository, string runId)
    {
        foreach (var candidate in LoadAll(repository))
        {
            if (candidate.RunId != runId && candidate.Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
                throw new CliFailure("state", "another Cursor pending run is active for this workspace", 3);
        }
    }
}
