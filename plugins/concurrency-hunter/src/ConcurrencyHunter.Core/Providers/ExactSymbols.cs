using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers;

/// <summary>Framework symbols resolved by identity, not by name: a type counts only when it is declared in an
/// assembly with the expected name and a version inside the supported range.</summary>
public static class ExactSymbols
{
    public static IAssemblySymbol? FindReferencedAssembly(Compilation compilation, string assemblyName) =>
        compilation.References
                   .Select(compilation.GetAssemblyOrModuleSymbol)
                   .OfType<IAssemblySymbol>()
                   .FirstOrDefault(assembly => string.Equals(assembly.Identity.Name, assemblyName, StringComparison.Ordinal));

    public static INamedTypeSymbol? FindType(Compilation compilation, SupportedAssemblyVersion range,
                                             string metadataName) =>
        compilation.GetTypesByMetadataName(metadataName)
                   .FirstOrDefault(type => IsInRange(type.ContainingAssembly, range));

    public static bool IsType(ITypeSymbol? type, SupportedAssemblyVersion range, string metadataName) =>
        type?.OriginalDefinition is INamedTypeSymbol named &&
        IsInRange(named.ContainingAssembly, range) &&
        string.Equals(MetadataName(named), metadataName, StringComparison.Ordinal);

    public static bool IsInRange(IAssemblySymbol? assembly, SupportedAssemblyVersion range) =>
        assembly is not null &&
        string.Equals(assembly.Identity.Name, range.AssemblyName, StringComparison.Ordinal) &&
        range.Contains(assembly.Identity.Version);

    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
            return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        return type.ContainingNamespace.IsGlobalNamespace
            ? type.MetadataName
            : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    }
}
