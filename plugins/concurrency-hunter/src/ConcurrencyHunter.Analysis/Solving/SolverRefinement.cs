using ConcurrencyHunter.Accesses;

namespace ConcurrencyHunter.Analysis;

/// <summary>What the solver left of a scope's candidates, with what it did on the way.</summary>
public sealed record SolverRefinementResult(PairAnalysis Pairs, IReadOnlyDictionary<string, int> Counters,
                                            IReadOnlyList<string> Traces);

/// <summary>
/// The last filter of the pipeline (TD-091): the candidates that survived indexing, overlap, operation and protection, asked one
/// by one whether their two paths and two cells can hold at once. Candidates are asked in the order of the score they are
/// expected to get, so the budget is spent where an answer changes a verdict, and that order is the same on every run.
/// An <c>Unsat</c> takes the candidate away with the reason recorded; anything else leaves it standing (TD-093).
/// </summary>
public static class SolverRefinement
{
    public const string SKIP_UNSATISFIABLE_QUERY = "unsatisfiable-query";

    public static string UnknownUncertainty(string reason) =>
        $"The solver did not decide whether the two paths can hold at once: {reason}";

    public static SolverRefinementResult Refine(PairAnalysis pairs, IConstraintSolver solver)
    {
        WorkCancellation.Check();
        return Refine(pairs, solver, SolverPolicy.Budget, SolverPolicy.QueryLimit);
    }

    public static SolverRefinementResult Refine(PairAnalysis pairs, IConstraintSolver solver, ulong budget, SolverLimit queryLimit)
    {
        WorkCancellation.Check();
        return Refine(pairs, solver, new SolverBudget(budget), queryLimit);
    }

    /// <summary>The same, spending a budget the caller holds: a run is many scopes and the budget is the run's, so the one
    /// budget is carried from scope to scope (TD-094). Every query asked is given and charged its whole limit (ADR 0021).</summary>
    /// <param name="pairs">The scope's candidates the cheap filters left.</param>
    /// <param name="solver">The solver behind the last filter.</param>
    /// <param name="budget">What is left of the run's budget.</param>
    /// <param name="queryLimit">The limit each query runs under.</param>
    public static SolverRefinementResult Refine(PairAnalysis pairs, IConstraintSolver solver, SolverBudget budget, SolverLimit queryLimit)
    {
        var counters = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            [solver.IsAvailable ? SolverCounters.AVAILABLE : SolverCounters.UNAVAILABLE] = 1
        };
        var traces = new List<string>();
        var kept = new AccessPair?[pairs.Pairs.Count];
        foreach (var index in Ordered(pairs.Pairs))
        {
            WorkCancellation.Check();
            var pair = pairs.Pairs[index];
            var query = Query(pair);
            if (query.IsEmpty)
            {
                kept[index] = pair;
                continue;
            }

            if (!budget.Holds(queryLimit))
            {
                counters[SolverCounters.BUDGET_EXHAUSTED] = counters.GetValueOrDefault(SolverCounters.BUDGET_EXHAUSTED) + 1;
                kept[index] = Undecided(pair, "the run's solver budget was spent");
                continue;
            }

            // Charged the whole limit whatever work it used, so the budget runs out at the same candidate on every run, and
            // never cut to a remainder, so a query's answer never depends on the ones asked before it (ADR 0021).
            var outcome = solver.Decide(query, queryLimit);
            budget.Charge(queryLimit);
            counters[SolverCounters.QUERIES] = counters.GetValueOrDefault(SolverCounters.QUERIES) + 1;
            switch (outcome.Answer)
            {
                case SolverAnswer.Unsat:
                    counters[SolverCounters.UNSAT] = counters.GetValueOrDefault(SolverCounters.UNSAT) + 1;
                    traces.Add($"{Site(pair)}: unsatisfiable; {outcome.Trace}");
                    break;
                case SolverAnswer.Sat:
                    counters[SolverCounters.SAT] = counters.GetValueOrDefault(SolverCounters.SAT) + 1;
                    traces.Add($"{Site(pair)}: satisfiable; {outcome.Trace}");
                    kept[index] = pair with { Feasibility = SolverAnswer.Sat };
                    break;
                default:
                    counters[SolverCounters.UNKNOWN] = counters.GetValueOrDefault(SolverCounters.UNKNOWN) + 1;
                    // The one answer the clock decided: counted apart, so a run that depended on the machine's load says so.
                    if (outcome.Stop == SolverStop.Safeguard)
                        counters[SolverCounters.SAFEGUARD_TIMEOUT] = counters.GetValueOrDefault(SolverCounters.SAFEGUARD_TIMEOUT) + 1;
                    kept[index] = Undecided(pair, outcome.Trace);
                    break;
            }
        }

        // The pairs keep the order they were found in: only the solver's verdict changes, never where a candidate stands.
        var remaining = kept.OfType<AccessPair>().ToArray();
        var skips = new SortedDictionary<string, int>(pairs.Skips.ToDictionary(), StringComparer.Ordinal);
        if (counters.GetValueOrDefault(SolverCounters.UNSAT) is var unsat and > 0)
            skips[SKIP_UNSATISFIABLE_QUERY] = skips.GetValueOrDefault(SKIP_UNSATISFIABLE_QUERY) + unsat;

        return new SolverRefinementResult(pairs with { Pairs = remaining, Skips = skips }, counters, traces);
    }

    /// <summary>A candidate that stands because nothing decided it, with the reason on the finding: an answer that decides
    /// nothing is not an answer that the two cannot meet (TD-093).</summary>
    private static AccessPair Undecided(AccessPair pair, string reason) =>
        pair with
        {
            Feasibility = SolverAnswer.Unknown,
            Uncertainties = [.. pair.Uncertainties, UnknownUncertainty(reason)]
        };

    /// <summary>
    /// The one question the two sides ask together. A pair is two runs that may be under way at once, and a value neither side
    /// proved canonical is each run's own unknown: naming both `i` would let the solver answer a run against itself, and decide
    /// that a write of <c>slots[i]</c> can never meet a write of <c>slots[i + 1]</c> although the two runs were handed different
    /// indices. So every subject and every term variable that is not proven canonical is named apart per side, and only the
    /// canonical ones stay shared (TD-092).
    /// </summary>
    private static SolverQuery Query(AccessPair pair) =>
        new([.. OfSide(pair.First.Conditions, "first"), .. OfSide(pair.Second.Conditions, "second")],
            OfSide(pair.First.SelectorTerm, "first"), OfSide(pair.Second.SelectorTerm, "second"));

    private static IEnumerable<PathPredicate> OfSide(IReadOnlyList<PathPredicate> conditions, string side)
    {
        WorkCancellation.Check();
        return conditions.Select(condition => condition with
            {
                Subject = condition.IsCanonical || condition.Subject.Length == 0 ? condition.Subject : $"{side}|{condition.Subject}",
                SubjectTerm = OfSide(condition.SubjectTerm, side)
            });
    }

    private static ValueTerm? OfSide(ValueTerm? term, string side)
    {
        WorkCancellation.Check();
        return term switch
        {
            VariableTerm variable when !IsShared(variable.Identity) => variable with { Identity = $"{side}|{variable.Identity}" },
            SumTerm sum => new SumTerm(OfSide(sum.Left, side)!, OfSide(sum.Right, side)!),
            ConvertTerm convert => convert with { Operand = OfSide(convert.Operand, side)! },
            _ => term
        };
    }

    /// <summary>Whether a name means the same value on both sides: a proven canonical one, and the empty subject of a predicate
    /// that constrains nothing at all.</summary>
    private static bool IsShared(string identity) =>
        identity.Length == 0 || identity.StartsWith(PathPredicate.CANONICAL, StringComparison.Ordinal);

    /// <summary>The candidates in the order the budget is spent on them: the score each is expected to get, highest first, and
    /// where that is equal, the resource and the two sites, so that two runs ask the same questions in the same order.</summary>
    internal static IReadOnlyList<int> Ordered(IReadOnlyList<AccessPair> pairs) =>
        Enumerable.Range(0, pairs.Count)
                  .OrderByDescending(index => ExpectedScore(pairs[index]))
                  .ThenBy(index => pairs[index].Resource.Identity, StringComparer.Ordinal)
                  .ThenBy(index => Site(pairs[index]), StringComparer.Ordinal)
                  .ThenBy(index => index)
                  .ToArray();

    /// <summary>What a candidate is expected to score (TD-103): the components a pair carries before the solver runs, which are
    /// the finding's own, so that the budget is spent in the order the findings are ranked in.</summary>
    /// <param name="pair">The pair whose expected finding score is computed.</param>
    private static int ExpectedScore(AccessPair pair)
    {
        WorkCancellation.Check();
        return ConflictFindings.ExpectedScore(pair);
    }

    private static string Site(AccessPair pair) =>
        $"{pair.First.BodyId}#{pair.First.OperationId} against {pair.Second.BodyId}#{pair.Second.OperationId}";
}
