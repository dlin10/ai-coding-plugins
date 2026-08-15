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

    public async Task<Critique> ReviewAsync(RunDirectory run, string planDraft, Selection selection, CancellationToken ct)
    {
        var state = run.ReadState();
        if (state.ReviewRounds >= state.ReviewRoundCap)
            throw new ReviewCapReachedException(state.ReviewRounds, state.ReviewRoundCap);

        var round = state.ReviewRounds + 1;
        var systemPrompt = _prompts.Load(_vendor.Id, VendorRole.Critic);

        await using var session = await _vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, systemPrompt), selection, resumeToken: null, ct);

        var prompt = Compose(planDraft, run.ReadReviewLog());
        SensitiveInput.Guard(prompt, "the plan under review");

        var critique = await session.RunAsync(prompt, Schemas.Critique, ct);

        run.AppendReviewRound(round, critique);
        run.WriteState(state with { ReviewRounds = round });
        return critique;
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
