using System.Globalization;
using System.Net;
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
/// Attributing usage to phases (R2, R7, R8): two adjacent phases — A, opened by <c>forge.begin</c> at
/// T0, and B, from the boundary at T0 + 60 s to its closing at T0 + 120 s — given the same usage through
/// each of the four formats, every record read back from <c>telemetry.json</c> after a close through the
/// phase owner and its real source. The expectation tables are written from the requirements, not
/// derived from the code's interval test. No test reads a real profile or environment variable.
/// </summary>
public sealed class UsageAttributionMatrixTests : IAsyncLifetime
{
    private const int BOUNDARY = 60;
    private const int CLOSING = 120;

    /// <summary>
    /// The formats in which a response spans time: Claude transcript lines, an exported Claude request's
    /// duration, or an exported Codex request start and its completed response.
    /// </summary>
    private static readonly Format[] STRADDLING = [Format.ClaudeTranscript, Format.ClaudeExport, Format.CodexExport];

    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient HTTP = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Axis B: when a single usage happens, in seconds after A's opening, and where R2 puts it.</summary>
    private static readonly Dictionary<UsageTime, (int Second, Place Place)> TIMES = new()
    {
        [UsageTime.BeforeA] = (-5, Place.Outside),
        [UsageTime.AtAOpening] = (0, Place.A),
        [UsageTime.InsideA] = (30, Place.A),
        [UsageTime.AtBoundary] = (BOUNDARY, Place.B),
        [UsageTime.InsideB] = (90, Place.B),
        [UsageTime.AtBClosing] = (CLOSING, Place.Outside),
        [UsageTime.AfterB] = (125, Place.Outside),
    };

    /// <summary>
    /// Axis C: a Claude response whose earliest line is one second before the crossing point and whose
    /// other line is one second after; R8 places it by its earliest line, so before the crossing point.
    /// Exported, it is a request that began one second before and ended one second after — for Claude by
    /// its duration, for Codex as a request start and its completed response — and R7 places it by its
    /// start, so in the same phase.
    /// </summary>
    private static readonly Dictionary<Crossing, (int Second, Place Place)> CROSSINGS = new()
    {
        [Crossing.AOpening] = (0, Place.Outside),
        [Crossing.Boundary] = (BOUNDARY, Place.A),
        [Crossing.BClosing] = (CLOSING, Place.B),
    };

    /// <summary>Axis D: each history of <c>cache_write_input_tokens</c> for phase A alone, with A's expectation from R8.</summary>
    private static readonly Dictionary<CacheWriteHistory, Rollout> HISTORIES = new()
    {
        [CacheWriteHistory.EveryEvent] = new([(-10, 10), (10, 12), (20, 15)], CacheWriteInA: 5, InputInA: 200, OutputInA: 2, Incomplete: false),
        [CacheWriteHistory.MissingInside] = new([(-10, 10), (10, null), (20, 15)], CacheWriteInA: 5, InputInA: 200, OutputInA: 2, Incomplete: false),
        [CacheWriteHistory.MissingFromLastBeforeOpening] = new([(-20, 10), (-10, null), (10, 15)], CacheWriteInA: 5, InputInA: 100, OutputInA: 1,
                                                               Incomplete: false),
        [CacheWriteHistory.MissingEverywhere] = new([(-10, null), (10, null), (20, null)], CacheWriteInA: null, InputInA: 200, OutputInA: 2,
                                                    Incomplete: false),
        [CacheWriteHistory.Falling] = new([(-10, 10), (10, 12), (20, 4)], CacheWriteInA: 6, InputInA: 200, OutputInA: 2, Incomplete: true),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-attribution", Guid.NewGuid().ToString("n"));
    private readonly string _repo;
    private readonly string _claude;
    private readonly string _codex;
    private readonly string _session = "session-" + Guid.NewGuid().ToString("n");
    private readonly string _thread = Guid.NewGuid().ToString();
    private TelemetryCollectorTests.Running? _collector;

    public UsageAttributionMatrixTests()
    {
        _repo = Path.Combine(_root, "repo");
        _claude = Path.Combine(_root, "claude");
        _codex = Path.Combine(_root, "codex");
    }

    /// <summary>Axis A: the source format.</summary>
    public enum Format { ClaudeTranscript, CodexRollout, ClaudeExport, CodexExport }

    /// <summary>Axis B: the usage's time.</summary>
    public enum UsageTime { BeforeA, AtAOpening, InsideA, AtBoundary, InsideB, AtBClosing, AfterB }

    /// <summary>Axis C's straddling shapes: the crossing point a two-line Claude response spans.</summary>
    public enum Crossing { AOpening, Boundary, BClosing }

    /// <summary>Axis D: how <c>cache_write_input_tokens</c> appears across a Codex rollout's events.</summary>
    public enum CacheWriteHistory { EveryEvent, MissingInside, MissingFromLastBeforeOpening, MissingEverywhere, Falling }

    private enum Place { Outside, A, B }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_repo);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_collector is not null) await _collector.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static TheoryData<Format, UsageTime> SingleUsages()
    {
        var data = new TheoryData<Format, UsageTime>();
        foreach (var format in Enum.GetValues<Format>())
        {
            foreach (var time in Enum.GetValues<UsageTime>()) data.Add(format, time);
        }

        return data;
    }

    public static TheoryData<Format, Crossing> Crossings()
    {
        var data = new TheoryData<Format, Crossing>();
        foreach (var format in STRADDLING)
        {
            foreach (var crossing in Enum.GetValues<Crossing>()) data.Add(format, crossing);
        }

        return data;
    }

    public static TheoryData<CacheWriteHistory> Histories()
    {
        var data = new TheoryData<CacheWriteHistory>();
        foreach (var history in Enum.GetValues<CacheWriteHistory>()) data.Add(history);
        return data;
    }

    public static TheoryData<Format> Formats()
    {
        var data = new TheoryData<Format>();
        foreach (var format in Enum.GetValues<Format>()) data.Add(format);
        return data;
    }

    // ---- Axes A × B: one record or event ----

    [Theory]
    [MemberData(nameof(SingleUsages))]
    public async Task A_single_usage_counts_only_in_the_phase_whose_half_open_interval_holds_its_time(Format format, UsageTime time)
    {
        var (second, place) = TIMES[time];

        var (a, b) = await RunAsync(format, [new Usage([second], 1, place)]);

        AssertCounters(format, a, place == Place.A ? 1 : 0);
        AssertCounters(format, b, place == Place.B ? 1 : 0);
    }

    // ---- Axis C: a Claude response straddling a crossing point, in either Claude format ----

    [Theory]
    [MemberData(nameof(Crossings))]
    public async Task A_straddling_Claude_response_counts_once_in_the_phase_of_its_earliest_line(Format format, Crossing crossing)
    {
        var (second, place) = CROSSINGS[crossing];

        var (a, b) = await RunAsync(format, [new Usage([second - 1, second + 1], 1, place)]);

        AssertCounters(format, a, place == Place.A ? 1 : 0);
        AssertCounters(format, b, place == Place.B ? 1 : 0);
    }

    // ---- Axis D: a Codex rollout counter's history, for phase A ----

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task A_rollout_counter_grows_from_its_own_last_event_before_the_opening(CacheWriteHistory history)
    {
        var rollout = HISTORIES[history];
        WriteRollout(rollout.Events.Select((e, index) => (e.Second, Totals(input: 100 * (index + 1), cached: 0, cacheWrite: e.CacheWrite,
                                                                                output: index + 1, reasoning: 0))));

        var (a, b) = await PhasesAsync(START, codex: true, Transcript());

        AssertRecord(a, new OrchestratorTokens(rollout.InputInA, 0, rollout.CacheWriteInA, rollout.OutputInA, 0), rollout.Incomplete);

        // A counter the rollout carries but no event within B carries counts zero; one it never carries is omitted.
        var carried = rollout.Events.Any(e => e.CacheWrite is not null) ? 0L : (long?)null;
        AssertRecord(b, new OrchestratorTokens(0, 0, carried, 0, 0), incomplete: false);
    }

    // ---- Nothing counted twice ----

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Phase_A_plus_phase_B_plus_the_usage_outside_both_is_the_whole_usage(Format format)
    {
        // Each usage a distinct power of ten, so a usage counted twice, or lost, shows in the sums.
        var usages = new List<Usage>();
        var multiplier = 1L;
        foreach (var (second, place) in TIMES.Values)
        {
            usages.Add(new Usage([second], multiplier, place));
            multiplier *= 10;
        }

        // These straddlers overlap the single usages at the same crossing points, which a Codex thread,
        // whose requests run one at a time, never does: a response pairs with its thread's latest start.
        if (STRADDLING.Contains(format) && format != Format.CodexExport)
        {
            foreach (var (second, place) in CROSSINGS.Values)
            {
                usages.Add(new Usage([second - 1, second + 1], multiplier, place));
                multiplier *= 10;
            }
        }

        var (a, b) = await RunAsync(format, usages);

        long Sum(Place place) => usages.Where(usage => usage.Place == place).Sum(usage => usage.Multiplier);
        AssertCounters(format, a, Sum(Place.A));
        AssertCounters(format, b, Sum(Place.B));

        var whole = Expected(format, usages.Sum(usage => usage.Multiplier));
        var outside = Expected(format, Sum(Place.Outside));
        foreach (var (name, counter) in Counters)
        {
            if (counter(whole) is not { } total) continue;
            Assert.Equal(total, a.GetProperty(name).GetInt64() + b.GetProperty(name).GetInt64() + counter(outside)!.Value);
        }

        if (whole.CostUsd is { } cost)
            Assert.Equal(cost, a.GetProperty("costUsd").GetDouble() + b.GetProperty("costUsd").GetDouble() + outside.CostUsd!.Value);
    }

    // ---- The matrix itself ----

    [Fact]
    public void Every_axis_value_occurs_and_every_expectation_table_covers_its_axis()
    {
        var single = SingleUsages().Select(row => ((Format)row[0], (UsageTime)row[1])).Distinct().ToList();
        Assert.Equal(Enum.GetValues<Format>().Length * Enum.GetValues<UsageTime>().Length, single.Count);
        Assert.Equal(STRADDLING.Length * Enum.GetValues<Crossing>().Length,
                     ((IEnumerable<object[]>)Crossings()).Select(row => ((Format)row[0], (Crossing)row[1])).Distinct().Count());
        Assert.Equal(Enum.GetValues<CacheWriteHistory>(), ((IEnumerable<object[]>)Histories()).Select(row => (CacheWriteHistory)row[0]));
        Assert.Equal(Enum.GetValues<Format>(), ((IEnumerable<object[]>)Formats()).Select(row => (Format)row[0]));

        Assert.Equal(Enum.GetValues<UsageTime>(), TIMES.Keys.Order());
        Assert.Equal(Enum.GetValues<Crossing>(), CROSSINGS.Keys.Order());
        Assert.Equal(Enum.GetValues<CacheWriteHistory>(), HISTORIES.Keys.Order());

        // Every place a usage can land is expected somewhere on Axes B and C.
        Assert.Equal(Enum.GetValues<Place>(), TIMES.Values.Select(t => t.Place).Distinct().Order());
        Assert.Equal(Enum.GetValues<Place>(), CROSSINGS.Values.Select(c => c.Place).Distinct().Order());
    }

    // ---- Helpers ----

    private static readonly (string Name, Func<OrchestratorTokens, long?> Counter)[] Counters =
    [
        ("inputTokens", t => t.InputTokens), ("cacheReadTokens", t => t.CacheReadTokens), ("cacheCreationTokens", t => t.CacheCreationTokens),
        ("outputTokens", t => t.OutputTokens), ("reasoningTokens", t => t.ReasoningTokens),
    ];

    /// <summary>
    /// One unit of usage in each format, times <paramref name="multiplier"/>, normalized as R7 and R8 say;
    /// a phase that holds none of the usage counts zero for every counter the format carries.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <param name="multiplier">How many units.</param>
    private static OrchestratorTokens Expected(Format format, long multiplier) => format switch
    {
        // input 1 + cache read 20 + cache creation 300.
        Format.ClaudeTranscript => new(321 * multiplier, 20 * multiplier, 300 * multiplier, 4000 * multiplier, 50000 * multiplier),
        Format.ClaudeExport => new(321 * multiplier, 20 * multiplier, 300 * multiplier, 4000 * multiplier, null, 0.125 * multiplier),
        _ => new(1000 * multiplier, 200 * multiplier, 30 * multiplier, 400 * multiplier, 5 * multiplier),
    };

    private static void AssertCounters(Format format, JsonElement record, long multiplier) =>
        AssertRecord(record, Expected(format, multiplier), incomplete: false);

    private static void AssertRecord(JsonElement record, OrchestratorTokens expected, bool incomplete)
    {
        Assert.False(record.TryGetProperty("usageMissing", out _));
        Assert.Equal(incomplete, record.TryGetProperty("usageIncomplete", out var flag) && flag.GetBoolean());
        foreach (var (name, counter) in Counters)
        {
            if (counter(expected) is { } value) Assert.Equal(value, record.GetProperty(name).GetInt64());
            else Assert.False(record.TryGetProperty(name, out _), name);
        }

        if (expected.CostUsd is { } cost) Assert.Equal(cost, record.GetProperty("costUsd").GetDouble());
        else Assert.False(record.TryGetProperty("costUsd", out _));
    }

    /// <summary>Writes the usages in the format's own files or collector, then runs A and B over them.</summary>
    /// <param name="format">The format.</param>
    /// <param name="usages">The usages; each line or event is written before any close.</param>
    private async Task<(JsonElement A, JsonElement B)> RunAsync(Format format, IReadOnlyList<Usage> usages)
    {
        switch (format)
        {
            case Format.ClaudeTranscript:
                WriteClaude(usages);
                return await PhasesAsync(START, codex: false, Transcript());

            case Format.CodexRollout:
            {
                // A zero baseline long before, then each usage as the growth of the running totals.
                var events = new List<(int Second, JsonObject Totals)> { (-30, Totals(0, 0, 0, 0, 0)) };
                var running = 0L;
                foreach (var usage in usages.OrderBy(usage => usage.Seconds[0]))
                {
                    running += usage.Multiplier;
                    events.Add((usage.Seconds[0], Totals(1000 * running, 200 * running, 30 * running, 400 * running, 5 * running)));
                }

                WriteRollout(events);
                return await PhasesAsync(START, codex: true, Transcript());
            }

            default:
                return await ExportAsync(format == Format.CodexExport, usages);
        }
    }

    /// <summary>
    /// Registers the session with a collector of the test's own, posts one export event per usage, and
    /// runs A and B from ten seconds after the registration, so the collector keeps an event from before
    /// A's opening as well.
    /// </summary>
    /// <param name="codex">Whether the events are Codex's rather than Claude Code's.</param>
    /// <param name="usages">The usages.</param>
    private async Task<(JsonElement A, JsonElement B)> ExportAsync(bool codex, IReadOnlyList<Usage> usages)
    {
        _collector = TelemetryCollectorTests.Running.Start();
        var source = new TelemetryUsageSource(new ExportEndpoint("127.0.0.1", _collector.Port, TimeSpan.Zero),
                                              _ => new CollectorStart(CollectorStartOutcome.Failed),
                                              TelemetryCollectorTests.SlowTimers.Instance);
        var session = codex ? new HostSession(HostSessions.CODEX, _thread) : new HostSession(HostSessions.CLAUDE_CODE, _session);
        var registeredAt = await source.RegisterAsync(session, CancellationToken.None);
        var t0 = new DateTimeOffset(registeredAt.UtcTicks - registeredAt.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero).AddSeconds(10);

        // A request starts at its first second and, when it straddles, ends at its last: a Claude request
        // by its duration, a Codex one as its request start and its completed response.
        var events = usages.SelectMany(usage => codex
                                           ? [TelemetryCollectorTests.CodexRequestStart(_thread, t0.AddSeconds(usage.Seconds[0])),
                                              CodexEvent(t0.AddSeconds(usage.Seconds[^1]), usage.Multiplier)]
                                           : new[]
                                           {
                                               ClaudeEvent(t0.AddSeconds(usage.Seconds[0]), usage.Multiplier,
                                                           usage.Seconds.Length > 1 ? (usage.Seconds[^1] - usage.Seconds[0]) * 1000L : 1200),
                                           })
                           .ToArray();
        using var answer = await HTTP.PostAsync(_collector.Url("v1/logs"),
                                                new StringContent(TelemetryCollectorTests.Envelope(events).ToJsonString(), Encoding.UTF8,
                                                                  "application/json"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(usages.Count, _collector.Collector.Kept().Count);

        return await PhasesAsync(t0, codex, new SlowDeadline(source));
    }

    private JsonObject ClaudeEvent(DateTimeOffset at, long multiplier, long durationMs) =>
        TelemetryCollectorTests.ClaudeRequest(_session, at,
                                              input: Int(1 * multiplier), cacheRead: Int(20 * multiplier), cacheCreation: Int(300 * multiplier),
                                              output: Int(4000 * multiplier), cost: new JsonObject { ["doubleValue"] = 0.125 * multiplier },
                                              durationMs: durationMs);

    private JsonObject CodexEvent(DateTimeOffset at, long multiplier) =>
        TelemetryCollectorTests.CodexCompleted(_thread, at,
                                               input: new JsonObject { ["stringValue"] = (1000 * multiplier).ToString(CultureInfo.InvariantCulture) },
                                               cached: Int(200 * multiplier), cacheWrite: Int(30 * multiplier),
                                               output: new JsonObject { ["stringValue"] = (400 * multiplier).ToString(CultureInfo.InvariantCulture) },
                                               reasoning: Int(5 * multiplier));

    private static JsonObject Int(long value) => new() { ["intValue"] = value };

    private TranscriptUsageSource Transcript() => new(_claude, _codex);

    /// <summary>
    /// Opens A with <c>forge.begin</c> at <paramref name="t0"/>, declares <c>planning</c> at the boundary
    /// and <c>done</c> at B's closing, through the phase owner over <paramref name="source"/>; answers the
    /// two records, after checking they are A and B, whole, from this source, and that nothing failed.
    /// </summary>
    /// <param name="t0">A's opening.</param>
    /// <param name="codex">Whether the host is Codex, carrying a thread, rather than Claude Code.</param>
    /// <param name="source">The source.</param>
    private async Task<(JsonElement A, JsonElement B)> PhasesAsync(DateTimeOffset t0, bool codex, IUsageSource source)
    {
        await InitRepoAsync();
        var clock = new TestClock(t0);
        var phases = new OrchestratorPhases(clock, source, name => name == "CLAUDE_CODE_SESSION_ID" ? _session : null);
        var client = codex ? "codex" : "claude-code";
        var thread = codex ? _thread : null;

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, client, thread, async () =>
            await ForgeTools.Begin(SessionRoots.None, client, CapabilityProfile.Text, _ => { }, _repo, CancellationToken.None));
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;

        foreach (var (phase, at) in new[] { ("planning", BOUNDARY), ("done", CLOSING) })
        {
            clock.Advance(t0.AddSeconds(at) - clock.GetUtcNow());
            await OrchestratorCall.InvokeAsync(phases, _repo, runId, client, thread, async () =>
                await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, $"entering {phase}", CancellationToken.None, phase: phase));
        }

        var run = RunDirectory.Open(_repo, runId);
        var records = JsonDocument.Parse(File.ReadAllText(run.TelemetryPath)).RootElement.EnumerateArray().ToArray();
        Assert.Equal(["interview", "planning"], records.Select(record => record.GetProperty("phase").GetString()!));
        foreach (var record in records)
        {
            Assert.Equal("00:01:00", record.GetProperty("wallDuration").GetString());
            Assert.Equal(codex ? _thread : _session, record.GetProperty("hostSessionId").GetString());
            Assert.Equal(source.Name, record.GetProperty("usageSource").GetString());
        }

        var failures = AtomicFile.Read(run.DiagnosticLogPath)
                                 .Split('\n')
                                 .Select(line => line.TrimEnd('\r'))
                                 .Where(line => line.Length > 0)
                                 .Select(line => JsonDocument.Parse(line).RootElement)
                                 .Where(entry => entry.GetProperty("event").GetString() == OrchestratorPhases.FAILED);
        Assert.Empty(failures);
        return (records[0], records[1]);
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

    /// <summary>One <c>assistant</c> line per line of each usage, the lines of one usage sharing its message id and usage.</summary>
    /// <param name="usages">The usages.</param>
    private void WriteClaude(IReadOnlyList<Usage> usages)
    {
        var lines = new List<string>();
        for (var index = 0; index < usages.Count; index++)
        {
            var multiplier = usages[index].Multiplier;
            var usage = new JsonObject
            {
                ["input_tokens"] = 1 * multiplier,
                ["cache_read_input_tokens"] = 20 * multiplier,
                ["cache_creation_input_tokens"] = 300 * multiplier,
                ["output_tokens"] = 4000 * multiplier,
                ["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = 50000 * multiplier },
            };
            foreach (var second in usages[index].Seconds)
            {
                var message = new JsonObject { ["id"] = $"msg-{index}", ["model"] = "claude-opus", ["role"] = "assistant", ["usage"] = usage.DeepClone() };
                lines.Add(Line(second, new JsonObject { ["type"] = "assistant", ["message"] = message }));
            }
        }

        var transcript = Path.Combine(_claude, "projects", "C--repo", _session + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, string.Join("\n", lines) + "\n");
    }

    /// <summary>The thread's rollout: one <c>token_count</c> event per entry, in the order given.</summary>
    /// <param name="events">When each event happened, and its <c>total_token_usage</c>.</param>
    private void WriteRollout(IEnumerable<(int Second, JsonObject Totals)> events)
    {
        var rollout = Path.Combine(_codex, "sessions", "2026", "10", "14", $"rollout-2026-10-14T09-00-00-{_thread}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        var lines = events.Select(e => Line(e.Second, new JsonObject
        {
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "token_count", ["info"] = new JsonObject { ["total_token_usage"] = e.Totals } },
        }));
        File.WriteAllText(rollout, string.Join("\n", lines) + "\n");
    }

    private static JsonObject Totals(long input, long cached, long? cacheWrite, long output, long reasoning)
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
        return totals;
    }

    /// <summary>A JSONL object with its <c>timestamp</c> set <paramref name="second"/> seconds after <see cref="START"/>.</summary>
    /// <param name="second">Seconds after A's opening.</param>
    /// <param name="json">The object, without a timestamp.</param>
    private static string Line(int second, JsonObject json)
    {
        var line = new JsonObject { ["timestamp"] = START.AddSeconds(second).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) };
        foreach (var (name, value) in json) line[name] = value?.DeepClone();
        return line.ToJsonString();
    }

    /// <summary>A usage: the times of its lines in seconds after A's opening, how many units it is, and where R2 puts it.</summary>
    /// <param name="Seconds">One time, or a straddling response's two, earliest first.</param>
    /// <param name="Multiplier">How many units of usage.</param>
    /// <param name="Place">The phase that counts it, written from the requirements.</param>
    private sealed record Usage(int[] Seconds, long Multiplier, Place Place);

    /// <summary>An Axis D fixture: its events and phase A's expected counters.</summary>
    /// <param name="Events">Each event's time, in seconds after A's opening, and its <c>cache_write_input_tokens</c> total, if any.</param>
    /// <param name="CacheWriteInA">A's <c>cacheCreationTokens</c>, or null when omitted.</param>
    /// <param name="InputInA">A's <c>inputTokens</c>, from totals of 100 per event.</param>
    /// <param name="OutputInA">A's <c>outputTokens</c>, from totals of 1 per event.</param>
    /// <param name="Incomplete">Whether A's record is <c>usageIncomplete</c>.</param>
    private sealed record Rollout((int Second, long? CacheWrite)[] Events, long? CacheWriteInA, long InputInA, long OutputInA, bool Incomplete);

    /// <summary>A clock that moves only when a test moves it, in UTC; a timer it creates fires at once.</summary>
    /// <param name="start">Where the clock starts.</param>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
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

    /// <summary>
    /// The source with its read deadline stretched as <see cref="TelemetryCollectorTests.SlowTimers"/>
    /// stretches the source's own: the close bounds the read on system time, which the test clock does
    /// not reach, and on a loaded CI runner one loopback read can outlast its 5 seconds.
    /// </summary>
    /// <param name="inner">The source, which answers everything else.</param>
    private sealed class SlowDeadline(IUsageSource inner) : IUsageSource
    {
        public string? Name => inner.Name;

        public TimeSpan SettleDelay => inner.SettleDelay;

        public TimeSpan ReadDeadline => TelemetryCollectorTests.SlowTimers.Stretch(inner.ReadDeadline);

        public bool RegistersAtOpening => inner.RegistersAtOpening;

        public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct) => inner.ReadAsync(window, ct);

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) => inner.PhaseOpenedAsync(session, ct);

        public Task CallArrivedAsync(HostSession? session, OrchestratorCall call) => inner.CallArrivedAsync(session, call);

        public Task CallEndedAsync(OrchestratorCall call) => inner.CallEndedAsync(call);
    }
}
