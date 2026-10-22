using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Jobs;

/// <summary>
/// The act a Job runs: <c>build.next</c> or <c>review.fix</c>, the two Builder acts that may outlast
/// a host's clock on one call, or <c>scout</c>, a question the Orchestrator overlaps with its own
/// work. Plan and code review have only their one-call tools — see docs/adr/0043 and 0044.
/// </summary>
internal sealed class WorkAct
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;

    public WorkAct(IVendor vendor)
        : this(vendor, new PromptLibrary())
    {
    }

    public WorkAct(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    /// <summary>Runs the act and answers with what its one-call tool would.</summary>
    /// <param name="act"><c>build.next</c>, <c>review.fix</c> or <c>scout</c>.</param>
    /// <param name="run">The Run the act belongs to.</param>
    /// <param name="selection">The Builder's model, effort and speed; null for <c>scout</c>, which runs on the persisted Scout selection.</param>
    /// <param name="ct">Cancels the act.</param>
    /// <param name="decisions">The code-review decision batch, for <c>review.fix</c> only.</param>
    /// <param name="fixAttemptId">The fix attempt, for <c>review.fix</c> only.</param>
    /// <param name="fixFindingIds">The findings to fix, for <c>review.fix</c> only.</param>
    /// <param name="note">The Orchestrator's Fix note, for <c>review.fix</c> only.</param>
    /// <param name="gate">The verification mode, for <c>review.fix</c> only.</param>
    /// <param name="question">The bounded reconnaissance question, for <c>scout</c> only.</param>
    /// <param name="sessionMode"><c>fresh</c> or <c>continue</c>, for <c>scout</c> only.</param>
    public async Task<string> RunAsync(
        string act,
        RunDirectory run,
        Selection? selection,
        CancellationToken ct,
        OrchestratorDecisionBatch? decisions = null,
        string? fixAttemptId = null,
        IReadOnlyList<string>? fixFindingIds = null,
        string? note = null,
        string? gate = null,
        string? question = null,
        string? sessionMode = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (act == "review.fix") run.ReadState();
        ValidateArguments(act, selection, decisions, fixAttemptId, fixFindingIds, note, gate, question, sessionMode);

        switch (act)
        {
            case "build.next":
                var buildAct = new Build(_vendor, _prompts);
                var build = await buildAct.NextAsync(run, selection!, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, BuildOutcomeView.Of(build).ToJsonString(), buildAct.SpeedWarning);

            case "review.fix":
                var fixAct = new ReviewFix(_vendor, _prompts);
                var fix = await fixAct.FixAsync(run, selection!, decisions, fixAttemptId,
                                                fixFindingIds, ct, note, gate).ConfigureAwait(false);
                var fixJson = JsonSerializer.Serialize(fix, ContractJson.Default.BuildResult);
                return SpeedWarnings.Attach(run, RaisedFindings.Attach(fixJson, fixAct.RaisedFindingIds),
                                            fixAct.SpeedWarning);

            case "scout":
                var scoutAct = new Scout(_vendor, _prompts);
                var scout = await scoutAct.RunAsync(run, question!, sessionMode!, ct).ConfigureAwait(false);
                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(scout, ForgeToolJson.Default.ScoutReport),
                                            scoutAct.SpeedWarning);

            default:
                throw new ArgumentRejectedException($"unknown work act '{act}'");
        }
    }

    /// <summary>Refuses an act a Job does not run, or arguments its act does not take.</summary>
    /// <param name="act"><c>build.next</c>, <c>review.fix</c> or <c>scout</c>.</param>
    /// <param name="selection">The Builder's model, effort and speed: the model is required, and <c>scout</c> takes none.</param>
    /// <param name="decisions">The code-review decision batch, for <c>review.fix</c> only.</param>
    /// <param name="fixAttemptId">The fix attempt, for <c>review.fix</c> only.</param>
    /// <param name="fixFindingIds">The findings to fix, for <c>review.fix</c> only.</param>
    /// <param name="note">The Orchestrator's Fix note, for <c>review.fix</c> only.</param>
    /// <param name="gate">The verification mode, for <c>review.fix</c> only.</param>
    /// <param name="question">The bounded reconnaissance question, required by <c>scout</c> only.</param>
    /// <param name="sessionMode"><c>fresh</c> or <c>continue</c>, required by <c>scout</c> only.</param>
    internal static void ValidateArguments(
        string act,
        Selection? selection,
        OrchestratorDecisionBatch? decisions = null,
        string? fixAttemptId = null,
        IReadOnlyList<string>? fixFindingIds = null,
        string? note = null,
        string? gate = null,
        string? question = null,
        string? sessionMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);

        if (act is not "build.next" and not "review.fix" and not "scout")
            throw new ArgumentRejectedException($"unknown work act '{act}': a job runs build.next, review.fix or scout");
        if (gate is not null && act != "review.fix")
            throw new ArgumentRejectedException($"gate is not used by {act}");

        if (act == "scout")
        {
            if (selection is not null)
                throw new ArgumentRejectedException("model is not used by scout, which runs on the persisted Scout selection");
            RejectPresent(decisions, nameof(decisions), act);
            RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
            RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
            RejectProvided(note, nameof(note), act);
            Scout.ValidateQuestion(question ?? throw new ArgumentRejectedException("scout requires question"));
            Scout.ValidateSessionMode(sessionMode ?? throw new ArgumentRejectedException("scout requires sessionMode"));
            return;
        }

        RejectPresent(question, nameof(question), act);
        RejectPresent(sessionMode, nameof(sessionMode), act);

        if (selection is null || string.IsNullOrWhiteSpace(selection.Model))
            throw new ArgumentRejectedException($"{act} requires model");

        switch (act)
        {
            case "build.next":
                RejectPresent(decisions, nameof(decisions), act);
                RejectPresent(fixAttemptId, nameof(fixAttemptId), act);
                RejectPresent(fixFindingIds, nameof(fixFindingIds), act);
                RejectProvided(note, nameof(note), act);
                break;
            case "review.fix":
                FixGatePolicy.GateMode(gate);
                if (gate is not null && (fixFindingIds is null || fixFindingIds.Count == 0))
                    throw new ArgumentRejectedException("gate requires non-empty fixFindingIds");
                if (fixFindingIds is null && decisions is null)
                    throw new ArgumentRejectedException($"{act} requires decisions or fixFindingIds");
                if (!string.IsNullOrWhiteSpace(fixAttemptId) && (fixFindingIds is null || fixFindingIds.Count == 0))
                    throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");
                if (fixFindingIds is { Count: > 0 } && string.IsNullOrWhiteSpace(fixAttemptId))
                    throw new ArgumentRejectedException("fixFindingIds requires fixAttemptId");
                if (!string.IsNullOrWhiteSpace(note) && (fixFindingIds is null || fixFindingIds.Count == 0))
                    throw new ArgumentRejectedException("note requires non-empty fixFindingIds");
                break;
        }
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
}

internal static class OrchestrationPreflight
{
    internal static void Validate(RunDirectory run, string act, OrchestratorDecisionBatch? decisions,
                                  string? fixAttemptId, IReadOnlyList<string>? fixFindingIds,
                                  string? note = null, string? gate = null)
    {
        if (act == "review.fix" && !run.ReadState().Approved)
            throw new NotApprovedException(run.RunId);
        var ledger = run.ReadDecisionLedger();
        switch (act)
        {
            case "review.fix":
                var (_, ids, attempt, _) = ReviewFix.ValidateRequest(run, ledger, decisions, fixAttemptId,
                                                                     fixFindingIds, note, gate);
                if (decisions is not null)
                    ledger.ValidateOrchestratorBatch(decisions, LedgerPhase.CodeReview);
                if (ids.Count > 0 && attempt is not { Terminal: true }) ledger.ValidateFixFindingIds(ids);
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
