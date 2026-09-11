using System.Text;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One round of code review. The loop deliberately no longer lives inside this call: when the
/// critic asks for work the approved plan excluded, only the orchestrator can arbitrate, because
/// it alone holds the interview context that settled the scope. Handing findings to the builder
/// is <see cref="ReviewFix"/>, called by the orchestrator after it has filtered them.
/// </summary>
internal sealed class CodeReview(IVendor vendor, PromptLibrary prompts, IReviewGit git)
{
    /// <param name="userGrantedRound">
    /// The orchestrator's assertion that it showed the user where the run stands, asked, and was
    /// told yes — the same kind of assertion <c>approved</c> carries on <c>forge.plan.confirm</c>,
    /// and, per docs/adr/0003, one no code here can check.
    /// </param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            Selection selection,
                                            bool userGrantedRound,
                                            CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);
        if (state.CodeReviewRounds >= state.CodeReviewRoundCap && !userGrantedRound)
            throw new CodeReviewCapReachedException(state.CodeReviewRounds, state.CodeReviewRoundCap);
        var granted = state.CodeReviewRounds >= state.CodeReviewRoundCap;

        var window = await git.ReadReviewWindowAsync(state.BaselineHead, ct);
        GuardChangedPaths(window);
        if (window.Diff.Length == 0)
            return WithReviewWindow(new Critique("approve", [], "nothing to review"), window);

        var review = ComposeReview(run.ReadPlan(), window, run.ReadReviewLog());
        SensitiveInput.Guard(review, "the diff under review");

        Critique critique;
        // Fresh critic each round, but handed the log so it converges instead of oscillating.
        await using (var critic = await vendor.StartAsync(new RoleSpec(VendorRole.Critic, prompts.LoadCodeReviewCritic(vendor.Id)),
                                                          selection, resumeToken: null, ct))
        {
            critique = WithReviewWindow(await critic.RunAsync(review, Schemas.Critique, ct), window);
        }

        var round = state.CodeReviewRounds + 1;
        run.AppendReviewRound(state.ReviewRounds + round, critique);
        if (granted) run.AppendFlowGrantedRound("Code review", round);
        run.AppendFlowCritique("Code review", round, critique);
        run.WriteState(granted
            ? state with { CodeReviewRounds = round, CodeReviewRoundCap = state.CodeReviewRoundCap + 1,
                           GrantedCodeReviewRounds = state.GrantedCodeReviewRounds + 1 }
            : state with { CodeReviewRounds = round });
        return critique;
    }

    /// <summary>
    /// The guard covers exactly the set of paths whose contents are sent, which is why it takes the
    /// same pathspec as the diff. A sensitive <em>name</em> under an excluded path — an ADR called
    /// <c>0005-token-rotation.md</c>, say — is not a leak, because that file's contents never reach
    /// a vendor, and aborting the run over it would refuse a legitimate name for no gain.
    /// </summary>
    private static void GuardChangedPaths(ReviewWindow window)
    {
        foreach (var path in window.ChangedPaths)
        {
            if (SensitiveInput.IsSensitivePath(path))
                throw new SensitiveContentException($"the diff touches {path}, which");
        }
    }

    private static string ComposeReview(string plan, ReviewWindow window, string reviewLog)
    {
        var prompt = new StringBuilder().AppendLine("# Approved plan")
                                        .AppendLine()
                                        .AppendLine(plan)
                                        .AppendLine()
                                        .AppendLine("# Review window")
                                        .AppendLine()
                                        .AppendLine($"Mode: {(window.IsFallback ? "HEAD fallback" : "run baseline")}")
                                        .AppendLine($"Base commit: {window.BaseHead}");

        if (window.IsFallback)
            prompt.AppendLine($"Run baseline: {window.BaselineHead}")
                  .AppendLine("Reason: the run baseline is not an ancestor of the current HEAD; "
                              + "committed run work may be outside this window.");

        prompt.AppendLine("Target: current working tree, including untracked files")
              .AppendLine("Excluded paths: every CONTEXT.md and docs/adr/** at any depth")
              .AppendLine()
              .AppendLine("# Diff under review")
              .AppendLine()
              .AppendLine("```diff")
              .AppendLine(window.Diff)
              .AppendLine("```");

        if (reviewLog.Length > 0)
            prompt.AppendLine()
                  .AppendLine("# Review log from earlier rounds")
                  .AppendLine()
                  .AppendLine(reviewLog);

        return prompt.ToString();
    }

    private static Critique WithReviewWindow(Critique critique, ReviewWindow window)
    {
        var summary = window.IsFallback
            ? $"Review window: HEAD fallback {Short(window.BaseHead)} to working tree; run baseline "
              + $"{Short(window.BaselineHead)} is not an ancestor, so committed run work may be outside this window."
            : $"Review window: run baseline {Short(window.BaseHead)} to working tree.";
        return critique with { Summary = $"{critique.Summary}\n\n{summary}" };
    }

    private static string Short(string head) => head[..Math.Min(12, head.Length)];
}

internal sealed class CodeReviewCapReachedException(int rounds, int cap)
    : Exception($"code review already ran {rounds} rounds, and the cap is {cap}. Ask the user "
                + "whether to run another round, and pass `userGrantedRound: true` if they say yes.");
