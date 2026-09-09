using Microsoft.CodeAnalysis;
using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>The graph values an entry point turns into, and the member walk that finds the methods to turn.
/// The other half of the split from <see cref="TypeShapes"/>: everything here produces something.</summary>
internal static class EntryPointSymbols
{
    internal static void AddMethods(INamedTypeSymbol type, string methodName, string kind, ICollection<EntryPoint> entryPoints)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var methods = current.GetMembers()
                                 .OfType<IMethodSymbol>()
                                 .Where(method => method.Name == methodName || method.Name.EndsWith($".{methodName}", StringComparison.Ordinal))
                                 .Where(method => method.Locations.Any(location => location.IsInSource))
                                 .ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                entryPoints.Add(new EntryPoint(method, kind, []));
            }

            return;
        }
    }

    internal static Handler CreateHandler(IMethodSymbol method, string solutionName, string kind, IReadOnlyList<HandlerRoute>? routes = null)
    {
        var location = GetSourceLocation(method);
        var lineSpan = location.GetLineSpan();
        var symbol = method.MethodKind == MethodKind.AnonymousFunction
                         ? $"{method.ContainingSymbol.ToDisplayString()}::<lambda>@{lineSpan.StartLinePosition.Line + 1}:" +
                           $"{lineSpan.StartLinePosition.Character + 1}"
                         : method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        return new Handler(solutionName, symbol, kind, lineSpan.Path, lineSpan.StartLinePosition.Line + 1)
        {
            Project = method.ContainingAssembly.Name,
            Routes = routes ?? []
        };
    }

    internal static Location GetSourceLocation(IMethodSymbol method) =>
        method.Locations.First(location => location.IsInSource);

    internal static Evidence CreateEvidence(SyntaxNode syntax)
    {
        return CreateEvidence(syntax.GetLocation());
    }

    internal static Evidence CreateEvidence(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    internal static string GetFullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
}
