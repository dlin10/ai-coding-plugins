using System.Globalization;
using ConcurrencyHunter.Analysis;
using Microsoft.Z3;

namespace ConcurrencyHunter.Solving;

/// <summary>
/// The solver of ADR 0004: Z3 over <c>QF_BV</c> for the numbers a selector or a guard names, in the width of their own types so
/// that a conversion and its overflow are decided as the language runs them, and <c>QF_UF</c> for values it cannot read, which
/// become unknowns of their width. No strings and no array theory: a collection's structure is a resource of its own (TD-070),
/// and a key nothing proves equal is an unknown like any other.
/// A native library that does not load is not an error here: <see cref="Create"/> gives back a solver that answers every query
/// <c>Unknown</c>, which is the answer the pipeline already knows how to carry.
/// Each query is decided in a Z3 <see cref="Context"/> of its own, disposed after it with everything made from it: in a context
/// queries share, a query's work depends on what earlier queries left there and on when the runtime frees it, and so on the
/// machine's load (ADR 0021).
/// </summary>
public sealed class Z3ConstraintSolver : IConstraintSolver
{
    private Z3ConstraintSolver()
    {
    }

    public bool IsAvailable => true;

    /// <summary>The solver, or one that answers <c>Unknown</c> when the native library is not there: one context made and
    /// disposed tells whether it loads.</summary>
    public static IConstraintSolver Create()
    {
        try
        {
            using (new Context())
                return new Z3ConstraintSolver();
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or TypeInitializationException
                                                or EntryPointNotFoundException)
        {
            return new UnavailableSolver($"{UnavailableSolver.NOT_LOADED} {error.Message}");
        }
    }

    /// <summary>Decides a query within Z3's resource limit (<c>rlimit</c>), which Z3 applies relative to its count at each
    /// <c>Check</c>, and with the safeguard as Z3's own timeout (ADR 0021), in a <see cref="Context"/> made for the query and
    /// disposed after it. The work is the difference of the <c>rlimit count</c> statistic around <c>Check</c>, which counts only
    /// this query's work since nothing else ran in its context.</summary>
    /// <param name="query">The candidate's question.</param>
    /// <param name="limit">The work limit and the safeguard.</param>
    public SolverOutcome Decide(SolverQuery query, SolverLimit limit)
    {
        try
        {
            using var context = new Context();
            using var solver = context.MkSolver();
            using var parameters = context.MkParams();
            parameters.Add("rlimit", limit.Work);
            parameters.Add("timeout", (uint)Math.Max(1, limit.Safeguard.TotalMilliseconds));
            solver.Parameters = parameters;

            var variables = new Dictionary<string, BitVecExpr>(StringComparer.Ordinal);
            foreach (var assertion in Constraints(context, query, variables))
                solver.Assert(assertion);

            var before = WorkCount(solver);
            var status = solver.Check();
            var work = WorkCount(solver) - before;
            return status switch
            {
                Status.UNSATISFIABLE => new SolverOutcome(SolverAnswer.Unsat, Describe(query)) { Work = work },
                Status.SATISFIABLE => new SolverOutcome(SolverAnswer.Sat, Model(solver, variables)) { Work = work },
                _ => SolverOutcome.Unknown($"the solver answered unknown for {Describe(query)}", StopOf(solver.ReasonUnknown), work)
            };
        }
        catch (Z3Exception error)
        {
            return SolverOutcome.Unknown($"the solver failed: {error.Message}");
        }
    }

    /// <summary>The <c>rlimit count</c> statistic of the query's own context, which Z3 reports as an unsigned number up to
    /// <c>UINT_MAX</c> and as a double above it.</summary>
    /// <param name="solver">The solver whose context's count is read.</param>
    private static ulong WorkCount(Solver solver)
    {
        var entry = solver.Statistics.Entries.FirstOrDefault(entry => entry.Key == "rlimit count");
        return entry is null ? 0 : entry.IsUInt ? entry.UIntValue : (ulong)entry.DoubleValue;
    }

    /// <summary>What stopped an unknown answer, as Z3 names it: <c>timeout</c> for the safeguard; for the resource limit,
    /// <c>max. resource limit exceeded</c> where preprocessing hit it and <c>canceled</c> where the search did (Z3 4.12.2); anything
    /// else is the solver's own answer.</summary>
    /// <param name="reason">Z3's <c>ReasonUnknown</c>.</param>
    private static SolverStop StopOf(string reason) => reason switch
    {
        "timeout" => SolverStop.Safeguard,
        "canceled" or "max. resource limit exceeded" => SolverStop.ResourceLimit,
        _ => SolverStop.Solver
    };

    /// <summary>What the query asserts: the predicates of both paths, and that the two cells are one cell.</summary>
    /// <param name="context">The query's own Z3 context, which makes every expression.</param>
    /// <param name="query">The query.</param>
    /// <param name="variables">The bit vectors made so far for the query's variables, by identity and width.</param>
    private static IEnumerable<BoolExpr> Constraints(Context context, SolverQuery query, Dictionary<string, BitVecExpr> variables)
    {
        foreach (var predicate in query.Conditions.Where(condition => condition.IsSupported))
        {
            if (Predicate(context, predicate, variables) is { } assertion)
                yield return assertion;
        }

        if (query.FirstSelector is { } first && query.SecondSelector is { } second)
        {
            var (left, right) = Aligned(context, (Term(context, first, variables), first.Signed),
                                        (Term(context, second, variables), second.Signed));
            yield return context.MkEq(left, right);
        }
    }

    /// <summary>A predicate over a subject the query names, in the width and signedness of that subject's own type: a bound on a
    /// <c>long</c> decided in 32 bits, or an unsigned one decided as signed, is a different guard, and one that can empty a range
    /// the program does satisfy (TD-094). A relation the bit-vector theory does not carry — a null test, a type test, a value that
    /// is not a number — constrains nothing here, and the caller's cheap comparisons already decided what those can decide.</summary>
    /// <param name="context">The query's own Z3 context, which makes every expression.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="variables">The bit vectors made so far for the query's variables, by identity and width.</param>
    private static BoolExpr? Predicate(Context context, PathPredicate predicate, Dictionary<string, BitVecExpr> variables)
    {
        if (predicate.Value is not { } text || Number(text) is not { } value)
            return null;

        var subject = predicate.SubjectTerm is { } term
            ? Converted(context, Term(context, term, variables), predicate.Width, term.Signed)
            : Variable(context, predicate.Subject, predicate.Width, variables);
        var constant = context.MkBV(value, (uint)predicate.Width);
        return predicate.Relation switch
        {
            PathRelation.Equal => context.MkEq(subject, constant),
            PathRelation.NotEqual => context.MkNot(context.MkEq(subject, constant)),
            PathRelation.Less => predicate.Signed ? context.MkBVSLT(subject, constant) : context.MkBVULT(subject, constant),
            PathRelation.LessOrEqual => predicate.Signed ? context.MkBVSLE(subject, constant) : context.MkBVULE(subject, constant),
            PathRelation.Greater => predicate.Signed ? context.MkBVSGT(subject, constant) : context.MkBVUGT(subject, constant),
            PathRelation.GreaterOrEqual => predicate.Signed ? context.MkBVSGE(subject, constant) : context.MkBVUGE(subject, constant),
            _ => null
        };
    }

    /// <summary>The number a predicate compares against, as the bits of its own type: one that does not fit a <c>long</c> is
    /// still a <c>ulong</c> the language can write, and its bit pattern is what the comparison reads.</summary>
    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed) ? signed
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned) ? unchecked((long)unsigned)
            : null;

    /// <summary>A term as a bit vector of its own width: a sum wraps at that width, and a conversion keeps the low bits going
    /// down and extends by the source's sign going up, which is what makes <c>(byte)(i + 256)</c> the cell <c>(byte)i</c>
    /// is.</summary>
    /// <param name="context">The query's own Z3 context, which makes every expression.</param>
    /// <param name="term">The term.</param>
    /// <param name="variables">The bit vectors made so far for the query's variables, by identity and width.</param>
    private static BitVecExpr Term(Context context, ValueTerm term, Dictionary<string, BitVecExpr> variables) => term switch
    {
        ConstantTerm constant => context.MkBV(constant.Value, (uint)constant.Width),
        VariableTerm variable => Variable(context, variable.Identity, variable.Width, variables),
        SumTerm sum => context.MkBVAdd(Term(context, sum.Left, variables),
                                       Widened(context, Term(context, sum.Right, variables), sum.Left.Width, sum.Right.Signed)),
        // A conversion widens the bits it is given, so it extends by the sign of what it converts and never by its own.
        ConvertTerm convert => Converted(context, Term(context, convert.Operand, variables), convert.Width, convert.Operand.Signed),
        _ => Variable(context, $"opaque:{term.GetHashCode()}", term.Width, variables)
    };

    private static BitVecExpr Converted(Context context, BitVecExpr operand, int width, bool signed)
    {
        var from = (int)operand.SortSize;
        if (from == width)
            return operand;
        return from > width
            ? context.MkExtract((uint)width - 1, 0, operand)
            : signed
                ? context.MkSignExt((uint)(width - from), operand)
                : context.MkZeroExt((uint)(width - from), operand);
    }

    /// <summary>Two cells are compared in the wider of the two widths, with the narrower one extended into it by its own sign:
    /// widening an unsigned value as if it were signed turns 200 into -56 and makes two cells that are one look like two.</summary>
    /// <param name="context">The query's own Z3 context, which makes every expression.</param>
    /// <param name="left">The first cell's bit vector, with whether its type is signed.</param>
    /// <param name="right">The second cell's bit vector, with whether its type is signed.</param>
    private static (BitVecExpr Left, BitVecExpr Right) Aligned(Context context, (BitVecExpr Value, bool Signed) left,
                                                               (BitVecExpr Value, bool Signed) right)
    {
        var width = (int)Math.Max(left.Value.SortSize, right.Value.SortSize);
        return (Converted(context, left.Value, width, left.Signed), Converted(context, right.Value, width, right.Signed));
    }

    private static BitVecExpr Widened(Context context, BitVecExpr operand, int width, bool signed) =>
        Converted(context, operand, width, signed);

    private static BitVecExpr Variable(Context context, string identity, int width, Dictionary<string, BitVecExpr> variables)
    {
        var key = $"{identity}:{width}";
        if (!variables.TryGetValue(key, out var variable))
            variables.Add(key, variable = (BitVecExpr)context.MkConst(context.MkSymbol(key), context.MkBitVecSort((uint)width)));
        return variable;
    }

    /// <summary>The model, as small as the query is: one value per name the query asked about (TD-093).</summary>
    private static string Model(Solver solver, Dictionary<string, BitVecExpr> variables)
    {
        var model = solver.Model;
        var values = variables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                              .Select(pair => $"{pair.Key}={model.Evaluate(pair.Value, true)}");
        return values.Any() ? $"model {string.Join(", ", values)}" : "model is empty";
    }

    private static string Describe(SolverQuery query) =>
        $"{query.Conditions.Count(condition => condition.IsSupported)} predicates and " +
        $"{(query.FirstSelector is null || query.SecondSelector is null ? "no cells" : "two cells")}";

    /// <summary>Nothing to free: every query disposed its own context.</summary>
    public void Dispose()
    {
    }
}
