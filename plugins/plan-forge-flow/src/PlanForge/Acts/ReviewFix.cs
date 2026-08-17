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

        var prompt = Compose(findings);
        SensitiveInput.Guard(prompt, "the code-review fixes");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");

        var sameVendor = string.Equals(state.BuilderVendor, vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;
        await using var builder = await vendor.StartAsync(new RoleSpec(VendorRole.Builder, prompts.Load(vendor.Id, VendorRole.Builder)),
                                                           selection, resumeToken, ct);

        var result = await builder.RunAsync(prompt, Schemas.BuildResult, ct);

        run.AppendReviewFix(state.ReviewRounds + state.CodeReviewRounds, findings, deferred);
        run.WriteState(state with
        {
            BuilderSessionId = sameVendor
                                   ? builder.ResumeToken ?? state.BuilderSessionId
                                   : builder.ResumeToken ?? string.Empty,
            BuilderVendor = vendor.Id
        });

        return result;
    }

    private static string Compose(string findings) => new StringBuilder().AppendLine("# Fix these review findings")
                                                                         .AppendLine()
                                                                         .AppendLine(findings)
                                                                         .ToString();
}
