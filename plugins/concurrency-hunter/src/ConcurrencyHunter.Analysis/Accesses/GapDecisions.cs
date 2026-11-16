using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>The components of TD-103 a semantic gap can decide (R6).</summary>
public static class GapComponents
{
    public const string OPERATION = "operation";
    public const string OVERLAP = "overlap";
    public const string PROTECTION = "protection";
}

/// <summary>A check of a pair that a semantic gap decides (R6): the gap's callee and kind, the component of TD-103 the check is, and
/// why the gap decides it.</summary>
public sealed record GapCheck(string Callee, string Kind, string Component, string Reason)
{
    /// <summary>What the finding's uncertainty says about it.</summary>
    public string Uncertainty => $"The unresolved call {Callee} ({Kind}) decides the {Component} check: {Reason}.";
}

/// <summary>A check and the particular call that decides it. The call is not part of the check's equality.</summary>
/// <param name="Check">The unchanged callee-level check.</param>
/// <param name="Call">The call that decided the check, null for a dynamic operation with no member target.</param>
public sealed record DecidingCall(GapCheck Check, CallIdentity? Call);

/// <summary>A gap reached through the result of a particular operation.</summary>
/// <param name="Gap">The callee-level gap.</param>
/// <param name="Call">The producing call, null for a dynamic operation.</param>
internal sealed record GapOrigin(SemanticGap Gap, CallIdentity? Call)
{
    internal string Callee => Gap.Callee;
    internal string Kind => Gap.Kind;
}

/// <summary>The semantic gaps whose result a value may be (R6): the calls of its body the value is the result of; through a call with a
/// body, what the callee returns; through a parameter, what each caller handed over; and through a field, what any store put into that
/// field of the objects read, as far as the heap's call edges and stores go.</summary>
internal sealed class GapOrigins
{
    private const int MAX_DEPTH = 8;

    private readonly HeapSolution _heap;
    private readonly IReadOnlyDictionary<string, MethodSummary> _summaries;
    private readonly Dictionary<(string BodyId, int OperationId), SemanticGap> _sites = [];
    private readonly Dictionary<(string Caller, int Operation), string[]> _callees;
    private readonly Dictionary<string, (string Caller, int Operation)[]> _callers;
    private readonly Lazy<ILookup<string, (MethodInstance Instance, StoreTransfer Store)>> _stores;
    private readonly Lazy<(MethodInstance Instance, ElementTransfer Store)[]> _elementStores;

    internal GapOrigins(HeapSolution heap, IReadOnlyList<SemanticGap> gaps)
    {
        _heap = heap;
        _summaries = heap.Instances.Values.GroupBy(instance => instance.BodyId, StringComparer.Ordinal)
                         .ToDictionary(group => group.Key, group => group.First().Summary, StringComparer.Ordinal);
        foreach (var gap in gaps)
        {
            WorkCancellation.Check();
            foreach (var site in gap.Sites)
            {
                WorkCancellation.Check();
                _sites.TryAdd((site.BodyId, site.OperationId), gap);
            }
        }

        _callees = heap.Edges.GroupBy(edge => (edge.CallerInstance, edge.OperationId))
                       .ToDictionary(group => group.Key, group => group.Select(edge => edge.CalleeInstance).Distinct(StringComparer.Ordinal).ToArray());
        _callers = heap.Edges.GroupBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                       .ToDictionary(group => group.Key, group => group.Select(edge => (edge.CallerInstance, edge.OperationId)).Distinct().ToArray(),
                                     StringComparer.Ordinal);
        // A field is written by a store, or by a store through a reference that names it.
        _stores = new(() => heap.Instances.Values.SelectMany(instance => instance.Summary.Stores.Concat(instance.Summary.ReferenceStores)
                                                                                 .Select(store => (instance, store)))
                                .ToLookup(item => FieldSlot.Key(item.store.Field), StringComparer.Ordinal));
        // A cell read gives back what went into the cells and a key read what went into the keys, each apart; never the list a node
        // was added to.
        _elementStores = new(() => heap.Instances.Values.SelectMany(instance => instance.Summary.Elements.Where(element => element.Kind == ElementOperationKind.Store &&
                                                                                                                           PathValue.IsStorage(element.Slot))
                                                                                         .Concat(instance.Summary.ReferenceElementStores)
                                                                                         .Select(element => (instance, element)))
                                       .ToArray());
        _copies = new(() => heap.Instances.Values.SelectMany(instance => instance.Summary.StructCopies.Select(copy => (instance, copy))).ToArray());
    }

    /// <summary>The struct copies of every instance, through which a struct's fields carry what was stored into them.</summary>
    private readonly Lazy<(MethodInstance Instance, StructCopy Copy)[]> _copies;

    /// <summary>The closures holding a capture cell an instance reads and writes: those each run of the body declaring it creates (R4).</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="cell">The capture cell.</param>
    private HashSet<string> Closures(MethodInstance instance, IrClosureCell cell) =>
        _heap.ClosuresOf(instance, cell).ToHashSet(StringComparer.Ordinal);

    /// <summary>The gap one of whose call sites an operation of a body is, null for any other operation.</summary>
    /// <param name="bodyId">The body.</param>
    /// <param name="operationId">The operation in that body.</param>
    internal GapOrigin? AtSite(string bodyId, int operationId) =>
        _sites.TryGetValue((bodyId, operationId), out var gap)
            ? new GapOrigin(gap, _summaries.GetValueOrDefault(bodyId)?.CallIdentities.GetValueOrDefault(operationId)) : null;

    internal IReadOnlyList<GapOrigin> Of(MethodInstance instance, ValueOrigin origin)
    {
        var found = new List<GapOrigin>();
        Walk(instance, origin, (current, operation) =>
        {
            if (AtSite(current.BodyId, operation) is not { } gap)
                return null;
            if (!found.Contains(gap))
                found.Add(gap);
            return ValueOrigin.None;
        });
        return found;
    }

    /// <summary>Follows the heap's value flows, stopping at calls the visitor answers. An answer follows the call's inputs instead
    /// of its returns; null follows its returns. Additional call results and allocation origins are read only by the producer walk.</summary>
    /// <param name="instance">The instance whose value is being followed.</param>
    /// <param name="origin">The value's origins.</param>
    /// <param name="call">The call visitor.</param>
    /// <param name="allocations">Whether allocations lead to their constructors.</param>
    internal void Walk(MethodInstance instance, ValueOrigin origin, Func<MethodInstance, int, ValueOrigin?> call, bool allocations = false)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(MethodInstance Instance, ValueOrigin Origin, int Depth)>([(instance, origin, 0)]);
        while (pending.TryDequeue(out var item))
        {
            WorkCancellation.Check();
            var (current, from, depth) = item;
            var operations = allocations
                ? from.Calls.Concat(from.ProducingCalls).Concat(from.Allocations.SelectMany(allocation => current.Summary.AllocationConstructors.GetValueOrDefault(allocation) ?? [])).Distinct()
                : from.Calls;
            foreach (var operation in operations.Order())
            {
                WorkCancellation.Check();
                if (!visited.Add($"call|{current.Id}|{operation}"))
                    continue;
                if (call(current, operation) is { } inputs)
                {
                    if (depth < MAX_DEPTH && !inputs.IsNone)
                        pending.Enqueue((current, inputs, depth + 1));
                    continue;
                }

                if (depth >= MAX_DEPTH)
                    continue;
                foreach (var callee in _callees.GetValueOrDefault((current.Id, operation)) ?? [])
                {
                    WorkCancellation.Check();
                    if (_heap.Instances.TryGetValue(callee, out var calleeInstance))
                    {
                        foreach (var returned in calleeInstance.Summary.Returns)
                        {
                            WorkCancellation.Check();
                            pending.Enqueue((calleeInstance, returned.Producers, depth + 1));
                        }
                    }
                }
            }

            if (depth >= MAX_DEPTH)
                continue;

            // A parameter is what each caller handed over at the call that bound it.
            foreach (var ordinal in from.Parameters.Order())
            {
                WorkCancellation.Check();
                if (!visited.Add($"parameter|{current.Id}|{ordinal}"))
                    continue;
                foreach (var (callerId, operation) in _callers.GetValueOrDefault(current.Id) ?? [])
                {
                    WorkCancellation.Check();
                    if (!_heap.Instances.TryGetValue(callerId, out var caller))
                        continue;
                    foreach (var transfer in caller.Summary.Calls.Where(call => call.OperationId == operation))
                    {
                        WorkCancellation.Check();
                        if (ordinal == -1)
                            pending.Enqueue((caller, transfer.ReceiverOrigin, depth + 1));
                        foreach (var argument in transfer.Arguments.Where(argument => argument.ParameterOrdinal == ordinal))
                        {
                            WorkCancellation.Check();
                            pending.Enqueue((caller, argument.Producers, depth + 1));
                        }
                    }
                }
            }

            // A captured variable holds what the body declaring it assigned it, and what a lambda sharing its closure stored into its cell
            // (R4).
            foreach (var cell in from.Captured.OrderBy(cell => cell.SymbolKey, StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                var closures = Closures(current, cell);
                if (!visited.Add($"captured|{string.Join(",", closures.Order(StringComparer.Ordinal))}|{WholeProgram.ClosureSlot(cell)}"))
                    continue;
                foreach (var owner in _heap.ClosureOwners(current, cell).Select(id => _heap.Instances.GetValueOrDefault(id)).OfType<MethodInstance>())
                {
                    WorkCancellation.Check();
                    foreach (var variable in owner.Summary.Variables.Where(variable => variable.SymbolKey == cell.SymbolKey))
                    {
                        WorkCancellation.Check();
                        pending.Enqueue((owner, variable.Producers, depth + 1));
                    }
                }

                foreach (var sharing in _heap.Instances.Values.Where(instance => Closures(instance, cell).Overlaps(closures)))
                {
                    WorkCancellation.Check();
                    foreach (var store in sharing.Summary.CapturedStores.Where(store => store.Cell == cell))
                    {
                        WorkCancellation.Check();
                        pending.Enqueue((sharing, store.Producers, depth + 1));
                    }
                }
            }

            // A cell read gives back what any store put into a cell of the arrays it reads, and a key read what any member filed as a
            // key of the dictionaries or pairs it reads.
            foreach (var (holders, slot) in from.Elements.Select(arrays => (arrays, PathValue.ELEMENT))
                                               .Concat(from.Keys.Select(keys => (keys, PathValue.KEYS)))
                                               .Concat(from.Kept.Select(keepers => (keepers, PathValue.KEPT))))
            {
                WorkCancellation.Check();
                var targets = holders.SelectMany(value => _heap.Resolve(current.Id, value)).ToHashSet(StringComparer.Ordinal);
                if (!visited.Add($"{slot}|{string.Join(",", targets.Order(StringComparer.Ordinal))}"))
                    continue;
                foreach (var (storing, store) in _elementStores.Value.Where(item => item.Store.Slot == slot))
                {
                    WorkCancellation.Check();
                    if (store.Arrays.SelectMany(value => _heap.Resolve(storing.Id, value)).Any(targets.Contains))
                        pending.Enqueue((storing, store.Producers, depth + 1));
                }
            }

            // A field read gives back what any store put into that field of the objects it reads.
            foreach (var field in from.Fields)
            {
                WorkCancellation.Check();
                var slot = FieldSlot.Key(field.Field);
                var targets = Targets(current, field.Field, field.Bases);
                if (!visited.Add($"field|{slot}|{string.Join(",", targets.Order(StringComparer.Ordinal))}"))
                    continue;
                foreach (var (storing, store) in _stores.Value[slot])
                {
                    WorkCancellation.Check();
                    if (Targets(storing, store.Field, store.Bases).Overlaps(targets))
                        pending.Enqueue((storing, store.Producers, depth + 1));
                }

                // A field of a struct's copy or storage also holds what each copy into it carried from the same field of its sources (R2).
                foreach (var (copying, copy) in _copies.Value)
                {
                    WorkCancellation.Check();
                    if (copy.Targets.SelectMany(value => _heap.Resolve(copying.Id, value)).Any(targets.Contains))
                        pending.Enqueue((copying, ValueOrigin.None with { Fields = [new FieldOrigin(field.Field, copy.Sources)] }, depth + 1));
                }
            }
        }

    }

    private HashSet<string> Targets(MethodInstance instance, IrFieldRef field, IReadOnlySet<AbstractValue> bases) =>
        field.IsStatic
            ? new HashSet<string>(StringComparer.Ordinal) { _heap.StaticRegionOf(instance.Id, field) }
            : bases.SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Marks each pair with the checks a semantic gap decides (R6). A side's own checks travel on its access: the identity of a lock it
/// holds came from a gap's result. The overlap of two executions is decided by a gap where one of them waits for, or continues, a task
/// that may be the gap's result while the other is work spawned in the same tree, which that wait or continuation would otherwise
/// order; and where one side runs as the callback of a timer whose object may be the gap's result, so that whether the timer is
/// periodic is not proven; and where one side runs in the unknown call of a delegate the gap was handed. A gap that only stands on the
/// call path before an access decides nothing.
/// </summary>
public static class GapDecisions
{
    private const string JOIN_REASON = "a task the other side's execution waits for may be its result, so the wait orders nothing";
    private const string CONTINUATION_REASON = "the task a continuation runs after may be its result, so the continuation is ordered after nothing";
    private const string TIMER_REASON = "the timer whose callback runs a side may be its result, so whether the timer is periodic is not proven";
    private const string DELEGATE_REASON = "a side runs in the unknown call of a delegate it was handed, which nothing orders";
    private const string PASS_JOIN_REASON = "the current pass's completion handle may be its result, so completion before the next pass is not proven";

    public static IReadOnlyList<AccessPair> Mark(IReadOnlyList<AccessPair> pairs, InterproceduralInput input, IReadOnlyList<SemanticGap> gaps)
    {
        if (gaps.Count == 0)
            return pairs;

        var facts = new ExecutionFacts(input, new GapOrigins(input.Heap, gaps));
        return pairs.Select(pair =>
                    {
                        var calls = pair.First.DecidingCalls.Concat(pair.Second.DecidingCalls)
                                        .Concat(facts.Overlap(pair.First, pair.Second)).Distinct().ToArray();
                        var gapChecks = pair.First.GapChecks.Concat(pair.Second.GapChecks)
                                         .Concat(calls.Select(call => call.Check))
                                         .Distinct()
                                         .ToArray();
                        return gapChecks.Length == 0 ? pair : pair with { GapChecks = gapChecks, DecidingCalls = calls };
                    })
                    .ToArray();
    }

    /// <summary>A join of an execution's instance whose handle may be a gap's result.</summary>
    private sealed record GapJoin(string Instance, int OperationId, GapOrigin Gap);

    private sealed record Facts(IReadOnlyList<GapJoin> Joins, IReadOnlyList<GapOrigin> Continuations, IReadOnlyList<GapOrigin> Timers,
                                IReadOnlyList<GapOrigin> Delegates);

    /// <summary>What each execution waits for, continues and runs as a callback, as far as a gap's result decides it, and the gaps whose
    /// delegate it runs.</summary>
    /// <param name="input">The analysis input: the heap, the executions and their order.</param>
    /// <param name="origins">The semantic gaps whose result each value may be.</param>
    private sealed class ExecutionFacts(InterproceduralInput input, GapOrigins origins)
    {
        private readonly Dictionary<string, Facts> _facts = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, DelegateHandoff> _handoffs =
            input.Heap.DelegateHandoffs.ToDictionary(handoff => handoff.RegionId, StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string[]> _instances =
            input.Executions.InstanceExecutions.SelectMany(pair => pair.Value.Select(execution => (Execution: execution, Instance: pair.Key)))
                 .GroupBy(item => item.Execution, StringComparer.Ordinal)
                 .ToDictionary(group => group.Key, group => group.Select(item => item.Instance).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        private readonly HashSet<OperationSite> _unprovenJoins = input.Executions.UnprovenJoins.ToHashSet();

        internal IEnumerable<DecidingCall> Overlap(Access firstAccess, Access secondAccess)
        {
            var (first, second) = (firstAccess.ExecutionId, secondAccess.ExecutionId);
            // A self-pair of work started again and again: a wait of its parent on a gap's result orders its passes if the result is
            // the current pass's own handle (R9).
            if (first == second && input.Executions.Execution(first).ParentId is { } parent)
            {
                foreach (var join in Of(parent).Joins.Where(join => input.Executions.PassJoinWouldOrder(join.Instance, join.OperationId,
                                                                                                         firstAccess, secondAccess)))
                {
                    WorkCancellation.Check();
                    yield return Check(join.Gap, PASS_JOIN_REASON);
                }
            }
            if (first != second && Related(first, second))
            {
                foreach (var (waitingAccess, spawned) in new[] { (firstAccess, second), (secondAccess, first) })
                {
                    WorkCancellation.Check();
                    var waiting = waitingAccess.ExecutionId;
                    // Only a join before the waiting side's access on every path could order it; one it may run ahead of decides nothing.
                    if (input.Executions.Execution(spawned).Kind is ExecutionKind.Spawn or ExecutionKind.TimerCallback)
                    {
                        foreach (var join in Of(waiting).Joins.Where(join => input.Executions.JoinDominates(join.Instance, join.OperationId, waitingAccess)))
                        {
                            WorkCancellation.Check();
                            yield return Check(join.Gap, JOIN_REASON);
                        }

                        // A continuation is ordered after the work its antecedent is, never after the rest of the execution that started
                        // it: only against such work does the antecedent's identity decide the pair.
                        if (spawned != input.Executions.Execution(waiting).ParentId)
                        {
                            foreach (var gap in Of(waiting).Continuations)
                            {
                                WorkCancellation.Check();
                                yield return Check(gap, CONTINUATION_REASON);
                            }
                        }
                    }
                }
            }

            foreach (var gap in Of(first).Timers.Concat(Of(second).Timers))
            {
                WorkCancellation.Check();
                yield return Check(gap, TIMER_REASON);
            }
            foreach (var gap in Of(first).Delegates.Concat(Of(second).Delegates))
            {
                WorkCancellation.Check();
                yield return Check(gap, DELEGATE_REASON);
            }
        }

        private static DecidingCall Check(GapOrigin gap, string reason) =>
            new(new GapCheck(gap.Callee, gap.Kind, GapComponents.OVERLAP, reason), gap.Call);

        /// <summary>Two executions of one tree: only there can a wait or a continuation of one order the other.</summary>
        private bool Related(string first, string second) =>
            input.Executions.Execution(first).TreeRootId == input.Executions.Execution(second).TreeRootId;

        private Facts Of(string execution)
        {
            WorkCancellation.Check();
            if (_facts.TryGetValue(execution, out var cached))
                return cached;

            var joins = new List<GapJoin>();
            foreach (var instance in (_instances.GetValueOrDefault(execution) ?? []).Select(id => input.Heap.Instances[id]))
            {
                WorkCancellation.Check();
                foreach (var join in instance.Summary.Joins.Where(join => _unprovenJoins.Contains(new OperationSite(instance.BodyId, join.OperationId))))
                {
                    WorkCancellation.Check();
                    joins.AddRange(join.Handles.SelectMany(handle => origins.Of(instance, handle.Producers))
                                       .Select(gap => new GapJoin(instance.Id, join.OperationId, gap)));
                }
            }

            var continuations = new List<GapOrigin>();
            var timers = new List<GapOrigin>();
            var spawned = input.Executions.Execution(execution);
            if (spawned.Origin is { IsTail: false } origin)
            {
                foreach (var instance in input.Heap.Instances.Values.Where(instance => instance.BodyId == origin.BodyId))
                {
                    WorkCancellation.Check();
                    if (spawned.Kind == ExecutionKind.Spawn)
                    {
                        continuations.AddRange(instance.Summary.Spawns.Where(spawn => spawn.OperationId == origin.OperationId && spawn.Kind == IrSpawnKind.ContinueWith &&
                                                                                      spawn.Antecedent is not null)
                                                       .SelectMany(spawn => origins.Of(instance, spawn.Antecedent!.Producers)));
                    }
                }
            }

            if (spawned.Kind == ExecutionKind.TimerCallback)
            {
                foreach (var site in input.Executions.TimerCallbackSites.GetValueOrDefault(execution) ?? [])
                {
                    WorkCancellation.Check();
                    var subscriber = input.Heap.Instances[site.CallerInstance];
                    timers.AddRange(subscriber.Summary.Timers.Where(timer => timer.OperationId == site.OperationId)
                                              .SelectMany(timer => origins.Of(subscriber, timer.Timer.Producers)));
                }
            }

            // The unknown call of a delegate runs because the calls that were handed it may run it: those gaps decide its overlap (R3, R6).
            var delegates = spawned.Kind == ExecutionKind.UnknownDelegateCall && _handoffs.TryGetValue(spawned.Subject!, out var handoff)
                ? handoff.Sites.Select(site => origins.AtSite(input.Heap.Instances[site.CallerInstance].BodyId, site.OperationId)).OfType<GapOrigin>()
                : [];
            var facts = new Facts(joins.Distinct().ToArray(), continuations.Distinct().ToArray(), timers.Distinct().ToArray(), delegates.Distinct().ToArray());
            _facts.Add(execution, facts);
            return facts;
        }
    }
}
