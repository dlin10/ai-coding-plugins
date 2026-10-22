using System.Text.Json;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Orchestrator phases and Orchestrator time (R1–R4, R10): <c>forge.begin</c> opens
/// <c>interview</c>, a <c>phase</c> declared through <c>forge.log.append</c> closes and opens, and
/// each close appends one <c>orchestrator.usage</c> record. Every owner and clock here is the test's
/// own, and no test reads the real environment.
/// </summary>
public sealed class OrchestratorPhaseTests : IDisposable
{
    private const string PLAN =
        """
        # Title

        ## Approach

        1. First task.
        2. Second task.
        3. Third task.
        """;

    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-phases", Guid.NewGuid().ToString("n"));
    private readonly TestClock _clock = new();

    public OrchestratorPhaseTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task An_unknown_phase_is_refused_listing_the_valid_values_and_changes_nothing() =>
        await AssertRefused("review");

    [Fact]
    public async Task An_empty_phase_is_refused() => await AssertRefused("");

    [Fact]
    public async Task A_whitespace_phase_is_refused() => await AssertRefused("   ");

    [Fact]
    public async Task A_phase_in_the_wrong_case_is_refused() => await AssertRefused("Planning");

    [Fact]
    public async Task Begin_opens_interview_at_the_call_arrival()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);

        var file = PhaseFile(runId);
        Assert.Equal("open", file.GetProperty("state").GetString());
        Assert.Equal("interview", file.GetProperty("phase").GetString());
        Assert.Equal(START, file.GetProperty("openedAt").GetDateTimeOffset());
        Assert.Equal("2026-10-14 09:00:00", file.GetProperty("openedAtLocal").GetString());
        Assert.False(File.Exists(TelemetryPath(runId)));
    }

    [Fact]
    public async Task A_declaration_closes_the_open_phase_and_opens_the_one_it_names()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        _clock.Advance(TimeSpan.FromSeconds(75));

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal("orchestrator.usage", record.GetProperty("event").GetString());
        Assert.Equal("interview", record.GetProperty("phase").GetString());
        Assert.Equal("2026-10-14 09:00:00", record.GetProperty("at").GetString());
        Assert.Equal("00:01:15", record.GetProperty("wallDuration").GetString());
        Assert.Equal("planning", PhaseFile(runId).GetProperty("phase").GetString());
        Assert.Equal(START.AddSeconds(75), PhaseFile(runId).GetProperty("openedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_record_keeps_the_property_order_of_the_requirement()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);

        await DeclareAsync(phases, runId, "planning");

        Assert.Equal(["at", "event", "phase", "host", "duration", "wallDuration", "toolDuration", "usageMissing"],
                     Single(Records(runId)).EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Done_closes_the_open_phase_and_leaves_the_explicit_no_phase_state()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);

        await DeclareAsync(phases, runId, "done");

        Assert.Equal("interview", Single(Records(runId)).GetProperty("phase").GetString());
        var file = PhaseFile(runId);
        Assert.Equal("none", file.GetProperty("state").GetString());
        Assert.False(file.TryGetProperty("phase", out _));
    }

    [Fact]
    public async Task Done_with_nothing_open_writes_its_note_and_no_record()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        await DeclareAsync(phases, runId, "done");

        await DeclareAsync(phases, runId, "done");

        Assert.Single(Records(runId));
        Assert.Equal(2, LogEntries(runId).Count(entry => entry.GetProperty("event").GetString() == "note"));
        Assert.Equal("none", PhaseFile(runId).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_declaration_writes_exactly_one_note()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);

        await DeclareAsync(phases, runId, "planning");

        Assert.Single(LogEntries(runId), entry => entry.GetProperty("event").GetString() == "note");
    }

    [Fact]
    public async Task Declaring_the_open_phase_again_records_it_twice()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        await DeclareAsync(phases, runId, "build");
        _clock.Advance(TimeSpan.FromSeconds(5));
        await DeclareAsync(phases, runId, "build");
        _clock.Advance(TimeSpan.FromSeconds(7));
        await DeclareAsync(phases, runId, "done");

        var records = Records(runId);
        Assert.Equal(["interview", "build", "build"], records.Select(record => record.GetProperty("phase").GetString()));
        Assert.Equal(["00:00:00", "00:00:05", "00:00:07"], records.Select(record => record.GetProperty("wallDuration").GetString()));
    }

    [Fact]
    public async Task A_phase_opened_by_one_owner_is_closed_by_a_fresh_one_with_only_its_tool_time()
    {
        var first = Phases();
        var runId = await BeginAsync(first);
        await ToolCallAsync(first, runId, TimeSpan.FromSeconds(20));

        var fresh = Phases();
        _clock.Advance(TimeSpan.FromSeconds(80));
        await ToolCallAsync(fresh, runId, TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(90));
        await DeclareAsync(fresh, runId, "planning");

        var record = Single(Records(runId));
        Assert.Equal("interview", record.GetProperty("phase").GetString());
        Assert.Equal("00:03:20", record.GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:10", record.GetProperty("toolDuration").GetString());
        Assert.Equal("00:03:10", record.GetProperty("duration").GetString());
    }

    [Fact]
    public async Task Plan_revision_carries_the_plan_review_rounds_run_so_far()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        UpdateState(runId, state => state with { ReviewRounds = 2 });

        await DeclareAsync(phases, runId, "plan_revision");
        await DeclareAsync(phases, runId, "done");

        var record = Records(runId)[1];
        Assert.Equal(2, record.GetProperty("round").GetInt32());
        Assert.False(record.TryGetProperty("taskNumber", out _));
    }

    [Fact]
    public async Task Code_review_carries_the_code_review_rounds_run_so_far()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        UpdateState(runId, state => state with { ReviewRounds = 4, CodeReviewRounds = 1 });

        await DeclareAsync(phases, runId, "code_review");
        await DeclareAsync(phases, runId, "done");

        Assert.Equal(1, Records(runId)[1].GetProperty("round").GetInt32());
    }

    [Fact]
    public async Task Build_carries_the_next_task_and_the_approved_plan_task_count()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        Run(runId).WritePlan(PLAN);
        UpdateState(runId, state => state with { Approved = true, TasksCompleted = 1 });

        await DeclareAsync(phases, runId, "build");
        await DeclareAsync(phases, runId, "done");

        var record = Records(runId)[1];
        Assert.Equal(2, record.GetProperty("taskNumber").GetInt32());
        Assert.Equal(3, record.GetProperty("taskCount").GetInt32());
        Assert.False(record.TryGetProperty("round", out _));
    }

    [Fact]
    public async Task Build_with_no_task_left_carries_no_position()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        Run(runId).WritePlan(PLAN);
        UpdateState(runId, state => state with { Approved = true, TasksCompleted = 3 });

        await DeclareAsync(phases, runId, "build");
        await DeclareAsync(phases, runId, "done");

        var record = Records(runId)[1];
        Assert.False(record.TryGetProperty("taskNumber", out _));
        Assert.False(record.TryGetProperty("taskCount", out _));
        Assert.DoesNotContain(Failures(runId), kind => kind == "position_unavailable");
    }

    [Fact]
    public async Task A_position_whose_state_cannot_be_read_is_omitted_and_the_declaration_proceeds()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        File.WriteAllText(Path.Combine(Run(runId).Path, "state.json"), "{ not json");

        await DeclareAsync(phases, runId, "plan_revision");
        await DeclareAsync(phases, runId, "done");

        var records = Records(runId);
        Assert.Equal(["interview", "plan_revision"], records.Select(record => record.GetProperty("phase").GetString()));
        Assert.False(records[1].TryGetProperty("round", out _));
        Assert.Equal(["position_unavailable"], Failures(runId));
    }

    [Fact]
    public async Task The_durations_add_up_after_truncation()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        _clock.Advance(TimeSpan.FromMilliseconds(10_400));
        await ToolCallAsync(phases, runId, TimeSpan.FromMilliseconds(30_600));
        _clock.Advance(TimeSpan.FromMilliseconds(59_900));

        await DeclareAsync(phases, runId, "planning");

        var record = Single(Records(runId));
        Assert.Equal("00:01:40", record.GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:30", record.GetProperty("toolDuration").GetString());
        Assert.Equal("00:01:10", record.GetProperty("duration").GetString());
    }

    [Fact]
    public async Task Two_runs_with_one_run_id_in_different_workspaces_keep_separate_tool_time()
    {
        var phases = Phases();
        var other = Path.Combine(_repo, "other");
        const string RunId = "20261014-090000-shared";
        foreach (var root in new[] { _repo, other })
        {
            var run = RunDirectory.Create(root, RunId);
            run.WriteState(new RunState(RunId, root, "Text", START, 0, 5));
            await DeclareAsync(phases, RunId, "planning", root);
        }

        await ToolCallAsync(phases, RunId, TimeSpan.FromSeconds(20), _repo);
        await DeclareAsync(phases, RunId, "done", _repo);
        await DeclareAsync(phases, RunId, "done", other);

        Assert.Equal("00:00:20", Single(Records(RunId, _repo)).GetProperty("toolDuration").GetString());
        Assert.Equal("00:00:00", Single(Records(RunId, other)).GetProperty("toolDuration").GetString());
    }

    [Fact]
    public async Task Declarations_applied_out_of_arrival_order_give_a_zero_length_phase_never_a_backward_one()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        _clock.Advance(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource();
        var earlier = OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
        {
            await release.Task;
            return await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "approval", CancellationToken.None, phase: "approval");
        }).AsTask();

        _clock.Advance(TimeSpan.FromSeconds(10));
        await DeclareAsync(phases, runId, "planning");
        release.SetResult();
        await earlier;
        _clock.Advance(TimeSpan.FromSeconds(10));
        await DeclareAsync(phases, runId, "done");

        var records = Records(runId);
        Assert.Equal(["interview", "planning", "approval"], records.Select(record => record.GetProperty("phase").GetString()));
        Assert.Equal(["00:00:20", "00:00:00", "00:00:10"], records.Select(record => record.GetProperty("wallDuration").GetString()));
        Assert.Equal("2026-10-14 09:00:20", records[2].GetProperty("at").GetString());
    }

    [Fact]
    public async Task A_phase_takes_its_position_and_host_session_when_its_opening_is_applied()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases, client: "codex", threadId: "thread-1");
        var release = new TaskCompletionSource();
        var declaring = OrchestratorCall.InvokeAsync(phases, _repo, runId, "codex", "thread-1", async () =>
        {
            await release.Task;
            return await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "revising", CancellationToken.None,
                                              phase: "plan_revision");
        }).AsTask();

        UpdateState(runId, state => state with { ReviewRounds = 3 });
        await ToolCallAsync(phases, runId, TimeSpan.Zero, client: "codex", threadId: "thread-2");
        release.SetResult();
        await declaring;

        var file = PhaseFile(runId);
        Assert.Equal(3, file.GetProperty("round").GetInt32());
        Assert.Equal("thread-2", file.GetProperty("hostSessionId").GetString());

        await DeclareAsync(phases, runId, "done", client: "codex", threadId: "thread-2");
        var record = Records(runId)[1];
        Assert.Equal("thread-2", record.GetProperty("hostSessionId").GetString());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
    }

    [Fact]
    public async Task A_host_session_that_changed_since_the_opening_marks_the_record_incomplete()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases, client: "codex", threadId: "thread-1");

        await DeclareAsync(phases, runId, "planning", client: "codex", threadId: "thread-2");

        var record = Single(Records(runId));
        Assert.Equal("codex", record.GetProperty("host").GetString());
        Assert.Equal("thread-2", record.GetProperty("hostSessionId").GetString());
        Assert.True(record.GetProperty("usageIncomplete").GetBoolean());
    }

    [Fact]
    public async Task A_host_without_a_session_records_time_only_with_no_host_session()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases, client: "other-mcp-client");

        await DeclareAsync(phases, runId, "planning", client: "other-mcp-client");

        var record = Single(Records(runId));
        Assert.Equal("no_host_session", record.GetProperty("usageMissing").GetString());
        Assert.False(record.TryGetProperty("hostSessionId", out _));
        Assert.False(record.TryGetProperty("usageSource", out _));
        Assert.False(record.TryGetProperty("inputTokens", out _));
    }

    [Fact]
    public async Task A_refused_call_still_counts_as_tool_time()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(15));
                return await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "bad", CancellationToken.None, phase: "nope");
            }).AsTask());
        _clock.Advance(TimeSpan.FromSeconds(5));
        await DeclareAsync(phases, runId, "planning");

        Assert.Equal("00:00:15", Single(Records(runId)).GetProperty("toolDuration").GetString());
    }

    [Fact]
    public async Task A_phase_file_holding_neither_state_is_replaced_and_logged_once()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        File.WriteAllText(Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME), """{"state":"open","phase":"lunch"}""");

        await DeclareAsync(phases, runId, "planning");

        Assert.False(File.Exists(TelemetryPath(runId)));
        Assert.Equal("planning", PhaseFile(runId).GetProperty("phase").GetString());
        Assert.Equal(["phase_file_invalid"], Failures(runId));
    }

    [Fact]
    public async Task A_phase_file_that_cannot_be_written_leaves_the_phase_open_for_a_later_declaration()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        var path = Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME);
        _clock.Advance(TimeSpan.FromSeconds(30));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await DeclareAsync(phases, runId, "planning");

        Assert.False(File.Exists(TelemetryPath(runId)));
        Assert.Equal("interview", PhaseFile(runId).GetProperty("phase").GetString());
        Assert.Equal(["phase_file_unwritable"], Failures(runId));

        _clock.Advance(TimeSpan.FromSeconds(30));
        await DeclareAsync(phases, runId, "approval");
        var record = Single(Records(runId));
        Assert.Equal("interview", record.GetProperty("phase").GetString());
        Assert.Equal("00:01:00", record.GetProperty("wallDuration").GetString());
    }

    [Fact]
    public async Task A_declaration_waiting_on_its_source_does_not_hold_the_run_lock()
    {
        var source = new BlockingSource();
        var phases = new OrchestratorPhases(_clock, source, name => name == "CLAUDE_CODE_SESSION_ID" ? "session-a" : null);
        var runId = await BeginAsync(phases);

        var waiting = DeclareAsync(phases, runId, "planning");
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _clock.Advance(TimeSpan.FromSeconds(4));
        await DeclareAsync(phases, runId, "approval").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(waiting.IsCompleted);
        Assert.Equal("approval", PhaseFile(runId).GetProperty("phase").GetString());

        source.Release.SetResult();
        await waiting;
        var records = Records(runId);
        Assert.Equal(2, records.Length);
        Assert.All(records, record => Assert.Equal("transcript", record.GetProperty("usageSource").GetString()));
        Assert.All(records, record => Assert.Equal("session-a", record.GetProperty("hostSessionId").GetString()));
    }

    [Fact]
    public async Task A_close_keeps_its_tool_time_though_a_later_declaration_forgets_before_the_close_reads()
    {
        var source = new BlockingSource();
        var phases = new OrchestratorPhases(_clock, source, name => name == "CLAUDE_CODE_SESSION_ID" ? "session-a" : null);
        var runId = await BeginAsync(phases);
        await ToolCallAsync(phases, runId, TimeSpan.FromSeconds(20));
        _clock.Advance(TimeSpan.FromSeconds(10));

        // The first close is still reading when the second declaration applies its boundary and forgets every call before it.
        var first = DeclareAsync(phases, runId, "planning");
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await DeclareAsync(phases, runId, "approval").WaitAsync(TimeSpan.FromSeconds(30));
        source.Release.SetResult();
        await first;

        var interview = Assert.Single(Records(runId), record => record.GetProperty("phase").GetString() == "interview");
        Assert.Equal("00:00:30", interview.GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:20", interview.GetProperty("toolDuration").GetString());
        var planning = Assert.Single(Records(runId), record => record.GetProperty("phase").GetString() == "planning");
        Assert.Equal("00:00:10", planning.GetProperty("wallDuration").GetString());
    }

    [Fact]
    public async Task A_declaration_waiting_for_the_run_lock_takes_the_position_as_its_opening_is_applied()
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        Run(runId).WritePlan(PLAN);
        UpdateState(runId, state => state with { Approved = true, TasksCompleted = 0 });

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        phases.PhaseFileWritten = phase =>
        {
            if (phase != "approval") return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var holding = Task.Run(() => DeclareAsync(phases, runId, "approval"));
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))));
        var waiting = Task.Run(() => DeclareAsync(phases, runId, "build"));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        // A task completes while the build declaration waits for the lock the approval holds.
        UpdateState(runId, state => state with { TasksCompleted = 2 });
        release.Set();
        await Task.WhenAll(holding, waiting).WaitAsync(TimeSpan.FromSeconds(30));

        var file = PhaseFile(runId);
        Assert.Equal("build", file.GetProperty("phase").GetString());
        Assert.Equal(3, file.GetProperty("taskNumber").GetInt32());
        Assert.Equal(3, file.GetProperty("taskCount").GetInt32());
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("returns")]
    [InlineData("no_host_session")]
    public async Task A_declaration_cancelled_after_the_phase_file_write_loses_the_record_with_one_cancelled_entry(string source)
    {
        var usage = new ReturningSource { ThrowOnCancel = source == "throws" };
        var phases = new OrchestratorPhases(_clock, usage, name => name == "CLAUDE_CODE_SESSION_ID" ? "session-a" : null);
        var client = source == "no_host_session" ? "other-mcp-client" : "claude-code";
        var runId = await BeginAsync(phases, client);
        _clock.Advance(TimeSpan.FromSeconds(10));

        using var cancel = new CancellationTokenSource();
        phases.PhaseFileWritten = _ => cancel.Cancel();
        await OrchestratorCall.InvokeAsync(phases, _repo, runId, client, null, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "entering planning", cancel.Token, phase: "planning"));

        Assert.False(File.Exists(TelemetryPath(runId)));
        Assert.Equal("planning", PhaseFile(runId).GetProperty("phase").GetString());
        Assert.Equal(["cancelled"], Failures(runId));
        // forge.begin's opening only: a cancelled close does not go on to register its opening.
        Assert.Equal(1, usage.Openings);
    }

    [Fact]
    public async Task A_declaration_not_cancelled_records_through_a_source_that_returns()
    {
        var usage = new ReturningSource();
        var phases = new OrchestratorPhases(_clock, usage, name => name == "CLAUDE_CODE_SESSION_ID" ? "session-a" : null);
        var runId = await BeginAsync(phases);
        _clock.Advance(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();

        await OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "entering planning", cancel.Token, phase: "planning"));

        Assert.Equal(5, Single(Records(runId)).GetProperty("inputTokens").GetInt64());
        Assert.Empty(Failures(runId));
    }

    [Theory]
    [InlineData("phase_file_unreadable")]
    [InlineData("phase_file_invalid")]
    [InlineData("phase_file_unwritable")]
    public async Task A_reread_that_keeps_the_registration_fails_with_one_entry_of_its_kind(string kind)
    {
        var usage = new ReturningSource();
        var phases = new OrchestratorPhases(_clock, usage, name => name == "CLAUDE_CODE_SESSION_ID" ? "session-a" : null);
        var runId = await BeginAsync(phases);
        var path = Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME);
        _clock.Advance(TimeSpan.FromSeconds(10));

        FileStream? held = null;
        usage.Opening = () =>
        {
            switch (kind)
            {
                case "phase_file_unreadable":
                    held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                case "phase_file_invalid":
                    File.WriteAllText(path, "{ not json");
                    break;
                default:
                    held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    break;
            }
        };

        try
        {
            await DeclareAsync(phases, runId, "planning");
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal([kind], Failures(runId));
        Assert.Equal("interview", Single(Records(runId)).GetProperty("phase").GetString());
        if (kind != "phase_file_invalid")
        {
            var file = PhaseFile(runId);
            Assert.Equal("planning", file.GetProperty("phase").GetString());
            Assert.False(file.TryGetProperty("registeredAt", out _));
        }
    }

    private OrchestratorPhases Phases() => new(_clock, NoUsageSource.Instance, _ => null);

    private async Task AssertRefused(string phase)
    {
        var phases = Phases();
        var runId = await BeginAsync(phases);
        var before = File.ReadAllText(Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME));
        _clock.Advance(TimeSpan.FromSeconds(9));

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => DeclareAsync(phases, runId, phase));

        foreach (var name in new[] { "interview", "planning", "plan_revision", "approval", "build", "code_review", "wrap_up", "done" })
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LogEntries(runId), entry => entry.GetProperty("event").GetString() == "note");
        Assert.False(File.Exists(TelemetryPath(runId)));
        Assert.Equal(before, File.ReadAllText(Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME)));
    }

    private async Task<string> BeginAsync(OrchestratorPhases phases, string client = "claude-code", string? threadId = null)
    {
        var git = new GitClient(_repo);
        var ct = CancellationToken.None;
        if (!Directory.Exists(Path.Combine(_repo, ".git")))
        {
            await git.OutputAsync(["init", "-q"], ct);
            await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
            await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
            await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
            await git.OutputAsync(["add", "tracked.txt"], ct);
            await git.OutputAsync(["commit", "-qm", "initial"], ct);
        }

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, client, threadId, async () =>
            await ForgeTools.Begin(SessionRoots.None, client, CapabilityProfile.Text, _ => { }, _repo, ct));
        return JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;
    }

    private Task<string> DeclareAsync(OrchestratorPhases phases, string runId, string phase, string? root = null,
                                      string client = "claude-code", string? threadId = null)
    {
        var workspace = root ?? _repo;
        return OrchestratorCall.InvokeAsync(phases, workspace, runId, client, threadId, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, workspace, runId, $"entering {phase}", CancellationToken.None,
                                       phase: phase)).AsTask();
    }

    /// <summary>A call of the Run that lasts <paramref name="length"/> on the test clock.</summary>
    /// <param name="phases">The phase owner the call passes through.</param>
    /// <param name="runId">The Run's id.</param>
    /// <param name="length">How long the call lasts.</param>
    /// <param name="root">The Run's workspace root; the test's repository when null.</param>
    /// <param name="client">The MCP client's name.</param>
    /// <param name="threadId">The <c>threadId</c> the call carries, if any.</param>
    private async Task ToolCallAsync(OrchestratorPhases phases, string runId, TimeSpan length, string? root = null,
                                     string client = "claude-code", string? threadId = null) =>
        await OrchestratorCall.InvokeAsync(phases, root ?? _repo, runId, client, threadId, () =>
        {
            _clock.Advance(length);
            return ValueTask.FromResult("done");
        });

    private RunDirectory Run(string runId, string? root = null) => RunDirectory.Open(root ?? _repo, runId);

    private void UpdateState(string runId, Func<RunState, RunState> change)
    {
        var run = Run(runId);
        run.WriteState(change(run.ReadState()));
    }

    private string TelemetryPath(string runId, string? root = null) => Path.Combine(Run(runId, root).Path, "telemetry.json");

    private JsonElement[] Records(string runId, string? root = null) =>
        [.. JsonDocument.Parse(File.ReadAllText(TelemetryPath(runId, root))).RootElement.EnumerateArray()];

    private static JsonElement Single(JsonElement[] records) => Assert.Single(records);

    private JsonElement PhaseFile(string runId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME))).RootElement;

    private IReadOnlyList<JsonElement> LogEntries(string runId) =>
        [.. AtomicFile.Read(Run(runId).DiagnosticLogPath)
                      .Split('\n')
                      .Select(line => line.TrimEnd('\r'))
                      .Where(line => line.Length > 0)
                      .Select(line => JsonDocument.Parse(line).RootElement)];

    private IReadOnlyList<string> Failures(string runId) =>
        [.. LogEntries(runId).Where(entry => entry.GetProperty("event").GetString() == "orchestrator.telemetry.failed")
                             .Select(entry => entry.GetProperty("fields").GetProperty("kind").GetString()!)];

    /// <summary>A clock that moves only when a test moves it, in UTC so local text is predictable.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = START.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }

    /// <summary>A transcript-like source whose first read waits for the test.</summary>
    private sealed class BlockingSource : IUsageSource
    {
        private int _reads;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Name => "transcript";

        public TimeSpan SettleDelay => TimeSpan.Zero;

        public TimeSpan ReadDeadline => TimeSpan.FromMinutes(1);

        public async Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
            }

            return new UsageReading(new OrchestratorTokens(InputTokens: 5, OutputTokens: 1));
        }

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);
    }

    /// <summary>
    /// A source that answers at once, whatever the cancellation — or throws on it when told to — and
    /// reports a registration at every opening, after running <see cref="Opening"/>.
    /// </summary>
    private sealed class ReturningSource : IUsageSource
    {
        private int _openings;

        public bool ThrowOnCancel { get; init; }

        public Action? Opening { get; set; }

        public int Openings => Volatile.Read(ref _openings);

        public string? Name => "telemetry";

        public TimeSpan SettleDelay => TimeSpan.Zero;

        public TimeSpan ReadDeadline => TimeSpan.FromMinutes(1);

        public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
        {
            if (ThrowOnCancel) ct.ThrowIfCancellationRequested();
            return Task.FromResult(new UsageReading(new OrchestratorTokens(InputTokens: 5, OutputTokens: 1)));
        }

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct)
        {
            Interlocked.Increment(ref _openings);
            Opening?.Invoke();
            return Task.FromResult<DateTimeOffset?>(START);
        }
    }
}
