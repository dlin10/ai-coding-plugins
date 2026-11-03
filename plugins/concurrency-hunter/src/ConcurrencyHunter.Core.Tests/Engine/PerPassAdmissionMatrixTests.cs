using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The per-pass admission of a <c>sequential-awaited</c> call (R1) and the wait it rests on (R2), over the parent running the
/// passes, how many calls a pass makes, how the body of the call waits for its task, how the pass waits for the body, and how many
/// places of the pass make the calls, against the real Polly 7. The call stands in the prefix of <c>Seed</c> and <c>Seed</c> in the
/// prefix of the pass, so one execution that runs once makes it; the pair is the continuation of the call's delegate with the
/// synchronous part of the next run, with another continuation, and with the parent's code after the wait.</summary>
public sealed class PerPassAdmissionMatrixTests
{
    public enum Parent { Loop, HelperTwice, SequentialRun, AwaitedRun }

    public enum Calls { One, Two }

    public enum Wait { Await, Blocking, NotAtOnce, None }

    public enum Completion { EveryPath, SkippedOnException, ContextInstaller }

    public enum Entries { One, Two }

    /// <summary>One combination of the axes: which parent runs the passes, how many calls a pass makes, how <c>Seed</c> waits for the
    /// task of its call, how the pass waits for the tasks of <c>Seed</c> and in how many places of the pass the calls stand.</summary>
    /// <param name="Parent">The parent running the passes: a loop, a helper called twice, a synchronous or an asynchronous retry.</param>
    /// <param name="Calls">How many calls of <c>Seed</c> a pass makes before it waits for the first.</param>
    /// <param name="Wait">How <c>Seed</c> waits for the task of the call it makes.</param>
    /// <param name="Completion">How the pass waits for the tasks of <c>Seed</c>, and whether a hosted service installs a context.</param>
    /// <param name="Entries">Whether the calls of <c>Seed</c> stand in one place of the pass or in two places on exclusive branches.</param>
    public sealed record Cell(Parent Parent, Calls Calls, Wait Wait, Completion Completion, Entries Entries)
    {
        public override string ToString() => $"{Parent}/{Calls}/{Wait}/{Completion}/{Entries}";
    }

    /// <summary>A cell whose answer is wider than its expectation — the pair stays where the requirements remove it — with the reason
    /// the analysis cannot be exact there.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Reason">Why the analysis keeps the pair.</param>
    private sealed record Wider(Cell Cell, string Reason);

    /// <summary>One decision of a pair: how its two accesses meet in time and the assumptions an ordered one rests on, sorted.</summary>
    /// <param name="Timing">How the accesses meet.</param>
    /// <param name="Assumed">The assumptions of the ordering, sorted and joined by commas; empty for none.</param>
    /// <param name="Accesses">The two accesses, by symbol and line, to name them in a failure.</param>
    private sealed record Decision(PairTiming Timing, string Assumed, string Accesses);

    /// <summary>What a program says about the continuation of the call's delegate and another write: whether a pair is left, and the
    /// decisions of the pairs left or, when none is, of every combination of the two writes' accesses.</summary>
    /// <param name="Pair">Whether the engine reports a pair of the two writes.</param>
    /// <param name="Decisions">The decisions of the pairs reported, or of all combinations when no pair is reported.</param>
    private sealed record Outcome(bool Pair, IReadOnlyList<Decision> Decisions);

    /// <summary>What a program of a cell says: the outcome of each side and whether the wait in <c>Seed</c> is an unproven join.</summary>
    /// <param name="NextSync">The continuation of the call's delegate and the synchronous part of the next run.</param>
    /// <param name="NextContinuation">The continuation of the call's delegate and another continuation.</param>
    /// <param name="AfterWait">The continuation of the call's delegate and the code after the wait.</param>
    /// <param name="SeedJoinUnproven">Whether the unproven joins hold the body of <c>Seed</c>.</param>
    private sealed record Observed(Outcome NextSync, Outcome NextContinuation, Outcome AfterWait, bool SeedJoinUnproven);

    /// <summary>The cells wider than their expectation, each with its reason; a cell that names no cell whose pair the requirements
    /// remove fails <see cref="Every_wider_cell_names_a_cell_that_expects_no_pair"/>.</summary>
    private static readonly Wider[] WiderCells = [];

    private static readonly ConcurrentDictionary<string, Lazy<Observed>> Runs = new(StringComparer.Ordinal);

    private const string SEED_CALL = "_seed.ExecuteAsync(async () => { First(); await Task.Yield(); Second(); })";

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

    public static IEnumerable<Cell> AllCells() =>
        from parent in Enum.GetValues<Parent>()
        from calls in Enum.GetValues<Calls>()
        from wait in Enum.GetValues<Wait>()
        from completion in Enum.GetValues<Completion>()
        from entries in Enum.GetValues<Entries>()
        select new Cell(parent, calls, wait, completion, entries);

    public static TheoryData<Parent, Calls, Wait, Completion, Entries> Cells()
    {
        var cells = new TheoryData<Parent, Calls, Wait, Completion, Entries>();
        foreach (var cell in AllCells())
            cells.Add(cell.Parent, cell.Calls, cell.Wait, cell.Completion, cell.Entries);
        return cells;
    }

    [Fact]
    public void There_are_192_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(192, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Parent>(), parent => Assert.Contains(cells, cell => cell.Parent == parent));
        Assert.All(Enum.GetValues<Calls>(), calls => Assert.Contains(cells, cell => cell.Calls == calls));
        Assert.All(Enum.GetValues<Wait>(), wait => Assert.Contains(cells, cell => cell.Wait == wait));
        Assert.All(Enum.GetValues<Completion>(), completion => Assert.Contains(cells, cell => cell.Completion == completion));
        Assert.All(Enum.GetValues<Entries>(), entries => Assert.Contains(cells, cell => cell.Entries == entries));
        Assert.Contains(cells, cell => !Expected(cell));
        Assert.Contains(cells, Expected);
        Assert.Contains(cells, cell => Expected(cell) && AssumedKeys(cell).Length == 0);
        Assert.Contains(cells, cell => Expected(cell) && AssumedKeys(cell).Length != 0);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        var cells = AllCells().Select(cell => cell.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.All(WiderCells, wider =>
        {
            Assert.Contains(wider.Cell.ToString(), cells);
            Assert.True(Expected(wider.Cell), $"{wider.Cell} expects the pair to stay, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Reason));
        });
    }

    /// <summary>No cell is narrower or wider than its expectation: where the requirements remove the pair, none of the three sides is
    /// reported, each is decided <c>Ordered</c> under exactly the assumptions of the cell, and the wait of <c>Seed</c> stays an
    /// unproven join; where they keep it, no side the pair stays on is decided <c>Ordered</c>.</summary>
    /// <param name="parent">The parent running the passes.</param>
    /// <param name="calls">How many calls of <c>Seed</c> a pass makes.</param>
    /// <param name="wait">How <c>Seed</c> waits for the task of its call.</param>
    /// <param name="completion">How the pass waits for the tasks of <c>Seed</c>.</param>
    /// <param name="entries">In how many places of the pass the calls stand.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact(Parent parent, Calls calls, Wait wait, Completion completion, Entries entries)
    {
        var cell = new Cell(parent, calls, wait, completion, entries);
        var observed = Runs.GetOrAdd(Source(cell), source => new Lazy<Observed>(() => Observe(source))).Value;
        var sides = new[]
        {
            ("next sync part", observed.NextSync),
            ("next continuation", observed.NextContinuation),
            ("code after the wait", observed.AfterWait)
        };
        var removed = Expected(cell);
        var listed = WiderCells.Any(wider => wider.Cell == cell);

        foreach (var (name, side) in sides)
        {
            Assert.NotEmpty(side.Decisions);
            if (!removed)
            {
                Assert.True(side.Pair, $"{cell}, {name}: the pair the requirements keep is gone.");
                Assert.All(side.Decisions, decision => Assert.True(decision.Timing != PairTiming.Ordered,
                                                                   $"{cell}, {name}: a pair kept is decided {decision.Timing}: {decision.Accesses}."));
            }
            else if (listed)
            {
                Assert.True(side.Pair, $"{cell}, {name}: listed as wider, but it is exact now; take it off the list.");
            }
            else
            {
                Assert.False(side.Pair, $"{cell}, {name}: the pair the requirements remove is still there: " +
                                        string.Join("; ", side.Decisions.Select(decision => $"{decision.Accesses} {decision.Timing}")));
                var assumed = string.Join(",", AssumedKeys(cell));
                Assert.All(side.Decisions, decision => Assert.True(decision.Timing == PairTiming.Ordered && decision.Assumed == assumed,
                                                                   $"{cell}, {name}: {decision.Accesses} decided {decision.Timing} under " +
                                                                   $"[{decision.Assumed}], expected Ordered under [{assumed}]."));
            }
        }

        // R3: the wait of the task of a call admitted only per pass is no proven join of the end of all the call's runs.
        if (cell.Wait is Wait.Await or Wait.Blocking)
            Assert.True(observed.SeedJoinUnproven, $"{cell}: the wait of the call's task in Seed is proven as a join.");
    }

    /// <summary>Whether the requirements remove the pair of the continuation of the call's delegate with each of the three sides.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell)
    {
        // R2: the wait stands right after the call and nothing that may throw stands between them.
        var atOnce = cell.Wait is Wait.Await or Wait.Blocking;
        // R1: a body that waits blocking has no suspension, so the work of the call is complete before Seed returns on both of its exits —
        // the exceptional one by ADR 0030 — and neither a second call of Seed nor a pass that skips the wait for Seed's task leaves
        // work unfinished.
        var doneBeforeReturn = cell.Wait == Wait.Blocking;
        // R1: a body that awaits hands its task to the caller on its suspension, so the work is complete only where the caller waits
        // for that task — before the second call of Seed and on every path, an exception included.
        var passWaits = cell.Calls == Calls.One && cell.Completion != Completion.SkippedOnException;
        // R1: the next pass or call arrives through the exceptional exit of a blocking wait, which a context installed in the program
        // takes away (ADR 0030). Calls on exclusive branches count as one, so the entries change nothing.
        var readsExceptionalExit = !(cell.Completion == Completion.ContextInstaller && Blocking(cell));
        return atOnce && (doneBeforeReturn || passWaits) && readsExceptionalExit;
    }

    /// <summary>Whether the proof of a cell reads the exceptional exit of a blocking wait as the end of the work: the next run comes
    /// through the <c>Wait</c> of a synchronous retry's attempt, or through the <c>Wait</c> in <c>Seed</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Blocking(Cell cell) => cell.Parent == Parent.SequentialRun || cell.Wait == Wait.Blocking;

    /// <summary>The assumptions a removed pair of a cell rests on, sorted (R4): the exceptional exits read as completion, keyed for a
    /// worker off the thread pool by <c>observers</c> and <c>thread-context</c>; none when the proof reads no such exit.</summary>
    /// <param name="cell">The cell.</param>
    private static string[] AssumedKeys(Cell cell) =>
        Blocking(cell) ? [OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT] : [];

    /// <summary>Runs the program of a cell and reads the three sides of its pair and the unproven join.</summary>
    /// <param name="source">The program.</param>
    private static Observed Observe(string source)
    {
        var options = new FixtureOptions { MetadataReferences = LibraryPackages.BesideStubs };
        var run = AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", source)), "scope:Fixture");
        var unproven = run.Execution.Analysis.UnprovenJoins.Any(site => site.BodyId.EndsWith("Worker.Seed", StringComparison.Ordinal));

        return new Observed(Side(run, "First"), Side(run, "Second"), Side(run, "After"), unproven);
    }

    /// <summary>The outcome of the continuation of the call's delegate (<c>Second</c>) and another write.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="other">The method of the other write.</param>
    private static Outcome Side(EngineRun run, string other)
    {
        var pair = BlockingWaitAssumptionMatrixTests.Pairs(run, "Second", other);
        var accesses = pair
            ? run.PairsOn("Value")
                 .Where(found => Is(found.First, "Second") && Is(found.Second, other) || Is(found.First, other) && Is(found.Second, "Second"))
                 .Select(found => (First: found.First, Second: found.Second))
                 .ToArray()
            : (from first in Writes(run, "Second")
               from second in Writes(run, other)
               select (First: first, Second: second)).ToArray();
        var decisions = accesses.Select(accessed =>
        {
            var timing = run.Execution.Analysis.Timing(accessed.First, accessed.Second, out var assumed);
            var keys = string.Join(",", assumed.Order(StringComparer.Ordinal));
            return new Decision(timing, keys, $"{Describe(accessed.First)} x {Describe(accessed.Second)}");
        }).ToArray();

        return new Outcome(pair, decisions);
    }

    private static Access[] Writes(EngineRun run, string method) =>
        run.Of("Value").Where(access => access.Operation == AccessOperation.Write && Is(access, method)).ToArray();

    private static string Describe(Access access) => $"{access.Symbol}@{access.Source.StartLine}";

    private static bool Is(Access access, string method) => access.Symbol.EndsWith($".{method}()", StringComparison.Ordinal);

    /// <summary>The program of a cell: a once-launched worker whose passes make the calls of <c>Seed</c> and wait for them as the cell
    /// says, over the real Polly 7, with a hosted service installing a context when the cell asks.</summary>
    /// <param name="cell">The cell.</param>
    private static string Source(Cell cell)
    {
        var seed = cell.Wait switch
        {
            Wait.Await => $"await {SEED_CALL};",
            Wait.Blocking => $"{SEED_CALL}.Wait();",
            Wait.NotAtOnce => $"var call = {SEED_CALL}; MayThrow(); await call;",
            _ => $"var detached = {SEED_CALL};"
        };
        var pass = Pass(cell);
        var places = cell.Entries == Entries.One ? pass : $"if (Flag) {{ {pass} }} else {{ {pass} }}";
        var guarded = $"try {{ {places} }} catch (Exception) {{ }}";
        var (body, helper, policy) = cell.Parent switch
        {
            Parent.Loop => ($"for (var pass = 0; pass < 2; pass++) {{ {guarded} }}", "", ""),
            Parent.HelperTwice => ("await Guarded(); await Guarded();", $"private async Task Guarded() {{ {guarded} }}", ""),
            Parent.SequentialRun => ($"_retry.Execute(() => {{ {places} }});", "",
                                     "private readonly RetryPolicy _retry = Policy.Handle<InvalidOperationException>().Retry(2);"),
            _ => ($"await _outer.ExecuteAsync(async () => {{ {places} }});", "",
                  "private readonly AsyncRetryPolicy _outer = Policy.Handle<InvalidOperationException>().RetryAsync(2);")
        };
        var members = $$"""
            private static bool Flag;
            private static void MayThrow() { if (Flag) throw new InvalidOperationException(); }
            private readonly AsyncRetryPolicy _seed = Policy.Handle<InvalidOperationException>().RetryAsync(2);
            {{policy}}
            private async Task Seed()
            {
                {{seed}}
                After();
            }
            {{helper}}
            """;
        var source = Usings + "using Polly;\nusing Polly.Retry;\n" + BlockingWaitAssumptionMatrixTests.Worker(body, members);

        return cell.Completion == Completion.ContextInstaller
            ? source.Replace("services.AddHostedService<Worker>();",
                             "services.AddHostedService<Worker>();\nservices.AddHostedService<Installer>();", StringComparison.Ordinal) + INSTALLER
            : source;
    }

    /// <summary>The calls of <c>Seed</c> in one place of a pass and the waits for their tasks: <c>await</c> in a parent that can
    /// suspend, <c>Wait()</c> in the delegate of a synchronous retry; a call that may throw stands before the waits when the cell
    /// skips them on an exception.</summary>
    /// <param name="cell">The cell.</param>
    private static string Pass(Cell cell)
    {
        string Join(string task) => cell.Parent == Parent.SequentialRun ? $"{task}.Wait();" : $"await {task};";
        var two = cell.Calls == Calls.Two;
        var calls = two ? "var s = Seed(); var u = Seed();" : "var s = Seed();";
        var gap = cell.Completion == Completion.SkippedOnException ? " MayThrow();" : "";
        var waits = two ? $"{Join("s")} {Join("u")}" : Join("s");

        return $"{calls}{gap} {waits}";
    }
}
