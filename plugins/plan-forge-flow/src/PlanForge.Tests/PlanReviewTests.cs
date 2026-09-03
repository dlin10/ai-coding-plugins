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
            () => act.ReviewAsync(run, HoledPlan, new Selection("sonnet", null), "tightened step 3", null,
                                  false, CancellationToken.None));

        Assert.Contains("cap is 5", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("userGrantedRound", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(5, run.ReadState().ReviewRounds);
    }

    /// <summary>
    /// The counters are only half of what a grant has to prove: "exactly one higher" on both
    /// <c>ReviewRounds</c> and <c>ReviewRoundCap</c> is what stops a single grant from raising the
    /// cap twice, and the flow-log entry is the half the counters do not cover — a reader has to see
    /// the round as bought, not budgeted.
    /// </summary>
    [Fact]
    public async Task A_granted_round_runs_past_the_cap_and_raises_it_by_one()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 5, cap: 5);

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                         "reworked step 3", null, true, CancellationToken.None);

        var state = run.ReadState();
        Assert.Equal(6, state.ReviewRounds);
        Assert.Equal(6, state.ReviewRoundCap);
        Assert.Equal(1, state.GrantedReviewRounds);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan review — extra round granted", flow, StringComparison.Ordinal);
        Assert.True(flow.IndexOf("## Plan review — extra round granted", StringComparison.Ordinal)
                    < flow.IndexOf("## Plan review — round 6", StringComparison.Ordinal),
                    "the grant must read before the critique it unlocked");
    }

    /// <summary>
    /// A grant is spent by the call that used it, not carried by the run: once it raises the cap, a
    /// further round past the new cap is refused again unless it brings its own grant. That is what
    /// makes "ask every time" fall out of the mechanism instead of depending on the orchestrator to
    /// remember.
    /// </summary>
    [Fact]
    public async Task A_round_past_the_raised_cap_is_refused_without_a_new_grant()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 5, cap: 5);
        var act = new PlanReview(critic, new PromptLibrary(RepositoryPrompts()));

        await act.ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                              "reworked step 3", null, true, CancellationToken.None);

        var rejection = await Assert.ThrowsAsync<ReviewCapReachedException>(
            () => act.ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                                  "reworked step 3 again", null, false, CancellationToken.None));

        Assert.Contains("cap is 6", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant is written after the critique returns, and this is the test that makes that
    /// ordering a property rather than only a comment: a builder who moved <c>WriteState</c> ahead
    /// of the critique would spend the grant on a round that never happened.
    /// </summary>
    [Fact]
    public async Task A_granted_rounds_dying_critique_spends_nothing()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new InvalidOperationException("vendor died mid-turn"));
        var run = NewRun(rounds: 5, cap: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
                .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                             "reworked step 3", null, true, CancellationToken.None));

        var state = run.ReadState();
        Assert.Equal(5, state.ReviewRounds);
        Assert.Equal(5, state.ReviewRoundCap);
        Assert.Equal(0, state.GrantedReviewRounds);
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

        var first = await act.ReviewAsync(run, HoledPlan, selection, null, null, false, ct);
        Assert.Equal("revise", first.Verdict);
        Assert.NotEmpty(first.Findings);

        // The holes are real ones: an undefined retention window, an unverified destructive delete,
        // and no failure handling. A critique that named none of them would be worthless.
        var raised = string.Join(" ", first.Findings.Select(f => $"{f.Where} {f.What}"));
        Assert.Contains(Holes, hole => raised.Contains(hole, StringComparison.OrdinalIgnoreCase));

        var second = await act.ReviewAsync(run, HardenedPlan, selection,
                                           "defined the retention window, verified every step, and made the "
                                           + "destructive delete run last behind a passing verification",
                                           null, false, ct);

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
            .ReviewAsync(run, HoledPlan, new Selection("critic-model", null), null, null, false,
                        CancellationToken.None);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan review — round 1", flow, StringComparison.Ordinal);
        Assert.Contains("Verdict: revise", flow, StringComparison.Ordinal);
        Assert.Contains("no verification step named", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gap this closes: the timeline used to show one verdict after another with nothing
    /// between them, because the orchestrator's turn — the only one that changes the plan — was
    /// never written anywhere.
    /// </summary>
    [Fact]
    public async Task The_orchestrators_answer_to_a_round_lands_in_the_flow_log_before_the_next_critique()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 1, cap: 5);

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                         "named the retention window and moved the delete behind a passing verification",
                         "- rollback rehearsal — the user ruled it out of scope", false, CancellationToken.None);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan revision after round 1", flow, StringComparison.Ordinal);
        Assert.Contains("named the retention window", flow, StringComparison.Ordinal);
        Assert.Contains("### Deferred by the orchestrator", flow, StringComparison.Ordinal);
        Assert.True(flow.IndexOf("## Plan revision after round 1", StringComparison.Ordinal)
                    < flow.IndexOf("## Plan review — round 2", StringComparison.Ordinal),
                    "the revision must read before the round it answers into");
    }

    /// <summary>
    /// Only the deferral travels to the critic. What was changed is already in the draft it is
    /// handed; what was refused is invisible there, and comes back as the same finding every round
    /// unless the log records it as a decision.
    /// </summary>
    [Fact]
    public async Task A_deferral_reaches_the_next_rounds_critic_and_the_revision_does_not()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 1, cap: 5);

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                         "rewrote the verification of step 2",
                         "- rollback rehearsal — the user ruled it out of scope", false, CancellationToken.None);

        var reviewLog = run.ReadReviewLog();
        Assert.Contains("## Round 1 — deferred by the orchestrator", reviewLog, StringComparison.Ordinal);
        Assert.Contains("rollback rehearsal", reviewLog, StringComparison.Ordinal);
        Assert.DoesNotContain("rewrote the verification", reviewLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_after_the_first_is_refused_without_a_revision()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 1, cap: 5);

        var rejection = await Assert.ThrowsAsync<RevisionMissingException>(
            () => new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
                .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null), " ", null,
                             false, CancellationToken.None));

        Assert.Contains("round 1 has already run", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
        Assert.Equal(1, run.ReadState().ReviewRounds);
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
            .ReviewAsync(run, HoledPlan, new Selection("critic-model", null), null, null, false,
                        CancellationToken.None);

        Assert.Contains("Coverage runs both ways", critic.Sessions[0].Role.SystemPrompt,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The write is deliberately ahead of the vendor call, not behind it: a round runs for minutes,
    /// and the plan exists to be read during them. A vendor with nothing scripted throws out of
    /// <c>StartAsync</c>, which is the earliest a real round can die.
    /// </summary>
    [Fact]
    public async Task The_draft_reaches_the_plan_file_before_the_critic_runs()
    {
        var critic = new RecordingVendor("claude");
        var run = NewRun(rounds: 0, cap: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
                .ReviewAsync(run, HoledPlan, new Selection("critic-model", null), null, null,
                             false, CancellationToken.None));

        Assert.Equal(HoledPlan, run.ReadPlan());
    }

    [Fact]
    public async Task Every_round_rewrites_the_plan_file_with_the_draft_it_was_handed()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [new Finding("major", "step 3", "no verification")], "one hole"));
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 0, cap: 5);
        var act = new PlanReview(critic, new PromptLibrary(RepositoryPrompts()));

        await act.ReviewAsync(run, HoledPlan, new Selection("critic-model", null), null, null,
                              false, CancellationToken.None);
        Assert.Equal(HoledPlan, run.ReadPlan());

        await act.ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                              "named the retention window", null, false, CancellationToken.None);

        Assert.Equal(HardenedPlan, run.ReadPlan());
    }

    /// <summary>
    /// The price of writing the plan from the first round on: the file the builder reads is no
    /// longer frozen at approval, so a later round has to take the approval back rather than leave
    /// a raised flag over text nobody approved.
    /// </summary>
    /// <remarks>
    /// The <c>ReviewRounds</c> assertion is the regression guard. The closing <c>WriteState</c>
    /// builds on the state captured at the top of the act, and a capture taken before the
    /// withdrawal would restore <c>Approved</c> on its way out — silently, and only on the path
    /// where a round succeeds.
    /// </remarks>
    [Fact]
    public async Task A_round_against_an_approved_plan_takes_the_approval_back()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "nothing left"));
        var run = NewRun(rounds: 1, cap: 5);
        run.WriteState(run.ReadState() with
        {
            Approved = true, TasksCompleted = 3, BuilderSessionId = "builder-session"
        });

        await new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
            .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                         "reworked step 3", null, false, CancellationToken.None);

        var state = run.ReadState();
        Assert.False(state.Approved);
        Assert.Equal(0, state.TasksCompleted);
        Assert.Equal(string.Empty, state.BuilderSessionId);
        Assert.Equal(2, state.ReviewRounds);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan reopened", flow, StringComparison.Ordinal);
        Assert.Contains("reset from 3 completed tasks", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The crash window between withdrawing the approval and writing the plan is one-directional by
    /// construction: whatever fails afterwards, the run is left refusing to build rather than
    /// building an unapproved plan.
    /// </summary>
    [Fact]
    public async Task A_round_that_dies_leaves_the_approval_withdrawn_rather_than_the_plan_unguarded()
    {
        var critic = new RecordingVendor("claude");
        var run = NewRun(rounds: 1, cap: 5);
        run.WriteState(run.ReadState() with { Approved = true, TasksCompleted = 3 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PlanReview(critic, new PromptLibrary(RepositoryPrompts()))
                .ReviewAsync(run, HardenedPlan, new Selection("critic-model", null),
                             "reworked step 3", null, false, CancellationToken.None));

        Assert.False(run.ReadState().Approved);
        Assert.Equal(HardenedPlan, run.ReadPlan());
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
