namespace ConcurrencyHunter.Execution;

/// <summary>What the timing of a scope's pairs did, counted for the pairing checkpoints and never read by the analysis: one set for a
/// happens-before graph and for every graph its assumption replays build (R5). The graph answers one question at a time, and so do
/// its counters.</summary>
internal sealed class TimingCounters
{
    /// <summary>The questions whether happens-before or the completion proof orders a pair, on the graph and its replays.</summary>
    internal long OrderQueries;

    /// <summary>The path searches between two executions that the execution reach let through to the point-level search.</summary>
    internal long PathSearches;

    /// <summary>The events those searches took from their queues.</summary>
    internal long PathEvents;

    /// <summary>The pairs whose assumptions were asked by withholding readings, each answered on replays of the graph.</summary>
    internal long AssumptionQueries;

    /// <summary>The replays built: a graph of the scope for each combination of assumption sets withheld.</summary>
    internal long Replays;

    /// <summary>The flows of one execution built, on the graph and its replays.</summary>
    internal long Flows;

    /// <summary>The nodes of those flows.</summary>
    internal long FlowNodes;

    /// <summary>The execution reach sets built, one per start execution and graph.</summary>
    internal long ReachSets;

    /// <summary>The executions those sets hold.</summary>
    internal long ReachExecutions;

    /// <summary>The counts as they stand, for a later report of what happened since.</summary>
    internal TimingCounters Copy() => (TimingCounters)MemberwiseClone();

    /// <summary>What was counted since an earlier copy, as a checkpoint line's fields.</summary>
    /// <param name="before">The copy taken earlier.</param>
    internal string Since(TimingCounters before) =>
        $"orderQueries={OrderQueries - before.OrderQueries} pathSearches={PathSearches - before.PathSearches} " +
        $"pathEvents={PathEvents - before.PathEvents} assumptionQueries={AssumptionQueries - before.AssumptionQueries} " +
        $"replays={Replays - before.Replays} replaysTotal={Replays} flows={Flows - before.Flows} flowNodes={FlowNodes - before.FlowNodes} " +
        $"flowsTotal={Flows} reachSets={ReachSets - before.ReachSets} reachExecutions={ReachExecutions - before.ReachExecutions}";
}
