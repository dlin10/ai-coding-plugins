using System.Text.Json;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Telemetry failures as a matrix (R3, R7, R10): every injected failure against every kind of call,
/// each cell judged on the call's result, its note, the phase open after, the record appended and
/// the one <c>forge.log</c> entry of source <c>telemetry</c> it writes. The fake source stands for a
/// choice an earlier call of the process already made, so <c>forge.begin</c>'s opening registers
/// like any other. The expectation is written from R10, not from the code.
/// </summary>
public sealed class TelemetryFailureMatrixTests : IDisposable
{
    private const string SESSION = "session-a";
    private const string INVALID_CONTENT = """{"state":"open","phase":"lunch"}""";
    private const string NONE_CONTENT = """{"state":"none"}""";

    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly Failure[] AXIS_A = Enum.GetValues<Failure>();

    private static readonly Call[] AXIS_B = Enum.GetValues<Call>();

    /// <summary>The combinations left out, each for the reason its cell would test nothing.</summary>
    private static readonly (Failure, Call)[] EXCLUDED =
    [
        // forge.begin closes nothing, so nothing of a close or a source can fail.
        (Failure.RecordNotAppendable, Call.Begin),
        (Failure.SourceFails, Call.Begin),
        (Failure.SourceTimeout, Call.Begin),
        (Failure.SessionUnknown, Call.Begin),
        (Failure.Cancelled, Call.Begin),

        // interview and done take no position.
        (Failure.PositionUnreadable, Call.Begin),
        (Failure.PositionUnreadable, Call.DoneCloses),

        // done opens nothing, so nothing registers.
        (Failure.RegistrationFails, Call.DoneCloses),

        // Nothing is open, so nothing is closed.
        (Failure.RecordNotAppendable, Call.NothingOpen),
        (Failure.SourceFails, Call.NothingOpen),
        (Failure.SourceTimeout, Call.NothingOpen),
        (Failure.SessionUnknown, Call.NothingOpen),
        (Failure.Cancelled, Call.NothingOpen),
    ];

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-failure-matrix", Guid.NewGuid().ToString("n"));
    private readonly TestClock _clock = new();
    private readonly FakeSource _source;
    private readonly OrchestratorPhases _phases;
    private FileStream? _held;
    private string? _fileBefore;

    public TelemetryFailureMatrixTests()
    {
        Directory.CreateDirectory(_repo);
        _source = new FakeSource(_clock);
        _phases = new OrchestratorPhases(_clock, _source, name => name == "CLAUDE_CODE_SESSION_ID" ? SESSION : null);
    }

    public void Dispose()
    {
        _held?.Dispose();
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The failure injected into the call.</summary>
    public enum Failure
    {
        /// <summary>The phase file held open without sharing.</summary>
        PhaseFileUnreadable,

        /// <summary>The phase file replaced by a directory.</summary>
        PhaseFileUnwritableDirectory,

        /// <summary>The phase file held open for reading only.</summary>
        PhaseFileUnwritableHeld,

        /// <summary>The phase file holding neither an open phase nor the no-phase state.</summary>
        PhaseFileInvalid,

        /// <summary>The Run's state cannot be read for the position.</summary>
        PositionUnreadable,

        /// <summary><c>telemetry.json</c> is not a JSON array.</summary>
        RecordNotAppendable,

        /// <summary>The source throws.</summary>
        SourceFails,

        /// <summary>The source misses its deadline.</summary>
        SourceTimeout,

        /// <summary>The source does not know the session.</summary>
        SessionUnknown,

        /// <summary>The registration at the opening throws.</summary>
        RegistrationFails,

        /// <summary>The declaring call is cancelled during the close's wait.</summary>
        Cancelled,
    }

    /// <summary>The call the failure is injected into.</summary>
    public enum Call
    {
        /// <summary><c>forge.begin</c>, which opens <c>interview</c>.</summary>
        Begin,

        /// <summary><c>plan_revision</c> declared while <c>interview</c> is open.</summary>
        CloseAndOpen,

        /// <summary><c>done</c> declared while <c>interview</c> is open.</summary>
        DoneCloses,

        /// <summary><c>plan_revision</c> declared after <c>done</c>.</summary>
        NothingOpen,
    }

    public static TheoryData<Failure, Call> Cells()
    {
        var cells = new TheoryData<Failure, Call>();
        foreach (var failure in AXIS_A)
        foreach (var call in AXIS_B)
            if (Applies(failure, call)) cells.Add(failure, call);
        return cells;
    }

    /// <summary>Every failure and call combination preserves its expected outcome and records.</summary>
    /// <param name="failure">The telemetry failure injected into the call.</param>
    /// <param name="call">The tool call exercised by the matrix cell.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_cell_gets_its_expected_answer(Failure failure, Call call)
    {
        var expected = Expect(failure, call);
        await InitRepoAsync();

        string runId;
        int recordsBefore = 0, logBefore = 0, notesBefore = 0;
        DateTimeOffset arrival;
        string result;
        if (call == Call.Begin)
        {
            arrival = _clock.GetUtcNow();
            _source.Mode = failure;
            result = await OrchestratorCall.InvokeAsync(_phases, _repo, null, "claude-code", null, async () =>
                await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text,
                                       _ => Inject(failure, Directory.GetDirectories(Path.Combine(_repo, ".forge")).Single()),
                                       _repo, CancellationToken.None));
            runId = JsonDocument.Parse(result).RootElement.GetProperty("runId").GetString()!;
            await AssertBeginResult(result, runId);
        }
        else
        {
            runId = await ArrangeAsync(call);
            recordsBefore = Records(runId).Length;
            logBefore = LogEntries(runId).Count;
            notesBefore = Notes(runId);
            Inject(failure, Run(runId).Path);
            _source.Mode = failure;
            _clock.Advance(TimeSpan.FromSeconds(10));
            arrival = _clock.GetUtcNow();

            using var cancel = new CancellationTokenSource();
            if (failure == Failure.Cancelled) _clock.OnTimer = () => _ = Task.Run(cancel.Cancel);
            var phase = call == Call.DoneCloses ? "done" : "plan_revision";
            result = await OrchestratorCall.InvokeAsync(_phases, _repo, runId, "claude-code", null, async () =>
                await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "a note", cancel.Token, phase: phase));
            Assert.Equal(Run(runId).DiagnosticLogPath, result);
        }

        _held?.Dispose();
        _held = null;

        Assert.Equal(call == Call.Begin ? 0 : 1, Notes(runId) - notesBefore);

        var telemetry = LogEntries(runId).Skip(logBefore).Where(entry => entry.GetProperty("source").GetString() == "telemetry").ToArray();
        var entry = Assert.Single(telemetry);
        Assert.Equal(expected.Log.Level, entry.GetProperty("level").GetString());
        Assert.Equal(expected.Log.Event, entry.GetProperty("event").GetString());
        Assert.Equal(expected.Log.Value, entry.GetProperty("fields").GetProperty(expected.Log.Field).GetString());

        if (failure == Failure.RecordNotAppendable)
        {
            Assert.Equal("{}", AtomicFile.Read(TelemetryPath(runId)));
        }
        else
        {
            var appended = Records(runId).Skip(recordsBefore).ToArray();
            if (expected.Record is null)
            {
                Assert.Empty(appended);
            }
            else
            {
                var record = Assert.Single(appended);
                Assert.Equal(expected.Record.Phase, record.GetProperty("phase").GetString());
                Assert.Equal(SESSION, record.GetProperty("hostSessionId").GetString());
                Assert.Equal("telemetry", record.GetProperty("usageSource").GetString());
                Assert.False(record.TryGetProperty("usageIncomplete", out _));
                if (expected.Record.Missing is null)
                {
                    Assert.Equal(10, record.GetProperty("inputTokens").GetInt64());
                    Assert.False(record.TryGetProperty("usageMissing", out _));
                }
                else
                {
                    Assert.Equal(expected.Record.Missing, record.GetProperty("usageMissing").GetString());
                    Assert.False(record.TryGetProperty("inputTokens", out _));
                }
            }
        }

        var path = PhaseFilePath(runId);
        if (expected.Unchanged)
        {
            if (failure == Failure.PhaseFileUnwritableDirectory) Assert.True(Directory.Exists(path));
            else Assert.Equal(_fileBefore, File.ReadAllText(path));
            return;
        }

        var file = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        if (expected.OpenAfter is null)
        {
            Assert.Equal("none", file.GetProperty("state").GetString());
            Assert.False(file.TryGetProperty("phase", out _));
            return;
        }

        Assert.Equal("open", file.GetProperty("state").GetString());
        Assert.Equal(expected.OpenAfter, file.GetProperty("phase").GetString());
        Assert.Equal(arrival, file.GetProperty("openedAt").GetDateTimeOffset());
        Assert.Equal(SESSION, file.GetProperty("hostSessionId").GetString());
        if (expected.Round is { } round) Assert.Equal(round, file.GetProperty("round").GetInt32());
        else Assert.False(file.TryGetProperty("round", out _));
        Assert.Equal(expected.Registered, file.TryGetProperty("registeredAt", out _));
    }

    [Fact]
    public void Every_value_of_both_axes_occurs_in_some_cell()
    {
        var cells = Cells().Select(row => ((Failure)row[0], (Call)row[1])).ToArray();

        Assert.Equal(AXIS_A, cells.Select(cell => cell.Item1).Distinct().Order());
        Assert.Equal(AXIS_B, cells.Select(cell => cell.Item2).Distinct().Order());
    }

    [Fact]
    public void The_excluded_combinations_are_the_only_ones_missing()
    {
        var cells = Cells().Select(row => ((Failure)row[0], (Call)row[1])).ToHashSet();
        var missing = AXIS_A.SelectMany(failure => AXIS_B.Select(call => (failure, call))).Where(cell => !cells.Contains(cell));

        Assert.Equal(EXCLUDED.Order(), missing.Order());
        Assert.Equal(AXIS_A.Length * AXIS_B.Length - EXCLUDED.Length, cells.Count);
    }

    /// <summary>Whether a failure can occur in a call: each rule is the reason its combination is excluded.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="call">The call.</param>
    private static bool Applies(Failure failure, Call call)
    {
        var closes = call is Call.CloseAndOpen or Call.DoneCloses;
        var opens = call != Call.DoneCloses;
        var takesPosition = call is Call.CloseAndOpen or Call.NothingOpen;
        return failure switch
        {
            Failure.RecordNotAppendable or Failure.SourceFails or Failure.SourceTimeout or Failure.SessionUnknown
                or Failure.Cancelled => closes,
            Failure.PositionUnreadable => takesPosition,
            Failure.RegistrationFails => opens,
            _ => true,
        };
    }

    /// <summary>The answer of R10 for one cell.</summary>
    /// <param name="failure">The injected failure.</param>
    /// <param name="call">The call.</param>
    private static Expectation Expect(Failure failure, Call call)
    {
        var opens = call switch
        {
            Call.Begin => "interview",
            Call.DoneCloses => null,
            _ => "plan_revision",
        };
        var closes = call is Call.CloseAndOpen or Call.DoneCloses ? "interview" : null;
        int? round = opens == "plan_revision" ? 2 : null;
        var recorded = closes is null ? null : new RecordExpectation(closes, null);

        return failure switch
        {
            Failure.PhaseFileUnreadable => Unchanged(Warn("phase_file_unreadable")),
            Failure.PhaseFileUnwritableDirectory or Failure.PhaseFileUnwritableHeld => Unchanged(Warn("phase_file_unwritable")),
            Failure.PhaseFileInvalid => new Expectation(false, opens, round, opens is not null, null, Warn("phase_file_invalid")),
            Failure.PositionUnreadable => new Expectation(false, opens, null, true, recorded, Warn("position_unavailable")),
            Failure.RecordNotAppendable =>
                new Expectation(false, opens, round, opens is not null, null,
                                new LogExpectation("error", "telemetry.write.failed", "event", "orchestrator.usage")),
            Failure.SourceFails => Sourced("source_failed"),
            Failure.SourceTimeout => Sourced("source_timeout"),
            Failure.SessionUnknown => Sourced("session_unknown"),
            Failure.RegistrationFails => new Expectation(false, opens, round, false, recorded, Warn("registration_failed")),
            Failure.Cancelled => new Expectation(false, opens, round, false, null, Warn("cancelled")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        Expectation Sourced(string kind) =>
            new(false, opens, round, opens is not null, new RecordExpectation(closes!, "source_failed"), Warn(kind));

        static Expectation Unchanged(LogExpectation log) => new(true, null, null, false, null, log);

        static LogExpectation Warn(string kind) => new("warn", "orchestrator.telemetry.failed", "kind", kind);
    }

    /// <summary>Puts the failure in place in the run folder; a failure of the source is its mode alone.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="runPath">The Run's folder.</param>
    private void Inject(Failure failure, string runPath)
    {
        var path = Path.Combine(runPath, OrchestratorPhases.FILE_NAME);
        switch (failure)
        {
            case Failure.PhaseFileUnreadable:
                if (!File.Exists(path)) File.WriteAllText(path, NONE_CONTENT);
                _fileBefore = File.ReadAllText(path);
                _held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                break;
            case Failure.PhaseFileUnwritableDirectory:
                if (File.Exists(path)) File.Delete(path);
                Directory.CreateDirectory(path);
                break;
            case Failure.PhaseFileUnwritableHeld:
                if (!File.Exists(path)) File.WriteAllText(path, NONE_CONTENT);
                _fileBefore = File.ReadAllText(path);
                _held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            case Failure.PhaseFileInvalid:
                File.WriteAllText(path, INVALID_CONTENT);
                break;
            case Failure.PositionUnreadable:
                File.WriteAllText(Path.Combine(runPath, "state.json"), "{ not json");
                break;
            case Failure.RecordNotAppendable:
                File.WriteAllText(Path.Combine(runPath, "telemetry.json"), "{}");
                break;
        }
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

    /// <summary>A fresh Run with <c>interview</c> open, two plan-review rounds run, and for <see cref="Call.NothingOpen"/> after <c>done</c>.</summary>
    /// <param name="call">The call the Run is arranged for.</param>
    private async Task<string> ArrangeAsync(Call call)
    {
        var json = await OrchestratorCall.InvokeAsync(_phases, _repo, null, "claude-code", null, async () =>
            await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text, _ => { }, _repo, CancellationToken.None));
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;

        var run = Run(runId);
        run.WriteState(run.ReadState() with { ReviewRounds = 2 });
        _clock.Advance(TimeSpan.FromSeconds(10));
        if (call == Call.NothingOpen)
            await OrchestratorCall.InvokeAsync(_phases, _repo, runId, "claude-code", null, async () =>
                await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "stop", CancellationToken.None, phase: "done"));
        return runId;
    }

    /// <summary><c>forge.begin</c> answers as it would without the failure.</summary>
    /// <param name="result">The call's result.</param>
    /// <param name="runId">The Run it created.</param>
    private async Task AssertBeginResult(string result, string runId)
    {
        var head = (await new GitClient(_repo).OutputAsync(["rev-parse", "HEAD"], CancellationToken.None)).Trim();
        var root = JsonDocument.Parse(result).RootElement;

        Assert.Equal(["runId", "runPath", "profile", "baselineHead", "client", "advisors"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Run(runId).Path, root.GetProperty("runPath").GetString());
        Assert.Equal("Text", root.GetProperty("profile").GetString());
        Assert.Equal(head, root.GetProperty("baselineHead").GetString());
        Assert.Equal("claude-code", root.GetProperty("client").GetString());
    }

    private RunDirectory Run(string runId) => RunDirectory.Open(_repo, runId);

    private string PhaseFilePath(string runId) => Path.Combine(Run(runId).Path, OrchestratorPhases.FILE_NAME);

    private string TelemetryPath(string runId) => Path.Combine(Run(runId).Path, "telemetry.json");

    /// <summary>Reads the Run usage records while allowing concurrent file writers.</summary>
    /// <param name="runId">The Run whose telemetry is read.</param>
    private JsonElement[] Records(string runId) =>
        File.Exists(TelemetryPath(runId)) ? [.. JsonDocument.Parse(AtomicFile.Read(TelemetryPath(runId))).RootElement.EnumerateArray()] : [];

    private IReadOnlyList<JsonElement> LogEntries(string runId) =>
        [.. AtomicFile.Read(Run(runId).DiagnosticLogPath)
                      .Split('\n')
                      .Select(line => line.TrimEnd('\r'))
                      .Where(line => line.Length > 0)
                      .Select(line => JsonDocument.Parse(line).RootElement)];

    private int Notes(string runId) => LogEntries(runId).Count(entry => entry.GetProperty("event").GetString() == "note");

    /// <summary>What one cell must give.</summary>
    /// <param name="Unchanged">Whether the phase file must be exactly as it was.</param>
    /// <param name="OpenAfter">The phase open after the call, or <see langword="null"/> for the explicit no-phase state.</param>
    /// <param name="Round">The round the open phase carries, or <see langword="null"/> for none.</param>
    /// <param name="Registered">Whether the open phase keeps a registration time.</param>
    /// <param name="Record">The one record the call appends, or <see langword="null"/> for none.</param>
    /// <param name="Log">The one <c>forge.log</c> entry of source <c>telemetry</c> the call writes.</param>
    private sealed record Expectation(bool Unchanged,
                                      string? OpenAfter,
                                      int? Round,
                                      bool Registered,
                                      RecordExpectation? Record,
                                      LogExpectation Log);

    /// <summary>The record a cell appends.</summary>
    /// <param name="Phase">The phase recorded.</param>
    /// <param name="Missing">Its <c>usageMissing</c>, or <see langword="null"/> when it carries counters.</param>
    private sealed record RecordExpectation(string Phase, string? Missing);

    /// <summary>The log entry a cell writes.</summary>
    /// <param name="Level">The entry's level.</param>
    /// <param name="Event">The entry's event.</param>
    /// <param name="Field">The field that names the failure.</param>
    /// <param name="Value">That field's value.</param>
    private sealed record LogExpectation(string Level, string Event, string Field, string Value);

    /// <summary>
    /// A clock that moves only when a test moves it, in UTC so local text is predictable; its timers
    /// never fire, and creating one tells the test the call is waiting.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = START.UtcTicks;

        public Action? OnTimer { get; set; }

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            OnTimer?.Invoke();
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
    /// A telemetry-export source chosen earlier in the process: it reads ten input tokens and
    /// registers at the opening, unless its mode is a failure of its own. It honours its token as a
    /// collector request would.
    /// </summary>
    /// <param name="clock">The clock registrations are stamped with.</param>
    private sealed class FakeSource(TimeProvider clock) : IUsageSource
    {
        public Failure? Mode { get; set; }

        public string? Name => "telemetry";

        public TimeSpan SettleDelay => Mode == Failure.Cancelled ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;

        public TimeSpan ReadDeadline => Mode == Failure.SourceTimeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10);

        public async Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            switch (Mode)
            {
                case Failure.SourceFails:
                    throw new InvalidOperationException("the collector broke");
                case Failure.SourceTimeout:
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    break;
                case Failure.SessionUnknown:
                    return new UsageReading(null, Missing: OrchestratorPhases.SOURCE_FAILED, FailureKind: "session_unknown");
            }

            return new UsageReading(new OrchestratorTokens(InputTokens: 10, OutputTokens: 1));
        }

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Mode == Failure.RegistrationFails) throw new InvalidOperationException("the collector refused");
            return Task.FromResult<DateTimeOffset?>(clock.GetUtcNow());
        }
    }
}
