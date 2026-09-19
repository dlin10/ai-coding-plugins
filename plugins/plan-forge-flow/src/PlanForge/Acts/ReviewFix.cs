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

        // Settled before the prompt is composed: the user's instructions go to a builder only on the
        // turn that starts its session, and a resumed one already has them. See docs/adr/0019.
        var sameVendor = string.Equals(state.BuilderVendor, vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;

        var prompt = Compose(findings, state.PendingGateFailure,
                             resumeToken is null ? state.BuilderInstructions : null);
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

        await using var builder = await vendor.StartAsync(new RoleSpec(VendorRole.Builder, prompts.Load(vendor.Id, VendorRole.Builder),
                                                                       state.BuilderRoots, WorkerTools.Effective(state.WorkerTools)),
                                                           selection, resumeToken, ct);

        BuildResult reported;
        try
        {
            reported = await BuilderTurn.RunAsync(builder, state.WorkspaceRoot, prompt, ct);
        }
        // The call is gone, so nothing can be returned — but the round happened, and what is known
        // of it goes down before the cancellation travels on. No gate runs: the tree is mid-edit
        // and the token that would run one is already dead.
        catch (TurnCutShortException cutShort)
        {
            run.AppendReviewFix(state.ReviewRounds + state.CodeReviewRounds, findings, deferred, cutShort: true);
            run.AppendFlowCutShort($"Fixes — round {state.CodeReviewRounds}", cutShort.FilesWritten);
            run.WriteState(Resumed(state, builder, sameVendor) with
            {
                PendingGateFailure = Gatekeeper.CutShortBrief(cutShort.FilesWritten)
            });

            throw;
        }

        // A fix round belongs to no single task, so the gates it answers to are the run-wide ones
        // under `## Gates` — the checks that span the whole change, which is what a fix touches.
        var plan = run.ReadPlan();
        var killed = builder.KilledBackgroundTasks;
        var result = await Gatekeeper.CheckAsync(reported, PlanGates.RunWideGates(plan), PlanGates.HasRunWideGates(plan), killed, state, ct);

        run.AppendReviewFix(state.ReviewRounds + state.CodeReviewRounds, findings, deferred);
        run.AppendFlowFix(state.CodeReviewRounds, findings, deferred, result);
        run.WriteState(Resumed(state, builder, sameVendor) with
        {
            PendingGateFailure = Gatekeeper.PendingFailure(result, killed, state.PendingGateFailure)
        });

        return result;
    }

    /// <summary>
    /// The builder's session as the run should remember it after a turn. A cut-short turn keeps its
    /// token like any other: the id arrives on the vendor's first stream line, long before the
    /// answer that never came, and without it the next fix starts a builder with no memory of the
    /// round it is continuing.
    /// </summary>
    private RunState Resumed(RunState state, IVendorSession builder, bool sameVendor) =>
        state with
        {
            BuilderSessionId = sameVendor
                                   ? builder.ResumeToken ?? state.BuilderSessionId
                                   : builder.ResumeToken ?? string.Empty,
            BuilderVendor = vendor.Id
        };

    private static string Compose(string findings, string? pendingGateFailure, string? instructions)
    {
        var prompt = new StringBuilder().AppendLine("# Fix these review findings")
                                        .AppendLine()
                                        .AppendLine(findings);
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}
