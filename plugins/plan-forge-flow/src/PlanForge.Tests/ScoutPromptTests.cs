using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutPromptTests : IDisposable
{
    private const string ScoutRoleText = "You are Scout, a read-only evidence gatherer.";
    private const string ReportText = "SCOUT-REPORT-ONLY-DO-NOT-FORWARD-7f2d";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-scout-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly PromptLibrary _prompts;

    public ScoutPromptTests()
    {
        Directory.CreateDirectory(_workspace);
        _prompts = new PromptLibrary(RepositoryPrompts());
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Plan_review_prompt_composition_does_not_load_scout_role_or_report()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new Critique("approve", [], "good"));
        var run = NewRun();

        await new PlanReview(vendor, _prompts).ReviewAsync(
            run, "## Approach\n\n1. Review the change.\n", new Selection("model", null),
            null, null, false, CancellationToken.None);

        AssertNoScoutContent(vendor);
    }

    [Fact]
    public async Task Build_prompt_composition_does_not_load_scout_role_or_report()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.cs"], new Verification("passed", "checked"), "built"));
        var run = NewRun(approved: true);

        await new Build(vendor, _prompts).NextAsync(run, new Selection("model", null), CancellationToken.None);

        AssertNoScoutContent(vendor);
    }

    [Fact]
    public async Task Code_review_prompt_composition_does_not_load_scout_role_or_report()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new Critique("approve", [], "good"));
        var run = NewRun(approved: true);

        await new CodeReview(vendor, _prompts, new ReviewGit()).ReviewAsync(
            run, new Selection("model", null), false, CancellationToken.None);

        AssertNoScoutContent(vendor);
    }

    [Fact]
    public async Task Review_fix_prompt_composition_does_not_load_scout_role_or_report()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.cs"], new Verification("passed", "checked"), "fixed"));
        var run = NewRun(approved: true);

        await new ReviewFix(vendor, _prompts).FixAsync(
            run, new Selection("model", null), "- **major** tracked.cs — fix it", null,
            CancellationToken.None);

        AssertNoScoutContent(vendor);
    }

    [Fact]
    public void Explicit_derived_conclusion_can_be_added_to_a_plan_or_task_without_loading_scout_role_text()
    {
        const string conclusion = "Derived Scout conclusion: the cache boundary is local to Run state.";
        var planPrompt = _prompts.LoadPlanReviewCritic("codex") + "\n" + conclusion;
        var taskPrompt = _prompts.Load("codex", VendorRole.Builder) + "\nTask: " + conclusion;

        Assert.Contains(conclusion, planPrompt, StringComparison.Ordinal);
        Assert.Contains(conclusion, taskPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ScoutRoleText, planPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ScoutRoleText, taskPrompt, StringComparison.Ordinal);
    }

    private RunDirectory NewRun(bool approved = false)
    {
        var runId = Guid.NewGuid().ToString("N");
        var run = RunDirectory.Create(_workspace, runId);
        run.WritePlan("## Approach\n\n1. Build tracked.cs.\n");
        run.WriteScoutReport($"# Latest Scout report\n\n{ScoutRoleText}\n\n{ReportText}\n");
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.UtcNow, 0, 5,
                                    BaselineHead: "base", Approved: approved, CodeReviewRoundCap: 3));
        return run;
    }

    private static void AssertNoScoutContent(RecordingVendor vendor)
    {
        var prompt = Assert.Single(vendor.Sessions).PromptText;
        Assert.DoesNotContain(ScoutRoleText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportText, prompt, StringComparison.Ordinal);
    }

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }

    private sealed class ReviewGit : IReviewGit
    {
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, CancellationToken ct) =>
            Task.FromResult(new ReviewWindow(baselineHead, baselineHead, IsFallback: false,
                                             ["tracked.cs"], "diff"));
    }
}
