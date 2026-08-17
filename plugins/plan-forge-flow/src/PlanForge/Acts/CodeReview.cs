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
internal sealed class CodeReview(IVendor vendor, PromptLibrary prompts, GitClient git)
{
    public async Task<Critique> ReviewAsync(RunDirectory run, Selection selection, CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);
        if (state.CodeReviewRounds >= state.CodeReviewRoundCap)
            throw new CodeReviewCapReachedException(state.CodeReviewRounds, state.CodeReviewRoundCap);

        await GuardChangedPathsAsync(ct);
        var diff = await git.DiffAsync(GitPathspec.WithoutDocumentation, ct);
        if (diff.Length == 0) return new Critique("approve", [], "nothing to review");

        var review = ComposeReview(run.ReadPlan(), diff, run.ReadReviewLog());
        SensitiveInput.Guard(review, "the diff under review");

        Critique critique;
        // Fresh critic each round, but handed the log so it converges instead of oscillating.
        await using (var critic = await vendor.StartAsync(new RoleSpec(VendorRole.Critic, prompts.LoadCodeReviewCritic(vendor.Id)),
                                                          selection, resumeToken: null, ct))
        {
            critique = await critic.RunAsync(review, Schemas.Critique, ct);
        }

        var round = state.CodeReviewRounds + 1;
        run.AppendReviewRound(state.ReviewRounds + round, critique);
        run.WriteState(state with { CodeReviewRounds = round });
        return critique;
    }

    /// <summary>
    /// The guard covers exactly the set of paths whose contents are sent, which is why it takes the
    /// same pathspec as the diff. A sensitive <em>name</em> under an excluded path — an ADR called
    /// <c>0005-token-rotation.md</c>, say — is not a leak, because that file's contents never reach
    /// a vendor, and aborting the run over it would refuse a legitimate name for no gain.
    /// </summary>
    private async Task GuardChangedPathsAsync(CancellationToken ct)
    {
        var changed = await git.ChangedPathsAsync(GitPathspec.WithoutDocumentation, ct);
        foreach (var path in changed)
        {
            if (SensitiveInput.IsSensitivePath(path))
                throw new SensitiveContentException($"the diff touches {path}, which");
        }
    }

    private static string ComposeReview(string plan, string diff, string reviewLog)
    {
        var prompt = new StringBuilder().AppendLine("# Approved plan")
                                        .AppendLine()
                                        .AppendLine(plan)
                                        .AppendLine()
                                        .AppendLine("# Diff under review")
                                        .AppendLine()
                                        .AppendLine("```diff")
                                        .AppendLine(diff)
                                        .AppendLine("```");

        if (reviewLog.Length > 0)
            prompt.AppendLine()
                  .AppendLine("# Review log from earlier rounds")
                  .AppendLine()
                  .AppendLine(reviewLog);

        return prompt.ToString();
    }
}

internal sealed class CodeReviewCapReachedException(int rounds, int cap)
    : Exception($"code review already ran {rounds} rounds, and the cap is {cap}");
