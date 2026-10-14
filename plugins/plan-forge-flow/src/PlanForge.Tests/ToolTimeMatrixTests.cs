using System.Text.Json;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Orchestrator tool time as a matrix (R2, R4): how a call ends, where it falls against the
/// declaration boundaries, what overlaps it and how it names its Run, each cell driven through the
/// call filter's logic with a fake clock and stub tool delegates. The expectation is written from R4
/// — the union of the Run's calls, clipped to each phase, truncated to the second — not from the code.
/// </summary>
public sealed class ToolTimeMatrixTests : IDisposable
{
    private static readonly DateTimeOffset START = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The cells' declarations, in seconds: <c>planning</c> opens at 0, and <c>done</c> at 60 leaves nothing open.</summary>
    private static readonly (int At, string Phase)[] DECLARATIONS = [(0, "planning"), (20, "approval"), (40, "wrap_up"), (60, "done")];

    private static readonly Ending[] AXIS_A = Enum.GetValues<Ending>();

    private static readonly Timing[] AXIS_B = Enum.GetValues<Timing>();

    private static readonly Overlap[] AXIS_C = Enum.GetValues<Overlap>();

    private static readonly Naming[] AXIS_D = Enum.GetValues<Naming>();

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tool-time", Guid.NewGuid().ToString("n"));
    private readonly TestClock _clock = new();

    public ToolTimeMatrixTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>How the call ends.</summary>
    public enum Ending
    {
        /// <summary>It returns a result.</summary>
        Returns,

        /// <summary>Refused before its Run is opened or its call logged, as <c>SensitiveInput</c> refuses.</summary>
        RefusedEarly,

        /// <summary>Refused inside the tool with <see cref="ArgumentRejectedException"/>.</summary>
        RefusedInside,

        /// <summary>Fails with another exception.</summary>
        Fails,

        /// <summary>Cancelled.</summary>
        Cancelled,
    }

    /// <summary>The call's span against the boundaries at 20, 40 and 60 (<c>done</c>).</summary>
    public enum Timing
    {
        /// <summary>[5, 12): arrives and ends before the first boundary.</summary>
        BeforeFirst,

        /// <summary>[12, 27): arrives before the first boundary and ends after it.</summary>
        AcrossFirst,

        /// <summary>[25, 33): arrives after the first boundary.</summary>
        AfterFirst,

        /// <summary>[15, 47): spans two boundaries, so <c>approval</c> lies wholly inside it.</summary>
        SpansTwo,

        /// <summary>[64, 75): arrives after <c>done</c>, while no phase is open.</summary>
        AfterDone,

        /// <summary>[55, 70): arrives before <c>done</c> and ends after it.</summary>
        AcrossDone,
    }

    /// <summary>What overlaps the call: a second call from three seconds after its arrival to four after its end.</summary>
    public enum Overlap
    {
        /// <summary>Nothing.</summary>
        Nothing,

        /// <summary>Another call of the same Run.</summary>
        SameRun,

        /// <summary>A call of another Run in the same workspace.</summary>
        OtherRun,

        /// <summary>A call naming the same <c>runId</c> in another workspace.</summary>
        OtherWorkspace,
    }

    /// <summary>How the call names its Run.</summary>
    public enum Naming
    {
        /// <summary>By <c>workspaceRoot</c> and <c>runId</c>.</summary>
        ByRun,

        /// <summary>Naming none.</summary>
        None,
    }

    public static TheoryData<Ending, Timing, Overlap, Naming> Cells()
    {
        var cells = new TheoryData<Ending, Timing, Overlap, Naming>();
        foreach (var ending in AXIS_A)
        foreach (var timing in AXIS_B)
        foreach (var overlap in AXIS_C)
        foreach (var naming in AXIS_D)
            if (!(naming == Naming.None && overlap == Overlap.SameRun)) cells.Add(ending, timing, overlap, naming);
        return cells;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_cell_counts_each_phase_to_the_second(Ending ending, Timing timing, Overlap overlap, Naming naming)
    {
        var phases = Phases();
        var runId = NewRun();
        var primary = Span(timing);
        var second = (Start: primary.Start + 3, End: primary.End + 4);
        var (secondRoot, secondRunId) = overlap switch
        {
            Overlap.OtherRun => (_repo, "20261014-090000-other0"),
            Overlap.OtherWorkspace => (Path.Combine(_repo, "other"), runId),
            _ => (_repo, runId),
        };

        Call? primaryCall = null, secondCall = null;
        var busy = TimeSpan.Zero;
        DateTimeOffset? opened = null;
        var steps = new List<(int At, Func<Task> Act)>
        {
            (primary.Start, () =>
            {
                primaryCall = naming == Naming.ByRun ? Arrive(phases, primary.Start * 1000, _repo, runId, ending)
                                                     : Arrive(phases, primary.Start * 1000, null, null, ending);
                return Task.CompletedTask;
            }),
            (primary.End, () => EndAsync(primaryCall!, primary.End * 1000)),
        };
        if (overlap != Overlap.Nothing)
        {
            steps.Add((second.Start, () =>
            {
                secondCall = Arrive(phases, second.Start * 1000, secondRoot, secondRunId, Ending.Returns);
                return Task.CompletedTask;
            }));
            steps.Add((second.End, () => EndAsync(secondCall!, second.End * 1000)));
        }

        foreach (var (at, phase) in DECLARATIONS)
            steps.Add((at, async () =>
            {
                _clock.Set(at * 1000);
                if (opened is { } from) busy += phases.Clock.Busy(_repo, runId, from, _clock.GetUtcNow());
                await DeclareAsync(phases, at * 1000, runId, phase);
                opened = phase == "done" ? null : _clock.GetUtcNow();
            }));

        foreach (var (_, act) in steps.OrderBy(step => step.At)) await act();

        List<(double Start, double End)> ofRun = [];
        if (naming == Naming.ByRun) ofRun.Add(primary);
        if (overlap == Overlap.SameRun) ofRun.Add(second);

        var records = Records(runId);
        Assert.Equal(["planning", "approval", "wrap_up"], records.Select(record => record.GetProperty("phase").GetString()));
        for (var i = 0; i < records.Length; i++)
        {
            var (from, to) = (DECLARATIONS[i].At, DECLARATIONS[i + 1].At);
            var tools = (int)Math.Floor(Covered(ofRun, from, to));
            Assert.Equal(Text(to - from), records[i].GetProperty("wallDuration").GetString());
            Assert.Equal(Text(tools), records[i].GetProperty("toolDuration").GetString());
            Assert.Equal(Text(to - from - tools), records[i].GetProperty("duration").GetString());
        }

        Assert.Equal(TimeSpan.FromSeconds(Covered(ofRun, DECLARATIONS[0].At, DECLARATIONS[^1].At)), busy);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs_in_some_cell()
    {
        var cells = Cells().Select(row => ((Ending)row[0], (Timing)row[1], (Overlap)row[2], (Naming)row[3])).ToArray();

        Assert.Equal(AXIS_A, cells.Select(cell => cell.Item1).Distinct().Order());
        Assert.Equal(AXIS_B, cells.Select(cell => cell.Item2).Distinct().Order());
        Assert.Equal(AXIS_C, cells.Select(cell => cell.Item3).Distinct().Order());
        Assert.Equal(AXIS_D, cells.Select(cell => cell.Item4).Distinct().Order());
    }

    [Fact]
    public void Naming_none_with_another_call_of_the_same_run_is_the_only_combination_missing()
    {
        var cells = Cells().Select(row => ((Ending)row[0], (Timing)row[1], (Overlap)row[2], (Naming)row[3])).ToHashSet();
        var all = from ending in AXIS_A
                  from timing in AXIS_B
                  from overlap in AXIS_C
                  from naming in AXIS_D
                  select (ending, timing, overlap, naming);

        Assert.All(all.Where(cell => !cells.Contains(cell)),
                   cell => Assert.Equal((Overlap.SameRun, Naming.None), (cell.overlap, cell.naming)));
        Assert.Equal(AXIS_A.Length * AXIS_B.Length * (AXIS_C.Length * AXIS_D.Length - 1), cells.Count);
    }

    [Fact]
    public async Task Begin_counts_from_its_arrival_in_interview()
    {
        var phases = Phases();
        await InitRepoAsync();

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, "claude-code", null, async () =>
        {
            _clock.Set(3_000);
            return await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text, _ => _clock.Set(7_000), _repo,
                                          CancellationToken.None);
        });
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;
        await DeclareAsync(phases, 30_000, runId, "planning");

        var record = Assert.Single(Records(runId));
        Assert.Equal("interview", record.GetProperty("phase").GetString());
        Assert.Equal("00:00:30", record.GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:07", record.GetProperty("toolDuration").GetString());
    }

    [Fact]
    public async Task A_begin_that_fails_before_creating_its_run_counts_nowhere()
    {
        var phases = Phases();
        await InitRepoAsync();
        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, "claude-code", null, async () =>
            await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text, _ => { }, _repo, CancellationToken.None));
        var runId = JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;

        _clock.Set(10_000);
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => OrchestratorCall.InvokeAsync(phases, _repo, null, "claude-code", null,
            async () =>
            {
                _clock.Set(20_000);
                return await ForgeTools.Begin(SessionRoots.None, "claude-code", CapabilityProfile.Text, _ => { }, _repo,
                                              CancellationToken.None, workerTools: ["not a server"]);
            }).AsTask());
        await DeclareAsync(phases, 30_000, runId, "planning");

        Assert.Equal("00:00:00", Assert.Single(Records(runId)).GetProperty("toolDuration").GetString());
    }

    [Fact]
    public async Task A_failed_phase_file_write_leaves_the_overlapped_time_with_the_phase_still_open()
    {
        var phases = Phases();
        var runId = NewRun();
        await DeclareAsync(phases, 0, runId, "planning");
        var call = Arrive(phases, 10_000, _repo, runId, Ending.Returns);

        using (new FileStream(PhaseFilePath(runId), FileMode.Open, FileAccess.Read, FileShare.Read))
            await DeclareAsync(phases, 20_000, runId, "approval");
        await EndAsync(call, 40_000);
        await DeclareAsync(phases, 50_000, runId, "approval");
        await DeclareAsync(phases, 60_000, runId, "done");

        var records = Records(runId);
        Assert.Equal(["planning", "approval"], records.Select(record => record.GetProperty("phase").GetString()));
        Assert.Equal("00:00:50", records[0].GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:30", records[0].GetProperty("toolDuration").GetString());
        Assert.Equal("00:00:00", records[1].GetProperty("toolDuration").GetString());
    }

    [Fact]
    public async Task A_phase_closed_by_a_fresh_owner_after_a_restart_carries_only_its_tool_time()
    {
        var first = Phases();
        var runId = NewRun();
        await DeclareAsync(first, 0, runId, "planning");
        await EndAsync(Arrive(first, 5_000, _repo, runId, Ending.Returns), 25_000);

        var fresh = Phases();
        await EndAsync(Arrive(fresh, 30_000, _repo, runId, Ending.Returns), 45_000);
        await DeclareAsync(fresh, 60_000, runId, "done");

        var record = Assert.Single(Records(runId));
        Assert.Equal("00:01:00", record.GetProperty("wallDuration").GetString());
        Assert.Equal("00:00:15", record.GetProperty("toolDuration").GetString());
        Assert.Equal("00:00:45", record.GetProperty("duration").GetString());
    }

    [Fact]
    public async Task A_busy_period_that_outlives_two_declarations_splits_across_all_three_phases()
    {
        var phases = Phases();
        var runId = NewRun();
        await DeclareAsync(phases, 0, runId, "planning");
        var first = Arrive(phases, 10_000, _repo, runId, Ending.Returns);
        await DeclareAsync(phases, 20_000, runId, "approval");
        var second = Arrive(phases, 35_000, _repo, runId, Ending.Returns);
        await DeclareAsync(phases, 40_000, runId, "wrap_up");
        await EndAsync(first, 45_000);
        await EndAsync(second, 70_000);
        await DeclareAsync(phases, 80_000, runId, "done");

        Assert.Equal(["00:00:10", "00:00:20", "00:00:30"],
                     Records(runId).Select(record => record.GetProperty("toolDuration").GetString()));
    }

    [Fact]
    public async Task The_declaring_call_own_time_goes_to_the_phase_it_opens()
    {
        var phases = Phases();
        var runId = NewRun();
        await DeclareAsync(phases, 0, runId, "planning");

        _clock.Set(20_000);
        await OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
        {
            _clock.Set(26_000);
            var path = await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "approving", CancellationToken.None, phase: "approval");
            _clock.Set(29_000);
            return path;
        });
        await DeclareAsync(phases, 50_000, runId, "done");

        var records = Records(runId);
        Assert.Equal(["00:00:20", "00:00:30"], records.Select(record => record.GetProperty("wallDuration").GetString()));
        Assert.Equal(["00:00:00", "00:00:09"], records.Select(record => record.GetProperty("toolDuration").GetString()));
    }

    [Fact]
    public async Task A_declaration_applied_after_a_later_opening_counts_its_earlier_part_in_the_phase_open_then()
    {
        var phases = Phases();
        var runId = NewRun();
        await DeclareAsync(phases, 0, runId, "planning");

        _clock.Set(9_000);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
        {
            await release.Task;
            return await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, "wrapping up", CancellationToken.None, phase: "wrap_up");
        }).AsTask();
        await DeclareAsync(phases, 10_000, runId, "approval");

        _clock.Set(12_000);
        release.SetResult();
        await late;
        await DeclareAsync(phases, 30_000, runId, "done");

        var records = Records(runId);
        Assert.Equal(["planning", "approval", "wrap_up"], records.Select(record => record.GetProperty("phase").GetString()));
        Assert.Equal(["00:00:10", "00:00:00", "00:00:20"], records.Select(record => record.GetProperty("wallDuration").GetString()));
        Assert.Equal(["00:00:01", "00:00:00", "00:00:02"], records.Select(record => record.GetProperty("toolDuration").GetString()));
    }

    [Fact]
    public async Task A_busy_period_of_1_2_seconds_split_evenly_by_a_boundary_gives_each_record_zero()
    {
        var phases = Phases();
        var runId = NewRun();
        await DeclareAsync(phases, 0, runId, "planning");
        var call = Arrive(phases, 9_400, _repo, runId, Ending.Returns);
        await DeclareAsync(phases, 10_000, runId, "approval");
        await EndAsync(call, 10_600);
        await DeclareAsync(phases, 20_000, runId, "done");

        Assert.Equal(["00:00:00", "00:00:00"], Records(runId).Select(record => record.GetProperty("toolDuration").GetString()));
    }

    [Fact]
    public async Task The_untruncated_tool_time_of_all_closed_phases_sums_to_the_run_calls_inside_open_phases()
    {
        var phases = Phases();
        var runId = NewRun();
        var busy = TimeSpan.Zero;
        DateTimeOffset? opened = null;

        async Task Declare(int ms, string phase)
        {
            _clock.Set(ms);
            if (opened is { } from) busy += phases.Clock.Busy(_repo, runId, from, _clock.GetUtcNow());
            await DeclareAsync(phases, ms, runId, phase);
            opened = phase == "done" ? null : _clock.GetUtcNow();
        }

        await Declare(0, "planning");
        var a = Arrive(phases, 1_100, _repo, runId, Ending.Returns);
        var unnamed = Arrive(phases, 2_000, null, null, Ending.Returns);
        var other = Arrive(phases, 5_000, _repo, "20261014-090000-other0", Ending.Returns);
        await EndAsync(unnamed, 8_000);
        await Declare(10_300, "approval");
        var b = Arrive(phases, 11_900, _repo, runId, Ending.Fails);
        await EndAsync(a, 12_400);
        await EndAsync(b, 14_250);
        var c = Arrive(phases, 19_500, _repo, runId, Ending.Cancelled);
        await Declare(20_700, "wrap_up");
        await EndAsync(other, 25_000);
        await Declare(30_200, "done");
        await EndAsync(c, 31_750);
        await EndAsync(Arrive(phases, 33_000, _repo, runId, Ending.Returns), 35_000);

        List<(double Start, double End)> ofRun = [(1.1, 12.4), (11.9, 14.25), (19.5, 31.75), (33, 35)];
        Assert.Equal(TimeSpan.FromMilliseconds(23_850), busy);
        Assert.Equal(TimeSpan.FromSeconds(Covered(ofRun, 0, 30.2)), busy);
        Assert.Equal(["00:00:09", "00:00:05", "00:00:09"], Records(runId).Select(record => record.GetProperty("toolDuration").GetString()));
    }

    /// <summary>The primary call's span, in seconds, for one value of axis B.</summary>
    /// <param name="timing">The value of axis B.</param>
    private static (int Start, int End) Span(Timing timing) => timing switch
    {
        Timing.BeforeFirst => (5, 12),
        Timing.AcrossFirst => (12, 27),
        Timing.AfterFirst => (25, 33),
        Timing.SpansTwo => (15, 47),
        Timing.AfterDone => (64, 75),
        Timing.AcrossDone => (55, 70),
        _ => throw new ArgumentOutOfRangeException(nameof(timing)),
    };

    /// <summary>R4's tool time: the length of the union of <paramref name="spans"/> inside <c>[from, to)</c>, in seconds.</summary>
    /// <param name="spans">The Run's calls, from arrival to end.</param>
    /// <param name="from">The phase's opening.</param>
    /// <param name="to">The phase's closing.</param>
    private static double Covered(IEnumerable<(double Start, double End)> spans, double from, double to)
    {
        var covered = 0.0;
        var reached = from;
        foreach (var (start, end) in spans.Select(span => (Start: Math.Max(span.Start, from), End: Math.Min(span.End, to)))
                                          .Where(span => span.End > span.Start)
                                          .OrderBy(span => span.Start))
        {
            if (end > Math.Max(start, reached)) covered += end - Math.Max(start, reached);
            reached = Math.Max(reached, end);
        }

        return covered;
    }

    private static string Text(int seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private OrchestratorPhases Phases() => new(_clock, NoUsageSource.Instance, _ => null);

    private string NewRun()
    {
        var runId = $"20261014-090000-{Guid.NewGuid().ToString("n")[..6]}";
        RunDirectory.Create(_repo, runId).WriteState(new RunState(runId, _repo, "Text", START, 0, 5));
        return runId;
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

    /// <summary>
    /// A call that arrives at <paramref name="ms"/> through the filter's logic and waits there until
    /// <see cref="EndAsync"/> ends it the way <paramref name="ending"/> says.
    /// </summary>
    /// <param name="phases">The phase owner whose clock counts the call.</param>
    /// <param name="ms">The arrival, in milliseconds after the start.</param>
    /// <param name="root">The call's <c>workspaceRoot</c>, or null.</param>
    /// <param name="runId">The call's <c>runId</c>, or null.</param>
    /// <param name="ending">How the call ends.</param>
    private Call Arrive(OrchestratorPhases phases, int ms, string? root, string? runId, Ending ending)
    {
        _clock.Set(ms);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = OrchestratorCall.InvokeAsync(phases, root, runId, "claude-code", null, async () =>
        {
            await release.Task;
            switch (ending)
            {
                case Ending.RefusedEarly:
                    SensitiveInput.Guard("api_key: Abcdefghijklmnop1234+", "the plan revision");
                    break;
                case Ending.RefusedInside:
                    using (OrchestratorCall.Join(root, runId))
                        throw new ArgumentRejectedException("refused inside the tool");
                case Ending.Fails:
                    throw new InvalidOperationException("the tool failed");
                case Ending.Cancelled:
                    new CancellationToken(canceled: true).ThrowIfCancellationRequested();
                    break;
            }

            return "result";
        }).AsTask();
        return new Call(release, task, ending);
    }

    /// <summary>Ends <paramref name="call"/> at <paramref name="ms"/> and checks it ended as its axis value says.</summary>
    /// <param name="call">The waiting call.</param>
    /// <param name="ms">The end, in milliseconds after the start.</param>
    private async Task EndAsync(Call call, int ms)
    {
        _clock.Set(ms);
        call.Release.SetResult();
        var error = await Record.ExceptionAsync(() => call.Task);
        var expected = call.Ending switch
        {
            Ending.RefusedEarly => typeof(SensitiveContentException),
            Ending.RefusedInside => typeof(ArgumentRejectedException),
            Ending.Fails => typeof(InvalidOperationException),
            Ending.Cancelled => typeof(OperationCanceledException),
            _ => null,
        };
        if (expected is null) Assert.Null(error);
        else Assert.IsType(expected, error);
    }

    private Task<string> DeclareAsync(OrchestratorPhases phases, int ms, string runId, string phase)
    {
        _clock.Set(ms);
        return OrchestratorCall.InvokeAsync(phases, _repo, runId, "claude-code", null, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, $"entering {phase}", CancellationToken.None,
                                       phase: phase)).AsTask();
    }

    private string PhaseFilePath(string runId) => Path.Combine(RunDirectory.Open(_repo, runId).Path, OrchestratorPhases.FILE_NAME);

    private JsonElement[] Records(string runId)
    {
        var path = Path.Combine(RunDirectory.Open(_repo, runId).Path, "telemetry.json");
        return File.Exists(path) ? [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray()] : [];
    }

    /// <summary>A call waiting at its gate.</summary>
    /// <param name="Release">Lets the call end.</param>
    /// <param name="Task">The call.</param>
    /// <param name="Ending">How it ends.</param>
    private sealed record Call(TaskCompletionSource Release, Task<string> Task, Ending Ending);

    /// <summary>A clock that moves only when a test sets it, in UTC so local text is predictable.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = START.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Set(long ms) => Interlocked.Exchange(ref _ticks, START.UtcTicks + ms * TimeSpan.TicksPerMillisecond);
    }
}
