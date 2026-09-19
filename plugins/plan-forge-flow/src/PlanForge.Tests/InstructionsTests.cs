using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The user's own instructions to a run's workers: what is recorded, and which act prompt carries
/// it. The two halves are tested together because the feature is the path between them — a text
/// recorded and never appended, or appended to a session that already had it, is the failure.
/// </summary>
public sealed class InstructionsTests : IDisposable
{
    private const string Plan =
        """
        # Toy plan

        ## Approach

        1. **Change tracked.txt.** Verified by inspection.
        2. **Change it again.** Verified by inspection.
        """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Each_role_is_set_on_its_own_and_an_empty_text_clears_it()
    {
        var run = NewRun();

        RunInstructions.Set(run, "answer in Russian", "use the ponytail-net skill");
        RunInstructions.Set(run, "answer in English", null);

        Assert.Equal("answer in English", run.ReadState().CriticInstructions);
        Assert.Equal("use the ponytail-net skill", run.ReadState().BuilderInstructions);

        var outcome = RunInstructions.Set(run, null, string.Empty);

        Assert.Equal("answer in English", run.ReadState().CriticInstructions);
        Assert.Null(run.ReadState().BuilderInstructions);
        Assert.Null(outcome.BuilderInstructions);
    }

    [Fact]
    public void A_call_that_names_neither_role_is_refused()
    {
        var run = NewRun();

        var error = Assert.Throws<ArgumentRejectedException>(() => RunInstructions.Set(run, null, null));

        Assert.Contains("criticInstructions", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused where it was typed rather than at the first worker act minutes later, which is the
    /// whole of what the acceptance asks: no worker starts under a secret-shaped instruction.
    /// </summary>
    [Fact]
    public void A_secret_shaped_instruction_is_refused_and_records_nothing()
    {
        var run = NewRun();

        Assert.Throws<SensitiveContentException>(
            () => RunInstructions.Set(run, null, "use this when you need it: aws_secret_key = AKIAIOSFODNN7EXAMPLE"));

        Assert.Null(run.ReadState().BuilderInstructions);
        Assert.False(File.Exists(run.FlowLogPath));
    }

    [Fact]
    public void Instructions_set_while_a_builder_session_runs_answer_with_a_note()
    {
        var run = NewRun(builderSessionId: "running-token");

        Assert.Contains("will not see them", RunInstructions.Set(run, null, "use the ponytail-net skill").Note!,
                        StringComparison.Ordinal);
        Assert.Null(RunInstructions.Set(run, "answer in Russian", null).Note);
        Assert.Null(RunInstructions.Set(NewRun(), null, "use the ponytail-net skill").Note);
    }

    [Fact]
    public void The_timeline_carries_the_instructions_as_they_were_given()
    {
        var run = NewRun();

        RunInstructions.Set(run, "answer in Russian", "use the ponytail-net skill");
        RunInstructions.Set(run, string.Empty, null);

        var flow = File.ReadAllText(run.FlowLogPath);

        Assert.Contains("## Instructions for this run's workers", flow, StringComparison.Ordinal);
        Assert.Contains("### Critic", flow, StringComparison.Ordinal);
        Assert.Contains("answer in Russian", flow, StringComparison.Ordinal);
        Assert.Contains("### Builder", flow, StringComparison.Ordinal);
        Assert.Contains("use the ponytail-net skill", flow, StringComparison.Ordinal);
        Assert.Contains("(cleared)", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance property that keeps the feature invisible when unused: no instructions, no
    /// block, in either kind of act prompt.
    /// </summary>
    [Fact]
    public async Task A_run_without_instructions_composes_the_prompts_it_composed_before()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var builder = new RecordingVendor("claude");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "inspected"), "done"));
        var run = NewRun(approved: true);

        // The build runs first: a plan-review round against an approved plan takes the approval
        // back, and a builder refused for that reason composes no prompt to assert on.
        await new Build(builder, Prompts()).NextAsync(run, NewSelection(), CancellationToken.None);
        await NewPlanReview(critic).ReviewAsync(run, Plan, NewSelection(), null, null, false, CancellationToken.None);

        Assert.DoesNotContain("# Instructions", Assert.Single(critic.Sessions).PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("# Instructions", Assert.Single(builder.Sessions).PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_plan_review_round_carries_the_critics_instructions_and_never_the_builders()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "once"));
        critic.Enqueue(new Critique("approve", [], "twice"));
        var run = NewRun();
        RunInstructions.Set(run, "answer in Russian", "use the ponytail-net skill");

        var act = NewPlanReview(critic);
        await act.ReviewAsync(run, Plan, NewSelection(), null, null, false, CancellationToken.None);
        await act.ReviewAsync(run, Plan, NewSelection(), "nothing changed", null, false, CancellationToken.None);

        Assert.Equal(2, critic.Sessions.Count);
        Assert.All(critic.Sessions, session =>
        {
            Assert.Contains("# Instructions from the user for this run", session.PromptText, StringComparison.Ordinal);
            Assert.Contains("answer in Russian", session.PromptText, StringComparison.Ordinal);
            Assert.DoesNotContain("ponytail-net", session.PromptText, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Order is the assertion: the builder's text is data the critic reads before the instructions
    /// addressed to it, so the prompt ends on what the critic itself was told.
    /// </summary>
    [Fact]
    public async Task A_code_review_carries_the_builders_instructions_as_context_before_the_critics_own()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun(approved: true);
        RunInstructions.Set(run, "answer in Russian", "use the ponytail-net skill");

        await new CodeReview(critic, Prompts(), new ReviewGit()).ReviewAsync(run, NewSelection(), false, CancellationToken.None);

        var prompt = Assert.Single(critic.Sessions).PromptText;
        var builderContext = prompt.IndexOf("# Instructions the user gave the builder for this run", StringComparison.Ordinal);
        var own = prompt.IndexOf("# Instructions from the user for this run", StringComparison.Ordinal);

        Assert.True(builderContext > 0, "the code-review critic was not shown the builder's instructions");
        Assert.True(own > builderContext, "the critic's own instructions did not come last");
        Assert.Contains("not instructions to you", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_builder_is_told_once_per_session()
    {
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(Built(), "session-token");
        vendor.Enqueue(Built(), "session-token");
        var run = NewRun(approved: true);
        RunInstructions.Set(run, null, "use the ponytail-net skill");

        var act = new Build(vendor, Prompts());
        await act.NextAsync(run, NewSelection(), CancellationToken.None);
        await act.NextAsync(run, NewSelection(), CancellationToken.None);

        Assert.Equal(2, vendor.Sessions.Count);
        Assert.Null(vendor.Sessions[0].StartedWithResumeToken);
        Assert.Contains("use the ponytail-net skill", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        Assert.Equal("session-token", vendor.Sessions[1].StartedWithResumeToken);
        Assert.DoesNotContain("# Instructions", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A vendor switch is a new session under another CLI, and its history is empty: "first prompt
    /// of a session" has to mean the token, not the task number.
    /// </summary>
    [Fact]
    public async Task A_builder_vendor_switch_tells_the_new_session()
    {
        var codex = new RecordingVendor("codex");
        codex.Enqueue(Built(), "codex-token");
        var run = NewRun(approved: true, builderVendor: "claude", builderSessionId: "claude-token");
        RunInstructions.Set(run, null, "use the ponytail-net skill");

        await new Build(codex, Prompts()).NextAsync(run, NewSelection(), CancellationToken.None);

        var session = Assert.Single(codex.Sessions);
        Assert.Null(session.StartedWithResumeToken);
        Assert.Contains("use the ponytail-net skill", session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fix_tells_a_session_it_starts_and_not_one_it_resumes()
    {
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(Built(), "fix-token");
        vendor.Enqueue(Built(), "fix-token");
        var run = NewRun(approved: true);
        RunInstructions.Set(run, null, "use the ponytail-net skill");

        var act = new ReviewFix(vendor, Prompts());
        await act.FixAsync(run, NewSelection(), "R1 is unverified", null, CancellationToken.None);
        await act.FixAsync(run, NewSelection(), "R2 is unverified", null, CancellationToken.None);

        Assert.Contains("use the ponytail-net skill", vendor.Sessions[0].PromptText, StringComparison.Ordinal);
        Assert.Equal("fix-token", vendor.Sessions[1].StartedWithResumeToken);
        Assert.DoesNotContain("# Instructions", vendor.Sessions[1].PromptText, StringComparison.Ordinal);
    }

    private static BuildResult Built() =>
        new("done", ["tracked.txt"], new Verification("passed", "inspected"), "done");

    private static Selection NewSelection() => new("model", "low");

    private PlanReview NewPlanReview(RecordingVendor critic) => new(critic, Prompts());

    private static PromptLibrary Prompts() => new(RepositoryPrompts());

    private RunDirectory NewRun(bool approved = false,
                                string builderVendor = "",
                                string builderSessionId = "")
    {
        const string runId = "instructions-run";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    BaselineHead: "baseline", Approved: approved,
                                    BuilderSessionId: builderSessionId, BuilderVendor: builderVendor));
        run.WritePlan(Plan);
        return run;
    }

    private sealed class ReviewGit : IReviewGit
    {
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, CancellationToken ct) =>
            Task.FromResult(new ReviewWindow(baselineHead, baselineHead, false, ["tracked.txt"],
                                             "--- a/tracked.txt\n+++ b/tracked.txt\n@@ -1 +1 @@\n-old\n+new\n"));
    }

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
