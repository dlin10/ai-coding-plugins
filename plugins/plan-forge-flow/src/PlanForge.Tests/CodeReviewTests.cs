using System.Globalization;
using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class CodeReviewTests : IDisposable
{
    private const string Plan = "## Approach\n\n1. **Change tracked.txt.** Verified by inspection.\n";

    /// <summary>
    /// A library model as concurrency-hunter's generator wrote it for
    /// <c>Microsoft.Extensions.Configuration.UserSecrets</c>: a hash beside a key, and strings that
    /// stand alone on their lines in an array.
    /// </summary>
    private const string GeneratedModel = """
        {
          "schemaVersion": 2,
          "generator": "e67eee3e02f7314f0fad7f17243ea570c8bea8d9af0e36df7012b0d1cb9171c4",
          "assembly": {
            "name": "Microsoft.Extensions.Configuration.UserSecrets",
            "version": "10.0.0.0",
            "mvid": null,
            "framework": "Microsoft.AspNetCore.App",
            "major": 10
          },
          "members": [
            {
              "member": "M:Microsoft.Extensions.Configuration.UserSecretsConfigurationExtensions.AddUserSecrets(Microsoft.Extensions.Configuration.IConfigurationBuilder,System.Reflection.Assembly,System.Boolean,System.Boolean)",
              "refusal": {
                "reason": "unknown-touch",
                "detail": "",
                "causes": [
                  "argument-member-unseen",
                  "probe-handed-to-unseen",
                  "unsupported-store",
                  "value-unobserved",
                  "witness-value-handed"
                ]
              }
            }
          ]
        }
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;
    private string _baselineHead = "baseline";

    public CodeReviewTests()
    {
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task The_plan_and_the_diff_reach_the_critic_without_documentation()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", "high"), false, ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Equal("approve", critique.Verdict);
        Assert.Contains("# Approved plan", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Change tracked.txt.", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("ordinary change", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("context change", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("adr change", session.PromptText, StringComparison.Ordinal);
        Assert.Equal(1, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task Work_committed_after_the_baseline_reaches_the_critic_with_its_review_window()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("tracked.txt", "committed during the run\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "run work"], ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(
            NewRun(baselineHead: baseline.Head), new Selection("critic-model", null), false, ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Contains("# Review window", session.PromptText, StringComparison.Ordinal);
        Assert.Contains($"Base commit: {baseline.Head}", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Mode: run baseline", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Target: current working tree, including untracked files", session.PromptText,
                        StringComparison.Ordinal);
        Assert.Contains("Excluded paths: every CONTEXT.md and docs/adr/** at any depth", session.PromptText,
                        StringComparison.Ordinal);
        Assert.Contains("committed during the run", session.PromptText, StringComparison.Ordinal);
        Assert.EndsWith($"Review window: run baseline {baseline.Head[..12]} to working tree.",
                        critique.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scope_contract_is_part_of_the_critic_system_prompt()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Contains("judging a diff against the approved plan", session.Role.SystemPrompt,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documentation_only_tree_is_reported_as_nothing_to_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        Assert.Equal("approve", critique.Verdict);
        Assert.StartsWith("nothing to review", critique.Summary, StringComparison.Ordinal);
        Assert.Contains("Review window: run baseline", critique.Summary, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
        Assert.Equal(0, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task Review_rounds_continue_the_plan_review_numbering()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "fix it"));
        var run = NewRun(reviewRounds: 5);

        await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        // Flow keeps the phase-local round label; ledger IDs carry identity across both phases.
        Assert.Contains("## Code review — round 1", File.ReadAllText(run.FlowLogPath));
    }

    [Fact]
    public async Task Review_refuses_beyond_the_cap()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun(codeReviewRounds: 3);

        var rejection = await Assert.ThrowsAsync<CodeReviewCapReachedException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.Contains("userGrantedRound", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
    }

    /// <summary>
    /// The counters are only half of what a grant has to prove: "exactly one higher" on both
    /// <c>CodeReviewRounds</c> and <c>CodeReviewRoundCap</c> is what stops a single grant from
    /// raising the cap twice, and the flow-log entry is the half the counters do not cover — a
    /// reader has to see the round as bought, not budgeted.
    /// </summary>
    [Fact]
    public async Task A_granted_round_runs_past_the_cap_and_raises_it_by_one()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun(codeReviewRounds: 3);

        await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), true, ct);

        var state = run.ReadState();
        Assert.Equal(4, state.CodeReviewRounds);
        Assert.Equal(4, state.CodeReviewRoundCap);
        Assert.Equal(1, state.GrantedCodeReviewRounds);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Code review — extra round granted", flow, StringComparison.Ordinal);
        Assert.True(flow.IndexOf("## Code review — extra round granted", StringComparison.Ordinal)
                    < flow.IndexOf("## Code review — round 4", StringComparison.Ordinal),
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
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun(codeReviewRounds: 3);
        var act = NewReview(critic);

        await act.ReviewAsync(run, new Selection("critic-model", null), true, ct);

        var rejection = await Assert.ThrowsAsync<CodeReviewCapReachedException>(() =>
            act.ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.Contains("cap is 4", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant is written after the critique returns, and this is the test that makes that
    /// ordering a property rather than only a comment: a builder who moved <c>WriteState</c> ahead
    /// of the critique would spend the grant on a round that never happened.
    /// </summary>
    [Fact]
    public async Task A_granted_rounds_dying_critique_spends_nothing()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new InvalidOperationException("vendor died mid-turn"));
        var run = NewRun(codeReviewRounds: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), true, ct));

        var state = run.ReadState();
        Assert.Equal(3, state.CodeReviewRounds);
        Assert.Equal(3, state.CodeReviewRoundCap);
        Assert.Equal(0, state.GrantedCodeReviewRounds);
    }

    /// <summary>
    /// Code review shares the cap with plan review, so it shares the rule too: falling findings with
    /// no blocker run the round past the cap without a grant, and leave the cap where it was.
    /// </summary>
    [Fact]
    public async Task At_the_cap_a_round_runs_without_a_grant_while_the_findings_fall()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun(codeReviewRounds: 3, codeReviewTallies: [new(5, false), new(2, false)]);
        var act = NewReview(critic);

        await act.ReviewAsync(run, new Selection("critic-model", null), false, ct);

        var state = run.ReadState();
        Assert.Equal(4, state.CodeReviewRounds);
        Assert.Equal(3, state.CodeReviewRoundCap);
        Assert.Equal(0, state.GrantedCodeReviewRounds);
        Assert.Equal(1, state.AutoContinuedCodeReviewRounds);
        Assert.Equal(new ReviewTally(0, false), state.CodeReviewTallies![^1]);
        Assert.Equal("5 → 2", act.ContinuedAutomatically);
        Assert.Contains("## Code review — round continued automatically", File.ReadAllText(run.FlowLogPath),
                        StringComparison.Ordinal);
    }

    /// <summary>Findings that did not fall send the cap question to the user, exactly as before rounds were tallied.</summary>
    [Fact]
    public async Task At_the_cap_a_round_whose_findings_did_not_fall_is_refused()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun(codeReviewRounds: 3, codeReviewTallies: [new(5, false), new(15, false)]);

        await Assert.ThrowsAsync<CodeReviewCapReachedException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.Empty(critic.Sessions);
        Assert.Equal(3, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task Review_requires_an_approved_plan()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun(approved: false);

        await Assert.ThrowsAsync<NotApprovedException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task Sensitive_appsettings_under_an_adr_does_not_abort_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "docs/adr/appsettings.Production.json");
        await WriteFileAsync("docs/adr/appsettings.Production.json", "secret-looking change\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct);

        // The name is sensitive, but the pathspec keeps the file's contents out of everything sent
        // to a vendor, so there is nothing to leak and nothing to refuse.
        Assert.Equal("approve", critique.Verdict);
        var session = Assert.Single(critic.Sessions);
        Assert.Contains("tracked.txt", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json", session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitive_appsettings_at_an_unexcluded_path_aborts_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "config/appsettings.Production.json");
        await WriteFileAsync("config/appsettings.Production.json", "secret-looking change\n", ct);

        var critic = new RecordingVendor("claude");

        await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task A_sensitive_path_only_in_committed_work_aborts_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "config/appsettings.Production.json");
        await WriteFileAsync("config/appsettings.Production.json", "committed secret-looking change\n", ct);
        await _git.OutputAsync(["add", "--", "config/appsettings.Production.json"], ct);
        await _git.OutputAsync(["commit", "-qm", "sensitive run work"], ct);

        var critic = new RecordingVendor("claude");

        var error = await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct));

        Assert.Contains("diff touches config/appsettings.Production.json", error.Message, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task Sensitive_paths_are_guarded_before_an_empty_diff_is_accepted()
    {
        var critic = new RecordingVendor("claude");
        var git = new ReviewGit(["config/password.txt"], string.Empty);

        await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic, git).ReviewAsync(NewRun(), new Selection("critic-model", null), false, CancellationToken.None));

        Assert.Empty(critic.Sessions);
    }

    /// <summary>
    /// A generated library model is named for the library, and the one for
    /// <c>Microsoft.Extensions.Configuration.UserSecrets</c> refused every round that touched it
    /// although it holds no secret: issue #200. A JSON file named only by a keyword is read instead.
    /// </summary>
    [Fact]
    public async Task A_json_file_named_for_a_secret_is_read_and_reaches_the_critic()
    {
        const string path = "models/Microsoft.Extensions.Configuration.UserSecrets.json";
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, path);
        await WriteFileAsync(path, GeneratedModel, ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct);

        Assert.Equal("approve", critique.Verdict);
        var session = Assert.Single(critic.Sessions);
        Assert.Contains(path, session.PromptText, StringComparison.Ordinal);
        Assert.Contains("argument-member-unseen", session.PromptText, StringComparison.Ordinal);
    }

    /// <summary>
    /// What reading the file is for: a secret on a line with nothing beside it, which the content
    /// rule cannot see because no name says it is one. The refusal locates the line and says how to
    /// go on, and never names the value.
    /// </summary>
    [Fact]
    public async Task A_secret_standing_alone_in_a_json_file_named_for_one_aborts_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "config/tokens.json");
        await WriteFileAsync("config/tokens.json", "[\n  \"sk-Lq83Hd0PzX7vNm41RbTuKcWy\"\n]\n", ct);

        var critic = new RecordingVendor("claude");

        var error = await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct));

        Assert.Contains("diff touches config/tokens.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("`excludePaths`", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-Lq83", error.Message, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
    }

    /// <summary>
    /// A name that stays refused tells the orchestrator the one way past it, and that the user decides:
    /// the pathspec takes the file out of the critic's view, so the round runs without it.
    /// </summary>
    [Fact]
    public async Task A_refusal_by_path_says_how_to_let_the_path_through()
    {
        var critic = new RecordingVendor("claude");
        var git = new ReviewGit(["config/password.txt"], string.Empty);

        var error = await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic, git).ReviewAsync(NewRun(), new Selection("critic-model", null), false, CancellationToken.None));

        Assert.Contains("diff touches config/password.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("ask the user", error.Message, StringComparison.Ordinal);
        Assert.Contains("`excludePaths`", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_head_fallback_is_approved_without_spending_a_round_and_warns_about_its_limit()
    {
        var baseline = new string('a', 40);
        var head = new string('b', 40);
        var critic = new RecordingVendor("claude");
        var git = new ReviewGit([], string.Empty, isFallback: true, baseline, head);
        var run = NewRun(baselineHead: baseline);

        var critique = await NewReview(critic, git).ReviewAsync(
            run, new Selection("critic-model", null), false, CancellationToken.None);

        Assert.Equal("approve", critique.Verdict);
        Assert.Contains("HEAD fallback bbbbbbbbbbbb", critique.Summary, StringComparison.Ordinal);
        Assert.Contains("run baseline aaaaaaaaaaaa is not an ancestor", critique.Summary, StringComparison.Ordinal);
        Assert.Contains("committed run work may be outside this window", critique.Summary, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
        Assert.Equal(0, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task A_head_fallback_is_explained_in_the_critic_prompt()
    {
        var baseline = new string('a', 40);
        var head = new string('b', 40);
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "fallback diff is sound"));
        var git = new ReviewGit(["tracked.txt"], "diff", isFallback: true, baseline, head);

        var critique = await NewReview(critic, git).ReviewAsync(
            NewRun(baselineHead: baseline), new Selection("critic-model", null), false, CancellationToken.None);

        var prompt = Assert.Single(critic.Sessions).PromptText;
        Assert.Contains("Mode: HEAD fallback", prompt, StringComparison.Ordinal);
        Assert.Contains($"Base commit: {head}", prompt, StringComparison.Ordinal);
        Assert.Contains($"Run baseline: {baseline}", prompt, StringComparison.Ordinal);
        Assert.Contains("committed run work may be outside this window", prompt, StringComparison.Ordinal);
        Assert.StartsWith("fallback diff is sound", critique.Summary, StringComparison.Ordinal);
        Assert.Contains("HEAD fallback bbbbbbbbbbbb", critique.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A decision taken mid-build reaches the code-review critic the same way it reaches the plan's:
    /// after the ledger projection, before the instruction blocks. See docs/adr/0045.
    /// </summary>
    [Fact]
    public async Task The_critic_is_shown_the_run_s_decisions_after_the_ledger_projection()
    {
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "diff is sound"));
        var run = NewRun();
        run.WriteState(run.ReadState() with { BuilderInstructions = "keep it simple" });
        DecisionRecords.Record(run, DecisionRecords.Validate("build", "May an empty wider list change the engine?", null,
                                                             "Yes: an empty list never means the engine may not change",
                                                             "user", "task 4", null));

        await NewReview(critic, new ReviewGit(["tracked.txt"], "diff")).ReviewAsync(run, new Selection("critic-model", null), false,
                                                                                     CancellationToken.None);

        var prompt = Assert.Single(critic.Sessions).PromptText;
        var projection = prompt.IndexOf("# Decision ledger projection", StringComparison.Ordinal);
        var decisions = prompt.IndexOf("# Decisions", StringComparison.Ordinal);
        var builder = prompt.IndexOf("# Instructions the user gave the builder for this run", StringComparison.Ordinal);
        Assert.True(projection >= 0 && projection < decisions && decisions < builder, prompt);
        Assert.Contains("- D-0001 (user): May an empty wider list change the engine? → Yes: an empty list never means the engine may not change",
                        prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_critique_lands_in_the_flow_log_numbered_by_its_own_loop()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [new Finding("minor", "tracked.txt", "sloppy change")], "one nit"));
        var run = NewRun(reviewRounds: 3);

        await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        // Flow is act-labelled; its code-review rounds start at 1 while the ledger owns finding IDs.
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Code review — round 1", flow, StringComparison.Ordinal);
        Assert.Contains("sloppy change", flow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_review_critic_gets_the_default_worker_tools_and_a_left_task_is_noted()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"), killedBackgroundTasks: ["dotnet test"]);
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        Assert.Equal("approve", critique.Verdict);
        var role = Assert.Single(critic.Sessions).Role;
        Assert.Equal(["roslyn-*"], role.WorkerTools);
        Assert.Equal("code_review", role.Telemetry?.Act);
        Assert.Equal(1, role.Telemetry?.Round);
        Assert.Contains("`dotnet test`", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal codex gave run 20261004-093111-b79938 only after it had started, now given before
    /// any critic starts, and only once every file is given by path: nothing spent, a granted round
    /// still granted, no diffs left behind, and the sizes the orchestrator shows the user.
    /// </summary>
    [Fact]
    public async Task A_prompt_over_the_vendors_limit_with_every_file_by_path_is_refused_and_spends_nothing()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", string.Concat(Enumerable.Repeat("tracked line\n", 40)), ct);
        await WriteFileAsync("src/New.cs", string.Concat(Enumerable.Repeat("// new line\n", 80)), ct);

        var critic = new RecordingVendor("codex") { PromptCharacterLimit = 100 };
        var run = NewRun(codeReviewRounds: 3);

        var error = await Assert.ThrowsAsync<ReviewPromptTooLargeException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), true, ct));

        Assert.Empty(critic.Sessions);
        var state = run.ReadState();
        Assert.Equal(3, state.CodeReviewRounds);
        Assert.Equal(3, state.CodeReviewRoundCap);
        Assert.Equal(0, state.GrantedCodeReviewRounds);
        Assert.False(Directory.Exists(run.ReviewDiffsPath(4)));
        Assert.Contains("with every changed file given to the critic by path, and codex accepts at most 100", error.Message,
                        StringComparison.Ordinal);
        Assert.Contains("the user's grant for it still stands", error.Message, StringComparison.Ordinal);
        Assert.Contains("- approved plan: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("- the list of 2 changed files, and everything else: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("`excludePaths`", error.Message, StringComparison.Ordinal);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Code review — round 4 not sent", flow, StringComparison.Ordinal);
        Assert.Contains("codex accepts at most 100", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run 20261017-180043-675224's case: the untracked files alone take the prompt under the limit,
    /// so they go by path, the tracked diff stays embedded and nothing is written. See docs/adr/0034.
    /// </summary>
    [Fact]
    public async Task Over_the_limit_the_untracked_files_go_by_path_first()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);
        await WriteFileAsync("src/New.cs", string.Concat(Enumerable.Repeat("// new line\n", 80)), ct);
        var embedded = await EmbeddedSizeAsync(ct);

        var critic = new RecordingVendor("codex") { PromptCharacterLimit = embedded - 1 };
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        var prompt = Assert.Single(critic.Sessions).PromptText;
        Assert.Contains("ordinary change", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("// new line", prompt, StringComparison.Ordinal);
        Assert.Contains("# Untracked files to read", prompt, StringComparison.Ordinal);
        Assert.Contains("- `src/New.cs` (80 lines)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("# Changed files to read", prompt, StringComparison.Ordinal);
        Assert.False(Directory.Exists(run.ReviewDiffsPath(1)));
        Assert.Contains($"Embedded, the window was {embedded.ToString("N0", CultureInfo.InvariantCulture)} characters",
                        critique.Summary, StringComparison.Ordinal);
        Assert.Contains("so the critic was given by path 1 untracked files.", critique.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the untracked files are not enough, each tracked file's diff is written to the run folder
    /// for the critic to read while it runs, listed with its path, and deleted when the round ends.
    /// </summary>
    [Fact]
    public async Task Over_the_limit_without_untracked_files_the_tracked_diffs_are_written_for_the_round()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", string.Concat(Enumerable.Repeat("tracked line\n", 200)), ct);
        await WriteFileAsync("src/New.cs", "one new line\n", ct);
        var embedded = await EmbeddedSizeAsync(ct);

        var run = NewRun();
        var diffPath = run.ReviewDiffPath(1, "tracked.txt");
        string? diffDuringRound = null;
        var critic = new RecordingVendor("codex")
        {
            PromptCharacterLimit = embedded - 1,
            OnRun = _ => diffDuringRound = File.Exists(diffPath) ? File.ReadAllText(diffPath) : null
        };
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct);

        var prompt = Assert.Single(critic.Sessions).PromptText;
        Assert.DoesNotContain("tracked line", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("# Diff under review", prompt, StringComparison.Ordinal);
        Assert.Contains("# Changed files to read", prompt, StringComparison.Ordinal);
        Assert.Contains($"- `tracked.txt` (+200 −1): `{diffPath}`", prompt, StringComparison.Ordinal);
        Assert.Contains("- `src/New.cs` (1 lines)", prompt, StringComparison.Ordinal);
        Assert.NotNull(diffDuringRound);
        Assert.StartsWith("diff --git", diffDuringRound, StringComparison.Ordinal);
        Assert.Contains("+tracked line", diffDuringRound, StringComparison.Ordinal);
        Assert.False(Directory.Exists(run.ReviewDiffsPath(1)));
        Assert.Contains("so the critic was given by path 1 untracked files and the diffs of 1 tracked files.",
                        critique.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tracked_diffs_are_deleted_when_the_critic_fails()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", string.Concat(Enumerable.Repeat("tracked line\n", 200)), ct);
        var embedded = await EmbeddedSizeAsync(ct);

        var run = NewRun();
        var written = false;
        var critic = new RecordingVendor("codex")
        {
            PromptCharacterLimit = embedded - 1,
            OnRun = _ => written = File.Exists(run.ReviewDiffPath(1, "tracked.txt"))
        };
        critic.Enqueue(new InvalidOperationException("the turn died"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.True(written);
        Assert.False(Directory.Exists(run.ReviewDiffsPath(1)));
    }

    [Theory]
    [InlineData("../state.json")]
    [InlineData("../../outside")]
    public void A_diff_path_outside_the_rounds_folder_is_refused(string filePath)
    {
        var run = NewRun();

        Assert.Throws<InvalidOperationException>(() => run.ReviewDiffPath(1, filePath));
    }

    /// <summary>
    /// Codex counts Rust <c>chars()</c>, so the limit is in Unicode scalar values: a prompt exactly at
    /// it is sent embedded, one over is not, and an emoji is one character, where a UTF-16 length
    /// would have sent the prompt at the limit by path.
    /// </summary>
    [Fact]
    public async Task The_limit_counts_characters_as_codex_does_and_embeds_a_prompt_exactly_at_it()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", string.Concat(Enumerable.Repeat("emoji \U0001F600\n", 200)), ct);

        var measuring = new RecordingVendor("codex");
        measuring.Enqueue(new Critique("approve", [], "looks good"));
        await NewReview(measuring).ReviewAsync(NewRun(runId: "measure"), new Selection("critic-model", null), false, ct);
        var prompt = Assert.Single(measuring.Sessions).PromptText;
        var characters = prompt.EnumerateRunes().Count();
        Assert.True(prompt.Length > characters);

        var atLimit = new RecordingVendor("codex") { PromptCharacterLimit = characters };
        atLimit.Enqueue(new Critique("approve", [], "looks good"));
        await NewReview(atLimit).ReviewAsync(NewRun(runId: "at-limit"), new Selection("critic-model", null), false, ct);
        Assert.Equal(prompt, Assert.Single(atLimit.Sessions).PromptText);

        var overLimit = new RecordingVendor("codex") { PromptCharacterLimit = characters - 1 };
        overLimit.Enqueue(new Critique("approve", [], "looks good"));
        var critique = await NewReview(overLimit).ReviewAsync(NewRun(runId: "over-limit"), new Selection("critic-model", null), false, ct);
        Assert.Contains("# Changed files to read", Assert.Single(overLimit.Sessions).PromptText, StringComparison.Ordinal);
        Assert.Contains($"Embedded, the window was {characters.ToString("N0", CultureInfo.InvariantCulture)} characters",
                        critique.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A round's exclusions go into the one pathspec the content diff and the sensitive-path guard
    /// share, so an excluded file is neither sent nor refused, and the critic is told it is out.
    /// </summary>
    [Fact]
    public async Task An_excluded_path_leaves_the_window_and_the_critic_is_told_so()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);
        await WriteFileAsync("build/gate.ps1", "gate script body\n", ct);
        await WriteFileAsync("config/appsettings.Production.json", "secret-looking change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), false, ct,
                                                           ReviewScope.From(["build/*.ps1", "config"]));

        var prompt = Assert.Single(critic.Sessions).PromptText;
        Assert.Contains("ordinary change", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("gate script body", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json", prompt, StringComparison.Ordinal);
        Assert.Contains("Also excluded from this round by the orchestrator: `build/*.ps1`, `config`.", prompt,
                        StringComparison.Ordinal);
        Assert.Contains("Excluded from this round: `build/*.ps1`, `config`.", critique.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The critic reads a file it is pointed to with its own tools, which sends the contents to the
    /// vendor all the same, so the secret guard reads them though the prompt would not carry them.
    /// </summary>
    [Fact]
    public async Task A_secret_in_a_file_the_critic_would_be_given_by_path_is_still_refused()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);
        await WriteFileAsync("src/Settings.cs", "SECRET=9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c\n", ct);

        var critic = new RecordingVendor("codex") { PromptCharacterLimit = 100 };
        var run = NewRun();

        var error = await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), false, ct));

        Assert.Contains("src/Settings.cs", error.Message, StringComparison.Ordinal);
        Assert.Empty(critic.Sessions);
        Assert.False(Directory.Exists(run.ReviewDiffsPath(1)));
    }

    [Theory]
    [InlineData(":(top)src")]
    [InlineData(":!src")]
    [InlineData(" ")]
    public void An_excluded_path_that_is_blank_or_pathspec_magic_is_refused(string path)
    {
        Assert.Throws<ArgumentRejectedException>(() => ReviewScope.From([path]));
    }

    /// <summary>The prompt's size with every file embedded, measured by a critic that states no limit.</summary>
    /// <param name="ct">Cancels the round.</param>
    private async Task<int> EmbeddedSizeAsync(CancellationToken ct)
    {
        var measuring = new RecordingVendor("codex");
        measuring.Enqueue(new Critique("approve", [], "looks good"));
        await NewReview(measuring).ReviewAsync(NewRun(runId: "measure"), new Selection("critic-model", null), false, ct);
        return Assert.Single(measuring.Sessions).PromptText.EnumerateRunes().Count();
    }

    private CodeReview NewReview(RecordingVendor critic, IReviewGit? reviewGit = null) =>
        new(critic, new PromptLibrary(RepositoryPrompts()), reviewGit ?? _git);

    private RunDirectory NewRun(bool approved = true,
                                int reviewRounds = 0,
                                int codeReviewRounds = 0,
                                string? baselineHead = null,
                                string runId = "review",
                                IReadOnlyList<ReviewTally>? codeReviewTallies = null)
    {
        var run = RunDirectory.Create(_repo, runId);
        run.WriteState(new RunState(runId, _repo, "Text", DateTimeOffset.Now, reviewRounds, 5,
                                    BaselineHead: baselineHead ?? _baselineHead, Approved: approved,
                                    CodeReviewRounds: codeReviewRounds, CodeReviewRoundCap: 3,
                                    CodeReviewTallies: codeReviewTallies));
        run.WritePlan(Plan);
        return run;
    }

    private async Task InitialCommitAsync(CancellationToken ct, params string[] additionalPaths)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await WriteFileAsync("tracked.txt", "original\n", ct);
        foreach (var path in additionalPaths)
            await WriteFileAsync(path, "original\n", ct);

        await _git.OutputAsync(["add", "--", "tracked.txt", .. additionalPaths], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);
        _baselineHead = (await _git.OutputAsync(["rev-parse", "HEAD"], ct)).Trim();
    }

    private async Task WriteFileAsync(string relativePath, string contents, CancellationToken ct)
    {
        var path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, ct);
    }

    private sealed class ReviewGit(IReadOnlyList<string> changedPaths,
                                   string diff,
                                   bool isFallback = false,
                                   string? baseline = null,
                                   string? baseHead = null) : IReviewGit
    {
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, IReadOnlyList<string> excludedPaths,
                                                        CancellationToken ct) =>
            Task.FromResult(new ReviewWindow(baseline ?? baselineHead,
                                             baseHead ?? baselineHead,
                                             isFallback,
                                             changedPaths,
                                             diff.Length == 0 ? [] : [new ReviewFile(changedPaths[0], diff, Untracked: false)]));
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
}
