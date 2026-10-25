using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A lowered body reached from an initializer in the inert attempt.</summary>
/// <param name="Initializer">The initializer whose closure contains the body.</param>
/// <param name="Body">The body to scan, from the inert copy.</param>
/// <param name="Edges">All recorded outgoing edges of this body.</param>
/// <param name="Bodies">The inert attempt's lowered bodies.</param>
internal sealed record InitializerBody(string Initializer, IrBody Body, IReadOnlyList<ReachEdge> Edges,
                                        IReadOnlyDictionary<string, IrBody> Bodies);

/// <summary>Decides from lowered operations whether an initializer may start work, without a heap.</summary>
internal static class InitializerWork
{
    /// <summary>The call's own targets, excluding work and delegates passed as arguments at the same operation.</summary>
    /// <param name="reached">The body and its recorded outgoing edges.</param>
    /// <param name="call">The call whose targets are requested.</param>
    internal static IEnumerable<ReachEdge> CallTargets(InitializerBody reached, IrCallOperation call) =>
        reached.Edges.Where(edge => edge.Operation == call.Id && (call.CallKind == IrCallKind.Delegate
            ? edge.Kind == ReachEdgeKind.Delegate
            : edge.Kind == ReachEdgeKind.Call && (call.CallKind is IrCallKind.Virtual or IrCallKind.Interface ||
                edge.TargetBody == (call.TargetMethodId ?? call.Method))));

    /// <summary>The callee the scan can follow without a heap; argument delegates never resolve the call.</summary>
    /// <param name="reached">The body and its recorded outgoing edges.</param>
    /// <param name="call">The call being scanned.</param>
    internal static IrBody? CallBody(InitializerBody reached, IrCallOperation call) =>
        call.CallKind is IrCallKind.Virtual or IrCallKind.Interface or IrCallKind.Delegate
            ? null : reached.Bodies.GetValueOrDefault(call.TargetMethodId ?? call.Method);

    internal static bool Starts(InitializerBody reached, IrOperation operation) => operation switch
    {
        IrSpawnOperation => true,
        IrTimerOperation timer => timer.Action switch
        {
            IrTimerAction.Create => timer.CallbackValue is not null,
            IrTimerAction.ElapsedSubscribe or IrTimerAction.Change or IrTimerAction.Start => true,
            IrTimerAction.SetEnabled => timer.Flag != IrTimerFlag.False,
            _ => false
        },
        IrCallOperation call => !call.IsAwaitedImmediately && CallTargets(reached, call).Any(edge => Async(reached, edge.TargetBody)) ||
            CallBody(reached, call) is null &&
            reached.Edges.Any(edge => edge.Operation == call.Id && edge.Kind == ReachEdgeKind.ArgumentDelegate && Async(reached, edge.TargetBody)),
        _ => false
    };

    private static bool Async(InitializerBody reached, string body) =>
        reached.Bodies.GetValueOrDefault(body) is { IsAsync: true, IsAsyncIterator: false };
}
