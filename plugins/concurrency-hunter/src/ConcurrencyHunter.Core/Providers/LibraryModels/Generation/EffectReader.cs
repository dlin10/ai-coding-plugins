using ConcurrencyHunter.Accesses;
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
/// <param name="ReadsUserObject">Whether a <c>reads-deep</c> of the receiver read a user object it holds — a seed setup placed in it,
/// standing for a user object a program stored there, or a probe its construction was given — or an object reachable from one.</param>
public sealed record GeneratedEffect(string Kind, IReadOnlyList<string> Roots, IReadOnlyList<string> EnumerationRoots,
                                     IReadOnlyList<string> NonEnumerationRoots, bool ReadsUserObject);

/// <summary>A pre-existing library region the member stored into, with the roots in whose trees it did so.</summary>
/// <param name="Region">The stored-into region.</param>
/// <param name="Roots"><c>V_Call</c> and/or <c>V_Enum</c>, and, for a static handed to unseen code in no action's tree, the executions
/// it was handed in.</param>
public sealed record StateStore(string Region, IReadOnlyList<string> Roots);

/// <summary>Reads effects, state stores and refusal evidence from one completed driver run. Only accesses made by bodies of the
/// implementation assembly in the trees of <c>V_Call</c> and <c>V_Enum</c> count.</summary>
public sealed class EffectReader
{
    public const string READS_DEEP = "reads-deep";
    public const string WRITES_ARGUMENT = "writes-arg";
    public const string WRITES_CELLS = "writes-cells";

    /// <summary>The element write of an array as a member of the table names it, directly or through an interface.</summary>
    private const string ARRAY_ELEMENT_WRITE = "System.Array.set_Item";

    private readonly Driver _driver;
    private readonly IReadOnlyDictionary<string, IrBody> _runBodies;
    private readonly HeapSolution _heap;
    private readonly ExecutionAnalysis _analysis;
    private readonly DriverExecutions _executions;
    private readonly HeapReachability _reachability;
    private readonly IReadOnlySet<string> _immutableTypeKeys;
    private readonly Allocations _allocations;
    private readonly GenerationHandoffs _handoffs;
    private readonly ValueObservation _observation;
    private readonly Dictionary<string, Allocation> _origins = new(StringComparer.Ordinal);
    private Dictionary<(string Slot, string Region), HashSet<string>>? _setupSlotValues;
    private ILookup<string, string>? _staticStorage;
    private HashSet<string>? _constructedStructs;
    private readonly Dictionary<(string Parameter, string Kind), Observation> _effects = [];
    private readonly Dictionary<string, HashSet<string>> _stateStores = new(StringComparer.Ordinal);
    private readonly List<Input> _inputs = [];
    private readonly HashSet<string> _probeReturns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _witnessReturns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownSensitive = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputSensitive = new(StringComparer.Ordinal);
    private readonly HashSet<string> _witnessObjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receiverUserObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Instance, string Action, IReadOnlySet<AbstractValue> Values), AccessSide> _sides = [];
    private readonly Dictionary<(string Witness, string Action), AccessSide> _witnessSides = [];
    private ILookup<string, CallEdge>? _callers;
    private readonly Dictionary<string, IReadOnlySet<string>> _otherReceivers = new(StringComparer.Ordinal);
    private readonly HashSet<ISymbol> _unreachedFields = new(SymbolEqualityComparer.Default);
    private readonly GenerationCauses _causes = new();
    private readonly GenerationChecks _checks;
    private readonly StageChecks _stage;

    /// <summary>Reads one completed driver run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="fates">The classified fates, or <c>null</c>: a parameter whose fate is <c>not-run</c> has no effect (R5).</param>
    public EffectReader(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate>? fates = null)
        : this(driver, run, fates, new GenerationChecks(CancellationToken.None))
    {
    }

    /// <summary>Reads one completed driver run as a stage of a generation: it checks between its readings and at each region,
    /// access, instance and handoff they go through.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="fates">The classified fates, or <c>null</c>: a parameter whose fate is <c>not-run</c> has no effect (R5).</param>
    /// <param name="checks">The generation's checks, which cancel the reading.</param>
    internal EffectReader(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate>? fates, GenerationChecks checks)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no effects to read.", nameof(run));

        _checks = checks;
        _stage = new StageChecks(checks, GenerationStage.ReadEffects);
        _driver = driver;
        _runBodies = run.Reachable.Bodies;
        _heap = run.Heap!;
        _analysis = run.Executions!;
        _reachability = new HeapReachability(_heap, null, _stage) { HiddenSlots = HeapReachability.HiddenSlotsOf(driver.Member.ContainingAssembly) };
        _immutableTypeKeys = run.ScopeProgram!.Program.ImmutableTypeKeys;
        var result = _reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        _executions = new DriverExecutions(driver, _analysis, _reachability.From(result is null ? [] : _reachability.Targets(result)));
        _allocations = new Allocations(driver, _heap, _analysis, _executions, _reachability, _stage);
        _handoffs = new GenerationHandoffs(driver, run, _executions, _reachability, _stage);
        _observation = new ValueObservation(driver, run, _stage.Each(GenerationHandoffs.UnfollowedCalls(run))
                                                               .Select(call => (call.Instance.Id, call.OperationId)).ToHashSet(), _stage);

        BuildInputs();
        BuildSpecialObjects();
        _checks.Check(GenerationStage.ReadEffects);
        ReadUnreachedSeeds();
        _checks.Check(GenerationStage.ReadEffects);
        ReadReasonsFromHandoffs();
        _checks.Check(GenerationStage.ReadEffects);
        ReadAccesses();
        _checks.Check(GenerationStage.ReadEffects);
        ReadSummaryEvidence();
        _checks.Check(GenerationStage.ReadEffects);
        ReadUnknownDelegateStateStores();
        _checks.Check(GenerationStage.ReadEffects);
        ReadStaticsHandedToUnseen();
        _checks.Check(GenerationStage.ReadEffects);
        ReadWitnessesAndUnknownCalls();
        _checks.Check(GenerationStage.ReadEffects);

        Effects = _effects.Where(pair => fates?.GetValueOrDefault(pair.Key.Parameter)?.Fate != FateClassifier.NOT_RUN)
                          .OrderBy(pair => pair.Key.Parameter, StringComparer.Ordinal).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
                          .GroupBy(pair => pair.Key.Parameter, StringComparer.Ordinal)
                          .ToDictionary(group => group.Key,
                                        group => (IReadOnlyList<GeneratedEffect>)group.Select(pair => pair.Value.ToEffect(pair.Key.Kind)).ToArray(),
                                        StringComparer.Ordinal);
        StateStores = _stateStores.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .Select(pair => new StateStore(pair.Key, OrderRoots(pair.Value))).ToArray();
        Causes = _causes.All;
        Reason = _causes.Reason;
    }

    /// <summary>The effects by parameter name; the receiver is named <c>this</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GeneratedEffect>> Effects { get; }

    /// <summary>Stores into library state that may have existed before the call.</summary>
    public IReadOnlyList<StateStore> StateStores { get; }

    /// <summary>The first applicable model reason: <c>unknown-touch</c>, <c>incomplete</c>, <c>vocabulary</c>, or <c>null</c>.</summary>
    public string? Reason { get; }

    /// <summary>Every case found that gives one of those reasons, one per code, sorted by code.</summary>
    public IReadOnlyList<GenerationCause> Causes { get; }

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
            _checks.Check(GenerationStage.ReadEffects);
            var variant = action == DriverSynthesizer.CALL ? DriverSynthesizer.CALL_VARIANT : DriverSynthesizer.ENUMERATE_VARIANT;
            if (!_driver.Member.IsStatic && _driver.Member.MethodKind != MethodKind.Constructor)
                AddInput(FateClassifier.THIS, _driver.Member.ContainingType, RefKind.None, isReceiver: true, action, $"Recv_{variant}", []);

            foreach (var parameter in _driver.Member.Parameters)
            {
                _checks.Check(GenerationStage.ReadEffects);
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
        // What the input itself is: what its field points to and the probes made for it. A struct kept in the field has no object of its
        // own (TD-041): what the reference fields of its storage hold is reached from it, never it.
        var own = new HashSet<string>(StringComparer.Ordinal);
        if (field is not null)
        {
            roots.UnionWith(StaticTargets(DriverSynthesizer.DRIVER_TYPE, field));
            own.UnionWith(OwnStaticTargets(DriverSynthesizer.DRIVER_TYPE, field));
        }
        foreach (var factory in _stage.Each(probes.Select(Allocations.ProbeBody)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var made = _stage.Each(_heap.Regions.Values).Where(region => region.SiteBodyId == factory).Select(region => region.Identity).ToArray();
            roots.UnionWith(made);
            own.UnionWith(made);
        }

        var reached = _reachability.From(roots);
        var probeGraph = _reachability.From(_stage.Each(reached).Where(region => IsProbeObject(region) || Origin(region).Role == DriverRole.ProbeDelegate));
        var ownProbeRoots = _stage.Each(own).Where(IsProbeObject).ToHashSet(StringComparer.Ordinal);
        _inputs.Add(new Input(name, type, refKind, isReceiver, action, field, roots, reached, probeGraph, ownProbeRoots) { Own = own });
    }

    private void BuildSpecialObjects()
    {
        var probeStarts = new HashSet<string>(StringComparer.Ordinal);
        var inputStarts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in _heap.Regions.Keys)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var origin = Origin(region);
            if (IsProbeObject(region) || origin.Role == DriverRole.ProbeDelegate)
                probeStarts.Add(region);
            if (origin.Role is DriverRole.ArgumentValue or DriverRole.Seed or DriverRole.OutInitialValue)
                inputStarts.Add(region);
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

        // What code the analysis does not see may write in an argument, the receiver, a seed or what setup built into them, as it may in
        // a probe, and no model can say whether it did: only an immutable object is safe in its hands, whatever holds it (questions 161
        // and 142). An access the analysis does see — in another execution, or in the body of a delegate the code was handed — is judged
        // by what it does.
        _unknownSensitive.UnionWith(_stage.Each(_reachability.From(probeStarts)).Where(Mutable));
        _inputSensitive.UnionWith(_stage.Each(_reachability.From(inputStarts)).Where(Mutable));
        BuildReceiverUserObjects();
        CloseInPlace(_probeReturns);
        CloseInPlace(_witnessReturns);
    }

    /// <summary>The user objects the receiver holds — a seed setup placed in it, a probe its construction was given — and every object
    /// below them, which decide an access whose own path does not say whose it is (<see cref="ReadsReceiverSeed"/>): found from the
    /// receiver's roots without ever entering an argument's own object. The receiver itself is the library's object, even as the driver's
    /// subclass. A probe or seed of an argument the member kept on the receiver is reached only through that argument, whose own effect
    /// the entry names. An object an argument also reaches stays the receiver's when a path from the receiver reaches it without the
    /// argument: the heap, which does not follow order, puts a seed setup stored below the receiver into the argument kept there too, and
    /// only an access whose every alternative starts at the argument is the argument's alone (<see cref="SideOf"/>, R7).</summary>
    private void BuildReceiverUserObjects()
    {
        var arguments = _inputs.Where(input => !input.IsReceiver).SelectMany(input => input.Roots).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<(string Region, bool BelowUser)>(_inputs.Where(input => input.IsReceiver).SelectMany(input => input.Roots)
                                                                         .Select(root => (root, false)));
        var seen = new HashSet<(string, bool)>();
        while (pending.TryPop(out var current))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (arguments.Contains(current.Region) || !seen.Add(current))
                continue;
            var belowUser = current.BelowUser || Origin(current.Region).Role == DriverRole.Seed || IsUserProbe(current.Region);
            if (belowUser)
                _receiverUserObjects.Add(current.Region);
            foreach (var (_, target) in _reachability.Edges(current.Region))
            {
                _checks.Check(GenerationStage.ReadEffects);
                pending.Push((target, belowUser));
            }
        }
    }

    /// <summary>Whether code may write a region: it is of no immutable type TD-034a names (<c>Type</c>, <c>string</c>, <c>Version</c>).</summary>
    /// <param name="region">The region.</param>
    private bool Mutable(string region) => _heap.Regions[region].TypeKey is not { } type || !_immutableTypeKeys.Contains(type);

    /// <summary>Whether a region is an instance of a probe class, a user object; the driver's subclass of a library type is not.</summary>
    /// <param name="region">The region.</param>
    private bool IsUserProbe(string region) =>
        IsProbeObject(region) && _heap.Regions.TryGetValue(region, out var held) &&
        held.TypeKey?.StartsWith($"{DriverSynthesizer.ASSEMBLY}:Probe_", StringComparison.Ordinal) == true;

    private void ReadReasonsFromHandoffs()
    {
        var onlyUnsupported = _handoffs.HandedOnlyToUnsupportedOutsideSetup;
        var onlyUnsupportedDirectly = _handoffs.HandedOnlyToUnsupportedOutsideSetupDirectly;
        if ((_handoffs.HandedOutsideSetup.FirstOrDefault(region => _unknownSensitive.Contains(region) && !onlyUnsupported.Contains(region)) ??
             _handoffs.HandedOutsideSetupDirectly.FirstOrDefault(region => _inputSensitive.Contains(region) && !onlyUnsupportedDirectly.Contains(region)))
            is { } handed)
        {
            _causes.Add(GenerationReasons.UNKNOWN_TOUCH, ModelCauses.PROBE_HANDED_TO_UNSEEN, Handed(handed));
        }
        // A probe only operations the lowering does not express were given is a case of its own: their lowering lifts it.
        if ((onlyUnsupported.FirstOrDefault(_unknownSensitive.Contains) ?? onlyUnsupportedDirectly.FirstOrDefault(_inputSensitive.Contains)) is { } given)
            _causes.Add(GenerationReasons.UNKNOWN_TOUCH, ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION, Handed(given));
        if (_handoffs.ForeignAccesses.FirstOrDefault(_unknownSensitive.Contains) is { } accessed)
            _causes.Add(GenerationReasons.UNKNOWN_TOUCH, ModelCauses.PROBE_ACCESSED_ELSEWHERE, accessed);
        if (_handoffs.UnfollowedReceiverCalls.FirstOrDefault() is { } unfollowed)
            _causes.Add(GenerationReasons.UNKNOWN_TOUCH, ModelCauses.CALL_RECEIVER_UNFOLLOWED, unfollowed);
        ReadUnseenMembers();
        if (_handoffs.HandedOutsideSetup.FirstOrDefault(_witnessReturns.Contains) is { } witnessHanded)
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.WITNESS_VALUE_HANDED, Handed(witnessHanded));
        if (_handoffs.ForeignAccesses.FirstOrDefault(_witnessReturns.Contains) is { } witnessAccessed)
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.WITNESS_VALUE_ACCESSED_ELSEWHERE, witnessAccessed);
    }

    /// <summary>Records the first member of an object an argument reaches, and the first of an object the receiver reaches, that a
    /// witness stood for outside setup, other than one the analysis takes to read, in words: the input and the witness's body. It is
    /// code the analysis does not see, however it is called — a member of the argument itself, of a seed in its field, or a seeded
    /// delegate's invoke; for the receiver, an override of the driver's subclass, which stands for a user override and for the
    /// library's own body alike, or a seed's member. An object both reach is the argument's.</summary>
    private void ReadUnseenMembers()
    {
        foreach (var (instanceId, receivers) in _handoffs.UnseenMembersOutsideSetup)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var input = _inputs.FirstOrDefault(input => !input.IsReceiver && receivers.Any(input.Reached.Contains)) ??
                        _inputs.FirstOrDefault(input => input.IsReceiver && receivers.Any(input.Reached.Contains));
            if (input is not null)
                _causes.Add(GenerationReasons.UNKNOWN_TOUCH, input.IsReceiver ? ModelCauses.RECEIVER_MEMBER_UNSEEN : ModelCauses.ARGUMENT_MEMBER_UNSEEN,
                            $"{input.Name}: {_heap.Instances[instanceId].BodyId}");
        }
    }

    /// <summary>A region handed outside setup, in words: with the callee and the place of the first call that handed it — or, for a
    /// region only operations the lowering does not express were handed, the kind and place of the first of them —, or alone when it was
    /// handed with a delegate rather than by one call.</summary>
    /// <param name="region">The region handed.</param>
    private string Handed(string region)
    {
        var unsupported = _handoffs.HandedOnlyToUnsupportedOutsideSetup.Contains(region);
        foreach (var ((instanceId, operation), regions) in _handoffs.HandoffsOutsideSetup)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!regions.Contains(region))
                continue;
            if (!_heap.Instances.TryGetValue(instanceId, out var instance))
                return $"{region} by {instanceId}#{operation}";
            var kind = instance.Summary.UnsupportedOperations.FirstOrDefault(given => given.OperationId == operation)?.OperationKind;
            if ((kind is not null) != unsupported)
                continue;
            if (kind is not null)
                return $"{region} to unsupported {kind} in {instance.BodyId}#{operation}";
            var callee = instance.Summary.Calls.FirstOrDefault(call => call.OperationId == operation)?.Callee ??
                         instance.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == operation)?.Callee ?? "a call";
            return $"{region} to {callee} in {instance.BodyId}#{operation}";
        }
        return region;
    }

    private void ReadAccesses()
    {
        foreach (var access in _analysis.ExecutionAccesses())
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!_heap.Instances.TryGetValue(access.InstanceId, out var instance) || !IsLibraryBody(instance.BodyId))
                continue;
            if (access.Access.Kind == SummaryAccessKind.Load && OutsideSetup(access.ExecutionId) && MatchesUnseeded(access.Access.Field))
                _causes.Add(GenerationReasons.INCOMPLETE, ModelCauses.UNSEEDED_READ, $"{FieldSlot.Key(access.Access.Field)} in {instance.BodyId}");
            if (ActionOf(access.ExecutionId) is not { } action)
                continue;

            if (IsIgnoredDriverStatic(access.Access.Field) || IsOtherReceiversAccess(instance, access, action))
                continue;
            if (IsWitnessRef(access.Access.Field) || IsWitnessRefStorage(access.RegionId, access.Slot))
            {
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.WITNESS_REFERENCE, $"{FieldSlot.Key(access.Access.Field)} in {instance.BodyId}");
                continue;
            }

            if (access.Access.Kind == SummaryAccessKind.Load)
                ReadLoad(instance, access, action);
            else if (access.Access.Kind == SummaryAccessKind.Store)
                ReadStore(access, action);
        }
    }

    /// <summary>Whether an access the member's entry makes through its own <c>this</c> lands on another action's receiver. The heap may merge
    /// the entry's contexts, and then every receiver the driver passes it is its <c>this</c>; but when the action's own body is the only
    /// caller entering it in the action, its <c>this</c> there is the action's receiver alone, and an object the driver made as another
    /// action's receiver is no object of the call (R8).</summary>
    /// <param name="instance">The library instance that accesses.</param>
    /// <param name="access">The access.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private bool IsOtherReceiversAccess(MethodInstance instance, ExecutionAccess access, string action)
    {
        if (access.Access.Bases.Count == 0 || !access.Access.Bases.All(@base => @base is ConcurrencyHunter.Heap.ThisValue) ||
            !OtherReceivers(action).Contains(access.RegionId))
        {
            return false;
        }
        var callers = _stage.Each(_heap.Edges).Where(edge => edge.CalleeInstance == instance.Id &&
                                                             _analysis.InstanceExecutions.GetValueOrDefault(edge.CallerInstance)?
                                                                      .Any(execution => ActionOf(execution) == action) == true)
                            .Select(edge => _heap.Instances[edge.CallerInstance].BodyId).ToArray();
        return callers.Length != 0 && callers.All(body => body == ValueObservation.DriverBody(action));
    }

    /// <summary>The objects the driver made as the receiver of an action or a trigger other than <paramref name="action"/>.</summary>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private IReadOnlySet<string> OtherReceivers(string action)
    {
        if (_otherReceivers.TryGetValue(action, out var known))
            return known;
        var prefix = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE, "Recv_");
        var own = Inputs(action).Where(input => input.IsReceiver).SelectMany(input => input.Own).ToHashSet(StringComparer.Ordinal);
        return _otherReceivers[action] = _stage.Each(_reachability.StaticFields()).Where(field => field.Slot.StartsWith(prefix, StringComparison.Ordinal))
                                               .SelectMany(_reachability.Targets).Where(region => !own.Contains(region))
                                               .ToHashSet(StringComparer.Ordinal);
    }

    private void ReadLoad(MethodInstance instance, ExecutionAccess access, string action)
    {
        if (_probeReturns.Contains(access.RegionId) || _witnessReturns.Contains(access.RegionId))
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.CALLBACK_VALUE_READ,
                        $"{FieldSlot.Key(access.Access.Field)} of {access.RegionId} in {instance.BodyId}");
        }

        var enumeration = IsEnumerationRead(instance, access.Access.OperationId);
        var side = SideOf(instance, access.Access.Bases, action);
        foreach (var input in Inputs(action).Where(input => Takes(input, side)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (input.ProbeGraph.Contains(access.RegionId) || input.IsCollection && input.Roots.Contains(access.RegionId))
                AddEffect(input.Name, READS_DEEP, action, enumeration, [access.RegionId], side);
        }
    }

    // A field of a struct kept in a cell is a write into that cell, as the store of the whole cell is (R7).
    // bounded: overload forwarding terminates at the implementation overload.
    private void ReadStore(ExecutionAccess access, string action)
    {
        var instance = _heap.Instances.GetValueOrDefault(access.InstanceId);
        ReadStore(access.RegionId, access.Slot, access.Access.Field,
                  access.Access.IsOnCollection && access.Access.Selector is not null || FieldSlot.Root(access.Slot) == PathValue.ELEMENT, action,
                  instance is not null && IsDelegateSlotStore(instance, access.Access, access.RegionId),
                  instance is null ? AccessSide.Region : SideOf(instance, access.Access.Bases, action));
    }

    /// <summary>Classifies one library store outside setup into the object it writes: an effect, a state store or a model reason.</summary>
    /// <param name="region">The object written into: the holder of a struct's storage, never the storage itself.</param>
    /// <param name="slot">The slot of <paramref name="region"/> written: the field's, or the path through every struct field to it.</param>
    /// <param name="field">The field written.</param>
    /// <param name="cell">Whether the store writes a cell of a collection.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    /// <param name="delegateSlot">Whether <see cref="IsDelegateSlotStore"/> says the store is no state store.</param>
    /// <param name="side">Whose store its own path says it is (<see cref="SideOf"/>); <see cref="AccessSide.Region"/> for a store
    /// through a reference, which has none.</param>
    private void ReadStore(string region, string slot, IrFieldRef field, bool cell, string action, bool delegateSlot = false,
                           AccessSide side = AccessSide.Region)
    {
        if (_probeReturns.Contains(region) || _witnessReturns.Contains(region))
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.CALLBACK_VALUE_WRITTEN, $"{FieldSlot.Key(field)} of {region}");
            return;
        }

        if (StructStorageStore(region, slot, action) || delegateSlot)
            return;

        var handled = false;
        foreach (var input in Inputs(action).Where(input => Takes(input, side)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (input.IsArray && input.Roots.Contains(region) && cell)
            {
                AddEffect(input.Name, WRITES_CELLS, action, enumeration: false, []);
                handled = true;
                continue;
            }
            if (input.IsCollection && input.Roots.Contains(region))
            {
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.COLLECTION_ARGUMENT_STORE, $"{input.Name}: {FieldSlot.Key(field)}");
                handled = true;
                continue;
            }
            if (!input.OwnProbeRoots.Contains(region))
            {
                // An object the call made is no part of what the input held before it, however the input reaches it now (R6).
                if (input.Reached.Contains(region) && !input.Own.Contains(region) && !_allocations.CreatedByCall(region))
                {
                    // A user object the receiver holds is no state of its own: a write into it has no form (TD-034b).
                    if (input.IsReceiver && ReadsReceiverSeed(side, [region]))
                        _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RECEIVER_OBJECT_WRITE, $"{FieldSlot.Key(field)} of {region}");
                    else if (input.IsReceiver)
                        AddStateStore(region, action);
                    else
                        _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.ARGUMENT_GRAPH_WRITE, $"{input.Name}: {FieldSlot.Key(field)} of {region}");
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

        // A seed no input reaches is a user object the library's own state holds, a static's: a write into it writes that state (R4).
        if (!handled && (!_witnessObjects.Contains(region) || Origin(region).Role == DriverRole.Seed))
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
    /// <see cref="ReadStore(ExecutionAccess, string)"/> and <see cref="ReadUnknownDelegateStateStores"/> both ask it before
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
            : _inputs.Any(input => input.IsReceiver && input.Own.Contains(region));
        if (!slotObject)
            return false;

        var producers = instance.Summary.Stores.Where(transfer => transfer.OperationId == store.OperationId)
                                .Select(transfer => transfer.Producers);
        if (_observation.HasUnobservedValues(instance, store.Values, ValueOrigin.Union(producers)))
            return false;
        var seeds = SetupSlotValues().GetValueOrDefault((FieldSlot.Key(store.Field), region));
        foreach (var value in store.Values)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var seedable = ReadsOwnSlot(instance, value, store, region) || value is not (PathValue or StaticFieldValue);
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
    /// stored into — of the body's own <c>this</c> when the store writes it there, as one run of a body has one <c>this</c> however
    /// many receivers the heap merges into its instance (R8).</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="value">One alternative of the stored value.</param>
    /// <param name="store">The store.</param>
    /// <param name="region">The object stored into.</param>
    private bool ReadsOwnSlot(MethodInstance instance, AbstractValue value, SummaryAccess store, string region)
    {
        var field = store.Field;
        var slot = FieldSlot.Key(field);
        if (value is StaticFieldValue @static)
            return field.IsStatic && FieldSlot.Key(@static.Field) == slot;
        if (field.IsStatic || value is not PathValue { Segments: [.., var last] } path || last != slot)
            return false;
        var owner = path.Segments.Count == 1 ? path.Base : path with { Segments = path.Segments.Take(path.Segments.Count - 1).ToArray() };
        if (owner is ConcurrencyHunter.Heap.ThisValue && store.Bases.Count != 0 && store.Bases.All(@base => @base is ConcurrencyHunter.Heap.ThisValue))
            return true;
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
        foreach (var access in _analysis.Accesses.Where(access => access.Access.Kind == SummaryAccessKind.Store && access.Executions.Any(_executions.InSetup)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!IsDelegateField(access.Access.Field) || !_heap.Instances.TryGetValue(access.InstanceId, out var instance))
                continue;
            var key = (FieldSlot.Key(access.Access.Field), access.RegionId);
            if (!_setupSlotValues.TryGetValue(key, out var values))
                _setupSlotValues[key] = values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in access.Access.Values.Where(value => !ReadsField(value, access.Access.Field)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                values.UnionWith(_observation.SetupValues(instance, value, _executions.InSetup));
            }
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

    /// <summary>The places a library store writes, each an object and its slot, as the collected accesses name them (R1): the regions its
    /// bases resolve to, a struct's storage standing as its holder with the path through every struct field, and for a static field, the
    /// static storage of its declaring type (task 11). A field of a struct copy is no place at all (R2).</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="access">The store.</param>
    private IReadOnlySet<(string Region, string Slot)> StoreTargets(MethodInstance instance, SummaryAccess access)
    {
        var slot = StructStorage.IsWholeStorage(access.Field) ? "" : FieldSlot.Key(access.Field);
        var targets = access.Bases.SelectMany(value => _heap.Resolve(instance.Id, value))
                            .Select(region => _heap.Located(region, slot)).OfType<(string Region, string Slot)>().ToHashSet();
        if (access.Field.IsStatic)
            targets.UnionWith(StaticStorage()[FieldSlot.TypeKey(access.Field.Assembly, access.Field.ContainingTypeId)].Select(region => (region, slot)));
        return targets;
    }

    /// <summary>The places a library instance's stores of a whole struct through a reference write — <c>x = w</c> for a <c>ref</c> local,
    /// a <c>ref</c> parameter or a <c>ref</c> return — each a holder and the slot of the struct's storage in it (R1): where the copy the store
    /// makes goes. A copy that goes into a struct held by value is no place (R2).</summary>
    /// <param name="instance">The library instance that stores.</param>
    private IEnumerable<(string Region, string Slot, IrFieldRef Field)> ReferenceStructStores(MethodInstance instance)
    {
        var stores = instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store).Select(access => access.OperationId)
                             .ToHashSet();
        foreach (var storage in instance.Summary.StructCopies.Where(copy => stores.Contains(copy.OperationId)).SelectMany(copy => copy.Targets)
                                        .SelectMany(value => _heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (_heap.Located(storage, "") is ({ } region, { Length: > 0 } slot))
                yield return (region, slot, StructStorage.WholeStorage(_heap.Regions[storage].TypeKey ?? ""));
        }
    }

    /// <summary>The fields a library instance's stores through a reference write, other than a whole struct's (<see cref="ReferenceStructStores"/>),
    /// each the object the field lies in, its slot and the field: where a store through an <c>out</c> or <c>ref</c> parameter, a <c>ref</c>
    /// local or a <c>ref</c> return lands, as the same store made directly lands (R1).</summary>
    /// <param name="instance">The library instance that stores.</param>
    private IEnumerable<(string Region, string Slot, IrFieldRef Field)> ReferenceFieldStores(MethodInstance instance)
    {
        var copies = instance.Summary.StructCopies.Select(copy => copy.OperationId).ToHashSet();
        return instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store && !copies.Contains(access.OperationId))
                       .SelectMany(access => access.Targets).SelectMany(target => ValueProvenance.ReferenceFields(_heap, instance, target))
                       .Where(place => !IsIgnoredDriverStatic(place.Field)).Distinct();
    }

    /// <summary>The slots a library instance's stores write in a struct the driver's action constructs (<see cref="ConstructedStructs"/>):
    /// a field store on it or on a struct's storage within it, and a whole struct stored through a reference to it, as <c>this = …</c> in a
    /// constructor does; empty for the whole storage.</summary>
    /// <param name="instance">The library instance that stores.</param>
    private IEnumerable<string> ConstructedStructStores(MethodInstance instance)
    {
        var constructed = ConstructedStructs();
        if (constructed.Count == 0)
            yield break;
        foreach (var access in instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var slot = StructStorage.IsWholeStorage(access.Field) ? "" : FieldSlot.Key(access.Field);
            foreach (var region in access.Bases.SelectMany(value => _heap.Resolve(instance.Id, value)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (_heap.Located(region, slot, copies: true) is ({ } holder, var written) && constructed.Contains(holder))
                    yield return written;
            }
        }
        var stores = instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store).Select(access => access.OperationId)
                             .ToHashSet();
        foreach (var storage in instance.Summary.StructCopies.Where(copy => stores.Contains(copy.OperationId)).SelectMany(copy => copy.Targets)
                                        .SelectMany(value => _heap.Resolve(instance.Id, value)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (_heap.Located(storage, "", copies: true) is ({ } holder, var written) && constructed.Contains(holder))
                yield return written;
        }
    }

    /// <summary>The structs the driver's actions construct: the copy each call they make runs on, which is the value <c>new S(…)</c> makes
    /// for a struct constructor (R2) and stands for the storage the caller constructs into. Every other call of an action runs on the
    /// driver's static storage of the receiver, which is no copy.</summary>
    private HashSet<string> ConstructedStructs()
    {
        if (_constructedStructs is not null)
            return _constructedStructs;
        var actions = new[] { DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE }
                      .Select(action => $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{action}").ToHashSet(StringComparer.Ordinal);
        var callers = _heap.Instances.Values.Where(instance => actions.Contains(instance.BodyId)).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        return _constructedStructs = _heap.Edges.Where(edge => callers.Contains(edge.CallerInstance))
                                          .SelectMany(edge => _heap.Resolve(edge.CalleeInstance, global::ConcurrencyHunter.Heap.ThisValue.Instance))
                                          .Where(_reachability.IsCopy)
                                          .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The static storage regions of the run, by their declaring type without type arguments.</summary>
    private ILookup<string, string> StaticStorage() =>
        _staticStorage ??= _heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey is not null)
                                .ToLookup(region => FieldSlot.WithoutTypeArguments(region.TypeKey!), region => region.Identity, StringComparer.Ordinal);

    /// <summary>The arrays a library instance's cell stores write into, directly or through a reference into a cell. What an interface
    /// call's site keeps on the objects it decides (<see cref="PathValue.IsDecided"/>) is the heap's record, which no code writes.</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="slot">The storage the stores write, such as the cells; null for every storage, a dictionary's keys among them.</param>
    private IReadOnlySet<string> CellStoreTargets(MethodInstance instance, string? slot = null) =>
        instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                .Where(store => store.Kind == ElementOperationKind.Store && !PathValue.IsDecided(store.Slot) && (slot is null || store.Slot == slot))
                .SelectMany(store => store.Arrays).SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every object a library instance's own stores write into, whatever the kind of store: a field store, a cell store,
    /// and a store through a reference into a cell or into a field (R6), with the full field slot from ValueProvenance.ReferenceFields;
    /// null only where the reference projection names no field. ReferenceFieldStores uses the same projection. A
    /// field store <see cref="IsDelegateSlotStore"/> says is no state store writes no object here (task 11).</summary>
    /// <param name="instance">The library instance that stores.</param>
    /// <param name="unobserved">Whether a store through a reference has no proven place.</param>
    private IReadOnlySet<(string Region, string? Slot)> StoreTargets(MethodInstance instance, out bool unobserved)
    {
        _checks.Check(GenerationStage.ReadEffects);
        unobserved = false;
        var targets = instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store)
                              .SelectMany(access => StoreTargets(instance, access).Where(place => !IsDelegateSlotStore(instance, access, place.Region)))
                              .Select(place => (place.Region, (string?)place.Slot))
                              .ToHashSet();
        targets.UnionWith(ReferenceStructStores(instance).Select(place => (place.Region, (string?)place.Slot)));
        targets.UnionWith(CellStoreTargets(instance).Select(region => (region, (string?)null)));
        foreach (var target in instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store)
                                       .SelectMany(access => access.Targets))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var fields = ValueProvenance.ReferenceFields(_heap, instance, target);
            targets.UnionWith(fields.Select(place => (place.Region, (string?)place.Slot)));
            targets.UnionWith(ValueProvenance.ReferenceStorage(_heap, instance, target, out var unproven)
                                            .Where(region => fields.All(place => place.Region != region)).Select(region => (region, (string?)null)));
            unobserved |= unproven;
        }
        return targets;
    }

    private void ReadSummaryEvidence()
    {
        foreach (var instance in _heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var executions = _analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>();
            // A load of an unseeded member counts in every execution outside setup — an unknown or startup execution the call caused
            // included — whatever object it is on (task 6).
            if (executions.Any(OutsideSetup) &&
                instance.Summary.Accesses.FirstOrDefault(access => access.Kind == SummaryAccessKind.Load && MatchesUnseeded(access.Field)) is { } unseeded)
            {
                _causes.Add(GenerationReasons.INCOMPLETE, ModelCauses.UNSEEDED_READ, $"{FieldSlot.Key(unseeded.Field)} in {instance.BodyId}");
            }
            var actions = executions.Select(ActionOf).Where(action => action is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            // A store into the struct the driver's action itself makes with `new S(...)` — what a struct constructor runs on — writes the
            // storage the caller constructs into, the struct receiver's own, R5's vocabulary, by whatever route it reaches it (R7).
            if (actions.Length != 0 && ConstructedStructStores(instance).FirstOrDefault() is { } constructed)
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.STRUCT_STORAGE_WRITE, $"this: {constructed} in {instance.BodyId}");
            foreach (var action in actions)
            {
                _checks.Check(GenerationStage.ReadEffects);
                // A whole struct stored through a reference writes the storage it reaches, as the same store made directly does (R7).
                foreach (var (region, slot, field) in ReferenceStructStores(instance))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    ReadStore(region, slot, field, FieldSlot.Root(slot) == PathValue.ELEMENT, action);
                }
                // So does any other store through a reference into a field — an `out` or `ref` helper's, a `ref` local's (R1).
                foreach (var (region, slot, field) in ReferenceFieldStores(instance))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    ReadStore(region, slot, field, cell: false, action);
                }

                var cells = CellStoreTargets(instance, PathValue.ELEMENT);
                foreach (var region in CellStoreTargets(instance))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var inputs = Inputs(action).Where(input => input.Reached.Contains(region)).ToArray();
                    var arrays = inputs.Where(input => input.IsArray && input.Roots.Contains(region)).ToArray();
                    if (arrays.Length != 0)
                    {
                        // A store into the cells of an array argument writes its cells, whatever way the array came to the store: a merge,
                        // a capture, a cell of another collection, a helper's result (question 34).
                        if (cells.Contains(region))
                        {
                            foreach (var array in arrays)
                            {
                                _checks.Check(GenerationStage.ReadEffects);
                                AddEffect(array.Name, WRITES_CELLS, action, enumeration: false, []);
                            }
                        }
                        continue;
                    }
                    if (inputs.FirstOrDefault(input => !input.IsReceiver) is { } argument)
                        _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.ARGUMENT_GRAPH_CELLS, $"{argument.Name}: {region} in {instance.BodyId}");
                    else if (inputs.Any(input => input.IsReceiver) || _allocations.Of(region).Kind == AllocationKind.LibraryBefore)
                        AddStateStore(region, action);
                }

                foreach (var access in instance.Summary.ReferenceAccesses)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    ReadReferenceAccess(instance, access, action);
                }
                foreach (var effect in instance.Summary.ArgumentEffects)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    ReadArgumentEffect(instance, effect, action);
                }
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
            _checks.Check(GenerationStage.ReadEffects);
            var actions = callers.SelectMany(caller => (_analysis.InstanceExecutions.GetValueOrDefault(caller) ?? new HashSet<string>())
                                                       .Select(ActionOf).Where(action => action is not null).Cast<string>())
                                 .Distinct(StringComparer.Ordinal).ToArray();
            if (actions.Length == 0)
                continue;
            // The execution the delegate runs in is every body it reaches, not only its own, and every kind of store it makes counts
            // (task 6, R6).
            foreach (var instanceId in Reached(callees, calls))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!_heap.Instances.TryGetValue(instanceId, out var instance) || !IsLibraryBody(instance.BodyId))
                    continue;
                var targets = StoreTargets(instance, out var unobserved);
                foreach (var (region, slot) in targets)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    foreach (var action in actions.Where(action => slot is null || !StructStorageStore(region, slot, action)))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        AddStateStore(region, action);
                    }
                }
                if (unobserved)
                {
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.UNPROVEN_REFERENCE_STORE,
                                $"in {instance.BodyId}, reached by a delegate handed off");
                }
            }
        }
    }

    /// <summary>Records a static field of the library's assembly that code the analysis does not see gets by reference outside setup — as
    /// the receiver or a <c>ref</c>, <c>in</c> or <c>out</c> argument of a call the analysis does not follow or of a call without a resolved
    /// target (<see cref="GenerationHandoffs.UnseenCalls"/>), in any execution <see cref="GenerationHandoffs.ExecutionsOutsideSetup"/> names:
    /// the call's or the enumeration's tree, an unknown execution, a type initializer, a lazy construction — as a store into the static
    /// storage of the field's type, which a store the analysis sees there is too (R1): that code may write it. It is recorded in the
    /// action whose tree the execution is in, else in the execution itself. The field is found through what the receiver is —
    /// a struct's storage, whose holder is the static storage, or the static storage itself — and through every static field a reference
    /// names (<see cref="ValueProvenance.ReferenceStatics"/>), which a reference to a scalar or a reference field names without any
    /// region of the heap. The driver's own statics, which it hands by reference to the member and its triggers, are no library state,
    /// and a static's value handed by value is no reference to it.</summary>
    private void ReadStaticsHandedToUnseen()
    {
        var library = _driver.Member.ContainingAssembly.Name;
        foreach (var call in _handoffs.UnseenCalls)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var roots = _handoffs.ExecutionsOutsideSetup(call.Instance.Id).Select(execution => ActionOf(execution) ?? execution)
                                 .Distinct(StringComparer.Ordinal).ToArray();
            if (roots.Length == 0)
                continue;
            var received = _stage.Each(call.Receivers).SelectMany(value => _heap.Resolve(call.Instance.Id, value))
                                 .Select(region => _heap.Located(region, "")?.Region ?? region).Where(IsLibraryStatic)
                                 .Concat(_stage.Each(call.References).SelectMany(target => ValueProvenance.ReferenceStatics(_heap, call.Instance, target))
                                               .Where(field => field.Assembly == library)
                                               .SelectMany(field => FieldSlot.TypeKey(field.Assembly, field.ContainingTypeId) is var type &&
                                                                    StaticStorage()[type].ToArray() is { Length: > 0 } regions
                                                                        ? regions
                                                                        : [$"static:{type}"]));
            foreach (var region in received.Distinct(StringComparer.Ordinal).ToArray())
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var root in roots)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddStateStore(region, root);
                }
            }
        }
    }

    /// <summary>Whether a region is the static storage of a type of the library's assembly.</summary>
    /// <param name="region">The region.</param>
    private bool IsLibraryStatic(string region) =>
        _heap.Regions.TryGetValue(region, out var held) && held.Kind == HeapRegionKind.Static &&
        held.TypeKey?.StartsWith($"{_driver.Member.ContainingAssembly.Name}:", StringComparison.Ordinal) == true;

    /// <summary>The instances a set of start instances reaches through call edges, the starts included. It checks at each instance
    /// it takes off its stack.</summary>
    /// <param name="starts">The start instances.</param>
    /// <param name="calls">The callees of each caller instance.</param>
    private IReadOnlySet<string> Reached(IEnumerable<string> starts, ILookup<string, string> calls)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(starts);
        while (pending.TryPop(out var current))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!reached.Add(current))
                continue;
            foreach (var callee in _stage.Each(calls[current]))
            {
                _checks.Check(GenerationStage.ReadEffects);
                pending.Push(callee);
            }
        }
        return reached;
    }

    private void ReadReferenceAccess(MethodInstance instance, SummaryReferenceAccess access, string action)
    {
        foreach (var target in access.Targets)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var arrays = ValueProvenance.ReferenceArrays(_heap, instance, target, out var unobserved);
            if (unobserved && access.Kind == SummaryAccessKind.Store)
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.UNPROVEN_REFERENCE_STORE, $"in {instance.BodyId}#{access.OperationId}");
            foreach (var input in Inputs(action).Where(input => input.IsArray && input.Roots.Any(arrays.Contains)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                AddEffect(input.Name, access.Kind == SummaryAccessKind.Store ? WRITES_CELLS : READS_DEEP, action, enumeration: false,
                          input.Roots.Where(arrays.Contains));
            }
            switch (target)
            {
                case ReferenceParameter parameter when access.Kind == SummaryAccessKind.Store && Parameter(parameter.Ordinal) is
                { IsStruct: true, RefKind: RefKind.Ref or RefKind.Out or RefKind.In } written:
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.STRUCT_REFERENCE_WRITE,
                                $"{written.Name} in {instance.BodyId}#{access.OperationId}");
                    break;
                case var reference when ReferencesWitness(instance, reference, []):
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.WITNESS_REFERENCE, $"in {instance.BodyId}#{access.OperationId}");
                    break;
            }
        }
    }

    private void ReadArgumentEffect(MethodInstance instance, SummaryArgumentEffect effect, string action)
    {
        var regions = effect.Values.SelectMany(value => _heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);
        // A library call's read or write of what a user delegate or a witness returned is no form of the vocabulary, as a field's is (R4).
        if (regions.FirstOrDefault(region => _probeReturns.Contains(region) || _witnessReturns.Contains(region)) is { } returned)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.CALLBACK_VALUE_IN_LIBRARY_CALL,
                        $"{effect.Kind} of {returned} by {instance.BodyId}#{effect.OperationId}");
        }
        // A read's own path is the value's before the heap resolves it: it says whose read it is (R7).
        var side = effect.Kind is IrLibraryEffectKind.DeepRead or IrLibraryEffectKind.Enumerate ? SideOf(instance, effect.Values, action) : AccessSide.Region;
        var inputs = Inputs(action).Where(input => Takes(input, side) && input.Reached.Any(regions.Contains)).ToArray();
        if (inputs.Length == 0)
            return;

        if (effect.Kind == IrLibraryEffectKind.Enumerate)
        {
            var enumeration = effect.Member is null || IsEnumeratingMember(effect.Member.Member);
            var reads = effect.Member is null || Reads(effect.Member.Structure) || Reads(effect.Member.Element);
            // An element write of an array is the store into its cells, which writes them as the same store on the array directly does.
            var writes = effect.Member is not null && (Writes(effect.Member.Structure) || Writes(effect.Member.Element)) &&
                         !WritesArrayCellsOnly(effect, regions);
            foreach (var input in inputs)
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (reads)
                    AddEffect(input.Name, READS_DEEP, action, enumeration, regions.Where(input.Reached.Contains), side);
                if (writes)
                {
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.ENUMERATION_WRITE,
                                $"{input.Name}: {effect.Member!.Member} by {instance.BodyId}#{effect.OperationId}");
                }
            }
            return;
        }

        foreach (var input in inputs)
        {
            _checks.Check(GenerationStage.ReadEffects);
            switch (effect.Kind)
            {
                case IrLibraryEffectKind.DeepRead:
                    AddEffect(input.Name, READS_DEEP, action, enumeration: false, regions.Where(input.Reached.Contains), side);
                    break;
                case IrLibraryEffectKind.WriteArgument when input.OwnProbeRoots.Any(regions.Contains):
                    AddEffect(input.Name, WRITES_ARGUMENT, action, enumeration: false, []);
                    break;
                case IrLibraryEffectKind.WriteArgument:
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.ARGUMENT_GRAPH_WRITE,
                                $"{input.Name}: {effect.Kind} by {instance.BodyId}#{effect.OperationId}");
                    break;
                case IrLibraryEffectKind.WriteCells when input.IsArray:
                    AddEffect(input.Name, WRITES_CELLS, action, enumeration: false, []);
                    break;
            }
        }
    }

    /// <summary>Whether a member of the table is, on every object it may be, the element write of an array and nothing else: its own
    /// member, or for a call through an interface the member each object's kind decides it as.</summary>
    /// <param name="effect">The member's effect.</param>
    /// <param name="regions">The objects it may be on.</param>
    private bool WritesArrayCellsOnly(SummaryArgumentEffect effect, IReadOnlySet<string> regions) =>
        regions.Count != 0 && regions.All(region =>
            (effect.Implementations.Count == 0
                ? effect.Member
                : CollectionObjects.Decision(effect.Implementations, CollectionObjects.KindOfType(_heap.Regions[region].TypeKey))?.Member) is
            { Member: ARRAY_ELEMENT_WRITE, Structure: IrCollectionEffect.None });

    /// <summary>Whether a store writes the caller's storage of a struct input: the driver's own static storage of the struct receiver or of
    /// a struct argument, whatever field of the struct it reaches and by whatever route. The receiver's and what a <c>ref</c>, <c>out</c>
    /// or <c>in</c> parameter refers to are written where the vocabulary has no name for it, R5's <c>vocabulary</c>; a by-value argument's
    /// storage the member never reaches, since it has a copy. Decided by the storage the write reaches, never by the struct's type (R7).</summary>
    /// <param name="region">The object written into, a struct's storage standing as its holder.</param>
    /// <param name="slot">The slot of <paramref name="region"/> written.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private bool StructStorageStore(string region, string slot, string action)
    {
        if (region != $"static:{FieldSlot.TypeKey(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE)}")
            return false;
        var root = FieldSlot.Root(slot);
        var input = Inputs(action).FirstOrDefault(input => input is { IsStruct: true, Field: { } field } &&
                                                           root == HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE, field));
        if (input is null)
            return false;
        if (StructStorage.IsStorage(OperandOf(input)))
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.STRUCT_STORAGE_WRITE, $"{input.Name}: {slot}");
        return true;
    }

    /// <summary>What names a struct input in the member's body, in the terms <see cref="StructStorage"/> decides on: the receiver, a
    /// <c>ref</c> or <c>out</c> parameter, a read-only reference, or a by-value parameter.</summary>
    /// <param name="input">The input.</param>
    private static StructOperand OperandOf(Input input) =>
        input.IsReceiver ? StructOperand.Receiver
        : input.RefKind is RefKind.Ref or RefKind.Out ? StructOperand.ReferenceParameter
        : input.RefKind != RefKind.None ? StructOperand.ReadOnlyReference
        : StructOperand.Parameter;

    private void ReadWitnessesAndUnknownCalls()
    {
        foreach (var (witness, regions) in _handoffs.WitnessesOutsideSetup)
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var input in _inputs)
            {
                _checks.Check(GenerationStage.ReadEffects);
                var side = WitnessSide(witness, input.Action);
                if (Takes(input, side) && input.Reached.Any(regions.Contains))
                    AddEffect(input.Name, READS_DEEP, input.Action, enumeration: false, input.Reached.Where(regions.Contains), side);
            }
        }

        foreach (var regions in _handoffs.HandoffsOutsideSetup.Values)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!regions.Any(_witnessObjects.Contains))
                continue;
            foreach (var input in _inputs.Where(input => input.Reached.Any(regions.Contains)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                AddEffect(input.Name, READS_DEEP, input.Action, enumeration: false, regions.Where(input.Reached.Contains));
            }
        }
    }

    private void ReadUnreachedSeeds()
    {
        var setup = _stage.Each(_heap.Instances.Values).Where(instance => instance.BodyId ==
            $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{DriverSynthesizer.SETUP}").ToArray();
        var paths = new List<string>();
        foreach (var statement in _driver.SeedStatements)
        {
            _checks.Check(GenerationStage.ReadEffects);
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
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var access in instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                                                                             Matches(access.Field, member) &&
                                                                             Within(access.Provenance.Span, span)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                yield return (instance, access);
            }

            if (!_runBodies.TryGetValue(instance.BodyId, out var body))
                continue;
            var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                            .Where(call => Within(call.Provenance.Span, span)).Select(call => call.Id).ToHashSet();
            foreach (var edge in _stage.Each(_heap.Edges).Where(edge => edge.CallerInstance == instance.Id && calls.Contains(edge.OperationId)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                    continue;
                foreach (var access in callee.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                                                                               Matches(access.Field, member)))
                {
                    _checks.Check(GenerationStage.ReadEffects);
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
            _checks.Check(GenerationStage.ReadEffects);
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

    /// <summary>Records one effect on an argument, and for a <c>reads-deep</c> of the receiver whether it read a user object the
    /// receiver holds or an object reachable from one (<see cref="ReadsReceiverSeed"/>).</summary>
    /// <param name="parameter">The parameter name, or <c>this</c>.</param>
    /// <param name="kind"><c>reads-deep</c>, <c>writes-arg</c> or <c>writes-cells</c>.</param>
    /// <param name="root"><c>V_Call</c> or <c>V_Enum</c>.</param>
    /// <param name="enumeration">Whether the access was an enumeration read.</param>
    /// <param name="read">The objects a <c>reads-deep</c> read; empty for a write.</param>
    /// <param name="side">Whose read its own path says it is; <see cref="AccessSide.Region"/> for one without a path.</param>
    private void AddEffect(string parameter, string kind, string root, bool enumeration, IEnumerable<string> read,
                           AccessSide side = AccessSide.Region)
    {
        var key = (parameter, kind);
        if (!_effects.TryGetValue(key, out var observation))
            _effects[key] = observation = new Observation();
        if (kind == READS_DEEP && parameter == FateClassifier.THIS && ReadsReceiverSeed(side, read))
            observation.ReadsUserObject = true;
        observation.Roots.Add(root);
        if (enumeration)
            observation.EnumerationRoots.Add(root);
        else
            observation.NonEnumerationRoots.Add(root);
    }

    /// <summary>Whether an access is to a user object the receiver holds — a seed setup placed in it, a probe its construction was given —
    /// or an object below one: the one place that answers it for every read and write of the receiver (SPEC "Fields and seeds", R7). An
    /// access with a path of its own — a load, a store, a value the body hands to a known call or to a witness — is decided by that path
    /// (<see cref="SideOf"/>, <see cref="WitnessSide"/>), so a region the heap lets the receiver's seed and an argument share changes no
    /// access's owner; one whose path does not say — an alternative from each side, a parameter of a helper body the receiver's seed was
    /// handed to — and one without a path — a handoff, a store through a reference — by the objects it reaches
    /// (<see cref="BuildReceiverUserObjects"/>), so the receiver's read of what it held stays.</summary>
    /// <param name="side">Whose access the access's own path says it is.</param>
    /// <param name="regions">The objects the access reaches.</param>
    private bool ReadsReceiverSeed(AccessSide side, IEnumerable<string> regions) => side switch
    {
        AccessSide.Receiver => true,
        AccessSide.Argument => false,
        _ => regions.Any(_receiverUserObjects.Contains)
    };

    /// <summary>Whether an input makes an access whose own path says whose it is: the receiver only one on a path from the receiver, an
    /// argument only one on a path from an argument, every input one without a path.</summary>
    /// <param name="input">The input.</param>
    /// <param name="side">Whose access the access's own path says it is.</param>
    private static bool Takes(Input input, AccessSide side) => side == AccessSide.Region || input.IsReceiver == (side == AccessSide.Receiver);

    /// <summary>Whose access an access's own path says it is (R7): <see cref="AccessSide.Receiver"/> when every alternative of it starts
    /// at the receiver and passes a field setup seeded or a probe its construction was given, <see cref="AccessSide.Argument"/> when every
    /// alternative starts at an argument's own object, else <see cref="AccessSide.Region"/>, which the objects it reaches decide.</summary>
    /// <param name="instance">The library instance that accesses.</param>
    /// <param name="values">The access's bases, or the value a known call is handed, before the heap resolves them.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private AccessSide SideOf(MethodInstance instance, IReadOnlySet<AbstractValue> values, string action)
    {
        var key = (instance.Id, action, values);
        if (_sides.TryGetValue(key, out var known))
            return known;
        var sides = _stage.Each(values).Select(value => PathSide(instance, value, action)).Distinct().ToArray();
        return _sides[key] = sides is [var only] ? only : AccessSide.Region;
    }

    /// <summary>Whose access one alternative of an access's path says it is (<see cref="SideOf"/>).</summary>
    /// <param name="instance">The library instance that accesses.</param>
    /// <param name="value">The alternative.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private AccessSide PathSide(MethodInstance instance, AbstractValue value, string action)
    {
        var start = value is PathValue path ? path.Base : value;
        var segments = value is PathValue { Segments: var followed } ? followed : [];
        var starts = _heap.Resolve(instance.Id, start);
        if (starts.Count == 0)
            return AccessSide.Region;
        if (starts.All(region => Inputs(action).Any(input => !input.IsReceiver && input.Own.Contains(region))))
            return AccessSide.Argument;
        if (!starts.All(region => Inputs(action).Any(input => input.IsReceiver && input.Own.Contains(region))))
            return AccessSide.Region;
        for (var count = 1; count <= segments.Count; count++)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (_heap.Resolve(instance.Id, new PathValue(start, segments.Take(count).ToArray()))
                     .Any(region => Origin(region).Role == DriverRole.Seed || IsUserProbe(region)))
            {
                return AccessSide.Receiver;
            }
        }
        return AccessSide.Region;
    }

    /// <summary>Whose read a witness's is (R7): a witness has no access of its own, but each call that entered it hands it values with
    /// paths of their own — its receiver and its arguments. When every alternative of every such value says the same side
    /// (<see cref="PathSide"/>), that is the witness's; else, or when no call says, <see cref="AccessSide.Region"/>.</summary>
    /// <param name="witness">The witness instance.</param>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    private AccessSide WitnessSide(string witness, string action)
    {
        if (_witnessSides.TryGetValue((witness, action), out var known))
            return known;
        _callers ??= _stage.Each(_heap.Edges).ToLookup(edge => edge.CalleeInstance, StringComparer.Ordinal);
        var sides = new HashSet<AccessSide>();
        foreach (var edge in _callers[witness])
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!_heap.Instances.TryGetValue(edge.CallerInstance, out var caller))
            {
                sides.Add(AccessSide.Region);
                continue;
            }
            foreach (var call in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId))
            {
                _checks.Check(GenerationStage.ReadEffects);
                sides.UnionWith(_stage.Each(call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values)))
                                      .Select(value => PathSide(caller, value, action)));
            }
        }
        return _witnessSides[(witness, action)] = sides is { Count: 1 } ? sides.Single() : AccessSide.Region;
    }

    /// <summary>Records a store into library state that existed before the call. Every route that finds a state store records it
    /// here, so this is where the rule is decided: an object the call made is no such state, whoever reaches it (R6).</summary>
    /// <param name="region">The object written into.</param>
    /// <param name="root"><c>V_Call</c> or <c>V_Enum</c>; for a static handed to unseen code in no action's tree, the execution
    /// (<see cref="ReadStaticsHandedToUnseen"/>).</param>
    private void AddStateStore(string region, string root)
    {
        if (_allocations.CreatedByCall(region))
            return;
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

    private IReadOnlySet<string> StaticTargets(string type, string field) =>
        _reachability.Held(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field));

    /// <summary>What a driver's static field itself points to, without what the storage of a struct kept in it holds.</summary>
    /// <param name="type">The driver's type.</param>
    /// <param name="field">The field.</param>
    private IReadOnlySet<string> OwnStaticTargets(string type, string field) =>
        _reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field)) is { } start
            ? _reachability.Targets(start)
            : new HashSet<string>(StringComparer.Ordinal);

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

    /// <summary>Whether an access writes inside the storage of a struct a witness's reference static holds: the access names the struct's
    /// field, and only its storage says where the struct is.</summary>
    /// <param name="region">The region the access is on.</param>
    /// <param name="slot">The slot of the region it names.</param>
    private static bool IsWitnessRefStorage(string region, string slot) =>
        slot.Contains(FieldSlot.PATH_SEPARATOR) &&
        region.StartsWith($"static:{FieldSlot.TypeKey(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.WITNESSED_REF_TYPE)}", StringComparison.Ordinal);

    private bool ReferencesWitness(MethodInstance instance, ReferenceTarget target, HashSet<(string Instance, ReferenceTarget Target)> seen)
    {
        _checks.Check(GenerationStage.ReadEffects);
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

    private static bool Reads(IrCollectionEffect effect) => effect is IrCollectionEffect.Read or IrCollectionEffect.ReadWrite;

    private static bool Writes(IrCollectionEffect effect) => effect is IrCollectionEffect.Write or IrCollectionEffect.ReadWrite;

    private static bool IsEnumeratingMember(string member) => member.EndsWith(".GetEnumerator", StringComparison.Ordinal) ||
        member.EndsWith(".MoveNext", StringComparison.Ordinal) || member.EndsWith(".get_Current", StringComparison.Ordinal) ||
        member.EndsWith(".CopyTo", StringComparison.Ordinal);

    private static ISymbol? SeedField(IOperation target)
    {
        WorkCancellation.Check();
        return target switch
        {
            ISimpleAssignmentOperation assignment => SeedField(assignment.Target),
            IEventAssignmentOperation subscription => SeedField(subscription.EventReference),
            IEventReferenceOperation @event => @event.Event,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            IConversionOperation conversion => SeedField(conversion.Operand),
            _ => null
        };
    }

    private static bool Matches(IrFieldRef field, ISymbol member)
    {
        var names = member is IPropertySymbol ? new[] { member.Name, $"<{member.Name}>k__BackingField" } : new[] { member.Name };
        return field.Assembly == member.ContainingAssembly?.Name && names.Contains(field.Name, StringComparer.Ordinal) &&
               member.ContainingType is { } type && Declares(field, type);
    }

    /// <summary>Whether a field an access names is declared by the type, or by its definition: a body of a generic type names the
    /// field of the definition (<c>ValueTuple&lt;T1, …&gt;.Item1</c>), and setup the field of the construction it built.</summary>
    /// <param name="field">The field the access names.</param>
    /// <param name="type">The type declaring the unseeded member.</param>
    private static bool Declares(IrFieldRef field, ITypeSymbol type)
    {
        var display = type.ToDisplayString();
        var definition = type.OriginalDefinition.ToDisplayString();
        return field.ContainingTypeId == display || field.ContainingType == display || field.ContainingType == definition ||
               field.ContainingType.EndsWith(type.Name, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> OrderRoots(IEnumerable<string> roots) =>
        roots.OrderBy(root => root == DriverSynthesizer.CALL ? 0 : 1).ThenBy(root => root, StringComparer.Ordinal).ToArray();

    private void CloseInPlace(HashSet<string> regions) => regions.UnionWith(_stage.Each(_reachability.From(regions)));

    /// <summary>One input of one action: the receiver or an argument, with the objects it is and reaches.</summary>
    /// <param name="Name">The parameter name, or <c>this</c>.</param>
    /// <param name="Type">The input's type.</param>
    /// <param name="RefKind">How the argument is passed.</param>
    /// <param name="IsReceiver">Whether the input is the receiver.</param>
    /// <param name="Action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    /// <param name="Field">The driver's static field the input is kept in, or <c>null</c> for a delegate argument.</param>
    /// <param name="Roots">The objects the input is.</param>
    /// <param name="Reached">Every object reachable from <paramref name="Roots"/>.</param>
    /// <param name="ProbeGraph">Every object reachable from a probe the input reaches.</param>
    /// <param name="OwnProbeRoots">The objects the input itself is (<see cref="Own"/>) that are probe objects.</param>
    private sealed record Input(string Name, ITypeSymbol Type, RefKind RefKind, bool IsReceiver, string Action, string? Field,
                                IReadOnlySet<string> Roots, IReadOnlySet<string> Reached, IReadOnlySet<string> ProbeGraph,
                                IReadOnlySet<string> OwnProbeRoots)
    {
        /// <summary>The objects the input itself is: <paramref name="Roots"/> without what the storage of a struct kept in its field
        /// holds, which is only reached from it (TD-041).</summary>
        public IReadOnlySet<string> Own { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public bool IsArray => Type is IArrayTypeSymbol;
        public bool IsCollection => IsArray || Type is INamedTypeSymbol named && SeedableFields.Collection(named) is not null;
        public bool IsStruct => Type.TypeKind == TypeKind.Struct && TypeShape.Of(Type) is TypeShapeKind.PlainStruct or TypeShapeKind.StructWithReferences;
    }

    /// <summary>Whose access an access's own path says it is (<see cref="SideOf"/>).</summary>
    private enum AccessSide
    {
        /// <summary>No path says: the objects the access reaches decide.</summary>
        Region,

        /// <summary>A path from the receiver through a field setup seeded or a probe its construction was given.</summary>
        Receiver,

        /// <summary>A path from an argument's own object.</summary>
        Argument
    }

    private sealed class Observation
    {
        public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EnumerationRoots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> NonEnumerationRoots { get; } = new(StringComparer.Ordinal);
        public bool ReadsUserObject { get; set; }

        public GeneratedEffect ToEffect(string kind) =>
            new(kind, OrderRoots(Roots), OrderRoots(EnumerationRoots), OrderRoots(NonEnumerationRoots), ReadsUserObject);
    }
}
