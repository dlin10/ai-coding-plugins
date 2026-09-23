using System.Globalization;
using ConcurrencyHunter.Analysis;
using System.Runtime.CompilerServices;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Heap;

/// <summary>
/// Summarizes one body from its SSA IR, symbolically in its receiver, parameters, captured variables, statics, allocations and
/// call results. Values and dependencies are solved to a fixpoint over the body's definitions, so phis in loops converge; a
/// path grows to at most <see cref="AnalysisLimits.MaxAccessPathDepth"/> segments and is a wildcard beyond.
/// </summary>
public static class MethodSummaryBuilder
{
    private static readonly HashSet<string> PRIMITIVE_TYPES = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char", "nint", "nuint"
    };

    private static readonly ConditionalWeakTable<ProgramIndex, HashSet<string>> DISPATCHED_WITH_BODY = new();

    /// <summary><paramref name="bodies"/> resolves a nested body id, so a delegate creation knows whether its target uses the
    /// receiver; without it a target body that is not at hand is assumed to use it.</summary>
    public static MethodSummary Build(IrBody body, ProgramIndex index, AnalysisLimits limits, Func<string, IrBody?>? bodies = null) =>
        new Builder(body, index, limits, bodies).Build();

    /// <summary>The method ids a virtual or interface call could run a source body for: every source-bodied method, and every method
    /// such a method overrides or implements, directly or through the methods it overrides.</summary>
    private static HashSet<string> DispatchedWithBody(ProgramIndex index) =>
        DISPATCHED_WITH_BODY.GetValue(index, program =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in program.Methods.Where(method => method.HasSourceBody))
            {
                ids.Add(method.MethodId);
                ids.UnionWith(method.ImplementedInterfaceMethodIds);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var overridden = method.OverriddenMethodId; overridden is not null && visited.Add(overridden);
                     overridden = program.Method(overridden)?.OverriddenMethodId)
                {
                    ids.Add(overridden);
                    ids.UnionWith(program.Method(overridden)?.ImplementedInterfaceMethodIds ?? []);
                }
            }

            return ids;
        });

    private sealed class Builder
    {
        /// <summary>How far the selector proves the offsets of a chain of slices before it gives up on the cell.</summary>
        private const int SELECTOR_BUDGET = 4;

        /// <summary>How far the chain of slices is unwound to find the collection it cuts, past the budget for its offsets.</summary>
        private const int SLICE_DEPTH = 32;

        private readonly IrBody _body;
        private readonly ProgramIndex _index;
        private readonly AnalysisLimits _limits;
        private readonly Func<string, IrBody?>? _bodies;
        private readonly IrOperation[] _operations;
        private readonly Dictionary<int, IrValue> _values;
        private readonly Dictionary<int, IrOperation> _definitions = [];
        private readonly Dictionary<int, int> _parameterOrdinals;
        private readonly HashSet<string> _capturedKeys;
        private readonly Dictionary<int, HashSet<AbstractValue>> _points = [];
        private readonly Dictionary<int, HashSet<ValueDependency>> _dependencies = [];
        private readonly Dictionary<int, HashSet<UnknownSource>> _unknown = [];
        private readonly Dictionary<int, HashSet<int>> _sourceCalls = [];
        private readonly Dictionary<int, HashSet<int>> _workValues;
        private readonly HashSet<int> _modeledCalls = [];
        private readonly Dictionary<int, int> _unwrapped = [];
        private Dictionary<int, int>? _blockOf;
        private Dictionary<int, HashSet<int>>? _dominators;

        internal Builder(IrBody body, ProgramIndex index, AnalysisLimits limits, Func<string, IrBody?>? bodies)
        {
            _body = body;
            _index = index;
            _limits = limits;
            _bodies = bodies;
            _operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            _workValues = ReachableSet.WorkOfCalls(_operations);
            _values = body.Values.ToDictionary(value => value.Id);
            foreach (var operation in _operations)
            {
                foreach (var defined in operation.DefinedValues)
                    _definitions[defined] = operation;
            }

            _parameterOrdinals = body.Parameters.ToDictionary(parameter => parameter.Value, parameter => parameter.Ordinal);
            _capturedKeys = _operations.OfType<IrCaptureOperation>().Select(capture => capture.SymbolKey).OfType<string>()
                                       .ToHashSet(StringComparer.Ordinal);
            foreach (var operation in _operations)
            {
                switch (operation)
                {
                    case IrSpawnOperation { HandleValue: int handle }:
                        ModelCall(handle);
                        break;
                    case IrTimerOperation { ResultValue: int result }:
                        ModelCall(result);
                        break;
                    case IrUnwrapOperation unwrap:
                        ModelCall(unwrap.ResultValue);
                        _unwrapped[unwrap.ResultValue] = unwrap.OuterValue;
                        break;
                    case IrWhenAllOperation whenAll:
                        ModelCall(whenAll.ResultValue);
                        break;
                }
            }
        }

        /// <summary>Marks the call defining <paramref name="value"/> as one whose result the heap models: a handle, a tail or a group.</summary>
        private void ModelCall(int value)
        {
            if (DefiningCall(value) is int call)
                _modeledCalls.Add(call);
        }

        private int? DefiningCall(int value) =>
            _definitions.TryGetValue(value, out var definition) && definition is IrCallOperation call && call.ResultValue == value ? call.Id : null;

        internal MethodSummary Build()
        {
            Solve();
            var delegates = FinalDelegates();
            var held = HeldLocks();

            // The atomic marks of the body, by the load or store each one makes atomic (TD-082).
            var atomic = _operations.OfType<IrAtomicOperation>()
                                    .Where(operation => operation.TargetOperationId is not null)
                                    .ToDictionary(operation => operation.TargetOperationId!.Value, operation => operation);
            var accesses = new List<SummaryAccess>();
            var referenceAccesses = new List<SummaryReferenceAccess>();
            var referenceReturns = new List<ReferenceTarget>();
            var collectionReturns = new List<ReferenceTarget>();
            var stores = new List<StoreTransfer>();
            var elements = new List<ElementTransfer>();
            var returns = new List<ReturnTransfer>();
            var delegateTransfers = new List<DelegateTransfer>();
            var calls = new List<CallTransfer>();
            var opaqueCalls = new List<SummaryOpaqueCall>();
            var capturedStores = new List<CapturedStore>();
            foreach (var operation in _operations)
            {
                var locks = held.GetValueOrDefault(operation.Id) ?? [];
                switch (operation)
                {
                    case IrLoadReferenceOperation load:
                        referenceAccesses.Add(new SummaryReferenceAccess(load.Id, SummaryAccessKind.Load,
                                                                         References(load.AddressValue), load.Provenance, locks,
                                                                         new HashSet<ValueDependency>()));
                        break;
                    case IrStoreReferenceOperation store:
                        referenceAccesses.Add(new SummaryReferenceAccess(store.Id, SummaryAccessKind.Store,
                                                                         References(store.AddressValue), store.Provenance, locks,
                                                                         Dependencies(store.Value))
                        {
                            ReadModifyWriteOf = store.ReadModifyWriteOf
                        });
                        break;
                    case IrLoadFieldOperation load:
                        accesses.Add(new SummaryAccess(load.Id, SummaryAccessKind.Load, load.Field, AccessBases(Points(load.ReceiverValue)),
                                                       load.Provenance, locks, null, Final(Points(load.ResultValue), delegates),
                                                       new HashSet<ValueDependency>())
                        {
                            Atomic = atomic.TryGetValue(load.Id, out var loadMark) ? loadMark.Effect : null
                        });
                        break;
                    case IrStoreFieldOperation store:
                        var stored = Final(Points(store.Value), delegates);
                        var bases = Final(Points(store.ReceiverValue), delegates);
                        accesses.Add(new SummaryAccess(store.Id, SummaryAccessKind.Store, store.Field, AccessBases(bases), store.Provenance, locks,
                                                       store.ReadModifyWriteOf, stored, Dependencies(store.Value))
                        {
                            Atomic = atomic.TryGetValue(store.Id, out var storeMark) ? storeMark.Effect : null,
                            ComparandLoad = Comparand(atomic, store.Id),
                            StoredTerm = Term(store.Value)
                        });
                        if (!IsValueType(store.Field.Type))
                            stores.Add(new StoreTransfer(store.Id, store.Field, bases, stored));
                        break;
                    case IrStoreElementOperation element when Cell(element.ReceiverValue, element.IndexValues, element.NamesOneCell) is { } storeCell:
                        accesses.Add(new SummaryAccess(element.Id, SummaryAccessKind.Store, storeCell.Field,
                                                       AccessBases(Final(Points(storeCell.BaseValue), delegates)), element.Provenance, locks, null,
                                                       Final(Points(element.Value), delegates), Dependencies(element.Value))
                        {
                            Atomic = atomic.TryGetValue(element.Id, out var storeElementMark) ? storeElementMark.Effect : null,
                            ComparandLoad = Comparand(atomic, element.Id),
                            Selector = storeCell.Selector,
                            SelectorParameter = element.IndexValues.Count == 1 && element.NamesOneCell ? ParameterOf(element.IndexValues[0]).Ordinal : null,
                            SelectorWidth = element.IndexValues.Count == 1 && element.NamesOneCell ? ParameterOf(element.IndexValues[0]).Width : null,
                            SelectorTerm = storeCell.Term,
                            IsOnCollection = true
                        });
                        elements.Add(new ElementTransfer(element.Id, ElementOperationKind.Store, Final(Points(element.ReceiverValue), delegates),
                                                         Final(Points(element.Value), delegates)));
                        break;
                    case IrLoadElementOperation loadElement when Cell(loadElement.ReceiverValue, loadElement.IndexValues, loadElement.NamesOneCell) is { } loadCell:
                        accesses.Add(new SummaryAccess(loadElement.Id, SummaryAccessKind.Load, loadCell.Field,
                                                       AccessBases(Final(Points(loadCell.BaseValue), delegates)), loadElement.Provenance, locks, null,
                                                       Final(Points(loadElement.ResultValue), delegates), new HashSet<ValueDependency>())
                        {
                            Atomic = atomic.TryGetValue(loadElement.Id, out var loadElementMark) ? loadElementMark.Effect : null,
                            Selector = loadCell.Selector,
                            SelectorParameter = loadElement.IndexValues.Count == 1 && loadElement.NamesOneCell ? ParameterOf(loadElement.IndexValues[0]).Ordinal : null,
                            SelectorWidth = loadElement.IndexValues.Count == 1 && loadElement.NamesOneCell ? ParameterOf(loadElement.IndexValues[0]).Width : null,
                            SelectorTerm = loadCell.Term,
                            IsOnCollection = true
                        });
                        elements.Add(new ElementTransfer(loadElement.Id, ElementOperationKind.Load, Final(Points(loadElement.ReceiverValue), delegates),
                                                         Final(Points(loadElement.ResultValue), delegates)));
                        break;
                    case IrStoreElementOperation element:
                        if (((ReferenceTarget?)ParameterElement(element.ReceiverValue, element.IndexValues, element.NamesOneCell) ??
                             CallCollection(element.ReceiverValue)) is { } storeCollection)
                            referenceAccesses.Add(new SummaryReferenceAccess(element.Id, SummaryAccessKind.Store, [storeCollection],
                                                                             element.Provenance, locks, Dependencies(element.Value))
                            {
                                IsCollectionElement = true
                            });
                        elements.Add(new ElementTransfer(element.Id, ElementOperationKind.Store, Final(Points(element.ReceiverValue), delegates),
                                                         Final(Points(element.Value), delegates)));
                        break;
                    case IrLoadElementOperation element:
                        if (((ReferenceTarget?)ParameterElement(element.ReceiverValue, element.IndexValues, element.NamesOneCell) ??
                             CallCollection(element.ReceiverValue)) is { } loadCollection)
                            referenceAccesses.Add(new SummaryReferenceAccess(element.Id, SummaryAccessKind.Load, [loadCollection],
                                                                             element.Provenance, locks, new HashSet<ValueDependency>())
                            {
                                IsCollectionElement = true
                            });
                        elements.Add(new ElementTransfer(element.Id, ElementOperationKind.Load, Final(Points(element.ReceiverValue), delegates),
                                                         Final(Points(element.ResultValue), delegates)));
                        break;
                    case IrReturnOperation { Value: int returned, IsByRef: true }:
                        referenceReturns.AddRange(References(returned));
                        break;
                    case IrReturnOperation { Value: int returned } @return:
                        // What the body hands back is only ever indexed if it is a collection; a return nothing names is kept as
                        // such, so an element operation on the result never takes the returns that do name one for all of them.
                        collectionReturns.Add(Collection(returned)?.Collection ?? ReferenceUnproven.Instance);
                        returns.Add(new ReturnTransfer(@return.Id, Final(Points(returned), delegates), Dependencies(returned))
                        {
                            UnknownSources = _unknown[returned],
                            SourceCalls = _sourceCalls[returned]
                        });
                        break;
                    case IrCreateDelegateOperation create:
                        delegateTransfers.Add(new DelegateTransfer(create.Id, delegates[Site(create)], Final(Points(create.ReceiverValue), delegates),
                                                                   create.TargetContainingTypeKey, create.TargetMethodTypeArgumentKeys));
                        break;
                    case IrCallOperation call when IsOpaque(call):
                        opaqueCalls.Add(new SummaryOpaqueCall(call.Id, call.Method,
                                                              call.ArgumentValues.Where(value => _workValues.GetValueOrDefault(call.Id)?.Contains(value) != true)
                                                                  .SelectMany(value => _points[value]).OfType<DelegateCreationValue>()
                                                                  .Distinct().Select(value => delegates[value.Site]).ToArray())
                        {
                            Receivers = Final(Points(call.ReceiverValue), delegates),
                            Arguments = Arguments(call, delegates),
                            ServiceCall = call.ServiceCall
                        });
                        break;
                    case IrCallOperation call:
                        calls.Add(new CallTransfer(call.Id, call.TargetMethodId ?? call.Method, call.CallKind, Final(Points(call.ReceiverValue), delegates),
                                                   Arguments(call, delegates), locks, call.TargetContainingTypeKey, call.TargetMethodTypeArgumentKeys)
                        {
                            IsAwaitedImmediately = call.IsAwaitedImmediately,
                            ReceiverUnknownSources = call.ReceiverValue is int receiver ? _unknown[receiver] : new HashSet<UnknownSource>(),
                            ReceiverSourceCalls = call.ReceiverValue is int source ? _sourceCalls[source] : new HashSet<int>(),
                            // An access runs where the call that reaches it runs, so the guards of the call site travel with the
                            // edge; without them two helpers called from exclusive branches look reachable together (R8, TD-090).
                            Conditions = Conditions(call.Id)
                        });
                        break;
                    case IrAssignOperation assign when _values[assign.TargetValue].SymbolKey is { } key && _capturedKeys.Contains(key):
                        capturedStores.Add(new CapturedStore(assign.Id, key, Final(Points(assign.SourceValue), delegates), Dependencies(assign.SourceValue)));
                        break;
                }
            }

            var collections = CollectionAccesses(delegates, held);
            // The load that only reaches a member is no access of its own: what the member does to the collection is.
            accesses.RemoveAll(access => collections.Receivers.Contains(access.OperationId));
            accesses.AddRange(collections.Accesses);
            // Every access runs where its conditions hold, and they are the same for every access of one operation (TD-090).
            var conditions = accesses.Select(access => access.OperationId).Distinct()
                                     .ToDictionary(operation => operation, Conditions);
            for (var index = 0; index < accesses.Count; index++)
                accesses[index] = accesses[index] with { Conditions = conditions[accesses[index].OperationId] };
            for (var index = 0; index < referenceAccesses.Count; index++)
                referenceAccesses[index] = referenceAccesses[index] with { Conditions = Conditions(referenceAccesses[index].OperationId) };

            var refParameters = _body.Parameters.Where(parameter => parameter.RefKind is IrRefKind.Ref or IrRefKind.Out)
                                     .Select(parameter =>
                                     {
                                         var atEnd = ValuesAtEnd(_body.Blocks.Count - 1, _values[parameter.Value].SymbolKey, parameter.Value).ToArray();
                                         return new RefParameterTransfer(parameter.Ordinal,
                                                                         Final(atEnd.SelectMany(value => _points[value]).ToHashSet(), delegates))
                                         {
                                             Dependencies = atEnd.SelectMany(value => _dependencies[value]).ToHashSet()
                                         };
                                     })
                                     .ToArray();
            var variables = VariableKeys().Select(key => new SummaryVariable(key, Final(VariablePoints(key), delegates), VariableDependencies(key))
                                          {
                                              UnknownSources = _body.Values.Where(value => value.SymbolKey == key).SelectMany(value => _unknown[value.Id]).ToHashSet()
                                          })
                                          .ToArray();
            var lockTransfers = _operations.Select(operation => operation switch
                                   {
                                       IrAcquireOperation acquire => new LockTransfer(acquire.Id, true, acquire.Primitive, acquire.Mode,
                                                                                      Points(acquire.LockValue), Origin(acquire.LockValue),
                                                                                      acquire.Provenance),
                                       IrReleaseOperation release => new LockTransfer(release.Id, false, release.Primitive, release.Mode,
                                                                                      Points(release.LockValue), Origin(release.LockValue),
                                                                                      release.Provenance) { Permits = release.Permits },
                                       _ => null
                                   })
                                   .OfType<LockTransfer>()
                                   .ToArray();
            return new MethodSummary(_body.BodyId, accesses, stores, elements, returns, refParameters, delegateTransfers, calls, opaqueCalls,
                                     capturedStores, variables)
            {
                ReferenceAccesses = referenceAccesses,
                ReferenceReturns = referenceReturns,
                CollectionReturns = collectionReturns,
                Locks = lockTransfers,
                Spawns = _operations.OfType<IrSpawnOperation>().Select(spawn => Spawn(spawn, delegates)).ToArray(),
                ThreadWorks = _operations.OfType<IrThreadWorkOperation>()
                                         .Select(work => new SummaryThreadWork(work.Id, Value(work.ThreadValue, delegates), Value(work.WorkValue, delegates),
                                                                               work.WorkIsAsync, work.Provenance))
                                         .ToArray(),
                Joins = _operations.Select(operation => Join(operation, delegates)).OfType<SummaryJoin>().ToArray(),
                WhenAlls = _operations.OfType<IrWhenAllOperation>()
                                      .Select(whenAll => new SummaryWhenAll(whenAll.Id, DefiningCall(whenAll.ResultValue)!.Value,
                                                                            whenAll.TaskValues.Select(task => Value(task, delegates)).ToArray(),
                                                                            whenAll.TasksKnown, whenAll.Provenance))
                                      .ToArray(),
                Unwraps = _operations.OfType<IrUnwrapOperation>()
                                     .Select(unwrap => new SummaryUnwrap(unwrap.Id, DefiningCall(unwrap.ResultValue)!.Value, Value(unwrap.OuterValue, delegates),
                                                                         unwrap.Provenance))
                                     .ToArray(),
                Timers = _operations.OfType<IrTimerOperation>().Select(timer => Timer(timer, delegates)).ToArray()
            };
        }

        private SummarySpawn Spawn(IrSpawnOperation spawn, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            new(spawn.Id, spawn.CallOperationId, spawn.Kind, Value(spawn.HandleValue, delegates),
                spawn.WorkValues.Select(work => Value(work, delegates)).ToArray(), spawn.Provenance)
            {
                State = Value(spawn.StateValue, delegates),
                Antecedent = Value(spawn.AntecedentValue, delegates),
                WorkMethod = spawn.WorkMethod,
                WorkIsAsync = spawn.WorkIsAsync,
                AwaitsWorkTask = spawn.AwaitsWorkTask,
                JoinsOnReturn = spawn.JoinsOnReturn
            };

        private SummaryJoin? Join(IrOperation operation, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) => operation switch
        {
            IrAwaitOperation awaited => new SummaryJoin(awaited.Id, SummaryJoinKind.Await, null,
                                                        [Value(awaited.TaskValue ?? awaited.AwaitableValue, delegates)], true, true, awaited.Provenance),
            IrJoinOperation join => new SummaryJoin(join.Id, join.Kind switch
                                                    {
                                                        IrJoinKind.Wait => SummaryJoinKind.Wait,
                                                        IrJoinKind.Join => SummaryJoinKind.Join,
                                                        IrJoinKind.WaitAll => SummaryJoinKind.WaitAll,
                                                        IrJoinKind.WaitOne => SummaryJoinKind.WaitOne,
                                                        _ => throw new ArgumentOutOfRangeException(nameof(operation), join.Kind, null)
                                                    },
                                                    join.CallOperationId, join.HandleValues.Select(handle => Value(handle, delegates)).ToArray(),
                                                    join.HandlesKnown, join.ThrowsOnlyAfterCompletion, join.Provenance),
            _ => null
        };

        private SummaryTimer Timer(IrTimerOperation timer, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            new(timer.Id, timer.Action, Value(timer.TimerValue, delegates), timer.Provenance)
            {
                Callback = Value(timer.CallbackValue, delegates),
                State = Value(timer.StateValue, delegates),
                DueTime = timer.DueTime,
                Period = timer.Period,
                WaitHandle = Value(timer.WaitHandleValue, delegates),
                Result = Value(timer.ResultValue, delegates),
                Flag = timer.Flag
            };

        private SummaryValue Value(int value, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            new(Final(_points[value], delegates), _unknown[value]) { SourceCalls = _sourceCalls[value] };

        private SummaryValue? Value(int? value, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            value is int id ? Value(id, delegates) : null;

        private CallArgument[] Arguments(IrCallOperation call, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            call.ArgumentValues.Select((value, position) => new CallArgument(
                    position < call.ArgumentParameterOrdinals.Count ? call.ArgumentParameterOrdinals[position] : position,
                    Final(Points(value), delegates))
                {
                    Dependencies = Dependencies(value),
                    Term = Term(value),
                    References = References(value),
                    Collection = Collection(value)
                })
                .ToArray();

        /// <summary>Solves the values and the dependencies of every IR value until neither changes.</summary>
        private void Solve()
        {
            foreach (var value in _body.Values)
            {
                _points[value.Id] = [];
                _dependencies[value.Id] = [];
                _unknown[value.Id] = [];
                _sourceCalls[value.Id] = [];
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var value in _body.Values)
                {
                    var points = EvaluatePoints(value);
                    if (!points.SetEquals(_points[value.Id]))
                    {
                        _points[value.Id] = points;
                        changed = true;
                    }

                    var dependencies = EvaluateDependencies(value);
                    if (!dependencies.SetEquals(_dependencies[value.Id]))
                    {
                        _dependencies[value.Id] = dependencies;
                        changed = true;
                    }

                    var unknown = EvaluateUnknown(value);
                    if (!unknown.SetEquals(_unknown[value.Id]))
                    {
                        _unknown[value.Id] = unknown;
                        changed = true;
                    }

                    var sourceCalls = EvaluateSourceCalls(value);
                    if (!sourceCalls.SetEquals(_sourceCalls[value.Id]))
                    {
                        _sourceCalls[value.Id] = sourceCalls;
                        changed = true;
                    }
                }
            }
        }

        /// <summary>The unknown sources a value may come from: <c>null</c> and defaults, parameters, captured variables, field loads
        /// (a field holds its default before its first write), call results other than the handles, tails and groups the heap models,
        /// and every operation the summary does not follow.</summary>
        private HashSet<UnknownSource> EvaluateUnknown(IrValue value)
        {
            if (value.Kind == IrValueKind.Receiver)
                return [];
            if (value.SymbolKey is { } key && _capturedKeys.Contains(key))
                return [UnknownSource.Captured];
            if (_parameterOrdinals.ContainsKey(value.Id))
                return [UnknownSource.Parameter];
            if (!_definitions.TryGetValue(value.Id, out var definition))
            {
                return value switch
                {
                    { Kind: IrValueKind.Constant, Name: "null" or "default" } => [UnknownSource.Null],
                    { Kind: IrValueKind.Constant, Name: not "exceptional" } => [],
                    _ => [UnknownSource.Other]
                };
            }

            return definition switch
            {
                IrAssignOperation assign => [.. _unknown[assign.SourceValue]],
                IrPhiOperation phi => phi.Inputs.SelectMany(input => _unknown[input.Value]).ToHashSet(),
                IrConvertOperation convert => [.. _unknown[convert.OperandValue]],
                IrAllocateOperation or IrCreateDelegateOperation or IrComputeOperation or IrCompareOperation => [],
                IrLoadFieldOperation => [UnknownSource.FieldBeforeWrite],
                IrCallOperation call when call.ResultValue == value.Id && _unwrapped.TryGetValue(value.Id, out var outer) => [.. _unknown[outer]],
                IrCallOperation call when call.ResultValue == value.Id && _modeledCalls.Contains(call.Id) => [],
                IrCallOperation call => [IsOpaque(call) ? UnknownSource.OpaqueCall : UnknownSource.SourceCall],
                IrAwaitOperation awaited when IsTaskType(value.Type) => [.. _unknown[awaited.TaskValue ?? awaited.AwaitableValue]],
                _ => [UnknownSource.Other]
            };
        }

        /// <summary>The calls a value comes from as <see cref="UnknownSource.SourceCall"/>, followed over the same steps as the unknown
        /// sources themselves, so that a value merging two calls keeps both.</summary>
        private HashSet<int> EvaluateSourceCalls(IrValue value)
        {
            if (value.Kind == IrValueKind.Receiver || (value.SymbolKey is { } key && _capturedKeys.Contains(key)) ||
                _parameterOrdinals.ContainsKey(value.Id) || !_definitions.TryGetValue(value.Id, out var definition))
            {
                return [];
            }

            return definition switch
            {
                IrAssignOperation assign => [.. _sourceCalls[assign.SourceValue]],
                IrPhiOperation phi => phi.Inputs.SelectMany(input => _sourceCalls[input.Value]).ToHashSet(),
                IrConvertOperation convert => [.. _sourceCalls[convert.OperandValue]],
                IrCallOperation call when call.ResultValue == value.Id && _unwrapped.TryGetValue(value.Id, out var outer) => [.. _sourceCalls[outer]],
                IrCallOperation call when call.ResultValue == value.Id && _modeledCalls.Contains(call.Id) => [],
                IrCallOperation call => IsOpaque(call) ? [] : [call.Id],
                IrAwaitOperation awaited when IsTaskType(value.Type) => [.. _sourceCalls[awaited.TaskValue ?? awaited.AwaitableValue]],
                _ => []
            };
        }

        private static bool IsTaskType(string type) =>
            type is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" ||
            type.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
            type.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal);

        private HashSet<AbstractValue> EvaluatePoints(IrValue value)
        {
            if (value.Kind == IrValueKind.Receiver)
                return [ThisValue.Instance];
            if (value.SymbolKey is { } key && _capturedKeys.Contains(key))
                return [new CapturedValue(key)];
            if (_parameterOrdinals.TryGetValue(value.Id, out var ordinal))
                return [new ParameterValue(ordinal)];
            if (!_definitions.TryGetValue(value.Id, out var definition))
                return [];

            return definition switch
            {
                IrAssignOperation assign => [.. _points[assign.SourceValue]],
                IrPhiOperation phi => phi.Inputs.SelectMany(input => _points[input.Value]).ToHashSet(),
                IrConvertOperation convert => [.. _points[convert.OperandValue]],
                IrAllocateOperation allocate => [new AllocationValue(new CreationSite(_body.BodyId, allocate.Id,
                                                                                      allocate.AllocatedTypeKey ?? allocate.AllocatedType,
                                                                                      allocate.SiteOrdinal))],
                IrLoadFieldOperation { Field.IsStatic: true } load => [new StaticFieldValue(load.Field)],
                IrLoadFieldOperation { ReceiverValue: int receiver } load => Extend(_points[receiver], FieldSlot.Key(load.Field)),
                IrLoadElementOperation load => Extend(_points[load.ReceiverValue], PathValue.ELEMENT),
                IrCallOperation call when call.ResultValue == value.Id => [new CallResultValue(call.Id)],
                IrCallOperation call => call.RefResults.Where(pair => pair.Value == value.Id)
                                            .Select(pair => (AbstractValue)new RefResultValue(call.Id, pair.Key))
                                            .ToHashSet(),
                IrCreateDelegateOperation create => [new DelegateCreationValue(Site(create), Target(create), new Dictionary<string, IReadOnlySet<AbstractValue>>())],
                IrAwaitOperation awaited when IsTaskType(value.Type) => [new AwaitResultValue(awaited.Id)],
                _ => []
            };
        }

        private HashSet<ValueDependency> EvaluateDependencies(IrValue value)
        {
            if (value.SymbolKey is { } key && _capturedKeys.Contains(key))
                return [new CapturedDependency(key)];
            if (_parameterOrdinals.TryGetValue(value.Id, out var ordinal))
                return [new ParameterDependency(ordinal)];
            if (!_definitions.TryGetValue(value.Id, out var definition))
                return [];

            return definition switch
            {
                IrAssignOperation assign => [.. _dependencies[assign.SourceValue]],
                IrPhiOperation phi => phi.Inputs.SelectMany(input => _dependencies[input.Value]).ToHashSet(),
                IrConvertOperation convert => [.. _dependencies[convert.OperandValue]],
                IrComputeOperation compute => compute.OperandValues.SelectMany(operand => _dependencies[operand]).ToHashSet(),
                IrCompareOperation compare => compare.Operands.SelectMany(operand => _dependencies[operand]).ToHashSet(),
                IrAwaitOperation awaited => [.. _dependencies[awaited.AwaitableValue]],
                IrUnknownOperation unknown => unknown.OperandValues.SelectMany(operand => _dependencies[operand]).ToHashSet(),
                IrLoadFieldOperation load => [new LoadDependency(load.Id)],
                IrLoadReferenceOperation load => [new LoadDependency(load.Id)],
                // What a collection member returns is what it read from the collection, so the member itself is the dependency:
                // that is what makes a change decided by an earlier read of the same collection one compound operation.
                IrCallOperation { Collection: not null } collection when collection.ResultValue == value.Id ||
                                                                         collection.RefResults.Any(pair => pair.Value == value.Id) =>
                    [new LoadDependency(collection.Id)],
                // Nothing is known about a call without a source body, so its results keep the receiver's and the arguments'
                // dependencies; a call the analysis can follow says what its return and each ref or out parameter depend on.
                IrCallOperation call when IsOpaque(call) => call.Operands.SelectMany(operand => _dependencies[operand]).ToHashSet(),
                IrCallOperation call when call.ResultValue == value.Id => [new CallDependency(call.Id)],
                IrCallOperation call => call.RefResults.Where(pair => pair.Value == value.Id)
                                            .Select(pair => (ValueDependency)new RefResultDependency(call.Id, pair.Key))
                                            .ToHashSet(),
                _ => []
            };
        }

        /// <summary>The bases of an access: the limit is on the access path, the base path and the accessed field together, so a base
        /// the field would grow past the limit is the wildcard of the region its path starts from (R5).</summary>
        private HashSet<AbstractValue> AccessBases(IEnumerable<AbstractValue> bases) =>
            bases.Select(value => value is PathValue { IsWildcard: false } path && path.Segments.Count >= _limits.MaxAccessPathDepth
                             ? new PathValue(path.Base, [PathValue.WILDCARD])
                             : value)
                 .ToHashSet();

        private HashSet<AbstractValue> Extend(IEnumerable<AbstractValue> bases, string segment) =>
            bases.Select(@base => Extend(@base, segment)).ToHashSet();

        private AbstractValue Extend(AbstractValue @base, string segment)
        {
            if (@base is PathValue { IsWildcard: true })
                return @base;
            var (root, segments) = @base is PathValue path ? (path.Base, path.Segments.Append(segment).ToArray()) : (@base, new[] { segment });
            return new PathValue(root, segments.Length > _limits.MaxAccessPathDepth ? [PathValue.WILDCARD] : segments);
        }

        /// <summary>Every delegate creation of the body with its captured values: each captured symbol key's variable, and
        /// <c>this</c> when the method group is bound to a receiver or the target body uses the receiver of this body.</summary>
        private Dictionary<CreationSite, DelegateCreationValue> FinalDelegates()
        {
            var receiver = _body.Values.FirstOrDefault(value => value.Kind == IrValueKind.Receiver);
            var delegates = new Dictionary<CreationSite, DelegateCreationValue>();
            foreach (var create in _operations.OfType<IrCreateDelegateOperation>())
            {
                var captured = create.CapturedSymbolKeys.Distinct(StringComparer.Ordinal)
                                     .ToDictionary(key => key, key => (IReadOnlySet<AbstractValue>)VariablePoints(key), StringComparer.Ordinal);
                if (create.ReceiverValue is int bound)
                    captured["this"] = _points[bound];
                else if (create.TargetBodyId is { } target && receiver is not null && UsesReceiver(target, []))
                    captured["this"] = new HashSet<AbstractValue> { ThisValue.Instance };
                delegates[Site(create)] = new DelegateCreationValue(Site(create), Target(create), captured);
            }

            return delegates;
        }

        /// <summary>Whether a lambda or local function touches the receiver it closes over, itself, through a delegate it creates or
        /// through a local function it calls; a body that is not at hand may, so it counts as using it.</summary>
        private bool UsesReceiver(string bodyId, HashSet<string> visited)
        {
            if (!visited.Add(bodyId))
                return false;
            if ((_bodies?.Invoke(bodyId) ?? (bodyId == _body.BodyId ? _body : null)) is not { } body)
                return true;

            var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            var nested = operations.OfType<IrCreateDelegateOperation>().Select(create => create.TargetBodyId)
                                   .Concat(operations.OfType<IrCallOperation>()
                                                     .Where(call => call.CallKind == IrCallKind.LocalFunction)
                                                     .Select(call => call.Method))
                                   .OfType<string>();
            if (body.Values.FirstOrDefault(value => value.Kind == IrValueKind.Receiver) is { } receiver &&
                operations.Any(operation => operation.Operands.Contains(receiver.Id)))
            {
                return true;
            }

            return nested.Any(target => UsesReceiver(target, visited));
        }

        /// <summary>A value set with each delegate creation replaced by its final value carrying what it captured.</summary>
        private static HashSet<AbstractValue> Final(IEnumerable<AbstractValue> values, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            values.Select(value => value is DelegateCreationValue created && delegates.TryGetValue(created.Site, out var final) ? final : value)
                  .ToHashSet();

        private Dictionary<int, IReadOnlyList<HeldLockValue>> HeldLocks()
        {
            var lockValues = new Dictionary<string, IReadOnlySet<AbstractValue>>(StringComparer.Ordinal);
            var held = MustHeldLocks.Compute(_body, operation =>
            {
                var (kind, lockValue, primitive, mode) = operation switch
                {
                    IrAcquireOperation acquire => (LockEffectKind.Acquire, acquire.LockValue, acquire.Primitive, acquire.Mode),
                    IrReleaseOperation release => (LockEffectKind.Release, release.LockValue, release.Primitive, release.Mode),
                    _ => (LockEffectKind.None, -1, default, default)
                };
                if (kind == LockEffectKind.None)
                    return LockEffect.None;

                var values = _points[lockValue];
                var known = values.Count == 1;
                // Two mechanisms on one object are independent, so the kind is part of what a release matches.
                var key = $"{primitive}:{(known ? values.Single().ToString() : $"value:{Origin(lockValue)}")}";
                lockValues[key] = known ? values : new HashSet<AbstractValue>();
                return new LockEffect(kind, new LockObject(key, key, null, !known) { Primitive = primitive, Mode = mode });
            });
            return held.Operations.ToDictionary(pair => pair.Key,
                                     pair => (IReadOnlyList<HeldLockValue>)pair.Value.Values
                                                                              .OrderBy(lockHeld => lockHeld.AcquisitionId)
                                                                              .Select(lockHeld => new HeldLockValue(lockValues[lockHeld.Lock.Key], lockHeld.AcquisitionId))
                                                                              .ToArray());
        }

        /// <summary>The cell an element operation touches: the field the collection was read from, the object holding that field,
        /// and which cell of it (TD-043). A collection the body did not read from a field — a local array, a parameter — is no
        /// resource of its own here, so it has no cell.</summary>
        private ElementCell? Cell(int receiver, IReadOnlyList<int> indices, bool namesOneCell = true)
        {
            var slice = Slice(receiver);
            if (Definition(slice.Array) is not IrLoadFieldOperation load)
                return null;

            // A cell of storage nothing numbers is still that storage: it stays an access of the collection, and only which cell
            // it is stays unknown, so two indices of it are never proven disjoint (TD-043).
            var selector = indices.Count == 1 && namesOneCell ? Selector(indices[0]) : ElementSelector.Unknown;
            return new ElementCell(load.Field, load.ReceiverValue ?? slice.Array, selector.InSlice(slice.Shift, slice.Length),
                                   indices.Count == 1 && namesOneCell ? Shifted(Term(indices[0]), slice.Shift) : null);
        }

        /// <summary>The cell an element address takes of a collection this body got by value: which cell, in the coordinates of the
        /// collection its caller handed over. Only the value the parameter has on entry is that collection.</summary>
        private ReferenceParameterElement? ParameterElement(int receiver, IReadOnlyList<int> indices, bool namesOneCell)
        {
            var slice = Slice(receiver);
            if (!_parameterOrdinals.TryGetValue(slice.Array, out var ordinal) ||
                !_body.Parameters.Any(parameter => parameter.Ordinal == ordinal && parameter.RefKind == IrRefKind.None))
                return null;

            var oneCell = indices.Count == 1 && namesOneCell;
            var selector = oneCell ? Selector(indices[0]) : ElementSelector.Unknown;
            return new ReferenceParameterElement(ordinal, selector.InSlice(slice.Shift, slice.Length),
                                                 oneCell ? Shifted(Term(indices[0]), slice.Shift) : null);
        }

        /// <summary>The collection an element operation is on when a call with a body returned it: which cell of it is left
        /// unknown, since only the callee's returns say what storage that is (R3).</summary>
        private ReferenceCallCollection? CallCollection(int receiver)
        {
            var array = Slice(receiver).Array;
            return Definition(array) is IrCallOperation call && call.ResultValue == array && !IsOpaque(call)
                ? new ReferenceCallCollection(call.Id)
                : null;
        }

        /// <summary>The collection a value is, as a caller hands it to a callee or a callee hands it back: the field it was read
        /// from, the parameter it came in as, or the call that returned it, with the slice cut from it.</summary>
        private ArgumentCollection? Collection(int value)
        {
            var slice = Slice(value);
            if (Definition(slice.Array) is IrLoadFieldOperation load)
                return new ArgumentCollection(new ReferenceCell(load.Field, AccessBases(Points(load.ReceiverValue ?? slice.Array)), null, null, true),
                                              slice.Shift, slice.Length);
            if (ParameterElement(value, [], false) is { } parameter)
                return new ArgumentCollection(parameter with { Selector = ElementSelector.Unknown }, slice.Shift, slice.Length);
            return CallCollection(value) is { } call ? new ArgumentCollection(call, slice.Shift, slice.Length) : null;
        }

        private IReadOnlyList<ReferenceTarget> References(int value, int depth = 0)
        {
            if (depth >= _limits.MaxAccessPathDepth)
                return [];
            if (_parameterOrdinals.TryGetValue(value, out var ordinal) &&
                _body.Parameters.Any(parameter => parameter.Ordinal == ordinal && parameter.RefKind != IrRefKind.None))
                return [new ReferenceParameter(ordinal)];
            if (!_definitions.TryGetValue(value, out var definition))
                return [];

            return definition switch
            {
                IrAddressFieldOperation field =>
                    [new ReferenceCell(field.Field, AccessBases(Points(field.ReceiverValue)), null, null, false)],
                IrAddressElementOperation element when Cell(element.ReceiverValue, element.IndexValues, element.NamesOneCell) is { } cell =>
                    [new ReferenceCell(cell.Field, AccessBases(Points(cell.BaseValue)), cell.Selector, cell.Term, true)],
                IrAddressElementOperation element when ParameterElement(element.ReceiverValue, element.IndexValues, element.NamesOneCell) is { } parameter =>
                    [parameter],
                IrAddressElementOperation element when CallCollection(element.ReceiverValue) is { } call => [call],
                IrAssignOperation assign => References(assign.SourceValue, depth + 1),
                IrPhiOperation phi => Alternatives(phi.Inputs.Select(input => References(input.Value, depth + 1)).ToArray()),
                IrConvertOperation convert => References(convert.OperandValue, depth + 1),
                IrCallOperation call when call.ResultValue == value && !IsOpaque(call) => [new ReferenceCall(call.Id)],
                _ => []
            };
        }

        /// <summary>The places a reference that joins several may point to. Where some alternative names a place and another names
        /// none, the unnamed one is a place nothing proves: it is kept as such, so it is counted rather than lost behind the others
        /// (R3). Alternatives that all name nothing are a value that is no reference at all.</summary>
        private static IReadOnlyList<ReferenceTarget> Alternatives(IReadOnlyList<ReferenceTarget>[] alternatives) =>
            alternatives.Any(targets => targets.Count != 0) && alternatives.Any(targets => targets.Count == 0)
                ? [.. alternatives.SelectMany(targets => targets), ReferenceUnproven.Instance]
                : alternatives.SelectMany(targets => targets).ToArray();

        /// <summary>The cell's expression in the coordinates of the collection it is cut from, which is where the selector already
        /// is: an index of a slice names the cell that many places further along, and handing the solver the index of the window
        /// would make <c>span(4, 8)[1]</c> and <c>array[5]</c> two cells although they are one. An offset nothing proves keeps the
        /// unknown it is rather than passing the window's own index off as the array's (TD-092).</summary>
        private static ValueTerm? Shifted(ValueTerm term, long? shift) => shift switch
        {
            null => null,
            0 => term,
            { } offset => new SumTerm(term, new ConstantTerm(offset, term.Width))
        };

        /// <summary>The member a call names, without the type that declares it, its generic arguments or its parameters:
        /// <c>System.MemoryExtensions.AsSpan&lt;T&gt;(int[], int, int)</c> is <c>AsSpan</c>. A member is recognised by its name and
        /// never by a substring of the whole signature, which a generic argument list is enough to defeat.</summary>
        private static string MemberOf(string method)
        {
            var parameters = method.IndexOf('(', StringComparison.Ordinal);
            var name = parameters < 0 ? method : method[..parameters];
            var declaring = name.LastIndexOf('.');
            name = declaring < 0 ? name : name[(declaring + 1)..];
            var generic = name.IndexOf('<', StringComparison.Ordinal);
            return generic < 0 ? name : name[..generic];
        }

        /// <summary>Whether the type that declares the member is one whose slices start where they say they do. A member is
        /// matched by its declaring type together with its name and never by the name alone: a foreign <c>Slice</c> may ignore the
        /// offset it is given, and reading it as a window would place its cells where nothing put them (TD-043).</summary>
        private static bool DeclaresKnownSlices(IrCallOperation call) =>
            call.TargetContainingTypeKey is { } key &&
            SpanTypes.Names(key.IndexOf(':') is var assembly && assembly >= 0 ? key[(assembly + 1)..] : key);

        /// <summary>A slice reduces to the region it is cut from and the offset it starts at, so two slices are compared by those
        /// and never by the slice objects (TD-043).</summary>
        private SliceOf Slice(int value, int depth = 0)
        {
            var origin = Origin(value);
            if (depth < SLICE_DEPTH && Definition(origin) is IrCallOperation { Method: var method } call && call.ResultValue == origin &&
                MemberOf(method) is "AsSpan" or "Slice" && DeclaresKnownSlices(call) && call.ArgumentValues.Count != 0)
            {
                // `AsSpan(array, start, ...)` is a static extension whose array is its first parameter, `Slice(start, ...)` an
                // instance member of the span it cuts. Each is read by the parameter it is bound to: a named argument may stand
                // anywhere in the call, and a position names a different parameter for every caller who writes them differently.
                var isExtension = call.ReceiverValue is null;
                var (arrayOrdinal, startOrdinal, lengthOrdinal) = isExtension ? (0, 1, 2) : (-1, 0, 1);
                if ((isExtension ? call.ArgumentAt(arrayOrdinal) : call.ReceiverValue) is not { } array)
                    return new SliceOf(origin, 0, null);

                var start = depth < SELECTOR_BUDGET
                    ? call.ArgumentAt(startOrdinal) is { } written ? Constant(written) : 0
                    : null;
                var inner = Slice(array, depth + 1);
                // Past the budget, and where an offset is not a constant, the chain is still unwound to the collection it cuts; only
                // the offset is given up, so the cell widens to unknown rather than falling away (TD-043).
                if (start is not { } offset || inner.Shift is not { } shift)
                    return new SliceOf(inner.Array, null, null);

                var length = call.ArgumentAt(lengthOrdinal) is { } given
                    ? Constant(given)
                    : inner.Length is { } outer ? outer - offset : null;
                return new SliceOf(inner.Array, shift + offset, length);
            }

            return new SliceOf(origin, 0, null);
        }

        /// <summary>What an index or a key names: a proven constant, a constant key, or the value's own identity, which proves
        /// nothing until a solver reads it but keeps two indices apart from a third.</summary>
        private ElementSelector Selector(int index)
        {
            var value = Origin(index);
            if (Constant(value) is { } constant)
                return ElementSelector.Exact(constant);
            return Literal(value) is { } literal ? ElementSelector.Key(literal) : ElementSelector.Symbol($"{_body.BodyId}:{value}");
        }

        /// <summary>How far the expression of a cell is followed before it is taken as a value of its own (TD-092).</summary>
        private const int TERM_DEPTH = 4;

        /// <summary>The expression a cell's index is, in the width of its own type: constants, sums and conversions as far as
        /// the budget goes, and a value of this body's own beyond it. The solver compares two of these, so a conversion kept
        /// here is a conversion decided there (TD-094).</summary>
        private ValueTerm Term(int value, int depth = 0)
        {
            var origin = Origin(value);
            var type = _values.TryGetValue(origin, out var named) ? named.Type : null;
            // A value carries the signedness of its own type: it is what decides how the value reaches a wider one, and reading
            // it off the type a conversion produces would widen an unsigned byte by a sign it does not have (TD-094).
            var (width, signed) = (Width(type), IsSigned(type));
            if (Constant(origin) is { } constant)
                return new ConstantTerm(constant, width, signed);
            if (depth >= TERM_DEPTH)
                return new VariableTerm($"{_body.BodyId}:{origin}", width, signed);

            return Definition(origin) switch
            {
                IrConvertOperation convert => new ConvertTerm(Term(convert.OperandValue, depth + 1), Width(convert.Type),
                                                              IsSigned(convert.Type)),
                IrComputeOperation { Operator: "Add", OperandValues.Count: 2 } compute =>
                    new SumTerm(Term(compute.OperandValues[0], depth + 1), Term(compute.OperandValues[1], depth + 1)),
                IrLoadFieldOperation load => new VariableTerm($"{_body.BodyId}#{load.Id}", width, signed),
                _ => new VariableTerm($"{_body.BodyId}:{origin}", width, signed)
            };
        }

        /// <summary>The width of a numeric type in bits; anything else is taken as the width of an <c>int</c>, which is what an
        /// index is unless the source says otherwise.</summary>
        private static int Width(string? type) => type switch
        {
            "byte" or "sbyte" or "System.Byte" or "System.SByte" => 8,
            "short" or "ushort" or "char" or "System.Int16" or "System.UInt16" or "System.Char" => 16,
            "long" or "ulong" or "System.Int64" or "System.UInt64" => 64,
            // A native integer is as wide as the process the analysis runs for, which is the 64-bit one this ships as: read as
            // 32 bits, a guard over one is truncated and a satisfiable range becomes empty (R9).
            "nint" or "nuint" or "System.IntPtr" or "System.UIntPtr" => NATIVE_WIDTH,
            _ => 32
        };

        /// <summary>The width of a native integer, which is the width of the process the analysis is built for.</summary>
        private static readonly int NATIVE_WIDTH = IntPtr.Size * 8;

        /// <summary>Whether a type is signed. Every unsigned integral type of the language is named here, <c>char</c> among them:
        /// it is a 16-bit unsigned number, and widening one by a sign it does not have turns a high code point negative (R9).</summary>
        private static bool IsSigned(string? type) =>
            type is not ("byte" or "ushort" or "uint" or "ulong" or "char" or "nuint" or
                         "System.Byte" or "System.UInt16" or "System.UInt32" or "System.UInt64" or "System.Char" or "System.UIntPtr");

        /// <summary>The text of a constant that is not a number, <c>null</c> and the frontend's own placeholders aside.</summary>
        private string? Literal(int value) =>
            _body.Values.FirstOrDefault(candidate => candidate.Id == NumericOrigin(value)) is { Kind: IrValueKind.Constant } constant &&
            constant.Name is not ("null" or "default" or "exceptional")
                ? constant.Name
                : null;

        /// <summary>The accesses of the modelled collection members this body calls (ADR 0010). Every member touches the
        /// collection's structure, and a member that reaches cells touches the cell its key names or every cell at once. A change
        /// that depends on an earlier read of the same collection is one compound operation, reported where the change is, over
        /// the resource the dependency crosses: the cell when both ends prove the same one, the structure otherwise, because a
        /// dependency between two cells nothing proves the same is only contained by the whole collection.</summary>
        private (IReadOnlyList<SummaryAccess> Accesses, IReadOnlySet<int> Receivers) CollectionAccesses(
            IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates,
            IReadOnlyDictionary<int, IReadOnlyList<HeldLockValue>> held)
        {
            var members = _operations.OfType<IrCallOperation>()
                                     .Select(call => call.Collection is { } effects && call.ReceiverValue is { } receiver &&
                                                     Definition(receiver) is IrLoadFieldOperation load
                                                 ? new CollectionMember(call, effects, load, Key(call, effects))
                                                 : null)
                                     .OfType<CollectionMember>()
                                     .ToArray();
            var accesses = new List<SummaryAccess>();
            foreach (var member in members)
            {
                var checks = Checks(member, members);
                // The sequence is contained by one cell only where every read it depends on is that cell: a read of the
                // collection's own structure, of another key or of a key nothing proves the same crosses cells, and only the
                // whole collection contains a dependency that crosses them. What the changing member does to the structure does
                // not decide this — a member that only reads the structure, an update in place among them, still makes a
                // sequence whose two ends only the whole collection holds (ADR 0010).
                var onCell = member.Selector is { } selector && checks.All(check => check.Selector?.IsProvenSameAs(selector) == true);
                var onElement = checks.Count != 0 && onCell && Changes(member.Effects.Element);
                var onStructure = checks.Count != 0 && !onElement;
                // Only the access the sequence is reported on carries its checks; the other resource keeps its own operation.
                var dependencies = checks.Select(check => (ValueDependency)new LoadDependency(check.Call.Id)).ToHashSet();
                if (member.Effects.Structure != IrCollectionEffect.None)
                {
                    accesses.Add(Access(member, member.Effects.Structure, null, onStructure,
                                        onStructure ? dependencies : [], delegates, held));
                }

                if (member.Effects.Element != IrCollectionEffect.None)
                {
                    accesses.Add(Access(member, member.Effects.Element, member.Selector ?? ElementSelector.Unknown, onElement,
                                        onElement ? dependencies : [], delegates, held));
                }
            }

            return (accesses, members.Select(member => member.Load.Id).ToHashSet());
        }

        /// <summary>The reads of the same collection that the change of <paramref name="member"/> depends on: through the
        /// conditions that decide it runs, and through the values it is given.</summary>
        private IReadOnlyList<CollectionMember> Checks(CollectionMember member, IReadOnlyList<CollectionMember> members)
        {
            if (!Changes(member.Effects.Structure) && !Changes(member.Effects.Element))
                return [];

            var dependencies = Guards(member.Call.Id).Select(guard => guard.Value).Concat(member.Call.ArgumentValues)
                                                     .SelectMany(value => _dependencies[value])
                                                     .OfType<LoadDependency>()
                                                     .Select(load => load.OperationId)
                                                     .ToHashSet();
            return members.Where(candidate => candidate.Call.Id != member.Call.Id && dependencies.Contains(candidate.Call.Id) &&
                                              Reads(candidate.Effects) && IsSameCollection(candidate, member))
                          .ToArray();
        }

        private static bool Changes(IrCollectionEffect effect) => effect is IrCollectionEffect.Write or IrCollectionEffect.ReadWrite;

        private static bool Reads(IrCollectionCall effects) =>
            effects.Structure is IrCollectionEffect.Read or IrCollectionEffect.ReadWrite ||
            effects.Element is IrCollectionEffect.Read or IrCollectionEffect.ReadWrite;

        /// <summary>
        /// Whether two members may work on one collection, which is a question about the collection objects and not about the
        /// names they were reached by: the objects their receivers may be have to meet. Two fields holding one dictionary hold
        /// one dictionary, and a receiver that may be either of two collections may be the one the other member holds — an
        /// ambiguity is no proof that the two touch different collections (ADR 0010). Where neither receiver resolves to an
        /// object at all, the field that names it is the only thing left to compare.
        /// </summary>
        private bool IsSameCollection(CollectionMember first, CollectionMember second)
        {
            var ours = Points(first.Call.ReceiverValue);
            var theirs = Points(second.Call.ReceiverValue);
            if (ours.Count != 0 && theirs.Count != 0)
                return ours.Overlaps(theirs);

            return FieldSlot.Key(first.Load.Field) == FieldSlot.Key(second.Load.Field) &&
                   Points(first.Load.ReceiverValue).Overlaps(Points(second.Load.ReceiverValue));
        }

        /// <summary>The cell a member names, read from the parameter that declares the key and never from the position the
        /// caller wrote it at.</summary>
        private ElementSelector? Key(IrCallOperation call, IrCollectionCall effects) =>
            effects.KeyArgument is { } ordinal && call.ArgumentAt(ordinal) is { } key ? Selector(key) : null;

        private SummaryAccess Access(CollectionMember member, IrCollectionEffect effect, ElementSelector? selector, bool compound,
                                     IReadOnlySet<ValueDependency> dependencies,
                                     IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates,
                                     IReadOnlyDictionary<int, IReadOnlyList<HeldLockValue>> held) =>
            new(member.Call.Id, effect == IrCollectionEffect.Read ? SummaryAccessKind.Load : SummaryAccessKind.Store, member.Load.Field,
                AccessBases(Final(Points(member.Load.ReceiverValue), delegates)), member.Call.Provenance,
                held.GetValueOrDefault(member.Call.Id) ?? [], null, new HashSet<AbstractValue>(), dependencies)
            {
                // A thread-safe collection performs each of its members atomically on both of its resources; the sequence of two
                // of them is still not atomic, which is what the compound operation says.
                Atomic = member.Effects.IsAtomic ? Atomic(effect) : null,
                Selector = selector,
                IsCompound = compound,
                IsOnCollection = true
            };

        private static IrAtomicEffect Atomic(IrCollectionEffect effect) => effect switch
        {
            IrCollectionEffect.Read => IrAtomicEffect.Read,
            IrCollectionEffect.Write => IrAtomicEffect.Write,
            _ => IrAtomicEffect.ReadModifyWrite
        };

        /// <summary>The conditions whose outcome decides that an operation runs, each with the value it must have there: the
        /// branch of every block that dominates the operation and reaches it through one of its two edges only. Reaching the
        /// destination a branch jumps to means the condition holds as the branch tests it; reaching the other means it does not.
        /// It is the edge that has to stand on every path, not only its destination: the block after an <c>if</c> without an
        /// <c>else</c> is the destination of the branch that skips the <c>if</c>, and is reached from its body as well.</summary>
        private IReadOnlyList<(int Value, bool Expected)> Guards(int operationId)
        {
            if (!BlockOf.TryGetValue(operationId, out var block))
                return [];

            var guards = new List<(int, bool)>();
            foreach (var candidate in _body.Blocks)
            {
                if (candidate.Ordinal == block || candidate.ConditionalBranch is not { ConditionValue: int condition, Destination: int taken } branch ||
                    candidate.FallThroughBranch?.Destination is not { } skipped || !Dominators[block].Contains(candidate.Ordinal))
                {
                    continue;
                }

                var jumped = EdgeDominates(candidate.Ordinal, taken, block);
                if (jumped != EdgeDominates(candidate.Ordinal, skipped, block))
                    guards.Add((condition, jumped == (branch.JumpIfTrue ?? true)));
            }

            return guards;
        }

        /// <summary>Whether every path to <paramref name="block"/> takes the edge from <paramref name="from"/> to
        /// <paramref name="to"/>: <paramref name="to"/> dominates it, and nothing enters <paramref name="to"/> but that edge and
        /// paths that already passed through <paramref name="to"/> itself.</summary>
        private bool EdgeDominates(int from, int to, int block) =>
            Dominators[block].Contains(to) &&
            _body.Blocks[to].Predecessors.All(predecessor => predecessor == from || Dominators[predecessor].Contains(to));

        /// <summary>The predicates that must hold where an operation runs (TD-090): one per condition that decides it, read as
        /// far as the bounded forms go. Everything else is kept as an unsupported predicate, which constrains nothing and is
        /// reported as an uncertainty rather than dropped (TD-095).</summary>
        private IReadOnlyList<SummaryPredicate> Conditions(int operationId) =>
            Guards(operationId).Select(guard => Predicate(guard.Value, guard.Expected)).ToArray();

        private SummaryPredicate Predicate(int value, bool expected)
        {
            var origin = Origin(value);
            switch (Definition(origin))
            {
                case IrConvertOperation convert:
                    return Predicate(convert.OperandValue, expected);
                // A condition that is the value itself tests a boolean, and the branch says which one it must be.
                case IrLoadFieldOperation load when load.Field.Type is "bool" or "System.Boolean":
                    return new SummaryPredicate(load.Id, origin, PathRelation.Equal, expected ? "true" : "false",
                                                $"{load.Field.ContainingType}.{load.Field.Name}");
                case IrCompareOperation { Operator: { } comparison } compare when Compare(compare, comparison, expected) is { } predicate:
                    return predicate;
                default:
                    return new SummaryPredicate(null, null, PathRelation.Unsupported, null, Describe(origin));
            }
        }

        /// <summary>A comparison as a predicate on the side of it that is not a constant. A comparison of two values the
        /// analysis cannot reduce to a constant is unsupported, as is one whose operator the lowering did not record.</summary>
        private SummaryPredicate? Compare(IrCompareOperation compare, IrComparisonOperator comparison, bool expected)
        {
            // Whether what the branch tests holds where the access runs: an inequality holds exactly where its equality does not.
            var holds = comparison is IrComparisonOperator.NotEqual ? !expected : expected;
            if (compare.Comparison == IrComparisonKind.Null)
                return Subject(compare.LeftValue, holds ? PathRelation.Null : PathRelation.NotNull, null);
            if (compare.Comparison == IrComparisonKind.Type)
                return Subject(compare.LeftValue, holds ? PathRelation.Type : PathRelation.NotType, compare.Type);
            if (compare.RightValue is not { } right)
                return null;

            var (subject, constant, mirrored) = ConstantText(compare.LeftValue) is { } left
                ? (right, left, true)
                : ConstantText(right) is { } value ? (compare.LeftValue, value, false) : (0, null!, false);
            if (constant is null)
                return null;

            var relation = compare.Comparison == IrComparisonKind.Equality
                ? holds ? PathRelation.Equal : PathRelation.NotEqual
                : Ordering(mirrored ? Mirror(comparison) : comparison, expected);
            return relation is null ? null : Subject(subject, relation.Value, constant);
        }

        /// <summary>The predicate on a value: on the field it loads, so that another execution reading the same field of the
        /// same object states the same subject, or on the value itself, which is this execution's own.</summary>
        private SummaryPredicate Subject(int value, PathRelation relation, string? constant)
        {
            var origin = Origin(value);
            // The subject is decided in the width of its own type: a guard over a `long` read as an `int` is another guard.
            var type = Definition(origin) is IrLoadFieldOperation field ? field.Field.Type : TypeOf(origin);
            return Definition(origin) is IrLoadFieldOperation load
                ? new SummaryPredicate(load.Id, origin, relation, constant, $"{load.Field.ContainingType}.{load.Field.Name}")
                    { Width = Width(type), Signed = IsSigned(type) }
                : new SummaryPredicate(null, origin, relation, constant, Describe(origin))
                    { Width = Width(type), Signed = IsSigned(type) };
        }

        /// <summary>What an ordering leaves its subject on the branch it decides: the comparison itself where it holds, and its
        /// negation where it does not.</summary>
        private static PathRelation? Ordering(IrComparisonOperator comparison, bool expected) => (comparison, expected) switch
        {
            (IrComparisonOperator.Less, true) or (IrComparisonOperator.GreaterOrEqual, false) => PathRelation.Less,
            (IrComparisonOperator.LessOrEqual, true) or (IrComparisonOperator.Greater, false) => PathRelation.LessOrEqual,
            (IrComparisonOperator.Greater, true) or (IrComparisonOperator.LessOrEqual, false) => PathRelation.Greater,
            (IrComparisonOperator.GreaterOrEqual, true) or (IrComparisonOperator.Less, false) => PathRelation.GreaterOrEqual,
            _ => null
        };

        /// <summary>The comparison as the other side states it: <c>5 &lt; x</c> is <c>x &gt; 5</c>.</summary>
        private static IrComparisonOperator Mirror(IrComparisonOperator comparison) => comparison switch
        {
            IrComparisonOperator.Less => IrComparisonOperator.Greater,
            IrComparisonOperator.LessOrEqual => IrComparisonOperator.GreaterOrEqual,
            IrComparisonOperator.Greater => IrComparisonOperator.Less,
            IrComparisonOperator.GreaterOrEqual => IrComparisonOperator.LessOrEqual,
            _ => comparison
        };

        /// <summary>The text of a value a predicate compares against, which is a literal or nothing at all. The name of a static
        /// field is not one: two names are two fields and never proof of two values, and a field whose value the analysis has not
        /// read leaves a predicate that constrains nothing rather than one that decides on a spelling (TD-090, TD-095). A
        /// constant reaches here as its own value, an enum member's number among them.</summary>
        private string? ConstantText(int value) => Literal(value);

        /// <summary>What an unsupported predicate is called in the uncertainties.</summary>
        private string Describe(int value) => Definition(value) switch
        {
            IrCallOperation call => call.Method,
            IrCompareOperation compare => $"{compare.Comparison} comparison at {compare.Provenance.Span.StartLine}",
            IrComputeOperation compute => compute.Operator,
            { } operation => $"{operation.Provenance.SyntaxKind} at {operation.Provenance.Span.StartLine}",
            _ => _values.TryGetValue(value, out var named) ? named.Name : $"%{value}"
        };

        /// <summary>The parameter a value comes from, when it comes from one, through the numeric conversions on the way, with
        /// the narrowest width it passes through — null where it passes through none. A conversion narrower than the range of the
        /// values the parameter takes maps two of them to one cell, and a cell two iterations may share proves nothing about
        /// either of them (TD-068).</summary>
        private (int? Ordinal, int? Width) ParameterOf(int value)
        {
            var origin = Origin(value);
            int? narrowest = null;
            var visited = new HashSet<int>();
            while (visited.Add(origin) && Definition(origin) is IrConvertOperation { ConversionKind: IrConversionKind.Numeric } convert)
            {
                narrowest = Math.Min(narrowest ?? int.MaxValue, Width(TypeOf(origin)));
                origin = Origin(convert.OperandValue);
            }

            return (_parameterOrdinals.TryGetValue(origin, out var ordinal) ? ordinal : null, narrowest);
        }

        /// <summary>The type the body records for a value, null where it records none.</summary>
        private string? TypeOf(int value) => _values.TryGetValue(Origin(value), out var named) ? named.Type : null;

        private IReadOnlyDictionary<int, int> BlockOf =>
            _blockOf ??= _body.Blocks.SelectMany(block => block.Operations.Select(operation => (operation.Id, block.Ordinal)))
                              .ToDictionary(pair => pair.Id, pair => pair.Ordinal);

        /// <summary>Which blocks every block is reached only through, by the usual fixpoint over the predecessors.</summary>
        private IReadOnlyDictionary<int, HashSet<int>> Dominators
        {
            get
            {
                if (_dominators is not null)
                    return _dominators;

                var all = _body.Blocks.Select(block => block.Ordinal).ToHashSet();
                _dominators = _body.Blocks.ToDictionary(block => block.Ordinal,
                                                        block => block.Predecessors.Count == 0 ? [block.Ordinal] : new HashSet<int>(all));
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var block in _body.Blocks.Where(block => block.Predecessors.Count != 0))
                    {
                        var dominators = block.Predecessors.Select(predecessor => _dominators[predecessor])
                                              .Aggregate(new HashSet<int>(all), (common, next) => common.Intersect(next).ToHashSet());
                        dominators.Add(block.Ordinal);
                        if (dominators.SetEquals(_dominators[block.Ordinal]))
                            continue;
                        _dominators[block.Ordinal] = dominators;
                        changed = true;
                    }
                }

                return _dominators;
            }
        }

        /// <summary>One call of a modelled collection member: the call, what it does, the load of the collection it works on and
        /// the cell its key names.</summary>
        private sealed record CollectionMember(IrCallOperation Call, IrCollectionCall Effects, IrLoadFieldOperation Load,
                                               ElementSelector? Selector);

        private long? Constant(int value) =>
            _body.Values.FirstOrDefault(candidate => candidate.Id == NumericOrigin(value)) is { Kind: IrValueKind.Constant } constant &&
            long.TryParse(constant.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

        /// <summary>The value a number is read from, through the widening conversions the language inserts to compare or index
        /// with two numeric types. A widening conversion hands on the number it is given, so a constant behind one is the same
        /// constant; a narrowing one the compiler has not already folded is a number of its own and stops the walk.</summary>
        private int NumericOrigin(int value)
        {
            var origin = Origin(value);
            var visited = new HashSet<int>();
            while (visited.Add(origin) && Definition(origin) is IrConvertOperation { ConversionKind: IrConversionKind.Numeric } convert &&
                   Width(TypeOf(origin)) >= Width(TypeOf(convert.OperandValue)))
            {
                origin = Origin(convert.OperandValue);
            }

            return origin;
        }

        private IrOperation? Definition(int value) => _definitions.GetValueOrDefault(Origin(value));

        /// <summary>A slice reduced to what it cuts: the value of the underlying collection, the offset it starts at when that is
        /// proven, and its length when that is a constant.</summary>
        private readonly record struct SliceOf(int Array, long? Shift, long? Length);

        /// <summary>One cell of one collection: the field it was read from, the object holding that field, which cell, and the
        /// expression naming that cell in the collection's own coordinates (TD-092).</summary>
        private readonly record struct ElementCell(IrFieldRef Field, int BaseValue, ElementSelector Selector, ValueTerm? Term);

        /// <summary>The value a lock object comes from through assigns and the conversions that keep it the same value, so a lock
        /// statement's release matches its acquisition when the object's values are unknown. A numeric conversion is not one of
        /// them: <c>(byte)i</c> is a number of its own, and following it through would hand the index of <c>i</c> to a cell it
        /// does not name (TD-094).</summary>
        private int Origin(int value)
        {
            var visited = new HashSet<int>();
            while (visited.Add(value) && _definitions.TryGetValue(value, out var definition))
            {
                if (definition is IrAssignOperation assign)
                    value = assign.SourceValue;
                else if (definition is IrConvertOperation { ConversionKind: not IrConversionKind.Numeric } convert)
                    value = convert.OperandValue;
                else
                    break;
            }

            return value;
        }

        private bool IsOpaque(IrCallOperation call)
        {
            if (call.CallKind is IrCallKind.LocalFunction or IrCallKind.Delegate || call.TargetMethodId is not { } methodId)
                return false;
            if (_index.Method(methodId) is not { } method)
                return true;
            if (method.HasSourceBody)
                return false;
            return call.CallKind is not (IrCallKind.Virtual or IrCallKind.Interface) || !DispatchedWithBody(_index).Contains(methodId);
        }

        private bool IsValueType(string type)
        {
            var underlying = type.EndsWith('?') ? type[..^1] : type;
            return PRIMITIVE_TYPES.Contains(underlying) ||
                   _index.Types.Any(candidate => candidate.IsValueType && candidate.DisplayName == underlying);
        }

        /// <summary>The IR values of a variable that may reach the end of a block: its last definition there, or its values at the end
        /// of every flow predecessor, or its incoming value at the entry.</summary>
        private HashSet<int> ValuesAtEnd(int ordinal, string? symbolKey, int incoming)
        {
            var result = new HashSet<int>();
            var visited = new HashSet<int>();
            var pending = new Stack<int>([ordinal]);
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current))
                    continue;

                var block = _body.Blocks[current];
                var defined = block.Operations.SelectMany(operation => operation.DefinedValues)
                                   .Where(value => symbolKey is not null && _values[value].SymbolKey == symbolKey)
                                   .Select(value => (int?)value)
                                   .LastOrDefault();
                if (defined is int definedValue)
                    result.Add(definedValue);
                else if (block.FlowPredecessors.Count == 0)
                    result.Add(incoming);
                else
                    foreach (var predecessor in block.FlowPredecessors)
                        pending.Push(predecessor.BlockOrdinal);
            }

            return result;
        }

        private IEnumerable<string> VariableKeys() =>
            _body.Values.Where(value => value.Kind is IrValueKind.Local or IrValueKind.Parameter)
                 .Select(value => value.SymbolKey)
                 .OfType<string>()
                 .Distinct(StringComparer.Ordinal);

        private HashSet<AbstractValue> VariablePoints(string key) =>
            _body.Values.Where(value => value.SymbolKey == key).SelectMany(value => _points[value.Id]).ToHashSet();

        private HashSet<ValueDependency> VariableDependencies(string key) =>
            _body.Values.Where(value => value.SymbolKey == key).SelectMany(value => _dependencies[value.Id]).ToHashSet();

        private HashSet<AbstractValue> Points(int? value) => value is int id ? _points[id] : [];

        private HashSet<ValueDependency> Dependencies(int value) => _dependencies[value];

        /// <summary>The load whose own value a compare-and-swap checks the cell against, null where it checks anything else. It
        /// is the value that must match, not the reads it was computed from: <c>old</c> is the value the sequence observed and
        /// <c>old + 2</c> is a value nobody observed, however plainly it depends on <c>old</c> (R1).</summary>
        private int? Comparand(IReadOnlyDictionary<int, IrAtomicOperation> atomic, int operationId)
        {
            if (!atomic.TryGetValue(operationId, out var mark) || mark.ComparandValue is not { } comparand)
                return null;

            var origin = Origin(comparand);
            return Definition(origin) switch
            {
                IrLoadFieldOperation load when load.ResultValue == origin => load.Id,
                IrLoadElementOperation element when element.ResultValue == origin => element.Id,
                _ => null
            };
        }

        private CreationSite Site(IrCreateDelegateOperation create) =>
            new(_body.BodyId, create.Id, _values[create.ResultValue].Type, create.SiteOrdinal);

        private static string Target(IrCreateDelegateOperation create) =>
            create.TargetBodyId ?? create.TargetMethodId ?? create.TargetMethod ?? "?";
    }
}
