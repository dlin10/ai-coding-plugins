using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Execution;

/// <summary>The heap's call edges by call and by callee, and the starts of child executions by the execution that makes them, each
/// built the first time it is asked for. A lookup gives what a pass over the whole list that keeps the matching items gives, in the
/// order of that list: the proofs of a handle ask these once per handle and pair, and the lists hold every edge and start of the scope.</summary>
/// <param name="edges">The heap's call edges, in its order.</param>
/// <param name="anchors">The starts of the child executions, in the order the analysis lists them.</param>
internal sealed class CallIndex(IReadOnlyList<CallEdge> edges, IReadOnlyList<SpawnAnchor> anchors)
{
    private Dictionary<(string Caller, int Operation), string[]>? _callees;
    private Dictionary<string, CallEdge[]>? _into;
    private Dictionary<string, SpawnAnchor[]>? _startsBy;

    /// <summary>The distinct instances a call's edges lead to, in the order of their first edge.</summary>
    /// <param name="caller">The calling instance.</param>
    /// <param name="operation">The call operation.</param>
    internal string[] Callees(string caller, int operation) =>
        (_callees ??= edges.GroupBy(edge => (edge.CallerInstance, edge.OperationId))
                           .ToDictionary(group => group.Key,
                                         group => group.Select(edge => edge.CalleeInstance).Distinct(StringComparer.Ordinal).ToArray()))
        .GetValueOrDefault((caller, operation)) ?? [];

    /// <summary>The edges into an instance, in the heap's order.</summary>
    /// <param name="callee">The called instance.</param>
    internal CallEdge[] Into(string callee) =>
        (_into ??= edges.GroupBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal))
        .GetValueOrDefault(callee) ?? [];

    /// <summary>The starts an execution makes, in the order the analysis lists them.</summary>
    /// <param name="execution">The execution making the starts.</param>
    internal SpawnAnchor[] StartsBy(string execution) =>
        (_startsBy ??= anchors.GroupBy(anchor => anchor.ExecutionId, StringComparer.Ordinal)
                              .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal))
        .GetValueOrDefault(execution) ?? [];
}
