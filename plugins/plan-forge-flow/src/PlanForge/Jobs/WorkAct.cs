using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Jobs;

internal sealed class WorkAct
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;
    private readonly IReviewGit? _git;

    public WorkAct(IVendor vendor)
        : this(vendor, new PromptLibrary())
    {
    }

    public WorkAct(IVendor vendor, PromptLibrary prompts, IReviewGit? git = null)
    {
        _vendor = vendor;
        _prompts = prompts;
        _git = git;
    }

    public async Task<string> RunAsync(
        string act,
        RunDirectory run,
        string? planDraft,
        Selection? selection,
        string? findings,
        string? deferred,
        string? revision,
        bool userGrantedRound,
        CancellationToken ct,
        string? question = null,
        string? sessionMode = null,
        OrchestratorDecisionBatch? decisions = null,
        string? fixAttemptId = null,
        IReadOnlyList<string>? fixFindingIds = null)
    {
        ValidateArguments(act, planDraft, selection, findings, deferred, revision, userGrantedRound,
                          question, sessionMode, decisions, fixAttemptId, fixFindingIds);
        ArgumentNullException.ThrowIfNull(run);

        switch (act)
        {
            case "plan.review":
                var planReview = new PlanReview(_vendor, _prompts);
                var critique = await planReview.ReviewAsync(run, planDraft, selection!, revision, deferred, userGrantedRound, ct,
                                                            orchestratorDecisions: decisions)
                                               .ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(critique, ContractJson.Default.Critique),
                                            planReview.SpeedWarning);

            case "build.next":
                var buildAct = new Build(_vendor, _prompts);
                var build = await buildAct.NextAsync(run, selection!, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(build, ForgeToolJson.Default.BuildOutcome),
                                            buildAct.SpeedWarning);

            case "review.code":
                var git = _git ?? new GitClient(run.ReadState().WorkspaceRoot);
                var codeReviewAct = new CodeReview(_vendor, _prompts, git);
                var codeReview = await codeReviewAct.ReviewAsync(run, selection!, userGrantedRound, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(codeReview, ContractJson.Default.Critique),
                                            codeReviewAct.SpeedWarning);

            case "review.fix":
                var fixAct = new ReviewFix(_vendor, _prompts);
                var fix = await fixAct.FixAsync(run, selection!, decisions, fixAttemptId,
                                                fixFindingIds, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(fix, ContractJson.Default.BuildResult),
                                            fixAct.SpeedWarning);

            case "scout":
                var scoutAct = new Scout(_vendor, _prompts);
                var scout = await scoutAct.RunAsync(run, question!, sessionMode!, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(scout, ForgeToolJson.Default.ScoutDigest),
                                            scoutAct.SpeedWarning);

            default:
                throw new ArgumentRejectedException($"unknown work act '{act}'");
        }
    }

    internal static void ValidateArguments(
        string act,
        string? planDraft,
        Selection? selection,
        string? findings,
        string? deferred,
        string? revision,
        bool userGrantedRound,
        string? question = null,
        string? sessionMode = null,
        OrchestratorDecisionBatch? decisions = null,
        string? fixAttemptId = null,
        IReadOnlyList<string>? fixFindingIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);

        if (act is not "plan.review" and not "build.next" and not "review.code" and not "review.fix" and not "scout")
            throw new ArgumentRejectedException($"unknown work act '{act}'");

        if (act == "scout")
        {
            RejectPresent(planDraft, nameof(planDraft), act);
            RejectPresent(selection?.Model, nameof(selection), act);
            RejectPresent(selection?.Effort, nameof(selection), act);
            RejectPresent(findings, nameof(findings), act);
            RejectPresent(deferred, nameof(deferred), act);
            RejectPresent(revision, nameof(revision), act);
            RejectPresent(decisions, nameof(decisions), act);
            RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
            RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
            RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
            if (question is null)
                throw new ArgumentRejectedException("scout requires question");
            Scout.ValidateQuestion(question);
            if (sessionMode is null)
                throw new ArgumentRejectedException("scout requires sessionMode");
            Scout.ValidateSessionMode(sessionMode);
            return;
        }

        if (selection is null || string.IsNullOrWhiteSpace(selection.Model))
            throw new ArgumentRejectedException($"{act} requires model");

        switch (act)
        {
            // planDraft is optional here and nowhere else: forge.plan.write puts the draft on disk
            // ahead of the round, and the act reads it from there when the start omits it. Whether
            // there is one to read is a question about the run, so forge.work.start asks it through
            // PlanReview.RequireDraft rather than here.
            case "plan.review":
                RejectProvided(findings, nameof(findings), act);
                RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
                RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
                break;
            case "build.next":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
                RejectPresent(decisions, nameof(decisions), act);
                RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
                RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
                break;
            case "review.code":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                RejectPresent(decisions, nameof(decisions), act);
                RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
                RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
                break;
            case "review.fix":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
                if (fixFindingIds is null && decisions is null)
                    throw new ArgumentRejectedException($"{act} requires decisions or fixFindingIds");
                if (!string.IsNullOrWhiteSpace(fixAttemptId) && (fixFindingIds is null || fixFindingIds.Count == 0))
                    throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");
                if (fixFindingIds is { Count: > 0 } && string.IsNullOrWhiteSpace(fixAttemptId))
                    throw new ArgumentRejectedException("fixFindingIds requires fixAttemptId");
                break;
        }

        RejectPresent(question, nameof(question), act);
        RejectPresent(sessionMode, nameof(sessionMode), act);
    }

    private static void RejectProvided(string? value, string argumentName, string act)
    {
        if (!string.IsNullOrWhiteSpace(value))
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectPresent(string? value, string argumentName, string act)
    {
        if (value is not null)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectPresent(object? value, string argumentName, string act)
    {
        if (value is not null)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectProvided(bool value, string argumentName, string act)
    {
        if (value)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }
}

internal static class OrchestrationPreflight
{
    internal static void Validate(RunDirectory run, string act, OrchestratorDecisionBatch? decisions,
                                  string? fixAttemptId, IReadOnlyList<string>? fixFindingIds)
    {
        var ledger = run.ReadDecisionLedger();
        switch (act)
        {
            case "plan.review":
                if (decisions is not null)
                    ledger.ValidateOrchestratorBatch(decisions, LedgerPhase.PlanReview);
                return;

            case "review.fix":
                if (!run.ReadState().Approved)
                    throw new NotApprovedException(run.RunId);
                var ids = fixFindingIds is null ? [] : ledger.NormalizeFixFindingIds(fixFindingIds);
                if (decisions is not null && ids.Count > 0
                    && decisions.Decisions.Any(decision => ids.Contains(decision.FindingId)))
                    throw new DecisionLedgerRequestException("a fix finding ID cannot also appear in the decision batch");
                if (decisions is not null)
                    ledger.ValidateOrchestratorBatch(decisions, LedgerPhase.CodeReview);

                if (ids.Count == 0) return;
                var attempt = ledger.FindFixAttempt(fixAttemptId!);
                if (attempt is not null && !attempt.FixFindingIds.SequenceEqual(ids, StringComparer.Ordinal))
                    throw new DecisionLedgerRequestException($"fixAttemptId '{fixAttemptId}' was used with a different fixFindingIds set");
                if (attempt is null || !attempt.Terminal)
                    ledger.ValidateFixFindingIds(ids);
                return;

            default:
                if (decisions is not null)
                    throw new ArgumentRejectedException($"decisions are not used by {act}");
                if (fixAttemptId is not null || fixFindingIds is not null)
                    throw new ArgumentRejectedException($"fix attempt arguments are not used by {act}");
                return;
        }
    }
}
