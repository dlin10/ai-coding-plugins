using System.Text;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// The whole critic-to-builder loop lives inside one call. Unlike plan review, no orchestrator turn
/// is needed between rounds: nothing here depends on the interview context.
/// </summary>
internal sealed class CodeReview
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;
    private readonly GitClient _git;

    public CodeReview(IVendor vendor, PromptLibrary prompts, GitClient git)
    {
        _vendor = vendor;
        _prompts = prompts;
        _git = git;
    }

    public async Task<CodeReviewOutcome> RunAsync(RunDirectory run,
                                                  Selection criticSelection,
                                                  Selection builderSelection,
                                                  int cap,
                                                  CancellationToken ct)
    {
        var criticPrompt = _prompts.Load(_vendor.Id, VendorRole.Critic);
        var builderPrompt = _prompts.Load(_vendor.Id, VendorRole.Builder);

        Critique? critique = null;
        for (var round = 1; round <= cap; round++)
        {
            var diff = await _git.OutputAsync(["diff"], ct);
            if (diff.Length == 0) return new CodeReviewOutcome(new Critique("approve", [], "nothing to review"), round - 1);

            // Fresh critic each round, but handed the log so it converges instead of oscillating.
            await using (var critic = await _vendor.StartAsync(
                new RoleSpec(VendorRole.Critic, criticPrompt), criticSelection, null, ct))
            {
                critique = await critic.RunAsync(ComposeReview(diff, run.ReadReviewLog()), Schemas.Critique, ct);
            }

            run.AppendReviewRound(run.ReadState().ReviewRounds + round, critique);
            if (critique.Verdict is "approve") return new CodeReviewOutcome(critique, round);

            var state = run.ReadState();
            await using var builder = await _vendor.StartAsync(
                new RoleSpec(VendorRole.Builder, builderPrompt), builderSelection,
                state.BuilderSessionId is { Length: > 0 } token ? token : null, ct);

            await builder.RunAsync(ComposeFixes(critique), Schemas.BuildResult, ct);
            run.WriteState(state with { BuilderSessionId = builder.ResumeToken ?? state.BuilderSessionId });
        }

        return new CodeReviewOutcome(critique, cap);
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
