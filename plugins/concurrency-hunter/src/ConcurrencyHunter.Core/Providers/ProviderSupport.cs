using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Providers;

internal static class ProviderSupport
{
    /// <summary>One <see cref="RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion"/> per referenced assembly of
    /// <paramref name="ranges"/> whose version is outside its range.</summary>
    internal static List<RootDiscoveryDiagnostic> VersionDiagnostics(RootDiscoveryContext context, string providerId,
                                                                     IEnumerable<SupportedAssemblyVersion> ranges)
    {
        var diagnostics = new List<RootDiscoveryDiagnostic>();
        var reported = new HashSet<(string, Version)>();
        foreach (var range in ranges)
        {
            foreach (var compilation in context.Compilations)
            {
                var assembly = ExactSymbols.FindReferencedAssembly(compilation, range.AssemblyName);
                if (assembly is null || range.Contains(assembly.Identity.Version) ||
                    !reported.Add((assembly.Identity.Name, assembly.Identity.Version)))
                {
                    continue;
                }

                diagnostics.Add(new RootDiscoveryDiagnostic(
                    providerId, RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion, assembly.Identity.Name,
                    $"{assembly.Identity.Name} {assembly.Identity.Version} is outside the supported range {range.Minimum} up to {range.MaximumExclusive}.",
                    []));
            }
        }

        return diagnostics;
    }

    internal static bool References(RootDiscoveryContext context, SupportedAssemblyVersion range) =>
        context.Compilations.Any(compilation => ExactSymbols.FindReferencedAssembly(compilation, range.AssemblyName) is not null);

    internal static bool HasSourceBody(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is MethodDeclarationSyntax { Body: not null } or MethodDeclarationSyntax { ExpressionBody: not null });

    internal static Compilation? CompilationOf(RootDiscoveryContext context, ISymbol symbol) =>
        context.Compilations.FirstOrDefault(compilation => SymbolEqualityComparer.Default.Equals(compilation.Assembly, symbol.ContainingAssembly));

    internal static SourceSpan Source(ISymbol symbol, string rootDirectory) =>
        symbol.Locations.FirstOrDefault(location => location.IsInSource) is { } location
            ? SourceSpans.From(location, rootDirectory)
            : new SourceSpan("", 0, 0, 0, 0);

    internal static bool DerivesFrom(ITypeSymbol type, ITypeSymbol? baseType)
    {
        for (var current = type; current is not null && baseType is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                return true;
        }

        return false;
    }

    internal static bool Overrides(IMethodSymbol method, IMethodSymbol baseMethod)
    {
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseMethod.OriginalDefinition))
                return true;
        }

        return false;
    }
}
