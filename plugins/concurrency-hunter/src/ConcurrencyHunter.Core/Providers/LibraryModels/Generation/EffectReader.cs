using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>One effect the member made on an argument, with the driver roots in whose trees it happened.</summary>
/// <param name="Kind"><c>reads-deep</c>, <c>writes-arg</c> or <c>writes-cells</c>.</param>
/// <param name="Roots"><c>V_Call</c> and/or <c>V_Enum</c>.</param>
/// <param name="EnumerationRoots">The roots in which a <c>reads-deep</c> access was an enumeration read.</param>
/// <param name="NonEnumerationRoots">The roots in which an access was not an enumeration read.</param>
/// <param name="ReadsSeed">Whether a <c>reads-deep</c> of the receiver read a seed setup placed in it, standing for a user object a
/// program stored there, or an object reachable from one.</param>
public sealed record GeneratedEffect(string Kind, IReadOnlyList<string> Roots, IReadOnlyList<string> EnumerationRoots,
                                     IReadOnlyList<string> NonEnumerationRoots, bool ReadsSeed);

/// <summary>A pre-existing library region the member stored into, with the roots in whose trees it did so.</summary>
/// <param name="Region">The stored-into region.</param>
/// <param name="Roots"><c>V_Call</c> and/or <c>V_Enum</c>.</param>
public sealed record StateStore(string Region, IReadOnlyList<string> Roots);

/// <summary>Reads effects, state stores and refusal evidence from one completed driver run. Only accesses made by bodies of the
/// implementation assembly in the trees of <c>V_Call</c> and <c>V_Enum</c> count.</summary>
public sealed class EffectReader
{
    public const string READS_DEEP = "reads-deep";
    public const string WRITES_ARGUMENT = "writes-arg";
    public const string WRITES_CELLS = "writes-cells";

    private readonly Driver _driver;
    private readonly IReadOnlyDictionary<string, IrBody> _runBodies;
    private readonly HeapSolution _heap;
    private readonly ExecutionAnalysis _analysis;
    private readonly DriverExecutions _executions;
    private readonly HeapReachability _reachability;
    private readonly Allocations _allocations;
    private readonly GenerationHandoffs _handoffs;
    private readonly ValueObservation _observation;
    private readonly Dictionary<string, Allocation> _origins = new(StringComparer.Ordinal);
    private Dictionary<(string Slot, string Region), HashSet<string>>? _setupSlotValues;
    private ILookup<string, string>? _staticStorage;
    private readonly Dictionary<(string Parameter, string Kind), Observation> _effects = [];
    private readonly Dictionary<string, HashSet<string>> _stateStores = new(StringComparer.Ordinal);
    private readonly List<Input> _inputs = [];
    private readonly HashSet<string> _probeReturns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _witnessReturns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownSensitive = new(StringComparer.Ordinal);
    private readonly HashSet<string> _witnessObjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receiverSeedObjects = new(StringComparer.Ordinal);
    private readonly HashSet<ISymbol> _unreachedFields = new(SymbolEqualityComparer.Default);
    private bool _unknownTouch;
    private bool _incomplete;
    private bool _vocabulary;

    /// <summary>Reads one completed driver run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="fates">The classified fates, or <c>null</c>: a parameter whose fate is <c>not-run</c> has no effect (R5).</param>
    public EffectReader(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate>? fates = null)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no effects to read.", nameof(run));

        _driver = driver;
        _runBodies = run.Reachable.Bodies;
        _heap = run.Heap!;
        _analysis = run.Executions!;
        _reachability = new HeapReachability(_heap);
        var result = _reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        _executions = new DriverExecutions(driver, _analysis, _reachability.From(result is null ? [] : _reachability.Targets(result)));
        _allocations = new Allocations(driver, _heap, _analysis, _executions, _reachability);
        _handoffs = new GenerationHandoffs(driver, run, _executions, _reachability);
        _observation = new ValueObservation(driver, run, GenerationHandoffs.UnfollowedCalls(run).Select(call => (call.Instance.Id, call.OperationId))
                                                                                             .ToHashSet());

        BuildInputs();
        BuildSpecialObjects();
        ReadUnreachedSeeds();
        ReadReasonsFromHandoffs();
        ReadAccesses();
        ReadSummaryEvidence();
        ReadUnknownDelegateStateStores();
        ReadWitnessesAndUnknownCalls();

        Effects = _effects.Where(pair => fates?.GetValueOrDefault(pair.Key.Parameter)?.Fate != FateClassifier.NOT_RUN)
                          .OrderBy(pair => pair.Key.Parameter, StringComparer.Ordinal).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
                          .GroupBy(pair => pair.Key.Parameter, StringComparer.Ordinal)
                          .ToDictionary(group => group.Key,
                                        group => (IReadOnlyList<GeneratedEffect>)group.Select(pair => pair.Value.ToEffect(pair.Key.Kind)).ToArray(),
                                        StringComparer.Ordinal);
        StateStores = _stateStores.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .Select(pair => new StateStore(pair.Key, OrderRoots(pair.Value))).ToArray();
        Reason = _unknownTouch ? GenerationReasons.UNKNOWN_TOUCH
            : _incomplete ? GenerationReasons.INCOMPLETE
            : _vocabulary ? GenerationReasons.VOCABULARY
            : null;
    }

    /// <summary>The effects by parameter name; the receiver is named <c>this</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GeneratedEffect>> Effects { get; }

    /// <summary>Stores into library state that may have existed before the call.</summary>
    public IReadOnlyList<StateStore> StateStores { get; }

    /// <summary>The first applicable model reason: <c>unknown-touch</c>, <c>incomplete</c>, <c>vocabulary</c>, or <c>null</c>.</summary>
    public string? Reason { get; }

    /// <summary>The seed paths whose assignment base the solved setup heap did not resolve to an object.</summary>
    public IReadOnlyList<string> UnreachedSeeds { get; private set; } = [];

    /// <summary>Whether an argument has an effect of a kind.</summary>
    /// <param name="parameter">The parameter name, or <c>this</c>.</param>
    /// <param name="kind"><c>reads-deep</c>, <c>writes-arg</c> or <c>writes-cells</c>.</param>
    public bool HasEffect(string parameter, string kind) =>
        Effects.TryGetValue(parameter, out var effects) && effects.Any(effect => effect.Kind == kind);

    private void BuildInputs()
    {
        foreach (var action in Actions())
        {
            var variant = action == DriverSynthesizer.CALL ? DriverSynthesizer.CALL_VARIANT : DriverSynthesizer.ENUMERATE_VARIANT;
            if (!_driver.Member.IsStatic && _driver.Member.MethodKind != MethodKind.Constructor)
                AddInput(FateClassifier.THIS, _driver.Member.ContainingType, RefKind.None, isReceiver: true, action, $"Recv_{variant}", []);

            foreach (var parameter in _driver.Member.Parameters)
            {
                var probes = _driver.Parameters.FirstOrDefault(candidate => candidate.Ordinal == parameter.Ordinal)?.Probes
                                    .Where(probe => probe.Variant == variant).ToArray() ?? [];
                var field = parameter.RefKind is RefKind.Ref or RefKind.Out ? $"Out_{parameter.Name}_{variant}"
                    : TypeShape.Of(parameter.Type) == TypeShapeKind.Delegate ? null
                    : $"Arg_{parameter.Name}_{variant}";
                AddInput(parameter.Name, parameter.Type, parameter.RefKind, isReceiver: false, action, field, probes);
            }
        }
    }

    private void AddInput(string name, ITypeSymbol type, RefKind refKind, bool isReceiver, string action, string? field,
                          IReadOnlyList<DriverProbe> probes)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        if (field is not null)
            roots.UnionWith(StaticTargets(DriverSynthesizer.DRIVER_TYPE, field));
        foreach (var factory in probes.Select(Allocations.ProbeBody))
        {
            roots.UnionWith(_heap.Regions.Values.Where(region => region.SiteBodyId == factory).Select(region => region.Identity));
        }

        var reached = _reachability.From(roots);
        var probeGraph = _reachability.From(reached.Where(region => IsProbeObject(region) || Origin(region).Role == DriverRole.ProbeDelegate));
        var ownProbeRoots = roots.Where(IsProbeObject).ToHashSet(StringComparer.Ordinal);
        _inputs.Add(new Input(name, type, refKind, isReceiver, action, roots, reached, probeGraph, ownProbeRoots));
    }

    private void BuildSpecialObjects()
    {
        var probeStarts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in _heap.Regions.Keys)
        {
            var origin = Origin(region);
            if (IsProbeObject(region) || origin.Role == DriverRole.ProbeDelegate)
                probeStarts.Add(region);
            if (origin.Role == DriverRole.ProbeLambdaReturn)
            {
                probeStarts.Add(region);
                _probeReturns.Add(region);
            }
            if (origin.Role == DriverRole.Witness)
                _witnessReturns.Add(region);
            if (IsProbeObject(region) || origin.Role is DriverRole.Seed or DriverRole.ProbeDelegate)
                _witnessObjects.Add(region);
        }

        _unknownSensitive.UnionWith(_reachability.From(probeStarts));
        BuildReceiverSeedObjects();
        CloseInPlace(_probeReturns);
        CloseInPlace(_witnessReturns);
    }

    /// <summary>The seeds setup placed in the receiver and every object below them: found from the receiver's roots without ever
    /// entering a probe or an argument's own object. A seed of an argument the member kept on the receiver is reached only through
    /// that argument, whose own effect the entry names. An object an argument also reaches stays the receiver's when a path from the
    /// receiver reaches it without the argument: the heap, which does not follow order, puts a seed setup stored below the receiver
    /// into the argument kept there too, and the read of it is still a read of what the receiver held (R9).</summary>
    private void BuildReceiverSeedObjects()
    {
        var arguments = _inputs.Where(input => !input.IsReceiver).SelectMany(input => input.Roots).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<(string Region, bool BelowSeed)>(_inputs.Where(input => input.IsReceiver).SelectMany(input => input.Roots)
                                                                         .Select(root => (root, false)));
        var seen = new HashSet<(string, bool)>();
        while (pending.TryPop(out var current))
        {
            if (arguments.Contains(current.Region) || IsProbeObject(current.Region) || !seen.Add(current))
                continue;
            var belowSeed = current.BelowSeed || Origin(current.Region).Role == DriverRole.Seed;
            if (belowSeed)
                _receiverSeedObjects.Add(current.Region);
            foreach (var (_, target) in _reachability.Edges(current.Region))
                pending.Push((target, belowSeed));
        }
    }

    private void ReadReasonsFromHandoffs()
    {
        if (_handoffs.HandedOutsideSetup.Any(_unknownSensitive.Contains) || _handoffs.ForeignAccesses.Any(_unknownSensitive.Contains))
            _unknownTouch = true;
        if (_handoffs.HandedOutsideSetup.Any(_witnessReturns.Contains) || _handoffs.ForeignAccesses.Any(_witnessReturns.Contains))
            _vocabulary = true;
    }

    private void ReadAccesses()
    {
        foreach (var access in _analysis.Accesses)
        {
            if (!_heap.Instances.TryGetValue(access.InstanceId, out var instance) || !IsLibraryBody(instance.BodyId))
                continue;
            if (access.Access.Kind == SummaryAccessKind.Load && OutsideSetup(access.ExecutionId) && MatchesUnseeded(access.Access.Field))
                _incomplete = true;
            if (ActionOf(access.ExecutionId) is not { } action)
                continue;

            if (IsIgnoredDriverStatic(access.Access.Field))
                continue;
            if (IsWitnessRef(access.Access.Field))
            {
                _vocabulary = true;
                continue;
            }

            if (access.Access.Kind == SummaryAccessKind.Load)
                ReadLoad(instance, access, action);
            else if (access.Access.Kind == SummaryAccessKind.Store)
                ReadStore(access, action);
        }
    }

    private void ReadLoad(MethodInstance instance, CollectedAccess access, string action)
    {
        if (_probeReturns.Contains(access.RegionId) || _witnessReturns.Contains(access.RegionId))
            _vocabulary = true;

        var enumeration = IsEnumerationRead(instance, access.Access.OperationId);
        foreach (var input in Inputs(action))
        {
            if (input.ProbeGraph.Contains(access.RegionId) || input.IsCollection && input.Roots.Contains(access.RegionId))
                AddEffect(input.Name, READS_DEEP, action, enumeration, [access.RegionId]);
        }
    }

    private void ReadStore(CollectedAccess access, string action) =>
        ReadStore(access.RegionId, access.Access.Field, access.Access.IsOnCollection && access.Access.Selector is not null, action,
                  _heap.Instances.TryGetValue(access.InstanceId, out var instance) &&
                  IsDelegateSlotStore(instance, access.Access, access.RegionId));

    /// <summary>Classifies one library store outside setup into the object it writes: an effect, a state store or a model reason.</summary>
    /// <param name="region">The object written into.</param>
    /// <param name="field">The field written.</param>
    /// <param name="cell">Whether the store writes a cell of a collection.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    /// <param name="delegateSlot">Whether <see cref="IsDelegateSlotStore"/> says the store is no state store.</param>
    private void ReadStore(string region, IrFieldRef field, bool cell, string action, bool delegateSlot = false)
    {
        if (_probeReturns.Contains(region) || _witnessReturns.Contains(region))
        {
            _vocabulary = true;
            return;
        }

        if (StructStorageStore(field, action) || delegateSlot)
            return;

        var handled = false;
        foreach (var input in Inputs(action))
        {
            if (input.IsArray && input.Roots.Contains(region) && cell)
            {
                AddEffect(input.Name, WRITES_CELLS, action, enumeration: false, []);
                handled = true;
                continue;
            }
            if (input.IsCollection && input.Roots.Contains(region))
            {
                _vocabulary = true;
                handled = true;
                continue;
            }
            if (!input.OwnProbeRoots.Contains(region))
            {
                if (input.Reached.Contains(region) && !input.Roots.Contains(region))
                {
                    if (input.IsReceiver)
                        AddStateStore(region, action);
                    else
                        _vocabulary = true;
                    handled = true;
                }
                continue;
            }

            if (input.IsReceiver && field.Assembly == _driver.Member.ContainingAssembly.Name)
                AddStateStore(region, action);
            else
                AddEffect(input.Name, WRITES_ARGUMENT, action, enumeration: false, []);
            handled = true;
        }

        if (!handled && !_witnessObjects.Contains(region) && !_allocations.CreatedByCall(region))
            AddStateStore(region, action);
    }

    /// <summary>Whether a store is the slot a handler waits in, which is no library state (TD-034a, task 11): a store into a
    /// delegate-typed field of the receiver or of a static, whose every object the heap says the value may point to is a probe of one
    /// of the member's delegate parameters or an object setup stored into that same field of that same object (its seed), and whose
    /// value may come from no origin the heap does not follow. <c>null</c> stores no object, and a removal keeps only its left
    /// operand's objects (R3). The heap may merge the seeds of one delegate type, so a value read from a field holds the slot's
    /// seed only when it reads that same field of that same object; a read of any other field qualifies only through probes. A read
    /// of the slot itself is checked object by object like any value: a delegate the library put there — an event initializer, a
    /// constructor's store — is no seed, so storing it back keeps the store a state store.
    /// <see cref="ReadStore(CollectedAccess, string)"/> and <see cref="ReadUnknownDelegateStateStores"/> both ask it before
    /// recording a state store.</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="store">The store.</param>
    /// <param name="region">The object written into.</param>
    private bool IsDelegateSlotStore(MethodInstance instance, SummaryAccess store, string region)
    {
        if (store.Kind != SummaryAccessKind.Store || !IsDelegateField(store.Field))
            return false;
        var slotObject = store.Field.IsStatic
            ? _heap.Regions.TryGetValue(region, out var held) && held.Kind == HeapRegionKind.Static
            : _inputs.Any(input => input.IsReceiver && input.Roots.Contains(region));
        if (!slotObject)
            return false;

        var producers = instance.Summary.Stores.Where(transfer => transfer.OperationId == store.OperationId)
                                .Select(transfer => transfer.Producers);
        if (_observation.HasUnobservedValues(instance, store.Values, ValueOrigin.Union(producers)))
            return false;
        var seeds = SetupSlotValues().GetValueOrDefault((FieldSlot.Key(store.Field), region));
        foreach (var value in store.Values)
        {
            var seedable = ReadsOwnSlot(instance, value, store.Field, region) || value is not (PathValue or StaticFieldValue);
            if (!_observation.Resolve(instance, value).All(stored => Origin(stored).Role == DriverRole.ProbeDelegate ||
                                                                     seedable && Origin(stored).Role == DriverRole.Seed &&
                                                                     seeds?.Contains(stored) == true))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a value is a read of the slot itself: the same static field, or the same field of no object but the one
    /// stored into.</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="value">One alternative of the stored value.</param>
    /// <param name="field">The field stored into.</param>
    /// <param name="region">The object stored into.</param>
    private bool ReadsOwnSlot(MethodInstance instance, AbstractValue value, IrFieldRef field, string region)
    {
        var slot = FieldSlot.Key(field);
        if (value is StaticFieldValue @static)
            return field.IsStatic && FieldSlot.Key(@static.Field) == slot;
        if (field.IsStatic || value is not PathValue { Segments: [.., var last] } path || last != slot)
            return false;
        var owner = path.Segments.Count == 1 ? path.Base : path with { Segments = path.Segments.Take(path.Segments.Count - 1).ToArray() };
        var owners = _observation.Resolve(instance, owner);
        return owners.Count != 0 && owners.All(candidate => candidate == region);
    }

    /// <summary>What setup stored into each delegate-typed field of each object, by the field's slot and the object: the seeds the
    /// slot holds before the call. A value setup read back from the same field is what the field held, not what setup put there, so
    /// it does not count.</summary>
    private Dictionary<(string Slot, string Region), HashSet<string>> SetupSlotValues()
    {
        if (_setupSlotValues is not null)
            return _setupSlotValues;
        _setupSlotValues = [];
        foreach (var access in _analysis.Accesses.Where(access => access.Access.Kind == SummaryAccessKind.Store && _executions.InSetup(access.ExecutionId)))
        {
            if (!IsDelegateField(access.Access.Field) || !_heap.Instances.TryGetValue(access.InstanceId, out var instance))
                continue;
            var key = (FieldSlot.Key(access.Access.Field), access.RegionId);
            if (!_setupSlotValues.TryGetValue(key, out var values))
                _setupSlotValues[key] = values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in access.Access.Values.Where(value => !ReadsField(value, access.Access.Field)))
                values.UnionWith(_observation.Resolve(instance, value));
        }
        return _setupSlotValues;
    }

    /// <summary>Whether a value is a read of a field's own slot: the same static field, or a path ending in it.</summary>
    /// <param name="value">The value.</param>
    /// <param name="field">The field.</param>
    private static bool ReadsField(AbstractValue value, IrFieldRef field) => value switch
    {
        StaticFieldValue @static => FieldSlot.Key(@static.Field) == FieldSlot.Key(field),
        PathValue { Segments: [.., var last] } => last == FieldSlot.Key(field),
        _ => false
    };

    /// <summary>Whether a field, or the storage of a field-like event, has a delegate type.</summary>
    /// <param name="field">The field.</param>
    private bool IsDelegateField(IrFieldRef field) =>
        FieldSymbols.TypeOf(field, _driver.Compilation) is { } type && TypeShape.Of(type) == TypeShapeKind.Delegate;

    /// <summary>A store into a field of a struct that sits in a field of an object, which the heap carries no region for: no
    /// collected access shows it, so it lands here (rule 2 of the run). A value that can hold a user object is kept where the heap
    /// cannot name it, R5's <c>vocabulary</c>; any other value is a write into the storage of the object that holds the struct.</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="access">The store.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private void ReadStructFieldStore(MethodInstance instance, SummaryAccess access, string action)
    {
        if (StructField(access.Field) is not { } field || StructOwners(instance, access) is not { Count: > 0 } owners)
            return;
        if (CanHoldUserObject(field.Type))
        {
            _vocabulary = true;
            return;
        }
        foreach (var owner in owners)
            ReadStore(owner, access.Field, cell: false, action);
    }

    /// <summary>The field a store writes, when it is declared in a struct.</summary>
    /// <param name="field">The stored field.</param>
    private IFieldSymbol? StructField(IrFieldRef field) =>
        FieldSymbols.Of(field, _driver.Compilation) is IFieldSymbol
        {
            ContainingType.IsValueType: true
        } symbol ? symbol : null;

    /// <summary>The objects holding the struct a store writes into, for a store whose path the heap resolves to no region: the last
    /// objects the path reaches before the struct field it stops at.</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="access">The store.</param>
    private IReadOnlySet<string> StructOwners(MethodInstance instance, SummaryAccess access)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in access.Bases.OfType<PathValue>().Where(path => _heap.Resolve(instance.Id, path).Count == 0))
        {
            IReadOnlySet<string> reached = _heap.Resolve(instance.Id, path.Base);
            foreach (var segment in path.Segments)
            {
                var next = reached.SelectMany(region => _heap.PointsTo(region, segment)).ToHashSet(StringComparer.Ordinal);
                if (next.Count == 0)
                    break;
                reached = next;
            }
            owners.UnionWith(reached);
        }
        return owners;
    }

    /// <summary>The objects a library store writes into: the regions its bases resolve to, for a field of a struct held in an
    /// object, that object, and for a static field, the static storage of its declaring type, which the collected accesses name
    /// as the object a static store writes into (task 11).</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="access">The store.</param>
    private IReadOnlySet<string> StoreTargets(MethodInstance instance, SummaryAccess access)
    {
        var targets = access.Bases.SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);
        if (StructField(access.Field) is not null)
            targets.UnionWith(StructOwners(instance, access));
        if (access.Field.IsStatic)
            targets.UnionWith(StaticStorage()[FieldSlot.TypeKey(access.Field.Assembly, access.Field.ContainingTypeId)]);
        return targets;
    }

    /// <summary>The static storage regions of the run, by their declaring type without type arguments.</summary>
    private ILookup<string, string> StaticStorage() =>
        _staticStorage ??= _heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey is not null)
                                .ToLookup(region => FieldSlot.WithoutTypeArguments(region.TypeKey!), region => region.Identity, StringComparer.Ordinal);

    /// <summary>The arrays a library instance's cell stores write into, directly or through a reference into a cell.</summary>
    /// <param name="instance">The library instance that stores.</param>
    private IReadOnlySet<string> CellStoreTargets(MethodInstance instance) =>
        instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores).Where(store => store.Kind == ElementOperationKind.Store)
                .SelectMany(store => store.Arrays).SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every object a library instance's own stores write into, whatever the kind of store: a field store, a cell store,
    /// and a store through a reference into a cell or into a field (R6). A field store <see cref="IsDelegateSlotStore"/> says is no
    /// state store writes no object here (task 11).</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="unobserved">Whether a store through a reference has no proven place.</param>
    private IReadOnlySet<string> StoreTargets(MethodInstance instance, out bool unobserved)
    {
        unobserved = false;
        var targets = instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store)
                              .SelectMany(access => StoreTargets(instance, access).Where(region => !IsDelegateSlotStore(instance, access, region)))
                              .ToHashSet(StringComparer.Ordinal);
        targets.UnionWith(CellStoreTargets(instance));
        foreach (var target in instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store)
                                       .SelectMany(access => access.Targets))
        {
            targets.UnionWith(ValueProvenance.ReferenceStorage(_heap, instance, target, out var unproven));
            unobserved |= unproven;
        }
        return targets;
    }

    private void ReadSummaryEvidence()
    {
        foreach (var instance in _heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId)))
        {
            var executions = _analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>();
            // A load of an unseeded member counts in every execution outside setup — an unknown or startup execution the call caused
            // included — whatever object it is on (task 6).
            if (executions.Any(OutsideSetup) &&
                instance.Summary.Accesses.Any(access => access.Kind == SummaryAccessKind.Load && MatchesUnseeded(access.Field)))
            {
                _incomplete = true;
            }
            var actions = executions.Select(ActionOf).Where(action => action is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            foreach (var action in actions)
            {
                foreach (var access in instance.Summary.Accesses)
                {
                    if (access.Kind == SummaryAccessKind.Store && !StructStorageStore(access.Field, action))
                        ReadStructFieldStore(instance, access, action);
                }

                foreach (var region in CellStoreTargets(instance))
                {
                    var inputs = Inputs(action).Where(input => input.Reached.Contains(region)).ToArray();
                    if (inputs.Any(input => input.IsArray && input.Roots.Contains(region)))
                        continue;
                    if (inputs.Any(input => !input.IsReceiver))
                        _vocabulary = true;
                    else if ((inputs.Any(input => input.IsReceiver) || _allocations.Of(region).Kind == AllocationKind.LibraryBefore) &&
                             !_allocations.CreatedByCall(region))
                        AddStateStore(region, action);
                }

                foreach (var access in instance.Summary.ReferenceAccesses)
                    ReadReferenceAccess(instance, access, action);
                foreach (var effect in instance.Summary.ArgumentEffects)
                    ReadArgumentEffect(instance, effect, action);
            }
        }
    }

    private void ReadUnknownDelegateStateStores()
    {
        var handoffs = _heap.DelegateHandoffs.Select(handoff => (Callers: handoff.Sites.Select(site => site.CallerInstance).ToArray(),
                                                                  Callees: (IEnumerable<string>)handoff.Callees))
                            .Concat(_heap.StartupDelegates.Select(handoff => (Callers: new[] { handoff.CallerInstance },
                                                                                Callees: (IEnumerable<string>)[handoff.CalleeInstance])));
        var calls = _heap.Edges.ToLookup(edge => edge.CallerInstance, edge => edge.CalleeInstance, StringComparer.Ordinal);
        foreach (var (callers, callees) in handoffs)
        {
            var actions = callers.SelectMany(caller => (_analysis.InstanceExecutions.GetValueOrDefault(caller) ?? new HashSet<string>())
                                                       .Select(ActionOf).Where(action => action is not null).Cast<string>())
                                 .Distinct(StringComparer.Ordinal).ToArray();
            if (actions.Length == 0)
                continue;
            // The execution the delegate runs in is every body it reaches, not only its own, and every kind of store it makes counts
            // (task 6, R6).
            foreach (var instanceId in Reached(callees, calls))
            {
                if (!_heap.Instances.TryGetValue(instanceId, out var instance) || !IsLibraryBody(instance.BodyId))
                    continue;
                var targets = StoreTargets(instance, out var unobserved);
                foreach (var region in targets)
                {
                    if (_allocations.CreatedByCall(region))
                        continue;
                    foreach (var action in actions)
                        AddStateStore(region, action);
                }
                if (unobserved)
                    _vocabulary = true;
            }
        }
    }

    /// <summary>The instances a set of start instances reaches through call edges, the starts included.</summary>
    /// <param name="starts">The start instances.</param>
    /// <param name="calls">The callees of each caller instance.</param>
    private static IReadOnlySet<string> Reached(IEnumerable<string> starts, ILookup<string, string> calls)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(starts);
        while (pending.TryPop(out var current))
        {
            if (!reached.Add(current))
                continue;
            foreach (var callee in calls[current])
                pending.Push(callee);
        }
        return reached;
    }

    private void ReadReferenceAccess(MethodInstance instance, SummaryReferenceAccess access, string action)
    {
        foreach (var target in access.Targets)
        {
            var arrays = ValueProvenance.ReferenceArrays(_heap, instance, target, out var unobserved);
            if (unobserved && access.Kind == SummaryAccessKind.Store)
                _vocabulary = true;
            foreach (var input in Inputs(action).Where(input => input.IsArray && input.Roots.Any(arrays.Contains)))
                AddEffect(input.Name, access.Kind == SummaryAccessKind.Store ? WRITES_CELLS : READS_DEEP, action, enumeration: false,
                          input.Roots.Where(arrays.Contains));
            switch (target)
            {
                case ReferenceParameter parameter when access.Kind == SummaryAccessKind.Store && Parameter(parameter.Ordinal) is
                { IsStruct: true, RefKind: RefKind.Ref or RefKind.Out or RefKind.In }:
                    _vocabulary = true;
                    break;
                case var reference when ReferencesWitness(instance, reference, []):
                    _vocabulary = true;
                    break;
            }
        }
    }

    private void ReadArgumentEffect(MethodInstance instance, SummaryArgumentEffect effect, string action)
    {
        var regions = effect.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);
        // A library call's read or write of what a user delegate or a witness returned is no form of the vocabulary, as a field's is (R4).
        if (regions.Any(region => _probeReturns.Contains(region) || _witnessReturns.Contains(region)))
            _vocabulary = true;
        var inputs = Inputs(action).Where(input => input.Reached.Any(regions.Contains)).ToArray();
        if (inputs.Length == 0)
            return;

        if (effect.Kind == IrLibraryEffectKind.Enumerate)
        {
            var enumeration = effect.Member is null || IsEnumeratingMember(effect.Member.Member);
            var reads = effect.Member is null || Reads(effect.Member.Structure) || Reads(effect.Member.Element);
            var writes = effect.Member is not null && (Writes(effect.Member.Structure) || Writes(effect.Member.Element));
            foreach (var input in inputs)
            {
                if (reads)
                    AddEffect(input.Name, READS_DEEP, action, enumeration, regions.Where(input.Reached.Contains));
                if (writes)
                    _vocabulary = true;
            }
            return;
        }

        foreach (var input in inputs)
        {
            switch (effect.Kind)
            {
                case IrLibraryEffectKind.DeepRead:
                    AddEffect(input.Name, READS_DEEP, action, enumeration: false, regions.Where(input.Reached.Contains));
                    break;
                case IrLibraryEffectKind.WriteArgument when input.OwnProbeRoots.Any(regions.Contains):
                    AddEffect(input.Name, WRITES_ARGUMENT, action, enumeration: false, []);
                    break;
                case IrLibraryEffectKind.WriteArgument:
                    _vocabulary = true;
                    break;
                case IrLibraryEffectKind.WriteCells when input.IsArray:
                    AddEffect(input.Name, WRITES_CELLS, action, enumeration: false, []);
                    break;
            }
        }
    }

    private bool StructStorageStore(IrFieldRef field, string action)
    {
        var matching = Inputs(action).Where(input => input.IsStruct && Declares(field, input.Type)).ToArray();
        if (matching.Length == 0)
            return false;
        foreach (var input in matching)
        {
            if (input.IsReceiver || input.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
                _vocabulary = true;
        }
        return true;
    }

    private void ReadWitnessesAndUnknownCalls()
    {
        foreach (var input in _inputs)
        {
            if (input.Reached.Any(_handoffs.WitnessedOutsideSetup.Contains))
                AddEffect(input.Name, READS_DEEP, input.Action, enumeration: false, input.Reached.Where(_handoffs.WitnessedOutsideSetup.Contains));
        }

        foreach (var regions in _handoffs.HandoffsOutsideSetup.Values)
        {
            if (!regions.Any(_witnessObjects.Contains))
                continue;
            foreach (var input in _inputs.Where(input => input.Reached.Any(regions.Contains)))
                AddEffect(input.Name, READS_DEEP, input.Action, enumeration: false, regions.Where(input.Reached.Contains));
        }
    }

    private void ReadUnreachedSeeds()
    {
        var setup = _heap.Instances.Values.Where(instance => instance.BodyId ==
            $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{DriverSynthesizer.SETUP}").ToArray();
        var paths = new List<string>();
        foreach (var statement in _driver.SeedStatements)
        {
            if (SeedField(statement.Operation) is not { } member)
                continue;
            // One statement at a time, by the store its own assignment makes: a reached seed of the same field elsewhere never
            // hides this one (task 6). A statement is reached only when that store is found: no store means unreached.
            var span = statement.Operation.Syntax.GetLocation().GetLineSpan();
            var candidates = SeedStores(setup, member, span).ToArray();
            if (candidates.Any(candidate => candidate.Access.Bases.Count == 0 ||
                candidate.Access.Bases.SelectMany(value => _heap.Resolve(candidate.Instance.Id, value))
                         .Any(region => !_allocations.CreatedByCall(region))))
            {
                continue;
            }

            paths.Add(statement.Path);
            _unreachedFields.Add(member);
        }
        UnreachedSeeds = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The stores of a seed statement into its field: a store setup makes inside the statement, or, for an auto-property
    /// the statement assigns through its setter, the setter instance's store into the backing field, and for a field-like event
    /// the statement subscribes to, the add accessor's compare-and-swap store into the event's storage.</summary>
    /// <param name="setup">The instances of the driver's setup body.</param>
    /// <param name="member">The field, auto-property or field-like event the statement seeds.</param>
    /// <param name="span">The statement's assignment.</param>
    private IEnumerable<(MethodInstance Instance, SummaryAccess Access)> SeedStores(IReadOnlyList<MethodInstance> setup, ISymbol member,
                                                                                   FileLinePositionSpan span)
    {
        foreach (var instance in setup)
        {
            foreach (var access in instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                                                                             Matches(access.Field, member) &&
                                                                             Within(access.Provenance.Span, span)))
            {
                yield return (instance, access);
            }

            if (!_runBodies.TryGetValue(instance.BodyId, out var body))
                continue;
            var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                            .Where(call => Within(call.Provenance.Span, span)).Select(call => call.Id).ToHashSet();
            foreach (var edge in _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && calls.Contains(edge.OperationId)))
            {
                if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                    continue;
                foreach (var access in callee.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                                                                               Matches(access.Field, member)))
                {
                    yield return (callee, access);
                }
            }
        }
    }

    /// <summary>Whether an IR span, one-based, lies inside a Roslyn line span, zero-based.</summary>
    /// <param name="inner">The IR operation's span.</param>
    /// <param name="outer">The seed statement's assignment.</param>
    private static bool Within(SourceSpan inner, FileLinePositionSpan outer) =>
        (inner.StartLine, inner.StartColumn).CompareTo((outer.StartLinePosition.Line + 1, outer.StartLinePosition.Character + 1)) >= 0 &&
        (inner.EndLine, inner.EndColumn).CompareTo((outer.EndLinePosition.Line + 1, outer.EndLinePosition.Character + 1)) <= 0;

    private bool IsEnumerationRead(MethodInstance instance, int operationId)
    {
        foreach (var effect in instance.Summary.ArgumentEffects.Where(effect => effect.OperationId == operationId &&
                                                                                 effect.Kind == IrLibraryEffectKind.Enumerate))
        {
            if (effect.Member is null || effect.Member.Member.EndsWith(".GetEnumerator", StringComparison.Ordinal) ||
                effect.Member.Member.EndsWith(".MoveNext", StringComparison.Ordinal) ||
                effect.Member.Member.EndsWith(".get_Current", StringComparison.Ordinal) ||
                effect.Member.Member.EndsWith(".CopyTo", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private bool MatchesUnseeded(IrFieldRef field) =>
        _unreachedFields.Concat(_driver.UnseededMembers).Any(member => Matches(field, member));

    /// <summary>Records one effect on an argument, and for a <c>reads-deep</c> of the receiver whether the objects it read include a
    /// seed setup placed in the receiver or an object reachable from one — the one place that answers it for every read.</summary>
    /// <param name="parameter">The parameter name, or <c>this</c>.</param>
    /// <param name="kind"><c>reads-deep</c>, <c>writes-arg</c> or <c>writes-cells</c>.</param>
    /// <param name="root"><c>V_Call</c> or <c>V_Enum</c>.</param>
    /// <param name="enumeration">Whether the access was an enumeration read.</param>
    /// <param name="read">The objects a <c>reads-deep</c> read; empty for a write.</param>
    private void AddEffect(string parameter, string kind, string root, bool enumeration, IEnumerable<string> read)
    {
        var key = (parameter, kind);
        if (!_effects.TryGetValue(key, out var observation))
            _effects[key] = observation = new Observation();
        if (kind == READS_DEEP && parameter == FateClassifier.THIS && read.Any(_receiverSeedObjects.Contains))
            observation.ReadsSeed = true;
        observation.Roots.Add(root);
        if (enumeration)
            observation.EnumerationRoots.Add(root);
        else
            observation.NonEnumerationRoots.Add(root);
    }

    private void AddStateStore(string region, string root)
    {
        if (!_stateStores.TryGetValue(region, out var roots))
            _stateStores[region] = roots = new HashSet<string>(StringComparer.Ordinal);
        roots.Add(root);
    }

    private IEnumerable<Input> Inputs(string action) => _inputs.Where(input => input.Action == action);

    private Input? Parameter(int ordinal) => _inputs.FirstOrDefault(input => !input.IsReceiver &&
        _driver.Member.Parameters.First(parameter => parameter.Name == input.Name).Ordinal == ordinal);

    private IEnumerable<string> Actions()
    {
        yield return DriverSynthesizer.CALL;
        if (_driver.Enumerates)
            yield return DriverSynthesizer.ENUMERATE;
    }

    private string? ActionOf(string execution)
    {
        var role = _executions.Of(execution);
        return role is
        {
            Role: DriverExecutionRole.Own or DriverExecutionRole.Child,
            Action: DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE
        }
            ? role.Action
            : null;
    }

    /// <summary>Whether an execution is outside setup: not in setup's tree and not the escape artefact, which <c>V_Enum</c> stands
    /// for.</summary>
    /// <param name="execution">The execution.</param>
    private bool OutsideSetup(string execution) =>
        _executions.Of(execution).Role is not (DriverExecutionRole.Setup or DriverExecutionRole.Artefact);

    private IReadOnlySet<string> StaticTargets(string type, string field)
    {
        var start = _reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field));
        return start is null ? new HashSet<string>(StringComparer.Ordinal) : _reachability.Targets(start);
    }

    private Allocation Origin(string region) => _origins.TryGetValue(region, out var origin)
        ? origin
        : _origins[region] = _allocations.Of(region);

    // Allocations owns the role: a probe class's object a witness returned is a witness's, whatever its class is called.
    private bool IsProbeObject(string region) => Origin(region).Role == DriverRole.ProbeObject;

    private bool IsLibraryBody(string body) => body.StartsWith($"body:{_driver.Member.ContainingAssembly.Name}:", StringComparison.Ordinal);

    private static bool IsIgnoredDriverStatic(IrFieldRef field) => field is
    {
        IsStatic: true,
        Assembly: DriverSynthesizer.ASSEMBLY,
        ContainingType: DriverSynthesizer.DRIVER_TYPE or DriverSynthesizer.KEEP_TYPE or DriverSynthesizer.WITNESSED_TYPE
    };

    private static bool IsWitnessRef(IrFieldRef field) => field.IsStatic && field.Assembly == DriverSynthesizer.ASSEMBLY &&
        field.ContainingType.StartsWith(DriverSynthesizer.WITNESSED_REF_TYPE, StringComparison.Ordinal);

    private bool ReferencesWitness(MethodInstance instance, ReferenceTarget target, HashSet<(string Instance, ReferenceTarget Target)> seen)
    {
        if (!seen.Add((instance.Id, target)))
            return false;
        if (target is ReferenceCell cell)
            return IsWitnessRef(cell.Field);
        if (target is not ReferenceCall call)
            return false;
        return _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId)
                    .Select(edge => _heap.Instances.GetValueOrDefault(edge.CalleeInstance))
                    .Where(callee => callee is not null)
                    .Any(callee => callee!.Summary.ReferenceReturns.Any(reference => ReferencesWitness(callee, reference, seen)));
    }

    private static bool CanHoldUserObject(ITypeSymbol type) => TypeShape.Of(type) is
        TypeShapeKind.Delegate or TypeShapeKind.TaskOfT or TypeShapeKind.StructWithReferences or TypeShapeKind.Reference;

    private static bool Reads(IrCollectionEffect effect) => effect is IrCollectionEffect.Read or IrCollectionEffect.ReadWrite;

    private static bool Writes(IrCollectionEffect effect) => effect is IrCollectionEffect.Write or IrCollectionEffect.ReadWrite;

    private static bool IsEnumeratingMember(string member) => member.EndsWith(".GetEnumerator", StringComparison.Ordinal) ||
        member.EndsWith(".MoveNext", StringComparison.Ordinal) || member.EndsWith(".get_Current", StringComparison.Ordinal) ||
        member.EndsWith(".CopyTo", StringComparison.Ordinal);

    private static ISymbol? SeedField(IOperation target) => target switch
    {
        ISimpleAssignmentOperation assignment => SeedField(assignment.Target),
        IEventAssignmentOperation subscription => SeedField(subscription.EventReference),
        IEventReferenceOperation @event => @event.Event,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        IConversionOperation conversion => SeedField(conversion.Operand),
        _ => null
    };

    private static bool Matches(IrFieldRef field, ISymbol member)
    {
        var names = member is IPropertySymbol ? new[] { member.Name, $"<{member.Name}>k__BackingField" } : new[] { member.Name };
        return field.Assembly == member.ContainingAssembly?.Name && names.Contains(field.Name, StringComparer.Ordinal) &&
               member.ContainingType is { } type && Declares(field, type);
    }

    private static bool Declares(IrFieldRef field, ITypeSymbol type)
    {
        var display = type.ToDisplayString();
        return field.ContainingTypeId == display || field.ContainingType == display ||
               field.ContainingType.EndsWith(type.Name, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> OrderRoots(IEnumerable<string> roots) =>
        roots.OrderBy(root => root == DriverSynthesizer.CALL ? 0 : 1).ThenBy(root => root, StringComparer.Ordinal).ToArray();

    private void CloseInPlace(HashSet<string> regions) => regions.UnionWith(_reachability.From(regions));

    private sealed record Input(string Name, ITypeSymbol Type, RefKind RefKind, bool IsReceiver, string Action,
                                IReadOnlySet<string> Roots, IReadOnlySet<string> Reached, IReadOnlySet<string> ProbeGraph,
                                IReadOnlySet<string> OwnProbeRoots)
    {
        public bool IsArray => Type is IArrayTypeSymbol;
        public bool IsCollection => IsArray || Type is INamedTypeSymbol named && SeedableFields.Collection(named) is not null;
        public bool IsStruct => Type.TypeKind == TypeKind.Struct && TypeShape.Of(Type) is TypeShapeKind.PlainStruct or TypeShapeKind.StructWithReferences;
    }

    private sealed class Observation
    {
        public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EnumerationRoots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> NonEnumerationRoots { get; } = new(StringComparer.Ordinal);
        public bool ReadsSeed { get; set; }

        public GeneratedEffect ToEffect(string kind) =>
            new(kind, OrderRoots(Roots), OrderRoots(EnumerationRoots), OrderRoots(NonEnumerationRoots), ReadsSeed);
    }
}
