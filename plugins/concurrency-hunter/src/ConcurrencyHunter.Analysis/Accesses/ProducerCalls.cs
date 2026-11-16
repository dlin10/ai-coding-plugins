using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>Finds the program's library calls producing a deciding call's receiver and arguments through the solved heap.</summary>
public static class ProducerCalls
{
    /// <summary>Follows inputs and their producers for at most eight heap-flow hops, independently for each deciding call.</summary>
    /// <param name="heap">The solved heap of the current analysis.</param>
    /// <param name="deciding">The deciding call.</param>
    /// <param name="isProgramBody">Whether a body belongs to the program, rather than a library.</param>
    public static IReadOnlySet<CallIdentity> Of(HeapSolution heap, CallIdentity deciding, Func<string, bool> isProgramBody)
    {
        var found = new HashSet<CallIdentity>();
        var walk = new GapOrigins(heap, []);
        foreach (var instance in heap.Instances.Values.Where(instance => instance.BodyId == deciding.BodyId))
        {
            WorkCancellation.Check();
            foreach (var operation in instance.Summary.CallIdentities.Where(pair => pair.Value == deciding).Select(pair => pair.Key))
            {
                WorkCancellation.Check();
                walk.Walk(instance, Inputs(instance.Summary, operation), Visit, allocations: true);
            }
        }
        found.Remove(deciding);
        return found;

        ValueOrigin? Visit(MethodInstance instance, int operation)
        {
            if (!instance.Summary.CallIdentities.TryGetValue(operation, out var identity))
                return null;
            // Program helpers carry their returns; library bodies never replace the project call that produced the value.
            if (isProgramBody(identity.Target))
                return null;
            if (isProgramBody(instance.BodyId))
                found.Add(identity);
            return Inputs(instance.Summary, operation);
        }
    }

    private static ValueOrigin Inputs(MethodSummary summary, int operation)
    {
        if (summary.Calls.FirstOrDefault(call => call.OperationId == operation) is { } call)
            return ValueOrigin.Union(call.Arguments.Select(argument => argument.Producers).Prepend(call.ReceiverOrigin));
        if (summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == operation) is { } opaque)
            return ValueOrigin.Union(opaque.Arguments.Select(argument => argument.Producers).Prepend(opaque.ReceiverOrigin));
        return summary.CallInputs.GetValueOrDefault(operation) ?? ValueOrigin.None;
    }
}
