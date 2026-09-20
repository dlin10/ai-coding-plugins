using System.Text;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.CallGraph;

/// <summary>What the reachable set is built over. <see cref="Members"/> lowers a member by its body id and returns its body
/// with every nested body, or nothing when it has no source body; the set asks for each member at most once.</summary>
public sealed record ReachabilityInput(ProgramIndex Program, IReadOnlyList<ExecutionRootDescriptor> Roots, DiIndex DiIndex,
                                       IReadOnlyList<TypeInjectionBindings> InjectionBindings,
                                       Func<string, IReadOnlyList<IrBody>> Members);

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

/// <summary>The class-hierarchy superset of what a scope can run. <see cref="ReachedBodies"/> maps each body a call, delegate,
/// root or construction reaches to that reason; <see cref="Bodies"/> holds every lowered body of the members in the set.</summary>
public sealed record ReachableSetResult(IReadOnlyList<ReachedMember> Members, IReadOnlyDictionary<string, string> ReachedBodies,
                                        IReadOnlyDictionary<string, IrBody> Bodies, IReadOnlyList<Construction> Constructions,
                                        IReadOnlyList<TypeInitializerCandidate> TypeInitializers, IReadOnlyList<UnreachedMember> Unreached,
                                        IReadOnlyDictionary<string, IReadOnlyList<OpaqueCall>> OpaqueCalls,
                                        IReadOnlyList<UnanalysedRegistration> UnanalysedRegistrations);

/// <summary>
/// Builds the reachable set from the roots: their entry bodies, the constructions they trigger with the DI services those
/// constructors take (transitively), and every body a reached body calls, dispatches to by class-hierarchy analysis,
/// or invokes through a compatible delegate. A construction reached from a hosted service's constructor chain is a
/// startup construction; a singleton or root-scope region startup reaches is built once, at startup.
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

        internal Walker(ReachabilityInput input)
        {
            _input = input;
            _program = input.Program;
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
                                          unanalysed);
        }

        private IReadOnlyList<string> NestedIds(string memberId, IReadOnlyList<IrBody> bodies) =>
            bodies.Count == 0
                ? _program.Method(memberId)?.NestedBodyIds ?? []
                : bodies.Select(body => body.BodyId).Where(id => id != memberId).ToArray();

        /// <summary>Resolves a service for a construction or a root. Startup resolves everything at startup; otherwise a singleton,
        /// and a scoped service resolved from the root scope, is a lazily resolved construction per region, unless startup
        /// already builds that region, and any other service is built inside the resolving execution.</summary>
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
            if (_reachedBodies.ContainsKey(bodyId))
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
                        break;
                    case IrCallOperation { TargetMethodId: { } targetId } call:
                    {
                        var targets = Targets(targetId, call.CallKind is IrCallKind.Virtual or IrCallKind.Interface);
                        if (targets.Count == 0)
                        {
                            RecordOpaque(bodyId, call, definitions, work.GetValueOrDefault(call.Id) ?? []);
                            ReachFactory(bodyId, call, definitions, reason);
                            Locate(bodyId, call);
                        }
                        foreach (var target in targets)
                            ReachBody(target.MethodId, reason);
                        if (call.CallKind is IrCallKind.Static or IrCallKind.Constructor && _program.Method(targetId) is { } method)
                            Reference(method.ContainingTypeKey, bodyId);
                        break;
                    }
                    case IrCreateDelegateOperation create when values.TryGetValue(create.ResultValue, out var created):
                        AddDelegateCreation(created.Type, create, bodyId);
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
                {
                    foreach (var target in Targets(methodId, virtualDispatch: true))
                        ReachBody(target.MethodId, reason);
                }

                return;
            }

            if (DelegateCreation(value, definitions) is { } creation)
                ReachDelegateTargets(creation, reason);
            else if (values.TryGetValue(value, out var delegateValue))
                AddDelegateInvocation(delegateValue.Type, reason);
        }

        private void AddDelegateCreation(string type, IrCreateDelegateOperation create, string bodyId)
        {
            var targets = new List<string>();
            if (create.TargetBodyId is { } nested)
            {
                targets.Add(nested);
            }
            else if (create.TargetMethodId is { } methodId && _program.Method(methodId) is { } method &&
                     (DelegateTypeKey(type) is not { } delegateTypeKey || _program.IsDelegateCompatible(delegateTypeKey, methodId)))
            {
                var virtualDispatch = method.IsVirtual || method.IsAbstract || method.IsOverride ||
                                      _program.Type(method.ContainingTypeKey)?.IsInterface == true;
                targets.AddRange(Targets(methodId, virtualDispatch).Select(target => target.MethodId));
                if (method.IsStatic)
                    Reference(method.ContainingTypeKey, bodyId);
            }

            var shape = Shape(type);
            if (!_delegateTargets.TryGetValue(shape, out var known))
                _delegateTargets.Add(shape, known = []);
            foreach (var target in targets.Where(target => !known.Contains((type, target))))
            {
                known.Add((type, target));
                var invocation = (_delegateInvocations.GetValueOrDefault(shape) ?? [])
                                 .Where(candidate => Invokes(candidate.Type, type))
                                 .Select(candidate => candidate.Reason)
                                 .FirstOrDefault();
                if (invocation is not null)
                    ReachBody(target, invocation);
            }
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
        /// virtual dispatch, and the method itself when it has a body.</summary>
        private IReadOnlyList<ProgramMethod> Targets(string methodId, bool virtualDispatch)
        {
            var targets = new List<ProgramMethod>();
            if (_program.Method(methodId) is { HasSourceBody: true } self)
                targets.Add(self);
            if (virtualDispatch)
                targets.AddRange(_dispatchTargets.GetValueOrDefault(methodId)?.Where(target => target.HasSourceBody) ?? []);
            return targets;
        }

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
            var virtualDispatch = method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true;
            foreach (var target in Targets(methodId, virtualDispatch))
                ReachBody(target.MethodId, reason);
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

        private ProgramMethod? TypeInitializer(string typeKey) =>
            _program.MethodsOf(typeKey).FirstOrDefault(method => method is { Kind: ProgramMethodKind.TypeInitializer, HasSourceBody: true });

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
