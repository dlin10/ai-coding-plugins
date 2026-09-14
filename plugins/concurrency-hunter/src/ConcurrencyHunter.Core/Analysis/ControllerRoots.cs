using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

internal static class ControllerRoots
{
    private const string CONTROLLER_BASE = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string NON_ACTION_ATTRIBUTE = "Microsoft.AspNetCore.Mvc.NonActionAttribute";

    internal static IReadOnlyList<DiscoveredRoot> Discover(Compilation compilation)
    {
        var roots = new List<DiscoveredRoot>();
        foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || !DerivesFromControllerBase(type))
            {
                continue;
            }

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!IsAction(method))
                    continue;

                var symbol = SymbolNames.Method(method);
                var root = new ExecutionRoot($"aspnetcore-action:{method.ContainingAssembly.Name}:{method.GetDocumentationCommentId()}", symbol,
                                             $"ControllerBase action {symbol}");
                roots.Add(new DiscoveredRoot(method, root));
            }
        }

        return roots;
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol @namespace)
    {
        foreach (var member in @namespace.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (var type in SourceTypes(childNamespace))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var sourceType in SourceTypes(type))
                    yield return sourceType;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamedTypeSymbol type)
    {
        if (type.Locations.Any(location => location.IsInSource))
            yield return type;

        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var sourceType in SourceTypes(nested))
                yield return sourceType;
        }
    }

    private static bool DerivesFromControllerBase(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (SymbolNames.Type(baseType.OriginalDefinition) == CONTROLLER_BASE)
                return true;
        }

        return false;
    }

    private static bool IsAction(IMethodSymbol method) => method.MethodKind == MethodKind.Ordinary 
                                                          && method.DeclaredAccessibility == Accessibility.Public 
                                                          && !method.IsStatic 
                                                          && !method.IsAbstract 
                                                          && method.DeclaringSyntaxReferences.Length != 0 
                                                          && !method.GetAttributes().Any(attribute => attribute.AttributeClass is not null &&
                                                                                                      SymbolNames.Type(attribute.AttributeClass) == NON_ACTION_ATTRIBUTE);

    internal sealed record DiscoveredRoot(IMethodSymbol Method, ExecutionRoot Root);
}
