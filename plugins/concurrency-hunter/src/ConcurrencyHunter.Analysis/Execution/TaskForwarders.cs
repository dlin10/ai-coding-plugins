using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Execution;

/// <summary>Proves which async call a non-async member forwards, once per run of that member.</summary>
/// <param name="scope">The bodies in the scope.</param>
/// <param name="heap">Their instances and call targets.</param>
/// <param name="steps">The call steps of each execution.</param>
/// <param name="calls">The heap's call edges and the starts of the child executions, indexed.</param>
/// <param name="proveHandle">The proof of a returned task's identity.</param>
/// <param name="knownTarget">Whether the call's known targets account for every alternative.</param>
internal sealed class TaskForwarders(ScopeProgram scope, HeapSolution heap,
                                     IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>> steps,
                                     CallIndex calls,
                                     Func<string, MethodInstance, SummaryValue, int, string?> proveHandle,
                                     Func<string, MethodInstance, int, bool> knownTarget)
{
    private readonly HashSet<(string Execution, string Instance)> _proving = [];
    private readonly Dictionary<(string Body, int Operation), bool> _cycles = [];
    private readonly Dictionary<(string Execution, string Instance), SpawnAnchor?> _returned = [];

    /// <summary>The terminal async spawn forwarded by every return of one run of the instance, or null.</summary>
    /// <param name="execution">The execution running the member.</param>
    /// <param name="instanceId">The member's instance.</param>
    internal SpawnAnchor? Returned(string execution, string instanceId)
    {
        WorkCancellation.Check();
        if (_proving.Contains((execution, instanceId)))
            return null;
        if (_returned.TryGetValue((execution, instanceId), out var known))
            return known;
        return _returned[(execution, instanceId)] = Prove(execution, instanceId);
    }

    /// <summary>Proves the returns and the number of calls of one instance.</summary>
    /// <param name="execution">The execution running the member.</param>
    /// <param name="instanceId">The member's instance.</param>
    private SpawnAnchor? Prove(string execution, string instanceId)
    {
        if (!heap.Instances.TryGetValue(instanceId, out var instance) ||
            !scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) || body.IsAsync ||
            instance.Summary.Returns.Count == 0 || !_proving.Add((execution, instanceId)))
            return null;
        try
        {
            SpawnAnchor? result = null;
            int? source = null;
            foreach (var returned in instance.Summary.Returns)
            {
                WorkCancellation.Check();
                var value = new SummaryValue(returned.Values, returned.UnknownSources)
                {
                    SourceCalls = returned.SourceCalls, Completions = returned.Completions,
                    CompletedCalls = returned.CompletedCalls
                };
                // A parameter, a field and a completed task are not this member's call, even when their points-to sets agree.
                if (value.Completions.Count != 0 || value.CompletedCalls.Count != 0 ||
                    value.UnknownSources.Any(origin => origin != UnknownSource.SourceCall) ||
                    value.Values.Any(item => item is not CallResultValue) || value.SourceCalls.ToArray() is not [var call] ||
                    source is { } previous && previous != call ||
                    !instance.Summary.Calls.Any(candidate => candidate.OperationId == call) ||
                    !knownTarget(execution, instance, call))
                    return null;
                source = call;
                if (calls.Callees(instanceId, call) is not [var target] || !heap.Instances.TryGetValue(target, out var callee))
                    return null;
                var asyncBody = scope.Reachable.Bodies.GetValueOrDefault(callee.BodyId) is { IsAsync: true, IsAsyncIterator: false };
                var forwarded = asyncBody ? calls.StartsBy(execution).FirstOrDefault(anchor =>
                    anchor.Kind == SpawnAnchorKind.AsyncReturn && anchor.CallerInstance == instanceId && anchor.OperationId == call)
                    : Returned(execution, target);
                if (forwarded is null || result is not null && result != forwarded ||
                    proveHandle(execution, instance, value, returned.OperationId) != forwarded.ChildId)
                    return null;
                result = forwarded;
            }
            return result is not null && OnePath(heap, steps.GetValueOrDefault(execution) ?? [],
                heap.Instances[result.CallerInstance].BodyId, result.OperationId, false,
                new HashSet<string>([instanceId], StringComparer.Ordinal), InCycle) ? result : null;
        }
        finally
        {
            _proving.Remove((execution, instanceId));
        }
    }

    /// <summary>The forwarded async spawn of a call with one resolved target, or null.</summary>
    /// <param name="execution">The execution making the call.</param>
    /// <param name="caller">The calling instance.</param>
    /// <param name="operation">The call operation.</param>
    internal SpawnAnchor? Call(string execution, string caller, int operation)
    {
        if (!heap.Instances[caller].Summary.Calls.Any(call => call.OperationId == operation) ||
            !knownTarget(execution, heap.Instances[caller], operation))
            return null;
        return calls.Callees(caller, operation) is [var target] ? Returned(execution, target) : null;
    }

    /// <summary>The async spawn named by a value's one unconsumed source call in this execution, directly or through a forwarder.</summary>
    /// <param name="execution">The execution expressing the value.</param>
    /// <param name="instance">The instance expressing it.</param>
    /// <param name="value">The task value.</param>
    internal SpawnAnchor? Source(string execution, MethodInstance instance, SummaryValue value)
    {
        if (value.Completions.Count != 0 || value.CompletedCalls.Count != 0 || value.SourceCalls.ToArray() is not [var operation] ||
            !instance.Summary.Calls.Any(call => call.OperationId == operation) || !knownTarget(execution, instance, operation))
            return null;
        if (calls.Callees(instance.Id, operation) is not [var target])
            return null;
        return scope.Reachable.Bodies[heap.Instances[target].BodyId] is { IsAsync: true, IsAsyncIterator: false }
            ? calls.StartsBy(execution).FirstOrDefault(anchor => anchor.CallerInstance == instance.Id && anchor.OperationId == operation &&
                                                                 anchor.Kind == SpawnAnchorKind.AsyncReturn)
            : Returned(execution, target);
    }

    /// <summary>Whether an operation's block can be reached again through the body's flow predecessors.</summary>
    /// <param name="bodyId">The body.</param>
    /// <param name="operation">The operation.</param>
    private bool InCycle(string bodyId, int operation)
    {
        if (_cycles.TryGetValue((bodyId, operation), out var known))
            return known;
        var body = scope.Reachable.Bodies[bodyId];
        var block = body.Blocks.FirstOrDefault(block => block.Operations.Any(item => item.Id == operation));
        if (block is null)
            return false;
        var pending = new Stack<int>(block.FlowPredecessors.Select(edge => edge.BlockOrdinal));
        var seen = new HashSet<int>();
        while (pending.TryPop(out var ordinal))
        {
            WorkCancellation.Check();
            if (ordinal == block.Ordinal)
                return _cycles[(bodyId, operation)] = true;
            if (seen.Add(ordinal))
                foreach (var predecessor in body.Blocks.First(item => item.Ordinal == ordinal).FlowPredecessors)
                {
                    WorkCancellation.Check();
                    pending.Push(predecessor.BlockOrdinal);
                }
        }
        return _cycles[(bodyId, operation)] = false;
    }

    /// <summary>Whether the roots reach a site by at most one path, without a loop, recursion or repeated delegate run.</summary>
    /// <param name="heap">The heap naming the instances and repeated runs.</param>
    /// <param name="steps">The steps of the execution.</param>
    /// <param name="bodyId">The body holding the site.</param>
    /// <param name="operationId">The site.</param>
    /// <param name="afterCall">Whether to count the call's return rather than its repeated delegate runs.</param>
    /// <param name="roots">The instances whose runs are counted.</param>
    /// <param name="inCycle">Whether a site lies in a cycle of its body.</param>
    internal static bool OnePath(HeapSolution heap, IReadOnlyList<ExecutionStep> steps, string bodyId, int operationId, bool afterCall,
                                 IReadOnlySet<string> roots, Func<string, int, bool> inCycle)
    {
        bool Repeated(ExecutionStep step) => heap.RepeatedRuns.Contains((step.Caller, step.OperationId, step.Callee));
        if (inCycle(bodyId, operationId) || !afterCall && steps.Any(step => step.OperationId == operationId &&
            Repeated(step) && heap.Instances[step.Caller].BodyId == bodyId))
            return false;
        var incoming = steps.GroupBy(step => step.Callee, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Select(step =>
                                (step.Caller, step.OperationId, Repeated: Repeated(step))).Distinct().ToArray(), StringComparer.Ordinal);
        var nodes = roots.Concat(incoming.Keys).ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            WorkCancellation.Check();
            changed = false;
            foreach (var node in nodes)
            {
                WorkCancellation.Check();
                var total = roots.Contains(node) ? 1 : 0;
                foreach (var (caller, operation, repeated) in incoming.GetValueOrDefault(node) ?? [])
                {
                    WorkCancellation.Check();
                    total += counts.GetValueOrDefault(caller) * (repeated || inCycle(heap.Instances[caller].BodyId, operation) ? 2 : 1);
                }
                total = Math.Min(total, 2);
                if (total != counts.GetValueOrDefault(node))
                {
                    counts[node] = total;
                    changed = true;
                }
            }
        }
        return counts.Where(pair => heap.Instances[pair.Key].BodyId == bodyId).Sum(pair => pair.Value) <= 1;
    }
}
