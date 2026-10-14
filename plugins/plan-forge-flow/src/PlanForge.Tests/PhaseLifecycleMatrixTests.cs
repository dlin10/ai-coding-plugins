using System.Text.Json;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The phase lifecycle as a matrix (R1, R2): every state before a call against every call, each
/// cell judged separately on the notes written, the records appended and the phase open after. The
/// expectation is written from the requirements, not from the code, and no cell has a safe side.
/// </summary>
public sealed class PhaseLifecycleMatrixTests : IDisposable
{
    /// <summary>The call without <c>phase</c>.</summary>
    private const string NO_PHASE = "(no phase)";

    /// <summary>The name of the phase open before the call.</summary>
    private const string OWN_NAME = "(own name)";

    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] NAMES = ["interview", "planning", "plan_revision", "approval", "build", "code_review", "wrap_up"];

    private static readonly string[] INVALID = ["Planning", "plan-revision", "", " ", "review"];

    private static readonly Before[] AXIS_A = [Before.Interview, Before.Build, Before.Nothing];

    private static readonly string[] AXIS_B = [NO_PHASE, .. NAMES, "done", OWN_NAME, .. INVALID];

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-phase-matrix", Guid.NewGuid().ToString("n"));
    private readonly TestClock _clock = new();

    public PhaseLifecycleMatrixTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The state of the Run before the call.</summary>
    public enum Before
    {
        /// <summary>A fresh Run: <c>interview</c> open.</summary>
        Interview,

        /// <summary>Another phase open: <c>build</c>.</summary>
        Build,

        /// <summary>Nothing open, after <c>done</c>.</summary>
        Nothing,
    }

    public static TheoryData<Before, string> Cells()
    {
        var cells = new TheoryData<Before, string>();
        foreach (var before in AXIS_A)
        foreach (var call in AXIS_B)
            if (!(before == Before.Nothing && call == OWN_NAME)) cells.Add(before, call);
        return cells;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_cell_gets_its_expected_answer(Before before, string call)
    {
        var expected = Expect(before, call);
        var phases = new OrchestratorPhases(_clock, NoUsageSource.Instance, _ => null);
        var runId = await ArrangeAsync(phases, before);
        var notesBefore = Notes(runId);
        var recordsBefore = Records(runId).Length;
        var fileBefore = File.ReadAllText(PhaseFilePath(runId));
        var openedBefore = OpenedAt(runId);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var phase = call switch
        {
            NO_PHASE => null,
            OWN_NAME => OpenName(before),
            _ => call,
        };
        if (expected.Refused)
            await Assert.ThrowsAsync<ArgumentRejectedException>(() => CallAsync(phases, runId, phase));
        else
            await CallAsync(phases, runId, phase);

        Assert.Equal(expected.Notes, Notes(runId) - notesBefore);

        var appended = Records(runId).Skip(recordsBefore).ToArray();
        string?[] recorded = expected.RecordFor is null ? [] : [expected.RecordFor];
        Assert.Equal(recorded, appended.Select(record => record.GetProperty("phase").GetString()));

        if (expected.Unchanged)
        {
            Assert.Equal(fileBefore, File.ReadAllText(PhaseFilePath(runId)));
            return;
        }

        var file = PhaseFile(runId);
        if (expected.OpenAfter is null)
        {
            Assert.Equal("none", file.GetProperty("state").GetString());
            Assert.False(file.TryGetProperty("phase", out _));
            return;
        }

        Assert.Equal("open", file.GetProperty("state").GetString());
        Assert.Equal(expected.OpenAfter, file.GetProperty("phase").GetString());
        Assert.Equal(START.AddSeconds(20), file.GetProperty("openedAt").GetDateTimeOffset());
        if (openedBefore is { } opened) Assert.True(OpenedAt(runId) > opened);
    }

    [Fact]
    public void Every_value_of_both_axes_occurs_in_some_cell()
    {
        var cells = Cells().Select(row => ((Before)row[0], (string)row[1])).ToArray();

        Assert.Equal(AXIS_A.Order(), cells.Select(cell => cell.Item1).Distinct().Order());
        Assert.Equal(AXIS_B.Order(StringComparer.Ordinal), cells.Select(cell => cell.Item2).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetValues<Before>().Order(), AXIS_A.Order());
    }

    [Fact]
    public void The_own_name_with_nothing_open_is_the_only_combination_missing()
    {
        var cells = Cells().Select(row => ((Before)row[0], (string)row[1])).ToHashSet();
        var missing = AXIS_A.SelectMany(before => AXIS_B.Select(call => (before, call))).Where(cell => !cells.Contains(cell));

        Assert.Equal([(Before.Nothing, OWN_NAME)], missing.ToArray());
        Assert.Equal(AXIS_A.Length * AXIS_B.Length - 1, cells.Count);
    }

    /// <summary>
    /// The answer of R1 and R2 for one cell: how many notes the call writes, which phase it records
    /// if any, and the phase open after it.
    /// </summary>
    /// <param name="before">The state of the Run before the call.</param>
    /// <param name="call">The call, as a value of axis B.</param>
    private static Expectation Expect(Before before, string call)
    {
        var open = OpenName(before);
        if (call == NO_PHASE) return new Expectation(Notes: 1, RecordFor: null, OpenAfter: open, Refused: false, Unchanged: true);
        if (INVALID.Contains(call)) return new Expectation(Notes: 0, RecordFor: null, OpenAfter: open, Refused: true, Unchanged: true);
        if (call == "done") return new Expectation(Notes: 1, RecordFor: open, OpenAfter: null, Refused: false, Unchanged: false);

        var named = call == OWN_NAME ? open! : call;
        return new Expectation(Notes: 1, RecordFor: open, OpenAfter: named, Refused: false, Unchanged: false);
    }

    private static string? OpenName(Before before) => before switch
    {
        Before.Interview => "interview",
        Before.Build => "build",
        _ => null,
    };

    /// <summary>Brings a fresh Run to <paramref name="before"/> at <c>START + 10 s</c>.</summary>
    /// <param name="phases">The phase owner the test uses.</param>
    /// <param name="before">The state to reach.</param>
    private async Task<string> ArrangeAsync(OrchestratorPhases phases, Before before)
    {
        var git = new GitClient(_repo);
        var ct = CancellationToken.None;
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, "claude-code", null, async () =>
            await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text, _ => { }, _repo, ct));
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;

        _clock.Advance(TimeSpan.FromSeconds(10));
        if (before == Before.Build) await CallAsync(phases, runId, "build");
        if (before == Before.Nothing) await CallAsync(phases, runId, "done");
        return runId;
    }

    private Task<string> CallAsync(OrchestratorPhases phases, string runId, string? phase) =>
        OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "a note", CancellationToken.None, phase: phase)).AsTask();

    private string RunPath(string runId) => RunDirectory.Open(_repo, runId).Path;

    private string PhaseFilePath(string runId) => Path.Combine(RunPath(runId), OrchestratorPhases.FILE_NAME);

    private JsonElement PhaseFile(string runId) => JsonDocument.Parse(File.ReadAllText(PhaseFilePath(runId))).RootElement;

    private DateTimeOffset? OpenedAt(string runId) =>
        PhaseFile(runId).TryGetProperty("openedAt", out var opened) ? opened.GetDateTimeOffset() : null;

    private JsonElement[] Records(string runId)
    {
        var path = Path.Combine(RunPath(runId), "telemetry.json");
        return File.Exists(path) ? [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray()] : [];
    }

    private int Notes(string runId) =>
        AtomicFile.Read(RunDirectory.Open(_repo, runId).DiagnosticLogPath)
                  .Split('\n')
                  .Select(line => line.TrimEnd('\r'))
                  .Where(line => line.Length > 0)
                  .Count(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString() == "note");

    /// <summary>What one cell must give.</summary>
    /// <param name="Notes">The note entries the call writes.</param>
    /// <param name="RecordFor">The phase of the one record the call appends, or <see langword="null"/> for none.</param>
    /// <param name="OpenAfter">The phase open after the call, or <see langword="null"/> for none.</param>
    /// <param name="Refused">Whether the call is refused.</param>
    /// <param name="Unchanged">Whether the phase file must be exactly as it was.</param>
    private sealed record Expectation(int Notes, string? RecordFor, string? OpenAfter, bool Refused, bool Unchanged);

    /// <summary>A clock that moves only when a test moves it, in UTC so local text is predictable.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = START.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }
}
