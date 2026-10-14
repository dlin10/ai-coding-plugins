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
/// The transcript Usage source (R8, R10, R13) over hand-written Claude Code transcripts and Codex
/// rollouts in a test-owned config dir and home: every normalized counter asserted, and each failure
/// both as the source answers it and as a close through the phase owner records and logs it.
/// </summary>
public sealed class TranscriptUsageSourceTests : IDisposable
{
    private const string SESSION = "session-a";
    private const string THREAD = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset END = START.AddSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-transcript", Guid.NewGuid().ToString("n"));
    private readonly string _claude;
    private readonly string _codex;
    private readonly string _repo;
    private FileStream? _held;

    public TranscriptUsageSourceTests()
    {
        _claude = Path.Combine(_root, "claude");
        _codex = Path.Combine(_root, "codex");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        _held?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---- Claude Code ----

    [Fact]
    public async Task Claude_the_lines_of_one_response_are_counted_once_at_their_earliest_line()
    {
        // The second line of msg-1 falls after the closing; the response belongs where its first line is.
        WriteClaude(Assistant("msg-1", 10, input: 3, read: 100, creation: 20, output: 7),
                    Assistant("msg-1", 70, input: 3, read: 100, creation: 20, output: 7),
                    Assistant("msg-2", 20, input: 1, read: 10, creation: 0, output: 2));

        var reading = await ReadClaudeAsync();

        AssertTokens(reading, input: 134, read: 110, creation: 20, output: 9, reasoning: null);
        Assert.False(reading.Incomplete);
    }

    [Fact]
    public async Task Claude_records_before_and_after_the_window_are_not_counted()
    {
        WriteClaude(Assistant("msg-0", -5, input: 1000, read: 1000, creation: 1000, output: 1000),
                    Assistant("msg-1", 0, input: 1, read: 2, creation: 3, output: 4),
                    Assistant("msg-2", 60, input: 1000, read: 1000, creation: 1000, output: 1000));

        AssertTokens(await ReadClaudeAsync(), input: 6, read: 2, creation: 3, output: 4, reasoning: null);
    }

    [Fact]
    public async Task Claude_a_synthetic_record_is_skipped()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4),
                    Assistant("msg-2", 20, input: 50, read: 50, creation: 50, output: 50, model: "<synthetic>"));

        AssertTokens(await ReadClaudeAsync(), input: 6, read: 2, creation: 3, output: 4, reasoning: null);
    }

    [Fact]
    public async Task Claude_a_subagent_file_is_counted()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));
        var subagents = Path.Combine(_claude, "projects", "C--repo", SESSION, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-1.jsonl"),
                          Assistant("msg-9", 30, input: 10, read: 20, creation: 30, output: 40) + "\n");

        AssertTokens(await ReadClaudeAsync(), input: 66, read: 22, creation: 33, output: 44, reasoning: null);
    }

    [Fact]
    public async Task Claude_a_partial_last_line_is_ignored()
    {
        var partial = Assistant("msg-2", 20, input: 9, read: 9, creation: 9, output: 9)[..40];
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4), partial);

        AssertTokens(await ReadClaudeAsync(), input: 6, read: 2, creation: 3, output: 4, reasoning: null);
    }

    [Fact]
    public async Task Claude_a_missing_transcript_answers_source_failed()
    {
        Directory.CreateDirectory(Path.Combine(_claude, "projects", "C--repo"));

        AssertFailed(await ReadClaudeAsync(), "source_failed");
    }

    [Fact]
    public async Task Claude_thinking_tokens_absent_leave_reasoning_absent()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));

        var reading = await ReadClaudeAsync();

        AssertTokens(reading, input: 6, read: 2, creation: 3, output: 4, reasoning: null);
        Assert.Null(reading.Tokens!.CostUsd);
    }

    [Fact]
    public async Task Claude_thinking_tokens_present_are_reasoning()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 40, thinking: 25),
                    Assistant("msg-2", 20, input: 1, read: 2, creation: 3, output: 10, thinking: 5));

        AssertTokens(await ReadClaudeAsync(), input: 12, read: 4, creation: 6, output: 50, reasoning: 30);
    }

    [Fact]
    public async Task Claude_a_negative_counter_is_absent_and_incomplete_while_the_others_still_sum()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: -4),
                    Assistant("msg-2", 20, input: 1, read: 2, creation: 3, output: 4));

        var reading = await ReadClaudeAsync();

        AssertTokens(reading, input: 12, read: 4, creation: 6, output: null, reasoning: null);
        Assert.True(reading.Incomplete);
    }

    [Fact]
    public async Task Claude_counts_as_digit_strings_sum_and_a_missing_component_leaves_input_absent()
    {
        var digits = Line(10, """{"type":"assistant","message":{"id":"msg-1","model":"claude","usage":{"input_tokens":"5","cache_read_input_tokens":"6","cache_creation_input_tokens":7,"output_tokens":"8"}}}""");
        var noCreation = Line(20, """{"type":"assistant","message":{"id":"msg-2","model":"claude","usage":{"input_tokens":1,"cache_read_input_tokens":1,"output_tokens":1}}}""");
        WriteClaude(digits, noCreation);

        var reading = await ReadClaudeAsync();

        AssertTokens(reading, input: null, read: 7, creation: 7, output: 9, reasoning: null);
        Assert.True(reading.Incomplete);
    }

    [Fact]
    public async Task Claude_a_sum_past_the_largest_count_is_absent_and_incomplete()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: long.MaxValue),
                    Assistant("msg-2", 20, input: 1, read: 2, creation: 3, output: 1));

        var reading = await ReadClaudeAsync();

        AssertTokens(reading, input: 12, read: 4, creation: 6, output: null, reasoning: null);
        Assert.True(reading.Incomplete);
    }

    // ---- Codex ----

    [Fact]
    public async Task Codex_events_before_inside_and_after_the_window()
    {
        WriteCodex(TokenCount(-10, input: 100, cached: 50, output: 10, reasoning: 5),
                   TokenCount(10, input: 160, cached: 80, output: 15, reasoning: 7),
                   TokenCount(30, input: 200, cached: 90, output: 25, reasoning: 9),
                   TokenCount(70, input: 900, cached: 900, output: 900, reasoning: 900));

        var reading = await ReadCodexAsync();

        AssertTokens(reading, input: 100, read: 40, creation: null, output: 15, reasoning: 4);
        Assert.False(reading.Incomplete);
    }

    [Fact]
    public async Task Codex_with_no_event_before_the_opening_the_first_counts_from_zero()
    {
        WriteCodex(TokenCount(10, input: 160, cached: 80, output: 15, reasoning: 7),
                   TokenCount(30, input: 200, cached: 90, output: 25, reasoning: 9));

        AssertTokens(await ReadCodexAsync(), input: 200, read: 90, creation: null, output: 25, reasoning: 9);
    }

    [Fact]
    public async Task Codex_repeated_totals_count_once()
    {
        WriteCodex(TokenCount(-10, input: 100, cached: 50, output: 10, reasoning: 5),
                   TokenCount(10, input: 150, cached: 60, output: 12, reasoning: 6),
                   TokenCount(20, input: 150, cached: 60, output: 12, reasoning: 6),
                   TokenCount(30, input: 150, cached: 60, output: 12, reasoning: 6));

        AssertTokens(await ReadCodexAsync(), input: 50, read: 10, creation: null, output: 2, reasoning: 1);
    }

    [Fact]
    public async Task Codex_a_total_that_falls_inside_the_window_counts_from_zero_and_is_incomplete()
    {
        WriteCodex(TokenCount(-10, input: 1000, cached: 500, output: 100, reasoning: 50),
                   TokenCount(10, input: 40, cached: 20, output: 4, reasoning: 2),
                   TokenCount(30, input: 70, cached: 30, output: 9, reasoning: 3));

        var reading = await ReadCodexAsync();

        AssertTokens(reading, input: 70, read: 30, creation: null, output: 9, reasoning: 3);
        Assert.True(reading.Incomplete);
    }

    [Fact]
    public async Task Codex_cache_write_tokens_carried_by_some_event_count_and_carried_by_none_are_omitted()
    {
        WriteCodex(TokenCount(10, input: 10, cached: 0, output: 1, reasoning: 0, cacheWrite: 3),
                   TokenCount(20, input: 30, cached: 5, output: 2, reasoning: 0));

        AssertTokens(await ReadCodexAsync(), input: 30, read: 5, creation: 3, output: 2, reasoning: 0);

        WriteCodex(TokenCount(10, input: 10, cached: 0, output: 1, reasoning: 0));
        AssertTokens(await ReadCodexAsync(), input: 10, read: 0, creation: null, output: 1, reasoning: 0);
    }

    [Fact]
    public async Task Codex_a_phase_with_no_event_counts_zero_for_the_counters_the_rollout_carries()
    {
        WriteCodex(TokenCount(-10, input: 100, cached: 50, output: 10, reasoning: 5),
                   Line(20, """{"type":"event_msg","payload":{"type":"token_count","info":null}}"""),
                   TokenCount(90, input: 300, cached: 60, output: 30, reasoning: 6));

        var reading = await ReadCodexAsync();

        AssertTokens(reading, input: 0, read: 0, creation: null, output: 0, reasoning: 0);
        Assert.False(reading.Incomplete);
        Assert.Null(reading.Missing);
    }

    // ---- The wait before a read, through the phase owner ----

    [Fact]
    public async Task Claude_a_line_written_during_the_wait_is_counted_in_the_closing_phase()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));
        var clock = new TestClock();
        clock.OnTimer = () => AppendClaude(Assistant("msg-2", 59, input: 10, read: 20, creation: 30, output: 40));

        var (record, _, _) = await CloseThroughOwnerAsync(clock, new TranscriptUsageSource(_claude, _codex), codex: false);

        Assert.Equal(1, clock.Timers);
        AssertRecord(record, input: 66, read: 22, creation: 33, output: 44, reasoning: null);
    }

    [Fact]
    public async Task Codex_a_line_written_during_the_wait_is_counted_in_the_closing_phase()
    {
        WriteCodex(TokenCount(10, input: 10, cached: 2, output: 1, reasoning: 0));
        var clock = new TestClock();
        clock.OnTimer = () => File.AppendAllText(Rollout(), TokenCount(59, input: 25, cached: 7, output: 3, reasoning: 1) + "\n");

        var (record, _, _) = await CloseThroughOwnerAsync(clock, new TranscriptUsageSource(_claude, _codex), codex: true);

        Assert.Equal(1, clock.Timers);
        AssertRecord(record, input: 25, read: 7, creation: null, output: 3, reasoning: 1);
    }

    // ---- Failures: as the source answers, and as a close records and logs them ----

    [Fact]
    public async Task Claude_a_transcript_held_without_sharing_fails()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));
        _held = new FileStream(ClaudeTranscript(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: false, "source_failed");
    }

    [Fact]
    public async Task Codex_a_rollout_held_without_sharing_fails()
    {
        WriteCodex(TokenCount(10, input: 10, cached: 2, output: 1, reasoning: 0));
        _held = new FileStream(Rollout(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: true, "source_failed");
    }

    [Fact]
    public async Task Claude_a_malformed_line_that_is_not_the_last_fails()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4), "{ not json",
                    Assistant("msg-2", 20, input: 1, read: 2, creation: 3, output: 4));

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: false, "source_failed");
    }

    [Fact]
    public async Task Codex_a_malformed_line_that_is_not_the_last_fails()
    {
        WriteCodex(TokenCount(10, input: 10, cached: 2, output: 1, reasoning: 0), "{ not json",
                   TokenCount(20, input: 20, cached: 2, output: 1, reasoning: 0));

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: true, "source_failed");
    }

    [Fact]
    public async Task Claude_a_read_past_its_deadline_times_out()
    {
        WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));

        await AssertFailureAsync(Stalled(), codex: false, "source_timeout");
    }

    [Fact]
    public async Task Codex_a_read_past_its_deadline_times_out()
    {
        WriteCodex(TokenCount(10, input: 10, cached: 2, output: 1, reasoning: 0));

        await AssertFailureAsync(Stalled(), codex: true, "source_timeout");
    }

    [Fact]
    public async Task Claude_a_phase_whose_every_counter_is_invalid_records_source_failed_with_one_warning()
    {
        var usage = """{"input_tokens":-1,"cache_read_input_tokens":1.5,"cache_creation_input_tokens":true,"output_tokens":null}""";
        WriteClaude(Line(10, """{"type":"assistant","message":{"id":"msg-1","model":"claude-opus","usage":""" + usage + "}}"));

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: false, "source_failed");
    }

    [Fact]
    public async Task Codex_a_phase_whose_every_total_is_invalid_records_source_failed_with_one_warning()
    {
        var totals = """{"input_tokens":-1,"cached_input_tokens":1.5,"cache_write_input_tokens":true,"output_tokens":null,"reasoning_output_tokens":"x"}""";
        WriteCodex(Line(10, """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":""" + totals + "}}}"));

        await AssertFailureAsync(new TranscriptUsageSource(_claude, _codex), codex: true, "source_failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_whose_synchronous_work_blocks_times_out_at_its_deadline(bool codex)
    {
        if (codex) WriteCodex(TokenCount(10, input: 10, cached: 2, output: 1, reasoning: 0));
        else WriteClaude(Assistant("msg-1", 10, input: 1, read: 2, creation: 3, output: 4));

        // A reader that blocks its thread and never looks at the deadline, as parsing a large file would.
        using var release = new ManualResetEventSlim();
        var blocked = new TranscriptUsageSource(_claude, _codex, (path, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return Task.FromResult(File.ReadAllText(path));
        }, TimeSpan.FromMilliseconds(200));

        try
        {
            var session = codex ? new HostSession(HostSessions.CODEX, THREAD) : new HostSession(HostSessions.CLAUDE_CODE, SESSION);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            AssertFailed(await blocked.ReadAsync(new UsageWindow(session, START, END, null), CancellationToken.None), "source_timeout");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());

            await AssertFailureAsync(blocked, codex, "source_timeout");
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
        }
    }

    // ---- Helpers ----

    private TranscriptUsageSource Stalled() =>
        new(_claude, _codex, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        }, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The source answers the failure with absent counters and its kind, and a close through the phase
    /// owner records it as <c>source_failed</c>, writes one <c>warn</c> of that kind and returns what it
    /// returns without the failure.
    /// </summary>
    /// <param name="source">The source, set up to fail.</param>
    /// <param name="codex">Whether the Host session is a Codex thread.</param>
    /// <param name="kind">The failure's kind.</param>
    private async Task AssertFailureAsync(TranscriptUsageSource source, bool codex, string kind)
    {
        var session = codex ? new HostSession(HostSessions.CODEX, THREAD) : new HostSession(HostSessions.CLAUDE_CODE, SESSION);
        AssertFailed(await source.ReadAsync(new UsageWindow(session, START, END, null), CancellationToken.None), kind);

        var (record, warnings, result) = await CloseThroughOwnerAsync(new TestClock(), source, codex);

        Assert.Equal("transcript", record.GetProperty("usageSource").GetString());
        Assert.Equal("source_failed", record.GetProperty("usageMissing").GetString());
        foreach (var counter in new[] { "inputTokens", "cacheReadTokens", "cacheCreationTokens", "outputTokens", "reasoningTokens" })
            Assert.False(record.TryGetProperty(counter, out _), counter);

        var warning = Assert.Single(warnings);
        Assert.Equal("warn", warning.GetProperty("level").GetString());
        Assert.Equal("orchestrator.telemetry.failed", warning.GetProperty("event").GetString());
        Assert.Equal(kind, warning.GetProperty("fields").GetProperty("kind").GetString());
    }

    /// <summary>
    /// Opens <c>interview</c> with <c>forge.begin</c> at <see cref="START"/> and closes it with a
    /// <c>done</c> declaration at <see cref="END"/>, through the phase owner over <paramref name="source"/>.
    /// </summary>
    /// <param name="clock">The owner's clock; its timers fire at once.</param>
    /// <param name="source">The transcript source.</param>
    /// <param name="codex">Whether the host is Codex, carrying a thread, rather than Claude Code.</param>
    private async Task<(JsonElement Record, JsonElement[] Warnings, string Result)> CloseThroughOwnerAsync(TestClock clock,
                                                                                                         TranscriptUsageSource source,
                                                                                                         bool codex)
    {
        await InitRepoAsync();
        var client = codex ? "codex" : "claude-code";
        var thread = codex ? THREAD : null;
        var phases = new OrchestratorPhases(clock, source, name => name == "CLAUDE_CODE_SESSION_ID" ? SESSION : null);

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, client, thread, async () =>
            await ForgeTools.Begin(SessionRoots.None, client, CapabilityProfile.Text, _ => { }, _repo, CancellationToken.None));
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;
        var run = RunDirectory.Open(_repo, runId);

        clock.Advance(END - START);
        var result = await OrchestratorCall.InvokeAsync(phases, _repo, runId, client, thread, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "a note", CancellationToken.None, phase: "done"));
        _held?.Dispose();
        _held = null;

        Assert.Equal(run.DiagnosticLogPath, result);
        var record = Assert.Single(JsonDocument.Parse(File.ReadAllText(run.TelemetryPath)).RootElement.EnumerateArray());
        Assert.Equal("interview", record.GetProperty("phase").GetString());
        Assert.Equal(codex ? THREAD : SESSION, record.GetProperty("hostSessionId").GetString());
        Assert.Equal("00:01:00", record.GetProperty("wallDuration").GetString());

        var warnings = AtomicFile.Read(run.DiagnosticLogPath)
                                 .Split('\n')
                                 .Select(line => line.TrimEnd('\r'))
                                 .Where(line => line.Length > 0)
                                 .Select(line => JsonDocument.Parse(line).RootElement)
                                 .Where(entry => entry.GetProperty("source").GetString() == "telemetry")
                                 .ToArray();
        return (record, warnings, result);
    }

    private async Task InitRepoAsync()
    {
        var git = new GitClient(_repo);
        var ct = CancellationToken.None;
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);
    }

    private Task<UsageReading> ReadClaudeAsync() =>
        new TranscriptUsageSource(_claude, _codex).ReadAsync(
            new UsageWindow(new HostSession(HostSessions.CLAUDE_CODE, SESSION), START, END, null), CancellationToken.None);

    private Task<UsageReading> ReadCodexAsync() =>
        new TranscriptUsageSource(_claude, _codex).ReadAsync(
            new UsageWindow(new HostSession(HostSessions.CODEX, THREAD), START, END, null), CancellationToken.None);

    private string ClaudeTranscript() => Path.Combine(_claude, "projects", "C--repo", SESSION + ".jsonl");

    private string Rollout() => Path.Combine(_codex, "sessions", "2026", "10", "14", $"rollout-2026-10-14T09-00-00-{THREAD}.jsonl");

    private void WriteClaude(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeTranscript())!);

        // Another session's transcript in the same folder is never read.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ClaudeTranscript())!, "session-b.jsonl"),
                          Assistant("msg-x", 10, input: 999, read: 999, creation: 999, output: 999) + "\n");
        File.WriteAllText(ClaudeTranscript(), string.Join("\n", lines) + (lines[^1].EndsWith('}') ? "\n" : ""));
    }

    private void AppendClaude(string line) => File.AppendAllText(ClaudeTranscript(), line + "\n");

    private void WriteCodex(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Rollout())!);
        File.WriteAllText(Rollout(), string.Join("\n", lines) + "\n");
    }

    private static string Assistant(string id, int second, long input, long read, long creation, long output,
                                    long? thinking = null, string model = "claude-opus")
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = input,
            ["cache_read_input_tokens"] = read,
            ["cache_creation_input_tokens"] = creation,
            ["output_tokens"] = output,
        };
        if (thinking is { } value) usage["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = value };

        var message = new JsonObject { ["id"] = id, ["model"] = model, ["role"] = "assistant", ["usage"] = usage };
        return Line(second, new JsonObject { ["type"] = "assistant", ["message"] = message }.ToJsonString());
    }

    private static string TokenCount(int second, long input, long cached, long output, long reasoning, long? cacheWrite = null)
    {
        var totals = new JsonObject
        {
            ["input_tokens"] = input,
            ["cached_input_tokens"] = cached,
            ["output_tokens"] = output,
            ["reasoning_output_tokens"] = reasoning,
            ["total_tokens"] = input + output,
        };
        if (cacheWrite is { } value) totals["cache_write_input_tokens"] = value;

        var payload = new JsonObject { ["type"] = "token_count", ["info"] = new JsonObject { ["total_token_usage"] = totals } };
        return Line(second, new JsonObject { ["type"] = "event_msg", ["payload"] = payload }.ToJsonString());
    }

    /// <summary>A JSONL object with its <c>timestamp</c> set <paramref name="second"/> seconds after <see cref="START"/>.</summary>
    /// <param name="second">Seconds after the phase's opening.</param>
    /// <param name="json">The object, without a timestamp.</param>
    private static string Line(int second, string json) =>
        $$"""{"timestamp":"{{START.AddSeconds(second).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}",""" + json[1..];

    private static void AssertTokens(UsageReading reading, long? input, long? read, long? creation, long? output, long? reasoning)
    {
        var tokens = Assert.IsType<OrchestratorTokens>(reading.Tokens);
        Assert.Null(reading.FailureKind);
        Assert.Equal(input, tokens.InputTokens);
        Assert.Equal(read, tokens.CacheReadTokens);
        Assert.Equal(creation, tokens.CacheCreationTokens);
        Assert.Equal(output, tokens.OutputTokens);
        Assert.Equal(reasoning, tokens.ReasoningTokens);
        Assert.Null(tokens.CostUsd);
    }

    private static void AssertFailed(UsageReading reading, string kind)
    {
        Assert.Null(reading.Tokens);
        Assert.Equal("source_failed", reading.Missing);
        Assert.Equal(kind, reading.FailureKind);
    }

    private static void AssertRecord(JsonElement record, long? input, long? read, long? creation, long? output, long? reasoning)
    {
        Assert.Equal("transcript", record.GetProperty("usageSource").GetString());
        Assert.False(record.TryGetProperty("usageMissing", out _));
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.False(record.TryGetProperty("costUsd", out _));
        foreach (var (name, expected) in new[] { ("inputTokens", input), ("cacheReadTokens", read), ("cacheCreationTokens", creation),
                                                 ("outputTokens", output), ("reasoningTokens", reasoning) })
        {
            if (expected is null) Assert.False(record.TryGetProperty(name, out _), name);
            else Assert.Equal(expected, record.GetProperty(name).GetInt64());
        }
    }

    /// <summary>
    /// A clock that moves only when a test moves it, in UTC; a timer it creates runs
    /// <see cref="OnTimer"/> — what the host writes while the close waits — and then fires at once.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = START.UtcTicks;
        private int _timers;

        public Action? OnTimer { get; set; }

        public int Timers => Volatile.Read(ref _timers);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            OnTimer?.Invoke();
            _ = Task.Run(() => callback(state));
            return new SilentTimer();
        }

        private sealed class SilentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
