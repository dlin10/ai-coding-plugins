using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The telemetry export Usage source and the choice of source (R6, R7, R9, R10), against a collector on
/// a free port. Every choice here is the test's own, over a test-owned environment and profile; no test
/// reads a real profile or environment variable, and every collector a test starts is stopped by it.
/// </summary>
public sealed class TelemetryUsageSourceTests : IAsyncLifetime
{
    private const string CHOSEN = "orchestrator.source.chosen";

    private static readonly HttpClient HTTP = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-telemetry", Guid.NewGuid().ToString("n"));
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly int _port = TelemetryCollectorTests.FreePort();
    private readonly List<TelemetryCollectorTests.Running> _collectors = [];
    private string? _settings;
    private int _profileReads;
    private int _starts;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_repo);
        _settings = Settings(_port, "1");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var collector in _collectors) await StopAsync(collector);
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---- Totals and the wait (R7) ----

    [Fact]
    public async Task A_phase_s_totals_are_the_sum_of_its_session_s_API_requests()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        var at = await InPhaseAsync();
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, at), TelemetryCollectorTests.ClaudeRequest(_session, at.AddMilliseconds(5)));

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal("telemetry", record.GetProperty("usageSource").GetString());
        Assert.Equal(_session, record.GetProperty("hostSessionId").GetString());
        Assert.Equal(2 * (2 + 46187), record.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0, record.GetProperty("cacheReadTokens").GetInt64());
        Assert.Equal(2 * 46187, record.GetProperty("cacheCreationTokens").GetInt64());
        Assert.Equal(8, record.GetProperty("outputTokens").GetInt64());
        Assert.Equal(2 * 0.0092396m, record.GetProperty("costUsd").GetDecimal());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.False(record.TryGetProperty("usageMissing", out _));
    }

    [Fact]
    public async Task A_close_waits_the_export_interval_and_a_second_so_a_late_event_is_counted()
    {
        _settings = Settings(_port, "1500");
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        var at = await InPhaseAsync();

        var watch = Stopwatch.StartNew();
        var declaring = DeclareAsync(phases, runId, "planning");
        await Task.Delay(500);
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, at));
        await declaring;
        watch.Stop();

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(2400), $"the close took {watch.Elapsed}");
        Assert.Equal(2 + 46187, Assert.Single(Records(runId)).GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task Events_of_a_session_no_server_registered_are_not_counted()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        var at = await InPhaseAsync();
        var other = Guid.NewGuid().ToString();
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(other, at), TelemetryCollectorTests.CodexCompleted(other, at));

        await DeclareAsync(phases, runId, "planning");

        Assert.Equal("no_telemetry_events", Assert.Single(Records(runId)).GetProperty("usageMissing").GetString());
        Assert.Empty(_collectors[0].Collector.Kept());
    }

    [Fact]
    public async Task A_session_without_events_since_its_registration_has_no_telemetry_events_and_no_warning()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal("no_telemetry_events", record.GetProperty("usageMissing").GetString());
        Assert.False(record.TryGetProperty("inputTokens", out _));
        Assert.Empty(Failures(runId));
    }

    [Fact]
    public async Task A_session_whose_events_lie_outside_the_phase_has_zero_counters()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await InPhaseAsync();
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, DateTimeOffset.UtcNow.AddHours(1)));

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal(0, record.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0, record.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0m, record.GetProperty("costUsd").GetDecimal());
        Assert.False(record.TryGetProperty("usageMissing", out _));
    }

    // ---- Coverage (R9) ----

    [Fact]
    public async Task Interview_opened_by_the_call_whose_choice_registered_the_session_is_not_incomplete()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        Assert.True(PhaseFile(runId).TryGetProperty("registeredAt", out _));
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync()));

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal(2 + 46187, record.GetProperty("inputTokens").GetInt64());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
    }

    [Fact]
    public async Task A_phase_that_began_before_an_earlier_call_s_registration_is_incomplete()
    {
        StartCollector();
        var earlier = Phases(NoUsageSource.Instance);
        var first = await BeginAsync(earlier);
        await Task.Delay(50);

        var phases = Phases(Choice());
        await BeginAsync(phases);
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync()));

        await DeclareAsync(phases, first, "planning");

        var record = Assert.Single(Records(first));
        Assert.Equal(2 + 46187, record.GetProperty("inputTokens").GetInt64());
        Assert.True(record.GetProperty("usageIncomplete").GetBoolean());
    }

    [Fact]
    public async Task A_collector_restarted_before_a_declaration_gives_session_unknown_and_covers_the_next_phase_from_its_opening()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await RestartCollectorAsync();

        await DeclareAsync(phases, runId, "planning");
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync()));
        await DeclareAsync(phases, runId, "done");

        var records = Records(runId);
        Assert.Equal("source_failed", records[0].GetProperty("usageMissing").GetString());
        Assert.Equal(["session_unknown"], Failures(runId));
        Assert.Equal("planning", records[1].GetProperty("phase").GetString());
        Assert.Equal(2 + 46187, records[1].GetProperty("inputTokens").GetInt64());
        Assert.False(records[1].TryGetProperty("usageIncomplete", out _));
    }

    [Fact]
    public async Task A_phase_closed_by_another_call_while_its_opening_call_registers_is_not_incomplete()
    {
        StartCollector();
        var paused = new PausedOpening(Choice());
        var phases = Phases(paused);
        var runId = await BeginAsync(phases);
        await RestartCollectorAsync();

        // A closes interview — its 404 registers the session again, after planning's opening — and pauses before its own registration.
        paused.PauseNext();
        var opening = DeclareAsync(phases, runId, "planning");
        await paused.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var marked = PhaseFile(runId).TryGetProperty("registering", out _);

        // B, from the same response, closes planning before A's registration is written.
        await DeclareAsync(phases, runId, "done");
        paused.Release();
        await opening;

        var records = Records(runId);
        Assert.Equal("planning", records[1].GetProperty("phase").GetString());
        Assert.False(records[1].TryGetProperty("usageIncomplete", out _));
        Assert.Equal("no_telemetry_events", records[1].GetProperty("usageMissing").GetString());
        Assert.Equal(["session_unknown"], Failures(runId));
        Assert.True(marked);
    }

    [Fact]
    public async Task A_phase_whose_opening_registration_failed_and_that_another_call_registered_later_is_incomplete()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await StopAsync(_collectors[0]);
        using (Silent())
        {
            await DeclareAsync(phases, runId, "planning");
        }

        Assert.False(PhaseFile(runId).TryGetProperty("registering", out _));
        Assert.False(PhaseFile(runId).TryGetProperty("registeredAt", out _));

        // Another Run's opening registers the session with a new collector, after planning opened.
        StartCollector();
        await BeginAsync(Phases(Choice()));
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync()));
        await DeclareAsync(phases, runId, "done");

        var record = Records(runId)[1];
        Assert.Equal(2 + 46187, record.GetProperty("inputTokens").GetInt64());
        Assert.True(record.GetProperty("usageIncomplete").GetBoolean());
        Assert.Equal(["source_timeout", "registration_failed"], Failures(runId));
    }

    [Fact]
    public async Task A_404_at_a_close_gives_session_unknown_and_registers_the_session_again_at_once()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await RestartCollectorAsync();

        await DeclareAsync(phases, runId, "done");

        Assert.Equal("source_failed", Assert.Single(Records(runId)).GetProperty("usageMissing").GetString());
        Assert.Equal(["session_unknown"], Failures(runId));
        var now = DateTimeOffset.UtcNow;
        var usage = await HTTP.GetAsync(Url($"planforge/usage?session={_session}&from={Uri.EscapeDataString(now.ToString("o"))}"
                                            + $"&to={Uri.EscapeDataString(now.AddMinutes(1).ToString("o"))}"));
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
    }

    // ---- Failures at a close (R10) ----

    [Fact]
    public async Task A_collector_that_stops_answering_gives_source_failed_one_warning_and_the_usual_result()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await StopAsync(_collectors[0]);

        var watch = Stopwatch.StartNew();
        var result = await DeclareAsync(phases, runId, "done");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the close took {watch.Elapsed}");
        Assert.Equal(Run(runId).DiagnosticLogPath, result);
        Assert.Equal("source_failed", Assert.Single(Records(runId)).GetProperty("usageMissing").GetString());
        Assert.Equal(["source_failed"], Failures(runId));
    }

    [Fact]
    public async Task A_collector_that_accepts_but_never_answers_gives_source_failed_within_the_deadline()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        await StopAsync(_collectors[0]);
        using var silent = Silent();

        var watch = Stopwatch.StartNew();
        var result = await DeclareAsync(phases, runId, "done");

        // One second's wait after the close, then the read's own two seconds.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the close took {watch.Elapsed}");
        Assert.Equal(Run(runId).DiagnosticLogPath, result);
        Assert.Equal("source_failed", Assert.Single(Records(runId)).GetProperty("usageMissing").GetString());
        Assert.Equal(["source_timeout"], Failures(runId));
    }

    // ---- Openings and restarts (R9, R10) ----

    [Fact]
    public async Task A_phase_opening_whose_registration_meets_a_silent_collector_is_delayed_at_most_two_seconds()
    {
        StartCollector();
        var phases = Phases(Choice());
        await BeginAsync(phases);
        await StopAsync(_collectors[0]);
        using var silent = Silent();

        var watch = Stopwatch.StartNew();
        var second = await BeginAsync(phases);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4.5), $"the opening took {watch.Elapsed}");
        Assert.Equal("interview", PhaseFile(second).GetProperty("phase").GetString());
        Assert.Equal(["registration_failed"], Failures(second));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task An_opening_that_finds_no_collector_restarts_one_registers_and_writes_one_restarted_entry()
    {
        var phases = Phases(Choice(StandIn(refuseBreakaway: false, killOnClose: false)));
        var runId = await BeginAsync(phases);
        Assert.Equal(1, _starts);
        await StopAsync(_collectors[0]);

        await DeclareAsync(phases, runId, "planning");

        Assert.Equal(2, _starts);
        Assert.True(PhaseFile(runId).TryGetProperty("registeredAt", out _));
        var restarted = Assert.Single(LogEntries(runId), entry => entry.GetProperty("event").GetString() == "orchestrator.collector.restarted");
        Assert.Equal("info", restarted.GetProperty("level").GetString());
        Assert.Equal(_port.ToString(), restarted.GetProperty("fields").GetProperty("port").GetString());
        Assert.Equal("false", restarted.GetProperty("fields").GetProperty("insideJob").GetString());
        Assert.Equal(["source_failed"], Failures(runId));
    }

    [Fact]
    public async Task A_restart_that_fails_is_registration_failed_and_none_is_tried_for_five_minutes()
    {
        var failing = false;
        var phases = Phases(Choice(port =>
        {
            if (failing)
            {
                _starts++;
                return new CollectorStart(CollectorStartOutcome.Failed);
            }

            return StandIn(refuseBreakaway: false, killOnClose: false)(port);
        }));
        var runId = await BeginAsync(phases);
        await StopAsync(_collectors[0]);
        failing = true;

        await DeclareAsync(phases, runId, "planning");
        await DeclareAsync(phases, runId, "approval");

        Assert.Equal(2, _starts);
        Assert.Equal(["source_failed", "registration_failed", "source_failed", "registration_failed"], Failures(runId));
        Assert.DoesNotContain(LogEntries(runId), entry => entry.GetProperty("event").GetString() == "orchestrator.collector.restarted");
    }

    // ---- The choice (R6, R9, R10) ----

    [Fact]
    public async Task A_collector_that_does_not_answer_within_the_choice_s_ten_seconds_leaves_the_transcript()
    {
        using var silent = Silent();
        // Timed where the choice is made and awaited, not around the whole call: the helper's git setup
        // and forge.begin's own git work share that call, and on a loaded runner they outlasted the
        // bound's slack.
        var source = new TimedArrival(Choice());
        var phases = Phases(source);

        var runId = await BeginAsync(phases);

        Assert.True(source.First is { } took && took >= TimeSpan.FromSeconds(9.5) && took < TimeSpan.FromSeconds(13.5),
                    $"the choice took {source.First}");
        var chosen = Assert.Single(Chosen(runId));
        Assert.Equal("transcript", chosen.GetProperty("usageSource").GetString());
        Assert.Contains("within 10 seconds", chosen.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, _starts);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(null, "unreadable")]
    public async Task A_job_object_that_refuses_breakaway_gets_a_collector_inside_it_whatever_its_close_does_and_the_entry_says_so(
        bool? killOnClose, string reported)
    {
        var phases = Phases(Choice(StandIn(refuseBreakaway: true, killOnClose: killOnClose)));

        var runId = await BeginAsync(phases);

        var chosen = Assert.Single(Chosen(runId));
        Assert.Equal("telemetry", chosen.GetProperty("usageSource").GetString());
        Assert.Equal("started", chosen.GetProperty("collector").GetString());
        Assert.Equal("true", chosen.GetProperty("insideJob").GetString());
        Assert.Equal(reported, chosen.GetProperty("jobKillsOnClose").GetString());
        Assert.Single(_collectors);
        Assert.True(PhaseFile(runId).TryGetProperty("registeredAt", out _));
    }

    [Fact]
    public async Task A_job_object_that_refuses_breakaway_and_in_which_no_process_can_be_created_leaves_the_transcript_with_the_reason()
    {
        var phases = Phases(Choice(StandIn(refuseBreakaway: true, killOnClose: true, createInside: false)));

        var runId = await BeginAsync(phases);

        var chosen = Assert.Single(Chosen(runId));
        Assert.Equal("transcript", chosen.GetProperty("usageSource").GetString());
        Assert.Contains("could not be started inside it", chosen.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(_collectors);
        await Assert.ThrowsAsync<HttpRequestException>(() => HTTP.GetAsync(Url("planforge/collector")));
    }

    [Fact]
    public async Task A_collector_inside_a_kill_on_close_job_that_ended_with_it_is_restarted_by_the_next_phase_opening()
    {
        var phases = Phases(Choice(StandIn(refuseBreakaway: true, killOnClose: true)));
        var runId = await BeginAsync(phases);
        Assert.Equal(1, _starts);

        // The host's job object closed, and the collector inside it ended with it.
        await StopAsync(_collectors[0]);

        await DeclareAsync(phases, runId, "planning");
        Assert.Equal(2, _starts);
        Assert.True(PhaseFile(runId).TryGetProperty("registeredAt", out _));
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync()));
        await DeclareAsync(phases, runId, "done");

        var restarted = Assert.Single(LogEntries(runId), entry => entry.GetProperty("event").GetString() == "orchestrator.collector.restarted");
        Assert.Equal("true", restarted.GetProperty("fields").GetProperty("insideJob").GetString());
        Assert.Equal("true", restarted.GetProperty("fields").GetProperty("jobKillsOnClose").GetString());
        var records = Records(runId);
        Assert.Equal("source_failed", records[0].GetProperty("usageMissing").GetString());
        Assert.Equal(2 + 46187, records[1].GetProperty("inputTokens").GetInt64());
        Assert.False(records[1].TryGetProperty("usageIncomplete", out _));
        Assert.Equal(["source_failed"], Failures(runId));
    }

    [Fact]
    public async Task A_deciding_note_without_a_phase_writes_the_choice_to_its_Run_under_another_MCP_root()
    {
        // The Run lives under the host's root; nothing of it lies under the workspace root the call names.
        var hostRoot = Path.Combine(_repo, "host-root");
        const string RunId = "20261014-090000-elsewhere";
        var run = RunDirectory.Create(hostRoot, RunId);
        var phases = Phases(Choice());

        await OrchestratorCall.InvokeAsync(phases, _repo, RunId, "claude-code", null, async () =>
            await ForgeTools.AppendLog(SessionRoots.At(hostRoot), _repo, RunId, "a note", CancellationToken.None));

        var chosen = Assert.Single(AtomicFile.Read(run.DiagnosticLogPath)
                                             .Split('\n')
                                             .Select(line => line.TrimEnd('\r'))
                                             .Where(line => line.Length > 0)
                                             .Select(line => JsonDocument.Parse(line).RootElement),
                                    entry => entry.GetProperty("event").GetString() == CHOSEN);
        Assert.Equal("transcript", chosen.GetProperty("fields").GetProperty("usageSource").GetString());
        Assert.False(Directory.Exists(Path.Combine(_repo, ".forge", RunId)));
    }

    [Fact]
    public async Task A_refused_deciding_call_for_a_Run_under_another_MCP_root_writes_the_choice_once_at_the_next_call_that_serves_it()
    {
        // A restarted server: the Run exists already, under the host's root, and the first call is refused.
        var hostRoot = Path.Combine(_repo, "host-root");
        const string RunId = "20261014-090000-refused";
        var run = RunDirectory.Create(hostRoot, RunId);
        var phases = Phases(Choice());
        var roots = SessionRoots.At(hostRoot);
        Task Note(string? phase) =>
            OrchestratorCall.InvokeAsync(phases, _repo, RunId, "claude-code", null, async () =>
                await ForgeTools.AppendLog(roots, _repo, RunId, "a note", CancellationToken.None, phase: phase)).AsTask();

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => Note("Planning"));
        Assert.Empty(ChosenIn(run.DiagnosticLogPath));

        // Refused again: still nothing it could write to.
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => Note(" "));
        Assert.Empty(ChosenIn(run.DiagnosticLogPath));

        await Note(null);
        var chosen = Assert.Single(ChosenIn(run.DiagnosticLogPath));
        Assert.Equal("transcript", chosen.GetProperty("fields").GetProperty("usageSource").GetString());

        await Note("planning");
        Assert.Single(ChosenIn(run.DiagnosticLogPath));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".forge", RunId)));
    }

    [Fact]
    public async Task A_refused_deciding_call_for_a_Run_under_its_workspace_root_writes_the_choice_once_as_it_ends()
    {
        const string RunId = "20261014-090000-refused-here";
        var run = RunDirectory.Create(_repo, RunId);
        var phases = Phases(Choice());
        Task Note(string? phase) =>
            OrchestratorCall.InvokeAsync(phases, _repo, RunId, "claude-code", null, async () =>
                await ForgeTools.AppendLog(SessionRoots.None, _repo, RunId, "a note", CancellationToken.None, phase: phase)).AsTask();

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => Note("unknown"));
        Assert.Single(ChosenIn(run.DiagnosticLogPath));

        await Note(null);
        await Note("planning");
        Assert.Single(ChosenIn(run.DiagnosticLogPath));
    }

    [Fact]
    public async Task A_held_choice_entry_is_not_written_to_another_Run()
    {
        var hostRoot = Path.Combine(_repo, "host-root");
        const string RunId = "20261014-090000-held";
        const string OtherId = "20261014-090000-other";
        var run = RunDirectory.Create(hostRoot, RunId);
        var other = RunDirectory.Create(hostRoot, OtherId);
        var phases = Phases(Choice());
        var roots = SessionRoots.At(hostRoot);
        Task Note(string runId, string? phase) =>
            OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
                await ForgeTools.AppendLog(roots, _repo, runId, "a note", CancellationToken.None, phase: phase)).AsTask();

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => Note(RunId, "Planning"));
        await Note(OtherId, null);
        Assert.Empty(ChosenIn(other.DiagnosticLogPath));

        await Note(RunId, null);
        Assert.Single(ChosenIn(run.DiagnosticLogPath));
    }

    private static List<JsonElement> ChosenIn(string logPath) =>
        !File.Exists(logPath)
            ? []
            : [.. AtomicFile.Read(logPath)
                            .Split('\n')
                            .Select(line => line.TrimEnd('\r'))
                            .Where(line => line.Length > 0)
                            .Select(line => JsonDocument.Parse(line).RootElement)
                            .Where(entry => entry.GetProperty("event").GetString() == CHOSEN)];

    // ---- Failures the source reports beside its reading (R10) ----

    [Fact]
    public async Task A_phase_whose_every_exported_counter_is_invalid_records_source_failed_with_one_warning()
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        var invalid = new JsonObject { ["boolValue"] = true };
        await PostAsync(TelemetryCollectorTests.ClaudeRequest(_session, await InPhaseAsync(), input: invalid.DeepClone().AsObject(),
                                                              cacheRead: invalid.DeepClone().AsObject(),
                                                              cacheCreation: invalid.DeepClone().AsObject(),
                                                              output: invalid.DeepClone().AsObject(), cost: invalid.DeepClone().AsObject()));

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal("source_failed", record.GetProperty("usageMissing").GetString());
        Assert.True(record.GetProperty("usageIncomplete").GetBoolean());
        Assert.Equal(["source_failed"], Failures(runId));
    }

    [Theory]
    [InlineData("succeeds", "planning")]
    [InlineData("succeeds", "done")]
    [InlineData("fails", "planning")]
    [InlineData("fails", "done")]
    [InlineData("times_out", "planning")]
    [InlineData("times_out", "done")]
    public async Task A_404_at_a_close_reports_the_re_registration_as_a_failure_of_its_own(string registration, string phase)
    {
        StartCollector();
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases);
        Scripted? scripted = null;
        if (registration == "succeeds")
        {
            await RestartCollectorAsync();
        }
        else
        {
            await StopAsync(_collectors[0]);
            scripted = new Scripted(_port, hang: registration == "times_out");
        }

        try
        {
            await DeclareAsync(phases, runId, phase);
        }
        finally
        {
            scripted?.Dispose();
        }

        Assert.Equal("source_failed", Records(runId)[0].GetProperty("usageMissing").GetString());

        // The close's two failures, then — for a declaration that opens — the opening's own registration.
        string[] expected = registration == "succeeds" ? ["session_unknown"]
                            : phase == "done" ? ["session_unknown", "registration_failed"]
                            : ["session_unknown", "registration_failed", "registration_failed"];
        Assert.Equal(expected, Failures(runId));
    }

    [Fact]
    public async Task A_job_object_that_refuses_breakaway_without_killing_on_close_gets_a_collector_inside_it()
    {
        var phases = Phases(Choice(StandIn(refuseBreakaway: true, killOnClose: false)));

        var runId = await BeginAsync(phases);

        var chosen = Assert.Single(Chosen(runId));
        Assert.Equal("telemetry", chosen.GetProperty("usageSource").GetString());
        Assert.Equal("started", chosen.GetProperty("collector").GetString());
        Assert.Equal("true", chosen.GetProperty("insideJob").GetString());
        Assert.True(PhaseFile(runId).TryGetProperty("registeredAt", out _));
    }

    [Fact]
    public async Task A_helper_in_a_job_object_without_breakaway_or_kill_on_close_starts_a_collector_inside_it_that_outlives_both()
    {
        var job = TelemetryCollectorTests.Job.Create(0);
        using var helper = TelemetryCollectorTests.StartHelper(job, _port, TelemetryCollectorTests.PlanForgeProgram);
        int? collectorPid = null;
        try
        {
            var (outcome, pid) = await helper.StartCollectorAsync();
            collectorPid = pid;
            Assert.Equal(CollectorStartOutcome.Started, outcome);
            Assert.True(helper.InsideJob);

            helper.Process.StandardInput.Close();
            Assert.True(helper.Process.WaitForExit(TimeSpan.FromSeconds(10)));
            job.Dispose();
            await Task.Delay(500);
            Assert.Equal(pid, (await TelemetryCollectorTests.IdentityWithinAsync(_port)).ProcessId);
        }
        finally
        {
            job.Dispose();
            TelemetryCollectorTests.KillById(collectorPid);
        }
    }

    [Fact]
    public async Task A_Codex_thread_first_seen_at_a_later_call_is_chosen_for_there_and_earlier_closes_carry_no_host_session()
    {
        var phases = Phases(Choice());
        var runId = await BeginAsync(phases, client: "codex");
        await DeclareAsync(phases, runId, "planning", client: "codex");
        Assert.Empty(Chosen(runId));
        Assert.Equal(0, _profileReads);

        await DeclareAsync(phases, runId, "approval", client: "codex", threadId: "thread-7");

        var records = Records(runId);
        Assert.Equal("no_host_session", records[0].GetProperty("usageMissing").GetString());
        Assert.Equal("thread-7", records[1].GetProperty("hostSessionId").GetString());
        var chosen = Assert.Single(Chosen(runId));
        Assert.Equal("codex", chosen.GetProperty("hostSession").GetString());
        Assert.Equal("transcript", chosen.GetProperty("usageSource").GetString());
        Assert.Equal(1, _profileReads);
    }

    [Fact]
    public async Task The_choice_entry_lands_once_in_the_deciding_Run_s_log()
    {
        StartCollector();
        var phases = Phases(Choice());
        var deciding = await BeginAsync(phases);
        var other = await BeginAsync(phases);
        await DeclareAsync(phases, deciding, "planning");
        await DeclareAsync(phases, other, "planning");

        var chosen = Assert.Single(Chosen(deciding));
        Assert.Equal("telemetry", chosen.GetProperty("usageSource").GetString());
        Assert.Equal("found", chosen.GetProperty("collector").GetString());
        Assert.Empty(Chosen(other));
        Assert.Equal(1, _profileReads);
    }

    [Fact]
    public async Task A_stdio_server_whose_choice_starts_a_collector_exits_to_end_of_file_while_the_collector_answers()
    {
        var profile = Path.Combine(_repo, "settings.json");
        await File.WriteAllTextAsync(profile, _settings);
        var info = new ProcessStartInfo(TelemetryCollectorTests.PlanForgeProgram)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.Environment["CLAUDE_CODE_SESSION_ID"] = _session;
        info.Environment["PLANFORGE_CLAUDE_SETTINGS"] = profile;
        info.Environment["PLANFORGE_CODEX_CONFIG"] = Path.Combine(_repo, "config.toml");
        info.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(_repo, "claude");
        info.Environment["CODEX_HOME"] = Path.Combine(_repo, "codex");

        // A job object the server cannot break away from and that leaves its processes when closed.
        var job = TelemetryCollectorTests.Job.Create(0);
        using var server = Process.Start(info)!;
        int? collectorPid = null;
        try
        {
            job.Assign(server);
            var errors = server.StandardError.ReadToEndAsync();
            await SendAsync(server, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "1.0.0" },
                },
            });
            await ReadAnswerAsync(server, 1);
            await SendAsync(server, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            await SendAsync(server, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "forge.status",
                    ["arguments"] = new JsonObject { ["workspaceRoot"] = _repo, ["runId"] = "20261014-090000-absent" },
                },
            });
            await ReadAnswerAsync(server, 2);

            collectorPid = (await TelemetryCollectorTests.IdentityWithinAsync(_port)).ProcessId;
            Assert.NotEqual(server.Id, collectorPid);

            server.StandardInput.Close();
            Assert.True(server.WaitForExit(TimeSpan.FromSeconds(20)));
            await server.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await errors.WaitAsync(TimeSpan.FromSeconds(10));
            job.Dispose();

            Assert.Equal(collectorPid, (await TelemetryCollectorTests.IdentityWithinAsync(_port)).ProcessId);
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            job.Dispose();
            TelemetryCollectorTests.KillById(collectorPid);
        }
    }

    [Fact]
    public async Task In_the_test_assembly_with_no_source_installed_a_Claude_session_records_time_only()
    {
        StartCollector();
        var phases = Phases(NoUsageSource.Instance);
        var runId = await BeginAsync(phases);

        await DeclareAsync(phases, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal(_session, record.GetProperty("hostSessionId").GetString());
        Assert.Equal("no_host_session", record.GetProperty("usageMissing").GetString());
        Assert.False(record.TryGetProperty("usageSource", out _));
        Assert.False(record.TryGetProperty("inputTokens", out _));
        Assert.Equal(0, _profileReads);
        Assert.Equal(0, _starts);
        Assert.Empty(Chosen(runId));
        var now = DateTimeOffset.UtcNow;
        var usage = await HTTP.GetAsync(Url($"planforge/usage?session={_session}&from={Uri.EscapeDataString(now.ToString("o"))}"
                                            + $"&to={Uri.EscapeDataString(now.ToString("o"))}"));
        Assert.Equal(HttpStatusCode.NotFound, usage.StatusCode);
    }

    // ---- Helpers ----

    /// <summary>The test's environment: its own session and empty profile folders, nothing of the real one.</summary>
    /// <param name="name">The variable.</param>
    private string? Environment(string name) => name switch
    {
        "CLAUDE_CODE_SESSION_ID" => _session,
        "CLAUDE_CONFIG_DIR" => Path.Combine(_repo, "claude"),
        "CODEX_HOME" => Path.Combine(_repo, "codex"),
        _ => null,
    };

    private string? ReadProfile(string path)
    {
        Interlocked.Increment(ref _profileReads);
        // No Codex profile: a Codex thread reads as not configured.
        return Path.GetFileName(path) == "settings.json" ? _settings : null;
    }

    private UsageSourceChoice Choice(Func<int, CollectorStart>? start = null) =>
        new(TimeProvider.System, Environment, ReadProfile, start ?? (_ =>
        {
            Interlocked.Increment(ref _starts);
            return new CollectorStart(CollectorStartOutcome.Failed);
        }));

    private OrchestratorPhases Phases(IUsageSource source) => new(TimeProvider.System, source, Environment);

    /// <summary>
    /// The real starter over a stand-in for the job object: creation outside it is refused or not, and
    /// a creation that succeeds starts an in-process collector on the port.
    /// </summary>
    /// <param name="refuseBreakaway">Whether a start with <c>CREATE_BREAKAWAY_FROM_JOB</c> fails with access denied.</param>
    /// <param name="killOnClose">What the limits query reports; null when the limits cannot be read.</param>
    /// <param name="createInside">Whether a creation inside the job object succeeds.</param>
    private Func<int, CollectorStart> StandIn(bool refuseBreakaway, bool? killOnClose, bool createInside = true) => port =>
    {
        Interlocked.Increment(ref _starts);
        return CollectorStarter.Start(port, new CollectorCommand(TelemetryCollectorTests.PlanForgeProgram, []), null,
                                      (string _, string _, int flags, out int processId, out int error) =>
                                      {
                                          var breakaway = (flags & CollectorStarter.CREATE_BREAKAWAY_FROM_JOB) != 0;
                                          if (refuseBreakaway && breakaway || !breakaway && !createInside)
                                          {
                                              processId = 0;
                                              error = CollectorStarter.ERROR_ACCESS_DENIED;
                                              return false;
                                          }

                                          StartCollector();
                                          processId = System.Environment.ProcessId;
                                          error = 0;
                                          return true;
                                      },
                                      () => killOnClose);
    };

    private void StartCollector()
    {
        lock (_collectors) _collectors.Add(TelemetryCollectorTests.Running.Start(port: _port));
    }

    private async Task RestartCollectorAsync()
    {
        await StopAsync(_collectors[^1]);
        StartCollector();
    }

    private static async Task StopAsync(TelemetryCollectorTests.Running collector)
    {
        if (collector.Run.IsCompleted) return;
        await collector.DisposeAsync();
    }

    /// <summary>
    /// A collector of this protocol that has lost every session: it answers who it is, 404 to every usage
    /// question, and to a registration either 500 or, when <c>hang</c>, nothing until disposed.
    /// </summary>
    private sealed class Scripted : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public Scripted(int port, bool hang)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = ServeAsync(hang);
        }

        public void Dispose()
        {
            _listener.Abort();
            _serving.Wait(TimeSpan.FromSeconds(10));
        }

        private async Task ServeAsync(bool hang)
        {
            var identity = Encoding.UTF8.GetBytes(
                $$"""{"protocol":{{TelemetryCollector.PROTOCOL}},"processId":4,"startedAt":"2026-10-14T09:00:00+00:00"}""");
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    var path = context.Request.Url!.AbsolutePath;
                    if (path == "/planforge/sessions" && hang) continue;

                    context.Response.StatusCode = path switch
                    {
                        "/planforge/collector" => 200,
                        "/planforge/usage" => 404,
                        _ => 500,
                    };
                    if (path == "/planforge/collector")
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(identity);
                    }

                    context.Response.Close();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                }
            }
        }
    }

    /// <summary>A source that, once asked, holds the next phase opening before it reaches the real source.</summary>
    /// <param name="inner">The real source.</param>
    private sealed class PausedOpening(IUsageSource inner) : IUsageSource
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pause;

        public Task Reached => _reached.Task;

        public string? Name => inner.Name;

        public TimeSpan SettleDelay => inner.SettleDelay;

        public TimeSpan ReadDeadline => inner.ReadDeadline;

        public bool RegistersAtOpening => inner.RegistersAtOpening;

        public void PauseNext() => Volatile.Write(ref _pause, 1);

        public void Release() => _release.TrySetResult();

        public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct) => inner.ReadAsync(window, ct);

        public async Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }

            return await inner.PhaseOpenedAsync(session, ct);
        }

        public Task CallArrivedAsync(HostSession? session, OrchestratorCall call) => inner.CallArrivedAsync(session, call);

        public Task CallEndedAsync(OrchestratorCall call) => inner.CallEndedAsync(call);
    }

    /// <summary>A source that times the first call's arrival, where the choice is made and awaited.</summary>
    /// <param name="inner">The real source.</param>
    private sealed class TimedArrival(IUsageSource inner) : IUsageSource
    {
        private TimeSpan? _first;

        /// <summary>How long the first call waited at its arrival; null before one arrived.</summary>
        public TimeSpan? First => _first;

        public string? Name => inner.Name;

        public TimeSpan SettleDelay => inner.SettleDelay;

        public TimeSpan ReadDeadline => inner.ReadDeadline;

        public bool RegistersAtOpening => inner.RegistersAtOpening;

        public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct) => inner.ReadAsync(window, ct);

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) => inner.PhaseOpenedAsync(session, ct);

        public async Task CallArrivedAsync(HostSession? session, OrchestratorCall call)
        {
            var watch = Stopwatch.StartNew();
            await inner.CallArrivedAsync(session, call);
            _first ??= watch.Elapsed;
        }

        public Task CallEndedAsync(OrchestratorCall call) => inner.CallEndedAsync(call);
    }

    /// <summary>A listener on the port that accepts connections and never answers.</summary>
    private TcpListener Silent()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        return listener;
    }

    private static string Settings(int port, string interval) =>
        new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
                ["OTEL_LOGS_EXPORTER"] = "otlp",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/json",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{port}",
                ["OTEL_LOGS_EXPORT_INTERVAL"] = interval,
            },
        }.ToJsonString();

    /// <summary>An instant inside the open phase and after its session's registration, to the millisecond.</summary>
    private static async Task<DateTimeOffset> InPhaseAsync()
    {
        await Task.Delay(20);
        var at = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        return at;
    }

    private async Task PostAsync(params JsonObject[] records)
    {
        var answer = await HTTP.PostAsync(Url("v1/logs"),
                                          new StringContent(TelemetryCollectorTests.Envelope(records).ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    private string Url(string path) => $"http://127.0.0.1:{_port}/{path}";

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

    private Task<string> DeclareAsync(OrchestratorPhases phases, string runId, string phase, string client = "claude-code",
                                      string? threadId = null) =>
        OrchestratorCall.InvokeAsync(phases, _repo, runId, client, threadId, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, $"entering {phase}", CancellationToken.None, phase: phase)).AsTask();

    private static async Task SendAsync(Process server, JsonObject message)
    {
        await server.StandardInput.WriteLineAsync(message.ToJsonString());
        await server.StandardInput.FlushAsync();
    }

    private static async Task ReadAnswerAsync(Process server, int id)
    {
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            if (JsonNode.Parse(line)?["id"]?.GetValue<int>() == id) return;
        }
    }

    private RunDirectory Run(string runId) => RunDirectory.Open(_repo, runId);

    private JsonElement[] Records(string runId) =>
        [.. JsonDocument.Parse(File.ReadAllText(Path.Combine(Run(runId).Path, "telemetry.json"))).RootElement.EnumerateArray()];

    private JsonElement PhaseFile(string runId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME))).RootElement;

    private IReadOnlyList<JsonElement> LogEntries(string runId) =>
        [.. AtomicFile.Read(Run(runId).DiagnosticLogPath)
                      .Split('\n')
                      .Select(line => line.TrimEnd('\r'))
                      .Where(line => line.Length > 0)
                      .Select(line => JsonDocument.Parse(line).RootElement)];

    private IReadOnlyList<string> Failures(string runId) =>
        [.. LogEntries(runId).Where(entry => entry.GetProperty("event").GetString() == OrchestratorPhases.FAILED)
                             .Select(entry => entry.GetProperty("fields").GetProperty("kind").GetString()!)];

    private IReadOnlyList<JsonElement> Chosen(string runId) =>
        [.. LogEntries(runId).Where(entry => entry.GetProperty("event").GetString() == CHOSEN).Select(entry => entry.GetProperty("fields"))];
}
