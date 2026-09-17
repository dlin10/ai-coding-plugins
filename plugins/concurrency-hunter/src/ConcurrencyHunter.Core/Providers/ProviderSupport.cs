using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Providers;

internal static class ProviderSupport
{
    /// <summary>One <see cref="RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion"/> per compilation and referenced assembly of
    /// <paramref name="ranges"/> whose version is outside its range, and the compilations with at least one such assembly, which
    /// root discovery skips.</summary>
    internal static (List<RootDiscoveryDiagnostic> Diagnostics, IReadOnlySet<Compilation> Skipped) VersionDiagnostics(
        RootDiscoveryContext context, string providerId, IReadOnlyList<SupportedAssemblyVersion> ranges)
    {
        var diagnostics = new List<RootDiscoveryDiagnostic>();
        var skipped = new HashSet<Compilation>();
        foreach (var compilation in context.Compilations)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var range in ranges)
            {
                var assembly = ExactSymbols.FindReferencedAssembly(compilation, range.AssemblyName);
                if (assembly is null || range.Contains(assembly.Identity.Version) || !reported.Add(assembly.Identity.Name))
                    continue;

                skipped.Add(compilation);
                diagnostics.Add(new RootDiscoveryDiagnostic(
                    providerId, RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion,
                    $"{compilation.AssemblyName}: {assembly.Identity.Name} {assembly.Identity.Version}",
                    $"{compilation.AssemblyName} references {assembly.Identity.Name} {assembly.Identity.Version}, outside the supported range " +
                    $"{range.Minimum} up to {range.MaximumExclusive}; its roots are not discovered.",
                    []));
            }
        }

        return (diagnostics, skipped);
    }

    /// <summary>The scope as root discovery scans it: every compilation but the skipped ones, or null when every compilation that
    /// references an assembly of <paramref name="ranges"/> is skipped, so the scope is not checked. A scope with no such compilation
    /// is scanned, and checked empty.</summary>
    internal static RootDiscoveryContext? Scanned(RootDiscoveryContext context, IReadOnlyList<SupportedAssemblyVersion> ranges,
                                                  IReadOnlySet<Compilation> skipped)
    {
        var referencing = context.Compilations.Where(compilation => ranges.Any(range => ExactSymbols.FindReferencedAssembly(compilation, range.AssemblyName) is not null))
                                 .ToArray();
        if (referencing.Length != 0 && referencing.All(skipped.Contains))
            return null;
        return skipped.Count == 0 ? context : context with { Compilations = context.Compilations.Where(compilation => !skipped.Contains(compilation)).ToArray() };
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
