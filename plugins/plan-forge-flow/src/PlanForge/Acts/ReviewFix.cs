using System.Text;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One builder pass over the findings the orchestrator chose to forward. The critic never talks to
/// the builder directly any more: the orchestrator sits between them, dropping findings the
/// approved plan excludes, and what it drops is recorded in the ledger with a reason so the next
/// round's critic treats it as settled.
/// </summary>
internal sealed class ReviewFix(IVendor vendor, PromptLibrary prompts)
{
    /// <summary>
    /// A raised finding has no ID until its batch is applied, so no fix set in the same call can name
    /// it. Shared with the background preflight, which refuses the call before a job exists.
    /// </summary>
    internal const string RaiseWithFixRefused =
        "a batch that raises findings cannot travel with fixFindingIds; raise them in a decisions-only call "
        + "and fix them by the IDs it returns";

    /// <summary>Set when the fix's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    /// <summary>The IDs the call's decision batch raised, empty when it raised none.</summary>
    internal IReadOnlyList<string> RaisedFindingIds { get; private set; } = [];

    /// <summary>
    /// Applies the orchestrator's code-review decisions, then runs one builder turn over exactly the
    /// findings named by <paramref name="fixFindingIds"/> and the run-wide gates after it. A call
    /// without fix IDs applies its decisions and starts no builder.
    /// </summary>
    /// <param name="run">The run whose ledger, state and timeline the fix reads and writes.</param>
    /// <param name="selection">The builder's model, effort and speed.</param>
    /// <param name="decisionBatch">Decisions and raises to apply before the fix, or null for none.</param>
    /// <param name="fixAttemptId">The retryable attempt the fix IDs belong to; required with them.</param>
    /// <param name="fixFindingIds">The unresolved code-review findings to fix; empty for a decisions-only call.</param>
    /// <param name="ct">Cancels the builder turn and the gates.</param>
    /// <param name="note">
    /// The orchestrator's own framing for the findings, shown to the builder after them and recorded
    /// in the Flow log. Only a call that fixes something has anyone to show it to.
    /// </param>
    /// <returns>
    /// The builder's result as the gates left it, the saved result of a completed attempt, or a
    /// no-op result for a decisions-only call.
    /// </returns>
    internal async Task<BuildResult> FixAsync(RunDirectory run,
                                              Selection selection,
                                              OrchestratorDecisionBatch? decisionBatch,
                                              string? fixAttemptId,
                                              IReadOnlyList<string>? fixFindingIds,
                                              CancellationToken ct,
                                              string? note = null)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var ledger = run.ReadDecisionLedger();
        var ids = fixFindingIds is null ? [] : ledger.NormalizeFixFindingIds(fixFindingIds);
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(fixAttemptId))
            throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(note))
            throw new ArgumentRejectedException("note requires non-empty fixFindingIds");
        if (ids.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(fixAttemptId))
                throw new ArgumentRejectedException("fixFindingIds requires fixAttemptId");
            if (decisionBatch is not null && decisionBatch.Decisions.Any(decision => ids.Contains(decision.FindingId)))
                throw new DecisionLedgerRequestException("a fix finding ID cannot also appear in the decision batch");
            if (decisionBatch?.Raises is { Count: > 0 })
                throw new DecisionLedgerRequestException(RaiseWithFixRefused);
        }
        if (!string.IsNullOrWhiteSpace(note)) SensitiveInput.Guard(note, "the orchestrator's note");

        try
        {
            if (decisionBatch is not null)
                ledger.ValidateOrchestratorBatch(decisionBatch, LedgerPhase.CodeReview);
        }
        catch (DecisionLedgerRequestException error)
        {
            if (decisionBatch is not null)
                run.AppendFlowDecisionRejected("Review fix", decisionBatch.DecisionBatchId, error.Message);
            throw;
        }

        var existing = string.IsNullOrWhiteSpace(fixAttemptId) ? null : ledger.FindFixAttempt(fixAttemptId);
        if (existing is not null && !existing.FixFindingIds.SequenceEqual(ids, StringComparer.Ordinal))
            throw new DecisionLedgerRequestException($"fixAttemptId '{fixAttemptId}' was used with a different fixFindingIds set");

        if (decisionBatch is not null)
        {
            try
            {
                var response = ledger.Apply(decisionBatch, LedgerPhase.CodeReview);
                run.AppendFlowDecisionBatch("Review fix", decisionBatch, response);
                response.ThrowIfConflict();
                RaisedFindingIds = response.Result.RaisedFindingIds ?? [];
            }
            catch (DecisionLedgerRequestException error)
            {
                run.AppendFlowDecisionRejected("Review fix", decisionBatch.DecisionBatchId, error.Message);
                throw;
            }
        }

        if (existing is { Terminal: true, LastResult: not null })
        {
            run.AppendFlowFixAttemptNoOp(fixAttemptId!, ids, existing.LastResult);
            return existing.LastResult;
        }
        if (ids.Count > 0) ledger.ValidateFixFindingIds(ids);

        if (ids.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(fixAttemptId))
                throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");

            var skipped = NoFixesResult();
            run.AppendFlowFix(state.CodeReviewRounds, string.Empty, null, skipped);
            return skipped;
        }

        var findings = ledger.RenderFixFindings(ids);
        SensitiveInput.Guard(findings, "ledger-derived fix findings");

        // A session covers one code-review round: the round's first fix starts fresh, with the Brief,
        // and its later calls and retries resume. See docs/adr/0026.
        var scope = BuilderSession.FixScope(state.CodeReviewRounds);
        var resumeToken = BuilderSession.ResumeToken(state, vendor.Id, scope);
        var prompt = Compose(findings, note, state.PendingGateFailure,
                             resumeToken is null ? state.BuilderInstructions : null);
        if (resumeToken is null)
            prompt = BuilderBrief.Prepend(prompt, run.ReadPlan());
        SensitiveInput.Guard(prompt, "the ledger-derived code-review fixes");

        await using var builder = await vendor.StartAsync(
            new RoleSpec(VendorRole.Builder, prompts.Load(vendor.Id, VendorRole.Builder),
                         state.BuilderRoots, WorkerTools.Effective(state.WorkerTools),
                         new WorkerTelemetryContext(run.TelemetryPath, run.Log, "review_fix",
                                                    Round: state.CodeReviewRounds)),
            selection, resumeToken, ct);

        BuildResult reported;
        try
        {
            reported = await BuilderTurn.RunAsync(builder, state.WorkspaceRoot, prompt, ct);
        }
        catch (TurnCutShortException cutShort)
        {
            ledger.RecordFixAttempt(fixAttemptId!, ids, null, false);
            run.AppendFlowCutShort($"Fixes — round {state.CodeReviewRounds}", cutShort.FilesWritten,
                                   fixAttemptId, ids);
            run.WriteState(BuilderSession.Record(state, builder, vendor.Id, scope, resumeToken) with
            {
                PendingGateFailure = Gatekeeper.CutShortBrief(cutShort.FilesWritten)
            });
            throw;
        }

        var plan = run.ReadPlan();
        var killed = builder.KilledBackgroundTasks;
        SpeedWarning = builder.SpeedWarning;
        var result = await Gatekeeper.CheckAsync(reported, PlanGates.RunWideGates(plan),
                                                 PlanGates.HasRunWideGates(plan), killed, state, GateRunner.FIX_TIMEOUT, ct);
        var closes = result.Gate?.Outcome == "passed"
                     || (result.Gate?.Outcome == "not_executable"
                         && result.Status == "done"
                         && result.Verification.Outcome == "passed");

        DecisionLedgerRequestException? closureError = null;
        if (closes)
        {
            var evidence = new[] { result.Gate?.Output, result.Gate?.Detail, result.Verification.Evidence }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? "host gate passed for this fix attempt";
            try
            {
                var closureBatch = new DecisionBatchRequest(
                    $"{fixAttemptId}:automatic-gate",
                    [], [], ids.Select(id => new LedgerClosureDecision(
                        id, LedgerClosureKind.AutomaticGate, LedgerDecisionMaker.Orchestrator,
                        "closed by the fix attempt", evidence)).ToArray());
                var closure = ledger.Apply(closureBatch);
                run.AppendFlowDecisionBatch("Review fix automatic gate", closureBatch, closure);
                closure.ThrowIfConflict();
            }
            catch (DecisionLedgerRequestException error)
            {
                closes = false;
                closureError = error;
                run.AppendFlowDecisionRejected("Review fix automatic gate",
                                               $"{fixAttemptId}:automatic-gate", error.Message);
            }
        }

        ledger.RecordFixAttempt(fixAttemptId!, ids, result, closes);
        run.AppendFlowFix(state.CodeReviewRounds, findings, note, result);
        run.WriteState(BuilderSession.Record(state, builder, vendor.Id, scope, resumeToken) with
        {
            PendingGateFailure = Gatekeeper.PendingFailure(result, killed, state.PendingGateFailure)
        });
        if (closureError is not null) throw closureError;
        return result;

    }

    private static BuildResult NoFixesResult() => new("done", [],
        new Verification("passed", "no fix IDs were sent to the builder; nothing to change or verify"),
        "no fix IDs passed through to the builder");

    /// <summary>
    /// The findings arrive under their own heading from the ledger. The note follows the findings it
    /// frames and stays a section of its own, so the ledger's verbatim findings never read as the
    /// orchestrator's words; the user's instructions stay last.
    /// </summary>
    /// <param name="findings">The ledger's rendering of the findings to fix, heading included.</param>
    /// <param name="note">The orchestrator's framing, or null when it sent none.</param>
    /// <param name="pendingGateFailure">What the last gate or turn left owing, or null when nothing is.</param>
    /// <param name="instructions">The user's builder instructions, for a fresh session only.</param>
    /// <returns>The prompt text, before the Builder Brief is put in front of it.</returns>
    private static string Compose(string findings, string? note, string? pendingGateFailure, string? instructions)
    {
        var prompt = new StringBuilder().AppendLine(findings);
        if (!string.IsNullOrWhiteSpace(note))
            prompt.AppendLine()
                  .AppendLine("# From the orchestrator")
                  .AppendLine()
                  .AppendLine(note.TrimEnd());
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}
