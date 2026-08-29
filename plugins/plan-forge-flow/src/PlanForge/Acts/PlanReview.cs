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
/// left the whole review act invisible to the user; now every round writes the draft it was handed,
/// so the plan is a document they can watch change. The cost is that an approval no longer survives
/// a later round — see <see cref="Reopen"/>.
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

    /// <param name="revision">
    /// What the orchestrator changed in the draft in answer to the previous round's findings.
    /// Required from the second round on — see <see cref="RequireRevision"/>.
    /// </param>
    /// <param name="deferred">What it decided not to change, and why. Optional in every round.</param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            string planDraft,
                                            Selection selection,
                                            string? revision,
                                            string? deferred,
                                            CancellationToken ct)
    {
        var state = run.ReadState();
        if (state.ReviewRounds >= state.ReviewRoundCap)
            throw new ReviewCapReachedException(state.ReviewRounds, state.ReviewRoundCap);
        RequireRevision(state.ReviewRounds, revision);

        // The draft lands on disk before the critic starts rather than after it finishes: a round
        // runs for minutes, and the point of writing it here is that the user has the plan to read
        // while it does. No guard precedes it — the same draft is already on disk in `forge.log`,
        // written by the tool-call record, so this adds no surface a secret could reach.
        state = Reopen(run, state);
        run.WritePlan(planDraft);

        var round = state.ReviewRounds + 1;
        var systemPrompt = _prompts.LoadPlanReviewCritic(_vendor.Id);

        // Both reach a vendor: the deferral through the next round's review log, the revision only
        // through the flow log, which no worker reads — but a secret pasted into either is one the
        // orchestrator is about to hand over, so both are guarded the same way.
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");

        await using var session = await _vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, systemPrompt), selection, resumeToken: null, ct);

        var prompt = Compose(planDraft, run.ReadReviewLog());
        SensitiveInput.Guard(prompt, "the plan under review");

        var critique = await session.RunAsync(prompt, Schemas.Critique, ct);

        // Written after the critique rather than before it, so an act that died — a vendor timeout,
        // a restarted server — and was retried with the same arguments records the revision once.
        if (revision is { Length: > 0 })
            run.AppendFlowRevision(state.ReviewRounds, revision, deferred);
        if (deferred is { Length: > 0 })
            run.AppendReviewDeferral(state.ReviewRounds, deferred);

        run.AppendReviewRound(round, critique);
        run.AppendFlowCritique("Plan review", round, critique);
        run.WriteState(state with { ReviewRounds = round });
        return critique;
    }

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
    : Exception($"plan review already ran {rounds} rounds, and the cap is {cap}");

internal sealed class RevisionMissingException(int round)
    : Exception($"round {round} has already run: pass `revision` saying what you changed in the plan "
                + "in answer to its findings, and `deferred` for what you decided not to change, with reasons");
