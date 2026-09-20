using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using ConcurrencyHunter.Solving;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// The last filter of the pipeline and what it is allowed to do (TD-093, TD-094, ADR 0004): only an unsatisfiable answer takes a
/// candidate away, numbers are decided in the width of their own types so a conversion and its overflow are decided as the
/// language runs them, values of independent executions are independent unknowns, and a solver that cannot answer — because it
/// never started, because it ran out of time, or because the run's budget is spent — leaves every candidate where it was.
/// </summary>
public sealed class SolverTests
{
    [Fact]
    public void Two_constant_cells_cannot_be_one_cell() =>
        Assert.Equal(SolverAnswer.Unsat, Decide(Query(Cell(0), Cell(1))));

    [Fact]
    public void Ranges_that_do_not_meet_cannot_both_hold() =>
        Assert.Equal(SolverAnswer.Unsat,
                     Decide(new SolverQuery([Predicate("limit", PathRelation.Less, "5"), Predicate("limit", PathRelation.GreaterOrEqual, "5")],
                                            null, null)));

    /// <summary>A conversion keeps the low bits, so two indices that differ by a whole turn of the type name one cell: the
    /// algebra says they differ, and the algebra is not what the program runs.</summary>
    [Fact]
    public void An_index_that_wraps_on_conversion_names_the_same_cell() =>
        Assert.Equal(SolverAnswer.Sat,
                     Decide(Query(Narrowed(Variable("i")), Narrowed(new SumTerm(Variable("i"), Cell(256))))));

    [Fact]
    public void An_index_that_does_not_wrap_on_conversion_names_another_cell() =>
        Assert.Equal(SolverAnswer.Unsat,
                     Decide(Query(Narrowed(Variable("i")), Narrowed(new SumTerm(Variable("i"), Cell(1))))));

    /// <summary>A widening conversion carries the value, so the wider comparison decides the narrower one.</summary>
    [Fact]
    public void A_widening_conversion_is_decided()
    {
        var widened = new ConvertTerm(Variable("i"), 64, true);

        Assert.Equal(SolverAnswer.Unsat, Decide(Query(widened, new SumTerm(widened, new ConstantTerm(1, 64)))));
        Assert.Equal(SolverAnswer.Sat, Decide(Query(widened, new ConvertTerm(Variable("i"), 64, true))));
    }

    /// <summary>Two executions each hold their own value, so nothing says the two cells differ.</summary>
    [Fact]
    public void Indices_of_independent_executions_may_be_one_cell()
    {
        var query = Query(Variable("first|i"), Variable("second|i"));

        Assert.Equal(SolverAnswer.Sat, Decide(query));
        Assert.Single(Refined(query).Pairs.Pairs);
    }

    /// <summary>A key the comparer decides is no number, so the bit-vector theory carries nothing about it and the candidate
    /// stands (ADR 0004: no string theory).</summary>
    [Fact]
    public void A_comparer_the_analysis_cannot_read_leaves_the_candidate()
    {
        var query = new SolverQuery([Predicate("key", PathRelation.Equal, "\"a\""), Predicate("key", PathRelation.Equal, "\"b\"")], null, null);

        Assert.Equal(SolverAnswer.Sat, Decide(query));
        Assert.Single(Refined(query).Pairs.Pairs);
    }

    [Fact]
    public void An_answer_that_decides_nothing_leaves_the_candidate_with_the_reason()
    {
        var refined = Refined(Query(Cell(0), Cell(1)), new StubSolver(_ => SolverOutcome.Unknown("the theory does not carry it")));

        var pair = Assert.Single(refined.Pairs.Pairs);
        Assert.Contains(SolverRefinement.UnknownUncertainty("the theory does not carry it"), pair.Uncertainties);
        Assert.Equal(SolverAnswer.Unknown, pair.Feasibility);
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.UNKNOWN));
    }

    /// <summary>The whole point of the degradation: a solver that never started must cost precision, never a verdict.</summary>
    [Fact]
    public void A_solver_that_never_started_leaves_every_candidate()
    {
        var refined = Refined(Query(Cell(0), Cell(1)), new UnavailableSolver(UnavailableSolver.NOT_LOADED));

        Assert.Single(refined.Pairs.Pairs);
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.UNAVAILABLE));
        Assert.Equal(0, refined.Counters.GetValueOrDefault(SolverCounters.AVAILABLE));
        Assert.DoesNotContain(SolverRefinement.SKIP_UNSATISFIABLE_QUERY, refined.Pairs.Skips.Keys);
    }

    [Fact]
    public void A_query_that_runs_out_of_time_leaves_the_candidate()
    {
        var refined = Refined(Query(Cell(0), Cell(1)),
                              new StubSolver(limit =>
                              {
                                  Thread.Sleep(limit + TimeSpan.FromMilliseconds(5));
                                  return SolverOutcome.Unknown("the query ran out of time");
                              }));

        Assert.Single(refined.Pairs.Pairs);
        Assert.Equal(1, refined.Counters.GetValueOrDefault(SolverCounters.UNKNOWN));
    }

    /// <summary>The per-query limit is the server's own constant, and it is what the solver is given (TD-094).</summary>
    [Fact]
    public void Every_query_is_given_the_per_query_limit()
    {
        var solver = new StubSolver(_ => SolverOutcome.Unknown("stub"));

        SolverRefinement.Refine(Analysis(Pair(Query(Cell(0), Cell(1))), Pair(Query(Cell(2), Cell(3)))), solver);

        Assert.Equal([SolverPolicy.QueryLimit, SolverPolicy.QueryLimit], solver.Limits);
        Assert.Equal(TimeSpan.FromMilliseconds(150), SolverPolicy.QueryLimit);
    }

    /// <summary>And the run's own budget stops the asking: the candidates past it stand, counted, rather than being asked
    /// past the share of the deadline the solver is allowed.</summary>
    [Fact]
    public void The_run_stops_asking_once_its_budget_is_spent()
    {
        var solver = new StubSolver(_ =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(20));
            return SolverOutcome.Unknown("stub");
        });
        var pairs = Analysis(Pair(Query(Cell(0), Cell(1))), Pair(Query(Cell(2), Cell(3))), Pair(Query(Cell(4), Cell(5))));

        var refined = SolverRefinement.Refine(pairs, solver, TimeSpan.FromMilliseconds(10), SolverPolicy.QueryLimit);

        Assert.Single(solver.Limits);
        Assert.Equal(2, refined.Counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED));
        Assert.Equal(3, refined.Pairs.Pairs.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), SolverPolicy.Budget);
    }

    /// <summary>The share of the deadline belongs to the run and not to each of its scopes: a run of many projects asks its
    /// questions out of one budget, and a scope that starts after it is spent asks none (TD-094).</summary>
    [Fact]
    public void The_budget_is_the_whole_run_and_not_each_scope()
    {
        var solver = new StubSolver(_ =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(20));
            return SolverOutcome.Unknown("stub");
        });
        var budget = new SolverBudget(TimeSpan.FromMilliseconds(10));

        var first = SolverRefinement.Refine(Analysis(Pair(Query(Cell(0), Cell(1)))), solver, budget, SolverPolicy.QueryLimit);
        var second = SolverRefinement.Refine(Analysis(Pair(Query(Cell(2), Cell(3)))), solver, budget, SolverPolicy.QueryLimit);

        Assert.Single(solver.Limits);
        Assert.Equal(0, first.Counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED));
        Assert.Equal(1, second.Counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED));
    }

    /// <summary>A query never outlives the run: the last one asked is given what is left and no more.</summary>
    [Fact]
    public void The_last_query_is_cut_to_what_is_left_of_the_run()
    {
        var solver = new StubSolver(_ => SolverOutcome.Unknown("stub"));
        var remaining = TimeSpan.FromMilliseconds(20);

        SolverRefinement.Refine(Analysis(Pair(Query(Cell(0), Cell(1)))), solver, new SolverBudget(remaining), SolverPolicy.QueryLimit);

        Assert.Equal([remaining], solver.Limits);
        Assert.True(remaining < SolverPolicy.QueryLimit, "The fixture must leave less than one query's limit.");
    }

    /// <summary>The budget is spent on the candidates that are worth the most, and the same way on every run (TD-094).</summary>
    [Fact]
    public void The_order_candidates_are_asked_in_is_the_same_on_every_run()
    {
        var pairs = Analysis(Pair(Query(Variable("beta"), Cell(1)), "beta"), Pair(Query(Variable("alpha"), Cell(3)), "alpha", wildcard: true),
                             Pair(Query(Variable("gamma"), Cell(5)), "gamma"));

        var first = new StubSolver(_ => SolverOutcome.Unknown("stub"));
        var second = new StubSolver(_ => SolverOutcome.Unknown("stub"));
        SolverRefinement.Refine(pairs, first);
        SolverRefinement.Refine(pairs, second);

        // The wildcard resource is worth less, so it is asked last however early it stands among the candidates.
        Assert.Equal(["beta", "gamma", "alpha"], first.Resources);
        Assert.Equal(first.Resources, second.Resources);
    }

    [Fact]
    public void An_unsatisfiable_answer_takes_the_candidate_away_with_the_reason()
    {
        var refined = Refined(Query(Cell(0), Cell(1)));

        Assert.Empty(refined.Pairs.Pairs);
        Assert.Equal(1, refined.Pairs.Skips.GetValueOrDefault(SolverRefinement.SKIP_UNSATISFIABLE_QUERY));
        Assert.Contains(refined.Traces, trace => trace.Contains("unsatisfiable", StringComparison.Ordinal));
    }

    /// <summary>The solver of the shipped build, here so that a test host that cannot load it says so rather than passing the
    /// suite on a degradation nobody asked for.</summary>
    [Fact]
    public void The_solver_this_build_ships_is_there()
    {
        using var solver = Z3ConstraintSolver.Create();

        Assert.True(solver.IsAvailable, "The solver's native library did not load in the test host.");
        Assert.Equal(SolverAnswer.Sat, solver.Decide(Query(Variable("i"), Variable("j")), SolverPolicy.QueryLimit).Answer);
    }

    /// <summary>The budget is spent in the order the candidates are expected to score in, and how protected a pair is decides as
    /// much of that score as its resource does: an unprotected candidate is asked before a partial one, which an order by the
    /// wildcard alone left standing in the order they came (TD-103).</summary>
    [Fact]
    public void The_budget_is_spent_on_the_unprotected_candidate_first()
    {
        var partial = Pair(Query(Cell(0), Cell(1))) with { Protection = PairProtection.PARTIAL };
        var unprotected = Pair(Query(Cell(2), Cell(3)));

        Assert.Equal([1, 0], SolverRefinement.Ordered([partial, unprotected]));
    }

    private static SolverAnswer Decide(SolverQuery query)
    {
        using var solver = Z3ConstraintSolver.Create();
        return solver.Decide(query, SolverPolicy.QueryLimit).Answer;
    }

    private static SolverRefinementResult Refined(SolverQuery query, IConstraintSolver? solver = null)
    {
        if (solver is null)
        {
            using var z3 = Z3ConstraintSolver.Create();
            return SolverRefinement.Refine(Analysis(Pair(query)), z3);
        }

        using (solver)
            return SolverRefinement.Refine(Analysis(Pair(query)), solver);
    }

    private static SolverQuery Query(ValueTerm first, ValueTerm second) => new([], first, second);

    private static PathPredicate Predicate(string subject, PathRelation relation, string value) =>
        new($"canon|{subject}", relation, value, subject);

    private static ValueTerm Cell(long index) => new ConstantTerm(index, 32);

    private static ValueTerm Variable(string identity) => new VariableTerm(identity, 32);

    /// <summary>A cast to a byte, which is where an index that wraps stops being the number it was.</summary>
    private static ValueTerm Narrowed(ValueTerm term) => new ConvertTerm(term, 8, false);

    private static PairAnalysis Analysis(params AccessPair[] pairs) =>
        new(pairs, pairs.Length, 0, new SortedDictionary<string, int>(StringComparer.Ordinal));

    private static AccessPair Pair(SolverQuery query, string resource = "R", bool wildcard = false) =>
        new(Access(query.FirstSelector, resource, wildcard), Access(query.SecondSelector, resource, wildcard), PairProtection.UNPROTECTED)
        {
            Uncertainties = []
        };

    private static Access Access(ValueTerm? selector, string resource, bool wildcard)
    {
        var span = new SourceSpan("Case.cs", 1, 1, 1, 2);
        var root = new AccessRoot("root", "Fixture.Run()", "Fixture.Run()", "fixture", "root",
                                  new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, "process"), "scope:Fixture");
        return new Access(new AccessResource("Fixture", "scope:Fixture", resource, [resource], new MemberKey("Fixture", resource, IrFieldKind.Field),
                                             $"region:{resource}", wildcard),
                          AccessOperation.Write, root, "Fixture.Run()", span, [], [], [], [], [])
        {
            SelectorTerm = selector,
            ExecutionId = "execution",
            BodyId = "body:Fixture",
            InstanceId = "instance"
        };
    }

    /// <summary>A solver of the test's own: the seam that lets a run be measured as if the shipped one behaved otherwise.</summary>
    private sealed class StubSolver(Func<TimeSpan, SolverOutcome> answer) : IConstraintSolver
    {
        public List<TimeSpan> Limits { get; } = [];

        public List<string> Resources { get; } = [];

        public bool IsAvailable => true;

        public SolverOutcome Decide(SolverQuery query, TimeSpan limit)
        {
            Limits.Add(limit);
            Resources.Add(Name(query.FirstSelector));
            return answer(limit);
        }

        /// <summary>Which candidate this query belongs to, which the fixtures encode in the first cell's own name. The query
        /// names each side's own unknowns apart, so the name is what follows the side the query put in front of it.</summary>
        private static string Name(ValueTerm? term) =>
            term is VariableTerm variable ? variable.Identity.Split('|')[^1] : $"{term}";

        public void Dispose()
        {
        }
    }
}
