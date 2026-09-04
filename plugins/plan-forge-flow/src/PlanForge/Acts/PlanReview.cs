using System.Text;
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

    /// <param name="planDraft">
    /// The draft to review, written to <c>PLAN.md</c> on the way past. Omitted when
    /// <see cref="Write"/> already put it there, which is the flow the skill asks for.
    /// </param>
    /// <param name="revision">
    /// What the orchestrator changed in the draft in answer to the previous round's findings.
    /// Required from the second round on — see <see cref="RequireRevision"/>.
    /// </param>
    /// <param name="deferred">What it decided not to change, and why. Optional in every round.</param>
    /// <param name="userGrantedRound">
    /// The orchestrator's assertion that it showed the user where the run stands, asked, and was
    /// told yes — the same kind of assertion <c>approved</c> carries on <c>forge.plan.confirm</c>,
    /// and, per docs/adr/0003, one no code here can check.
    /// </param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            string? planDraft,
                                            Selection selection,
                                            string? revision,
                                            string? deferred,
                                            bool userGrantedRound,
                                            CancellationToken ct)
    {
        var state = run.ReadState();
        if (state.ReviewRounds >= state.ReviewRoundCap && !userGrantedRound)
            throw new ReviewCapReachedException(state.ReviewRounds, state.ReviewRoundCap);
        var granted = state.ReviewRounds >= state.ReviewRoundCap;
        RequireRevision(state.ReviewRounds, revision);

        // The draft is on disk before the critic starts rather than after it finishes: a round runs
        // for minutes, and those minutes are exactly when having the plan to read is worth
        // something. No guard precedes the write — the same draft is already on disk in `forge.log`,
        // written by the tool-call record, so this adds no surface a secret could reach.
        state = Reopen(run, state);
        var draft = TakeDraft(run, planDraft);

        var round = state.ReviewRounds + 1;
        var systemPrompt = _prompts.LoadPlanReviewCritic(_vendor.Id);

        // Both reach a vendor: the deferral through the next round's review log, the revision only
        // through the flow log, which no worker reads — but a secret pasted into either is one the
        // orchestrator is about to hand over, so both are guarded the same way.
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");

        await using var session = await _vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, systemPrompt), selection, resumeToken: null, ct);

        var prompt = Compose(draft, run.ReadReviewLog());
        SensitiveInput.Guard(prompt, "the plan under review");

        var critique = await session.RunAsync(prompt, Schemas.Critique, ct);

        // Written after the critique rather than before it, so an act that died — a vendor timeout,
        // a restarted server — and was retried with the same arguments records the revision once.
        // The grant is spent the same way: recording it here rather than at the cap check means a
        // retried call with the same arguments spends no grant.
        if (revision is { Length: > 0 })
            run.AppendFlowRevision(state.ReviewRounds, revision, deferred);
        if (deferred is { Length: > 0 })
            run.AppendReviewDeferral(state.ReviewRounds, deferred);

        run.AppendReviewRound(round, critique);
        if (granted) run.AppendFlowGrantedRound("Plan review", round);
        run.AppendFlowCritique("Plan review", round, critique);
        run.WriteState(granted
            ? state with { ReviewRounds = round, ReviewRoundCap = state.ReviewRoundCap + 1,
                           GrantedReviewRounds = state.GrantedReviewRounds + 1 }
            : state with { ReviewRounds = round });
        return critique;
    }

    /// <summary>
    /// Writes the draft without reviewing it, so the plan is on disk — and its path in front of the
    /// user — before the round that judges it rather than after. Everything a round does to the
    /// file it does here too: the same whole-file replacement, and the same withdrawal of an
    /// approval the new text has invalidated.
    /// </summary>
    /// <remarks>
    /// No round is spent and no cap is consulted: writing a draft is not reviewing it, and a write
    /// whose round is then refused at the cap leaves the user reading the draft they are being
    /// asked about. See docs/adr/0014.
    /// </remarks>
    public static void Write(RunDirectory run, string planDraft)
    {
        if (string.IsNullOrWhiteSpace(planDraft))
            throw new ArgumentRejectedException("forge.plan.write requires planDraft");

        Reopen(run, run.ReadState());
        run.WritePlan(planDraft);
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
    /// This runs before <see cref="RunDirectory.WritePlan"/>, and the order is the whole point. A
    /// crash between the two leaves the approval flag off over the previously approved text, which
    /// only refuses a build; the other order would leave the flag on over text nobody approved,
    /// which is the hole that writing the plan early would otherwise open.
    /// </remarks>
    private static RunState Reopen(RunDirectory run, RunState state)
    {
        if (!state.Approved) return state;

        var reopened = state with { Approved = false, TasksCompleted = 0, BuilderSessionId = string.Empty };
        run.WriteState(reopened);
        run.AppendFlowReopened(state.TasksCompleted);
        return reopened;
    }

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

    private static string Compose(string planDraft, string reviewLog)
    {
        var prompt = new StringBuilder()
            .AppendLine("# Plan under review")
            .AppendLine()
            .AppendLine(planDraft);

        if (reviewLog.Length > 0)
            prompt.AppendLine()
                  .AppendLine("# Review log from earlier rounds")
                  .AppendLine()
                  .AppendLine(reviewLog);

        return prompt.ToString();
    }
}

internal sealed class ReviewCapReachedException(int rounds, int cap)
    : Exception($"plan review already ran {rounds} rounds, and the cap is {cap}. Ask the user "
                + "whether to run another round, and pass `userGrantedRound: true` if they say yes.");

internal sealed class RevisionMissingException(int round)
    : Exception($"round {round} has already run: pass `revision` saying what you changed in the plan "
                + "in answer to its findings, and `deferred` for what you decided not to change, with reasons");
