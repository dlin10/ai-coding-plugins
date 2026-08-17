using System.Text;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// The whole critic-to-builder loop lives inside one call. Unlike plan review, no orchestrator turn
/// is needed between rounds: nothing here depends on the interview context.
/// </summary>
internal sealed class CodeReview
{
    private readonly IVendor _criticVendor;
    private readonly IVendor _builderVendor;
    private readonly PromptLibrary _prompts;
    private readonly GitClient _git;

    public CodeReview(IVendor criticVendor, IVendor builderVendor, PromptLibrary prompts, GitClient git)
    {
        _criticVendor = criticVendor;
        _builderVendor = builderVendor;
        _prompts = prompts;
        _git = git;
    }

    public async Task<CodeReviewOutcome> RunAsync(RunDirectory run,
                                                  Selection criticSelection,
                                                  Selection builderSelection,
                                                  int cap,
                                                  CancellationToken ct)
    {
        var criticPrompt = _prompts.Load(_criticVendor.Id, VendorRole.Critic);
        var builderPrompt = _prompts.Load(_builderVendor.Id, VendorRole.Builder);

        Critique? critique = null;
        for (var round = 1; round <= cap; round++)
        {
            await GuardChangedPathsAsync(ct);
            var diff = await _git.DiffAsync(GitPathspec.WithoutDocumentation, ct);
            if (diff.Length == 0) return new CodeReviewOutcome(new Critique("approve", [], "nothing to review"), round - 1);

            var review = ComposeReview(diff, run.ReadReviewLog());
            SensitiveInput.Guard(review, "the diff under review");

            // Fresh critic each round, but handed the log so it converges instead of oscillating.
            await using (var critic = await _criticVendor.StartAsync(
                new RoleSpec(VendorRole.Critic, criticPrompt), criticSelection, null, ct))
            {
                critique = await critic.RunAsync(review, Schemas.Critique, ct);
            }

            run.AppendReviewRound(run.ReadState().ReviewRounds + round, critique);
            if (critique.Verdict is "approve") return new CodeReviewOutcome(critique, round);

            var state = run.ReadState();
            var sameVendor = string.Equals(state.BuilderVendor, _builderVendor.Id, StringComparison.Ordinal);
            var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;
            var fixes = ComposeFixes(critique);
            SensitiveInput.Guard(fixes, "the code-review fixes");
            await using var builder = await _builderVendor.StartAsync(
                new RoleSpec(VendorRole.Builder, builderPrompt), builderSelection,
                resumeToken, ct);

            await builder.RunAsync(fixes, Schemas.BuildResult, ct);
            run.WriteState(state with
            {
                BuilderSessionId = sameVendor
                    ? builder.ResumeToken ?? state.BuilderSessionId
                    : builder.ResumeToken ?? string.Empty,
                BuilderVendor = _builderVendor.Id
            });
        }

        return new CodeReviewOutcome(critique, cap);
    }

    /// <summary>
    /// The guard covers exactly the set of paths whose contents are sent, which is why it takes the
    /// same pathspec as the diff. A sensitive <em>name</em> under an excluded path — an ADR called
    /// <c>0005-token-rotation.md</c>, say — is not a leak, because that file's contents never reach
    /// a vendor, and aborting the run over it would refuse a legitimate name for no gain.
    /// </summary>
    private async Task GuardChangedPathsAsync(CancellationToken ct)
    {
        var changed = await _git.ChangedPathsAsync(GitPathspec.WithoutDocumentation, ct);
        foreach (var path in changed)
            if (SensitiveInput.IsSensitivePath(path))
                throw new SensitiveContentException($"the diff touches {path}, which");
    }

    private static string ComposeReview(string diff, string reviewLog)
    {
        var prompt = new StringBuilder()
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

    private static string ComposeFixes(Critique critique)
    {
        var prompt = new StringBuilder()
            .AppendLine("# Fix these review findings")
            .AppendLine()
            .AppendLine(critique.Summary)
            .AppendLine();

        foreach (var finding in critique.Findings)
            prompt.Append("- **").Append(finding.Severity).Append("** ")
                  .Append(finding.Where).Append(" — ").AppendLine(finding.What);

        return prompt.ToString();
    }
}

internal sealed record CodeReviewOutcome(Critique? Verdict, int Rounds);
