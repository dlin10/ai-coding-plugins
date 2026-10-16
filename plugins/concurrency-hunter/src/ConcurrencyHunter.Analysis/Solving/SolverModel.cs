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

/// <summary>Why a query stopped without deciding (ADR 0021): only <see cref="Safeguard"/> depends on the clock, which is why
/// coverage counts it apart.</summary>
public enum SolverStop
{
    /// <summary>The query was decided.</summary>
    None,

    /// <summary>The solver spent the query's whole work limit.</summary>
    ResourceLimit,

    /// <summary>The wall-clock safeguard stopped the solver before its work limit did.</summary>
    Safeguard,

    /// <summary>The solver gave up on its own: a theory it does not carry, a failure, or a solver that never started.</summary>
    Solver
}

/// <summary>An answer with what the solver said about it: the unsatisfiable core for a suppression, the model for a candidate
/// that stands, and the reason for an answer that decided nothing (TD-093).</summary>
/// <param name="Answer">What the query decided.</param>
/// <param name="Trace">The core, the model or the reason, as the run's diagnostics and an undecided finding carry it.</param>
public sealed record SolverOutcome(SolverAnswer Answer, string Trace)
{
    /// <summary>The solver's work units the query used, as the solver counts them (ADR 0021).</summary>
    public ulong Work { get; init; }

    /// <summary>Why an <see cref="SolverAnswer.Unknown"/> answer stopped; <see cref="SolverStop.None"/> for a decided one.</summary>
    public SolverStop Stop { get; init; }

    /// <summary>An answer that decided nothing.</summary>
    /// <param name="reason">Why, as the finding's uncertainty carries it.</param>
    /// <param name="stop">What stopped the query; the solver's own answer unless a limit did.</param>
    /// <param name="work">The work units the query used.</param>
    public static SolverOutcome Unknown(string reason, SolverStop stop = SolverStop.Solver, ulong work = 0) =>
        new(SolverAnswer.Unknown, reason) { Stop = stop, Work = work };
}

/// <summary>What one query may spend (TD-094, ADR 0021). The work limit, in the solver's own units, is what decides the answer,
/// so the same query gets the same answer under any load; the wall-clock safeguard only stops a pathological query, well past
/// what a query at its work limit takes.</summary>
/// <param name="Work">The solver's work units the query may use.</param>
/// <param name="Safeguard">The wall-clock time after which the solver is stopped whatever work it used.</param>
public sealed record SolverLimit(uint Work, TimeSpan Safeguard);

/// <summary>
/// The solver as the analysis sees it, with no type of any solver in the signature: this is the seam a test replaces to run the
/// engine as if the native library had never loaded (ADR 0004).
/// </summary>
public interface IConstraintSolver : IDisposable
{
    /// <summary>Whether the solver is there at all. An unavailable solver still answers, and answers <c>Unknown</c>.</summary>
    bool IsAvailable { get; }

    /// <summary>Decides one query within its limit, reporting the work it used and, for an undecided answer, what stopped
    /// it.</summary>
    /// <param name="query">The candidate's question.</param>
    /// <param name="limit">The work limit and the safeguard the query runs under.</param>
    SolverOutcome Decide(SolverQuery query, SolverLimit limit);
}

/// <summary>A solver that never started: every query is unknown, and the run goes on without it (ADR 0004).</summary>
public sealed class UnavailableSolver(string reason) : IConstraintSolver
{
    public const string NOT_LOADED = "The solver's native library did not load.";

    public bool IsAvailable => false;

    public SolverOutcome Decide(SolverQuery query, SolverLimit limit) => SolverOutcome.Unknown(reason);

    public void Dispose()
    {
    }
}

/// <summary>
/// What is left of one run's whole solver budget (TD-094, ADR 0021). The budget belongs to the run and not to each of its
/// scopes: a budget that started again for every project would let a run of ten projects spend ten times the allowance, so the
/// one budget is carried from scope to scope. Every query is charged its whole work limit, whatever work it used, so the budget
/// is a number of queries: a query that stopped early or late moves no later query, and no query is given less than its whole
/// limit.
/// </summary>
/// <param name="total">The run's whole budget, in the solver's work units.</param>
public sealed class SolverBudget(ulong total)
{
    public ulong Remaining { get; private set; } = total;

    /// <summary>Whether what is left still holds one more query's whole limit.</summary>
    /// <param name="limit">The limit the next query runs under.</param>
    public bool Holds(SolverLimit limit) => Remaining >= limit.Work;

    /// <summary>Charges a query its whole work limit.</summary>
    /// <param name="limit">The limit the query ran under.</param>
    public void Charge(SolverLimit limit) => Remaining -= limit.Work;
}

/// <summary>The limits the server runs the solver under, which are constants of the server and of no caller's (TD-094, ADR
/// 0021): a query has a work limit calibrated on Z3 4.12.2 as the work it does in 150 ms on a query that does not finish, and a
/// two-second safeguard; the run's budget is 1,200 such limits, what a tenth of the 30-minute deadline bought at 150 ms a query.
/// A change of the shipped Z3 is a re-calibration.</summary>
public static class SolverPolicy
{
    public const uint QUERY_WORK = 390_000;

    public const int BUDGET_QUERIES = 1_200;

    public static TimeSpan Safeguard { get; } = TimeSpan.FromSeconds(2);

    public static SolverLimit QueryLimit { get; } = new(QUERY_WORK, Safeguard);

    public static ulong Budget { get; } = (ulong)QUERY_WORK * BUDGET_QUERIES;
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
    public const string SAFEGUARD_TIMEOUT = "solver-safeguard-timeout";
}
