using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
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
/// The order of a call's read and its registrations (R6, R7, R9, R10) walked as a matrix: when the
/// process's choice is made, what the answering collector knows of the Host session at the call, and
/// what the call opens and closes. Every cell has its own Run, port, Claude Code session and fake
/// clock; the choice owner is task 9's, over a profile string the test writes, and every collector is
/// an in-process one on the cell's port. No cell reads a real profile or environment variable.
/// </summary>
public sealed class RegistrationOrderMatrixTests : IAsyncLifetime
{
    private const string TELEMETRY = "telemetry";
    private const string CHOSEN = "orchestrator.source.chosen";
    private const string RESTARTED = "orchestrator.collector.restarted";

    // One claude_code.api_request of the recorded shape, as R7 normalizes it.
    private const long INPUT = 2 + 0 + 46187;
    private const long CACHE_CREATION = 46187;
    private const long OUTPUT = 4;
    private const decimal COST = 0.0092396m;

    /// <summary>The cells known to be wider than their expectation: none, since a lost session read as a known one would record a short count as complete.</summary>
    private static readonly string[] WIDER = [];

    private static readonly HttpClient HTTP = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-registration-order", Guid.NewGuid().ToString("n"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return Task.CompletedTask;
    }

    /// <summary>Axis A: when the process's choice is made.</summary>
    private enum ChoiceTime { AtThisCall, Earlier }

    /// <summary>Axis B: what the answering collector knows of the Host session at this call.</summary>
    private enum Knowledge { Knows, Lost, NeverKnew, Gone }

    /// <summary>Axis C: the call.</summary>
    private enum CallKind { Begin, Declaration, Done }

    /// <summary>One cell of the matrix.</summary>
    /// <param name="Choice">Axis A.</param>
    /// <param name="Collector">Axis B.</param>
    /// <param name="Call">Axis C.</param>
    private sealed record Cell(ChoiceTime Choice, Knowledge Collector, CallKind Call);

    /// <summary>What a cell must come to.</summary>
    /// <param name="Closes">Whether the call closes a phase.</param>
    /// <param name="Counters">Whether the closed phase's record carries counters.</param>
    /// <param name="Missing">The closed phase's <c>usageMissing</c>, if any.</param>
    /// <param name="Failures">The <c>orchestrator.telemetry.failed</c> kinds the call writes, in order.</param>
    /// <param name="Restarted">How many <c>orchestrator.collector.restarted</c> entries the call writes.</param>
    /// <param name="KnowsAfter">Whether the collector on the port knows the session after the call; null when nothing answers there.</param>
    /// <param name="CoversOpening">Whether the call opens a phase, which must be covered from its opening.</param>
    private sealed record Expectation(bool Closes, bool Counters, string? Missing, string[] Failures, int Restarted, bool? KnowsAfter,
                                      bool CoversOpening);

    // ---- The matrix ----

    [Fact]
    public async Task Every_cell_is_exact_and_the_wider_list_is_empty()
    {
        Assert.Empty(WIDER);
        Assert.All(WIDER, name => Assert.Contains(Cells(), cell => cell.ToString() == name));

        var problems = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Cells(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (cell, _) =>
        {
            var found = new List<string>();
            await using (var world = new World(_root))
            {
                try
                {
                    await RunCellAsync(cell, world, found);
                }
                catch (Exception error)
                {
                    found.Add(error.ToString());
                }
            }

            foreach (var problem in found) problems.Add($"{cell}: {problem}");
        });

        Assert.True(problems.IsEmpty, string.Join(Environment.NewLine, problems.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        var cells = Cells().ToList();

        Assert.Equal(Enum.GetValues<ChoiceTime>(), cells.Select(cell => cell.Choice).Distinct().Order());
        Assert.Equal(Enum.GetValues<Knowledge>(), cells.Select(cell => cell.Collector).Distinct().Order());
        Assert.Equal(Enum.GetValues<CallKind>(), cells.Select(cell => cell.Call).Distinct().Order());
    }

    [Fact]
    public void The_excluded_combinations_are_the_only_ones_missing()
    {
        var all = from choice in Enum.GetValues<ChoiceTime>()
                  from collector in Enum.GetValues<Knowledge>()
                  from call in Enum.GetValues<CallKind>()
                  select new Cell(choice, collector, call);

        var missing = all.Except(Cells()).ToHashSet();

        var excluded = Enum.GetValues<CallKind>()
                           .SelectMany(call => new[]
                           {
                               new Cell(ChoiceTime.Earlier, Knowledge.NeverKnew, call),
                               new Cell(ChoiceTime.AtThisCall, Knowledge.Gone, call),
                           })
                           .ToHashSet();
        Assert.Equal(excluded.Select(cell => cell.ToString()).Order(), missing.Select(cell => cell.ToString()).Order());
        Assert.Equal(2 * 4 * 3 - 2 * 3, Cells().Count());
    }

    // ---- The expectation ----

    private static IEnumerable<Cell> Cells() =>
        from choice in Enum.GetValues<ChoiceTime>()
        from collector in Enum.GetValues<Knowledge>()
        from call in Enum.GetValues<CallKind>()
        let cell = new Cell(choice, collector, call)
        where Allowed(cell)
        select cell;

    /// <summary>The combinations that cannot occur.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) =>
        // An earlier choice registered the session with this collector, so it cannot never have known it.
        !(cell.Choice == ChoiceTime.Earlier && cell.Collector == Knowledge.NeverKnew)
        // A choice at this call starts the collector when none answers, which is "it never knew it".
        && !(cell.Choice == ChoiceTime.AtThisCall && cell.Collector == Knowledge.Gone);

    /// <summary>Written from R7, R9 and R10, never from the code under test.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(Cell cell)
    {
        var closes = cell.Call != CallKind.Begin;
        var opens = cell.Call != CallKind.Done;
        (bool Counters, string? Missing, string[] Failures) close = !closes
            ? (false, null, [])
            : cell.Collector switch
            {
                // R7: a session the collector knows gives the phase's counters.
                Knowledge.Knows => (true, null, []),
                // R7, R9: the read comes before any registration the call makes, so a session the collector
                // lost or never knew is unknown at the close.
                Knowledge.Lost or Knowledge.NeverKnew => (false, "source_failed", ["session_unknown"]),
                // R10: a collector that is gone is a source that failed.
                _ => (false, "source_failed", ["source_failed"]),
            };

        // R9, R10: an opening that finds no collector restarts it after the read and writes one entry; done opens nothing.
        var restarted = opens && cell.Collector == Knowledge.Gone ? 1 : 0;

        // R7, R9: after the call the collector knows the session — the read registers it again at once, an
        // opening registers it — except done with the collector gone, which restarts nothing.
        bool? knowsAfter = !opens && cell.Collector == Knowledge.Gone ? null : true;

        // R9: a phase the call opens is covered from its opening.
        return new Expectation(closes, close.Counters, close.Missing, close.Failures, restarted, knowsAfter, opens);
    }

    /// <summary>Arranges the cell, makes its call, and writes every difference from its expectation.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="world">The cell's world.</param>
    /// <param name="problems">Where a difference is written.</param>
    private static async Task RunCellAsync(Cell cell, World world, List<string> problems)
    {
        var expected = Expected(cell);
        var owner = world.NewOwner();
        string? closed = null;

        var opening = world.Clock.GetUtcNow();
        if (cell.Choice == ChoiceTime.Earlier)
        {
            // An earlier call of this process chose the export, finding a collector; its opening registered the session.
            world.StartCollector();
            var first = await world.BeginAsync(owner.Phases);
            if (cell.Call != CallKind.Begin) closed = first;
        }
        else
        {
            // An earlier server process of the same Host session registered it before opening the phase.
            if (cell.Collector != Knowledge.NeverKnew)
            {
                world.StartCollector();
                await world.RegisterAsync();
            }

            if (cell.Call != CallKind.Begin) closed = await world.BeginAsync(world.Phases(NoUsageSource.Instance));
        }

        // One request inside the phase this call closes, kept by whatever collector answers now.
        if (closed is not null && world.Serving) await world.PostAsync(opening.AddSeconds(10));

        if (cell.Collector == Knowledge.Lost) await world.RestartCollectorAsync();
        if (cell.Collector == Knowledge.Gone) await world.StopCollectorAsync();
        world.Clock.Advance(TimeSpan.FromSeconds(30));

        var arrival = world.Clock.GetUtcNow();
        var chosenBefore = closed is null ? 0 : world.Chosen(closed).Count;
        string target;
        if (cell.Call == CallKind.Begin)
        {
            target = await world.BeginAsync(owner.Phases);
        }
        else
        {
            target = closed!;
            await world.DeclareAsync(owner.Phases, target, cell.Call == CallKind.Done ? "done" : "planning");
        }

        Expect(problems, TELEMETRY, owner.Choice.Name, "the source");
        // R6: the choice is written once, by the call that made it, to the Run it serves.
        var chosen = world.Chosen(target).Skip(chosenBefore).ToList();
        Expect(problems, cell.Choice == ChoiceTime.AtThisCall ? 1 : 0, chosen.Count, "choice entries the call wrote");
        if (cell.Choice == ChoiceTime.AtThisCall && chosen.Count == 1)
        {
            Expect(problems, TELEMETRY, Field(chosen[0], "usageSource"), "the entry's source");
            Expect(problems, cell.Collector == Knowledge.NeverKnew ? "started" : "found", Field(chosen[0], "collector"), "the entry's collector");
        }

        if (expected.Closes)
        {
            var records = world.Records(target);
            Expect(problems, 1, records.Count, "records after the call");
            if (records.Count > 0) CheckRecord(problems, world, records[0], "interview", expected.Counters, expected.Missing, "the closed phase");
        }
        else
        {
            Expect(problems, 0, world.Records(target).Count, "records after the call");
        }

        var failures = world.Failures(target);
        Expect(problems, string.Join(",", expected.Failures), string.Join(",", failures), "failures");
        Expect(problems, expected.Restarted, world.Count(target, RESTARTED), "restarted entries");
        Expect(problems, expected.KnowsAfter, await world.KnowsAsync(), "whether the collector knows the session after the call");

        if (!expected.CoversOpening) return;

        // The phase the call opened, closed by the same process with one request inside it.
        await world.PostAsync(arrival.AddSeconds(10));
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        await world.DeclareAsync(owner.Phases, target, "done");

        var all = world.Records(target);
        Expect(problems, expected.Closes ? 2 : 1, all.Count, "records after the opened phase closed");
        if (all.Count > 0)
            CheckRecord(problems, world, all[^1], cell.Call == CallKind.Begin ? "interview" : "planning", counters: true, missing: null, "the opened phase");
        Expect(problems, string.Join(",", failures), string.Join(",", world.Failures(target)), "failures after the opened phase closed");
    }

    private static void CheckRecord(List<string> problems, World world, JsonElement record, string phase, bool counters, string? missing,
                                    string what)
    {
        Expect(problems, phase, Field(record, "phase"), $"{what}: phase");
        Expect(problems, TELEMETRY, Field(record, "usageSource"), $"{what}: usageSource");
        Expect(problems, world.Session, Field(record, "hostSessionId"), $"{what}: hostSessionId");
        Expect(problems, missing, Field(record, "usageMissing"), $"{what}: usageMissing");
        Expect(problems, false, record.TryGetProperty("usageIncomplete", out _), $"{what}: usageIncomplete");
        Expect(problems, counters ? INPUT : (long?)null, Number(record, "inputTokens"), $"{what}: inputTokens");
        Expect(problems, counters ? CACHE_CREATION : (long?)null, Number(record, "cacheCreationTokens"), $"{what}: cacheCreationTokens");
        Expect(problems, counters ? OUTPUT : (long?)null, Number(record, "outputTokens"), $"{what}: outputTokens");
        Expect(problems, counters ? COST : (decimal?)null, record.TryGetProperty("costUsd", out var cost) ? cost.GetDecimal() : (decimal?)null,
               $"{what}: costUsd");
    }

    private static string? Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static void Expect<T>(List<string> problems, T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            problems.Add($"{what}: expected {expected?.ToString() ?? "none"}, got {actual?.ToString() ?? "none"}");
    }

    // ---- Beyond the cells ----

    [Fact]
    public async Task A_declaration_closing_a_phase_whose_collector_is_gone_reads_then_restarts_within_10_s_then_registers_within_2_s()
    {
        await using var world = new World(_root);
        var owner = world.NewOwner();
        world.StartCollector();
        var runId = await world.BeginAsync(owner.Phases);
        await world.StopCollectorAsync();
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        world.Clock.ClearTimeline();

        await world.DeclareAsync(owner.Phases, runId, "planning");

        // The close's 6 s wait, then the read, which finds nothing listening; the opening's 2 s probe; the
        // restart under its 10 s deadline, each probe under 2 s; then the registration under 2 s.
        Assert.Equal(["wait 6s", "deadline 2s", "deadline 10s", "deadline 2s", "start", "wait 0.1s", "deadline 2s", "deadline 2s"],
                     world.Clock.Timeline);
        var events = world.LogEntries(runId).Select(entry => entry.GetProperty("event").GetString()).ToList();
        Assert.True(events.IndexOf(OrchestratorPhases.FAILED) < events.IndexOf(RESTARTED), string.Join(", ", events));
        Assert.Equal(["source_failed"], world.Failures(runId));
        Assert.True(await world.KnowsAsync());

        // A restart that never answers ends at its deadline, ten seconds on the clock after it began.
        await using var hung = new World(_root);
        var other = hung.NewOwner();
        hung.StartCollector();
        var hungRun = await hung.BeginAsync(other.Phases);
        await hung.StopCollectorAsync();
        hung.Starting = () => new CollectorStart(CollectorStartOutcome.Started, Environment.ProcessId);
        var before = hung.Clock.GetUtcNow();

        await hung.DeclareAsync(other.Phases, hungRun, "planning");

        Assert.Equal(TimeSpan.FromSeconds(6 + 10), hung.Clock.GetUtcNow() - before);
        Assert.Equal(["source_failed", "registration_failed"], hung.Failures(hungRun));
    }

    [Fact]
    public async Task A_failed_restart_is_registration_failed_and_none_is_tried_again_for_five_minutes()
    {
        await using var world = new World(_root);
        var owner = world.NewOwner();
        world.StartCollector();
        var runId = await world.BeginAsync(owner.Phases);
        await world.StopCollectorAsync();
        world.Starting = () => new CollectorStart(CollectorStartOutcome.Failed);
        world.Clock.Advance(TimeSpan.FromSeconds(30));

        await world.DeclareAsync(owner.Phases, runId, "planning");
        Assert.Equal(1, world.Starts);
        Assert.Equal(["source_failed", "registration_failed"], world.Failures(runId));

        world.Clock.Advance(TimeSpan.FromMinutes(4));
        await world.DeclareAsync(owner.Phases, runId, "approval");
        Assert.Equal(1, world.Starts);
        Assert.Equal(["source_failed", "registration_failed", "source_failed", "registration_failed"], world.Failures(runId));

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Starting = null;
        await world.DeclareAsync(owner.Phases, runId, "wrap_up");
        Assert.Equal(2, world.Starts);
        Assert.Equal(["source_failed", "registration_failed", "source_failed", "registration_failed", "source_failed"], world.Failures(runId));
        Assert.Equal(1, world.Count(runId, RESTARTED));
        Assert.True(await world.KnowsAsync());
    }

    [Fact]
    public async Task A_deciding_call_that_closes_a_phase_chooses_within_10_s_waits_11_s_reads_then_registers_within_2_s()
    {
        await using var world = new World(_root, interval: 10_000);
        world.StartCollector();
        await world.RegisterAsync();
        var opening = world.Clock.GetUtcNow();
        var runId = await world.BeginAsync(world.Phases(NoUsageSource.Instance));
        await world.PostAsync(opening.AddSeconds(10));
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        var owner = world.NewOwner();
        world.Clock.ClearTimeline();
        var before = world.Clock.GetUtcNow();

        await world.DeclareAsync(owner.Phases, runId, "done");

        // The choice under its 10 s deadline, finding the collector with one 2 s probe; the 11 s wait; the
        // read's 2 s request; then the choice's registration under 2 s.
        Assert.Equal(["deadline 10s", "deadline 2s", "wait 11s", "deadline 2s", "deadline 2s"], world.Clock.Timeline);
        Assert.Equal(TimeSpan.FromSeconds(11), world.Clock.GetUtcNow() - before);
        var record = Assert.Single(world.Records(runId));
        Assert.Equal(INPUT, record.GetProperty("inputTokens").GetInt64());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.Equal(TELEMETRY, Field(Assert.Single(world.Chosen(runId)), "usageSource"));
        Assert.Empty(world.Failures(runId));
    }

    [Fact]
    public async Task A_declaration_arriving_while_the_choice_is_pending_waits_for_it_and_uses_the_same_choice_logged_once()
    {
        await using var world = new World(_root);
        var earlier = world.Phases(NoUsageSource.Instance);
        var first = await world.BeginAsync(earlier);
        var second = await world.BeginAsync(earlier);
        world.Clock.Advance(TimeSpan.FromSeconds(30));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        world.Starting = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            world.StartCollector();
            return new CollectorStart(CollectorStartOutcome.Started, Environment.ProcessId);
        };
        var owner = world.NewOwner();

        var deciding = world.DeclareAsync(owner.Phases, first, "planning");
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))));
        var waiting = world.DeclareAsync(owner.Phases, second, "planning");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        Assert.Equal("interview", world.PhaseFile(second).GetProperty("phase").GetString());

        release.Set();
        await Task.WhenAll(deciding, waiting);

        Assert.Equal(1, world.Starts);
        Assert.Equal(1, world.ProfileReads);
        Assert.Equal(TELEMETRY, Field(Assert.Single(world.Chosen(first)), "usageSource"));
        Assert.Empty(world.Chosen(second));
        Assert.Equal(TELEMETRY, Field(Assert.Single(world.Records(first)), "usageSource"));
        Assert.Equal(TELEMETRY, Field(Assert.Single(world.Records(second)), "usageSource"));
    }

    [Fact]
    public async Task A_choice_whose_profile_read_blocks_ends_at_its_10_s_deadline_with_the_transcript()
    {
        await using var world = new World(_root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        world.Reading = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            var before = world.Clock.GetUtcNow();
            var deciding = world.BeginAsync(world.NewOwner().Phases);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))));
            await Task.Delay(200);
            Assert.False(deciding.IsCompleted);

            world.Clock.Advance(TimeSpan.FromSeconds(10));
            var runId = await deciding.WaitAsync(TimeSpan.FromSeconds(10));

            // The call returned at the deadline while the read still blocks; the late read is discarded.
            Assert.False(release.IsSet);
            Assert.Equal(TimeSpan.FromSeconds(10), world.Clock.GetUtcNow() - before);
            var chosen = Assert.Single(world.Chosen(runId));
            Assert.Equal("transcript", Field(chosen, "usageSource"));
            Assert.Equal("the profile was not read within 10 seconds", Field(chosen, "reason"));
            Assert.Equal(0, world.Starts);
            Assert.Empty(world.Failures(runId));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_choice_whose_starter_blocks_ends_at_its_10_s_deadline_with_the_transcript()
    {
        await using var world = new World(_root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        world.Starting = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new CollectorStart(CollectorStartOutcome.Failed);
        };

        try
        {
            var before = world.Clock.GetUtcNow();
            var deciding = world.BeginAsync(world.NewOwner().Phases);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))));
            await Task.Delay(200);
            Assert.False(deciding.IsCompleted);

            world.Clock.Advance(TimeSpan.FromSeconds(10));
            var runId = await deciding.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(release.IsSet);
            Assert.Equal(TimeSpan.FromSeconds(10), world.Clock.GetUtcNow() - before);
            var chosen = Assert.Single(world.Chosen(runId));
            Assert.Equal("transcript", Field(chosen, "usageSource"));
            Assert.Equal($"no collector answered on port {world.Port} within 10 seconds", Field(chosen, "reason"));
            Assert.Empty(world.Failures(runId));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_restart_whose_starter_blocks_ends_at_its_10_s_deadline_as_registration_failed()
    {
        await using var world = new World(_root);
        var owner = world.NewOwner();
        world.StartCollector();
        var runId = await world.BeginAsync(owner.Phases);
        await world.StopCollectorAsync();
        world.Clock.Advance(TimeSpan.FromSeconds(30));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        world.Starting = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new CollectorStart(CollectorStartOutcome.Failed);
        };

        try
        {
            var before = world.Clock.GetUtcNow();
            var declaring = world.DeclareAsync(owner.Phases, runId, "planning");
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(30))));
            await Task.Delay(200);
            Assert.False(declaring.IsCompleted);

            world.Clock.Advance(TimeSpan.FromSeconds(10));
            await declaring.WaitAsync(TimeSpan.FromSeconds(10));

            // The close's 6 s wait, then the restart's 10 s; the opening is registration_failed, once.
            Assert.False(release.IsSet);
            Assert.Equal(TimeSpan.FromSeconds(6 + 10), world.Clock.GetUtcNow() - before);
            Assert.Equal(["source_failed", "registration_failed"], world.Failures(runId));
            Assert.Equal(0, world.Count(runId, RESTARTED));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_phase_registered_after_its_opening_and_closed_by_a_fresh_owner_is_not_incomplete()
    {
        // The clock a minute behind, so the opening call's registration comes after the phase's opening.
        await using var world = new World(_root, start: DateTimeOffset.UtcNow.AddMinutes(-1));
        world.StartCollector();
        var runId = await world.BeginAsync(world.NewOwner().Phases);
        var file = world.PhaseFile(runId);
        Assert.True(file.GetProperty("registeredAt").GetDateTimeOffset() > file.GetProperty("openedAt").GetDateTimeOffset());

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.PostAsync(DateTimeOffset.UtcNow);
        await world.DeclareAsync(world.NewOwner().Phases, runId, "done");

        var record = Assert.Single(world.Records(runId));
        Assert.Equal(INPUT, record.GetProperty("inputTokens").GetInt64());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.False(record.TryGetProperty("usageMissing", out _));
        Assert.Empty(world.Failures(runId));
    }

    [Fact]
    public async Task The_same_phase_whose_collector_restarted_after_its_opening_is_closed_with_session_unknown()
    {
        await using var world = new World(_root, start: DateTimeOffset.UtcNow.AddMinutes(-1));
        world.StartCollector();
        var runId = await world.BeginAsync(world.NewOwner().Phases);
        await world.PostAsync(DateTimeOffset.UtcNow);
        await world.RestartCollectorAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.DeclareAsync(world.NewOwner().Phases, runId, "done");

        var record = Assert.Single(world.Records(runId));
        Assert.Equal("source_failed", record.GetProperty("usageMissing").GetString());
        Assert.False(record.TryGetProperty("inputTokens", out _));
        Assert.Equal(["session_unknown"], world.Failures(runId));
    }

    // ---- A world ----

    /// <summary>
    /// One scenario's repository, port, Claude Code session, profile and fake clock, and the collectors
    /// on its port. A start through the choice owner's starter is an in-process collector on the port,
    /// unless <see cref="Starting"/> says otherwise.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly List<TelemetryCollectorTests.Running> _collectors = [];
        private readonly string _settings;
        private int _starts;
        private int _profileReads;

        /// <summary>Creates a world.</summary>
        /// <param name="root">The folder worlds are made in.</param>
        /// <param name="interval">Claude's export interval, in milliseconds.</param>
        /// <param name="start">Where the fake clock starts; an hour ahead of the real one by default, so every registration precedes every opening.</param>
        public World(string root, int interval = 5000, DateTimeOffset? start = null)
        {
            Root = Path.Combine(root, Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Root);
            Port = TelemetryCollectorTests.FreePort();
            Clock = new FakeClock(start ?? DateTimeOffset.UtcNow.AddHours(1));
            _settings = new JsonObject
            {
                ["env"] = new JsonObject
                {
                    ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
                    ["OTEL_LOGS_EXPORTER"] = "otlp",
                    ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/json",
                    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{Port}",
                    ["OTEL_LOGS_EXPORT_INTERVAL"] = interval.ToString(CultureInfo.InvariantCulture),
                },
            }.ToJsonString();
        }

        public string Root { get; }

        public int Port { get; }

        public string Session { get; } = Guid.NewGuid().ToString();

        public FakeClock Clock { get; }

        /// <summary>What a start through the starter does instead of serving the port; null to serve it.</summary>
        public Func<CollectorStart>? Starting { get; set; }

        /// <summary>What a profile read does before it answers, such as blocking; null to answer at once.</summary>
        public Action? Reading { get; set; }

        public int Starts => Volatile.Read(ref _starts);

        public int ProfileReads => Volatile.Read(ref _profileReads);

        /// <summary>Whether a collector of this world serves the port.</summary>
        public bool Serving
        {
            get { lock (_collectors) return _collectors.Any(collector => !collector.Run.IsCompleted); }
        }

        /// <summary>A fresh server process: its own choice owner and the phase owner that tells it of calls.</summary>
        public (UsageSourceChoice Choice, OrchestratorPhases Phases) NewOwner()
        {
            var choice = new UsageSourceChoice(Clock, Variable, ReadProfile, Start);
            return (choice, Phases(choice));
        }

        /// <summary>A phase owner over <paramref name="source"/>, on this world's clock and environment.</summary>
        /// <param name="source">The Usage source.</param>
        public OrchestratorPhases Phases(IUsageSource source) => new(Clock, source, Variable);

        public void StartCollector()
        {
            lock (_collectors) _collectors.Add(TelemetryCollectorTests.Running.Start(port: Port));
        }

        public async Task StopCollectorAsync()
        {
            TelemetryCollectorTests.Running[] running;
            lock (_collectors) running = [.. _collectors.Where(collector => !collector.Run.IsCompleted)];
            foreach (var collector in running) await collector.DisposeAsync();
        }

        /// <summary>A collector that restarted: the old one gone, with what it kept, and a new one on the port.</summary>
        public async Task RestartCollectorAsync()
        {
            await StopCollectorAsync();
            StartCollector();
        }

        /// <summary>Registers the session as an earlier server process would.</summary>
        public Task RegisterAsync() =>
            new TelemetryUsageSource(new ExportEndpoint("127.0.0.1", Port, TimeSpan.Zero), _ => new CollectorStart(CollectorStartOutcome.Failed),
                                     TimeProvider.System).RegisterAsync(new HostSession(HostSessions.CLAUDE_CODE, Session), CancellationToken.None);

        /// <summary>Posts one API request of the session at <paramref name="at"/>.</summary>
        /// <param name="at">The event time.</param>
        public async Task PostAsync(DateTimeOffset at)
        {
            using var answer = await HTTP.PostAsync(Url("v1/logs"),
                                                    new StringContent(TelemetryCollectorTests.Envelope(TelemetryCollectorTests.ClaudeRequest(Session, at))
                                                                                             .ToJsonString(),
                                                                      Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        }

        /// <summary>Whether the collector on the port knows the session; null when nothing listens there.</summary>
        public async Task<bool?> KnowsAsync()
        {
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == Port)) return null;

            var now = DateTimeOffset.UtcNow;
            using var answer = await HTTP.GetAsync(Url($"planforge/usage?session={Session}&from={Uri.EscapeDataString(now.ToString("o"))}"
                                                       + $"&to={Uri.EscapeDataString(now.ToString("o"))}"));
            return answer.StatusCode == HttpStatusCode.OK;
        }

        public async Task<string> BeginAsync(OrchestratorPhases phases)
        {
            var git = new GitClient(Root);
            var ct = CancellationToken.None;
            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                await git.OutputAsync(["init", "-q"], ct);
                await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
                await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
                await File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "original\n", ct);
                await git.OutputAsync(["add", "tracked.txt"], ct);
                await git.OutputAsync(["commit", "-qm", "initial"], ct);
            }

            var json = await OrchestratorCall.InvokeAsync(phases, Root, null, HostSessions.CLAUDE_CODE, null, async () =>
                await ForgeTools.Begin(SessionRoots.None, HostSessions.CLAUDE_CODE, CapabilityProfile.Text, _ => { }, Root, ct));
            return JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;
        }

        public Task DeclareAsync(OrchestratorPhases phases, string runId, string phase) =>
            OrchestratorCall.InvokeAsync(phases, Root, runId, HostSessions.CLAUDE_CODE, null, async () =>
                await ForgeTools.AppendLog(SessionRoots.None, Root, runId, $"entering {phase}", CancellationToken.None, phase: phase)).AsTask();

        public IReadOnlyList<JsonElement> Records(string runId)
        {
            var path = RunDirectory.Open(Root, runId).TelemetryPath;
            return File.Exists(path) ? [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray()] : [];
        }

        public JsonElement PhaseFile(string runId) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(RunDirectory.Open(Root, runId).Path, OrchestratorPhases.FILE_NAME))).RootElement;

        public IReadOnlyList<JsonElement> LogEntries(string runId)
        {
            var path = RunDirectory.Open(Root, runId).DiagnosticLogPath;
            if (!File.Exists(path)) return [];
            return [.. AtomicFile.Read(path)
                                 .Split('\n')
                                 .Select(line => line.TrimEnd('\r'))
                                 .Where(line => line.Length > 0)
                                 .Select(line => JsonDocument.Parse(line).RootElement)];
        }

        public IReadOnlyList<string> Failures(string runId) =>
            [.. LogEntries(runId).Where(entry => entry.GetProperty("event").GetString() == OrchestratorPhases.FAILED)
                                 .Select(entry => entry.GetProperty("fields").GetProperty("kind").GetString()!)];

        public int Count(string runId, string name) => LogEntries(runId).Count(entry => entry.GetProperty("event").GetString() == name);

        public IReadOnlyList<JsonElement> Chosen(string runId) =>
            [.. LogEntries(runId).Where(entry => entry.GetProperty("event").GetString() == CHOSEN).Select(entry => entry.GetProperty("fields"))];

        public async ValueTask DisposeAsync() => await StopCollectorAsync();

        private string Url(string path) => $"http://127.0.0.1:{Port}/{path}";

        private string? Variable(string name) => name switch
        {
            "CLAUDE_CODE_SESSION_ID" => Session,
            "CLAUDE_CONFIG_DIR" => Path.Combine(Root, "claude"),
            "CODEX_HOME" => Path.Combine(Root, "codex"),
            _ => null,
        };

        private string? ReadProfile(string path)
        {
            Interlocked.Increment(ref _profileReads);
            Reading?.Invoke();
            return path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == "settings.json" ? _settings : null;
        }

        private CollectorStart Start(int port)
        {
            Interlocked.Increment(ref _starts);
            Clock.Mark("start");
            if (Starting is { } starting) return starting();

            StartCollector();
            return new CollectorStart(CollectorStartOutcome.Started, Environment.ProcessId);
        }
    }

    /// <summary>
    /// A clock that moves only when a test or a wait moves it. A deadline — a timer whose state is a
    /// <see cref="CancellationTokenSource"/> — fires only once the clock passes it; a wait, such as a
    /// <c>Task.Delay</c>, moves the clock to its end at once. Every timer is written to the timeline as
    /// <c>deadline Ns</c> or <c>wait Ns</c>.
    /// </summary>
    /// <param name="start">Where the clock starts.</param>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly HashSet<FakeTimer> _scheduled = [];
        private readonly List<string> _timeline = [];
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public IReadOnlyList<string> Timeline
        {
            get { lock (_gate) return [.. _timeline]; }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public void ClearTimeline()
        {
            lock (_gate) _timeline.Clear();
        }

        public void Mark(string what)
        {
            lock (_gate) _timeline.Add(what);
        }

        /// <summary>Moves the clock on and fires every timer it passes, earliest first.</summary>
        /// <param name="span">How far.</param>
        public void Advance(TimeSpan span)
        {
            List<FakeTimer> due;
            lock (_gate)
            {
                _now += span;
                due = [.. _scheduled.Where(timer => timer.DueAt <= _now).OrderBy(timer => timer.DueAt)];
                foreach (var timer in due)
                {
                    _scheduled.Remove(timer);
                    timer.DueAt = null;
                }
            }

            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var deadline = state is CancellationTokenSource;
            var timer = new FakeTimer(this, callback, state, deadline);
            Mark($"{(deadline ? "deadline" : "wait")} {dueTime.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s");
            timer.Change(dueTime, period);
            if (!deadline && dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Advance(dueTime);
            return timer;
        }

        /// <summary>Schedules <paramref name="timer"/> <paramref name="dueTime"/> from now, or unschedules it when infinite.</summary>
        /// <param name="timer">The timer.</param>
        /// <param name="dueTime">When it is due.</param>
        public void Schedule(FakeTimer timer, TimeSpan dueTime)
        {
            lock (_gate)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _scheduled.Remove(timer);
                    timer.DueAt = null;
                    return;
                }

                timer.DueAt = _now + dueTime;
                _scheduled.Add(timer);
            }

            if (dueTime <= TimeSpan.Zero) Advance(TimeSpan.Zero);
        }
    }

    /// <summary>A timer of <see cref="FakeClock"/>: a deadline fires on the thread that moves the clock, a wait on the thread pool.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="callback">What it calls.</param>
    /// <param name="state">The callback's state.</param>
    /// <param name="deadline">Whether it cancels a token rather than ends a wait.</param>
    private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state, bool deadline) : ITimer
    {
        /// <summary>When it fires; null while unscheduled. Read and written under the clock's lock.</summary>
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Schedule(this, dueTime);
            return true;
        }

        public void Fire()
        {
            if (deadline) callback(state);
            else _ = Task.Run(() => callback(state));
        }

        public void Dispose() => clock.Schedule(this, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
