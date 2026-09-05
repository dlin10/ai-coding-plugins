using System.Text;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One builder pass over the findings the orchestrator chose to forward. The critic never talks to
/// the builder directly any more: the orchestrator sits between them, dropping findings the
/// approved plan excludes, and what it drops is recorded in the review log with a reason so the
/// next round's critic treats it as settled.
/// </summary>
internal sealed class ReviewFix(IVendor vendor, PromptLibrary prompts)
{
    public async Task<BuildResult> FixAsync(RunDirectory run,
                                            Selection selection,
                                            string findings,
                                            string? deferred,
                                            CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var prompt = Compose(findings, state.PendingGateFailure);
        SensitiveInput.Guard(prompt, "the code-review fixes");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");

        if (string.IsNullOrWhiteSpace(findings))
        {
            var skipped = new BuildResult("done", [],
                new Verification("passed", "no findings were sent to the builder; nothing to change or verify"),
                "no findings passed through to the builder");
            run.AppendReviewFix(state.ReviewRounds + state.CodeReviewRounds, findings, deferred);
            run.AppendFlowFix(state.CodeReviewRounds, findings, deferred, skipped);
            return skipped;
        }

        var sameVendor = string.Equals(state.BuilderVendor, vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;
        await using var builder = await vendor.StartAsync(new RoleSpec(VendorRole.Builder, prompts.Load(vendor.Id, VendorRole.Builder), state.BuilderRoots),
                                                           selection, resumeToken, ct);

        var reported = await BuilderTurn.RunAsync(builder, state.WorkspaceRoot, prompt, ct);

        // A fix round belongs to no single task, so the gates it answers to are the run-wide ones
        // under `## Gates` — the checks that span the whole change, which is what a fix touches.
        var plan = run.ReadPlan();
        var result = await Gatekeeper.CheckAsync(reported, PlanGates.RunWideGates(plan), PlanGates.HasRunWideGates(plan), state, ct);

        run.AppendReviewFix(state.ReviewRounds + state.CodeReviewRounds, findings, deferred);
        run.AppendFlowFix(state.CodeReviewRounds, findings, deferred, result);
        run.WriteState(state with
        {
            PendingGateFailure = Gatekeeper.PendingFailure(result, state.PendingGateFailure),
            BuilderSessionId = sameVendor
                                   ? builder.ResumeToken ?? state.BuilderSessionId
                                   : builder.ResumeToken ?? string.Empty,
            BuilderVendor = vendor.Id
        });

        return result;
    }

    private static string Compose(string findings, string? pendingGateFailure)
    {
        var prompt = new StringBuilder().AppendLine("# Fix these review findings")
                                        .AppendLine()
                                        .AppendLine(findings);
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        return prompt.ToString();
    }
}
