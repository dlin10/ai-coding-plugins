using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.CallGraph;

/// <summary>What the reachable set is built over. <see cref="Members"/> lowers a member by its body id and returns its body
/// with every nested body, or nothing when it has no source body; the set asks for each member at most once.</summary>
/// <param name="Program">The scope's types and members.</param>
/// <param name="Roots">The execution roots.</param>
/// <param name="DiIndex">The dependency injection registrations.</param>
/// <param name="InjectionBindings">The constructor injection bindings.</param>
/// <param name="Members">The member lowering function.</param>
public sealed record ReachabilityInput(ProgramIndex Program, IReadOnlyList<ExecutionRootDescriptor> Roots, DiIndex DiIndex,
                                       IReadOnlyList<TypeInjectionBindings> InjectionBindings,
                                       Func<string, IReadOnlyList<IrBody>> Members)
{
    /// <summary>How a call that dispatches reaches its targets, and the types whose bodies the set never enters.</summary>
    public ReachabilityRules Rules { get; init; } = ReachabilityRules.ClassHierarchy;
}

/// <summary>How a call that dispatches — a virtual or interface call, a delegate created on a virtual or interface method, a
/// spawn's work — reaches the overrides it may run.</summary>
public enum DispatchRule
{
    /// <summary>Every override the class hierarchy offers.</summary>
    ClassHierarchy,

    /// <summary>Only the overrides of a type the run constructs, or a type derived from it, and of a value type once a reached body
    /// boxes it; an override whose type is constructed later is reached then (ADR 0019).</summary>
    ConstructedTypes
}

/// <summary>The rules the reachable set is built by: class-hierarchy dispatch and no type left out for every run but the generation
/// of <c>System.Private.CoreLib</c> (ADR 0019).</summary>
/// <param name="Dispatch">How a call that dispatches reaches its targets.</param>
/// <param name="LeftOutTypeKeys">The definition keys of the types no body of which is ever in the set, whatever reaches for it: a call
/// whose every target is one of their members has no target.</param>
public sealed record ReachabilityRules(DispatchRule Dispatch, IReadOnlySet<string> LeftOutTypeKeys)
{
    /// <summary>Class-hierarchy dispatch with no type left out.</summary>
    public static ReachabilityRules ClassHierarchy { get; } = new(DispatchRule.ClassHierarchy, new HashSet<string>(StringComparer.Ordinal));
}

public enum ConstructionKind
{
    Controller,
    LazySingleton,
    ResolvedInExecution,
    Startup
}

public static class ConstructionKindExtensions
{
    public static string ToWireName(this ConstructionKind kind) => kind switch
    {
        ConstructionKind.Controller => "controller",
        ConstructionKind.LazySingleton => "lazy-singleton",
        ConstructionKind.ResolvedInExecution => "resolved-in-execution",
        ConstructionKind.Startup => "startup",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public enum ConstructionTriggerKind
{
    Root,
    Construction,
    Locator
}

/// <summary>What a construction runs inside: a root's execution, the construction whose constructor parameter resolved it, or a
/// locator call (<c>body:operation</c>) resolving it.</summary>
public sealed record ConstructionTrigger(ConstructionTriggerKind Kind, string Id);

/// <summary>A construction of <see cref="TypeKey"/>: a controller receiver, a hosted service or a DI service's region.
/// <see cref="ConstructorBodyIds"/> are the constructors it starts from; each reaches its chain through its calls.</summary>
public sealed record Construction(string Id, ConstructionKind Kind, string TypeKey, string? RegionId,
                                  IReadOnlyList<ConstructionTrigger> Triggers, IReadOnlyList<string> ConstructorBodyIds);

/// <summary>A member in the set, with the reason it was first reached and the ids of its nested bodies.</summary>
public sealed record ReachedMember(string MemberId, string Reason, IReadOnlyList<string> NestedBodyIds);

public sealed record UnreachedMember(string MemberId, IReadOnlyList<string> NestedBodyIds);

/// <summary>A type initializer that may run: the bodies referencing its type's statics or constructors, and the constructions
/// building its type.</summary>
public sealed record TypeInitializerCandidate(string TypeKey, string BodyId, IReadOnlyList<string> ReferencingBodyIds,
                                              IReadOnlyList<string> TriggeringConstructionIds);

/// <summary>A call into a method without a source body. <see cref="DelegateValues"/> are the delegate creations passed to it,
/// with <see cref="DelegateTargets"/> the bodies or methods they name; none of them is invoked through the call.</summary>
public sealed record OpaqueCall(int OperationId, string Callee, IReadOnlyList<int> DelegateValues, IReadOnlyList<string> DelegateTargets);

/// <summary>A factory or instance registration: its region has no construction body.</summary>
public sealed record UnanalysedRegistration(string RegionId, DiRegistration Registration);

/// <summary>What a scope can run under class-hierarchy or constructed-types dispatch. <see cref="ReachedBodies"/> maps each body a call, delegate,
/// root or construction reaches to that reason; <see cref="Bodies"/> holds every lowered body of the members in the set.</summary>
/// <param name="Members">The reached members and their nested bodies.</param>
/// <param name="ReachedBodies">The first reason each body was reached.</param>
/// <param name="Bodies">The lowered bodies in the set.</param>
/// <param name="Constructions">The constructions the set starts.</param>
/// <param name="TypeInitializers">The candidate type initializers and their triggers.</param>
/// <param name="Unreached">The members not reached.</param>
/// <param name="OpaqueCalls">The opaque calls in each body.</param>
/// <param name="UnanalysedRegistrations">The registrations without construction bodies.</param>
public sealed record ReachableSetResult(IReadOnlyList<ReachedMember> Members, IReadOnlyDictionary<string, string> ReachedBodies,
                                        IReadOnlyDictionary<string, IrBody> Bodies, IReadOnlyList<Construction> Constructions,
                                        IReadOnlyList<TypeInitializerCandidate> TypeInitializers, IReadOnlyList<UnreachedMember> Unreached,
                                        IReadOnlyDictionary<string, IReadOnlyList<OpaqueCall>> OpaqueCalls,
                                        IReadOnlyList<UnanalysedRegistration> UnanalysedRegistrations)
{
    /// <summary>How the set's calls that dispatch reached their targets.</summary>
    public DispatchRule Dispatch { get; init; } = DispatchRule.ClassHierarchy;

    /// <summary>Whether a member has a body in this run, which the lowering's, the summaries' and the heap's callees are decided by:
    /// a source body under class-hierarchy dispatch, and a body among <see cref="Bodies"/> under constructed types, so a member the
    /// set left out or never reached is one without a body, as a member without a source body is (ADR 0019).</summary>
    /// <param name="method">The member.</param>
    public bool HasBody(ProgramMethod method) =>
        Dispatch == DispatchRule.ClassHierarchy ? method.HasSourceBody : Bodies.ContainsKey(method.MethodId);
}

/// <summary>
/// Builds the reachable set from the roots: their entry bodies, the constructions they trigger with the DI services those
/// constructors take (transitively), and every body a reached body calls, dispatches to by the input's
/// <see cref="DispatchRule"/>, or invokes through a compatible delegate. A construction reached from a hosted service's
/// constructor chain is a startup construction; a singleton or root-scope region startup reaches is built once, at startup.
/// </summary>
public static class ReachableSet
{
    public const string CONTAINER_TYPE = "container";

    public static ReachableSetResult Build(ReachabilityInput input) => new Walker(input).Run();

    /// <summary>The members that run at startup as constructions of the container: the entry point, and every member holding a
    /// supported factory or instance registration, in id order.</summary>
    public static IReadOnlyList<string> StartupMembers(ProgramIndex program, DiIndex diIndex) =>
        program.Methods.Where(method => method is { IsStatic: true, HasSourceBody: true, Kind: ProgramMethodKind.Ordinary, Name: "Main" or "<Main>$" })
               .Select(method => method.MethodId)
               .Concat(diIndex.Registrations.Where(registration => registration is { IsSupported: true, BodyId: not null } &&
                                                                   registration.Form != DiRegistrationForm.Type)
                              .Select(registration => registration.BodyId!))
               .Where(member => program.Method(member) is { HasSourceBody: true })
               .Distinct(StringComparer.Ordinal)
               .Order(StringComparer.Ordinal)
               .ToArray();

    /// <summary>The method id of a spawn's parameterless work method, as the IR names it (<c>Ns.Type.Method()</c>), among the
    /// methods the index knows; null when the index has no such method.</summary>
    internal static string? WorkMethodId(ProgramIndex program, string workMethod)
    {
        var suffix = $":M:{workMethod.TrimEnd('(', ')')}";
        return program.Methods.FirstOrDefault(method => method.MethodId.EndsWith(suffix, StringComparison.Ordinal))?.MethodId;
    }

    /// <summary>What the container passes a constructor parameter of a constructed type. The bindings resolve the type's own
    /// definition, so a closed generic resolves its parameter again with the type arguments substituted: the parameter of
    /// <c>Consumer&lt;Order&gt;</c> declared as <c>Store&lt;T&gt;</c> is <c>Store&lt;Order&gt;</c>.</summary>
    internal static DiResolution ResolveConstructorParameter(ProgramIndex program, DiIndex diIndex, string typeKey,
                                                             ConstructorParameterResolution parameter)
    {
        if (program.Type(typeKey) is not { } type || type.TypeParameterKeys.Count == 0)
            return parameter.Resolution;

        var (_, arguments) = program.Decompose(typeKey);
        if (arguments.Count != type.TypeParameterKeys.Count)
            return parameter.Resolution;

        var substitution = type.TypeParameterKeys.Zip(arguments).Where(pair => pair.First != pair.Second)
                               .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return substitution.Count == 0 ? parameter.Resolution : diIndex.Resolve(ProgramIndex.Substitute(parameter.TypeKey, substitution));
    }

    /// <summary>The constructor the container selects: the one whose parameters the bindings list, or every source constructor of
    /// the type when the bindings name none and it has more than one.</summary>
    internal static IReadOnlyList<ProgramMethod> SelectedConstructors(ProgramIndex program, string typeKey, TypeInjectionBindings? bindings)
    {
        var constructors = program.Constructors(typeKey).Where(constructor => constructor.HasSourceBody).ToArray();
        var parameterKeys = bindings?.ConstructorParameters.Select(parameter => parameter.TypeKey).ToArray() ?? [];
        var selected = constructors.Where(constructor => constructor.Parameters.Select(parameter => parameter.TypeKey)
                                                                    .SequenceEqual(parameterKeys, StringComparer.Ordinal))
                                   .ToArray();
        return selected.Length == 1 && (parameterKeys.Length != 0 || constructors.Length == 1) ? selected : constructors;
    }

    private enum ResolutionContext
    {
        Startup,
        RootScope,
        Execution
    }

    /// <summary>The work values each recognized call takes: a spawn names its call, and the lowering puts a thread's or a timer's
    /// work event right after its call.</summary>
    internal static Dictionary<int, HashSet<int>> WorkOfCalls(IReadOnlyList<IrOperation> operations)
    {
        var work = new Dictionary<int, HashSet<int>>();
        IrCallOperation? previousCall = null;
        foreach (var operation in operations)
        {
            var owner = operation is IrSpawnOperation spawn ? spawn.CallOperationId : previousCall?.Id;
            var values = Walker.WorkValues(operation).Select(item => item.Value).ToArray();
            if (owner is int call && values.Length != 0)
            {
                if (!work.TryGetValue(call, out var set))
                    work.Add(call, set = []);
                set.UnionWith(values);
            }

            previousCall = operation as IrCallOperation;
        }

        return work;
    }

    private sealed class ConstructionState(string id, ConstructionKind kind, string typeKey, string? regionId)
    {
        internal string Id { get; } = id;
        internal ConstructionKind Kind { get; } = kind;
        internal string TypeKey { get; } = typeKey;
        internal string? RegionId { get; } = regionId;
        internal List<ConstructionTrigger> Triggers { get; } = [];
        internal List<string> ConstructorBodyIds { get; } = [];
    }

    private sealed class Walker
    {
        private readonly ReachabilityInput _input;
        private readonly ProgramIndex _program;
        private readonly Dictionary<string, string> _memberOfNestedBody = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ProgramMethod>> _dispatchTargets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Reason, IReadOnlyList<IrBody> Bodies)> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IrBody> _bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reachedBodies = new(StringComparer.Ordinal);
        private readonly Queue<string> _pending = new();
        private readonly Dictionary<string, ConstructionState> _constructions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _startupRegions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string BodyId, List<string> Referencing, List<string> Constructions)> _typeInitializers =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<OpaqueCall>> _opaqueCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Type, string Target)>> _delegateTargets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Type, string Reason)>> _delegateInvocations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _typeParameterNames;
        private readonly Dictionary<string, string> _sourceDelegateTypeKeys;
        private readonly HashSet<string> _leftOutMembers;
        private readonly Dictionary<string, string[]> _valueTypeKeys;
        private readonly Dictionary<string, ProgramType[]> _typesByDisplayShape;
        private readonly Dictionary<(string Owner, int Ordinal), HashSet<string>> _parameterBindings = new();
        private readonly Dictionary<(string Owner, int Ordinal), HashSet<(string Owner, int Ordinal)>> _parameterEdges = new();
        private readonly HashSet<(string Owner, int Ordinal)> _boxedParameters = new();
        private readonly HashSet<string> _instantiated = new(StringComparer.Ordinal);
        private readonly HashSet<string> _constructedTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Target, Action<string> Reach)>> _waiting = new(StringComparer.Ordinal);

        internal Walker(ReachabilityInput input)
        {
            _input = input;
            _program = input.Program;
            _leftOutMembers = _program.Methods.Where(method => IsLeftOutType(method.ContainingTypeKey))
                                      .Select(method => method.MethodId)
                                      .ToHashSet(StringComparer.Ordinal);
            _valueTypeKeys = _program.Types.Where(type => type.IsValueType)
                                     .GroupBy(type => Shape(type.DisplayName), StringComparer.Ordinal)
                                     .ToDictionary(group => group.Key, group => group.Select(type => type.TypeKey).ToArray(), StringComparer.Ordinal);
            _typesByDisplayShape = _program.Types.GroupBy(type => Shape(type.DisplayName), StringComparer.Ordinal)
                                          .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            _typeParameterNames = _program.Types.SelectMany(type => type.TypeParameterKeys)
                                          .Concat(_program.Methods.SelectMany(method => method.TypeParameterKeys))
                                          .Select(key => key[(key.IndexOf(':') + 1)..])
                                          .ToHashSet(StringComparer.Ordinal);
            _sourceDelegateTypeKeys = _program.Types.Where(type => type.IsDelegate)
                                              .GroupBy(type => Shape(type.DisplayName), StringComparer.Ordinal)
                                              .Where(group => group.Count() == 1)
                                              .ToDictionary(group => group.Key, group => group.Single().TypeKey, StringComparer.Ordinal);
            foreach (var method in _program.Methods)
            {
                foreach (var nested in method.NestedBodyIds)
                    _memberOfNestedBody.TryAdd(nested, method.MethodId);
            }

            foreach (var method in _program.Methods)
            {
                var ancestors = new List<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var overridden = method.OverriddenMethodId; overridden is not null && visited.Add(overridden);
                     overridden = _program.Method(overridden)?.OverriddenMethodId)
                {
                    ancestors.Add(overridden);
                }

                var interfaceMethods = method.ImplementedInterfaceMethodIds
                                             .Concat(ancestors.SelectMany(ancestor => _program.Method(ancestor)?.ImplementedInterfaceMethodIds ?? []));
                foreach (var dispatched in ancestors.Concat(interfaceMethods).Distinct(StringComparer.Ordinal))
                {
                    if (!_dispatchTargets.TryGetValue(dispatched, out var targets))
                        _dispatchTargets.Add(dispatched, targets = []);
                    targets.Add(method);
                }
            }
        }

        internal ReachableSetResult Run()
        {
            foreach (var member in StartupMembers(_program, _input.DiIndex))
            {
                var construction = new ConstructionState($"startup:{member}", ConstructionKind.Startup, CONTAINER_TYPE, null);
                construction.ConstructorBodyIds.Add(member);
                _constructions.Add(construction.Id, construction);
                ReachBody(member, $"construction:{construction.Id}");
            }

            var roots = _input.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal).ToArray();
            foreach (var root in roots.Where(root => root.InstanceBindings is { Receiver: ReceiverKind.HostedService, ReceiverTypeKey: not null }))
            {
                var trigger = new ConstructionTrigger(ConstructionTriggerKind.Root, root.StableRootId);
                Construct(ConstructionKind.Startup, root.InstanceBindings.ReceiverTypeKey!, DiLifetime.Singleton, null, trigger,
                          ResolutionContext.Startup);
            }

            foreach (var root in roots)
            {
                var trigger = new ConstructionTrigger(ConstructionTriggerKind.Root, root.StableRootId);
                ReachBody(root.Entry.BodyKey, $"root:{root.StableRootId}");
                if (root.InstanceBindings is { Receiver: ReceiverKind.PerInvocation, ReceiverTypeKey: { } controller })
                    Construct(ConstructionKind.Controller, controller, null, null, trigger, ResolutionContext.Execution);
                else if (root.InstanceBindings is { Receiver: ReceiverKind.DiService, ReceiverTypeKey: { } service })
                    Resolve(_input.DiIndex.Resolve(service), trigger, ResolutionContext.Execution);
                foreach (var parameter in root.InstanceBindings.Parameters.Where(parameter => parameter is { Kind: ParameterBindingKind.DiService, TypeKey: not null }))
                    Resolve(_input.DiIndex.Resolve(parameter.TypeKey!), trigger, ResolutionContext.Execution);
            }

            while (_pending.TryDequeue(out var bodyId))
                Process(bodyId);

            return Result();
        }

        private ReachableSetResult Result()
        {
            var members = _members.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .Select(pair => new ReachedMember(pair.Key, pair.Value.Reason, NestedIds(pair.Key, pair.Value.Bodies)))
                                  .ToArray();
            var unreached = _program.Methods.Where(method => method.HasSourceBody && !_members.ContainsKey(method.MethodId))
                                    .OrderBy(method => method.MethodId, StringComparer.Ordinal)
                                    .Select(method => new UnreachedMember(method.MethodId, method.NestedBodyIds))
                                    .ToArray();
            var constructions = _constructions.Values.OrderBy(construction => construction.Id, StringComparer.Ordinal)
                                              .Select(construction => new Construction(construction.Id, construction.Kind, construction.TypeKey,
                                                                                       construction.RegionId, construction.Triggers.ToArray(),
                                                                                       construction.ConstructorBodyIds.ToArray()))
                                              .ToArray();
            var typeInitializers = _typeInitializers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                    .Select(pair => new TypeInitializerCandidate(pair.Key, pair.Value.BodyId,
                                                                                                 pair.Value.Referencing.ToArray(),
                                                                                                 pair.Value.Constructions.ToArray()))
                                                    .ToArray();
            var unanalysed = _input.DiIndex.Registrations
                                   .Where(registration => registration is { IsSupported: true, ImplementationType: not null, ServiceTypeKey: not null } &&
                                                          registration.Form != DiRegistrationForm.Type)
                                   .Select(registration => new UnanalysedRegistration(
                                               DiIndex.RegionId(registration.ServiceTypeKey!, registration.ImplementationTypeKey!,
                                                                registration.Lifetime, registration.Number),
                                               registration))
                                   .ToArray();
            return new ReachableSetResult(members, new Dictionary<string, string>(_reachedBodies, StringComparer.Ordinal),
                                          new Dictionary<string, IrBody>(_bodies, StringComparer.Ordinal), constructions, typeInitializers,
                                          unreached,
                                          _opaqueCalls.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<OpaqueCall>)pair.Value.ToArray(),
                                                                    StringComparer.Ordinal),
                                          unanalysed)
            {
                Dispatch = _input.Rules.Dispatch
            };
        }

        private IReadOnlyList<string> NestedIds(string memberId, IReadOnlyList<IrBody> bodies) =>
            bodies.Count == 0
                ? _program.Method(memberId)?.NestedBodyIds ?? []
                : bodies.Select(body => body.BodyId).Where(id => id != memberId).ToArray();

        /// <summary>Resolves a service for a construction or a root. Startup resolves everything at startup; otherwise a singleton,
        /// and a scoped service resolved from the root scope, is a lazily resolved construction per region, unless startup
        /// already builds that region, and any other service is built inside the resolving execution.</summary>
        /// <param name="resolution">The DI resolution of the service.</param>
        /// <param name="trigger">What asks for the service: a construction or a root.</param>
        /// <param name="context">Where the resolution happens.</param>
        private void Resolve(DiResolution resolution, ConstructionTrigger trigger, ResolutionContext context)
        {
            if (resolution is not { Kind: DiResolutionKind.Bound, Binding: { } binding } ||
                resolution.Registrations.All(registration => registration.Form != DiRegistrationForm.Type))
            {
                return;
            }

            var kind = context == ResolutionContext.Startup ? ConstructionKind.Startup
                : binding.Lifetime == DiLifetime.Singleton || binding.Lifetime == DiLifetime.Scoped && context == ResolutionContext.RootScope
                    ? ConstructionKind.LazySingleton
                    : ConstructionKind.ResolvedInExecution;
            if (kind == ConstructionKind.LazySingleton && _startupRegions.Contains(RegionKey(binding.ImplementationTypeKey, binding.Lifetime)))
                return;

            var childContext = kind switch
            {
                ConstructionKind.Startup => ResolutionContext.Startup,
                ConstructionKind.LazySingleton => ResolutionContext.RootScope,
                _ => context
            };
            Construct(kind, binding.ImplementationTypeKey, binding.Lifetime, binding.RegionId, trigger, childContext);
        }

        private void Construct(ConstructionKind kind, string typeKey, DiLifetime? lifetime, string? regionId, ConstructionTrigger trigger,
                               ResolutionContext childContext)
        {
            var id = lifetime is { } known ? $"{kind.ToWireName()}:{RegionKey(typeKey, known)}" : $"{kind.ToWireName()}:{typeKey}";
            if (_constructions.TryGetValue(id, out var existing))
            {
                if (!existing.Triggers.Contains(trigger))
                    existing.Triggers.Add(trigger);
                return;
            }

            if (_program.Type(typeKey) is not { IsValueType: true })
                Instantiate(typeKey);
            var construction = new ConstructionState(id, kind, typeKey, regionId);
            construction.Triggers.Add(trigger);
            _constructions.Add(id, construction);
            if (kind == ConstructionKind.Startup && lifetime is { } startupLifetime && startupLifetime != DiLifetime.Transient)
                _startupRegions.Add(RegionKey(typeKey, startupLifetime));

            var bindings = _program.Type(typeKey) is { } type
                ? _input.InjectionBindings.FirstOrDefault(candidate => candidate.TypeKey == type.TypeKey)
                : null;
            foreach (var constructor in SelectedConstructors(_program, typeKey, bindings))
            {
                construction.ConstructorBodyIds.Add(constructor.MethodId);
                ReachBody(constructor.MethodId, $"construction:{id}");
            }

            if (_program.Type(typeKey) is { } built)
                AddTypeInitializerTrigger(built.TypeKey, id);

            var self = new ConstructionTrigger(ConstructionTriggerKind.Construction, id);
            foreach (var parameter in bindings?.ConstructorParameters ?? [])
                Resolve(ResolveConstructorParameter(_program, _input.DiIndex, typeKey, parameter), self, childContext);
        }

        private static string RegionKey(string implementationTypeKey, DiLifetime lifetime) => $"{implementationTypeKey}@{lifetime}";

        private void ReachBody(string bodyId, string reason)
        {
            if (_reachedBodies.ContainsKey(bodyId) || _leftOutMembers.Contains(_memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId))
                return;

            var memberId = _memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId;
            if (!_members.ContainsKey(memberId))
            {
                var bodies = _input.Members(memberId);
                _members.Add(memberId, (reason, bodies));
                foreach (var body in bodies)
                    _bodies.TryAdd(body.BodyId, body);
            }

            _reachedBodies.Add(bodyId, reason);
            _pending.Enqueue(bodyId);
        }

        private void Process(string bodyId)
        {
            if (!_bodies.TryGetValue(bodyId, out var body))
                return;

            var values = body.Values.ToDictionary(value => value.Id);
            var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            var definitions = new Dictionary<int, IrOperation>();
            foreach (var operation in operations)
            {
                foreach (var defined in operation.DefinedValues)
                    definitions[defined] = operation;
            }

            var work = WorkOfCalls(operations);

            foreach (var operation in operations)
            {
                var reason = $"call:{bodyId}:{operation.Id}";
                if (_input.Rules.Dispatch == DispatchRule.ConstructedTypes)
                    Instantiate(operation, values, bodyId);
                switch (operation)
                {
                    case IrLoadFieldOperation { Field.IsStatic: true } load:
                        Reference(DiIndex.TypeKey(load.Field.Assembly, load.Field.ContainingTypeId), bodyId);
                        break;
                    case IrStoreFieldOperation { Field.IsStatic: true } store:
                        Reference(DiIndex.TypeKey(store.Field.Assembly, store.Field.ContainingTypeId), bodyId);
                        break;
                    case IrCallOperation { CallKind: IrCallKind.LocalFunction } call:
                        ReachBody(call.Method, reason);
                        break;
                    case IrCallOperation { CallKind: IrCallKind.Delegate } call:
                        if (call.ReceiverValue is int receiver && values.TryGetValue(receiver, out var delegateValue))
                            AddDelegateInvocation(delegateValue.Type, $"delegate:{bodyId}:{operation.Id}");
                        // The delegate invoked may be one the analysis cannot follow, which may run what it is handed (R3).
                        ReachHandedDelegates(call.ArgumentValues, values, definitions, operations, reason);
                        break;
                    // A delegate put into an array's cell is followed by nothing that invokes it by type: whatever the array reaches may run
                    // it, so it is reached where it is stored (R3).
                    case IrStoreElementOperation element when DelegateCreation(element.Value, definitions) is { } stored:
                        ReachDelegateTargets(stored, reason);
                        break;
                    case IrCallOperation { TargetMethodId: { } targetId } call:
                    {
                        var dispatched = call.CallKind is IrCallKind.Virtual or IrCallKind.Interface;
                        var targets = Targets(targetId, dispatched);
                        if (targets.Count == 0)
                        {
                            RecordOpaque(bodyId, call, definitions, work.GetValueOrDefault(call.Id) ?? []);
                            ReachFactory(bodyId, call, definitions, reason);
                            Locate(bodyId, call);
                            // The factories of `GetOrAdd` and `AddOrUpdate` run where the call stands (R11): one created in the body is
                            // reached by its target, and one read from a field or a parameter as a call of a delegate of its type is.
                            if (InterproceduralAccesses.RunsFactories(call.Collection))
                            {
                                foreach (var ordinal in call.Collection!.Factories)
                                {
                                    if (call.ArgumentAt(ordinal) is not int factory)
                                        continue;
                                    if (DelegateCreation(factory, definitions) is { } creation)
                                        ReachDelegateTargets(creation, reason);
                                    else if (values.TryGetValue(factory, out var factoryValue))
                                        AddDelegateInvocation(factoryValue.Type, $"delegate:{bodyId}:{call.Id}");
                                }
                            }

                            // The delegates a known call runs by its model's fates are reached as those factories are (R3).
                            if (call.Library is { InRange: true, DeclaredOpaque: false } library)
                            {
                                // A not-run delegate is neither run nor kept by the call (ADR 0015): it reaches nothing.
                                foreach (var fate in library.Fates.Where(fate => fate.Kind != IrFateKind.NotRun))
                                {
                                    if (call.ArgumentAt(fate.ParameterOrdinal) is not int handed)
                                        continue;
                                    if (DelegateCreation(handed, definitions) is { } creation)
                                        ReachDelegateTargets(creation, reason);
                                    else if (values.TryGetValue(handed, out var handedValue))
                                        AddDelegateInvocation(handedValue.Type, $"delegate:{bodyId}:{call.Id}");
                                }
                            }
                        }

                        // A delegate handed to a call the heap may leave unresolved runs in an unknown execution (R3): an opaque call no
                        // recognizer and no table entry models, or a dispatch that may find no receiver object.
                        if (targets.Count == 0 ? !call.IsRecognized && call.Library is not { InRange: true, DeclaredOpaque: false } : call.CallKind is IrCallKind.Virtual or IrCallKind.Interface)
                            ReachHandedDelegates(call.ArgumentValues.Where(value => work.GetValueOrDefault(call.Id)?.Contains(value) != true), values,
                                                 definitions, operations, reason);
                        // A delegate handed over as an object is followed by nothing that invokes it by type: whatever the callee hands it
                        // to may run it, so it is reached here, unless a table entry or a recognizer says what the call does (R3).
                        else if (!call.IsRecognized && call.Library is not { InRange: true, DeclaredOpaque: false })
                        {
                            foreach (var untyped in call.ArgumentValues.Where(value => values.TryGetValue(value, out var argument) && IsUntyped(argument.Type))
                                                        .Select(value => DelegateCreation(value, definitions)).OfType<IrCreateDelegateOperation>())
                                ReachDelegateTargets(untyped, reason);
                        }
                        ReachTargets(targets, dispatched, target => ReachBody(target, reason));
                        if (call.CallKind is IrCallKind.Static or IrCallKind.Constructor && _program.Method(targetId) is { } method)
                            Reference(method.ContainingTypeKey, bodyId);
                        break;
                    }
                    case IrCreateDelegateOperation create when values.TryGetValue(create.ResultValue, out var created):
                        AddDelegateCreation(created.Type, create, bodyId);
                        break;
                    case IrUnknownOperation { DynamicCallee: not null } dynamic:
                        ReachHandedDelegates(dynamic.OperandValues, values, definitions, operations, reason);
                        break;
                    default:
                        foreach (var (value, method) in WorkValues(operation))
                            ReachWork(value, method, values, definitions, $"spawn:{bodyId}:{operation.Id}");
                        break;
                }
            }
        }

        /// <summary>The work a spawn, a thread constructor or a timer runs: each work or callback value, with the method a spawn
        /// calls on it instead of invoking it.</summary>
        internal static IEnumerable<(int Value, string? Method)> WorkValues(IrOperation operation) => operation switch
        {
            IrSpawnOperation spawn => spawn.WorkValues.Select(value => (value, spawn.WorkMethod)),
            IrThreadWorkOperation threadWork => [(threadWork.WorkValue, null)],
            IrTimerOperation { CallbackValue: int callback } => [(callback, null)],
            _ => []
        };

        /// <summary>Reaches what a spawn or a timer runs, as a DI factory's delegate: the targets of a delegate created in the body,
        /// every delegate of the value's type when it comes from elsewhere, or the implementations of the method it calls.</summary>
        private void ReachWork(int value, string? method, IReadOnlyDictionary<int, IrValue> values,
                               IReadOnlyDictionary<int, IrOperation> definitions, string reason)
        {
            if (method is not null)
            {
                if (WorkMethodId(_program, method) is { } methodId)
                    ReachTargets(Targets(methodId, virtualDispatch: true), virtualDispatch: true, target => ReachBody(target, reason));

                return;
            }

            if (DelegateCreation(value, definitions) is { } creation)
                ReachDelegateTargets(creation, reason);
            else if (values.TryGetValue(value, out var delegateValue))
                AddDelegateInvocation(delegateValue.Type, reason);
        }

        private void AddDelegateCreation(string type, IrCreateDelegateOperation create, string bodyId)
        {
            if (create.TargetBodyId is { } nested)
            {
                AddDelegateTarget(type, nested);
            }
            else if (create.TargetMethodId is { } methodId && _program.Method(methodId) is { } method &&
                     (DelegateTypeKey(type) is not { } delegateTypeKey || _program.IsDelegateCompatible(delegateTypeKey, methodId)))
            {
                var virtualDispatch = !create.IsNonVirtual && (method.IsVirtual || method.IsAbstract || method.IsOverride ||
                                                               _program.Type(method.ContainingTypeKey)?.IsInterface == true);
                var targets = Targets(methodId, virtualDispatch);
                if (method.IsStatic)
                    Reference(method.ContainingTypeKey, bodyId);
                ReachTargets(targets, virtualDispatch, target => AddDelegateTarget(type, target));
            }
        }

        /// <summary>Makes a body a target of the delegates created as <paramref name="type"/>, reached at once when an invocation of
        /// a matching delegate type is already known.</summary>
        /// <param name="type">The created delegate's type.</param>
        /// <param name="target">The body the delegate runs.</param>
        private void AddDelegateTarget(string type, string target)
        {
            var shape = Shape(type);
            if (!_delegateTargets.TryGetValue(shape, out var known))
                _delegateTargets.Add(shape, known = []);
            if (known.Contains((type, target)))
                return;

            known.Add((type, target));
            var invocation = (_delegateInvocations.GetValueOrDefault(shape) ?? [])
                             .Where(candidate => Invokes(candidate.Type, type))
                             .Select(candidate => candidate.Reason)
                             .FirstOrDefault();
            if (invocation is not null)
                ReachBody(target, invocation);
        }

        private void AddDelegateInvocation(string type, string reason)
        {
            var shape = Shape(type);
            if (!_delegateInvocations.TryGetValue(shape, out var invocations))
                _delegateInvocations.Add(shape, invocations = []);
            invocations.Add((type, reason));
            foreach (var target in (_delegateTargets.GetValueOrDefault(shape) ?? []).Where(target => Invokes(type, target.Type)))
                ReachBody(target.Target, reason);
        }

        /// <summary>Whether an invocation of one delegate type may run a delegate created as another. Two closed types run each
        /// other's delegates only when they are the same type, so an <c>Action&lt;int&gt;</c> invocation never reaches an
        /// <c>Action&lt;string&gt;</c> method group; while either side still names a type parameter, the analysis has no
        /// substitution here and matches the shapes, so a <c>Func&lt;T&gt;</c> created in generic code is invoked as
        /// <c>Func&lt;Order&gt;</c>.</summary>
        private bool Invokes(string invokedType, string createdType) =>
            IsOpen(invokedType) || IsOpen(createdType) || invokedType == createdType;

        private bool IsOpen(string type) =>
            type.Split(['<', '>', ',', ' ', '[', ']', '*', '?'], StringSplitOptions.RemoveEmptyEntries).Any(_typeParameterNames.Contains);

        /// <summary>The type key of a delegate the scope declares, or null for a framework delegate the index does not know.</summary>
        private string? DelegateTypeKey(string type) => _sourceDelegateTypeKeys.GetValueOrDefault(Shape(type));

        /// <summary>The source bodies a call of <paramref name="methodId"/> may run: every source override or implementation under
        /// virtual dispatch, and the method itself when it has a body, but never a member of a left-out type. A call this leaves
        /// nothing has no target; one whose targets wait for their types to be constructed still has them.</summary>
        /// <param name="methodId">The method the call, delegate or work names.</param>
        /// <param name="virtualDispatch">Whether the call dispatches.</param>
        private IReadOnlyList<ProgramMethod> Targets(string methodId, bool virtualDispatch)
        {
            var targets = new List<ProgramMethod>();
            if (_program.Method(methodId) is { HasSourceBody: true } self)
                targets.Add(self);
            if (virtualDispatch)
                targets.AddRange(_dispatchTargets.GetValueOrDefault(methodId)?.Where(target => target.HasSourceBody) ?? []);
            targets.RemoveAll(target => _leftOutMembers.Contains(target.MethodId));
            return targets;
        }

        /// <summary>Reaches the targets of a call. Under <see cref="DispatchRule.ConstructedTypes"/> a target of a call that dispatches
        /// is reached only once its declaring type, or a type derived from it, is constructed; until then it waits, with what the place
        /// that asked does on reaching it, and is reached when that type is (ADR 0019).</summary>
        /// <param name="targets">The targets <see cref="Targets"/> gave.</param>
        /// <param name="virtualDispatch">Whether the call dispatches.</param>
        /// <param name="reach">What reaching a target does at the place that asked.</param>
        private void ReachTargets(IReadOnlyList<ProgramMethod> targets, bool virtualDispatch, Action<string> reach)
        {
            foreach (var target in targets)
            {
                var declaringType = _program.Type(target.ContainingTypeKey)?.TypeKey ?? target.ContainingTypeKey;
                if (!virtualDispatch || _input.Rules.Dispatch == DispatchRule.ClassHierarchy || _constructedTypes.Contains(declaringType))
                {
                    reach(target.MethodId);
                    continue;
                }

                if (!_waiting.TryGetValue(declaringType, out var waiting))
                    _waiting.Add(declaringType, waiting = []);
                waiting.Add((target.MethodId, reach));
            }
        }

        /// <summary>Records what an operation constructs under <see cref="DispatchRule.ConstructedTypes"/>: the type a constructor call
        /// creates, unless it is a value type, and the value type a boxing conversion boxes or a delegate created on a value's method
        /// binds, which boxes it with no conversion lowered. Boxing a type parameter admits only value types bound to that
        /// owner's parameter, including bindings and parameter-to-parameter edges discovered after the boxing.</summary>
        /// <param name="operation">The operation of a reached body.</param>
        /// <param name="values">The body's values by id.</param>
        /// <param name="bodyId">The reached body whose method and containing types own its type parameters.</param>
        private void Instantiate(IrOperation operation, IReadOnlyDictionary<int, IrValue> values, string bodyId)
        {
            if (_input.Rules.Dispatch != DispatchRule.ConstructedTypes)
                return;

            var owner = _program.Method(_memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId);
            var (methodId, containingType, arguments) = operation switch
            {
                IrCallOperation call => (call.TargetMethodId, call.TargetContainingTypeKey, call.TargetMethodTypeArgumentKeys),
                IrCreateDelegateOperation creation => (creation.TargetMethodId, creation.TargetContainingTypeKey, creation.TargetMethodTypeArgumentKeys),
                _ => (null, null, (IReadOnlyList<string>)[])
            };
            if (methodId is not null && _program.Method(methodId) is { } method)
            {
                var dispatch = operation is IrCallOperation { CallKind: IrCallKind.Virtual or IrCallKind.Interface } ||
                               operation is IrCreateDelegateOperation { IsNonVirtual: false } &&
                               (method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true);
                // Bind even targets still waiting for construction: their parameter edges must also receive later bindings.
                foreach (var target in Targets(methodId, dispatch).Prepend(method).DistinctBy(target => target.MethodId))
                    for (var ordinal = 0; ordinal < Math.Min(arguments.Count, target.TypeParameterKeys.Count); ordinal++)
                        Bind((target.MethodId, ordinal), arguments[ordinal]);
            }
            if (containingType is not null)
                SeeType(containingType);

            switch (operation)
            {
                case IrCallOperation { CallKind: IrCallKind.Constructor } call
                    when (call.TargetContainingTypeKey ?? (call.TargetMethodId is { } id ? _program.Method(id)?.ContainingTypeKey : null)) is { } typeKey &&
                         _program.Type(typeKey) is not { IsValueType: true }:
                    Instantiate(typeKey);
                    break;
                case IrConvertOperation { ConversionKind: IrConversionKind.Boxing } boxing when values.TryGetValue(boxing.OperandValue, out var boxed):
                    Box(boxed.Type);
                    break;
                case IrCreateDelegateOperation { ReceiverValue: int receiver } when values.TryGetValue(receiver, out var bound):
                    Box(bound.Type);
                    break;
            }

            void SeeType(string key)
            {
                var typeArguments = ProgramIndex.Shape(key).Arguments;
                var types = _program.Type(key) is { } type ? [type] : _typesByDisplayShape.GetValueOrDefault(Shape(key)) ?? [];
                foreach (var definition in types)
                    for (var ordinal = 0; ordinal < Math.Min(typeArguments.Count, definition.TypeParameterKeys.Count); ordinal++)
                        Bind(TypeParameter(definition, ordinal), typeArguments[ordinal]);
                foreach (var argument in typeArguments)
                    SeeType(argument);
            }

            void Bind((string Owner, int Ordinal) parameter, string key)
            {
                key = UnwrapNullable(key);
                if (Parameter(key, owner) is { } source)
                {
                    if (!_parameterEdges.TryGetValue(source, out var edges))
                        _parameterEdges[source] = edges = [];
                    if (edges.Add(parameter))
                        foreach (var binding in (_parameterBindings.GetValueOrDefault(source) ?? []).ToArray())
                            BindValue(parameter, binding);
                }
                else
                {
                    var candidates = _program.Type(key) is { IsValueType: true } type
                        ? [type.TypeKey] : _valueTypeKeys.GetValueOrDefault(Shape(key)) ?? [];
                    foreach (var candidate in candidates)
                        BindValue(parameter, candidate);
                }
            }

            void Box(string type)
            {
                type = type.TrimEnd('?');
                if (Parameter(type, owner) is { } parameter)
                {
                    _boxedParameters.Add(parameter);
                    foreach (var argument in _parameterBindings.GetValueOrDefault(parameter) ?? [])
                        Instantiate(argument);
                }
                else
                {
                    foreach (var key in _valueTypeKeys.GetValueOrDefault(Shape(type)) ?? [])
                        Instantiate(key);
                }
            }
        }

        private static string UnwrapNullable(string key)
        {
            key = key.Trim().TrimEnd('?');
            var (shape, arguments) = ProgramIndex.Shape(key);
            return arguments.Count == 1 && (shape == "System.Nullable<1>" || shape.EndsWith(":System.Nullable<1>", StringComparison.Ordinal))
                ? arguments[0] : key;
        }

        private (string Owner, int Ordinal)? Parameter(string key, ProgramMethod? method)
        {
            if (method is null)
                return null;
            var name = key[(key.IndexOf(':') + 1)..];
            for (var ordinal = 0; ordinal < method.TypeParameterKeys.Count; ordinal++)
                if (Name(method.TypeParameterKeys[ordinal]) == name)
                    return (method.MethodId, ordinal);
            if (_program.Type(method.ContainingTypeKey) is { } type)
                for (var ordinal = type.TypeParameterKeys.Count - 1; ordinal >= 0; ordinal--)
                    if (Name(type.TypeParameterKeys[ordinal]) == name)
                        return TypeParameter(type, ordinal);
            return null;

            static string Name(string parameter) => parameter[(parameter.IndexOf(':') + 1)..];
        }

        private (string Owner, int Ordinal) TypeParameter(ProgramType type, int ordinal)
        {
            var depth = 0;
            for (var index = type.TypeKey.Length - 1; index >= 0; index--)
            {
                var character = type.TypeKey[index];
                if (character == '>') depth++;
                else if (character == '<') depth--;
                else if (character == '.' && depth == 0 && _program.Type(type.TypeKey[..index]) is { } outer)
                    return ordinal < outer.TypeParameterKeys.Count
                        ? TypeParameter(outer, ordinal) : (type.TypeKey, ordinal - outer.TypeParameterKeys.Count);
            }
            return (type.TypeKey, ordinal);
        }

        private void BindValue((string Owner, int Ordinal) parameter, string typeKey)
        {
            var pending = new Queue<(string Owner, int Ordinal)>();
            pending.Enqueue(parameter);
            while (pending.TryDequeue(out var current))
            {
                if (!_parameterBindings.TryGetValue(current, out var bindings))
                    _parameterBindings[current] = bindings = new(StringComparer.Ordinal);
                if (!bindings.Add(typeKey))
                    continue;
                if (_boxedParameters.Contains(current))
                    Instantiate(typeKey);
                foreach (var target in _parameterEdges.GetValueOrDefault(current) ?? [])
                    pending.Enqueue(target);
            }
        }

        /// <summary>Records a type as constructed: it and every type it derives from or implements now have their overrides reached,
        /// and the targets waiting for any of them are reached.</summary>
        /// <param name="typeKey">The constructed or boxed type.</param>
        private void Instantiate(string typeKey)
        {
            if (_input.Rules.Dispatch != DispatchRule.ConstructedTypes || !_instantiated.Add(typeKey))
                return;

            foreach (var supertype in _program.Supertypes(typeKey))
            {
                var definition = _program.Type(supertype)?.TypeKey ?? supertype;
                if (!_constructedTypes.Add(definition) || !_waiting.Remove(definition, out var waiting))
                    continue;
                foreach (var (target, reach) in waiting)
                    reach(target);
            }
        }

        private bool IsLeftOutType(string typeKey) =>
            _input.Rules.LeftOutTypeKeys.Contains(_program.Type(typeKey)?.TypeKey ?? typeKey);

        private void RecordOpaque(string bodyId, IrCallOperation call, IReadOnlyDictionary<int, IrOperation> definitions, IReadOnlySet<int> work)
        {
            var creations = call.ArgumentValues.Where(value => !work.Contains(value))
                                .Select(value => DelegateCreation(value, definitions)).OfType<IrCreateDelegateOperation>().ToArray();
            if (!_opaqueCalls.TryGetValue(bodyId, out var calls))
                _opaqueCalls.Add(bodyId, calls = []);
            calls.Add(new OpaqueCall(call.Id, call.Method, creations.Select(creation => creation.ResultValue).ToArray(),
                                     creations.Select(creation => creation.TargetBodyId ?? creation.TargetMethodId ?? creation.TargetMethod ?? "?")
                                              .ToArray()));
        }

        /// <summary>A factory registration's delegate targets run as the construction of its region, so the set reaches them.</summary>
        private void ReachFactory(string bodyId, IrCallOperation call, IReadOnlyDictionary<int, IrOperation> definitions, string reason)
        {
            var memberId = _memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId;
            if (call.ArgumentValues.Count == 0 ||
                !_input.DiIndex.Registrations.Any(registration => registration is { IsSupported: true, Form: DiRegistrationForm.Factory } &&
                                                                  registration.BodyId == memberId && registration.OperationId == call.Id) ||
                // The factory is the member's last parameter, taken by its ordinal: a named argument may be written anywhere.
                call.LastArgument() is not { } factory || DelegateCreation(factory, definitions) is not { } creation)
            {
                return;
            }

            ReachDelegateTargets(creation, reason);
        }

        /// <summary>Reaches the delegates handed to a call as its arguments or operands, as a spawn's work is reached: the targets of a
        /// delegate created in the body, every delegate of the value's type when it comes from a parameter or a field, and the elements
        /// of an array the call is handed in their place (R3).</summary>
        private void ReachHandedDelegates(IEnumerable<int> handed, IReadOnlyDictionary<int, IrValue> values,
                                          IReadOnlyDictionary<int, IrOperation> definitions, IReadOnlyList<IrOperation> operations, string reason,
                                          HashSet<int>? arrays = null)
        {
            arrays ??= [];
            foreach (var value in handed)
            {
                if (DelegateCreation(value, definitions) is { } creation)
                {
                    ReachDelegateTargets(creation, reason);
                    continue;
                }

                if (Origin(value, definitions) is var array && definitions.GetValueOrDefault(array) is IrAllocateOperation)
                {
                    if (arrays.Add(array))
                    {
                        ReachHandedDelegates(operations.OfType<IrStoreElementOperation>().Where(store => Origin(store.ReceiverValue, definitions) == array)
                                                       .Select(store => store.Value),
                                             values, definitions, operations, reason, arrays);
                    }

                    continue;
                }

                if (values.TryGetValue(value, out var delegateValue))
                    AddDelegateInvocation(delegateValue.Type, reason);
            }
        }

        /// <summary>Whether a value's type is no delegate type at all, only what one converts to.</summary>
        private static bool IsUntyped(string type) =>
            type is "object" or "dynamic" or "System.Object" or "System.Delegate" or "Delegate" or "System.MulticastDelegate" or "MulticastDelegate";

        /// <summary>The value another is assigned or converted from.</summary>
        private static int Origin(int value, IReadOnlyDictionary<int, IrOperation> definitions)
        {
            var visited = new HashSet<int>();
            while (visited.Add(value) && definitions.TryGetValue(value, out var definition))
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

        /// <summary>Reaches a created delegate's nested body, or every body its method group may run.</summary>
        private void ReachDelegateTargets(IrCreateDelegateOperation creation, string reason)
        {
            if (creation.TargetBodyId is { } nested)
            {
                ReachBody(nested, reason);
                return;
            }

            if (creation.TargetMethodId is not { } methodId || _program.Method(methodId) is not { } method)
                return;
            var virtualDispatch = !creation.IsNonVirtual &&
                                  (method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true);
            ReachTargets(Targets(methodId, virtualDispatch), virtualDispatch, target => ReachBody(target, reason));
        }

        /// <summary>A locator call with a constant type constructs what it resolves: the bound registration, or every supported
        /// registration for <c>GetServices</c>.</summary>
        private void Locate(string bodyId, IrCallOperation call)
        {
            if (call.ServiceCall is not { Kind: IrServiceCallKind.Locator or IrServiceCallKind.LocatorAll, ServiceTypeKey: { } key } service)
                return;

            var trigger = new ConstructionTrigger(ConstructionTriggerKind.Locator, $"{bodyId}:{call.Id}");
            var resolution = _input.DiIndex.Resolve(key);
            if (service.Kind == IrServiceCallKind.Locator)
            {
                Resolve(resolution, trigger, ResolutionContext.Execution);
                return;
            }

            foreach (var registration in resolution.Registrations.Where(registration => registration is
                         { IsSupported: true, IsHostedService: false, Form: DiRegistrationForm.Type, ImplementationTypeKey: not null }))
            {
                var kind = registration.Lifetime == DiLifetime.Singleton ? ConstructionKind.LazySingleton : ConstructionKind.ResolvedInExecution;
                Construct(kind, registration.ImplementationTypeKey!, registration.Lifetime,
                          DiIndex.RegionId(key, registration.ImplementationTypeKey!, registration.Lifetime, registration.Number), trigger,
                          kind == ConstructionKind.LazySingleton ? ResolutionContext.RootScope : ResolutionContext.Execution);
            }
        }

        private static IrCreateDelegateOperation? DelegateCreation(int value, IReadOnlyDictionary<int, IrOperation> definitions)
        {
            var visited = new HashSet<int>();
            while (visited.Add(value) && definitions.TryGetValue(value, out var definition))
            {
                switch (definition)
                {
                    case IrCreateDelegateOperation create:
                        return create;
                    case IrAssignOperation assign:
                        value = assign.SourceValue;
                        break;
                    case IrConvertOperation convert:
                        value = convert.OperandValue;
                        break;
                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>A reference from <paramref name="bodyId"/> to a static member or constructor of a type: that type's type
        /// initializer, unless the reference is in it, is a candidate reached for lowering.</summary>
        private void Reference(string typeKey, string bodyId)
        {
            if (_program.Type(typeKey) is not { } type || TypeInitializer(type.TypeKey) is not { } initializer)
                return;
            if ((_memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId) == initializer.MethodId)
                return;

            var candidate = Candidate(type.TypeKey, initializer.MethodId);
            if (!candidate.Referencing.Contains(bodyId, StringComparer.Ordinal))
                candidate.Referencing.Add(bodyId);
        }

        private void AddTypeInitializerTrigger(string typeKey, string constructionId)
        {
            if (TypeInitializer(typeKey) is not { } initializer)
                return;
            var candidate = Candidate(typeKey, initializer.MethodId);
            if (!candidate.Constructions.Contains(constructionId, StringComparer.Ordinal))
                candidate.Constructions.Add(constructionId);
        }

        private (string BodyId, List<string> Referencing, List<string> Constructions) Candidate(string typeKey, string initializerId)
        {
            if (!_typeInitializers.TryGetValue(typeKey, out var candidate))
            {
                candidate = (initializerId, [], []);
                _typeInitializers.Add(typeKey, candidate);
                ReachBody(initializerId, $"type-initializer:{typeKey}");
            }

            return candidate;
        }

        /// <summary>The type initializer of a type, null when it has none with a source body or the type is left out.</summary>
        /// <param name="typeKey">The type.</param>
        private ProgramMethod? TypeInitializer(string typeKey) =>
            IsLeftOutType(typeKey)
                ? null
                : _program.MethodsOf(typeKey).FirstOrDefault(method => method is { Kind: ProgramMethodKind.TypeInitializer, HasSourceBody: true });

        /// <summary>A delegate type's name with each type-argument list reduced to its arity: a delegate of <c>Func&lt;T&gt;</c>
        /// created in generic code is invoked as <c>Func&lt;Order&gt;</c>.</summary>
        private static string Shape(string type)
        {
            var shape = new StringBuilder();
            var depth = 0;
            var count = 0;
            foreach (var character in type)
            {
                switch (character)
                {
                    case '<':
                        if (depth++ == 0)
                            count = 1;
                        break;
                    case '>':
                        if (--depth == 0)
                            shape.Append('<').Append(count).Append('>');
                        break;
                    case ',' when depth == 1:
                        count++;
                        break;
                    default:
                        if (depth == 0)
                            shape.Append(character);
                        break;
                }
            }

            return shape.ToString();
        }
    }
}
