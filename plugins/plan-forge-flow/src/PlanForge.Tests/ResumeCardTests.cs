using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Orchestration;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ResumeCardTests : IDisposable
{
    private const string SESSION = "session-a";
    private const string PLAN_TEXT = "PLAN-TEXT-NEVER-PRINTED";
    private const string FINDING_TEXT = "FINDING-TEXT-NEVER-PRINTED";
    private const string INSTRUCTIONS = "INSTRUCTIONS-NEVER-PRINTED";
    private const string GATE_VALUE = "GATE-VALUE-NEVER-PRINTED";

    private static readonly string PLAN = $"""
        Builder: codex / gpt-x / high

        ## Requirements

        1. **R1.** {PLAN_TEXT}

        ## Approach

        1. **One.** First. **Gate:** `exit 0` (R1)
        2. **Two.** Second. **Gate:** `exit 0` (R1)
        3. **Three.** Third. **Gate:** `exit 0` (R1)
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Nothing_is_printed_without_a_run_of_this_session()
    {
        Assert.Equal("", ResumeCard.Of(_root, SESSION, null));

        NewRun("20261021-100000-other", approved: false, tasksCompleted: 0, session: "session-b");
        var done = NewRun("20261021-110000-done", approved: false, tasksCompleted: 0);
        File.WriteAllText(Path.Combine(done.Path, OrchestratorPhases.FILE_NAME), """{ "state": "none" }""");

        Assert.Equal("", ResumeCard.Of(_root, SESSION, null));
        Assert.Equal("", ResumeCard.Of(_root, null, null));
    }

    [Fact]
    public void A_plan_under_review_gets_its_hash_on_patch_and_confirm_and_no_plan_argument()
    {
        var run = NewRun("20261021-100000-review", approved: false, tasksCompleted: 0);
        var finding = run.ReadDecisionLedger().AddFinding(new Finding("major", "PLAN.md", FINDING_TEXT), LedgerPhase.PlanReview);
        Called(run, "forge.plan.review", ("vendor", "codex"), ("model", "critic-x"), ("effort", "xhigh"), ("fast", "false"));

        var card = ResumeCard.Of(_root, SESSION, null);

        var hash = PlanOutline.Hash(PLAN);
        Assert.Contains("Approved: no.", card, StringComparison.Ordinal);
        Assert.Contains($"forge.plan.patch {{ baseHash: \"{hash}\"", card, StringComparison.Ordinal);
        Assert.Contains($"forge.plan.confirm {{ approved: true, planHash: \"{hash}\"", card, StringComparison.Ordinal);
        Assert.Contains("forge.plan.review { model: \"critic-x\", effort: \"xhigh\", vendor: \"codex\", fast: false, revision:", card, StringComparison.Ordinal);
        Assert.Contains("\"addressedByRevision\"", card, StringComparison.Ordinal);
        Assert.Contains($"Unresolved: {finding.FindingId}.", card, StringComparison.Ordinal);
        Assert.Contains($"of which active in plan review: {finding.FindingId}.", card, StringComparison.Ordinal);
        Assert.Contains("read references/plan-review.md, references/ledger.md, references/approval.md", card, StringComparison.Ordinal);
        Clean(card);
    }

    [Fact]
    public void Rounds_run_past_the_cap_without_asking_are_counted_beside_the_granted_ones()
    {
        var run = NewRun("20261021-100000-rounds", approved: false, tasksCompleted: 0);
        run.WriteState(run.ReadState() with { ReviewRounds = 8, ReviewRoundCap = 6, GrantedReviewRounds = 1,
                                              AutoContinuedReviewRounds = 2 });

        var card = ResumeCard.Of(_root, SESSION, null);

        Assert.Contains("- Plan review: 8 rounds, cap 6 + 1 granted + 2 automatic; code review: ", card, StringComparison.Ordinal);
        Clean(card);
    }

    [Fact]
    public void A_build_in_progress_names_the_next_task_and_the_builder_call_as_last_passed()
    {
        var run = NewRun("20261021-100000-build", approved: true, tasksCompleted: 1);
        Called(run, "forge.build.next", ("vendor", "claude"), ("model", "old-builder"), ("fast", "false"));
        Called(run, "forge.work.start", ("act", "build.next"), ("vendor", "codex"), ("model", "gpt-x"), ("effort", "high"), ("fast", "true"));

        var card = ResumeCard.Of(_root, SESSION, null);

        Assert.Contains("Tasks built: 1 of 3.", card, StringComparison.Ordinal);
        Assert.Contains("build, task 2 of 3 is next.", card, StringComparison.Ordinal);
        Assert.Contains("forge.build.next { model: \"gpt-x\", effort: \"high\", vendor: \"codex\", fast: true }", card, StringComparison.Ordinal);
        Assert.Contains("phase: \"build\"", card, StringComparison.Ordinal);
        Assert.Contains("Critic: not used yet.", card, StringComparison.Ordinal);
        Assert.Contains("Gate environment holds: CD_SQL (names only).", card, StringComparison.Ordinal);
        Assert.Contains("references/build.md", card, StringComparison.Ordinal);
        Clean(card);
    }

    [Fact]
    public void Code_review_lists_the_findings_pending_full_verification_and_the_fix_shape()
    {
        var run = NewRun("20261021-100000-code", approved: true, tasksCompleted: 3);
        var ledger = run.ReadDecisionLedger();
        var pending = ledger.AddFinding(new Finding("major", "a.cs", FINDING_TEXT), LedgerPhase.CodeReview);
        var open = ledger.AddFinding(new Finding("minor", "b.cs", FINDING_TEXT), LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("fix-1", [pending.FindingId],
                                new BuildResult("done", [], new Verification("passed", "ok"), "done",
                                                new GateRun("passed", "gate", null, null, null, null, null)),
                                terminal: true, FixGatePolicy.Targeted);

        var card = ResumeCard.Of(_root, SESSION, null);

        Assert.Contains("every task is built", card, StringComparison.Ordinal);
        Assert.Contains($"Pending full host verification under fix attempt fix-1: {pending.FindingId}.", card, StringComparison.Ordinal);
        Assert.Contains($"of which active in code review: {pending.FindingId}, {open.FindingId}.", card, StringComparison.Ordinal);
        Assert.Contains("forge.review.code { model: \"<the critic's model>\"", card, StringComparison.Ordinal);
        Assert.Contains("\"hostVerified\"", card, StringComparison.Ordinal);
        Assert.Contains("raises?: [{ severity:", card, StringComparison.Ordinal);
        Assert.Contains("gate?: \"full\" | \"targeted\"", card, StringComparison.Ordinal);
        Assert.Contains("read references/code-review.md, references/ledger.md", card, StringComparison.Ordinal);
        Clean(card);
    }

    [Fact]
    public void A_declared_phase_the_run_has_moved_past_is_called_out()
    {
        NewRun("20261021-100000-lagging", approved: true, tasksCompleted: 1, phase: "planning");
        NewRun("20261021-100000-current", approved: true, tasksCompleted: 1, session: "session-b");

        Assert.Contains("Last declared phase: planning, opened 2026-10-21 10:00:00. The run has moved past it",
                        ResumeCard.Of(_root, SESSION, null), StringComparison.Ordinal);
        Assert.DoesNotContain("moved past", ResumeCard.Of(_root, "session-b", null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_run_of_this_session_is_the_one_shown()
    {
        NewRun("20261020-100000-older", approved: false, tasksCompleted: 0);
        NewRun("20261021-100000-newer", approved: false, tasksCompleted: 0);
        NewRun("20261022-100000-elsewhere", approved: false, tasksCompleted: 0, session: "session-b");

        var card = ResumeCard.Of(_root, SESSION, null);

        Assert.Contains("forge run 20261021-100000-newer.", card, StringComparison.Ordinal);
        Assert.DoesNotContain("20261020-100000-older", card, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_whose_state_cannot_be_read_still_sends_the_orchestrator_to_forge_status()
    {
        var run = NewRun("20261021-100000-broken", approved: false, tasksCompleted: 0);
        File.WriteAllText(Path.Combine(run.Path, "state.json"), """{ "fullGate": 7 }""");

        var card = ResumeCard.Of(_root, SESSION, null);

        Assert.Contains("could not be read for a resume card", card, StringComparison.Ordinal);
        Assert.Contains("Call forge.status for this run", card, StringComparison.Ordinal);
    }

    [Fact]
    public void The_entry_point_reads_the_session_and_the_skill_folder_from_its_environment()
    {
        NewRun("20261021-100000-entry", approved: true, tasksCompleted: 1);
        var plugin = RepositoryRoot();
        var environment = new Dictionary<string, string>
        {
            [HostSessions.CLAUDE_SESSION_VARIABLE] = SESSION,
            [PromptLibrary.RootVariable] = Path.Combine(plugin, "prompts")
        };
        var output = new StringWriter();

        Assert.Equal(0, ResumeCard.Run(["--resume-card", _root], TextReader.Null, output, name => environment.GetValueOrDefault(name)));

        Assert.Contains(Path.Combine(plugin, "skills", "forge", "references", "build.md"), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_hands_the_session_and_the_directory_on_stdin()
    {
        NewRun("20261021-100000-codex", approved: true, tasksCompleted: 1);
        var input = new JsonObject
        {
            ["session_id"] = SESSION, ["cwd"] = _root, ["hook_event_name"] = "SessionStart", ["source"] = "compact"
        }.ToJsonString();
        var output = new StringWriter();

        Assert.Equal(0, ResumeCard.Run(["--resume-card"], new StringReader(input), output, _ => null));

        Assert.Contains("forge run 20261021-100000-codex.", output.ToString(), StringComparison.Ordinal);
        Clean(output.ToString());
    }

    [Fact]
    public void The_entry_point_prints_nothing_without_a_run_and_refuses_bad_arguments()
    {
        var output = new StringWriter();

        Assert.True(ResumeCard.IsCommand(["--resume-card", _root]));
        Assert.Equal(0, ResumeCard.Run(["--resume-card", _root], TextReader.Null, output, _ => SESSION));
        Assert.Equal(0, ResumeCard.Run(["--resume-card"], new StringReader(new JsonObject { ["session_id"] = SESSION, ["cwd"] = _root }.ToJsonString()),
                                       output, _ => null));
        Assert.Equal("", output.ToString());
        Assert.Equal(2, ResumeCard.Run(["--resume-card", "relative"], TextReader.Null, output, _ => SESSION));
        Assert.Equal(2, ResumeCard.Run(["--resume-card"], TextReader.Null, output, _ => SESSION));
        Assert.Equal(2, ResumeCard.Run(["--resume-card"], new StringReader("""{"session_id":"s","cwd":"relative"}"""), output, _ => null));
        Assert.Equal(2, ResumeCard.Run(["--resume-card"], new StringReader("not json"), output, _ => null));
    }

    /// <summary>Asserts the card holds no run content, is ASCII, and fits the host's 10,000-character hook output.</summary>
    /// <param name="card">The printed card.</param>
    private static void Clean(string card)
    {
        foreach (var secret in new[] { PLAN_TEXT, FINDING_TEXT, INSTRUCTIONS, GATE_VALUE })
            Assert.DoesNotContain(secret, card, StringComparison.Ordinal);
        Assert.All(card, character => Assert.True(character < 128, $"non-ASCII U+{(int)character:X4} in the card"));
        Assert.InRange(card.Length, 1, 10_000);
    }

    private RunDirectory NewRun(string id, bool approved, int tasksCompleted, string session = SESSION, string? phase = null)
    {
        var run = RunDirectory.Create(_root, id);
        run.WritePlan(PLAN);
        run.WriteState(new RunState(id, _root, "Text", DateTimeOffset.Now, 2, 5,
                                    Approved: approved, TasksCompleted: tasksCompleted,
                                    GateEnvironment: new Dictionary<string, string> { ["CD_SQL"] = GATE_VALUE },
                                    CriticInstructions: INSTRUCTIONS, BuilderInstructions: INSTRUCTIONS,
                                    PendingGateFailure: GATE_VALUE));
        var open = new OrchestratorPhaseFile(OrchestratorPhases.OPEN, phase ?? (approved ? "build" : "plan_revision"),
                                             DateTimeOffset.UtcNow, "2026-10-21 10:00:00", HostSessionId: session);
        File.WriteAllText(Path.Combine(run.Path, OrchestratorPhases.FILE_NAME),
                          JsonSerializer.Serialize(open, OrchestratorPhaseJson.Default.OrchestratorPhaseFile));
        return run;
    }

    private static void Called(RunDirectory run, string tool, params (string Name, string? Value)[] arguments) =>
        run.Log.Write("info", "server", "tool.call", [("tool", tool), .. arguments]);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "skills", "forge", "SKILL.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("the plugin root was not found above the test binaries");
    }
}
