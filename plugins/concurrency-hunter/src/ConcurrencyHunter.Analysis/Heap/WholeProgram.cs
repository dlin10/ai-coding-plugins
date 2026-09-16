using System.Text.RegularExpressions;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Heap;

/// <summary>Builds a body's summary the first time it is asked for, and counts how many it built.</summary>
public sealed class SummaryCache(IReadOnlyDictionary<string, IrBody> bodies, ProgramIndex program, AnalysisLimits limits)
{
    private readonly Dictionary<string, MethodSummary> _summaries = new(StringComparer.Ordinal);

    public int Built => _summaries.Count;

    public bool IsBuilt(string bodyId) => _summaries.ContainsKey(bodyId);

    public MethodSummary? Get(string bodyId)
    {
        if (_summaries.TryGetValue(bodyId, out var summary))
            return summary;
        if (!bodies.TryGetValue(bodyId, out var body))
            return null;
        summary = MethodSummaryBuilder.Build(body, program, limits, id => bodies.GetValueOrDefault(id));
        _summaries.Add(bodyId, summary);
        return summary;
    }
}

/// <summary>Everything the whole-program fixpoint of one process scope is solved over.</summary>
public sealed record ScopeProgram(string ScopeId, IReadOnlyList<ExecutionRootDescriptor> Roots, ReachableSetResult Reachable,
                                  SummaryCache Summaries, ProgramIndex Program, DiIndex DiIndex,
                                  IReadOnlyList<TypeInjectionBindings> InjectionBindings);

public enum HeapRegionKind
{
    Static,
    Di,
    Allocation,
    Receiver,
    Delegate
}

/// <summary>One object or set of objects. <see cref="Identity"/> includes the context; <see cref="Display"/> never does.
/// An open region names a type parameter and stands for every closed region of its <see cref="Group"/>.
/// <see cref="MayOverlapItself"/> marks a hosted service whose instance count is unknown. <see cref="SiteBodyId"/> and
/// <see cref="SiteOperationId"/> name an allocation's or a delegate creation's site.</summary>
public sealed record HeapRegion(string Identity, HeapRegionKind Kind, string Display, string? TypeKey, string Context, string Group,
                                bool IsOpen, bool IsMerged, bool MayOverlapItself = false, string? SiteBodyId = null, int? SiteOperationId = null);

/// <summary>An instantiation of a body: its context, type substitution and the regions its receiver and parameters point to.
/// A receiverless instance has no <c>this</c>; its accesses based on <c>this</c> have no resource.</summary>
public sealed record MethodInstance(string Id, string BodyId, string Context, IReadOnlyDictionary<string, string> Substitution,
                                    bool IsMerged, bool IsReceiverless, MethodSummary Summary, IReadOnlySet<string> Receivers,
                                    IReadOnlyDictionary<int, IReadOnlySet<string>> Parameters, IReadOnlySet<string> CellOwners);

public sealed record CallEdge(string CallerInstance, int OperationId, string CalleeInstance, string Reason);

/// <summary>A closed type's type initializer: the instance running it and what triggered it.</summary>
public sealed record TypeInitializerConstruction(string TypeKey, string InstanceId, IReadOnlyList<string> TriggeringInstances,
                                                 IReadOnlyList<string> TriggeringRegions);

/// <summary>A region the container or the framework constructs, with the constructor instances that run for it.</summary>
public sealed record RegionConstruction(string RegionId, IReadOnlyList<string> ConstructorInstances);

public static class HeapCounters
{
    public const string MERGED_CONTEXT = "merged-context";
    public const string SCC_BUDGET_EXCEEDED = "scc-budget-exceeded";
    public const string NO_RECEIVER_OBJECT = "no-receiver-object";
    public const string REACHABLE_BODIES = "reachable-bodies";
    public const string LOWERED_NOT_REACHED = "lowered-not-reached";
}

/// <summary>The solved heap of one scope.</summary>
public sealed class HeapSolution
{
    private readonly Func<string, AbstractValue, IReadOnlySet<string>> _resolve;
    private readonly Func<string, string, IReadOnlySet<string>> _load;
    private readonly Func<string, string, IReadOnlySet<string>> _cell;
    private readonly Func<string, IrFieldRef, string> _staticRegion;
    private readonly Func<string, IReadOnlyList<string>> _fieldsOf;
    private readonly Func<string, IReadOnlySet<string>> _delegateCaptures;

    internal HeapSolution(IReadOnlyDictionary<string, HeapRegion> regions, IReadOnlyDictionary<string, MethodInstance> instances,
                          IReadOnlyList<CallEdge> edges, IReadOnlyList<TypeInitializerConstruction> typeInitializers,
                          IReadOnlyList<RegionConstruction> constructions, IReadOnlyDictionary<string, int> counters,
                          IReadOnlySet<string> reachableBodies, IReadOnlyList<string> loweredNotReached,
                          IReadOnlyList<(string BodyId, int OperationId)> noReceiverObjects,
                          Func<string, AbstractValue, IReadOnlySet<string>> resolve, Func<string, string, IReadOnlySet<string>> load,
                          Func<string, string, IReadOnlySet<string>> cell, IReadOnlyDictionary<string, string> rootInstances,
                          Func<string, IrFieldRef, string> staticRegion, Func<string, IReadOnlyList<string>> fieldsOf,
                          Func<string, IReadOnlySet<string>> delegateCaptures)
    {
        _cell = cell;
        RootInstances = rootInstances;
        _staticRegion = staticRegion;
        _fieldsOf = fieldsOf;
        _delegateCaptures = delegateCaptures;
        Regions = regions;
        Instances = instances;
        Edges = edges;
        TypeInitializers = typeInitializers;
        Constructions = constructions;
        Counters = counters;
        ReachableBodies = reachableBodies;
        LoweredNotReached = loweredNotReached;
        NoReceiverObjects = noReceiverObjects;
        _resolve = resolve;
        _load = load;
    }

    public IReadOnlyDictionary<string, HeapRegion> Regions { get; }
    public IReadOnlyDictionary<string, MethodInstance> Instances { get; }
    public IReadOnlyList<CallEdge> Edges { get; }
    public IReadOnlyList<TypeInitializerConstruction> TypeInitializers { get; }
    public IReadOnlyList<RegionConstruction> Constructions { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }
    public IReadOnlySet<string> ReachableBodies { get; }
    public IReadOnlyList<string> LoweredNotReached { get; }
    public IReadOnlyList<(string BodyId, int OperationId)> NoReceiverObjects { get; }

    /// <summary>The entry instance of each root, by stable root id.</summary>
    public IReadOnlyDictionary<string, string> RootInstances { get; }

    /// <summary>The storage region of a static field as an instance's substitution names it.</summary>
    public string StaticRegionOf(string instanceId, IrFieldRef field) => _staticRegion(instanceId, field);

    /// <summary>The fields (and <c>[]</c>) through which a region points to other regions.</summary>
    public IReadOnlyList<string> FieldsOf(string regionId) => _fieldsOf(regionId);

    /// <summary>The regions a delegate region captures: its receiver and the cells of the variables it closes over.</summary>
    public IReadOnlySet<string> DelegateCaptures(string regionId) => _delegateCaptures(regionId);

    /// <summary>The regions an abstract value of an instance's summary points to.</summary>
    public IReadOnlySet<string> Resolve(string instanceId, AbstractValue value) => _resolve(instanceId, value);

    /// <summary>The regions a field of a region points to, including what open regions of its group store. The field is a slot key
    /// as <see cref="FieldsOf"/> gives it, <c>[]</c>, or a bare field name, which joins the slots of every declaring type with that
    /// name.</summary>
    public IReadOnlySet<string> PointsTo(string regionId, string field) => _load(regionId, field);

    /// <summary>The regions the capture cell of a symbol key in a member-body instance points to.</summary>
    public IReadOnlySet<string> Cell(string ownerInstanceId, string symbolKey) => _cell(ownerInstanceId, symbolKey);
}

/// <summary>
/// Andersen-style propagation over <c>(method, context)</c> instances created on demand from the roots and the constructions
/// they trigger. An instance method's context is its receiver region, a static method's its call site; allocations take the
/// instance's context. Contexts beyond <see cref="AnalysisLimits.MaxContextsPerMethod"/>, a substitution naming an open type
/// parameter, and a call-graph cycle that keeps changing for more than <see cref="AnalysisLimits.MaxSccIterations"/> rounds go
/// to one merged context per body. Points-to sets only grow over a finite universe, so the rounds reach a fixpoint.
/// </summary>
public static class WholeProgram
{
    public static HeapSolution Solve(ScopeProgram program, AnalysisLimits limits) => new Solver(program, limits).Run();

    private static readonly Regex ASSEMBLY_PREFIX = new(@"(?<=^|[<\s,])[^<>,\s:\[\]*]+:");

    /// <summary>A type key as <c>SymbolNames.Type</c> displays it: every assembly prefix removed.</summary>
    internal static string DisplayType(string typeKey) => ASSEMBLY_PREFIX.Replace(typeKey, "");

    /// <summary>Where a DI resolution happens: inside one HTTP invocation, or in the root scope.</summary>
    private sealed record ResolutionScope(string? InvocationRootId);

    private sealed class InstanceState(string id, string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                       bool isMerged, bool isReceiverless, MethodSummary summary)
    {
        internal string Id { get; } = id;
        internal string BodyId { get; } = bodyId;
        internal string Context { get; } = context;
        internal IReadOnlyDictionary<string, string> Substitution { get; } = substitution;
        internal bool IsMerged { get; } = isMerged;
        internal bool IsReceiverless { get; } = isReceiverless;
        internal MethodSummary Summary { get; } = summary;
        internal HashSet<string> Receivers { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal Dictionary<int, HashSet<string>> Parameters { get; } = [];
        internal Dictionary<int, HashSet<string>> CallResults { get; } = [];
        internal Dictionary<(int Operation, int Ordinal), HashSet<string>> RefResults { get; } = [];
        internal HashSet<string> Returns { get; } = new(StringComparer.Ordinal);
        internal Dictionary<int, HashSet<string>> RefParameters { get; } = [];
    }

    private sealed class DelegateState(string target, bool isNestedBody, string? containingTypeKey, IReadOnlyList<string> methodTypeArguments,
                                       IReadOnlyDictionary<string, string> ownerSubstitution)
    {
        internal string Target { get; } = target;
        internal bool IsNestedBody { get; } = isNestedBody;
        internal string? ContainingTypeKey { get; } = containingTypeKey;
        internal IReadOnlyList<string> MethodTypeArguments { get; } = methodTypeArguments;
        internal IReadOnlyDictionary<string, string> OwnerSubstitution { get; } = ownerSubstitution;
        internal HashSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CapturedReceivers { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CapturedKeys { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Solver
    {
        private static readonly IReadOnlyDictionary<string, string> NO_SUBSTITUTION = new Dictionary<string, string>();

        private readonly ScopeProgram _scope;
        private readonly ProgramIndex _program;
        private readonly AnalysisLimits _limits;
        private readonly Dictionary<string, string> _memberOfNestedBody = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _capturedKeysOfMember = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HeapRegion> _regions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Region, string Field), HashSet<string>> _fields = [];
        private readonly Dictionary<(string Owner, string Key), HashSet<string>> _cells = [];
        private readonly Dictionary<string, DelegateState> _delegates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InstanceState> _instances = new(StringComparer.Ordinal);
        private readonly List<string> _instanceOrder = [];
        private readonly Dictionary<string, int> _contextCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _mergedBodies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sccHandled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _changedRounds = new(StringComparer.Ordinal);
        private readonly HashSet<(string Caller, int Operation, string Callee, string Reason)> _edges = [];
        private readonly Dictionary<string, (string InstanceId, HashSet<string> Instances, HashSet<string> Regions)> _typeInitializers =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _constructions = new(StringComparer.Ordinal);
        private readonly HashSet<(string BodyId, int OperationId)> _noReceiver = [];
        private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _rootInstances = new(StringComparer.Ordinal);
        private int _changes;

        internal Solver(ScopeProgram scope, AnalysisLimits limits)
        {
            _scope = scope;
            _program = scope.Program;
            _limits = limits;
            foreach (var method in _program.Methods)
            {
                foreach (var nested in method.NestedBodyIds)
                    _memberOfNestedBody.TryAdd(nested, method.MethodId);
            }
        }

        internal HeapSolution Run()
        {
            Seed();
            while (true)
            {
                var before = _changes;
                for (var index = 0; index < _instanceOrder.Count; index++)
                {
                    var instance = _instances[_instanceOrder[index]];
                    var start = _changes;
                    Process(instance);
                    if (_changes != start)
                        CountRound(instance);
                }

                if (_changes == before)
                    break;
            }

            return Result();
        }

        private HeapSolution Result()
        {
            var reachable = _instances.Values.Select(instance => instance.BodyId).ToHashSet(StringComparer.Ordinal);
            // Every lowered body, nested ones included: a lambda the heap never reaches is inventory too.
            var loweredNotReached = _scope.Reachable.Bodies.Keys.Where(body => !reachable.Contains(body))
                                          .Order(StringComparer.Ordinal).ToArray();
            _counters[HeapCounters.NO_RECEIVER_OBJECT] = _noReceiver.Count;
            _counters[HeapCounters.REACHABLE_BODIES] = reachable.Count;
            _counters[HeapCounters.LOWERED_NOT_REACHED] = loweredNotReached.Length;
            _counters.TryAdd(HeapCounters.MERGED_CONTEXT, 0);
            _counters.TryAdd(HeapCounters.SCC_BUDGET_EXCEEDED, 0);

            var instances = _instances.Values.ToDictionary(
                instance => instance.Id,
                instance => new MethodInstance(instance.Id, instance.BodyId, instance.Context, instance.Substitution, instance.IsMerged,
                                               instance.IsReceiverless, instance.Summary, instance.Receivers,
                                               instance.Parameters.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value),
                                               instance.CellOwners),
                StringComparer.Ordinal);
            return new HeapSolution(
                _regions,
                instances,
                _edges.OrderBy(edge => edge.Caller, StringComparer.Ordinal).ThenBy(edge => edge.Operation)
                      .ThenBy(edge => edge.Callee, StringComparer.Ordinal)
                      .Select(edge => new CallEdge(edge.Caller, edge.Operation, edge.Callee, edge.Reason)).ToArray(),
                _typeInitializers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                 .Select(pair => new TypeInitializerConstruction(pair.Key, pair.Value.InstanceId,
                                                                                 pair.Value.Instances.Order(StringComparer.Ordinal).ToArray(),
                                                                                 pair.Value.Regions.Order(StringComparer.Ordinal).ToArray()))
                                 .ToArray(),
                _constructions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                              .Select(pair => new RegionConstruction(pair.Key, pair.Value.ToArray())).ToArray(),
                _counters,
                reachable,
                loweredNotReached,
                _noReceiver.OrderBy(item => item.BodyId, StringComparer.Ordinal).ThenBy(item => item.OperationId).ToArray(),
                (instanceId, value) => Eval(_instances[instanceId], value),
                LoadAny,
                (owner, key) => _cells.GetValueOrDefault((owner, key)) ?? new HashSet<string>(StringComparer.Ordinal),
                _rootInstances,
                (instanceId, field) => StaticRegion(_instances[instanceId], field),
                regionId => _fields.Where(pair => pair.Key.Region == regionId && pair.Value.Count != 0).Select(pair => pair.Key.Field)
                                   .Order(StringComparer.Ordinal).ToArray(),
                regionId => _delegates.TryGetValue(regionId, out var state)
                    ? state.CapturedReceivers.Concat(state.CellOwners.SelectMany(owner => state.CapturedKeys.SelectMany(key => Cell(owner, key))))
                           .ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal));
        }

        private void Seed()
        {
            foreach (var root in _scope.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
            {
                var bindings = root.InstanceBindings;
                var invocation = $"root:{root.StableRootId}";
                var context = invocation;
                string? receiver = null;
                ResolutionScope scope = new(root.StableRootId);
                if (bindings is { Receiver: ReceiverKind.HostedService, ReceiverTypeKey: { } hostedKey })
                {
                    scope = new ResolutionScope(null);
                    var hosted = _scope.DiIndex.HostedServices.FirstOrDefault(candidate => candidate.ImplementationTypeKey == hostedKey);
                    receiver = Region($"di|{DiIndex.HOSTED_SERVICE_KEY}|{hostedKey}@Singleton|singleton", HeapRegionKind.Di,
                                      DiIndex.RegionId(bindings.ReceiverType ?? DisplayType(hostedKey), DiLifetime.Singleton), hostedKey,
                                      "singleton", null, mayOverlapItself: hosted?.InstanceCount == HostedServiceInstanceCount.Unknown);
                    Construct(receiver, hostedKey, scope);
                }
                else if (bindings is { Receiver: ReceiverKind.PerInvocation, ReceiverTypeKey: { } controllerKey })
                {
                    receiver = Region($"receiver|{controllerKey}|{invocation}", HeapRegionKind.Receiver, $"receiver:{DisplayType(controllerKey)}",
                                      controllerKey, invocation, null);
                    Construct(receiver, controllerKey, scope);
                }

                var bodyId = root.Entry.BodyKey;
                var memberId = _memberOfNestedBody.GetValueOrDefault(bodyId);
                var method = _program.Method(memberId ?? bodyId);
                var substitution = receiver is not null && method is not null ? ReceiverSubstitution(_regions[receiver], method, []) : NO_SUBSTITUTION;
                var entry = Instance(bodyId, receiver ?? context, substitution, receiver is null ? [] : [receiver],
                                     memberId is null ? [] : [invocation], receiver is null && method is { IsStatic: false });
                if (entry is null)
                    continue;
                _rootInstances[root.StableRootId] = entry.Id;

                for (var ordinal = 0; ordinal < bindings.Parameters.Count; ordinal++)
                {
                    var parameter = bindings.Parameters[ordinal];
                    if (parameter is { Kind: ParameterBindingKind.DiService, IsValueType: false, TypeKey: { } typeKey })
                    {
                        Add(Parameter(entry, ordinal), Resolve(_scope.DiIndex.Resolve(typeKey), scope, invocation, root.Entry.Symbol, parameter.Name));
                    }
                }
            }
        }

        /// <summary>The region a DI resolution gives: one object per registration for a singleton; per invocation, or one root-scope
        /// object, for a scoped service; per resolving object, consumer and parameter for a transient. A new region with a type
        /// registration is constructed.</summary>
        private IReadOnlyList<string> Resolve(DiResolution resolution, ResolutionScope scope, string resolver, string consumer, string parameter)
        {
            if (resolution is not { Kind: DiResolutionKind.Bound, Binding: { } binding })
                return [];

            var context = binding.Lifetime switch
            {
                DiLifetime.Singleton => "singleton",
                DiLifetime.Scoped => scope.InvocationRootId is { } rootId ? $"invocation:root:{rootId}" : "root-scope",
                _ => $"{resolver}|{consumer}|{parameter}"
            };
            var identity = $"di|{resolution.ServiceType}|{binding.ImplementationTypeKey}@{binding.Lifetime}|{context}";
            var isNew = !_regions.ContainsKey(identity);
            Region(identity, HeapRegionKind.Di, binding.RegionId, binding.ImplementationTypeKey, context, null);
            if (isNew && resolution.Registrations.Any(registration => registration.Form == DiRegistrationForm.Type))
                Construct(identity, binding.ImplementationTypeKey, binding.Lifetime == DiLifetime.Singleton ? new ResolutionScope(null) : scope);
            return [identity];
        }

        /// <summary>Runs a region's selected constructor with the region as <c>this</c> and its parameters bound to their resolved DI
        /// regions, once, and activates the built type's type initializer.</summary>
        private void Construct(string regionId, string typeKey, ResolutionScope scope)
        {
            if (_constructions.ContainsKey(regionId))
                return;
            var constructorInstances = new List<string>();
            _constructions.Add(regionId, constructorInstances);

            var type = _program.Type(typeKey);
            var bindings = type is null ? null : _scope.InjectionBindings.FirstOrDefault(candidate => candidate.TypeKey == type.TypeKey);
            foreach (var constructor in ReachableSet.SelectedConstructors(_program, typeKey, bindings))
            {
                var instance = Instance(constructor.MethodId, regionId, ReceiverSubstitution(_regions[regionId], constructor, []), [regionId], [], false);
                if (instance is null)
                    continue;
                constructorInstances.Add(instance.Id);
                foreach (var parameter in bindings?.ConstructorParameters ?? [])
                {
                    if (parameter.Ordinal < constructor.Parameters.Count && constructor.Parameters[parameter.Ordinal].TypeKey == parameter.TypeKey)
                    {
                        var resolution = ReachableSet.ResolveConstructorParameter(_program, _scope.DiIndex, typeKey, parameter);
                        Add(Parameter(instance, parameter.Ordinal), Resolve(resolution, scope, regionId, typeKey, parameter.Name));
                    }
                }
            }

            ActivateTypeInitializer(typeKey, null, regionId);
        }

        private InstanceState? Instance(string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                        IEnumerable<string> receivers, IEnumerable<string> cellOwners, bool receiverless,
                                        bool merged = false)
        {
            if (!_scope.Reachable.Bodies.ContainsKey(bodyId))
                return null;

            merged = merged || _mergedBodies.Contains(bodyId) || substitution.Values.Any(_program.IsOpen);
            var key = $"{bodyId}@{context}{SubstitutionText(substitution)}";
            if (!merged && !_instances.ContainsKey(key))
            {
                var count = _contextCounts.GetValueOrDefault(bodyId);
                if (count >= _limits.MaxContextsPerMethod)
                {
                    _mergedBodies.Add(bodyId);
                    merged = true;
                }
                else
                {
                    _contextCounts[bodyId] = count + 1;
                }
            }

            if (merged)
                key = $"{bodyId}@merged";

            if (!_instances.TryGetValue(key, out var instance))
            {
                var summary = _scope.Summaries.Get(bodyId);
                if (summary is null)
                    return null;
                instance = new InstanceState(key, bodyId, merged ? "merged" : context, merged ? NO_SUBSTITUTION : substitution, merged,
                                             !merged && receiverless, summary);
                _instances.Add(key, instance);
                _instanceOrder.Add(key);
                _changes++;
                if (merged)
                    _counters[HeapCounters.MERGED_CONTEXT] = _counters.GetValueOrDefault(HeapCounters.MERGED_CONTEXT) + 1;
                if (!_memberOfNestedBody.ContainsKey(bodyId))
                    instance.CellOwners.Add(key);
            }

            Add(instance.Receivers, receivers);
            Add(instance.CellOwners, cellOwners);
            return instance;
        }

        private static string SubstitutionText(IReadOnlyDictionary<string, string> substitution) =>
            substitution.Count == 0 ? "" : "|" + string.Join(";", substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                              .Select(pair => $"{pair.Key}={pair.Value}"));

        private void Process(InstanceState instance)
        {
            var summary = instance.Summary;
            var memberId = _memberOfNestedBody.GetValueOrDefault(instance.BodyId) ?? instance.BodyId;
            var capturedKeys = CapturedKeys(memberId);
            foreach (var variable in summary.Variables.Where(variable => capturedKeys.Contains(variable.SymbolKey)))
            {
                var values = Eval(instance, variable.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                    Add(Cell(owner, variable.SymbolKey), values);
            }

            foreach (var store in summary.CapturedStores)
            {
                var values = Eval(instance, store.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                    Add(Cell(owner, store.SymbolKey), values);
            }

            foreach (var store in summary.Stores)
            {
                var values = Eval(instance, store.Values);
                if (store.Field.IsStatic)
                    Add(Field(StaticRegion(instance, store.Field), FieldSlot.Key(store.Field)), values);
                else
                    foreach (var @base in Eval(instance, store.Bases))
                        Add(Field(@base, FieldSlot.Key(store.Field)), values);
            }

            foreach (var element in summary.Elements.Where(element => element.Kind == ElementOperationKind.Store))
            {
                var values = Eval(instance, element.Values);
                foreach (var array in Eval(instance, element.Arrays))
                    Add(Field(array, PathValue.ELEMENT), values);
            }

            foreach (var @return in summary.Returns)
                Add(instance.Returns, Eval(instance, @return.Values));
            foreach (var parameter in summary.RefParameters)
                Add(RefParameter(instance, parameter.Ordinal), Eval(instance, parameter.Values));

            foreach (var created in summary.Delegates)
            {
                var region = DelegateRegion(instance, created.Delegate);
                var state = _delegates[region];
                Add(state.CellOwners, instance.CellOwners);
                state.CapturedKeys.UnionWith(created.Delegate.CapturedValues.Keys.Where(key => key != "this"));
                if (created.Delegate.CapturedValues.TryGetValue("this", out var thisValues))
                    Add(state.CapturedReceivers, Eval(instance, thisValues));
                if (!state.IsNestedBody && _program.Method(state.Target) is { IsStatic: true } method)
                    ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, instance, null);
            }

            foreach (var access in summary.Accesses.Where(access => access.Field.IsStatic))
            {
                StaticRegion(instance, access.Field);
                ActivateTypeInitializer(StaticTypeKey(instance, access.Field), instance, null);
            }

            foreach (var call in summary.Calls)
                Call(instance, call);
        }

        private void Call(InstanceState caller, CallTransfer call)
        {
            var typeArguments = (call.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, caller.Substitution)).ToArray();
            var containing = call.TargetContainingTypeKey is null ? null : ProgramIndex.Substitute(call.TargetContainingTypeKey, caller.Substitution);
            switch (call.Kind)
            {
                case IrCallKind.LocalFunction:
                {
                    var callee = Instance(call.Target, OwnerContext(caller.CellOwners), caller.Substitution, caller.Receivers, caller.CellOwners,
                                          caller.IsReceiverless);
                    Bind(caller, call, callee, "exact");
                    return;
                }
                case IrCallKind.Delegate:
                {
                    var regions = Eval(caller, call.Receivers).Where(region => _delegates.ContainsKey(region)).ToArray();
                    if (regions.Length == 0)
                        NoReceiver(caller, call);
                    foreach (var region in regions)
                        CallDelegate(caller, call, _delegates[region]);
                    return;
                }
                case IrCallKind.Virtual or IrCallKind.Interface:
                {
                    var receivers = Eval(caller, call.Receivers);
                    if (receivers.Count == 0)
                        NoReceiver(caller, call);
                    foreach (var receiver in receivers)
                        Dispatch(caller, call, call.Target, receiver, typeArguments, _regions[receiver].Kind == HeapRegionKind.Di ? "di-binding" : "points-to");
                    return;
                }
                case IrCallKind.Static:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    var callee = Instance(call.Target, $"{caller.BodyId}#{call.OperationId}", Substitution(method, containing, typeArguments), [], [], false);
                    Bind(caller, call, callee, "exact");
                    ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
                default:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    var receivers = Eval(caller, call.Receivers);
                    if (receivers.Count == 0)
                    {
                        NoReceiver(caller, call);
                        var callee = Instance(call.Target, $"{caller.Context}|{caller.BodyId}#{call.OperationId}",
                                              Substitution(method, containing, typeArguments), [], [], true);
                        Bind(caller, call, callee, "exact");
                    }

                    foreach (var receiver in receivers)
                    {
                        var callee = Instance(call.Target, receiver + TypeArgumentText(typeArguments),
                                              ReceiverSubstitution(_regions[receiver], method, typeArguments), [receiver], [], false);
                        Bind(caller, call, callee, "exact");
                    }

                    if (call.Kind == IrCallKind.Constructor)
                        ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
            }
        }

        /// <summary>A delegate invocation runs the delegate's target: its nested body with the creating instance's cells and captured
        /// receiver, a static method at the invocation site, or an instance method on each captured receiver region, resolved
        /// through the program index when the method is virtual.</summary>
        private void CallDelegate(InstanceState caller, CallTransfer call, DelegateState state)
        {
            if (state.IsNestedBody)
            {
                var owner = state.CellOwners.Select(id => _instances.GetValueOrDefault(id)).OfType<InstanceState>().FirstOrDefault();
                var callee = Instance(state.Target, OwnerContext(state.CellOwners), state.OwnerSubstitution, state.CapturedReceivers, state.CellOwners,
                                      owner?.IsReceiverless ?? false);
                Bind(caller, call, callee, "delegate");
                return;
            }

            if (_program.Method(state.Target) is not { } method)
                return;
            if (method.IsStatic)
            {
                var callee = Instance(method.MethodId, $"{caller.BodyId}#{call.OperationId}",
                                      Substitution(method, state.ContainingTypeKey, state.MethodTypeArguments), [], [], false);
                Bind(caller, call, callee, "delegate");
                ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, caller, null);
                return;
            }

            if (state.CapturedReceivers.Count == 0)
                NoReceiver(caller, call);
            var isVirtual = method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true;
            foreach (var receiver in state.CapturedReceivers.ToArray())
            {
                if (isVirtual)
                {
                    Dispatch(caller, call, method.MethodId, receiver, state.MethodTypeArguments, "delegate");
                    continue;
                }

                var callee = Instance(method.MethodId, receiver + TypeArgumentText(state.MethodTypeArguments),
                                      ReceiverSubstitution(_regions[receiver], method, state.MethodTypeArguments), [receiver], [], false);
                Bind(caller, call, callee, "delegate");
            }
        }

        /// <summary>A lambda or local function body instance has the context of the member-body instance owning its cells.</summary>
        private static string OwnerContext(IEnumerable<string> cellOwners) => string.Join(",", cellOwners.Order(StringComparer.Ordinal));

        private void Dispatch(InstanceState caller, CallTransfer call, string methodId, string receiver, IReadOnlyList<string> typeArguments,
                              string reason)
        {
            var region = _regions[receiver];
            if (region.TypeKey is null || _program.Implementation(region.TypeKey, methodId) is not { HasSourceBody: true } implementation)
            {
                NoReceiver(caller, call);
                return;
            }

            var callee = Instance(implementation.MethodId, receiver + TypeArgumentText(typeArguments),
                                  ReceiverSubstitution(region, implementation, typeArguments), [receiver], [], false);
            Bind(caller, call, callee, reason);
        }

        private void Bind(InstanceState caller, CallTransfer call, InstanceState? callee, string reason)
        {
            if (callee is null)
                return;
            foreach (var argument in call.Arguments)
                Add(Parameter(callee, argument.ParameterOrdinal), Eval(caller, argument.Values));
            Add(CallResult(caller, call.OperationId), callee.Returns);
            foreach (var (ordinal, values) in callee.RefParameters)
                Add(RefResult(caller, call.OperationId, ordinal), values);
            if (_edges.Add((caller.Id, call.OperationId, callee.Id, reason)))
                _changes++;
        }

        private void NoReceiver(InstanceState caller, CallTransfer call) => _noReceiver.Add((caller.BodyId, call.OperationId));

        /// <summary>The substitution of a method running on a receiver region: the region's type projected onto the method's declaring
        /// type, plus the call's method type arguments.</summary>
        private IReadOnlyDictionary<string, string> ReceiverSubstitution(HeapRegion receiver, ProgramMethod method, IReadOnlyList<string> typeArguments) =>
            Substitution(method, receiver.TypeKey is null ? null : _program.ConstructedBase(receiver.TypeKey, method.ContainingTypeKey), typeArguments);

        private IReadOnlyDictionary<string, string> Substitution(ProgramMethod method, string? containingTypeKey, IReadOnlyList<string> typeArguments)
        {
            var substitution = new Dictionary<string, string>(StringComparer.Ordinal);
            if (containingTypeKey is not null && _program.Type(method.ContainingTypeKey) is { TypeParameterKeys.Count: > 0 } declaring)
            {
                var (_, arguments) = _program.Decompose(containingTypeKey);
                foreach (var (parameter, argument) in declaring.TypeParameterKeys.Zip(arguments))
                {
                    if (parameter != argument)
                        substitution[parameter] = argument;
                }
            }

            foreach (var (parameter, argument) in method.TypeParameterKeys.Zip(typeArguments))
            {
                if (parameter != argument)
                    substitution[parameter] = argument;
            }

            return substitution.Count == 0 ? NO_SUBSTITUTION : substitution;
        }

        private static string TypeArgumentText(IReadOnlyList<string> typeArguments) =>
            typeArguments.Count == 0 ? "" : $"<{string.Join(",", typeArguments)}>";

        /// <summary>Activates a closed type's type-initializer construction once, recording what triggered it; a reference from the
        /// type initializer itself is not a trigger.</summary>
        private void ActivateTypeInitializer(string typeKey, InstanceState? triggerInstance, string? triggerRegion)
        {
            if (_program.Type(typeKey) is not { } type ||
                _program.MethodsOf(type.TypeKey).FirstOrDefault(method => method is { Kind: ProgramMethodKind.TypeInitializer, HasSourceBody: true }) is not { } initializer ||
                triggerInstance?.BodyId == initializer.MethodId)
            {
                return;
            }

            if (!_typeInitializers.TryGetValue(typeKey, out var construction))
            {
                var (_, arguments) = _program.Decompose(typeKey);
                var substitution = type.TypeParameterKeys.Zip(arguments).Where(pair => pair.First != pair.Second)
                                       .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                // An open type's one construction stands for every closed one, so its regions are merged.
                var instance = Instance(initializer.MethodId, $"type-initializer:{typeKey}", substitution, [], [], false,
                                        merged: _program.IsOpen(typeKey));
                construction = (instance?.Id ?? "", new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                _typeInitializers.Add(typeKey, construction);
                _changes++;
            }

            if (triggerInstance is not null && construction.Instances.Add(triggerInstance.Id))
                _changes++;
            if (triggerRegion is not null && construction.Regions.Add(triggerRegion))
                _changes++;
        }

        private HashSet<string> Eval(InstanceState instance, IEnumerable<AbstractValue> values)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
                result.UnionWith(Eval(instance, value));
            return result;
        }

        private HashSet<string> Eval(InstanceState instance, AbstractValue value) => value switch
        {
            ThisValue => new HashSet<string>(instance.Receivers, StringComparer.Ordinal),
            ParameterValue parameter => new HashSet<string>(Parameter(instance, parameter.Ordinal), StringComparer.Ordinal),
            StaticFieldValue field => Load(StaticRegion(instance, field.Field), FieldSlot.Key(field.Field)),
            AllocationValue allocation => [AllocationRegion(instance, allocation.Site)],
            CallResultValue result => new HashSet<string>(CallResult(instance, result.OperationId), StringComparer.Ordinal),
            RefResultValue result => new HashSet<string>(RefResult(instance, result.OperationId, result.Ordinal), StringComparer.Ordinal),
            DelegateCreationValue created => [DelegateRegion(instance, created)],
            CapturedValue captured => instance.CellOwners.SelectMany(owner => Cell(owner, captured.SymbolKey)).ToHashSet(StringComparer.Ordinal),
            PathValue path => EvalPath(instance, path),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };

        private HashSet<string> EvalPath(InstanceState instance, PathValue path)
        {
            var current = Eval(instance, path.Base);
            foreach (var segment in path.Segments)
            {
                var next = new HashSet<string>(StringComparer.Ordinal);
                foreach (var region in current)
                {
                    if (segment == PathValue.WILDCARD)
                        next.UnionWith(Closure(region));
                    else
                        next.UnionWith(Load(region, segment));
                }

                current = next;
            }

            return current;
        }

        /// <summary>Every region reachable from a region through any fields.</summary>
        private HashSet<string> Closure(string region)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([region]);
            while (pending.TryPop(out var current))
            {
                foreach (var pair in _fields.Where(pair => pair.Key.Region == current).ToArray())
                {
                    foreach (var target in pair.Value)
                    {
                        if (seen.Add(target))
                            pending.Push(target);
                    }
                }
            }

            return seen;
        }

        /// <summary>A field slot of a region, or, for a bare field name, every declaring type's slot of that name.</summary>
        private HashSet<string> LoadAny(string regionId, string field)
        {
            if (field.Contains('.', StringComparison.Ordinal) || field == PathValue.ELEMENT)
                return Load(regionId, field);

            var result = Load(regionId, field);
            foreach (var key in _fields.Keys.Where(key => key.Region == regionId && FieldSlot.Name(key.Field) == field))
                result.UnionWith(Load(regionId, key.Field));
            return result;
        }

        /// <summary>A field of a region, joined with the same field of the open regions of its group, or of every region of the group
        /// when the region itself is open.</summary>
        private HashSet<string> Load(string regionId, string field)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!_regions.TryGetValue(regionId, out var region))
                return result;
            foreach (var member in _groups[region.Group])
            {
                if (member == regionId || region.IsOpen || _regions[member].IsOpen)
                    result.UnionWith(_fields.GetValueOrDefault((member, field)) ?? []);
            }

            return result;
        }

        private string StaticTypeKey(InstanceState instance, IrFieldRef field) =>
            ProgramIndex.Substitute(DiIndex.TypeKey(field.Assembly, field.ContainingTypeId), instance.Substitution);

        private string StaticRegion(InstanceState instance, IrFieldRef field)
        {
            var typeKey = StaticTypeKey(instance, field);
            return Region($"static:{typeKey}", HeapRegionKind.Static, $"static:{DisplayType(typeKey)}", typeKey, "",
                          $"static|{_program.Decompose(typeKey).DefinitionKey}", merged: instance.IsMerged && _program.IsOpen(typeKey));
        }

        private string AllocationRegion(InstanceState instance, CreationSite site)
        {
            var typeKey = ProgramIndex.Substitute(site.TypeKey, instance.Substitution);
            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            return Region($"alloc|{site.BodyId}#{site.OperationId}|{typeKey}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                          $"alloc:{owner}#{DisplayType(typeKey)}{ordinal}", typeKey, instance.Context, $"alloc|{site.BodyId}#{site.OperationId}",
                          merged: instance.IsMerged, site: site);
        }

        private string DelegateRegion(InstanceState instance, DelegateCreationValue created)
        {
            var site = created.Site;
            var identity = $"delegate|{site.BodyId}#{site.OperationId}|{ContextKey(instance)}";
            if (_delegates.ContainsKey(identity))
                return identity;

            var transfer = instance.Summary.Delegates.FirstOrDefault(candidate => candidate.OperationId == site.OperationId);
            var method = _program.Method(created.Target);
            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            Region(identity, HeapRegionKind.Delegate, $"delegate:{owner}#{method?.DisplaySymbol ?? created.Target}{ordinal}",
                   SubstituteDisplay(site.TypeKey, instance.Substitution),
                   instance.Context, $"delegate|{site.BodyId}#{site.OperationId}", merged: instance.IsMerged, site: site);
            _delegates[identity] = new DelegateState(
                created.Target, method is null,
                transfer?.TargetContainingTypeKey is { } containing ? ProgramIndex.Substitute(containing, instance.Substitution) : null,
                (transfer?.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, instance.Substitution)).ToArray(),
                instance.Substitution);
            return identity;
        }

        /// <summary>A delegate creation names its type as the IR value's display type, not as a type key, so its substitution is
        /// applied by display name: <c>Action&lt;T&gt;</c> created in an instance of <c>T = Order</c> is <c>Action&lt;Order&gt;</c>.</summary>
        private static string SubstituteDisplay(string type, IReadOnlyDictionary<string, string> substitution)
        {
            if (substitution.Count == 0)
                return type;
            var byDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (parameter, argument) in substitution)
                byDisplay.TryAdd(DisplayType(parameter), DisplayType(argument));
            return ProgramIndex.Substitute(type, byDisplay);
        }

        /// <summary>An instance's context with its substitution: one body reached from one call site with two type arguments runs in
        /// two contexts, so it creates two objects, even when the created type itself is not generic.</summary>
        private static string ContextKey(InstanceState instance) =>
            instance.Substitution.Count == 0
                ? instance.Context
                : $"{instance.Context}|{string.Join(",", instance.Substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                             .Select(pair => $"{pair.Key}={pair.Value}"))}";

        private string Region(string identity, HeapRegionKind kind, string display, string? typeKey, string context, string? group,
                              bool merged = false, bool mayOverlapItself = false, CreationSite? site = null)
        {
            if (_regions.ContainsKey(identity))
                return identity;
            group ??= identity;
            _regions.Add(identity, new HeapRegion(identity, kind, display, typeKey, context, group,
                                                  typeKey is not null && _program.IsOpen(typeKey), merged, mayOverlapItself,
                                                  site?.BodyId, site?.OperationId));
            if (!_groups.TryGetValue(group, out var members))
                _groups.Add(group, members = []);
            members.Add(identity);
            _changes++;
            return identity;
        }

        private HashSet<string> CapturedKeys(string memberId)
        {
            if (_capturedKeysOfMember.TryGetValue(memberId, out var keys))
                return keys;
            keys = new HashSet<string>(StringComparer.Ordinal);
            var nested = _program.Method(memberId)?.NestedBodyIds ?? [];
            foreach (var bodyId in nested)
            {
                if (_scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                {
                    keys.UnionWith(body.Blocks.SelectMany(block => block.Operations).OfType<IrCaptureOperation>()
                                       .Select(capture => capture.SymbolKey).OfType<string>());
                }
            }

            _capturedKeysOfMember.Add(memberId, keys);
            return keys;
        }

        /// <summary>Counts a changing round of an instance; a call-graph cycle that keeps changing past the budget has its bodies'
        /// contexts merged, and propagation continues.</summary>
        private void CountRound(InstanceState instance)
        {
            var rounds = _changedRounds.GetValueOrDefault(instance.Id) + 1;
            _changedRounds[instance.Id] = rounds;
            if (rounds <= _limits.MaxSccIterations || _sccHandled.Contains(instance.Id))
                return;

            var component = Component(instance.Id);
            var cyclic = component.Count > 1 || _edges.Any(edge => edge.Caller == instance.Id && edge.Callee == instance.Id);
            _sccHandled.UnionWith(component);
            if (!cyclic)
                return;

            _mergedBodies.UnionWith(component.Select(id => _instances[id].BodyId));
            _counters[HeapCounters.SCC_BUDGET_EXCEEDED] = _counters.GetValueOrDefault(HeapCounters.SCC_BUDGET_EXCEEDED) + 1;
        }

        /// <summary>The strongly connected component of the instance call graph that contains <paramref name="start"/>: the instances
        /// it reaches that also reach it.</summary>
        private HashSet<string> Component(string start)
        {
            var successors = _edges.GroupBy(edge => edge.Caller, StringComparer.Ordinal)
                                   .ToDictionary(group => group.Key, group => group.Select(edge => edge.Callee).ToArray(), StringComparer.Ordinal);
            var predecessors = _edges.GroupBy(edge => edge.Callee, StringComparer.Ordinal)
                                     .ToDictionary(group => group.Key, group => group.Select(edge => edge.Caller).ToArray(), StringComparer.Ordinal);
            var forward = Reach(start, successors);
            var backward = Reach(start, predecessors);
            forward.IntersectWith(backward);
            forward.Add(start);
            return forward;
        }

        private static HashSet<string> Reach(string start, IReadOnlyDictionary<string, string[]> graph)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([start]);
            while (pending.TryPop(out var current))
            {
                foreach (var next in graph.GetValueOrDefault(current) ?? [])
                {
                    if (seen.Add(next))
                        pending.Push(next);
                }
            }

            return seen;
        }

        private HashSet<string> Field(string region, string field) => Get(_fields, (region, field));

        private HashSet<string> Cell(string owner, string key) => Get(_cells, (owner, key));

        private static HashSet<string> Parameter(InstanceState instance, int ordinal) => Get(instance.Parameters, ordinal);

        private static HashSet<string> CallResult(InstanceState instance, int operation) => Get(instance.CallResults, operation);

        private static HashSet<string> RefResult(InstanceState instance, int operation, int ordinal) => Get(instance.RefResults, (operation, ordinal));

        private static HashSet<string> RefParameter(InstanceState instance, int ordinal) => Get(instance.RefParameters, ordinal);

        private static HashSet<string> Get<TKey>(Dictionary<TKey, HashSet<string>> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
                map.Add(key, set = new HashSet<string>(StringComparer.Ordinal));
            return set;
        }

        private void Add(HashSet<string> target, IEnumerable<string> values)
        {
            foreach (var value in values.ToArray())
            {
                if (target.Add(value))
                    _changes++;
            }
        }
    }
}
