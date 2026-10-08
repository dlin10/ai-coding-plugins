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
/// <param name="vendor">The Builder vendor.</param>
/// <param name="prompts">The role prompt library.</param>
/// <param name="gateTimeout">An optional bound on the host fix gate, defaulting to the production fix timeout.</param>
internal sealed class ReviewFix(IVendor vendor, PromptLibrary prompts, TimeSpan? gateTimeout = null)
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
    /// findings named by <paramref name="fixFindingIds"/> and the selected gates after it. A call
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
    /// <param name="gate">Full plan verification by default, or the explicit targeted Fix gate.</param>
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
                                              string? note = null, string? gate = null)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var ledger = run.ReadDecisionLedger();
        var (mode, ids, existing, gates) = ValidateRequest(run, ledger, decisionBatch, fixAttemptId,
                                                          fixFindingIds, note, gate);

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

        if (decisionBatch is not null)
        {
            try
            {
                var response = ledger.Apply(decisionBatch, LedgerPhase.CodeReview);
                run.AppendFlowDecisionBatch("Review fix", decisionBatch, response, LedgerPhase.CodeReview);
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
            var replay = Annotate(existing.LastResult, ledger, fixAttemptId!, mode);
            run.AppendFlowFixAttemptNoOp(fixAttemptId!, ids, replay);
            return replay;
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

        ledger.BeginFixAttempt(fixAttemptId!, ids, mode);
        if (!string.IsNullOrWhiteSpace(note)) SensitiveInput.Guard(note, "the orchestrator's note");
        var findings = ledger.RenderFixFindings(ids);
        SensitiveInput.Guard(findings, "ledger-derived fix findings");

        // A session covers one code-review round: the round's first fix starts fresh, with the Brief,
        // and its later calls and retries resume. See docs/adr/0026.
        var scope = BuilderSession.FixScope(state.CodeReviewRounds);
        var resumeToken = BuilderSession.ResumeToken(state, vendor.Id, scope);
        var prompt = Compose(findings, note, existing?.LastResult, state.PendingGateFailure, mode, gates,
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
            ledger.RecordFixAttempt(fixAttemptId!, ids, null, false, mode);
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
        var result = await Gatekeeper.CheckAsync(reported, gates,
                                                 mode == FixGatePolicy.Targeted || PlanGates.HasRunWideGates(plan),
                                                 killed, state, gateTimeout ?? GateRunner.FIX_TIMEOUT, ct);
        var completion = FixGatePolicy.Completion(mode, result);
        var closes = completion == FixCompletion.Closed;

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
                run.AppendFlowDecisionBatch("Review fix automatic gate", closureBatch, closure, LedgerPhase.CodeReview);
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

        ledger.RecordFixAttempt(fixAttemptId!, ids, result, closes || completion == FixCompletion.PendingFullGate, mode);
        run.AppendFlowFix(state.CodeReviewRounds, findings, note,
                          Annotate(result, ledger, fixAttemptId!, mode), fixAttemptId, ids);
        run.WriteState(BuilderSession.Record(state, builder, vendor.Id, scope, resumeToken) with
        {
            PendingGateFailure = Gatekeeper.PendingFailure(result, killed, state.PendingGateFailure)
        });
        if (closureError is not null) throw closureError;
        return Annotate(result, ledger, fixAttemptId!, mode);

    }

    internal static (string Mode, IReadOnlyList<string> Ids, FixAttemptRecord? Existing,
                     IReadOnlyList<GateCommand> Gates)
        ValidateRequest(RunDirectory run, DecisionLedger ledger, OrchestratorDecisionBatch? decisions,
                        string? fixAttemptId, IReadOnlyList<string>? fixFindingIds, string? note, string? gate)
    {
        var mode = FixGatePolicy.GateMode(gate);
        var ids = fixFindingIds is null ? [] : ledger.NormalizeFixFindingIds(fixFindingIds);
        if (ids.Count == 0 && gate is not null)
            throw new ArgumentRejectedException("gate requires non-empty fixFindingIds");
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(fixAttemptId))
            throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(note))
            throw new ArgumentRejectedException("note requires non-empty fixFindingIds");
        if (ids.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(fixAttemptId))
                throw new ArgumentRejectedException("fixFindingIds requires fixAttemptId");
            if (decisions is not null && decisions.Decisions.Any(decision => ids.Contains(decision.FindingId)))
                throw new DecisionLedgerRequestException("a fix finding ID cannot also appear in the decision batch");
            if (decisions?.Raises is { Count: > 0 })
                throw new DecisionLedgerRequestException(RaiseWithFixRefused);
        }
        var existing = string.IsNullOrWhiteSpace(fixAttemptId) ? null
            : ledger.ValidateFixBinding(fixAttemptId, ids, mode);
        var gates = ids.Count > 0 && existing is not { Terminal: true }
            ? FixGatePolicy.SelectGates(mode, run.ReadPlan()) : [];

        return (mode, ids, existing, gates);
    }

    private static BuildResult Annotate(BuildResult result, DecisionLedger ledger, string attemptId, string mode) =>
        result with
        {
            GateMode = mode,
            PendingFullGateFindingIds = mode == FixGatePolicy.Targeted
                ? ledger.Summary.PendingFullGateAttempts.Where(attempt => attempt.FixAttemptId == attemptId)
                        .SelectMany(attempt => attempt.FindingIds).ToArray()
                : null
        };

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
    /// <param name="lastResult">The current attempt's last completed result, if any.</param>
    /// <param name="pendingGateFailure">What the last gate or turn left owing, or null when nothing is.</param>
    /// <param name="mode">The immutable gate mode for this attempt.</param>
    /// <param name="gates">The selected executable commands the server will run.</param>
    /// <param name="instructions">The user's builder instructions, for a fresh session only.</param>
    /// <returns>The prompt text, before the Builder Brief is put in front of it.</returns>
    private static string Compose(string findings, string? note, BuildResult? lastResult, string? pendingGateFailure,
                                  string mode, IReadOnlyList<GateCommand> gates, string? instructions)
    {
        var prompt = new StringBuilder().AppendLine(findings);
        if (!string.IsNullOrWhiteSpace(note))
            prompt.AppendLine()
                  .AppendLine("# From the orchestrator")
                  .AppendLine()
                  .AppendLine(note.TrimEnd());
        prompt.AppendLine().AppendLine("# Fix verification").AppendLine()
              .Append("gate=").AppendLine(mode)
              .AppendLine(mode == FixGatePolicy.Targeted
                  ? "The server runs the targeted Fix gate after your turn. The Orchestrator runs all full plan checks; a passed Fix gate leaves these findings pending full verification."
                  : "The server runs the full plan G-commands after your turn. Check any plan conditions yourself.")
              .AppendLine("Do not run executable task or fix gates, including on retries, or reproduce their full equivalent. Report only your own diagnostic checks in verification, or unavailable with the server ownership explained.");
        foreach (var gate in gates)
            prompt.AppendLine().Append(gate.Label).AppendLine(":").AppendLine("```")
                  .AppendLine(gate.Command).AppendLine("```");
        Gatekeeper.AppendFixRetry(prompt, lastResult, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}
