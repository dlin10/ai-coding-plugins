using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Roots;

public sealed record ExecutionRootDescriptor(string StableRootId, string RootKind, string ProviderId, RootEntry Entry,
                                             InstanceBindings InstanceBindings, InvocationPolicy InvocationPolicy,
                                             IReadOnlyList<CompletionEvent> CompletionEvents,
                                             IReadOnlyList<OrderingConstraint> OrderingConstraints,
                                             IReadOnlyList<DiscoveryEvidence> DiscoveryEvidence,
                                             IReadOnlyList<string> PrecisionFlags);

public sealed record RootEntry(string BodyKey, string Symbol, string Display, SourceSpan Source);

public enum ReceiverKind
{
    None,
    PerInvocation,
    HostedService,
    Unbound,
    /// <summary>The object the DI registration of <see cref="InstanceBindings.ReceiverTypeKey"/> gives the invocation.</summary>
    DiService
}

public enum ParameterBindingKind
{
    DiService,
    RequestData,
    Unsupported
}

/// <summary><see cref="TypeKey"/> identifies the parameter's type across the assemblies of a scope (the DI index's
/// type key); a DI service parameter without one binds nothing.</summary>
public sealed record ParameterBinding(string Name, string Type, ParameterBindingKind Kind, bool IsValueType = false, string? TypeKey = null);

/// <summary><see cref="ReceiverType"/> names the receiver's type when there is one: the controller, the hosted-service
/// implementation, the gRPC service, or the type declaring an instance handler; <see cref="ReceiverTypeKey"/> identifies it.</summary>
public sealed record InstanceBindings(ReceiverKind Receiver, IReadOnlyList<ParameterBinding> Parameters, string? ReceiverType = null,
                                     string? ReceiverTypeKey = null);

public enum Multiplicity
{
    AtMostOnce,
    Repeated,
    Unknown
}

public enum SelfOverlap
{
    MayOverlap,
    Serialized,
    Unknown
}

public sealed record InvocationPolicy(Multiplicity Multiplicity, SelfOverlap SelfOverlap, string ScopeBinding);

public sealed record CompletionEvent(string EventRef, string Kind);

public sealed record OrderingConstraint(string BeforeEventRef, string AfterEventRef, string? GuardRef,
                                        IReadOnlyList<string> EvidenceIds);

public sealed record DiscoveryEvidence(string Id, string Kind, string Text, SourceSpan? Source);
