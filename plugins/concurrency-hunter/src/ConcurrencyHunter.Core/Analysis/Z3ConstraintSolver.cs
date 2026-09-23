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
/// </summary>
public sealed class Z3ConstraintSolver : IConstraintSolver
{
    private readonly Context _context;

    private Z3ConstraintSolver(Context context) => _context = context;

    public bool IsAvailable => true;

    /// <summary>The solver, or one that answers <c>Unknown</c> when the native library is not there.</summary>
    public static IConstraintSolver Create()
    {
        try
        {
            return new Z3ConstraintSolver(new Context());
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or TypeInitializationException
                                                or EntryPointNotFoundException)
        {
            return new UnavailableSolver($"{UnavailableSolver.NOT_LOADED} {error.Message}");
        }
    }

    public SolverOutcome Decide(SolverQuery query, TimeSpan limit)
    {
        try
        {
            var solver = _context.MkSolver();
            var parameters = _context.MkParams();
            parameters.Add("timeout", (uint)Math.Max(1, limit.TotalMilliseconds));
            solver.Parameters = parameters;

            var variables = new Dictionary<string, BitVecExpr>(StringComparer.Ordinal);
            foreach (var assertion in Constraints(query, variables))
                solver.Assert(assertion);

            return solver.Check() switch
            {
                Status.UNSATISFIABLE => new SolverOutcome(SolverAnswer.Unsat, Describe(query)),
                Status.SATISFIABLE => new SolverOutcome(SolverAnswer.Sat, Model(solver, variables)),
                _ => SolverOutcome.Unknown($"the solver answered unknown for {Describe(query)}")
            };
        }
        catch (Z3Exception error)
        {
            return SolverOutcome.Unknown($"the solver failed: {error.Message}");
        }
    }

    /// <summary>What the query asserts: the predicates of both paths, and that the two cells are one cell.</summary>
    private IEnumerable<BoolExpr> Constraints(SolverQuery query, Dictionary<string, BitVecExpr> variables)
    {
        foreach (var predicate in query.Conditions.Where(condition => condition.IsSupported))
        {
            if (Predicate(predicate, variables) is { } assertion)
                yield return assertion;
        }

        if (query.FirstSelector is { } first && query.SecondSelector is { } second)
        {
            var (left, right) = Aligned((Term(first, variables), first.Signed), (Term(second, variables), second.Signed));
            yield return _context.MkEq(left, right);
        }
    }

    /// <summary>A predicate over a subject the query names, in the width and signedness of that subject's own type: a bound on a
    /// <c>long</c> decided in 32 bits, or an unsigned one decided as signed, is a different guard, and one that can empty a range
    /// the program does satisfy (TD-094). A relation the bit-vector theory does not carry — a null test, a type test, a value that
    /// is not a number — constrains nothing here, and the caller's cheap comparisons already decided what those can decide.</summary>
    private BoolExpr? Predicate(PathPredicate predicate, Dictionary<string, BitVecExpr> variables)
    {
        if (predicate.Value is not { } text || Number(text) is not { } value)
            return null;

        var subject = predicate.SubjectTerm is { } term
            ? Converted(Term(term, variables), predicate.Width, term.Signed)
            : Variable(predicate.Subject, predicate.Width, variables);
        var constant = _context.MkBV(value, (uint)predicate.Width);
        return predicate.Relation switch
        {
            PathRelation.Equal => _context.MkEq(subject, constant),
            PathRelation.NotEqual => _context.MkNot(_context.MkEq(subject, constant)),
            PathRelation.Less => predicate.Signed ? _context.MkBVSLT(subject, constant) : _context.MkBVULT(subject, constant),
            PathRelation.LessOrEqual => predicate.Signed ? _context.MkBVSLE(subject, constant) : _context.MkBVULE(subject, constant),
            PathRelation.Greater => predicate.Signed ? _context.MkBVSGT(subject, constant) : _context.MkBVUGT(subject, constant),
            PathRelation.GreaterOrEqual => predicate.Signed ? _context.MkBVSGE(subject, constant) : _context.MkBVUGE(subject, constant),
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
    private BitVecExpr Term(ValueTerm term, Dictionary<string, BitVecExpr> variables) => term switch
    {
        ConstantTerm constant => _context.MkBV(constant.Value, (uint)constant.Width),
        VariableTerm variable => Variable(variable.Identity, variable.Width, variables),
        SumTerm sum => _context.MkBVAdd(Term(sum.Left, variables), Widened(Term(sum.Right, variables), sum.Left.Width, sum.Right.Signed)),
        // A conversion widens the bits it is given, so it extends by the sign of what it converts and never by its own.
        ConvertTerm convert => Converted(Term(convert.Operand, variables), convert.Width, convert.Operand.Signed),
        _ => Variable($"opaque:{term.GetHashCode()}", term.Width, variables)
    };

    private BitVecExpr Converted(BitVecExpr operand, int width, bool signed)
    {
        var from = (int)operand.SortSize;
        if (from == width)
            return operand;
        return from > width
            ? _context.MkExtract((uint)width - 1, 0, operand)
            : signed
                ? _context.MkSignExt((uint)(width - from), operand)
                : _context.MkZeroExt((uint)(width - from), operand);
    }

    /// <summary>Two cells are compared in the wider of the two widths, with the narrower one extended into it by its own sign:
    /// widening an unsigned value as if it were signed turns 200 into -56 and makes two cells that are one look like two.</summary>
    private (BitVecExpr Left, BitVecExpr Right) Aligned((BitVecExpr Value, bool Signed) left, (BitVecExpr Value, bool Signed) right)
    {
        var width = (int)Math.Max(left.Value.SortSize, right.Value.SortSize);
        return (Converted(left.Value, width, left.Signed), Converted(right.Value, width, right.Signed));
    }

    private BitVecExpr Widened(BitVecExpr operand, int width, bool signed) => Converted(operand, width, signed);

    private BitVecExpr Variable(string identity, int width, Dictionary<string, BitVecExpr> variables)
    {
        var key = $"{identity}:{width}";
        if (!variables.TryGetValue(key, out var variable))
            variables.Add(key, variable = (BitVecExpr)_context.MkConst(_context.MkSymbol(key), _context.MkBitVecSort((uint)width)));
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

    public void Dispose() => _context.Dispose();
}
