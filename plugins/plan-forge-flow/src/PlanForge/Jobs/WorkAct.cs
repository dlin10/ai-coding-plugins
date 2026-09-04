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
        bool userGrantedRound,
        CancellationToken ct)
    {
        ValidateArguments(act, planDraft, selection, findings, deferred, revision, userGrantedRound);
        ArgumentNullException.ThrowIfNull(run);

        switch (act)
        {
            case "plan.review":
                var critique = await new PlanReview(_vendor, _prompts)
                    .ReviewAsync(run, planDraft!, selection, revision, deferred, userGrantedRound, ct)
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
                    .ReviewAsync(run, selection, userGrantedRound, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(codeReview, ContractJson.Default.Critique);

            case "review.fix":
                var fix = await new ReviewFix(_vendor, _prompts)
                    .FixAsync(run, selection, findings!, deferred, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(fix, ContractJson.Default.BuildResult);

            default:
                throw new ArgumentRejectedException($"unknown work act '{act}'");
        }
    }

    internal static void ValidateArguments(
        string act,
        string? planDraft,
        Selection? selection,
        string? findings,
        string? deferred,
        string? revision,
        bool userGrantedRound)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);

        if (act is not "plan.review" and not "build.next" and not "review.code" and not "review.fix")
            throw new ArgumentRejectedException($"unknown work act '{act}'");

        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Model);

        switch (act)
        {
            case "plan.review":
                Require(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                break;
            case "build.next":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
                break;
            case "review.code":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(findings, nameof(findings), act);
                RejectProvided(deferred, nameof(deferred), act);
                RejectProvided(revision, nameof(revision), act);
                break;
            case "review.fix":
                RejectProvided(planDraft, nameof(planDraft), act);
                RejectProvided(revision, nameof(revision), act);
                RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
                if (findings is null)
                    throw new ArgumentRejectedException($"{act} requires findings");
                break;
        }
    }

    private static void Require(string? value, string argumentName, string act)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentRejectedException($"{act} requires {argumentName}");
    }

    private static void RejectProvided(string? value, string argumentName, string act)
    {
        if (!string.IsNullOrWhiteSpace(value))
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectProvided(bool value, string argumentName, string act)
    {
        if (value)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }
}
