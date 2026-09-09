using Microsoft.CodeAnalysis;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>The questions asked of a type before it is recognised as an entry point. Predicates only: what
/// a type looks like, never what to build from it.</summary>
internal static class TypeShapes
{
    internal static bool IsPublicAction(IMethodSymbol method) =>
        method.DeclaredAccessibility == Accessibility.Public && method.MethodKind == MethodKind.Ordinary && !method.IsStatic &&
        !method.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType && HasShape(attributeType, "NonActionAttribute", 0)) &&
        method.Locations.Any(location => location.IsInSource);

    internal static bool DerivesFrom(INamedTypeSymbol type, string name, int arity)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (HasShape(current, name, arity))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasAttribute(INamedTypeSymbol type, string name, int arity)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType && HasShape(attributeType, name, arity)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasShape(INamedTypeSymbol type, string name, int arity) =>
        type.Name == name && type.Arity == arity;
}
