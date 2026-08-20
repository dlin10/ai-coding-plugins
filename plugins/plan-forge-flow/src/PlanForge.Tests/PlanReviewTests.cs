using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanReviewTests : IDisposable
{
    private static readonly string[] Holes = ["retention", "delete", "verif", "fail", "rollback"];

    private const string HoledPlan =
        """
        # Plan: nightly export

        1. Read every row from the orders table.
        2. Write them to a CSV on the shared drive.
        3. Delete rows older than the retention window.
        """;

    private const string HardenedPlan =
        """
        # Plan: nightly export

        Retention window is 400 days, defined in `config/retention.yaml`; nothing else may define it.

        1. Snapshot `orders` at a read-committed transaction boundary, paging 10k rows at a time so
           the export cannot hold a long transaction. Verify: row count matches `SELECT COUNT(*)`
           taken inside the same transaction.
        2. Write to `\\share\exports\orders-<date>.csv.tmp`, fsync, then rename to the final name.
           A partial file therefore never appears under the final name. Verify: the temp file is
           gone and the final file's row count matches step 1.
        3. Only after step 2's verification passes, delete rows older than the retention window,
           in batches of 10k, logging the id range of each batch. Verify: the oldest remaining row
           is newer than the window, and the deleted count equals the pre-computed count.

        Failure handling: any step that fails aborts the run and leaves the database untouched;
        step 3 is the only destructive step and it runs last, behind a passing verification.
        Re-running after a failure is safe because step 2 is idempotent per date.
        """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Review_is_rejected_once_the_cap_is_reached()
    {
        var run = NewRun(rounds: 5, cap: 5);
        var act = NewAct();

        var rejection = await Assert.ThrowsAsync<ReviewCapReachedException>(
            () => act.ReviewAsync(run, HoledPlan, new Selection("sonnet", null), CancellationToken.None));

        Assert.Contains("cap is 5", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(5, run.ReadState().ReviewRounds);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Rounds_accumulate_and_a_revised_plan_fares_better()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var run = NewRun(rounds: 0, cap: 5);
        var act = NewAct();
        var selection = new Selection("sonnet", "low");

        var first = await act.ReviewAsync(run, HoledPlan, selection, ct);
        Assert.Equal("revise", first.Verdict);
        Assert.NotEmpty(first.Findings);

        // The holes are real ones: an undefined retention window, an unverified destructive delete,
        // and no failure handling. A critique that named none of them would be worthless.
        var raised = string.Join(" ", first.Findings.Select(f => $"{f.Where} {f.What}"));
        Assert.Contains(Holes, hole => raised.Contains(hole, StringComparison.OrdinalIgnoreCase));

        var second = await act.ReviewAsync(run, HardenedPlan, selection, ct);

        Assert.Equal(2, run.ReadState().ReviewRounds);

        var log = File.ReadAllText(run.ReviewLogPath);
        Assert.Contains("## Round 1", log, StringComparison.Ordinal);
        Assert.Contains("## Round 2", log, StringComparison.Ordinal);

        Assert.True(second.Verdict is "approve" || second.Findings.Count < first.Findings.Count,
            $"hardened plan fared no better: verdict={second.Verdict}, " +
            $"findings {first.Findings.Count} -> {second.Findings.Count}");
    }

    [Fact]
    public async Task The_critique_lands_in_the_flow_log()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [new Finding("major", "step 3", "no verification step named")],
            "one hole"));
        var run = NewRun(rounds: 0, cap: 5);

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HoledPlan, new Selection("critic-model", null), CancellationToken.None);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan review — round 1", flow, StringComparison.Ordinal);
        Assert.Contains("Verdict: revise", flow, StringComparison.Ordinal);
        Assert.Contains("no verification step named", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The requirements contract reaches the critic only through <c>LoadPlanReviewCritic</c>. A
    /// plan review wired to the plain critic prompt still runs and still returns a verdict — it
    /// just judges the tasks and never the requirements above them, which nothing else would catch.
    /// </summary>
    [Fact]
    public async Task The_critic_is_told_that_the_requirements_are_under_review()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 0, cap: 5);

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HoledPlan, new Selection("critic-model", null), CancellationToken.None);

        Assert.Contains("Coverage runs both ways", critic.Sessions[0].Role.SystemPrompt,
            StringComparison.Ordinal);
    }

    private RunDirectory NewRun(int rounds, int cap)
    {
        const string runId = "test-run";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, rounds, cap));
        return run;
    }

    private static PlanReview NewAct() =>
        new(new ClaudeCliVendor(), new PromptLibrary(RepositoryPrompts()));

    /// <summary>Walks up from the test binary to the repository's editable prompt tree.</summary>
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
}
