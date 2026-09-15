using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Di;

public enum InjectionMemberKind
{
    Field,
    AutoProperty,
    PrimaryConstructorParameter
}

public sealed record BindingEvidence(string Kind, string Text, SourceSpan Source);

/// <summary>A member of <see cref="Type"/> that holds what the container passed to one parameter of the type's
/// public constructor. <see cref="DeclaringType"/> differs from <see cref="Type"/> for a member inherited through
/// pass-through <c>base(...)</c> arguments; the constructor parameter is always the one on <see cref="Type"/>. A closed generic
/// base is named as <see cref="Type"/> inherits it; <see cref="DeclaringTypeIdentity"/> identifies it as field references do.</summary>
public sealed record InjectionBinding(string Type, string DeclaringType, string Member, InjectionMemberKind MemberKind,
                                      string ConstructorParameter, int ConstructorParameterOrdinal, string ServiceType,
                                      DiResolution Resolution, IReadOnlyList<BindingEvidence> Evidence,
                                      string? DeclaringTypeIdentity = null);

public sealed record TypeInjectionBindings(string Type, IReadOnlyList<InjectionBinding> Bindings,
                                           IReadOnlyList<DiDiagnostic> Diagnostics, string TypeKey);
