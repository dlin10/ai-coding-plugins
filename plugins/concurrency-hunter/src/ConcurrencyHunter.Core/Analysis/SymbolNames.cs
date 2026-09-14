using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

public static class SymbolNames
{
    private static readonly SymbolDisplayFormat TYPE_FORMAT =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat PARAMETER_TYPE_FORMAT = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                                                                          genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                                                                          miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string Type(INamedTypeSymbol type) => type.ToDisplayString(TYPE_FORMAT);

    public static string Method(IMethodSymbol method)
    {
        var typeParameters = method.IsGenericMethod ? $"<{string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name))}>" : string.Empty;
        var parameters = string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(PARAMETER_TYPE_FORMAT)));
        return $"{Type(method.ContainingType)}.{method.Name}{typeParameters}({parameters})";
    }
}
