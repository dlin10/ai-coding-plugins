namespace ConcurrencyHunter.Heap;

/// <summary>What a heap solve spent its passes on, which the checkpoint at the end of the heap solve reports in a diagnostic run. None
/// of it is a counter of <see cref="HeapSolution.Counters"/>: the run's metrics and snapshots are the same with or without it.</summary>
/// <param name="Skipped">The instances a pass found clean and did not process again.</param>
/// <param name="Calls">The calls the processed instances made, each counted once per processing; an audit or a full pass after
/// convergence, when asked for, adds its own.</param>
/// <param name="ChangingCalls">The part of <paramref name="Calls"/> that added a solver fact. A call that added none may still have had
/// new inputs, so the rest is at most the share of calls a worklist per call could skip.</param>
/// <param name="SkippedCalls">The calls of processed instances found clean and not processed again; not part of
/// <paramref name="Calls"/>.</param>
/// <param name="DirtyChecks">The time the passes took to decide whether an instance is clean.</param>
/// <param name="Processing">The time the passes took to process the instances they found dirty.</param>
/// <param name="CallTime">The part of <paramref name="Processing"/> that went into the instances' calls, the callees they reach and the
/// arguments bound to them; a full pass after convergence, when asked for, adds its own.</param>
/// <param name="PassRest">The time the passes took after their instances: assembling the instances' contributions, the holders
/// captured by delegates handed over, the factories and the sinks.</param>
/// <param name="Total">The time the whole solve took, the rounds between the passes included.</param>
internal sealed record HeapSolveProfile(long Skipped, long Calls, long ChangingCalls, long SkippedCalls, TimeSpan DirtyChecks,
                                        TimeSpan Processing, TimeSpan CallTime, TimeSpan PassRest, TimeSpan Total)
{
    /// <summary>The profile as one report line, with the passes and processings the solve counted.</summary>
    /// <param name="counters">The solve's counters.</param>
    internal string Line(IReadOnlyDictionary<string, int> counters) =>
        $"solver passes={counters.GetValueOrDefault(HeapCounters.PROPAGATE_PASSES)} " +
        $"processed={counters.GetValueOrDefault(HeapCounters.INSTANCE_PROCESSINGS)} skipped={Skipped} calls={Calls} " +
        $"changingCalls={ChangingCalls} skippedCalls={SkippedCalls} dirtyCheckMs={(long)DirtyChecks.TotalMilliseconds} processMs={(long)Processing.TotalMilliseconds} " +
        $"callsMs={(long)CallTime.TotalMilliseconds} passRestMs={(long)PassRest.TotalMilliseconds} solveMs={(long)Total.TotalMilliseconds}";
}
