using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>How the driver fills a value of a type (SPEC TD-034b, G-3), the axes in their order.</summary>
public enum ParameterKind
{
    /// <summary>Axis 1: a ref-like or pointer type; no driver is synthesized.</summary>
    NotSynthesized,

    /// <summary>Axis 2: a delegate, filled with a probe lambda.</summary>
    Delegate,

    /// <summary>Axis 3: an immutable type or a plain struct, filled with a canned value; not holding.</summary>
    Canned,

    /// <summary>Axis 4: an array or a sequence interface, filled with two values of its element's kind.</summary>
    Container,

    /// <summary>Axis 5: <c>object</c>, an interface, a derivable abstract class or a type parameter, filled with a probe class.</summary>
    ProbeObject,

    /// <summary>Axis 6: a non-sealed concrete class a user could subclass, filled with the driver's subclass of it.</summary>
    Subclass,

    /// <summary>Axis 7: any other class, a task, or a struct with references, filled with the recipe's value.</summary>
    RecipeValue
}

/// <summary>The one owner of a parameter's <see cref="ParameterKind"/>, which the candidate rule and the synthesizer both call.
/// It reads what a type can carry only through <see cref="TypeShape.Of"/>.</summary>
public static class ParameterKinds
{
    private static readonly HashSet<string> Sequences = new(
    [
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.IReadOnlyList`1"
    ], StringComparer.Ordinal);

    /// <summary>The kind of a value of <paramref name="type"/>: the first axis that matches. A <c>ref</c>, <c>out</c> or <c>in</c>
    /// parameter is asked about with its referenced type.</summary>
    /// <param name="type">The type.</param>
    public static ParameterKind Of(ITypeSymbol type)
    {
        switch (TypeShape.Of(type))
        {
            case TypeShapeKind.RefLikeOrPointer:
                return ParameterKind.NotSynthesized;
            case TypeShapeKind.Delegate:
                return ParameterKind.Delegate;
            case TypeShapeKind.Immutable or TypeShapeKind.PlainStruct:
                return ParameterKind.Canned;
            case TypeShapeKind.Task or TypeShapeKind.TaskOfT or TypeShapeKind.StructWithReferences:
                return ParameterKind.RecipeValue;
        }

        if (type is IArrayTypeSymbol || IsSequence(type))
            return ParameterKind.Container;
        if (type.SpecialType == SpecialType.System_Object || type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Dynamic ||
            type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true } abstractClass && Derivation.CanDerive(abstractClass))
        {
            return ParameterKind.ProbeObject;
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsSealed: false } concrete && Derivation.CanDerive(concrete) &&
            Derivation.Overridable(concrete, out _).Count > 0)
        {
            return ParameterKind.Subclass;
        }

        return ParameterKind.RecipeValue;
    }

    /// <summary>Whether a value of the kind can hold what the member stores into it.</summary>
    /// <param name="kind">The kind.</param>
    public static bool IsHolding(ParameterKind kind) =>
        kind is ParameterKind.Container or ParameterKind.ProbeObject or ParameterKind.Subclass or ParameterKind.RecipeValue;

    /// <summary>Whether a value of <paramref name="type"/> can carry a user object and makes its member a generation candidate: a
    /// <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> exactly when its completion type T can, as the value handed directly would.</summary>
    /// <param name="type">The receiver or parameter type.</param>
    public static bool IsCandidate(ITypeSymbol type) => TypeShape.Of(type) switch
    {
        TypeShapeKind.Delegate or TypeShapeKind.Reference or TypeShapeKind.StructWithReferences => true,
        TypeShapeKind.TaskOfT => TaskTypes.CompletionType(type) is { } completion && IsCandidate(completion),
        _ => false
    };

    /// <summary>Whether the type is one of the sequence interfaces axis 4 fills with a list.</summary>
    /// <param name="type">The type.</param>
    internal static bool IsSequence(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Interface, Arity: 1 } named &&
        Sequences.Contains(named.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + named.MetadataName);
}

/// <summary>What a user type in another assembly could do with a library class: derive from it, call its constructors, override
/// its members.</summary>
internal static class Derivation
{
    /// <summary>Whether a class in another assembly can derive from <paramref name="type"/>: a non-sealed, non-static class that
    /// is not one the language forbids as a base, with a constructor a subclass can call and every abstract member one a subclass
    /// can override.</summary>
    /// <param name="type">The class.</param>
    internal static bool CanDerive(INamedTypeSymbol type)
    {
        if (type is not { TypeKind: TypeKind.Class, IsSealed: false, IsStatic: false } ||
            type.SpecialType is SpecialType.System_Enum or SpecialType.System_ValueType or SpecialType.System_Delegate
                                or SpecialType.System_MulticastDelegate or SpecialType.System_Array ||
            !IsVisible(type) || Constructors(type).Count == 0)
        {
            return false;
        }

        Overridable(type, out var blocked);
        return !blocked;
    }

    /// <summary>The constructors a subclass in another assembly can call, without <c>ref</c>, <c>out</c> or ref-like parameters,
    /// fewest parameters first, then in declaration order.</summary>
    /// <param name="type">The class.</param>
    internal static IReadOnlyList<IMethodSymbol> Constructors(INamedTypeSymbol type) =>
        type.InstanceConstructors.Where(constructor => IsVisibleToSubclass(constructor.DeclaredAccessibility) &&
                                                       constructor.Parameters.All(parameter => parameter.RefKind is RefKind.None or RefKind.In &&
                                                                                               TypeShape.Of(parameter.Type) != TypeShapeKind.RefLikeOrPointer))
            .OrderBy(constructor => constructor.Parameters.Length)
            .ToArray();

    /// <summary>The members a subclass in another assembly could override — <c>virtual</c>, <c>abstract</c> or <c>override</c>,
    /// not <c>sealed</c>, visible to it, with a signature it can name — taken at their most derived declaration. Finalizers are
    /// left out.</summary>
    /// <param name="type">The class.</param>
    /// <param name="blocked">Whether an abstract member is one no subclass in another assembly can override.</param>
    internal static IReadOnlyList<ISymbol> Overridable(INamedTypeSymbol type, out bool blocked)
    {
        blocked = false;
        var result = new List<ISymbol>();
        var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                    continue;
                var isOverridden = overridden.Contains(member.OriginalDefinition);
                for (var hidden = Overridden(member); hidden is not null; hidden = Overridden(hidden))
                    overridden.Add(hidden.OriginalDefinition);
                if (isOverridden || !(member.IsVirtual || member.IsAbstract || member.IsOverride) || member.IsSealed ||
                    member is IMethodSymbol { Name: "Finalize", Parameters.Length: 0 } || !signatures.Add(Signature(member)))
                {
                    continue;
                }

                if (!IsVisibleToSubclass(member.DeclaredAccessibility) || !Signature(member, IsVisible))
                {
                    blocked |= member.IsAbstract;
                    continue;
                }

                result.Add(member);
            }
        }

        return result;
    }

    /// <summary>Whether a subclass in another assembly sees a member of this accessibility.</summary>
    /// <param name="accessibility">The accessibility.</param>
    internal static bool IsVisibleToSubclass(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    /// <summary>Whether code in another assembly, a subclass included, can name the type.</summary>
    /// <param name="type">The type.</param>
    internal static bool IsVisible(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsVisible(array.ElementType),
        IPointerTypeSymbol pointer => IsVisible(pointer.PointedAtType),
        ITypeParameterSymbol or IDynamicTypeSymbol => true,
        INamedTypeSymbol named => (named.ContainingType is null ? named.DeclaredAccessibility == Accessibility.Public : IsVisibleToSubclass(named.DeclaredAccessibility)) &&
                                  (named.ContainingType is null || IsVisible(named.ContainingType)) && named.TypeArguments.All(IsVisible),
        _ => false
    };

    /// <summary>Whether every type a member's signature names passes <paramref name="test"/>.</summary>
    /// <param name="member">The member.</param>
    /// <param name="test">The test.</param>
    internal static bool Signature(ISymbol member, Func<ITypeSymbol, bool> test) => member switch
    {
        IMethodSymbol method => (method.ReturnsVoid || test(method.ReturnType)) && method.Parameters.All(parameter => test(parameter.Type)),
        IPropertySymbol property => test(property.Type) && property.Parameters.All(parameter => test(parameter.Type)),
        IEventSymbol @event => test(@event.Type),
        _ => false
    };

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null
    };

    private static string Signature(ISymbol member) => member switch
    {
        IMethodSymbol method => $"M:{method.Name}`{method.Arity}({string.Join(",", method.Parameters.Select(parameter => parameter.RefKind + parameter.Type.ToDisplayString()))})",
        IPropertySymbol property => $"P:{property.Name}({string.Join(",", property.Parameters.Select(parameter => parameter.Type.ToDisplayString()))})",
        _ => $"E:{member.Name}"
    };
}
