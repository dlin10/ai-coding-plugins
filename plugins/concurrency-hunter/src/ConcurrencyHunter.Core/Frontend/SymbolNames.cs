using ConcurrencyHunter.Di;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Frontend;

public static class SymbolNames
{
    private static readonly SymbolDisplayFormat TYPE_FORMAT =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat PARAMETER_TYPE_FORMAT = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                                                                          genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                                                                          miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string Type(ITypeSymbol type) => type.ToDisplayString(TYPE_FORMAT);

    /// <summary>The DI index's type key: <see cref="TypeIdentity"/> qualified by the assembly declaring the type, or the key
    /// of an array's element or a pointer's target, so same-named types of two projects in one scope stay apart.</summary>
    public static string TypeKey(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => $"{TypeKey(array.ElementType)}[{new string(',', array.Rank - 1)}]",
        IPointerTypeSymbol pointer => $"{TypeKey(pointer.PointedAtType)}*",
        _ => type.ContainingAssembly is { } assembly ? DiIndex.TypeKey(assembly.Name, TypeIdentity(type)) : TypeIdentity(type)
    };

    /// <summary>A type's name as it identifies the type beside its declaring assembly: the display name, except that in a
    /// generic type (or a type nested in one) every type argument is its own <see cref="TypeKey"/>.</summary>
    public static string TypeIdentity(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || !IsOrInGenericType(named))
            return Type(type);

        var arguments = named.TypeArguments.Length == 0 ? "" : $"<{string.Join(", ", named.TypeArguments.Select(TypeKey))}>";
        var container = named.ContainingType is { } containing ? TypeIdentity(containing)
            : named.ContainingNamespace is { IsGlobalNamespace: false } @namespace ? @namespace.ToDisplayString()
            : null;
        return container is null ? named.Name + arguments : $"{container}.{named.Name}{arguments}";
    }

    private static bool IsOrInGenericType(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
                return true;
        }

        return false;
    }

    public static string Method(IMethodSymbol method)
    {
        var typeParameters = method.IsGenericMethod ? $"<{string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name))}>" : string.Empty;
        var parameters = string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(PARAMETER_TYPE_FORMAT)));
        return $"{Type(method.ContainingType)}.{method.Name}{typeParameters}({parameters})";
    }
}
