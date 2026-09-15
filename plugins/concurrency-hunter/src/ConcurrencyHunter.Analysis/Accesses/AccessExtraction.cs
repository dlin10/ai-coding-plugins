using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Accesses;

/// <summary>
/// Field accesses of every root's entry body and the bodies nested in it (R8). A static field is always an access; an
/// instance field is one only when its object traces, through assigns and identity or reference conversions, to the
/// hosted-service receiver, a receiver member the container binds, or an unassigned parameter bound to a DI service.
/// Everything else increments a skip counter by reason.
/// </summary>
public static class AccessExtraction
{
    public const string SKIP_LOCAL = "local";
    public const string SKIP_ALLOCATION = "allocation";
    public const string SKIP_CALL_RESULT = "call-result";
    public const string SKIP_NESTED_FIELD = "nested-field";
    public const string SKIP_UNBOUND_MEMBER = "unbound-member";
    public const string SKIP_UNBOUND_PARAMETER = "unbound-parameter";
    public const string SKIP_REASSIGNED_PARAMETER = "reassigned-parameter";
    public const string SKIP_PHI = "phi";
    public const string SKIP_CONVERSION = "conversion";

    private const string HOSTED_SERVICE = DiIndex.HOSTED_SERVICE_TYPE;
    private const string HOSTED_SERVICE_KEY = DiIndex.HOSTED_SERVICE_KEY;

    public static AccessExtractionResult Extract(ScopeAnalysisInput input)
    {
        var accesses = new List<Access>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in input.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
        {
            var key = root.Entry.BodyKey;
            if (!input.Bodies.ContainsKey(key))
                continue;

            var bodies = input.Bodies.Values
                              .Where(body => body.BodyId == key || body.BodyId.StartsWith(key + "#", StringComparison.Ordinal))
                              .OrderBy(body => body.BodyId, StringComparer.Ordinal)
                              .ToArray();
            var analysis = new RootAnalysis(input, root, bodies);
            foreach (var body in bodies)
                analysis.Extract(body, accesses, skips);
        }

        return new AccessExtractionResult(accesses, skips);
    }

    private abstract record ObjectTrace;

    private sealed record SkippedTrace(string Reason) : ObjectTrace;

    private sealed record StaticFieldTrace(IrFieldRef Field) : ObjectTrace;

    private sealed record ReceiverTrace(ReceiverKind Kind, string? Type) : ObjectTrace;

    /// <summary><see cref="ObjectKey"/> identifies the object across assemblies (service and implementation type keys);
    /// <see cref="Region"/> and <see cref="ServiceType"/> are for display.</summary>
    private sealed record DiTrace(string Region, DiLifetime Lifetime, string ServiceType, string SharingKey, bool SingleObject,
                                  IReadOnlyList<BindingEvidence> Evidence, IReadOnlyList<string> Uncertainties, string ObjectKey) : ObjectTrace;

    private sealed class BodyFacts
    {
        internal BodyFacts(IrBody body)
        {
            Body = body;
            Values = body.Values.ToDictionary(value => value.Id);
            Operations = body.Blocks.SelectMany(block => block.Operations).ToDictionary(operation => operation.Id);
            var definitions = new Dictionary<int, IrOperation>();
            foreach (var operation in Operations.Values)
            {
                foreach (var defined in operation.DefinedValues)
                    definitions[defined] = operation;
            }

            Definitions = definitions;

            Captured = Operations.Values.OfType<IrCaptureOperation>().Select(capture => capture.Value).ToHashSet();
        }

        internal IrBody Body { get; }
        internal IReadOnlyDictionary<int, IrValue> Values { get; }
        internal IReadOnlyDictionary<int, IrOperation> Operations { get; }
        internal IReadOnlyDictionary<int, IrOperation> Definitions { get; }
        internal IReadOnlySet<int> Captured { get; }
    }

    private sealed class RootAnalysis
    {
        private readonly ScopeAnalysisInput _input;
        private readonly ExecutionRootDescriptor _root;
        private readonly AccessRoot _accessRoot;
        private readonly Dictionary<string, BodyFacts> _facts;
        private readonly TypeInjectionBindings? _receiverBindings;
        private readonly HostedServiceRegistration? _hosted;

        internal RootAnalysis(ScopeAnalysisInput input, ExecutionRootDescriptor root, IReadOnlyList<IrBody> bodies)
        {
            _input = input;
            _root = root;
            _accessRoot = new AccessRoot(root.StableRootId, root.Entry.Symbol, root.Entry.Display, root.ProviderId, root.RootKind,
                                         root.InvocationPolicy, input.ScopeId, root.InstanceBindings.ReceiverType,
                                         root.InstanceBindings.ReceiverTypeKey);
            _facts = bodies.ToDictionary(body => body.BodyId, body => new BodyFacts(body), StringComparer.Ordinal);
            var receiverTypeKey = root.InstanceBindings.ReceiverTypeKey;
            _receiverBindings = receiverTypeKey is null || !IsContainerConstructed
                ? null
                : input.InjectionBindings.FirstOrDefault(bindings => bindings.TypeKey == receiverTypeKey);
            _hosted = Receiver == ReceiverKind.HostedService && receiverTypeKey is not null
                ? input.DiIndex.HostedServices.FirstOrDefault(hosted => hosted.ImplementationTypeKey == receiverTypeKey)
                : null;
        }

        private ReceiverKind Receiver => _root.InstanceBindings.Receiver;

        /// <summary>Only controllers and hosted services are built by the container, so only their members are bound.</summary>
        private bool IsContainerConstructed => Receiver is ReceiverKind.PerInvocation or ReceiverKind.HostedService;

        private bool IsHostedRoot => Receiver == ReceiverKind.HostedService;

        internal void Extract(IrBody body, List<Access> accesses, IDictionary<string, int> skips)
        {
            var facts = _facts[body.BodyId];
            var held = MustHeldLocks.Compute(body, operation => LockEffectOf(facts, operation));
            var readModifyWriteLoads = facts.Operations.Values.OfType<IrStoreFieldOperation>()
                                            .Where(store => store.ReadModifyWriteOf is not null)
                                            .Select(store => store.ReadModifyWriteOf!.Value)
                                            .ToHashSet();
            foreach (var operation in body.Blocks.SelectMany(block => block.Operations))
            {
                switch (operation)
                {
                    case IrLoadFieldOperation load when !readModifyWriteLoads.Contains(load.Id):
                        Emit(facts, load, load.Field, load.ReceiverValue, AccessOperation.Read, held, accesses, skips);
                        break;
                    case IrStoreFieldOperation store:
                        Emit(facts, store, store.Field, store.ReceiverValue,
                             store.ReadModifyWriteOf is null ? AccessOperation.Write : AccessOperation.ReadModifyWrite, held, accesses, skips);
                        break;
                }
            }
        }

        private void Emit(BodyFacts facts, IrOperation operation, IrFieldRef field, int? receiver, AccessOperation kind,
                          IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>> held, List<Access> accesses,
                          IDictionary<string, int> skips)
        {
            string region;
            string sharingKey;
            IReadOnlyList<BindingEvidence> evidence;
            IReadOnlyList<string> uncertainties = [];
            if (field.IsStatic)
            {
                region = $"static:{field.ContainingType}";
                sharingKey = SharingKeys.PROCESS;
                evidence = [];
            }
            else
            {
                var trace = receiver is int value ? TraceObject(facts, value) : new SkippedTrace(SKIP_LOCAL);
                switch (trace)
                {
                    case DiTrace di:
                        region = di.Region;
                        sharingKey = di.SharingKey;
                        evidence = di.Evidence;
                        uncertainties = di.Uncertainties;
                        break;
                    case ReceiverTrace { Kind: ReceiverKind.Unbound }:
                        Count(skips, SKIP_UNBOUND_MEMBER);
                        return;
                    case StaticFieldTrace:
                        Count(skips, SKIP_NESTED_FIELD);
                        return;
                    case SkippedTrace skipped:
                        Count(skips, skipped.Reason);
                        return;
                    default:
                        Count(skips, SKIP_LOCAL);
                        return;
                }
            }

            var state = held.GetValueOrDefault(operation.Id) ?? new Dictionary<string, HeldLock>();
            var locks = state.Values.OrderBy(lockHeld => lockHeld.Lock.Display, StringComparer.Ordinal).ToArray();
            var codeFlow = new List<CodeFlowStep> { new("root", $"{_root.Entry.Display} starts", _root.Entry.Source) };
            codeFlow.AddRange(locks.OrderBy(lockHeld => lockHeld.AcquisitionId)
                                   .Select(lockHeld => new CodeFlowStep("acquire", $"acquires {lockHeld.Lock.Display}",
                                                                        facts.Operations[lockHeld.AcquisitionId].Provenance.Span)));
            codeFlow.Add(new CodeFlowStep("access", $"{kind.ToWireName()} {field.ContainingType}.{field.Name}", operation.Provenance.Span));

            accesses.Add(new Access(
                new AccessResource(field.Assembly, _input.ScopeId, region, [field.Name],
                                   new MemberKey(field.ContainingType, field.Name, field.Kind,
                                                 field.ContainingTypeIdentity == field.ContainingType ? null : field.ContainingTypeIdentity)),
                kind,
                _accessRoot,
                facts.Body.OwnerSymbol,
                operation.Provenance.Span,
                locks.Select(lockHeld => lockHeld.Lock.Display).ToArray(),
                locks.Select(lockHeld => lockHeld.Lock.SingleObjectId).OfType<string>().Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal).ToArray(),
                sharingKey,
                evidence,
                codeFlow,
                uncertainties));
        }

        private LockEffect LockEffectOf(BodyFacts facts, IrOperation operation) => operation switch
        {
            IrAcquireOperation acquire => new LockEffect(LockEffectKind.Acquire, LockObjectOf(facts, acquire.LockValue, acquire.Provenance)),
            IrReleaseOperation release => new LockEffect(LockEffectKind.Release, LockObjectOf(facts, release.LockValue, release.Provenance)),
            _ => LockEffect.None
        };

        private LockObject LockObjectOf(BodyFacts facts, int value, IrProvenance provenance)
        {
            const string NOT_ONE_OBJECT = " (not one object per process)";
            switch (TraceObject(facts, value))
            {
                case StaticFieldTrace { Field: var field }:
                {
                    var name = $"static:{field.ContainingType}.{field.Name}";
                    var key = $"static:{field.ContainingTypeId}.{field.Name}";
                    return field.IsReadOnly
                        ? new LockObject(key, name, $"{_input.ScopeId}|{field.Assembly}:{key}")
                        : new LockObject(key, name + NOT_ONE_OBJECT, null);
                }
                case DiTrace di:
                {
                    var name = $"{di.Region} as {di.ServiceType}";
                    return di.SingleObject
                        ? new LockObject(name, name, $"{_input.ScopeId}|{di.ObjectKey}")
                        : new LockObject(name, name + NOT_ONE_OBJECT, null);
                }
                case ReceiverTrace receiver:
                {
                    var name = $"this {receiver.Type}";
                    return new LockObject(name, name + NOT_ONE_OBJECT, null);
                }
                case SkippedTrace when OwnReceiverMember(facts, value) is { } member:
                {
                    // A member of the receiver the container does not bind (such as `_gate = new object()` on a
                    // controller) is still one lock object per receiver: held, but never one object per process.
                    var name = $"{member.ContainingType}.{member.Name}";
                    return new LockObject(name, name + NOT_ONE_OBJECT, null);
                }
                default:
                {
                    // Held all the same, so the evidence shows it, but it never suppresses: its identity is unknown.
                    var (origin, description) = UnresolvedLockOrigin(facts, value);
                    return new LockObject($"unresolved:{origin}",
                                          $"{description} at {provenance.Span.Path}:{provenance.Span.StartLine} (identity unknown, not one object per process)",
                                          null, IdentityUnknown: true);
                }
            }
        }

        /// <summary>The value a lock object comes from after assigns and identity or reference conversions, which is the
        /// same for a lock statement's acquisition and release, and a short description of it.</summary>
        private static (int Origin, string Description) UnresolvedLockOrigin(BodyFacts facts, int value)
        {
            while (facts.Definitions.TryGetValue(value, out var definition))
            {
                switch (definition)
                {
                    case IrAssignOperation assign:
                        value = assign.SourceValue;
                        continue;
                    case IrConvertOperation { ConversionKind: IrConversionKind.Identity or IrConversionKind.Reference } convert:
                        value = convert.OperandValue;
                        continue;
                    case IrAllocateOperation allocate:
                        return (value, $"lock on new {allocate.AllocatedType}");
                    case IrCallOperation:
                        return (value, "lock on a call result");
                }

                break;
            }

            var named = facts.Values.TryGetValue(value, out var irValue) && irValue.Kind is IrValueKind.Local or IrValueKind.Parameter;
            return (value, named ? $"lock on {irValue!.Name}" : "lock on an unresolved object");
        }

        /// <summary>Follows a value back to the object it denotes.</summary>
        private ObjectTrace TraceObject(BodyFacts facts, int valueId)
        {
            if (!facts.Values.TryGetValue(valueId, out var value))
                return new SkippedTrace(SKIP_LOCAL);

            switch (value.Kind)
            {
                case IrValueKind.Receiver:
                    return ReceiverObject();
                case IrValueKind.Parameter when value.SsaVersion != 0:
                    return new SkippedTrace(SKIP_REASSIGNED_PARAMETER);
                case IrValueKind.Parameter:
                    return ParameterObject(facts, value);
                case IrValueKind.Constant:
                    return new SkippedTrace(SKIP_LOCAL);
            }

            if (!facts.Definitions.TryGetValue(valueId, out var definition))
                return new SkippedTrace(SKIP_LOCAL);

            return definition switch
            {
                IrAssignOperation assign => TraceObject(facts, assign.SourceValue),
                IrConvertOperation { ConversionKind: IrConversionKind.Identity or IrConversionKind.Reference } convert =>
                    TraceObject(facts, convert.OperandValue),
                IrConvertOperation => new SkippedTrace(SKIP_CONVERSION),
                IrPhiOperation => new SkippedTrace(SKIP_PHI),
                IrAllocateOperation => new SkippedTrace(SKIP_ALLOCATION),
                IrCallOperation or IrAwaitOperation => new SkippedTrace(SKIP_CALL_RESULT),
                IrLoadFieldOperation { Field.IsStatic: true } load => new StaticFieldTrace(load.Field),
                IrLoadFieldOperation { ReceiverValue: int receiver } load => MemberObject(facts, receiver, load.Field),
                _ => new SkippedTrace(SKIP_LOCAL)
            };
        }

        private ObjectTrace ReceiverObject()
        {
            if (!IsHostedRoot)
                return new ReceiverTrace(Receiver, _root.InstanceBindings.ReceiverType);

            var type = _root.InstanceBindings.ReceiverType!;
            var region = DiIndex.RegionId(type, DiLifetime.Singleton);
            var evidence = _hosted?.Registrations.Select(registration => new BindingEvidence(
                                                             "registration", $"{registration.Method} registers {type}", registration.Source))
                                  .ToArray() ?? [];
            return new DiTrace(region, DiLifetime.Singleton, HOSTED_SERVICE, $"{SharingKeys.PROCESS}:{HOSTED_SERVICE_KEY}",
                               _hosted?.InstanceCount == HostedServiceInstanceCount.One && _input.DiIndex.UnresolvedRegistrations.Count == 0,
                               evidence, UnresolvedUncertainties(),
                               ObjectKey(HOSTED_SERVICE_KEY, _root.InstanceBindings.ReceiverTypeKey ?? type, DiLifetime.Singleton));
        }

        private ObjectTrace MemberObject(BodyFacts facts, int receiver, IrFieldRef field)
        {
            var owner = TraceObject(facts, receiver);
            if (owner is not ReceiverTrace && !(owner is DiTrace && IsHostedRoot && IsOwnReceiver(facts, receiver)))
                return new SkippedTrace(SKIP_NESTED_FIELD);
            if (!IsContainerConstructed || _receiverBindings is null)
                return new SkippedTrace(SKIP_UNBOUND_MEMBER);

            var binding = _receiverBindings.Bindings.FirstOrDefault(candidate =>
                (candidate.DeclaringTypeIdentity ?? candidate.DeclaringType) == field.ContainingTypeId && candidate.Member == field.Name &&
                Matches(candidate.MemberKind, field.Kind));
            if (binding?.Resolution is not { Kind: DiResolutionKind.Bound, Binding: { } bound })
                return new SkippedTrace(SKIP_UNBOUND_MEMBER);

            var sharingKey = bound.Lifetime switch
            {
                DiLifetime.Singleton => $"{SharingKeys.PROCESS}:{bound.ServiceTypeKey}",
                DiLifetime.Scoped when IsHostedRoot => $"root-scope:{bound.ServiceTypeKey}",
                DiLifetime.Transient when IsHostedRoot => $"{SharingKeys.HOSTED_INSTANCE}:{_receiverBindings.TypeKey}:ctor:{binding.ConstructorParameter}",
                _ => SharingKeys.INVOCATION
            };
            return new DiTrace(bound.RegionId, bound.Lifetime, bound.ServiceType, sharingKey, IsSingleObject(bound), binding.Evidence, bound.Uncertainties,
                               ObjectKey(bound));
        }

        /// <summary>The instance field of the root's own receiver that <paramref name="value"/> was loaded from, if any.</summary>
        private IrFieldRef? OwnReceiverMember(BodyFacts facts, int value)
        {
            while (facts.Definitions.TryGetValue(value, out var definition))
            {
                switch (definition)
                {
                    case IrAssignOperation assign:
                        value = assign.SourceValue;
                        break;
                    case IrConvertOperation { ConversionKind: IrConversionKind.Identity or IrConversionKind.Reference } convert:
                        value = convert.OperandValue;
                        break;
                    case IrLoadFieldOperation { Field.IsStatic: false, ReceiverValue: int receiver } load when IsOwnReceiver(facts, receiver):
                        return load.Field;
                    default:
                        return null;
                }
            }

            return null;
        }

        private bool IsOwnReceiver(BodyFacts facts, int value)
        {
            while (true)
            {
                if (facts.Values.TryGetValue(value, out var candidate) && candidate.Kind == IrValueKind.Receiver)
                    return true;
                if (!facts.Definitions.TryGetValue(value, out var definition))
                    return false;
                switch (definition)
                {
                    case IrAssignOperation assign:
                        value = assign.SourceValue;
                        break;
                    case IrConvertOperation { ConversionKind: IrConversionKind.Identity or IrConversionKind.Reference } convert:
                        value = convert.OperandValue;
                        break;
                    default:
                        return false;
                }
            }
        }

        private ObjectTrace ParameterObject(BodyFacts facts, IrValue parameter)
        {
            if (!ReferencesRootParameter(facts, parameter))
                return new SkippedTrace(SKIP_UNBOUND_PARAMETER);

            var binding = _root.InstanceBindings.Parameters.FirstOrDefault(candidate => candidate.Name == parameter.Name);
            if (binding is null || binding.Kind != ParameterBindingKind.DiService || binding.IsValueType)
                return new SkippedTrace(SKIP_UNBOUND_PARAMETER);
            if (IsReassigned(parameter.Name))
                return new SkippedTrace(SKIP_REASSIGNED_PARAMETER);

            if (binding.TypeKey is null || _input.DiIndex.Resolve(binding.TypeKey) is not { Kind: DiResolutionKind.Bound, Binding: { } bound } resolution)
                return new SkippedTrace(SKIP_UNBOUND_PARAMETER);

            var sharingKey = bound.Lifetime == DiLifetime.Singleton
                ? $"{SharingKeys.PROCESS}:{bound.ServiceTypeKey}"
                : bound.Lifetime == DiLifetime.Scoped && IsHostedRoot
                    ? $"root-scope:{bound.ServiceTypeKey}"
                    : SharingKeys.INVOCATION;
            var evidence = resolution.Registrations.Select(registration => new BindingEvidence(
                                                               "registration", $"{registration.Method} registers {binding.Type}", registration.Source))
                                     .ToArray();
            return new DiTrace(bound.RegionId, bound.Lifetime, bound.ServiceType, sharingKey, IsSingleObject(bound), evidence, bound.Uncertainties,
                               ObjectKey(bound));
        }

        private static string ObjectKey(DiBinding bound) => ObjectKey(bound.ServiceTypeKey, bound.ImplementationTypeKey, bound.Lifetime);

        private static string ObjectKey(string serviceTypeKey, string implementationTypeKey, DiLifetime lifetime) =>
            $"{serviceTypeKey}|{DiIndex.RegionId(implementationTypeKey, lifetime)}";

        /// <summary>Whether a parameter value in <paramref name="facts"/> is the root entry's own parameter: the entry's
        /// uncaptured parameter, or a capture that resolves, body by enclosing body, to it.</summary>
        private bool ReferencesRootParameter(BodyFacts facts, IrValue parameter)
        {
            var captured = facts.Captured.Contains(parameter.Id);
            if (facts.Body.BodyId == _root.Entry.BodyKey)
                return !captured;
            if (!captured)
                return false;

            // An enclosing body that only passes the capture through to a nested body does not mention it, so the search
            // continues outward until a body declares or captures the name.
            var bodyId = facts.Body.BodyId;
            while (bodyId.LastIndexOf('#') is var separator and >= 0)
            {
                bodyId = bodyId[..separator];
                if (!_facts.TryGetValue(bodyId, out var parent))
                    return false;
                var outer = parent.Values.Values.FirstOrDefault(value => value is { Kind: IrValueKind.Parameter, SsaVersion: 0 } &&
                                                                         value.Name == parameter.Name);
                if (outer is not null)
                    return ReferencesRootParameter(parent, outer);
            }

            return false;
        }

        private bool IsReassigned(string name)
        {
            foreach (var facts in _facts.Values)
            {
                var parameters = facts.Values.Values
                                      .Where(value => value.Kind == IrValueKind.Parameter && value.Name == name)
                                      .ToArray();
                var version0 = parameters.FirstOrDefault(value => value.SsaVersion == 0);
                if (version0 is null || !ReferencesRootParameter(facts, version0))
                    continue;
                if (parameters.Any(value => value.SsaVersion != 0))
                    return true;
                var ids = parameters.Select(value => value.Id).ToHashSet();
                if (facts.Operations.Values.OfType<IrUnknownOperation>()
                         .Any(unknown => unknown.Reason == "address-taken" && unknown.OperandValues.Any(ids.Contains)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A registration whose service type is unknown may override any binding of the scope, so while one exists
        /// no DI binding yields a single-object lock id.</summary>
        private bool IsSingleObject(DiBinding bound) =>
            bound.Lifetime == DiLifetime.Singleton && _input.DiIndex.UnresolvedRegistrations.Count == 0 &&
            !(bound.ServiceTypeKey == HOSTED_SERVICE_KEY &&
              _input.DiIndex.HostedServices.Any(hosted => hosted.ImplementationTypeKey == bound.ImplementationTypeKey &&
                                                          hosted.InstanceCount != HostedServiceInstanceCount.One));

        private IReadOnlyList<string> UnresolvedUncertainties() =>
            _input.DiIndex.UnresolvedRegistrations
                  .Select(unresolved => $"An unresolved registration at {unresolved.Source.Path}:{unresolved.Source.StartLine} may change this binding.")
                  .ToArray();

        private static bool Matches(InjectionMemberKind member, IrFieldKind field) => (member, field) switch
        {
            (InjectionMemberKind.Field, IrFieldKind.Field) => true,
            (InjectionMemberKind.AutoProperty, IrFieldKind.PropertyBackingField) => true,
            (InjectionMemberKind.PrimaryConstructorParameter, IrFieldKind.PrimaryConstructorParameter) => true,
            _ => false
        };

        private static void Count(IDictionary<string, int> skips, string reason) =>
            skips[reason] = skips.TryGetValue(reason, out var count) ? count + 1 : 1;
    }
}
