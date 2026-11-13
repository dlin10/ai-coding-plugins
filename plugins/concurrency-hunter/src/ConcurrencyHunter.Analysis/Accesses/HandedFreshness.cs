using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>The allocation-grounded least fixpoint of fresh parameters and receivers of one scope.</summary>
internal sealed class HandedFreshness
{
    private readonly HashSet<(string Instance, HandedSlot Slot)> _fresh = [];
    private readonly Dictionary<string, bool> _forwarders = new(StringComparer.Ordinal);
    private readonly ScopeProgram _scope;
    private readonly ExecutionAnalysis _executions;

    internal HandedFreshness(ScopeProgram scope, HeapSolution heap, ExecutionAnalysis executions, bool reverseOrder = false, int? orderSeed = null)
    {
        _scope = scope;
        _executions = executions;
        var incoming = heap.Edges.GroupBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                           .ToDictionary(group => group.Key, group => group.DistinctBy(edge =>
                               (edge.CallerInstance, edge.OperationId, edge.CalleeInstance)).ToArray(), StringComparer.Ordinal);
        var roots = heap.RootInstances.Values.ToHashSet(StringComparer.Ordinal);
        var instances = heap.Instances.Values.OrderBy(instance => instance.Id, StringComparer.Ordinal).ToArray();
        if (reverseOrder)
            Array.Reverse(instances);
        if (orderSeed is int seed)
            new Random(seed).Shuffle(instances);
        bool changed;
        do
        {
            WorkCancellation.Check();
            changed = false;
            foreach (var instance in instances)
            {
                WorkCancellation.Check();
                if (instance.IsMerged || roots.Contains(instance.Id) || !incoming.TryGetValue(instance.Id, out var edges) || edges.Length == 0)
                    continue;
                var slots = instance.Parameters.Keys.Order().Select(ordinal => new HandedSlot(ordinal)).Prepend(new HandedSlot(null)).ToArray();
                if (reverseOrder)
                    Array.Reverse(slots);
                if (orderSeed is int slotSeed)
                    new Random(slotSeed).Shuffle(slots);
                foreach (var slot in slots)
                {
                    WorkCancellation.Check();
                    if (IsFresh(instance.Id, slot))
                        continue;
                    var regions = slot.ParameterOrdinal is int ordinal ? instance.Parameters[ordinal] : instance.Receivers;
                    if (regions.Count == 0)
                        continue;
                    var passed = new HashSet<string>(StringComparer.Ordinal);
                    var proven = true;
                    foreach (var edge in edges)
                    {
                        WorkCancellation.Check();
                        if (!heap.TransferEdges.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance)))
                        {
                            proven = false;
                            break;
                        }
                        var caller = heap.Instances[edge.CallerInstance];
                        var call = caller.Summary.Calls.Single(transfer => transfer.OperationId == edge.OperationId);
                        var argument = slot.ParameterOrdinal is int parameter
                            ? call.Arguments.SingleOrDefault(value => value.ParameterOrdinal == parameter) : null;
                        var values = slot.ParameterOrdinal is null ? call.Receivers : argument?.Values;
                        var local = slot.ParameterOrdinal is null ? call.ReceiverIsFresh : argument?.IsFresh == true;
                        var handed = slot.ParameterOrdinal is null ? call.ReceiverHandedSlot : argument?.HandedSlot;
                        var unknown = slot.ParameterOrdinal is null ? call.ReceiverUnknownSources : argument?.UnknownSources;
                        if (values is null || unknown is null || !ValueIsFresh(caller.Id, local, handed) ||
                            unknown.Any(source => source != UnknownSource.Parameter || handed?.ParameterOrdinal is null) ||
                            !Waited(caller, instance, call, local, handed))
                        {
                            proven = false;
                            break;
                        }
                        var bound = values.SelectMany(value => heap.Resolve(caller.Id, value)).ToHashSet(StringComparer.Ordinal);
                        // Dispatch passes only the receivers this target instance runs on; arguments are passed whole.
                        if (slot.ParameterOrdinal is null)
                            bound.IntersectWith(instance.Receivers);
                        if (bound.Count == 0)
                        {
                            proven = false;
                            break;
                        }
                        passed.UnionWith(bound);
                    }
                    if (proven && passed.SetEquals(regions))
                        changed |= _fresh.Add((instance.Id, slot));
                }
            }
        } while (changed);
    }

    internal bool IsFresh(string instance, HandedSlot? slot) => slot is not null && _fresh.Contains((instance, slot));

    private bool ValueIsFresh(string instance, bool local, HandedSlot? slot) => local || IsFresh(instance, slot);

    private bool IsForwarder(string instance)
    {
        if (!_forwarders.TryGetValue(instance, out var result))
        {
            result = _executions.InstanceExecutions.TryGetValue(instance, out var executions) && executions.Count != 0 &&
                     executions.All(execution => _executions.ForwardedTask(execution, instance) is not null);
            _forwarders.Add(instance, result);
        }
        return result;
    }

    private bool Waited(MethodInstance caller, MethodInstance callee, CallTransfer call, bool local, HandedSlot? slot)
    {
        if (!_scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) || body.IsIterator || body.IsAsyncIterator)
            return false;
        if (!body.IsAsync && !IsForwarder(callee.Id))
            return true;
        if (call.IsAwaitedImmediately)
            return true;
        // A slot already proved fresh in a forwarder got there only by kinds 2 or 3: kind 1 excludes forwarders.
        // This induction also prevents a recursive forwarding cycle from proving itself fresh.
        return IsForwarder(caller.Id) && caller.Summary.Returns.Count != 0 &&
               caller.Summary.Returns.All(value => value.SourceCalls.Count == 1 && value.SourceCalls.Contains(call.OperationId)) &&
               (local || IsFresh(caller.Id, slot));
    }
}
