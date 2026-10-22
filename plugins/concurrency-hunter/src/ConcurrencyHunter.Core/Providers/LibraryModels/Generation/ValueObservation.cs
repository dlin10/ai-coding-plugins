using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The one owner of whether a value of a generator run is observed: whether every object it may hold is one the heap names,
/// walking parameters back through the callers' arguments, source calls into their callees' returns, paths to their bases, and a
/// task's completion value into what completed the task.
/// <see cref="ValueProvenance"/> asks it before naming a value, and <see cref="GenerationHandoffs"/> before it excuses a call on a
/// parameter the heap binds.</summary>
internal sealed class ValueObservation
{
    private readonly Driver _driver;
    private readonly ScopeRun _run;
    private readonly HeapSolution _heap;
    private readonly IReadOnlySet<(string Instance, int Operation)> _unknownCalls;
    private readonly StageChecks _checks;

    /// <summary>The task regions whose completion the current walk is reading: a task met again on its own path adds nothing.</summary>
    private readonly HashSet<string> _walking = new(StringComparer.Ordinal);

    /// <summary>The run's writes into storage by slot, built on first use (<see cref="Writes"/>).</summary>
    private ILookup<string, StorageWrite>? _writes;

    /// <summary>Reads observation from one completed generator run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="unknownCalls">The run's calls the engine cannot follow, by instance and operation.</param>
    /// <param name="checks">The stage's checks, which cancel each walk at every value, task, write and edge it goes through; none by
    /// default.</param>
    public ValueObservation(Driver driver, ScopeRun run, IReadOnlySet<(string Instance, int Operation)> unknownCalls, StageChecks checks = default)
    {
        _driver = driver;
        _run = run;
        _heap = run.Heap!;
        _unknownCalls = unknownCalls;
        _checks = checks;
    }

    /// <summary>Whether an instance is the member's own body entered by the driver's call or enumeration.</summary>
    /// <param name="instance">The instance.</param>
    public bool IsMemberEntry(MethodInstance instance) => instance.BodyId == IrLowering.RootBodyId(_driver.Member) &&
        _heap.Edges.Any(edge => edge.CalleeInstance == instance.Id && _heap.Instances[edge.CallerInstance].BodyId is { } body &&
                               (body == DriverBody(DriverSynthesizer.CALL) || body == DriverBody(DriverSynthesizer.ENUMERATE)));

    /// <summary>The body id of one driver action.</summary>
    /// <param name="action">The action.</param>
    public static string DriverBody(string action) => $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{action}";

    /// <summary>The regions a value holds, following parameters, the receiver, paths, allocations, source calls and returned
    /// references where the solved heap names none.</summary>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The symbolic value.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    public IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value) => Resolve(instance, value, []);

    /// <summary>The regions a value a setup store writes may hold: what <see cref="Resolve(MethodInstance, AbstractValue)"/> finds, and
    /// for a parameter what the instance's setup callers pass to it. Once a body's contexts merge, a setup call still reaches the
    /// instance it reached before the merge beside the merged one, and that instance keeps no binding of the argument: the seed the
    /// call passed is still what setup put into the slot (R8).</summary>
    /// <param name="instance">The instance storing.</param>
    /// <param name="value">The stored value.</param>
    /// <param name="inSetup">Whether an execution is one of setup's.</param>
    public IReadOnlySet<string> SetupValues(MethodInstance instance, AbstractValue value, Func<string, bool> inSetup)
    {
        var values = Resolve(instance, value).ToHashSet(StringComparer.Ordinal);
        if (value is not ParameterValue parameter)
            return values;
        foreach (var edge in _heap.Edges.Where(edge => edge.CalleeInstance == instance.Id))
        {
            _checks.Check();
            if (!_heap.Instances.TryGetValue(edge.CallerInstance, out var caller) ||
                _run.Executions?.InstanceExecutions.GetValueOrDefault(caller.Id)?.Any(inSetup) != true)
            {
                continue;
            }
            foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments)
                                           .Where(argument => argument.ParameterOrdinal == parameter.Ordinal))
            {
                _checks.Check();
                values.UnionWith(argument.Values.SelectMany(argumentValue => Resolve(caller, argumentValue)));
            }
        }
        return values;
    }

    /// <summary>Whether any alternative, or an unknown call among their origins, may hold an object the heap does not name.</summary>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">The symbolic alternatives.</param>
    /// <param name="producers">Origins preserved through copies and merges.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    public bool HasUnobservedValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers) =>
        HasUnobservedValues(instance, values, producers, []);

    /// <summary>The bodies whose writes fill the storage a value reads — a static field, the last field or cell of a path on the objects
    /// its base reaches, a capture cell —, sorted; empty for any other value, or for storage nothing writes.</summary>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The value.</param>
    public IReadOnlyList<string> Fillers(MethodInstance instance, AbstractValue value)
    {
        var (holders, slot) = value switch
        {
            StaticFieldValue field => (new HashSet<string>(StringComparer.Ordinal) { _heap.StaticRegionOf(instance.Id, field.Field) }, FieldSlot.Key(field.Field)),
            PathValue { Segments: [.., var last] } path when last != PathValue.WILDCARD =>
                ((IReadOnlySet<string>)Resolve(instance, path with { Segments = path.Segments.SkipLast(1).ToArray() }), last),
            CapturedValue captured => (Closures(instance, captured.Cell), WholeProgram.ClosureSlot(captured.Cell)),
            _ => ((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal), "")
        };
        return holders.Count == 0 ? [] : Writes[slot].Where(write => write.Holders.Overlaps(holders)).Select(write => write.Instance.BodyId)
                                                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Checks every alternative and the unknown calls in its origins; an origin is not an additional returned object.</summary>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">The symbolic alternatives.</param>
    /// <param name="producers">Origins preserved through copies and merges.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                      HashSet<(string Instance, AbstractValue Value)> seen) =>
        values.Any(value => HasUnobserved(instance, value, seen)) ||
        producers.Calls.Any(operation => _unknownCalls.Contains((instance.Id, operation)) && !IsDecidedCurrent(instance, operation));

    /// <summary>Checks source-call and argument alternatives without treating a nonempty points-to set as completeness.</summary>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The symbolic value.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobserved(MethodInstance instance, AbstractValue value, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        // Every descent into a caller, a callee, a task or a write enters here again.
        _checks.Check();
        if (!seen.Add((instance.Id, value)))
            return false;
        try
        {
            // A parameter is what its callers hand it: an ordinary call's argument, or the input a known call's fate hands the delegate
            // it runs.
            if (value is ParameterValue parameter)
            {
                return _heap.Edges.Where(edge => edge.CalleeInstance == instance.Id).Any(edge =>
                {
                    var caller = _heap.Instances[edge.CallerInstance];
                    return caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments)
                                 .Where(argument => argument.ParameterOrdinal == parameter.Ordinal)
                                 .Any(argument => HasUnobservedValues(caller, argument.Values, argument.Producers, seen));
                }) ||
                (_heap.FateRuns.GetValueOrDefault(instance.Id) ?? new HashSet<DelegateSite>()).Any(site => HasUnobservedFateInput(site, parameter.Ordinal, seen));
            }
            // A completion value is unobserved when the task waited for is, or when what completed any task it may be is.
            if (value is AwaitResultValue awaited)
            {
                return instance.Summary.Joins.Where(join => join.OperationId == awaited.OperationId).SelectMany(join => join.Handles)
                               .Any(handle => HasUnobservedTasks(instance, handle.Values, handle.Producers, seen));
            }
            if (value is CallResultValue call)
            {
                // Unwrap() gives what its outer task completes with: the task the outer task's work returned.
                if (instance.Summary.Unwraps.FirstOrDefault(unwrap => unwrap.CallOperationId == call.OperationId) is { } unwrapped)
                    return HasUnobservedTasks(instance, unwrapped.Outer.Values, unwrapped.Outer.Producers, seen);
                var opaque = instance.Summary.OpaqueCalls.Where(opaque => opaque.OperationId == call.OperationId).ToArray();
                // A decided `foreach` puts the alias of the storage it enumerates beside its Current placeholder, which is the
                // call's handle and not a value of its own, even where the call itself is one the engine cannot follow (ADR 0010,
                // TD-043); the alias is judged as itself.
                if (IsDecidedCurrent(instance, call.OperationId))
                    return false;
                if (_unknownCalls.Contains((instance.Id, call.OperationId)))
                    return true;
                // A dispatch whose receiver may be an object the heap does not name may run a body the heap does not have, beside the
                // callees it resolved (HeapSolution.UnresolvedCallTargets); a receiver the closed world binds is followed to its origin.
                if (_heap.UnresolvedCallTargets.Contains((instance.Id, call.OperationId)) &&
                    instance.Summary.Calls.Where(transfer => transfer.OperationId == call.OperationId)
                            .Any(transfer => HasUnobservedValues(instance, transfer.Receivers, ValueOrigin.None, seen)))
                {
                    return true;
                }
                if (opaque.Any(opaque => GenerationHandoffs.IsTransparentIntrinsic(opaque.Callee)))
                    return opaque.SelectMany(opaque => opaque.Arguments).Any(argument =>
                        argument.Values.Any(argumentValue => HasUnobserved(instance, argumentValue, seen)));
                var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId)
                                   .Select(edge => _heap.Instances[edge.CalleeInstance]).ToArray();
                // A known call's result is what its model's result form names: what its own delegates give back reaches it only through a
                // returns: of the form, each as the engine answers it; a body the call reaches otherwise is walked as a callee.
                if (opaque.FirstOrDefault(opaque => opaque.Library is { Result: not null }) is { Library.Result: { } form } known)
                {
                    if (form.TaskDepth == 0 && HasUnobservedModelResult(instance, known, form.Leaf, seen))
                        return true;
                    var own = known.Library.Fates
                                   .SelectMany(fate => DelegateReturned(instance, known, fate.ParameterOrdinal)?.Runs ?? [])
                                   .Select(run => run.Callee).ToHashSet(StringComparer.Ordinal);
                    callees = callees.Where(callee => !own.Contains(callee.Id)).ToArray();
                }
                if (callees.Length != 0)
                    return callees.Any(callee => callee.Summary.Returns.Any(returned =>
                        HasUnobservedValues(callee, returned.Values, returned.Producers, seen)));
                // Recognizers and collection models put the result's alias beside this call placeholder in the summary values.
                if (opaque.Length != 0 && opaque.All(opaque => opaque.IsRecognized || opaque.Collection is not null || opaque.Implementations.Count != 0))
                    return false;
            }
            if (value is RefResultValue reference)
            {
                var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == reference.OperationId)
                                   .Select(edge => _heap.Instances[edge.CalleeInstance]).ToArray();
                if (callees.Length != 0)
                    return callees.Any(callee => callee.Summary.RefParameters.Where(parameter => parameter.Ordinal == reference.Ordinal)
                                                     .SelectMany(parameter => parameter.Values).Any(returned => HasUnobserved(callee, returned, seen)));
            }
            // A struct held by value is no object of its own: it holds what it was copied from, which may be what a reference nothing
            // proves reads (R2, question 160).
            if (value is StructCopyValue copy)
            {
                return instance.Summary.StructCopies.Where(transfer => transfer.Targets.Contains(copy))
                               .Any(transfer => HasUnobservedValues(instance, transfer.Sources, ValueOrigin.None, seen));
            }
            if (value is PathValue path && (HasUnobserved(instance, path.Base, seen) || HasUnobservedWrite(instance, path, seen)))
                return true;
            if (value is CapturedValue captured && HasUnobservedWriteInto(Closures(instance, captured.Cell), WholeProgram.ClosureSlot(captured.Cell), seen))
                return true;
            if (value is StaticFieldValue field &&
                HasUnobservedWriteInto(new HashSet<string>(StringComparer.Ordinal) { _heap.StaticRegionOf(instance.Id, field.Field) },
                                       FieldSlot.Key(field.Field), seen))
            {
                return true;
            }
            // What a known call keeps or stores is what its model names there.
            if (value is LibraryKeepingValue keeping)
                return HasUnobservedLibraryWrite(instance, keeping.OperationId, library => library.Keeps.GetValueOrDefault(keeping.KeeperOrdinal), seen);
            if (value is LibraryStoredValue stored)
                return HasUnobservedLibraryWrite(instance, stored.OperationId, library => library.Stores.GetValueOrDefault(stored.TargetOrdinal), seen);
            return Resolve(instance, value).Count == 0 &&
                !(IsMemberEntry(instance) && (value is ConcurrencyHunter.Heap.ThisValue || value is PathValue { Base: ParameterValue }));
        }
        finally
        {
            seen.Remove((instance.Id, value));
        }
    }

    /// <summary>Whether something written into a slot a storage read reads may be an object the heap does not name: the heap keeps only
    /// the objects it names, so a slot written <c>flag ? x : opaque</c> reads as <c>x</c> alone (question 137). Every slot along the
    /// path is judged, on the objects the read reaches there; a wildcard reads every slot of every object its base reaches.</summary>
    /// <param name="instance">The instance expressing the read.</param>
    /// <param name="path">The storage read.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedWrite(MethodInstance instance, PathValue path, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        var holders = Resolve(instance, path.Base);
        foreach (var segment in path.Segments)
        {
            _checks.Check();
            if (holders.Count == 0)
                return false;
            if (segment == PathValue.WILDCARD)
                return HasUnobservedWriteInto(Reached(holders), null, seen);
            if (HasUnobservedWriteInto(holders, segment, seen))
                return true;
            holders = holders.SelectMany(region => _heap.PointsTo(region, segment)).ToHashSet(StringComparer.Ordinal);
        }
        return false;
    }

    /// <summary>Whether a write into a slot of some objects may write an object the heap does not name.</summary>
    /// <param name="holders">The objects whose slot is read.</param>
    /// <param name="slot">The slot, or null for every slot.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedWriteInto(IReadOnlySet<string> holders, string? slot, HashSet<(string Instance, AbstractValue Value)> seen) =>
        _checks.Each(slot is null ? Writes.SelectMany(writes => writes) : Writes[slot]).Any(write =>
            write.Holders.Overlaps(holders) && HasUnobservedValues(write.Instance, write.Values, write.Producers, seen));

    /// <summary>Whether what a known call keeps or stores may be an object the heap does not name: what its model names there, observed as
    /// the call's model values are.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="operation">The call's operation.</param>
    /// <param name="values">What the model names there, given the call's model.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedLibraryWrite(MethodInstance instance, int operation, Func<IrLibraryCall, IReadOnlyList<IrModelValue>?> values,
                                           HashSet<(string Instance, AbstractValue Value)> seen) =>
        instance.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == operation && call.Library is not null) is not { } call ||
        values(call.Library!) is not { } named || named.Any(value => HasUnobservedModelValue(instance, call, value, seen));

    /// <summary>Every write into storage the run makes, by the slot it writes: a field store, static or not, and an element or collection
    /// store, what a known call keeps or stores among them, each also through a reference; and a captured variable's capture cell, by its
    /// slot of the closures holding it. A store an interface call makes out of other storage writes what that storage holds.</summary>
    private ILookup<string, StorageWrite> Writes => _writes ??= StorageWrites().ToLookup(write => write.Slot, write => write.Write, StringComparer.Ordinal);

    private IEnumerable<(string Slot, StorageWrite Write)> StorageWrites()
    {
        foreach (var instance in _checks.Each(_heap.Instances.Values))
        {
            var summary = instance.Summary;
            foreach (var store in _checks.Each(summary.Stores.Concat(summary.ReferenceStores)))
            {
                var holders = store.Field.IsStatic
                    ? new HashSet<string>(StringComparer.Ordinal) { _heap.StaticRegionOf(instance.Id, store.Field) }
                    : Regions(instance, store.Bases);
                yield return (FieldSlot.Key(store.Field), new StorageWrite(instance, holders, store.Values, store.Producers));
            }

            foreach (var element in _checks.Each(summary.Elements.Where(element => element.Kind == ElementOperationKind.Store).Concat(summary.ReferenceElementStores)))
            {
                var values = element.Values.Concat(element.FromSlot is { } from ? element.Arrays.Select(array => new PathValue(array, [from])) : [])
                                    .Concat(element is { ViewOf: { } viewed, CopiedFrom: { } sources } ? sources.Select(source => new PathValue(source, [viewed])) : [])
                                    .ToHashSet();
                yield return (element.Slot, new StorageWrite(instance, Regions(instance, element.Arrays), values, element.Producers));
            }

            // A captured variable's cell is a slot of the closure of each run of the body declaring it; every version of the variable,
            // and what a nested body assigns to it, goes there (R4).
            foreach (var variable in _checks.Each(summary.Variables.Where(variable => variable.Cell is not null)))
            {
                yield return (WholeProgram.ClosureSlot(variable.Cell!), new StorageWrite(instance, Closures(instance, variable.Cell!), variable.Values, variable.Producers));
            }
            foreach (var store in _checks.Each(summary.CapturedStores))
            {
                yield return (WholeProgram.ClosureSlot(store.Cell), new StorageWrite(instance, Closures(instance, store.Cell), store.Values, store.Producers));
            }
        }
    }

    /// <summary>The closures holding a capture cell an instance reads and writes: those each run of the body declaring it creates (R4).</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="cell">The capture cell.</param>
    private HashSet<string> Closures(MethodInstance instance, IrClosureCell cell) =>
        _heap.ClosuresOf(instance, cell).ToHashSet(StringComparer.Ordinal);

    private IReadOnlySet<string> Regions(MethodInstance instance, IEnumerable<AbstractValue> values) =>
        values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The objects some regions reach through any slot, the regions among them.</summary>
    /// <param name="regions">The regions to start from.</param>
    private HashSet<string> Reached(IReadOnlySet<string> regions)
    {
        var reached = regions.ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(reached);
        while (pending.TryDequeue(out var region))
        {
            _checks.Check();
            foreach (var next in _checks.Each(_heap.FieldsOf(region).SelectMany(slot => _heap.PointsTo(region, slot))))
            {
                if (reached.Add(next))
                    pending.Enqueue(next);
            }
        }
        return reached;
    }

    /// <summary>A write into a slot of some objects.</summary>
    /// <param name="Instance">The instance making the write.</param>
    /// <param name="Holders">The objects whose slot it writes; for a captured variable, the closures holding its cell.</param>
    /// <param name="Values">What it writes, as the instance's values.</param>
    /// <param name="Producers">The origins of what it writes, preserved through copies and merges.</param>
    private sealed record StorageWrite(MethodInstance Instance, IReadOnlySet<string> Holders, IReadOnlySet<AbstractValue> Values, ValueOrigin Producers);

    /// <summary>Whether the tasks some values name may be, or may complete with, an object the heap does not name: the values themselves,
    /// or what completed any task region they resolve to.</summary>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">The values naming the tasks.</param>
    /// <param name="producers">Origins preserved through copies and merges.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedTasks(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                    HashSet<(string Instance, AbstractValue Value)> seen)
    {
        var alternatives = values.ToArray();
        return HasUnobservedValues(instance, alternatives, producers, seen) ||
               alternatives.SelectMany(value => Resolve(instance, value)).Distinct(StringComparer.Ordinal)
                           .Any(task => HasUnobservedCompletion(task, seen));
    }

    /// <summary>Whether what completed a task region may be an object the heap does not name, walking what completed it as a call result
    /// walks its callee's returns (<see cref="HeapSolution.TaskCompleters"/>). A task region no producer the heap names completed, or
    /// one completed by code the analysis does not see in full, is unobserved.</summary>
    /// <param name="task">The task region.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedCompletion(string task, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        _checks.Check();
        if (!_walking.Add(task))
            return false;
        try
        {
            return _heap.TaskCompleters.GetValueOrDefault(task) is not { Count: > 0 } completers ||
                   completers.Any(completer => HasUnobservedCompleter(completer, seen));
        }
        finally
        {
            _walking.Remove(task);
        }
    }

    /// <summary>Whether one producer of a task's completion value may give an object the heap does not name.</summary>
    /// <param name="completer">The producer.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedCompleter(TaskCompleter completer, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        if (!_heap.Instances.TryGetValue(completer.Instance, out var instance))
            return true;
        var summary = instance.Summary;
        switch (completer.Kind)
        {
            case TaskCompleterKind.Returns:
                return summary.Returns.Any(returned => HasUnobservedValues(instance, returned.Values, returned.Producers, seen));
            case TaskCompleterKind.ReturnedTasks:
                return summary.Returns.Any(returned => HasUnobservedTasks(instance, returned.Values, returned.Producers, seen));
            case TaskCompleterKind.Spawn:
                return summary.Spawns.Where(spawn => spawn.OperationId == completer.Operation).SelectMany(spawn => spawn.Work)
                              .Any(work => HasUnobservedValues(instance, work.Values, work.Producers, seen));
            case TaskCompleterKind.TaskOperation:
                return summary.TaskOperations.FirstOrDefault(operation => operation.OperationId == completer.Operation) is not { } operation ||
                       operation.Kind switch
                       {
                           IrTaskKind.Completed => operation.Values.Any(item => HasUnobservedValues(instance, item.Values, item.Producers, seen)),
                           // WhenAny's task completes with one of the tasks it was given, the task itself: those it lists, or those the
                           // element storage of the collection it was given holds.
                           IrTaskKind.AnyOf => operation.Values.Any(item => HasUnobservedValues(
                               instance, operation.ValuesKnown ? item.Values : Elements(item.Values), item.Producers, seen)),
                           IrTaskKind.CompletionOf => operation.Values.Any(item => HasUnobservedTasks(instance, item.Values, item.Producers, seen)),
                           _ => true
                       };
            case TaskCompleterKind.WhenAll:
                // The array is an object the heap makes; its elements are what every member task completes with: each it lists, or each
                // the element storage of the collection it was given holds.
                return summary.WhenAlls.FirstOrDefault(whenAll => whenAll.OperationId == completer.Operation) is not { } whenAll ||
                       (whenAll.TasksKnown
                           ? whenAll.Tasks.Any(task => HasUnobservedTasks(instance, task.Values, task.Producers, seen))
                           : whenAll.Source is not { } source || HasUnobservedTasks(instance, Elements(source.Values), source.Producers, seen));
            case TaskCompleterKind.ModelResult:
                return summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == completer.Operation) is not { Library: { } library } call ||
                       library.Result is { } result && HasUnobservedModelResult(instance, call, result.Leaf, seen);
            case TaskCompleterKind.Task:
                return false;
            default:
                return true;
        }
    }

    /// <summary>What the element storage of the objects some values name holds, as values: what a collection given to <c>WhenAll</c> or
    /// <c>WhenAny</c> holds.</summary>
    /// <param name="values">The values naming the collections.</param>
    private static AbstractValue[] Elements(IEnumerable<AbstractValue> values) =>
        values.Select(value => (AbstractValue)new PathValue(value, [PathValue.ELEMENT])).ToArray();

    /// <summary>Whether a value a known call's model names may be an object the heap does not name: an argument or the receiver as
    /// itself, a <c>completion(…)</c> as the tasks its source names (<see cref="HasUnobservedModelTasks"/>), what one run of the delegate a
    /// <c>returns:</c> names gives back as the engine answers it (<see cref="HasUnobservedReturns"/>), what a keeper keeps as the keeper's
    /// storage, the elements of a collection as the collection and what was written into its element storage, a new object or sequence
    /// as what it is built of.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="value">The model value.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedModelValue(MethodInstance instance, SummaryOpaqueCall call, IrModelValue value,
                                         HashSet<(string Instance, AbstractValue Value)> seen)
    {
        _checks.Check();
        return value switch
        {
            IrModelNew => false,
            IrModelThis => HasUnobservedValues(instance, call.Receivers, ValueOrigin.None, seen),
            IrModelArgument argument => call.Arguments.Where(item => item.ParameterOrdinal == argument.ParameterOrdinal)
                                            .Any(item => HasUnobservedValues(instance, item.Values, item.Producers, seen)),
            IrModelCompletion completion => HasUnobservedModelTasks(instance, call, completion.Source, seen),
            IrModelReturns returned => HasUnobservedReturns(instance, call, returned, seen),
            IrModelKept kept => HasUnobserved(instance, Kept(call, kept), seen),
            IrModelElements elements => HasUnobservedModelValue(instance, call, elements.Source, seen) ||
                                        HasUnobservedWriteInto(ModelRegions(instance, call, elements.Source), PathValue.ELEMENT, seen),
            IrModelSequence sequence => sequence.Values.Any(item => HasUnobservedModelValue(instance, call, item, seen)),
            IrModelGrouping grouping => HasUnobservedModelValue(instance, call, grouping.Key, seen) ||
                                        HasUnobservedModelValue(instance, call, grouping.Values, seen),
            _ => true
        };
    }

    /// <summary>Whether the tasks a model value names may be, or may complete with, an object the heap does not name: the value itself as
    /// <see cref="HasUnobservedModelValue"/> walks it, and what completed every task region it names, at any depth of <c>completion(…)</c>
    /// — as the same tasks handed over directly are walked (<see cref="HasUnobservedTasks"/>).</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="source">The model value naming the tasks.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedModelTasks(MethodInstance instance, SummaryOpaqueCall call, IrModelValue source,
                                         HashSet<(string Instance, AbstractValue Value)> seen) =>
        HasUnobservedModelValue(instance, call, source, seen) || ModelRegions(instance, call, source).Any(task => HasUnobservedCompletion(task, seen));

    /// <summary>The regions a model value names: an argument's or the receiver's, what the tasks a <c>completion(…)</c> names complete
    /// with, what one run of the delegate a <c>returns:</c> names gives back as the engine answers it — an async run's task region —,
    /// what a keeper keeps, what a collection's element storage holds.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="value">The model value.</param>
    private IReadOnlySet<string> ModelRegions(MethodInstance instance, SummaryOpaqueCall call, IrModelValue value)
    {
        _checks.Check();
        IEnumerable<string> regions = value switch
        {
            IrModelThis => call.Receivers.SelectMany(item => Resolve(instance, item)),
            IrModelArgument argument => call.Arguments.Where(item => item.ParameterOrdinal == argument.ParameterOrdinal)
                                            .SelectMany(item => item.Values).SelectMany(item => Resolve(instance, item)),
            IrModelCompletion completion => ModelRegions(instance, call, completion.Source).SelectMany(_heap.Completion),
            IrModelReturns returned => DelegateReturned(instance, call, returned.ParameterOrdinal)?.Regions ?? Enumerable.Empty<string>(),
            IrModelKept kept => Resolve(instance, Kept(call, kept)),
            IrModelElements elements => ModelRegions(instance, call, elements.Source).SelectMany(region => _heap.PointsTo(region, PathValue.ELEMENT)),
            _ => []
        };
        return regions.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What one run of a delegate parameter of a known call gives back, as the engine answers it
    /// (<see cref="HeapSolution.DelegateReturns"/>); null where the engine gave no answer.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="ordinal">The delegate parameter's ordinal.</param>
    private DelegateReturnRuns? DelegateReturned(MethodInstance instance, SummaryOpaqueCall call, int ordinal) =>
        _heap.DelegateReturns.GetValueOrDefault(new DelegateSite(instance.Id, call.OperationId, ordinal));

    /// <summary>Whether what a known call's result form gives, its leaf under any depth of <c>task(…)</c>, may be an object the heap
    /// does not name: any value a <c>oneOf</c> leaf names; of an object the call creates — a new object, collection, dictionary or
    /// sequence, which the heap names — the values it is built of that name what a delegate gives back, which its synchronous form
    /// answers the same way.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="leaf">The result form's leaf.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedModelResult(MethodInstance instance, SummaryOpaqueCall call, IrLibraryResult leaf,
                                          HashSet<(string Instance, AbstractValue Value)> seen) =>
        leaf.Values.Where(value => leaf.Kind == IrResultKind.OneOf || NamesReturns(value))
                   .Any(value => HasUnobservedModelValue(instance, call, value, seen));

    /// <summary>Whether a model value names what a delegate gives back, at any depth.</summary>
    /// <param name="value">The model value.</param>
    private static bool NamesReturns(IrModelValue value)
    {
        WorkCancellation.Check();
        return value switch
        {
            IrModelReturns => true,
            IrModelCompletion completion => NamesReturns(completion.Source),
            IrModelElements elements => NamesReturns(elements.Source),
            IrModelSequence sequence => sequence.Values.Any(NamesReturns),
            IrModelGrouping grouping => NamesReturns(grouping.Key) || NamesReturns(grouping.Values),
            _ => false
        };
    }

    /// <summary>Whether what one run of the delegate a <c>returns:</c> names gives back may be an object the heap does not name: where
    /// the engine gave no answer, where some alternative of the delegate runs code the analysis does not see in full, or where a run's
    /// returns may be one. An async run gives back its own task region, which the heap names; what completes it is that task's
    /// completion, walked where the task is consumed.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The known call.</param>
    /// <param name="returned">The model's <c>returns:</c> value.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedReturns(MethodInstance instance, SummaryOpaqueCall call, IrModelReturns returned,
                                      HashSet<(string Instance, AbstractValue Value)> seen) =>
        DelegateReturned(instance, call, returned.ParameterOrdinal) is not { Unseen: false } returns ||
        returns.Runs.Where(run => run.Task is null).Any(run =>
            !_heap.Instances.TryGetValue(run.Callee, out var callee) ||
            callee.Summary.Returns.Any(value => HasUnobservedValues(callee, value.Values, value.Producers, seen)));

    /// <summary>Whether what a known call's fate hands a delegate parameter may be an object the heap does not name: the fate's input
    /// for that parameter, observed as the call's model values are; unobserved where the model names no input for it.</summary>
    /// <param name="site">The known call's delegate parameter whose fate ran the delegate.</param>
    /// <param name="ordinal">The delegate's parameter.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedFateInput(DelegateSite site, int ordinal, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        if (!_heap.Instances.TryGetValue(site.Instance, out var caller) ||
            caller.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == site.Operation && call.Library is not null) is not { } call ||
            call.Library!.Fates.FirstOrDefault(fate => fate.ParameterOrdinal == site.Ordinal) is not { } fate ||
            ordinal >= fate.Inputs.Count)
        {
            return true;
        }
        return fate.Inputs[ordinal].Any(value => HasUnobservedModelValue(caller, call, value, seen));
    }

    /// <summary>What a keeper of a known call keeps, as a value: the storage <c>kept:…</c> reads.</summary>
    /// <param name="call">The known call.</param>
    /// <param name="kept">The model's <c>kept:…</c> value.</param>
    private static PathValue Kept(SummaryOpaqueCall call, IrModelKept kept) =>
        new(new LibraryKeeperValue(call.OperationId, kept.KeeperOrdinal, Read: true), [PathValue.KEPT]);

    /// <summary>Whether the call is a <c>get_Current</c> of an enumeration whose <c>GetEnumerator</c> in the same body has a
    /// receiver, the enumeration the engine's summary decides and so names by an alias beside the call's placeholder.</summary>
    /// <param name="instance">The instance whose body makes the call.</param>
    /// <param name="operationId">The call's operation in that body.</param>
    private bool IsDecidedCurrent(MethodInstance instance, int operationId)
    {
        if (!_run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
            return false;
        var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().ToArray();
        return calls.FirstOrDefault(call => call.Id == operationId) is { EnumerationRole: IrEnumerationRole.Current, EnumerationId: int enumeration } &&
            calls.Any(call => call is { EnumerationRole: IrEnumerationRole.GetEnumerator, ReceiverValue: not null } && call.EnumerationId == enumeration);
    }

    private IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value,
                                         HashSet<(string Instance, AbstractValue Value)> seen)
    {
        // Every descent into a base, a callee or a task's completion enters here again.
        _checks.Check();
        if (!seen.Add((instance.Id, value)))
            return new HashSet<string>(StringComparer.Ordinal);
        var solved = _heap.Resolve(instance.Id, value);
        if (solved.Count != 0)
            return solved;
        switch (value)
        {
            case ParameterValue parameter:
                return instance.Parameters.GetValueOrDefault(parameter.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            case ConcurrencyHunter.Heap.ThisValue:
                return instance.Receivers;
            case PathValue path:
            {
                IReadOnlySet<string> reached = Resolve(instance, path.Base, seen);
                foreach (var segment in path.Segments)
                {
                    _checks.Check();
                    reached = reached.SelectMany(region => _heap.PointsTo(region, segment)).ToHashSet(StringComparer.Ordinal);
                }
                return reached;
            }
            case AllocationValue allocation:
                return _heap.Regions.Values.Where(region => region.SiteBodyId == allocation.Site.BodyId &&
                                                            region.SiteOperationId == allocation.Site.OperationId)
                            .Select(region => region.Identity).ToHashSet(StringComparer.Ordinal);
            case CallResultValue call:
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var intrinsic in instance.Summary.OpaqueCalls.Where(opaque => opaque.OperationId == call.OperationId &&
                    GenerationHandoffs.IsTransparentIntrinsic(opaque.Callee)))
                {
                    _checks.Check();
                    result.UnionWith(intrinsic.Arguments.SelectMany(argument => argument.Values)
                                             .SelectMany(argument => Resolve(instance, argument, seen)));
                }
                foreach (var edge in _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId))
                {
                    _checks.Check();
                    // An async body gives its call the task the heap makes for it; what it returns is that task's completion value.
                    if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee) ||
                        _run.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false })
                    {
                        continue;
                    }
                    result.UnionWith(callee.Summary.Returns.SelectMany(returned => returned.Values)
                                                 .SelectMany(returned => Resolve(callee, returned, seen)));
                }
                return result;
            }
            case AwaitResultValue awaited:
                // What the tasks waited for complete with.
                return instance.Summary.Joins.Where(join => join.OperationId == awaited.OperationId)
                               .SelectMany(join => join.Handles).SelectMany(handle => handle.Values)
                               .SelectMany(handle => Resolve(instance, handle, seen)).SelectMany(_heap.Completion)
                               .ToHashSet(StringComparer.Ordinal);
            case RefResultValue reference:
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == reference.OperationId))
                {
                    _checks.Check();
                    if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                        continue;
                    result.UnionWith(callee.Summary.RefParameters.Where(parameter => parameter.Ordinal == reference.Ordinal)
                                                 .SelectMany(parameter => parameter.Values)
                                                 .SelectMany(returned => Resolve(callee, returned, seen)));
                }
                return result;
            }
            default:
                return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
