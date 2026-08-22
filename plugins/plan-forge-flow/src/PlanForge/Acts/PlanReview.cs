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
