namespace ConcurrencyHunter.Analysis;

/// <summary>A step timed by <see cref="ScopeStepTimes"/>. Lowering is nested inside the reachable set.</summary>
public enum ScopeStep
{
    RootDiscovery,
    ProgramIndex,
    Lowering,
    ReachableSet,
    SummariesAndFixpoint,
    Executions,
    Accesses
}

/// <summary>A pipeline cancellation with the completed steps and the stopped step's observations.</summary>
public sealed class ScopeCancelledException : OperationCanceledException
{
    internal ScopeCancelledException(ScopeStep step, bool started, TimeSpan elapsed, TimeSpan lowering,
                                      IReadOnlyDictionary<ScopeStep, TimeSpan> completedSteps, int? reachableBodies,
                                      IReadOnlyDictionary<ScopeStep, IReadOnlyDictionary<string, int>> counters,
                                      OperationCanceledException innerException, CancellationToken cancellationToken)
        : base($"The scope stopped in {step}.", innerException, cancellationToken)
    {
        Step = step;
        Started = started;
        Elapsed = elapsed;
        Lowering = lowering;
        CompletedSteps = completedSteps;
        ReachableBodies = reachableBodies;
        Counters = counters;
    }

    public ScopeStep Step { get; }
    public bool Started { get; }
    public TimeSpan Elapsed { get; }
    public TimeSpan Lowering { get; }
    public IReadOnlyDictionary<ScopeStep, TimeSpan> CompletedSteps { get; }
    public int? ReachableBodies { get; }
    public IReadOnlyDictionary<ScopeStep, IReadOnlyDictionary<string, int>> Counters { get; }
}
