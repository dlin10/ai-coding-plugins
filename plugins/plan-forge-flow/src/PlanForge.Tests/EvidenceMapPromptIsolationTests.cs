using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>A map produced by a real mapper call is never loaded into any Worker prompt.</summary>
public sealed class EvidenceMapPromptIsolationTests
{
    /// <summary>The plan-review critic receives only the plan and its normal projection.</summary>
    [Fact]
    public Task Plan_review_prompts_do_not_receive_the_map() => Check("plan.review");

    /// <summary>The task Builder receives no Evidence map.</summary>
    [Fact]
    public Task Build_prompts_do_not_receive_the_map() => Check("build");

    /// <summary>The code-review critic receives no Evidence map.</summary>
    [Fact]
    public Task Code_review_prompts_do_not_receive_the_map() => Check("code.review");

    /// <summary>The review-fix Builder receives no Evidence map.</summary>
    [Fact]
    public Task Review_fix_prompts_do_not_receive_the_map() => Check("review.fix");

    /// <summary>The read-only Scout receives no Evidence map.</summary>
    [Fact]
    public Task Scout_prompts_do_not_receive_the_map() => Check("scout");

    /// <summary>Writes a real map with distinctive content, then composes and executes one Worker act.</summary>
    /// <param name="act">The Worker act whose complete composed prompt is inspected.</param>
    private static async Task Check(string act)
    {
        using var fixture = new EvidenceToolRun();
        fixture.Advisor.Choose = question => FakeAdvisor.Valid(question, "insufficient_context");
        await fixture.Call();
        Assert.Contains(EvidenceToolRun.ItemMarker, AtomicFile.Read(fixture.Run.EvidenceMapPath));
        var run = fixture.Run;
        run.WriteState(run.ReadState() with { Approved = act is not "plan.review", CodeReviewRoundCap = 3 });
        var prompts = new PromptLibrary(RepositoryPrompts());
        var vendor = new RecordingVendor("codex");
        var selection = new Selection("model", null);
        if (act is "build" or "review.fix")
            vendor.Enqueue(new BuildResult("done", ["tracked.cs"], new Verification("passed", "checked"), "built"));
        else if (act is "scout")
            vendor.Enqueue(new ScoutReport { Summary = "new answer", ConfirmedFacts = [], MaterialAssumptions = [],
                OpenDecisions = [], LikelyChangeSurface = [], VerificationEvidence = [], DependentsAndPinnedBehaviour = [] });
        else vendor.Enqueue(new Critique("approve", [], "good"));
        switch (act)
        {
            case "plan.review":
                await new PlanReview(vendor, prompts).ReviewAsync(run, run.ReadPlan(), selection, null, null, false, CancellationToken.None);
                break;
            case "build":
                await new Build(vendor, prompts).NextAsync(run, selection, CancellationToken.None);
                break;
            case "code.review":
                await new CodeReview(vendor, prompts, new ReviewGit()).ReviewAsync(run, selection, false, CancellationToken.None);
                break;
            case "review.fix":
                var finding = run.ReadDecisionLedger().AddFinding(new Finding("major", "tracked.cs", "fix it"), LedgerPhase.CodeReview);
                await new ReviewFix(vendor, prompts).FixAsync(run, selection, null, "fix", [finding.FindingId], CancellationToken.None);
                break;
            case "scout":
                await new Scout(vendor, prompts).RunAsync(run, "Inspect tracked.cs", "fresh", CancellationToken.None);
                break;
        }
        var session = Assert.Single(vendor.Sessions);
        Assert.DoesNotContain(EvidenceToolRun.ItemMarker, session.PromptText);
        Assert.DoesNotContain(EvidenceToolRun.ItemMarker, session.Role.SystemPrompt);
        Assert.DoesNotContain("evidence-map.json", session.PromptText);
    }

    /// <summary>Locates the repository's role contracts above the test binary.</summary>
    private static string RepositoryPrompts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
        }
        throw new DirectoryNotFoundException("could not locate prompts above the test binary");
    }

    /// <summary>Supplies a bounded review window without starting git or a Vendor CLI.</summary>
    private sealed class ReviewGit : IReviewGit
    {
        /// <summary>Returns one ordinary changed source file.</summary>
        /// <param name="baselineHead">The Run's baseline.</param>
        /// <param name="excludedPaths">The round's exclusions.</param>
        /// <param name="ct">Caller cancellation.</param>
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, IReadOnlyList<string> excludedPaths,
                                                        CancellationToken ct) =>
            Task.FromResult(new ReviewWindow(baselineHead, baselineHead, false,
                ["tracked.cs"], [new ReviewFile("tracked.cs", "diff", false)]));
    }
}
