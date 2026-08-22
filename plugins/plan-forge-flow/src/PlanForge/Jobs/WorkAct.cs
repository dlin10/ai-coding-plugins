using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Jobs;

internal sealed class WorkAct
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;
    private readonly IReviewGit? _git;

    public WorkAct(IVendor vendor)
        : this(vendor, new PromptLibrary())
    {
    }

    public WorkAct(IVendor vendor, PromptLibrary prompts, IReviewGit? git = null)
    {
        _vendor = vendor;
        _prompts = prompts;
        _git = git;
    }

    public async Task<string> RunAsync(
        string act,
        RunDirectory run,
        string? planDraft,
        Selection selection,
        string? findings,
        string? deferred,
        string? revision,
        CancellationToken ct)
    {
        ValidateArguments(act, planDraft, selection, findings, deferred, revision);
        ArgumentNullException.ThrowIfNull(run);

        switch (act)
        {
            case "plan.review":
                var critique = await new PlanReview(_vendor, _prompts)
                    .ReviewAsync(run, planDraft!, selection, revision, deferred, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(critique, ContractJson.Default.Critique);

            case "build.next":
                var build = await new Build(_vendor, _prompts)
                    .NextAsync(run, selection, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(build, ForgeToolJson.Default.BuildOutcome);

            case "review.code":
                var git = _git ?? new GitClient(run.ReadState().WorkspaceRoot);
                var codeReview = await new CodeReview(_vendor, _prompts, git)
                    .ReviewAsync(run, selection, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(codeReview, ContractJson.Default.Critique);

            case "review.fix":
                var fix = await new ReviewFix(_vendor, _prompts)
                    .FixAsync(run, selection, findings!, deferred, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(fix, ContractJson.Default.BuildResult);

            default:
                throw new ArgumentException($"unknown work act '{act}'", nameof(act));
        }
    }

    internal static void ValidateArguments(
        string act,
        string? planDraft,
        Selection? selection,
        string? findings,
        string? deferred,
        string? revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);

        if (act is not "plan.review" and not "build.next" and not "review.code" and not "review.fix")
            throw new ArgumentException($"unknown work act '{act}'", nameof(act));

        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Model);

        switch (act)
        {
            case "plan.review":
                Require(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                break;
            case "build.next":
            case "review.code":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                break;
            case "review.fix":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(revision, nameof(revision), act);
                if (findings is null)
                    throw new ArgumentException($"{act} requires findings", nameof(findings));
                break;
        }
    }

    private static void Require(string? value, string argumentName, string act)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{act} requires {argumentName}", argumentName);
    }

    private static void RejectProvided(string? value, string argumentName, string act)
    {
        if (!string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{argumentName} is not used by {act}", argumentName);
    }
}
