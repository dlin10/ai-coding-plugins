namespace ConcurrencyHunter.Analysis;

/// <summary>What a query decided (TD-093). <see cref="Unsat"/> is the only answer that takes a candidate away;
/// <see cref="Unknown"/> covers a timeout, a query the theory does not carry and a solver that never started, none of which mean
/// the candidate is safe.</summary>
public enum SolverAnswer
{
    Unsat,
    Sat,
    Unknown
}

/// <summary>
/// A value a query reasons about, in the width of its own type so that conversions and overflow are decided as the language
/// runs them rather than as algebra reads them (TD-094). A leaf is a constant, a value with an identity of its own, or a value
/// the analysis cannot read, which the solver treats as an unknown of that width.
/// </summary>
public abstract record ValueTerm
{
    public abstract int Width { get; }

    /// <summary>Whether the type of this value is signed. It is what says how the value reaches a wider width: a value extends
    /// by its own sign and an unsigned one has none to extend, so a <c>byte</c> holding 200 widens to 200 and never to -56
    /// (TD-094).</summary>
    public abstract bool Signed { get; }

    /// <summary>The identities this term reads, which is what a caller passes to the solver and nothing more (TD-092).</summary>
    public abstract IEnumerable<string> Variables { get; }
}

public sealed record ConstantTerm(long Value, int Width, bool Signed = true) : ValueTerm
{
    public override int Width { get; } = Width;
    public override bool Signed { get; } = Signed;
    public override IEnumerable<string> Variables => [];
}

/// <summary>A value with an identity: two terms of one identity are one value, and two executions never share an identity
/// unless the value is proven canonical.</summary>
public sealed record VariableTerm(string Identity, int Width, bool Signed = true) : ValueTerm
{
    public override int Width { get; } = Width;
    public override bool Signed { get; } = Signed;
    public override IEnumerable<string> Variables => [Identity];
}

/// <summary>A sum in the width of its operands, which wraps exactly as the type does.</summary>
public sealed record SumTerm(ValueTerm Left, ValueTerm Right) : ValueTerm
{
    public override int Width => Left.Width;
    public override bool Signed => Left.Signed;
    public override IEnumerable<string> Variables => Left.Variables.Concat(Right.Variables);
}

/// <summary>A conversion to another width: a narrowing one keeps the low bits, as a cast does, and a widening one extends by the
/// sign of what it converts — <see cref="Operand"/>'s own, never this term's, because it is the operand's bits that are being
/// made wider. <see cref="Signed"/> is the type the conversion produces, which is how this value reaches a wider one in
/// turn.</summary>
public sealed record ConvertTerm(ValueTerm Operand, int Width, bool Signed) : ValueTerm
{
    public override int Width { get; } = Width;
    public override bool Signed { get; } = Signed;
    public override IEnumerable<string> Variables => Operand.Variables;
}

/// <summary>
/// One candidate's question: can the two paths run and name one cell at the same time? It carries the predicates of both sides
/// and the two cells' expressions, and nothing else: an expression of another candidate would let one candidate's answer depend
/// on another's (TD-092).
/// </summary>
public sealed record SolverQuery(IReadOnlyList<PathPredicate> Conditions, ValueTerm? FirstSelector, ValueTerm? SecondSelector)
{
    /// <summary>Whether there is anything to ask at all: without a predicate to combine or two cells to compare, the answer is
    /// whatever the cheap filters already said.</summary>
    public bool IsEmpty => Conditions.Count(condition => condition.IsSupported) < 2 && (FirstSelector is null || SecondSelector is null);
}

/// <summary>An answer with what the solver said about it: the unsatisfiable core for a suppression, the model for a candidate
/// that stands, and the reason for an answer that decided nothing (TD-093).</summary>
public sealed record SolverOutcome(SolverAnswer Answer, string Trace)
{
    public static SolverOutcome Unknown(string reason) => new(SolverAnswer.Unknown, reason);
}

/// <summary>
/// The solver as the analysis sees it, with no type of any solver in the signature: this is the seam a test replaces to run the
/// engine as if the native library had never loaded (ADR 0004).
/// </summary>
public interface IConstraintSolver : IDisposable
{
    /// <summary>Whether the solver is there at all. An unavailable solver still answers, and answers <c>Unknown</c>.</summary>
    bool IsAvailable { get; }

    SolverOutcome Decide(SolverQuery query, TimeSpan limit);
}

/// <summary>A solver that never started: every query is unknown, and the run goes on without it (ADR 0004).</summary>
public sealed class UnavailableSolver(string reason) : IConstraintSolver
{
    public const string NOT_LOADED = "The solver's native library did not load.";

    public bool IsAvailable => false;

    public SolverOutcome Decide(SolverQuery query, TimeSpan limit) => SolverOutcome.Unknown(reason);

    public void Dispose()
    {
    }
}

/// <summary>
/// What is left of one run's whole share of its deadline (TD-094). The share belongs to the run and not to each of its scopes:
/// a budget that started again for every project would let a run of ten projects spend ten times the allowance, so the one
/// budget is carried from scope to scope and every query is cut to what is left of it.
/// </summary>
public sealed class SolverBudget(TimeSpan total)
{
    public TimeSpan Remaining { get; private set; } = total;

    public bool IsSpent => Remaining <= TimeSpan.Zero;

    /// <summary>What one query may take: its own limit, or the rest of the run where that is less.</summary>
    public TimeSpan LimitOf(TimeSpan queryLimit) => Remaining < queryLimit ? Remaining : queryLimit;

    public void Spend(TimeSpan elapsed) => Remaining -= elapsed;
}

/// <summary>The limits the server runs the solver under, which are constants of the server and of no caller's (TD-094): a
/// query has 150 ms, and every query of a run together has a tenth of the run's deadline.</summary>
public static class SolverPolicy
{
    public static TimeSpan QueryLimit { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The run deadline the budget is a share of; the same 30 minutes the run registry gives a run.</summary>
    public static TimeSpan Deadline { get; } = TimeSpan.FromMinutes(30);

    public const double DeadlineShare = 0.10;

    public static TimeSpan Budget { get; } = Deadline * DeadlineShare;
}

/// <summary>What the solver did to a scope's candidates, as coverage reports it (TD-093).</summary>
public static class SolverCounters
{
    public const string AVAILABLE = "solver-available";
    public const string UNAVAILABLE = "solver-unavailable";
    public const string QUERIES = "solver-queries";
    public const string UNSAT = "solver-unsat";
    public const string SAT = "solver-sat";
    public const string UNKNOWN = "solver-unknown";
    public const string BUDGET_EXHAUSTED = "solver-budget-exhausted";
}
