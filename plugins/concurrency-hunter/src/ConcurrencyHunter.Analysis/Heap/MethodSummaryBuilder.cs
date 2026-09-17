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

        internal Builder(IrBody body, ProgramIndex index, AnalysisLimits limits, Func<string, IrBody?>? bodies)
        {
            _body = body;
            _index = index;
            _limits = limits;
            _bodies = bodies;
            _operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            _values = body.Values.ToDictionary(value => value.Id);
            foreach (var operation in _operations)
            {
                foreach (var defined in operation.DefinedValues)
                    _definitions[defined] = operation;
            }

            _parameterOrdinals = body.Parameters.ToDictionary(parameter => parameter.Value, parameter => parameter.Ordinal);
            _capturedKeys = _operations.OfType<IrCaptureOperation>().Select(capture => capture.SymbolKey).OfType<string>()
                                       .ToHashSet(StringComparer.Ordinal);
        }

        internal MethodSummary Build()
        {
            Solve();
            var delegates = FinalDelegates();
            var held = HeldLocks();

            var accesses = new List<SummaryAccess>();
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
                    case IrLoadFieldOperation load:
                        accesses.Add(new SummaryAccess(load.Id, SummaryAccessKind.Load, load.Field, AccessBases(Points(load.ReceiverValue)),
                                                       load.Provenance, locks, null, Final(Points(load.ResultValue), delegates),
                                                       new HashSet<ValueDependency>()));
                        break;
                    case IrStoreFieldOperation store:
                        var stored = Final(Points(store.Value), delegates);
                        var bases = Final(Points(store.ReceiverValue), delegates);
                        accesses.Add(new SummaryAccess(store.Id, SummaryAccessKind.Store, store.Field, AccessBases(bases), store.Provenance, locks,
                                                       store.ReadModifyWriteOf, stored, Dependencies(store.Value)));
                        if (!IsValueType(store.Field.Type))
                            stores.Add(new StoreTransfer(store.Id, store.Field, bases, stored));
                        break;
                    case IrStoreElementOperation element:
                        elements.Add(new ElementTransfer(element.Id, ElementOperationKind.Store, Final(Points(element.ReceiverValue), delegates),
                                                         Final(Points(element.Value), delegates)));
                        break;
                    case IrLoadElementOperation element:
                        elements.Add(new ElementTransfer(element.Id, ElementOperationKind.Load, Final(Points(element.ReceiverValue), delegates),
                                                         Final(Points(element.ResultValue), delegates)));
                        break;
                    case IrReturnOperation { Value: int returned } @return:
                        returns.Add(new ReturnTransfer(@return.Id, Final(Points(returned), delegates), Dependencies(returned)));
                        break;
                    case IrCreateDelegateOperation create:
                        delegateTransfers.Add(new DelegateTransfer(create.Id, delegates[Site(create)], Final(Points(create.ReceiverValue), delegates),
                                                                   create.TargetContainingTypeKey, create.TargetMethodTypeArgumentKeys));
                        break;
                    case IrCallOperation call when IsOpaque(call):
                        opaqueCalls.Add(new SummaryOpaqueCall(call.Id, call.Method,
                                                              call.ArgumentValues.SelectMany(value => _points[value]).OfType<DelegateCreationValue>()
                                                                  .Distinct().Select(value => delegates[value.Site]).ToArray())
                        {
                            Receivers = Final(Points(call.ReceiverValue), delegates),
                            Arguments = Arguments(call, delegates),
                            ServiceCall = call.ServiceCall
                        });
                        break;
                    case IrCallOperation call:
                        calls.Add(new CallTransfer(call.Id, call.TargetMethodId ?? call.Method, call.CallKind, Final(Points(call.ReceiverValue), delegates),
                                                   Arguments(call, delegates), locks, call.TargetContainingTypeKey, call.TargetMethodTypeArgumentKeys));
                        break;
                    case IrAssignOperation assign when _values[assign.TargetValue].SymbolKey is { } key && _capturedKeys.Contains(key):
                        capturedStores.Add(new CapturedStore(assign.Id, key, Final(Points(assign.SourceValue), delegates), Dependencies(assign.SourceValue)));
                        break;
                }
            }

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
            var variables = VariableKeys().Select(key => new SummaryVariable(key, Final(VariablePoints(key), delegates), VariableDependencies(key)))
                                          .ToArray();
            var lockTransfers = _operations.Select(operation => operation switch
                                   {
                                       IrAcquireOperation acquire => new LockTransfer(acquire.Id, true, Points(acquire.LockValue), Origin(acquire.LockValue),
                                                                                      acquire.Provenance),
                                       IrReleaseOperation release => new LockTransfer(release.Id, false, Points(release.LockValue), Origin(release.LockValue),
                                                                                      release.Provenance),
                                       _ => null
                                   })
                                   .OfType<LockTransfer>()
                                   .ToArray();
            return new MethodSummary(_body.BodyId, accesses, stores, elements, returns, refParameters, delegateTransfers, calls, opaqueCalls,
                                     capturedStores, variables)
            {
                Locks = lockTransfers
            };
        }

        private CallArgument[] Arguments(IrCallOperation call, IReadOnlyDictionary<CreationSite, DelegateCreationValue> delegates) =>
            call.ArgumentValues.Select((value, position) => new CallArgument(
                    position < call.ArgumentParameterOrdinals.Count ? call.ArgumentParameterOrdinals[position] : position,
                    Final(Points(value), delegates))
                {
                    Dependencies = Dependencies(value)
                })
                .ToArray();

        /// <summary>Solves the values and the dependencies of every IR value until neither changes.</summary>
        private void Solve()
        {
            foreach (var value in _body.Values)
            {
                _points[value.Id] = [];
                _dependencies[value.Id] = [];
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
                }
            }
        }

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
                var (kind, lockValue) = operation switch
                {
                    IrAcquireOperation acquire => (LockEffectKind.Acquire, acquire.LockValue),
                    IrReleaseOperation release => (LockEffectKind.Release, release.LockValue),
                    _ => (LockEffectKind.None, -1)
                };
                if (kind == LockEffectKind.None)
                    return LockEffect.None;

                var values = _points[lockValue];
                var known = values.Count == 1;
                var key = known ? values.Single().ToString() : $"value:{Origin(lockValue)}";
                lockValues[key] = known ? values : new HashSet<AbstractValue>();
                return new LockEffect(kind, new LockObject(key, key, null, !known));
            });
            return held.ToDictionary(pair => pair.Key,
                                     pair => (IReadOnlyList<HeldLockValue>)pair.Value.Values
                                                                              .OrderBy(lockHeld => lockHeld.AcquisitionId)
                                                                              .Select(lockHeld => new HeldLockValue(lockValues[lockHeld.Lock.Key], lockHeld.AcquisitionId))
                                                                              .ToArray());
        }

        /// <summary>The value a lock object comes from through assigns and conversions, so a lock statement's release matches its
        /// acquisition when the object's values are unknown.</summary>
        private int Origin(int value)
        {
            var visited = new HashSet<int>();
            while (visited.Add(value) && _definitions.TryGetValue(value, out var definition))
            {
                if (definition is IrAssignOperation assign)
                    value = assign.SourceValue;
                else if (definition is IrConvertOperation convert)
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

        private CreationSite Site(IrCreateDelegateOperation create) =>
            new(_body.BodyId, create.Id, _values[create.ResultValue].Type, create.SiteOrdinal);

        private static string Target(IrCreateDelegateOperation create) =>
            create.TargetBodyId ?? create.TargetMethodId ?? create.TargetMethod ?? "?";
    }
}
