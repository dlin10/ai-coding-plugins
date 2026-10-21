using System.Text;
using PlanForge.Evidence;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One round of plan review. The loop deliberately does not live inside this call: the orchestrator
/// holds the interview context, so it — not a worker — revises the plan between rounds.
/// </summary>
/// <remarks>
/// This is also where <c>PLAN.md</c> is written. The file used to appear only at approval, which
/// left the whole review act invisible to the user; now the draft is on disk before the critic
/// starts, so the plan is a document they can watch change. <see cref="Write"/> is that write on
/// its own, ahead of the round, because a draft streamed into this call is not on disk until the
/// call arrives — see docs/adr/0014. The cost is that an approval no longer survives a later round
/// — see <see cref="Reopen"/>.
/// </remarks>
internal sealed class PlanReview
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;

    public PlanReview(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    /// <summary>Set when the round's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    /// <summary>Set when the round judged a plan the Run's Evidence map had not seen since an earlier round.</summary>
    internal string? EvidenceMapWarning { get; private set; }

    /// <summary>The finding counts the round ran on when it ran past the cap without asking; see <see cref="ReviewCap"/>.</summary>
    internal string? ContinuedAutomatically { get; private set; }

    /// <summary>Reviews the current plan and applies independent plan-phase decisions.</summary>
    /// <param name="run">The run whose plan, ledger and timeline the review reads and writes.</param>
    /// <param name="selection">The critic's model, effort and speed.</param>
    /// <param name="ct">Cancels the review.</param>
    /// <param name="orchestratorDecisions">Optional decisions to apply before the review.</param>
    /// <param name="planDraft">
    /// The draft to review, written to <c>PLAN.md</c> on the way past. Omitted when
    /// <see cref="Write"/> already put it there, which is the flow the skill asks for.
    /// </param>
    /// <param name="revision">
    /// What the orchestrator changed in the draft in answer to the previous round's findings.
    /// Required from the second round on — see <see cref="RequireRevision"/>.
    /// </param>
    /// <param name="deferred">Optional Flow-only compatibility narrative; it never changes ledger state.</param>
    /// <param name="userGrantedRound">
    /// The orchestrator's assertion that it showed the user where the run stands, asked, and was
    /// told yes — the same kind of assertion <c>approved</c> carries on <c>forge.plan.confirm</c>,
    /// and, per docs/adr/0003, one no code here can check. Without it a round at the cap still runs
    /// when <see cref="ReviewCap.Continuation"/> allows it.
    /// </param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            string? planDraft,
                                            Selection selection,
                                            string? revision,
                                            string? deferred,
                                            bool userGrantedRound,
                                            CancellationToken ct,
                                            OrchestratorDecisionBatch? orchestratorDecisions = null)
    {
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");
        var state = run.ReadState();
        var atCap = state.ReviewRounds >= state.ReviewRoundCap;
        var continuation = atCap && !userGrantedRound
            ? ReviewCap.Continuation(state.ReviewTallies)
              ?? throw new ReviewCapReachedException(state.ReviewRounds, state.ReviewRoundCap)
            : null;
        var granted = atCap && userGrantedRound;
        RequireRevision(state.ReviewRounds, revision);
        var ledger = run.ReadDecisionLedger();
        if (orchestratorDecisions is not null)
        {
            try
            {
                ledger.ValidateOrchestratorBatch(orchestratorDecisions, LedgerPhase.PlanReview);
            }
            catch (DecisionLedgerRequestException error)
            {
                run.AppendFlowDecisionRejected("Plan review", orchestratorDecisions.DecisionBatchId, error.Message);
                throw;
            }
        }

        // The draft is on disk before the critic starts rather than after it finishes: a round runs
        // for minutes, and those minutes are exactly when having the plan to read is worth
        // something. No guard precedes the write — the same draft is already on disk in `forge.log`,
        // written by the tool-call record, so this adds no surface a secret could reach. A round handed
        // a draft rewrites the plan whole and keeps no built task; a round over the plan on disk changes
        // no text, so it keeps whatever the write or patch that put the plan there kept.
        var handed = !string.IsNullOrWhiteSpace(planDraft);
        state = Reopen(run, state, "a review round ran", handed ? planDraft! : WrittenDraft(run) ?? string.Empty,
                       handed ? 0 : state.TasksCompleted, out _);
        var draft = TakeDraft(run, planDraft);
        var evidenceMapWarning = EvidenceMapCurrency.Warning(run, state.ReviewRounds);

        var round = state.ReviewRounds + 1;
        var systemPrompt = _prompts.LoadPlanReviewCritic(_vendor.Id);

        if (orchestratorDecisions is not null)
        {
            try
            {
                var decisionResult = ledger.Apply(orchestratorDecisions, LedgerPhase.PlanReview);
                run.AppendFlowDecisionBatch("Plan review", orchestratorDecisions, decisionResult, LedgerPhase.PlanReview);
                decisionResult.ThrowIfConflict();
            }
            catch (DecisionLedgerRequestException error)
            {
                run.AppendFlowDecisionRejected("Plan review", orchestratorDecisions.DecisionBatchId, error.Message);
                throw;
            }
        }
        // Both reach a vendor through the plan and ledger projection. A secret pasted into either
        // is one the orchestrator is about to hand over, so both are guarded the same way.
        // The worker tools come from forge.begin because this critic runs before anything is
        // confirmed — see docs/adr/0017.
        await using var session = await _vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, systemPrompt,
                         WorkerTools: WorkerTools.Effective(state.WorkerTools),
                         Telemetry: new WorkerTelemetryContext(run.TelemetryPath, run.Log, "plan_review", Round: round)),
            selection, resumeToken: null, ct);

        var prompt = Compose(draft, ledger.RenderProjection(LedgerPhase.PlanReview), state.CriticInstructions);
        SensitiveInput.Guard(prompt, "the plan under review");

        VendorCritique wireCritique;
        try
        {
            wireCritique = await session.RunAsync(prompt, Schemas.Critique, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            run.AppendFlowCritiqueRejected("Plan review", round, null, error.Message);
            throw;
        }

        Critique critique;
        try
        {
            critique = ledger.IngestCritique(wireCritique, LedgerPhase.PlanReview);
        }
        catch (DecisionLedgerCritiqueException error)
        {
            run.AppendFlowCritiqueRejected("Plan review", round, wireCritique, error.Message);
            throw;
        }

        // Written after the critique rather than before it, so an act that died — a vendor timeout,
        // a restarted server — and was retried with the same arguments records the revision once.
        // The grant is spent the same way: recording it here rather than at the cap check means a
        // retried call with the same arguments spends no grant, and no automatic round either.
        if (revision is { Length: > 0 })
            run.AppendFlowRevision(state.ReviewRounds, revision, deferred);
        if (granted) run.AppendFlowGrantedRound("Plan review", round);
        if (continuation is not null) run.AppendFlowAutoContinuedRound("Plan review", round, continuation);
        if (evidenceMapWarning is not null) run.AppendFlowEvidenceMapWarning(round, evidenceMapWarning);
        run.AppendFlowCritique("Plan review", round, critique);
        run.AppendFlowKilledTasks("Plan review", round, session.KilledBackgroundTasks);
        SpeedWarning = session.SpeedWarning;
        EvidenceMapWarning = evidenceMapWarning;
        ContinuedAutomatically = continuation;
        var reviewed = state with
        {
            ReviewRounds = round,
            ReviewTallies = [.. state.ReviewTallies ?? [], ReviewCap.Tally(critique, ledger, LedgerPhase.PlanReview)]
        };
        run.WriteState(granted
            ? reviewed with { ReviewRoundCap = state.ReviewRoundCap + 1, GrantedReviewRounds = state.GrantedReviewRounds + 1 }
            : continuation is not null
                ? reviewed with { AutoContinuedReviewRounds = state.AutoContinuedReviewRounds + 1 }
                : reviewed);
        return critique;
    }

    /// <summary>
    /// Writes the draft without reviewing it, so the plan is on disk — and its path in front of the
    /// user — before the round that judges it rather than after. Everything a round does to the
    /// file it does here too: the same whole-file replacement, and the same withdrawal of an
    /// approval the new text has invalidated. A whole draft says nothing about which tasks it left
    /// alone, so it keeps no built task.
    /// </summary>
    /// <param name="run">The run whose plan is replaced.</param>
    /// <param name="planDraft">The complete draft, stored as supplied.</param>
    /// <remarks>
    /// No round is spent and no cap is consulted: writing a draft is not reviewing it, and a write
    /// whose round is then refused at the cap leaves the user reading the draft they are being
    /// asked about. See docs/adr/0014.
    /// </remarks>
    public static void Write(RunDirectory run, string planDraft)
    {
        if (string.IsNullOrWhiteSpace(planDraft))
            throw new ArgumentRejectedException("forge.plan.write requires planDraft");

        Reopen(run, run.ReadState(), "the plan was rewritten with forge.plan.write", planDraft, 0, out _);
        run.WritePlan(planDraft);
    }

    /// <summary>
    /// Applies a Plan patch and writes the result. The approval goes as it does for a write, but the
    /// build progress goes back only to the first task whose text the patch changed: built tasks
    /// before it keep their count and their hand-over, and a patch that changes no built task keeps
    /// them all.
    /// </summary>
    /// <remarks>
    /// The tasks are compared as the build parses them, not by which Edit targets the patch named:
    /// prose inserted after a task joins that task's text, which no target position shows.
    /// </remarks>
    /// <param name="run">The run whose plan is patched.</param>
    /// <param name="baseHash">The hash of the plan the edits were written against.</param>
    /// <param name="edits">The edits, against that plan's outline.</param>
    /// <returns>The new outline, the changes, their sizes and the build progress the run keeps.</returns>
    public static PlanPatchOutcome Patch(RunDirectory run, string? baseHash, IReadOnlyList<PlanEdit?>? edits)
    {
        var sizes = PlanPatchSizes.Payload(edits);
        var approvalWithdrawn = false;
        PlanPatchResult patch;
        string plan;
        int tasksKept;
        try
        {
            if (!File.Exists(run.PlanPath)) throw NoPlan();
            plan = run.ReadPlan();
            sizes = sizes.Before(plan);
            if (PlanOutline.IsBlank(plan)) throw NoPlan();
            patch = PlanPatch.Apply(plan, baseHash, edits);
            sizes = patch.Sizes;
            var state = run.ReadState();
            var kept = PlanTasks.FirstChanged(plan, patch.Plan) is { } changed
                ? Math.Min(state.TasksCompleted, changed - 1)
                : state.TasksCompleted;
            tasksKept = Reopen(run, state, "the plan was patched with forge.plan.patch", patch.Plan, kept,
                               out approvalWithdrawn).TasksCompleted;
            run.WritePlan(patch.Plan);
        }
        catch (PlanPatchRejectedException) { throw; }
        catch (Exception)
        {
            throw new PlanPatchFailedException("failed_unchanged", sizes,
                "The plan is unchanged; the approval was " + (approvalWithdrawn ? "already withdrawn." : "not withdrawn."));
        }

        var outline = PlanOutline.Of(patch.Plan);
        var beforeHash = PlanOutline.Hash(plan);
        try
        {
            run.AppendFlowPlanPatched(beforeHash, outline.PlanHash, patch.Changes, sizes);
        }
        catch (Exception)
        {
            throw new PlanPatchFailedException("failed_after_patch", sizes,
                $"The plan was patched, but its Flow entry could not be written. The new plan hash is {outline.PlanHash}; read forge.plan.outline.");
        }
        return new PlanPatchOutcome(beforeHash, outline, patch.Changes, sizes, tasksKept, FirstTaskOwed(patch.Plan, tasksKept));

        PlanPatchRejectedException NoPlan() => new("no_plan", sizes,
            "Write the plan first with forge.plan.write before reading an outline or patching it.");
    }

    /// <summary>
    /// The draft this round judges: the one it was handed, written on the way past, or the one
    /// already on disk when the orchestrator omitted it because <see cref="Write"/> put it there.
    /// </summary>
    private static string TakeDraft(RunDirectory run, string? planDraft)
    {
        if (!string.IsNullOrWhiteSpace(planDraft))
        {
            run.WritePlan(planDraft);
            return planDraft;
        }

        return WrittenDraft(run) ?? throw MissingDraft();
    }

    /// <summary>
    /// The same demand as <see cref="TakeDraft"/>, made before a background job is started, so a
    /// round with no draft anywhere is an argument error answered by the call that made it rather
    /// than a job that fails with nothing running behind it.
    /// </summary>
    internal static void RequireDraft(RunDirectory run, string? planDraft)
    {
        if (string.IsNullOrWhiteSpace(planDraft) && WrittenDraft(run) is null) throw MissingDraft();
    }

    private static string? WrittenDraft(RunDirectory run) =>
        File.Exists(run.PlanPath) && run.ReadPlan() is { } plan && !string.IsNullOrWhiteSpace(plan)
            ? plan
            : null;

    private static ArgumentRejectedException MissingDraft() =>
        new("plan.review needs a draft: call forge.plan.write with the plan first, or pass planDraft");

    /// <summary>
    /// Takes back an approval that a new round has just invalidated, and hands back the state the
    /// rest of the round must build on — the returned value is what makes the closing
    /// <c>WriteState</c> keep the approval off rather than restoring it from a stale capture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs before <see cref="RunDirectory.WritePlan"/>, and the order is the whole point. A
    /// crash between the two leaves the approval flag off over the previously approved text, which
    /// only refuses a build; the other order would leave the flag on over text nobody approved,
    /// which is the hole that writing the plan early would otherwise open.
    /// </para>
    /// <para>
    /// The build progress goes back only as far as the change reaches. Built tasks before the first
    /// one the new text changes stay built, and a run that was not approved — because an earlier
    /// patch already reopened it — still loses the tasks this change reaches. The Builder session is
    /// forgotten either way; the next task starts a fresh one anyway (docs/adr/0026).
    /// </para>
    /// </remarks>
    /// <param name="run">The run whose state and timeline record the reopening.</param>
    /// <param name="state">The state the round started from.</param>
    /// <param name="cause">The action that invalidated the approval.</param>
    /// <param name="plan">The plan the run holds once the change lands, to name the first task owed.</param>
    /// <param name="kept">How many built tasks the change leaves untouched; more than are built keeps them all.</param>
    /// <param name="approvalWithdrawn">Whether an approval was taken back and persisted, even if its Flow entry fails.</param>
    /// <returns>The state the round builds on: unchanged when nothing was approved or rewound, reopened otherwise.</returns>
    private static RunState Reopen(RunDirectory run, RunState state, string cause, string plan, int kept,
                                   out bool approvalWithdrawn)
    {
        approvalWithdrawn = false;
        if (!state.Approved && kept >= state.TasksCompleted) return state;

        var reopened = kept < state.TasksCompleted
            ? Rewind(state, kept) with { Approved = false }
            : BuilderSession.Forget(state) with { Approved = false };
        run.WriteState(reopened);
        approvalWithdrawn = state.Approved;
        run.AppendFlowReopened(state.Approved, state.TasksCompleted, reopened.TasksCompleted,
                               FirstTaskOwed(plan, reopened.TasksCompleted), cause);
        return reopened;
    }

    /// <summary>
    /// Takes the build progress back to <paramref name="kept"/> tasks. What the dropped tasks changed
    /// is no hand-over for their rebuild, and the gate failure the next task owed is owed to nobody
    /// once tasks before it restart; the tasks that stay keep their hand-over, so the first one rebuilt
    /// is told what they changed.
    /// </summary>
    /// <param name="state">The run state holding the progress.</param>
    /// <param name="kept">How many built tasks stay built; fewer than are built.</param>
    /// <returns>The run state with the progress, hand-over, owed gate failure and Builder session taken back.</returns>
    internal static RunState Rewind(RunState state, int kept)
    {
        var changes = state.TaskChanges?.Where(change => change.TaskNumber <= kept).ToArray();
        return BuilderSession.Forget(state) with
        {
            TasksCompleted = kept,
            PendingGateFailure = null,
            TaskChanges = changes is { Length: > 0 } ? changes : null
        };
    }

    /// <summary>The task the next build runs after <paramref name="kept"/> tasks, or null when the plan has none after them.</summary>
    /// <param name="plan">The plan the build reads.</param>
    /// <param name="kept">How many tasks are built.</param>
    /// <returns>The first task owed, numbered from 1.</returns>
    internal static int? FirstTaskOwed(string plan, int kept) =>
        PlanTasks.Locate(plan)?.TaskStarts.Count > kept ? kept + 1 : null;

    /// <summary>
    /// A round after the first is an answer to the one before it, and the answer is the orchestrator's
    /// alone — the critic never sees the reasoning, only the redrafted plan. Refusing the call is what
    /// keeps that answer in the timeline; asking for it in the skill alone is what left four verdicts
    /// in a row with nothing between them.
    /// </summary>
    internal static void RequireRevision(int roundsSoFar, string? revision)
    {
        if (roundsSoFar > 0 && string.IsNullOrWhiteSpace(revision))
            throw new RevisionMissingException(roundsSoFar);
    }

    /// <summary>
    /// The critic's own instructions come last, after everything it is judging. The builder's never
    /// appear here: they explain code, and at plan review there is none.
    /// </summary>
    private static string Compose(string planDraft, string ledgerProjection, string? criticInstructions)
    {
        var prompt = new StringBuilder()
            .AppendLine("# Plan under review")
            .AppendLine()
            .AppendLine(planDraft);

        if (ledgerProjection.Length > 0)
            prompt.AppendLine()
                  .AppendLine(ledgerProjection.TrimEnd())
                  .AppendLine()
                  .AppendLine();

        RunInstructions.Append(prompt, criticInstructions);

        return prompt.ToString();
    }
}

internal sealed record PlanPatchOutcome(string BaseHash, PlanOutline Outline,
                                        IReadOnlyList<PlanPatchChange> Changed, PlanPatchSizes Sizes,
                                        int TasksKept, int? FirstTaskOwed);

internal sealed class PlanPatchFailedException(string outcome, PlanPatchSizes sizes, string message) : Exception(message)
{
    public string Outcome { get; } = outcome;
    public PlanPatchSizes Sizes { get; } = sizes;
}

internal sealed class ReviewCapReachedException(int rounds, int cap)
    : Exception($"plan review already ran {rounds} rounds, and the cap is {cap}. Ask the user "
                + "whether to run another round, and pass `userGrantedRound: true` if they say yes.");

internal sealed class RevisionMissingException(int round)
    : Exception($"round {round} has already run: pass `revision` saying what you changed in the plan "
                + "in answer to its findings, and pass any authoritative finding decisions through `decisions`");
