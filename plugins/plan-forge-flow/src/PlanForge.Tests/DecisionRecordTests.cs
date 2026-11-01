using System.Text.Json.Nodes;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The Run's decisions as <c>forge.decision.record</c> keeps them, and what reads them back: the
/// status, the documents of every result, and the Decisions section a review Critic is shown.
/// See docs/adr/0045.
/// </summary>
public sealed class DecisionRecordTests : IDisposable
{
    private const string SECRET = "aws_secret_key = AKIAIOSFODNN7EXAMPLE";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_decision_is_numbered_by_the_server_and_lands_in_the_file_and_the_flow_log()
    {
        var run = NewRun();

        var first = await Record(run, question: "Is generation by dependencies\nin scope for this run?", answer: "Deferred",
                                 options: [new DecisionOption("In scope"), new DecisionOption("Deferred", Recommended: true)],
                                 bearsOn: "R3, F-0012", phase: "plan_revision");
        var second = await Record(run, question: "Which store holds the cache?", answer: "SQLite", by: "orchestrator",
                                  phase: "planning");

        Assert.Equal(("D-0001", "recorded", 1), Outcome(first));
        Assert.Equal(("D-0002", "recorded", 2), Outcome(second));
        var records = run.ReadDecisions();
        Assert.Equal(["D-0001", "D-0002"], records.Select(record => record.DecisionId));
        Assert.Equal("Is generation by dependencies\nin scope for this run?", records[0].Question);
        Assert.Equal([("In scope", false), ("Deferred", true)], records[0].Options!.Select(option => (option.Label, option.Recommended)));
        Assert.Null(records[1].Options);
        Assert.Null(records[1].BearsOn);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Decision D-0001", flow, StringComparison.Ordinal);
        Assert.Contains("Phase: plan_revision", flow, StringComparison.Ordinal);
        Assert.Contains("Bears on: R3, F-0012", flow, StringComparison.Ordinal);
        Assert.Contains("Question: Is generation by dependencies\nin scope for this run?", flow.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("- Deferred (recommended)", flow, StringComparison.Ordinal);
        Assert.Contains("Answer: Deferred", flow, StringComparison.Ordinal);
        Assert.Contains("## Decision D-0002", flow, StringComparison.Ordinal);
        Assert.Contains("By: orchestrator", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retried call, after an answer the host lost, must not record the decision twice: the record
    /// is recognised by what it says, as a decision batch's retry is by its digest.
    /// </summary>
    [Fact]
    public async Task The_same_content_again_is_a_no_op_that_answers_with_the_id_it_has()
    {
        var run = NewRun();
        await Record(run, answer: "Deferred", options: [new DecisionOption("Deferred", true), new DecisionOption("In scope")]);
        var file = File.ReadAllText(run.DecisionsPath);
        var flow = File.ReadAllText(run.FlowLogPath);

        var replay = await Record(run, answer: "Deferred", options: [new DecisionOption("Deferred", true), new DecisionOption("In scope")]);

        Assert.Equal(("D-0001", "no_op", 1), Outcome(replay));
        Assert.Equal(file, File.ReadAllText(run.DecisionsPath));
        Assert.Equal(flow, File.ReadAllText(run.FlowLogPath));
        Assert.Equal(("D-0002", "recorded", 2), Outcome(await Record(run, answer: "In scope")));
        Assert.Equal(("D-0003", "recorded", 3),
                     Outcome(await Record(run, answer: "Deferred", options: [new DecisionOption("Deferred"), new DecisionOption("In scope")])));
    }

    [Theory]
    [InlineData("phase-done", "phase must be exactly one of interview, planning, plan_revision, approval, build, code_review, wrap_up (case-sensitive); 'done' is not")]
    [InlineData("phase-case", "'Interview' is not")]
    [InlineData("by-case", "by must be exactly user or orchestrator (case-sensitive); 'User' is not")]
    [InlineData("question-blank", "question must be non-blank and at most 4,000 characters")]
    [InlineData("question-long", "question must be non-blank and at most 4,000 characters")]
    [InlineData("answer-blank", "answer must be non-blank and at most 8,000 characters")]
    [InlineData("answer-long", "answer must be non-blank and at most 8,000 characters")]
    [InlineData("option-null", "options[0].label must be non-blank and at most 500 characters")]
    [InlineData("option-blank", "options[1].label must be non-blank")]
    [InlineData("option-twice", "options must not offer the same label twice")]
    [InlineData("two-recommended", "at most one option is recommended")]
    [InlineData("too-many-options", "options holds 21 choices; at most 20 are recorded")]
    [InlineData("bears-on-long", "bearsOn must be at most 1,000 characters")]
    [InlineData("supersedes-short", "supersedes must be a decision ID such as D-0001; 'D-1' is not")]
    [InlineData("supersedes-finding", "'F-0001' is not")]
    [InlineData("scope-case", "scope must be exactly widens or narrows (case-sensitive) when given; 'Widens' is not")]
    [InlineData("scope-other", "'broadens' is not")]
    public async Task A_malformed_decision_is_refused_before_anything_is_written(string kind, string reason)
    {
        var run = NewRun();
        Func<Task> call = kind switch
        {
            "phase-done" => () => Record(run, phase: "done"),
            "phase-case" => () => Record(run, phase: "Interview"),
            "by-case" => () => Record(run, by: "User"),
            "question-blank" => () => Record(run, question: " \n"),
            "question-long" => () => Record(run, question: new string('q', 4_001)),
            "answer-blank" => () => Record(run, answer: ""),
            "answer-long" => () => Record(run, answer: new string('a', 8_001)),
            "option-null" => () => Record(run, options: [null!]),
            "option-blank" => () => Record(run, options: [new DecisionOption("Yes"), new DecisionOption(" ")]),
            "option-twice" => () => Record(run, options: [new DecisionOption("Yes"), new DecisionOption("Yes", true)]),
            "two-recommended" => () => Record(run, options: [new DecisionOption("Yes", true), new DecisionOption("No", true)]),
            "too-many-options" => () => Record(run, options: [.. Enumerable.Range(1, 21).Select(n => new DecisionOption($"Option {n}"))]),
            "bears-on-long" => () => Record(run, bearsOn: new string('R', 1_001)),
            "supersedes-short" => () => Record(run, supersedes: "D-1"),
            "supersedes-finding" => () => Record(run, supersedes: "F-0001"),
            "scope-case" => () => Record(run, scope: "Widens"),
            "scope-other" => () => Record(run, scope: "broadens"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(call);

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(run.DecisionsPath));
        Assert.False(File.Exists(run.FlowLogPath));
    }

    /// <summary>
    /// Every field reaches every later Critic, so a secret is refused where it was typed — and before
    /// the call is logged, since the run log records the question and the answer in full.
    /// </summary>
    [Theory]
    [InlineData("question")]
    [InlineData("answer")]
    [InlineData("option")]
    [InlineData("bearsOn")]
    public async Task A_secret_in_any_field_is_refused_and_reaches_no_run_file(string field)
    {
        var run = NewRun();

        await Assert.ThrowsAsync<SensitiveContentException>(() => field switch
        {
            "question" => Record(run, question: SECRET),
            "answer" => Record(run, answer: SECRET),
            "option" => Record(run, options: [new DecisionOption(SECRET)]),
            _ => Record(run, bearsOn: SECRET)
        });

        Assert.False(File.Exists(run.DecisionsPath));
        Assert.False(File.Exists(run.FlowLogPath));
        Assert.False(File.Exists(run.DiagnosticLogPath));
    }

    [Fact]
    public async Task A_changed_answer_supersedes_the_one_standing_and_both_stay_recorded()
    {
        var run = NewRun();
        await Record(run, answer: "Deferred");

        var changed = await Record(run, answer: "In scope after all", supersedes: "D-0001");

        Assert.Equal(("D-0002", "recorded", 2), Outcome(changed));
        Assert.Equal(["Deferred", "In scope after all"], run.ReadDecisions().Select(record => record.Answer));
        Assert.Contains("Supersedes: D-0001", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);

        var twice = await Assert.ThrowsAsync<ArgumentRejectedException>(() => Record(run, answer: "Deferred again", supersedes: "D-0001"));
        Assert.Equal("D-0001 is already superseded by D-0002: supersede that one instead", twice.Message);
        var unknown = await Assert.ThrowsAsync<ArgumentRejectedException>(() => Record(run, answer: "Later", supersedes: "D-0009"));
        Assert.Equal("supersedes D-0009 names no recorded decision; the last is D-0002", unknown.Message);
        Assert.Equal(2, run.ReadDecisions().Count);

        // A replay of the superseding record is recognised before its supersedes is checked again.
        Assert.Equal(("D-0002", "no_op", 2), Outcome(await Record(run, answer: "In scope after all", supersedes: "D-0001")));
    }

    [Fact]
    public async Task Superseding_with_nothing_recorded_says_so()
    {
        var run = NewRun();

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => Record(run, supersedes: "D-0001"));

        Assert.Equal("supersedes D-0001 names no recorded decision; none is recorded yet", error.Message);
        Assert.False(File.Exists(run.DecisionsPath));
    }

    [Theory]
    [InlineData("{}", "it is not a JSON array of decision records")]
    [InlineData("""[{"decisionId":"D-0001","phase":"interview","question":"q","by":"user"}]""", "it is not a JSON array of decision records")]
    [InlineData("""[{"decisionId":"D-0002","phase":"interview","question":"q","answer":"a","by":"user"}]""", "entry 1 is not D-0001")]
    [InlineData("""[{"decisionId":"D-0001","phase":"done","question":"q","answer":"a","by":"user"}]""", "D-0001 is not a well-formed record")]
    [InlineData("""[{"decisionId":"D-0001","phase":"interview","question":"q","answer":"a","by":"user","scope":"broadens"}]""", "D-0001 is not a well-formed record")]
    [InlineData("""[{"decisionId":"D-0001","phase":"interview","question":"q","answer":"a","by":"user","supersedes":"D-0001"}]""",
                "D-0001 supersedes D-0001, which is not an earlier record still standing")]
    public async Task A_hand_edited_file_stops_every_reader_and_is_left_as_it_is(string content, string reason)
    {
        var run = NewRun();
        File.WriteAllText(run.DecisionsPath, content);

        var error = await Assert.ThrowsAsync<DecisionRecordsException>(() => Record(run));

        Assert.StartsWith($"decisions.json is malformed: {reason}", error.Message, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(run.DecisionsPath));
        Assert.Throws<DecisionRecordsException>(() => run.ReadDecisions());
    }

    [Fact]
    public async Task Status_counts_the_decisions_and_shows_the_last_five_one_line_each()
    {
        var run = await NewGitRun();
        Assert.Equal((0, 0), Decisions(await Status(run)));
        for (var number = 1; number <= 6; number++)
            await Record(run, question: $"Question {number}?", answer: $"Answer {number}");
        await Record(run, question: "Long?", answer: new string('x', 400), supersedes: "D-0006");

        var status = await Status(run);

        Assert.Equal((7, 5), Decisions(status));
        var last = status["decisions"]!["last"]!.AsArray().Select(line => line!.GetValue<string>()).ToArray();
        Assert.Equal("D-0003 (user): Question 3? → Answer 3", last[0]);
        Assert.Equal("D-0006 (user): Question 6? → Answer 6", last[3]);
        Assert.StartsWith("D-0007 (user, supersedes D-0006): Long? → xxx", last[4], StringComparison.Ordinal);
        Assert.Equal(DecisionRecords.STATUS_LINE_LIMIT, last[4].Length);
        Assert.EndsWith("…", last[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_documents_name_the_decisions_once_the_first_is_recorded()
    {
        var run = NewRun();
        var ct = CancellationToken.None;

        var before = JsonNode.Parse(await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, "# Plan\n", ct))!;
        var recorded = await Record(run);
        var after = JsonNode.Parse(await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, "# Plan\n", ct))!;

        Assert.False(before["documents"]!.AsObject().ContainsKey("decisions"));
        Assert.Equal(run.DecisionsPath, recorded["documents"]!["decisions"]!["path"]!.GetValue<string>());
        Assert.Equal(run.DecisionsPath, after["documents"]!["decisions"]!["path"]!.GetValue<string>());
        Assert.Contains("forge.decision.record", after["documents"]!["decisions"]!["next"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_lists_the_current_decisions_oldest_first_and_names_what_each_superseded()
    {
        DecisionRecord[] records =
        [
            Decision(1, "Generation by dependencies?", "Deferred"),
            Decision(2, "Generation by dependencies?", "In scope", supersedes: "D-0001"),
            Decision(3, "Which store?", "SQLite\nwith WAL", by: "orchestrator")
        ];

        Assert.Equal(
            """
            # Decisions

            What this run settled, as the user or the orchestrator answered it, oldest first; an answer a later one superseded is left out.

            - D-0002 (user, supersedes D-0001): Generation by dependencies? → In scope
            - D-0003 (orchestrator): Which store? → SQLite with WAL

            """.ReplaceLineEndings(), DecisionRecords.Section(records));
        Assert.Equal("", DecisionRecords.Section([]));
    }

    /// <summary>
    /// The budget binds on the oldest: what the interview settled is in the plan's requirements
    /// already, and what was settled between rounds may be nowhere else the Critic reads.
    /// </summary>
    [Fact]
    public void Over_its_budget_the_section_leaves_the_oldest_out_and_names_them()
    {
        var records = Enumerable.Range(1, 40).Select(number => Decision(number, $"Question {number}?", new string('a', 1_000))).ToArray();

        var section = DecisionRecords.Section(records);

        var lines = section.Split(Environment.NewLine).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(20, lines.Length);
        Assert.StartsWith("- D-0021 (user): Question 21? → aaa", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("- D-0040 ", lines[^1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(DecisionRecords.SECTION_LINE_LIMIT, line.Length - 2));
        Assert.True(lines.Sum(line => line.Length - 2) <= DecisionRecords.SECTION_LIMIT);
        Assert.Contains("Left out to keep this section within 12,000 characters, the oldest: "
                        + string.Join(", ", Enumerable.Range(1, 20).Select(number => $"D-{number:0000}")) + ".",
                        section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_cut_short_never_splits_a_surrogate_pair()
    {
        var line = DecisionRecords.Line(Decision(1, "qq", string.Concat(Enumerable.Repeat("😀", 200))), 200);

        Assert.Equal(199, line.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(line[^2]));
    }

    /// <summary>
    /// A decision without a scope is written exactly as before the tag existed, so a run begun earlier
    /// reads back unchanged and a replay of an old record still finds its digest.
    /// </summary>
    [Fact]
    public async Task A_scope_tag_is_kept_in_the_file_and_a_record_without_one_is_written_as_before()
    {
        var run = NewRun();

        await Record(run, answer: "Plain");
        var plain = File.ReadAllText(run.DecisionsPath);
        await Record(run, question: "Add the second mechanism?", answer: "Yes", phase: "plan_revision", scope: "widens");
        await Record(run, question: "Add a model family?", answer: "No", phase: "plan_revision", scope: " ");

        Assert.Equal(
            """
            [
              {
                "decisionId": "D-0001",
                "phase": "interview",
                "question": "Is generation by dependencies in scope?",
                "answer": "Plain",
                "by": "user"
              }
            ]
            """.ReplaceLineEndings(), plain);
        var records = run.ReadDecisions();
        Assert.Equal([null, "widens", null], records.Select(record => record.Scope));
        Assert.Contains("\"scope\": \"widens\"", File.ReadAllText(run.DecisionsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void The_widenings_since_the_interview_count_the_standing_decisions_tagged_widens_after_it()
    {
        DecisionRecord[] records =
        [
            Decision(1, "Widen at the interview?", "Yes", phase: "interview", scope: "widens"),
            Decision(2, "Add the first mechanism?", "Yes", scope: "widens"),
            Decision(3, "Add a model family?", "Yes", phase: "build", scope: "widens"),
            Decision(4, "Add an input?", "Yes", scope: "widens"),
            Decision(5, "Add an input?", "No, keep it conservative", scope: "narrows", supersedes: "D-0004"),
            Decision(6, "Which store?", "SQLite")
        ];

        Assert.Equal(2, DecisionRecords.Widenings(records));
        Assert.Equal(0, DecisionRecords.Widenings([]));
    }

    [Fact]
    public async Task The_flow_entry_of_a_scoped_decision_shows_the_widenings_since_the_interview()
    {
        var run = NewRun();

        await Record(run, answer: "Interview widening", scope: "widens");
        await Record(run, question: "Add the first mechanism?", phase: "plan_revision", scope: "widens");
        await Record(run, question: "Add the second mechanism?", phase: "plan_revision", scope: "widens");
        await Record(run, question: "Narrow the acceptance?", phase: "plan_revision", scope: "narrows");
        await Record(run, question: "Which store?", answer: "SQLite", phase: "plan_revision");

        var scopes = File.ReadLines(run.FlowLogPath).Where(line => line.StartsWith("Scope:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["Scope: widens; widenings since the interview: 0",
                      "Scope: widens; widenings since the interview: 1",
                      "Scope: widens; widenings since the interview: 2",
                      "Scope: narrows; widenings since the interview: 2"], scopes);
    }

    [Fact]
    public async Task Status_shows_the_widenings_since_the_interview_only_once_there_is_one()
    {
        var run = await NewGitRun();
        await Record(run, phase: "plan_revision", answer: "Plain");
        Assert.False((await Status(run))["decisions"]!.AsObject().ContainsKey("widenings"));

        await Record(run, question: "Add the first mechanism?", phase: "plan_revision", scope: "widens");
        await Record(run, question: "Add the second mechanism?", phase: "build", scope: "widens");

        Assert.Equal(2, (await Status(run))["decisions"]!["widenings"]!.GetValue<int>());
    }

    private async Task<JsonNode> Record(RunDirectory run,
                                        string question = "Is generation by dependencies in scope?",
                                        string answer = "Deferred",
                                        string phase = "interview",
                                        string by = "user",
                                        DecisionOption[]? options = null,
                                        string? bearsOn = null,
                                        string? supersedes = null,
                                        string? scope = null) =>
        JsonNode.Parse(await ForgeTools.RecordDecision(SessionRoots.None, _workspace, run.RunId, phase, question, answer, by,
                                                       CancellationToken.None, options, bearsOn, supersedes, scope))!;

    private async Task<JsonNode> Status(RunDirectory run) =>
        JsonNode.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId, CancellationToken.None, _ => null))!;

    private static (string Id, string Outcome, int Count) Outcome(JsonNode result) =>
        (result["decisionId"]!.GetValue<string>(), result["outcome"]!.GetValue<string>(), result["count"]!.GetValue<int>());

    private static (int Count, int Shown) Decisions(JsonNode status) =>
        (status["decisions"]!["count"]!.GetValue<int>(), status["decisions"]!["last"]!.AsArray().Count);

    private static DecisionRecord Decision(int number, string question, string answer, string by = "user", string? supersedes = null,
                                           string phase = "plan_revision", string? scope = null) =>
        new($"D-{number:0000}", phase, question, null, answer, by, Supersedes: supersedes, Scope: scope);

    private RunDirectory NewRun()
    {
        var run = RunDirectory.Create(_workspace, "decisions");
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5, BaselineHead: "HEAD"));
        return run;
    }

    private async Task<RunDirectory> NewGitRun()
    {
        var ct = CancellationToken.None;
        Directory.CreateDirectory(_workspace);
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "tracked\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);
        return NewRun();
    }
}
