using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Solving;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.SolverTests;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// A solver query is limited by Z3's resource count, not by time (ADR 0021, TD-094): the same query gets the same answer
/// whatever the machine's load, every query is charged its whole limit so the budget is a number of queries, and the
/// wall-clock safeguard is the one stop the clock decides, counted apart.
/// </summary>
public sealed class SolverResourceLimitTests
{
    private static readonly TimeSpan GENEROUS_SAFEGUARD = TimeSpan.FromSeconds(30);

    /// <summary>The edge is the least work limit on which the query is decided: one unit less leaves it undecided. Both sides
    /// of the edge answer as they do on an idle machine with every core busy.</summary>
    [Fact]
    public void A_query_at_the_edge_of_its_limit_gets_the_same_answer_with_every_core_loaded_as_idle()
    {
        var query = SubsetSum(items: 6, bits: 8, seed: 3);
        var edge = Edge(query);

        var idle = (Decide(query, Limit(edge)), Decide(query, Limit(edge - 1)));
        (SolverOutcome, SolverOutcome) loaded;
        using (new BusyCores())
            loaded = (Decide(query, Limit(edge)), Decide(query, Limit(edge - 1)));

        Assert.NotEqual(SolverAnswer.Unknown, idle.Item1.Answer);
        Assert.Equal(SolverAnswer.Unknown, idle.Item2.Answer);
        Assert.Equal(SolverStop.ResourceLimit, idle.Item2.Stop);
        Assert.Equal((idle.Item1.Answer, idle.Item1.Stop), (loaded.Item1.Answer, loaded.Item1.Stop));
        Assert.Equal((idle.Item2.Answer, idle.Item2.Stop), (loaded.Item2.Answer, loaded.Item2.Stop));
    }

    /// <summary>A query that needs more work than its limit answers <c>Unknown</c> for the limit, having used the whole of it,
    /// and the safeguard did not stop it, so it is no safeguard stop.</summary>
    [Fact]
    public void A_query_over_its_limit_answers_unknown_for_the_limit_and_is_no_safeguard_stop()
    {
        var hard = HardQuery();

        var outcome = Decide(hard, SolverPolicy.QueryLimit);
        var refined = RefineAs(hard, SolverPolicy.QueryLimit);

        Assert.Equal(SolverAnswer.Unknown, outcome.Answer);
        Assert.Equal(SolverStop.ResourceLimit, outcome.Stop);
        Assert.True(outcome.Work >= SolverPolicy.QUERY_WORK, $"The query used {outcome.Work} of {SolverPolicy.QUERY_WORK} units.");
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.UNKNOWN));
        Assert.DoesNotContain(SolverCounters.SAFEGUARD_TIMEOUT, refined.Counters.Keys);
    }

    /// <summary>A query the safeguard stops before its work limit answers <c>Unknown</c> for the safeguard, and coverage counts
    /// it under <c>solver-safeguard-timeout</c> besides <c>solver-unknown</c>.</summary>
    [Fact]
    public void A_query_the_safeguard_stops_is_counted_under_the_safeguard_counter()
    {
        var hard = HardQuery();
        var limit = new SolverLimit(uint.MaxValue, TimeSpan.FromMilliseconds(50));

        var outcome = Decide(hard, limit);
        var refined = RefineAs(hard, limit);

        Assert.Equal(SolverAnswer.Unknown, outcome.Answer);
        Assert.Equal(SolverStop.Safeguard, outcome.Stop);
        Assert.Single(refined.Pairs.Pairs);
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.UNKNOWN));
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.SAFEGUARD_TIMEOUT));
    }

    /// <summary>Every query is charged its whole limit, so a budget of three limits asks the same three candidates and leaves the
    /// same two standing, whatever work each query used and whether the safeguard stopped it.</summary>
    /// <param name="stop">How each query ends: answered, stopped at its resource limit, or stopped by the wall-clock safeguard.</param>
    [Theory]
    [InlineData(SolverStop.None)]
    [InlineData(SolverStop.ResourceLimit)]
    [InlineData(SolverStop.Safeguard)]
    public void Every_query_spends_its_whole_limit_so_the_budget_runs_out_at_the_same_candidate(SolverStop stop)
    {
        var solver = new RecordingSolver(limit => stop switch
        {
            SolverStop.None => new SolverOutcome(SolverAnswer.Sat, "model is empty") { Work = 1 },
            SolverStop.ResourceLimit => SolverOutcome.Unknown("stub", stop, limit.Work),
            _ => SolverOutcome.Unknown("stub", stop, limit.Work / 3)
        });

        var refined = SolverRefinement.Refine(FiveCandidates(), solver, 3UL * SolverPolicy.QUERY_WORK, SolverPolicy.QueryLimit);

        Assert.Equal(["a", "b", "c"], solver.Asked);
        Assert.Equal(2, refined.Counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED));
        Assert.Equal(3, refined.Counters.GetValueOrDefault(SolverCounters.QUERIES));
    }

    /// <summary>No query is given less than the whole limit: a budget of two and a half limits asks two queries with the whole
    /// limit each and leaves the rest standing, never cutting a third to the half left.</summary>
    [Fact]
    public void No_query_is_given_less_than_the_whole_limit()
    {
        var solver = new RecordingSolver(_ => SolverOutcome.Unknown("stub"));

        var refined = SolverRefinement.Refine(FiveCandidates(), solver, 5UL * SolverPolicy.QUERY_WORK / 2, SolverPolicy.QueryLimit);

        Assert.Equal([SolverPolicy.QueryLimit, SolverPolicy.QueryLimit], solver.Limits);
        Assert.Equal(3, refined.Counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED));
    }

    /// <summary>A query's work depends on that query alone: asked by one solver right after a query that used its whole limit,
    /// it reports the work a fresh solver reports for it, three times on an idle machine and three times with every core busy.
    /// In a context the two queries shared, the work moved with what the first query left there and with when the runtime freed
    /// it, and so with the load (ADR 0021).</summary>
    [Fact]
    public void A_querys_work_is_the_same_after_a_query_stopped_at_its_limit()
    {
        var query = SubsetSum(items: 6, bits: 8, seed: 3);

        var first = Decide(query, Limit(uint.MaxValue));
        var second = Decide(query, Limit(uint.MaxValue));
        var idle = Enumerable.Range(0, 3).Select(_ => AfterAWholeLimit(query)).ToList();
        List<(SolverOutcome Before, SolverOutcome After)> loaded;
        using (new BusyCores())
            loaded = Enumerable.Range(0, 3).Select(_ => AfterAWholeLimit(query)).ToList();

        Assert.True(first.Work > 0, "The query reported no work.");
        Assert.Equal(first.Work, second.Work);
        foreach (var (before, after) in idle.Concat(loaded))
        {
            Assert.True(before.Work >= SolverPolicy.QUERY_WORK, $"The query stopped at its limit used {before.Work} units.");
            Assert.Equal(first.Work, after.Work);
        }

        static (SolverOutcome Before, SolverOutcome After) AfterAWholeLimit(SolverQuery query)
        {
            using var solver = Z3ConstraintSolver.Create();
            var before = solver.Decide(HardQuery(), Limit(SolverPolicy.QUERY_WORK));
            return (before, solver.Decide(query, Limit(uint.MaxValue)));
        }
    }

    /// <summary>Coverage shows <c>solver-safeguard-timeout</c> only when the safeguard stopped a query, as the other answer
    /// counters are shown only when they count something.</summary>
    [Fact]
    public void The_safeguard_counter_is_absent_when_nothing_tripped_it()
    {
        var answers = new Queue<SolverOutcome>([
            new SolverOutcome(SolverAnswer.Sat, "model is empty"), new SolverOutcome(SolverAnswer.Unsat, "core"),
            SolverOutcome.Unknown("stub"), SolverOutcome.Unknown("stub", SolverStop.ResourceLimit, SolverPolicy.QUERY_WORK)
        ]);
        var solver = new RecordingSolver(_ => answers.Count > 0 ? answers.Dequeue() : SolverOutcome.Unknown("stub"));

        using var z3 = Z3ConstraintSolver.Create();

        var refined = SolverRefinement.Refine(FiveCandidates(), solver);
        var real = SolverRefinement.Refine(SolverTests.Analysis(Pair(Query(Cell(0), Cell(1)))), z3);

        Assert.Equal(5, refined.Counters.GetValueOrDefault(SolverCounters.QUERIES));
        Assert.DoesNotContain(SolverCounters.SAFEGUARD_TIMEOUT, refined.Counters.Keys);
        Assert.DoesNotContain(SolverCounters.SAFEGUARD_TIMEOUT, real.Counters.Keys);
    }

    /// <summary>
    /// A subset sum built only of what a query can say — additions in 64 bits, a zero extension of each one-bit choice and
    /// equalities to constants — over random weights of <paramref name="bits"/> bits, asked to reach one more than a subset's
    /// total. Each power of two of a choice is a variable the equalities pin to twice the one before it, so the query stays linear
    /// in size; with forty forty-bit weights Z3 does not finish it in seconds, which is what calibrates the per-query limit.
    /// </summary>
    /// <param name="items">How many weights there are.</param>
    /// <param name="bits">How many bits each weight has.</param>
    /// <param name="seed">The seed of the weights and the subset.</param>
    internal static SolverQuery SubsetSum(int items, int bits, int seed)
    {
        var random = new Random(seed);
        var conditions = new List<PathPredicate>();
        var parts = new List<ValueTerm>();
        var target = 1L;
        for (var item = 0; item < items; item++)
        {
            var weight = random.NextInt64(1L << (bits - 1), 1L << bits);
            if (random.Next(2) == 0)
                target += weight;
            ValueTerm power = new ConvertTerm(new VariableTerm($"choice{item}", 1, false), 64, false);
            for (var bit = 0; bit < bits; bit++)
            {
                if (((weight >> bit) & 1) == 1)
                    parts.Add(power);
                var negated = new VariableTerm($"negated{item}_{bit}", 64);
                var doubled = new VariableTerm($"power{item}_{bit}", 64);
                conditions.Add(Zero(new SumTerm(negated, new SumTerm(power, power))));
                conditions.Add(Zero(new SumTerm(doubled, negated)));
                power = doubled;
            }
        }

        conditions.Add(Equal(Sum(parts, 0, parts.Count), target));
        return new SolverQuery(conditions, null, null);
    }

    internal static SolverQuery HardQuery() => SubsetSum(items: 40, bits: 40, seed: 40);

    private static PathPredicate Zero(ValueTerm term) => Equal(term, 0);

    private static PathPredicate Equal(ValueTerm term, long value) =>
        new($"{PathPredicate.CANONICAL}subset", PathRelation.Equal, $"{value}", "subset sum") { SubjectTerm = term, Width = 64 };

    /// <summary>A balanced sum, so that no term nests deeper than the logarithm of the parts.</summary>
    /// <param name="parts">The terms summed.</param>
    /// <param name="from">The first part of this sum.</param>
    /// <param name="to">The part past the last one of this sum.</param>
    private static ValueTerm Sum(List<ValueTerm> parts, int from, int to) =>
        to - from == 1 ? parts[from] : new SumTerm(Sum(parts, from, (from + to) / 2), Sum(parts, (from + to) / 2, to));

    private static SolverLimit Limit(uint work) => new(work, GENEROUS_SAFEGUARD);

    /// <summary>One query asked by a fresh solver.</summary>
    /// <param name="query">The query asked.</param>
    /// <param name="limit">The limit it runs under.</param>
    private static SolverOutcome Decide(SolverQuery query, SolverLimit limit)
    {
        using var solver = Z3ConstraintSolver.Create();
        return solver.Decide(query, limit);
    }

    /// <summary>The least work limit on which the query is decided.</summary>
    /// <param name="query">A query Z3 decides with no limit.</param>
    private static uint Edge(SolverQuery query)
    {
        var decided = Decide(query, Limit(uint.MaxValue));
        Assert.NotEqual(SolverAnswer.Unknown, decided.Answer);
        var (low, high) = (0U, (uint)Math.Min(uint.MaxValue, decided.Work * 2));
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (Decide(query, Limit(middle)).Answer == SolverAnswer.Unknown)
                low = middle;
            else
                high = middle;
        }

        return high;
    }

    /// <summary>One candidate refined by the shipped solver, asked <paramref name="query"/> in its place.</summary>
    /// <param name="query">The query the solver is asked instead of the candidate's own.</param>
    /// <param name="limit">The per-query limit, and the whole budget: one query.</param>
    private static SolverRefinementResult RefineAs(SolverQuery query, SolverLimit limit)
    {
        using var z3 = Z3ConstraintSolver.Create();
        var solver = new RecordingSolver(given => z3.Decide(query, given));
        return SolverRefinement.Refine(SolverTests.Analysis(Pair(Query(Cell(0), Cell(1)))), solver, limit.Work, limit);
    }

    private static PairAnalysis FiveCandidates() =>
        SolverTests.Analysis([.. new[] { "a", "b", "c", "d", "e" }.Select((name, index) => Pair(Query(Variable(name), Cell(index)), name))]);

    /// <summary>A solver of the test's own that records what it was asked and with what limit.</summary>
    /// <param name="answer">What the solver answers a query asked with a limit.</param>
    private sealed class RecordingSolver(Func<SolverLimit, SolverOutcome> answer) : IConstraintSolver
    {
        public List<SolverLimit> Limits { get; } = [];

        public List<string> Asked { get; } = [];

        public bool IsAvailable => true;

        public SolverOutcome Decide(SolverQuery query, SolverLimit limit)
        {
            Limits.Add(limit);
            Asked.Add(query.FirstSelector is VariableTerm variable ? variable.Identity.Split('|')[^1] : $"{query.FirstSelector}");
            return answer(limit);
        }

        public void Dispose()
        {
        }
    }
}
