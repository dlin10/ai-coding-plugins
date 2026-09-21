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
        Selection? selection,
        string? findings,
        string? deferred,
        string? revision,
        bool userGrantedRound,
        CancellationToken ct,
        string? question = null,
        string? sessionMode = null)
    {
        ValidateArguments(act, planDraft, selection, findings, deferred, revision, userGrantedRound,
                          question, sessionMode);
        ArgumentNullException.ThrowIfNull(run);

        switch (act)
        {
            case "plan.review":
                var critique = await new PlanReview(_vendor, _prompts)
                    .ReviewAsync(run, planDraft, selection!, revision, deferred, userGrantedRound, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(critique, ContractJson.Default.Critique);

            case "build.next":
                var build = await new Build(_vendor, _prompts)
                    .NextAsync(run, selection!, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(build, ForgeToolJson.Default.BuildOutcome);

            case "review.code":
                var git = _git ?? new GitClient(run.ReadState().WorkspaceRoot);
                var codeReview = await new CodeReview(_vendor, _prompts, git)
                    .ReviewAsync(run, selection!, userGrantedRound, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(codeReview, ContractJson.Default.Critique);

            case "review.fix":
                var fix = await new ReviewFix(_vendor, _prompts)
                    .FixAsync(run, selection!, findings!, deferred, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(fix, ContractJson.Default.BuildResult);

            case "scout":
                var scout = await new Scout(_vendor, _prompts)
                    .RunAsync(run, question!, sessionMode!, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(scout, ForgeToolJson.Default.ScoutDigest);

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
        bool userGrantedRound,
        string? question = null,
        string? sessionMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);

        if (act is not "plan.review" and not "build.next" and not "review.code" and not "review.fix" and not "scout")
            throw new ArgumentRejectedException($"unknown work act '{act}'");

        if (act == "scout")
        {
            RejectPresent(planDraft, nameof(planDraft), act);
            RejectPresent(selection?.Model, nameof(selection), act);
            RejectPresent(selection?.Effort, nameof(selection), act);
            RejectPresent(findings, nameof(findings), act);
            RejectPresent(deferred, nameof(deferred), act);
            RejectPresent(revision, nameof(revision), act);
            RejectProvided(userGrantedRound, nameof(userGrantedRound), act);
            if (question is null)
                throw new ArgumentRejectedException("scout requires question");
            Scout.ValidateQuestion(question);
            if (sessionMode is null)
                throw new ArgumentRejectedException("scout requires sessionMode");
            Scout.ValidateSessionMode(sessionMode);
            return;
        }

        if (selection is null || string.IsNullOrWhiteSpace(selection.Model))
            throw new ArgumentRejectedException($"{act} requires model");

        switch (act)
        {
            // planDraft is optional here and nowhere else: forge.plan.write puts the draft on disk
            // ahead of the round, and the act reads it from there when the start omits it. Whether
            // there is one to read is a question about the run, so forge.work.start asks it through
            // PlanReview.RequireDraft rather than here.
            case "plan.review":
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

        RejectPresent(question, nameof(question), act);
        RejectPresent(sessionMode, nameof(sessionMode), act);
    }

    private static void RejectProvided(string? value, string argumentName, string act)
    {
        if (!string.IsNullOrWhiteSpace(value))
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectPresent(string? value, string argumentName, string act)
    {
        if (value is not null)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }

    private static void RejectProvided(bool value, string argumentName, string act)
    {
        if (value)
            throw new ArgumentRejectedException($"{argumentName} is not used by {act}");
    }
}
