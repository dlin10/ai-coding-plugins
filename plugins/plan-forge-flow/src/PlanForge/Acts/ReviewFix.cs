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
    /// <summary>Set when the fix's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    internal async Task<BuildResult> FixAsync(RunDirectory run,
                                              Selection selection,
                                              OrchestratorDecisionBatch? decisionBatch,
                                              string? fixAttemptId,
                                              IReadOnlyList<string>? fixFindingIds,
                                              CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var ledger = run.ReadDecisionLedger();
        var ids = fixFindingIds is null ? [] : ledger.NormalizeFixFindingIds(fixFindingIds);
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(fixAttemptId))
            throw new ArgumentRejectedException("fixAttemptId requires non-empty fixFindingIds");
        if (ids.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(fixAttemptId))
                throw new ArgumentRejectedException("fixFindingIds requires fixAttemptId");
            if (decisionBatch is not null && decisionBatch.Decisions.Any(decision => ids.Contains(decision.FindingId)))
                throw new DecisionLedgerRequestException("a fix finding ID cannot also appear in the decision batch");
        }

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

        var sameVendor = string.Equals(state.BuilderVendor, vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;
        var prompt = Compose(findings, state.PendingGateFailure,
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
            run.WriteState(Resumed(state, builder, sameVendor) with
            {
                PendingGateFailure = Gatekeeper.CutShortBrief(cutShort.FilesWritten)
            });
            throw;
        }

        var plan = run.ReadPlan();
        var killed = builder.KilledBackgroundTasks;
        SpeedWarning = builder.SpeedWarning;
        var result = await Gatekeeper.CheckAsync(reported, PlanGates.RunWideGates(plan),
                                                 PlanGates.HasRunWideGates(plan), killed, state, ct);
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
        run.AppendFlowFix(state.CodeReviewRounds, findings, null, result);
        run.WriteState(Resumed(state, builder, sameVendor) with
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
    /// The builder's session as the run should remember it after a turn. A cut-short turn keeps its
    /// token like any other: the id arrives on the vendor's first stream line, long before the
    /// answer that never came, and without it the next fix starts a builder with no memory of the
    /// round it is continuing.
    /// </summary>
    private RunState Resumed(RunState state, IVendorSession builder, bool sameVendor) =>
        state with
        {
            BuilderSessionId = sameVendor
                                   ? builder.ResumeToken ?? state.BuilderSessionId
                                   : builder.ResumeToken ?? string.Empty,
            BuilderVendor = vendor.Id
        };

    private static string Compose(string findings, string? pendingGateFailure, string? instructions)
    {
        var prompt = new StringBuilder().AppendLine("# Fix these review findings")
                                        .AppendLine()
                                        .AppendLine(findings);
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}
