using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The climb of an admitted call's completion through the waiting levels above it: the work of the call's runs is complete
/// at a wait only on the exits of that wait that follow its completion — the normal exit always, the exceptional one as the join
/// reading gives it (an <c>await</c>, yes; a blocking wait of a <c>Task</c> under ADR 0030 with its keys counted in <c>assumed</c>;
/// a <c>ValueTask</c> and anything else, no) — and, for code reached through the suspension of an async body, only where that
/// body's task is waited by its caller, level by level (R1, R2, R4). The pair is the continuation of the call's delegate with the
/// synchronous part of the next run, with another continuation and with the code after the wait; a removed pair is decided
/// <c>Ordered</c> under exactly the assumptions of the exits its proof reads.</summary>
public sealed class PerPassClimbTests
{
    /// <summary>The expectation of a side whose pair stays.</summary>
    private const string PAIR = "pair";

    /// <summary>The expectation of a side whose pair goes and rests on no assumption.</summary>
    private const string ORDERED = "ordered:";

    /// <summary>The expectation of a side whose pair goes by the exceptional exit of a blocking wait of a worker off the thread pool.</summary>
    private const string BLOCKING = "ordered:observers,thread-context";

    /// <summary>The expectation of a side whose pair goes by the exceptional exit of a blocking wait of work on the thread pool.</summary>
    private const string POOLED = "ordered:observers,thread-pool-entry";

    /// <summary>The members of every Polly row: the seeding call and its callers, the outer admitted call, and the three writes — the
    /// synchronous part of a run (<c>Sync</c>), its continuation (<c>Cont</c>) and the parent's own (<c>Mine</c>).</summary>
    private const string MEMBERS = """
        private readonly AsyncRetryPolicy _seed = Policy.Handle<Exception>().RetryAsync(2);
        private readonly AsyncRetryPolicy _outer = Policy.Handle<Exception>().RetryAsync(2);
        private static void MayThrow() { if (Environment.TickCount % 2 == 0) throw new InvalidOperationException(); }
        private void Sync() { Value = 1; }
        private void Cont() { Value = 2; }
        private void Mine() { Value = 3; }
        private async Task Seed()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Sync();
                await Task.Yield();
                Cont();
            });
        }
        private async Task<int> SeedOf()
        {
            await _seed.ExecuteAsync(async () =>
            {
                Sync();
                await Task.Yield();
                Cont();
            });
            return 1;
        }
        """;

    /// <summary>The members of every <c>Runs.Lib</c> row: the seeding call, whose record runs its delegate one run at a time and
    /// awaits each run's task, and the three writes.</summary>
    private const string RUNS_MEMBERS = """
        private void Sync() { _state.C = 3; }
        private void Cont() { _state.C = 1; }
        private void Mine() { _state.C = 2; }
        private async Task Seed() { await Runs.Lib.Run(async () => { Sync(); await Task.Yield(); Cont(); }); }
        """;

    private const string INSTALLER = """

        public sealed class Installer : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>The statement that makes a context reachable for a <c>Runs.Lib</c> program: the other hosted service installs it.</summary>
    private const string RUNS_INSTALLER = "SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());";

    /// <summary>The outer admitted call whose delegate waits for the seeding.</summary>
    private const string OUTER = "_outer.ExecuteAsync(async () => { var s = Seed(); await s; })";

    private const string LOOP = "for (var pass = 0; pass < 2; pass++) { ";

    private const string CATCH = " } catch (Exception) { }";

    private static readonly ConcurrentDictionary<string, Lazy<Observed>> Runs = new(StringComparer.Ordinal);

    /// <summary>What a program says about each of the three sides: whether a pair is left and the decisions of the accesses.</summary>
    /// <param name="Pair">Whether the engine reports a pair of the two writes.</param>
    /// <param name="Decisions">The decisions of the pairs reported, or of every combination of the accesses when none is.</param>
    private sealed record Side(bool Pair, IReadOnlyList<string> Decisions);

    /// <summary>The three sides of a program.</summary>
    /// <param name="Sync">The continuation and the synchronous part of a run.</param>
    /// <param name="Self">The continuation and another continuation.</param>
    /// <param name="Mine">The continuation and the parent's own write.</param>
    private sealed record Observed(Side Sync, Side Self, Side Mine);

    /// <summary>One row of a theory: the program and what each of its three sides must say.</summary>
    /// <param name="Label">What the row is, first, so that a failure names it.</param>
    /// <param name="Body">The body of the worker's <c>ExecuteAsync</c>.</param>
    /// <param name="Members">The members of the row beside the common ones.</param>
    /// <param name="Other">For a Polly row, <c>installer</c> when a hosted service installs a synchronization context; for a
    /// <c>Runs.Lib</c> row, what the other hosted service does.</param>
    /// <param name="Sync">The expectation of the next synchronous part.</param>
    /// <param name="Self">The expectation of the next continuation.</param>
    /// <param name="Mine">The expectation of the code after the wait.</param>
    private sealed record Row(string Label, string Body, string Members, string Other, string Sync, string Self, string Mine);

    public static TheoryData<string, string, string, string, string, string, string> PollyRows()
    {
        var rows = new TheoryData<string, string, string, string, string, string, string>();
        foreach (var row in OuterRows().Concat(ChainRows()).Concat(LevelRows()))
            rows.Add(row.Label, row.Body, row.Members, row.Other, row.Sync, row.Self, row.Mine);
        return rows;
    }

    public static TheoryData<string, string, string, string, string, string, string> RunsRows()
    {
        var rows = new TheoryData<string, string, string, string, string, string, string>();
        foreach (var row in OuterFormRows())
            rows.Add(row.Label, row.Body, row.Members, row.Other, row.Sync, row.Self, row.Mine);
        return rows;
    }

    /// <summary>The wait for the task of an outer admitted call, and what the rest of the execution makes of it: the exits of the wait
    /// as the join reading gives them, and a call whose task nobody waits for at once.</summary>
    private static IEnumerable<Row> OuterRows()
    {
        // The exits of the wait for the outer call's task, read as the join reading gives them: a blocking wait of a task is the end of
        // the work on its exceptional exit when no context is installed, and counts under the keys of that assumption.
        yield return new Row("outer-wait-caught", $"try {{ {OUTER}.Wait();{CATCH} Mine();", "", "", ORDERED, ORDERED, BLOCKING);
        yield return new Row("outer-wait-caught-installer", $"try {{ {OUTER}.Wait();{CATCH} Mine();", "", "installer", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-get-caught", $"try {{ {OUTER}.GetAwaiter().GetResult();{CATCH} Mine();", "", "", ORDERED, ORDERED, BLOCKING);
        yield return new Row("outer-get-caught-installer", $"try {{ {OUTER}.GetAwaiter().GetResult();{CATCH} Mine();", "", "installer", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-wait-all-caught", $"try {{ var t = {OUTER}; Task.WaitAll(t);{CATCH} Mine();", "", "", ORDERED, ORDERED, BLOCKING);
        yield return new Row("outer-wait-all-caught-installer", $"try {{ var t = {OUTER}; Task.WaitAll(t);{CATCH} Mine();", "", "installer", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-await-caught", $"try {{ await {OUTER};{CATCH} Mine();", "", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-await-caught-installer", $"try {{ await {OUTER};{CATCH} Mine();", "", "installer", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-wait-uncaught", $"{OUTER}.Wait(); Mine();", "", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-wait-uncaught-installer", $"{OUTER}.Wait(); Mine();", "", "installer", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-wait-caught-on-the-pool", $"await Task.Run(() => {{ try {{ {OUTER}.Wait();{CATCH} Mine(); }});", "", "", ORDERED, ORDERED, POOLED);
        yield return new Row("outer-wait-caught-on-the-pool-installer", $"await Task.Run(() => {{ try {{ {OUTER}.Wait();{CATCH} Mine(); }});", "", "installer",
                             ORDERED, ORDERED, PAIR);
        yield return new Row("outer-await-caught-on-the-pool", $"await Task.Run(async () => {{ try {{ await {OUTER};{CATCH} Mine(); }});", "", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-wait-in-a-loop-caught", $"{LOOP}try {{ {OUTER}.Wait();{CATCH} Mine(); }}", "", "", BLOCKING, BLOCKING, BLOCKING);
        yield return new Row("outer-wait-in-a-loop-caught-installer", $"{LOOP}try {{ {OUTER}.Wait();{CATCH} Mine(); }}", "", "installer", PAIR, PAIR, PAIR);
        yield return new Row("outer-await-in-a-loop-caught", $"{LOOP}try {{ await {OUTER};{CATCH} Mine(); }}", "", "", ORDERED, ORDERED, ORDERED);

        // The next run of the outer call's delegate follows the library's wait of the run's task, whatever happens to the outer call's
        // task; only the code after the outer call's return, and a next pass of the call itself, need that task waited.
        yield return new Row("outer-unwaited", $"{OUTER}; Mine();", "", "", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-waited-after-may-throw", $"var t = {OUTER}; MayThrow(); await t; Mine();", "", "", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-when-all", $"await Task.WhenAll({OUTER}); Mine();", "", "", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-unwaited-in-a-loop", $"{LOOP}{OUTER}; Mine(); }}", "", "", PAIR, PAIR, PAIR);
        yield return new Row("outer-waited-after-may-throw-in-a-loop", $"{LOOP}var t = {OUTER}; MayThrow(); await t; Mine(); }}", "", "", PAIR, PAIR, PAIR);
        yield return new Row("outer-awaited-in-a-loop", $"{LOOP}await {OUTER}; Mine(); }}", "", "", ORDERED, ORDERED, ORDERED);

        // The call is waited for in a body of its own, which suspends: the work is complete for the pass where the pass waits that body.
        const string HELPER = "private async Task Helper() { await " + OUTER + "; }";
        yield return new Row("outer-awaited-in-a-helper", "var h = Helper(); await h; Mine();", HELPER, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-awaited-in-a-helper-in-a-loop", $"{LOOP}var h = Helper(); await h; Mine(); }}", HELPER, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-awaited-in-a-helper-waited-late", "var h = Helper(); Mine(); await h;", HELPER, "", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-awaited-in-a-helper-unwaited-in-a-loop", $"{LOOP}var h = Helper(); Mine(); }}", HELPER, "", PAIR, PAIR, PAIR);
        const string HELPER_TAIL = "private async Task Helper() { await " + OUTER + "; await Task.Yield(); Mine(); }";
        yield return new Row("outer-awaited-in-a-helper-whose-tail-writes", "var h = Helper(); await h;", HELPER_TAIL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-awaited-in-a-helper-whose-tail-writes-unwaited", "var h = Helper();", HELPER_TAIL, "", ORDERED, ORDERED, PAIR);
    }

    /// <summary>A body with nothing after its last <c>await</c> has no tail: it completes where that <c>await</c> resumes, and the
    /// climb goes on from it to the caller's wait for its task, through any number of levels and tails.</summary>
    private static IEnumerable<Row> ChainRows()
    {
        const string NO_TAIL = "private async Task Inner() { await Seed(); } ";
        const string OUTER_TAIL = "private async Task Outer() { var t = Inner(); await t; Mine(); }";
        const string DIRECT_TAIL = "private async Task Outer() { await Inner(); Mine(); }";

        yield return new Row("no-tail-waited", $"{LOOP}await Outer(); }}", NO_TAIL + OUTER_TAIL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("no-tail-outer-unwaited", $"{LOOP}var o = Outer(); }}", NO_TAIL + OUTER_TAIL, "", PAIR, PAIR, PAIR);
        yield return new Row("no-tail-awaited-directly", $"{LOOP}await Outer(); }}", NO_TAIL + DIRECT_TAIL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("no-tail-directly-unwaited", $"{LOOP}var o = Outer(); }}", NO_TAIL + DIRECT_TAIL, "", PAIR, PAIR, PAIR);
        yield return new Row("no-tail-three-levels", $"{LOOP}await Top(); }}", NO_TAIL + OUTER_TAIL + " private async Task Top() { var u = Outer(); await u; }", "",
                             ORDERED, ORDERED, ORDERED);
        yield return new Row("no-tail-three-levels-unwaited-above", $"{LOOP}await Top(); }}",
                             NO_TAIL + OUTER_TAIL + " private async Task Top() { var u = Outer(); await Task.Yield(); }", "", PAIR, PAIR, PAIR);
        yield return new Row("no-tail-three-levels-direct", $"{LOOP}await Top(); }}", NO_TAIL + DIRECT_TAIL + " private async Task Top() { await Outer(); }", "",
                             ORDERED, ORDERED, ORDERED);
        yield return new Row("no-tail-called-twice-before-the-wait", "var a = Outer(); var b = Outer(); await a; await b;", NO_TAIL + OUTER_TAIL, "", PAIR, PAIR, PAIR);

        // The tail of a body above the call: its write follows the work where the body below is waited at once, through an await of the
        // call or of its local, and a caller that awaits the body at once suspends in turn.
        yield return new Row("tail-through-a-local", $"{LOOP}var o = Outer(); await o; }}", NO_TAIL + OUTER_TAIL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("tail-through-a-local-unwaited", $"{LOOP}var o = Outer(); }}", NO_TAIL + OUTER_TAIL, "", PAIR, PAIR, PAIR);
        yield return new Row("tail-through-an-await", $"{LOOP}var o = Outer(); await o; }}", NO_TAIL + DIRECT_TAIL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("tail-through-an-await-unwaited", $"{LOOP}var o = Outer(); }}", NO_TAIL + DIRECT_TAIL, "", PAIR, PAIR, PAIR);
        yield return new Row("tail-through-two-awaits", $"{LOOP}var o = Top(); await o; }}",
                             NO_TAIL + DIRECT_TAIL + " private async Task Top() { await Outer(); Mine(); }", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("tail-through-two-awaits-unwaited-above", $"{LOOP}var o = Top(); }}",
                             NO_TAIL + DIRECT_TAIL + " private async Task Top() { await Outer(); Mine(); }", "", PAIR, PAIR, PAIR);
    }

    /// <summary>A level above the call that waits for the task below in each form, with an exception that reaches the next pass or does
    /// not, and a context that may be installed: a body that awaits at once completes the work on every exit; a blocking wait of a task
    /// does on its exceptional exit only under ADR 0030, which an installed context takes away, and the caller of the level reaches
    /// the next pass by that exit only where it catches.</summary>
    private static IEnumerable<Row> LevelRows()
    {
        (string Form, string Members, string Call, bool Blocking)[] forms =
        [
            ("await-direct", "private async Task Middle() { await Seed(); }", "await Middle();", false),
            ("await-local", "private async Task Middle() { var t = Seed(); await t; }", "await Middle();", false),
            ("wait", "private void Middle() { Seed().Wait(); }", "Middle();", true),
            ("get-result", "private void Middle() { Seed().GetAwaiter().GetResult(); }", "Middle();", true),
            ("wait-all", "private void Middle() { var t = Seed(); Task.WaitAll(t); }", "Middle();", true),
            ("result", "private void Middle() { _ = SeedOf().Result; }", "Middle();", true)
        ];
        foreach (var (form, members, call, blocking) in forms)
        {
            foreach (var caught in new[] { false, true })
            {
                foreach (var installer in new[] { false, true })
                {
                    var body = LOOP + (caught ? $"try {{ {call}{CATCH}" : call) + " Mine(); }";
                    var expected = !blocking || !caught ? ORDERED : installer ? PAIR : BLOCKING;
                    yield return new Row($"level-{form}{(caught ? "-caught" : "")}{(installer ? "-installer" : "")}", body, members, installer ? "installer" : "",
                                         expected, expected, expected);
                }
            }
        }

        // A level that does not wait for the task below, or waits for it where the paths can leave first.
        yield return new Row("level-none", $"{LOOP}await Middle(); Mine(); }}", "private async Task Middle() { var t = Seed(); await Task.Yield(); }", "", PAIR, PAIR, PAIR);
        yield return new Row("level-may-throw-uncaught", $"{LOOP}await Middle(); Mine(); }}", "private async Task Middle() { var t = Seed(); MayThrow(); await t; }", "",
                             ORDERED, ORDERED, ORDERED);
        yield return new Row("level-may-throw-caught", $"{LOOP}try {{ await Middle();{CATCH} Mine(); }}",
                             "private async Task Middle() { var t = Seed(); MayThrow(); await t; }", "", PAIR, PAIR, PAIR);
        yield return new Row("level-when-all", $"{LOOP}await Middle(); Mine(); }}", "private async Task Middle() { await Task.WhenAll(Seed()); }", "", PAIR, PAIR, PAIR);

        // The delegate of an outer admitted call calls a level that waits for the call below.
        const string DELEGATE_LEVEL = "_outer.ExecuteAsync(async () => { await Middle(); })";
        const string LEVEL = "private async Task Middle() { await Seed(); }";
        yield return new Row("outer-delegate-level-awaited", $"await {DELEGATE_LEVEL}; Mine();", LEVEL, "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-delegate-level-unwaited", $"{DELEGATE_LEVEL}; Mine();", LEVEL, "", ORDERED, ORDERED, PAIR);
        yield return new Row("outer-delegate-level-unwaited-in-a-loop", $"{LOOP}{DELEGATE_LEVEL}; Mine(); }}", LEVEL, "", PAIR, PAIR, PAIR);
        yield return new Row("outer-delegate-level-blocked-caught", $"try {{ {DELEGATE_LEVEL}.Wait();{CATCH} Mine();", LEVEL, "", ORDERED, ORDERED, BLOCKING);
        yield return new Row("outer-delegate-level-blocked-caught-installer", $"try {{ {DELEGATE_LEVEL}.Wait();{CATCH} Mine();", LEVEL, "installer", ORDERED, ORDERED, PAIR);
        const string DIRECT = "_outer.ExecuteAsync(async () => { await _seed.ExecuteAsync(async () => { Sync(); await Task.Yield(); Cont(); }); })";
        yield return new Row("outer-delegate-call-awaited", $"await {DIRECT}; Mine();", "", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("outer-delegate-call-awaited-in-a-loop", $"{LOOP}await {DIRECT}; Mine(); }}", "", "", ORDERED, ORDERED, ORDERED);
        yield return new Row("level-blocking-on-the-pool", $"await Task.Run(() => {{ {LOOP}try {{ Middle();{CATCH} Mine(); }} }});",
                             "private void Middle() { Seed().Wait(); }", "", POOLED, POOLED, POOLED);
    }

    /// <summary>The wait for the task of an outer admitted call of the project's library, in every form: <c>Task</c> and
    /// <c>ValueTask</c>, <c>await</c> and each blocking wait, a member that returns no task; with an exception that reaches the code
    /// after the call or does not, and a context that may be installed. The next run follows the library's wait whatever the form.</summary>
    private static IEnumerable<Row> OuterFormRows()
    {
        const string TASK = "Runs.Lib.Run(async () => { var s = Seed(); await s; })";
        const string TASK_OF = "Runs.Lib.TaskOf(async () => { var s = Seed(); await s; return 1; })";
        const string VALUE_OF = "Runs.Lib.ValueOf(async () => { var s = Seed(); await s; return 1; })";
        const string FIRE = "Runs.Lib.Fire(async () => { var s = Seed(); await s; });";
        (string Form, string Statement, string Kind)[] forms =
        [
            ("await", $"await {TASK};", "joined"),
            ("wait", $"{TASK}.Wait();", "task"),
            ("get-result", $"{TASK}.GetAwaiter().GetResult();", "task"),
            ("wait-all", $"var t = {TASK}; Task.WaitAll(t);", "task"),
            ("result", $"_ = {TASK_OF}.Result;", "task"),
            ("value-result", $"_ = {VALUE_OF}.Result;", "value"),
            ("value-get-result", $"_ = {VALUE_OF}.GetAwaiter().GetResult();", "value"),
            ("task-less", FIRE, "joined")
        ];
        foreach (var (form, statement, kind) in forms)
        {
            foreach (var caught in new[] { false, true })
            {
                foreach (var installer in new[] { false, true })
                {
                    var body = (caught ? $"try {{ {statement}{CATCH}" : statement) + " Mine();";
                    var mine = kind == "joined" || !caught ? ORDERED
                        : kind == "value" || installer ? PAIR
                        : BLOCKING;
                    yield return new Row($"runs-{form}{(caught ? "-caught" : "")}{(installer ? "-installer" : "")}", body, "", installer ? RUNS_INSTALLER : "",
                                         ORDERED, ORDERED, mine);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PollyRows))]
    public void Climb_reads_each_wait_as_the_join_reading_gives_it(string label, string body, string members, string other, string sync, string self,
                                                                   string mine)
    {
        var observed = Runs.GetOrAdd("polly|" + label + "|" + body + "|" + members + "|" + other, _ => new Lazy<Observed>(() => ObservePolly(body, members, other))).Value;

        Check(label, "next synchronous part", observed.Sync, sync);
        Check(label, "next continuation", observed.Self, self);
        Check(label, "code after the wait", observed.Mine, mine);
    }

    [Theory]
    [MemberData(nameof(RunsRows))]
    public void Outer_wait_of_the_projects_library_is_read_by_its_form(string label, string body, string members, string other, string sync, string self,
                                                                       string mine)
    {
        var observed = Runs.GetOrAdd("runs|" + label + "|" + body + "|" + members + "|" + other, _ => new Lazy<Observed>(() => ObserveRuns(body, members, other))).Value;

        Check(label, "next synchronous part", observed.Sync, sync);
        Check(label, "next continuation", observed.Self, self);
        Check(label, "code after the wait", observed.Mine, mine);
    }

    /// <summary>Asserts a side against its expectation, naming the row and the side in a failure.</summary>
    /// <param name="label">What the row is.</param>
    /// <param name="name">Which side.</param>
    /// <param name="side">What the program says.</param>
    /// <param name="expected">The expectation: <c>pair</c>, or <c>ordered:</c> and the sorted keys.</param>
    private static void Check(string label, string name, Side side, string expected)
    {
        if (expected == PAIR)
        {
            Assert.True(side.Pair, $"{label}, {name}: the pair the rule keeps is gone.");
            return;
        }

        Assert.False(side.Pair, $"{label}, {name}: the pair the rule removes is still there: {string.Join("; ", side.Decisions)}");
        Assert.All(side.Decisions, decision => Assert.True(decision.EndsWith(" " + expected, StringComparison.OrdinalIgnoreCase),
                                                           $"{label}, {name}: decided {decision}, expected {expected}."));
    }

    /// <summary>Runs a Polly program and reads its three sides.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    /// <param name="members">The members of the row beside the common ones.</param>
    /// <param name="installer">Whether a hosted service installs a synchronization context.</param>
    private static Observed ObservePolly(string body, string members, string installer)
    {
        var source = Usings + "using Polly;\nusing Polly.Retry;\n" + BlockingWaitAssumptionMatrixTests.Worker(body, MEMBERS + members);
        if (installer == "installer")
            source = source.Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>();\nservices.AddHostedService<Installer>();", StringComparison.Ordinal) + INSTALLER;
        var options = new FixtureOptions { MetadataReferences = LibraryPackages.BesideStubs };
        var run = AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", source)), "scope:Fixture");

        return Observe(run, "Value");
    }

    /// <summary>Runs a program over the project's library and reads its three sides.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    /// <param name="members">The members of the row beside the common ones.</param>
    /// <param name="other">What the other hosted service does.</param>
    private static Observed ObserveRuns(string body, string members, string other) =>
        Observe(AwaitedRunsFixture.Run(body, members: RUNS_MEMBERS + members, other: other), "C");

    /// <summary>Reads the three sides of a program.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="field">The field the writes are on.</param>
    private static Observed Observe(EngineRun run, string field) => new(Of(run, field, "Sync"), Of(run, field, "Cont"), Of(run, field, "Mine"));

    /// <summary>The outcome of the continuation of the call's delegate (<c>Cont</c>) and another write.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="field">The field the writes are on.</param>
    /// <param name="other">The method of the other write; <c>Cont</c> itself for the continuation with another continuation.</param>
    private static Side Of(EngineRun run, string field, string other)
    {
        var pair = run.PairsOn(field).Any(found => Is(found.First, "Cont") && Is(found.Second, other) || Is(found.First, other) && Is(found.Second, "Cont"));
        Assert.Contains(run.Of(field), access => access.Operation == AccessOperation.Write && Is(access, "Cont"));
        Assert.Contains(run.Of(field), access => access.Operation == AccessOperation.Write && Is(access, other));
        var accesses = pair
            ? run.PairsOn(field)
                 .Where(found => Is(found.First, "Cont") && Is(found.Second, other) || Is(found.First, other) && Is(found.Second, "Cont"))
                 .Select(found => (First: found.First, Second: found.Second))
                 .ToArray()
            : (from first in Writes(run, field, "Cont")
               from second in Writes(run, field, other)
               select (First: first, Second: second)).ToArray();
        var decisions = accesses.Select(accessed =>
        {
            var timing = run.Execution.Analysis.Timing(accessed.First, accessed.Second, out var assumed);
            return $"{accessed.First.Symbol}@{accessed.First.Source.StartLine} x {accessed.Second.Symbol}@{accessed.Second.Source.StartLine} " +
                   $"{timing}:{string.Join(",", assumed.Order(StringComparer.Ordinal))}";
        }).ToArray();

        return new Side(pair, decisions);
    }

    private static Access[] Writes(EngineRun run, string field, string method) =>
        run.Of(field).Where(access => access.Operation == AccessOperation.Write && Is(access, method)).ToArray();

    private static bool Is(Access access, string method) => access.Symbol.EndsWith($".{method}()", StringComparison.Ordinal);
}
