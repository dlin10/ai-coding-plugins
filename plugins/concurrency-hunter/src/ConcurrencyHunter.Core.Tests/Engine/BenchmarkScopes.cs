using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The single owner of the CoreLib benchmark's whole-member extern selection.</summary>
internal static class BenchmarkScopes
{
    internal static IReadOnlySet<string> Select(Compilation compilation, string scope)
    {
        if (scope is not ("four" or "all"))
            throw new ArgumentOutOfRangeException(nameof(scope));
        var selected = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var types = Types(compilation.Assembly.GlobalNamespace).ToArray();
        foreach (var type in types)
        {
            foreach (var member in type.GetMembers())
            {
                if (scope == "all" ? Overrides(member) : OverridesObject(member))
                    selected.Add(Whole(member));
            }

            foreach (var contract in type.AllInterfaces)
            {
                foreach (var member in contract.GetMembers())
                {
                    if (scope == "four" && !(contract.SpecialType == SpecialType.System_IDisposable &&
                                            member is IMethodSymbol { Name: "Dispose", Parameters.Length: 0 }))
                        continue;
                    if (type.FindImplementationForInterfaceMember(member) is { } implementation)
                        selected.Add(Whole(implementation));
                }
            }

            // A default interface implementation implements that interface's own slot.
            if (scope == "all" && type.TypeKind == TypeKind.Interface)
            {
                foreach (var member in type.GetMembers().Where(member => member.IsVirtual || member.IsAbstract))
                    selected.Add(Whole(member));
            }
        }

        return selected.Where(member => SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, compilation.Assembly) && HasBody(member))
                       .Select(member => member.GetDocumentationCommentId()).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static ISymbol Whole(ISymbol member) => member is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : member;

    private static bool Overrides(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod is not null,
        IPropertySymbol property => property.OverriddenProperty is not null ||
                                    property.GetMethod?.OverriddenMethod is not null || property.SetMethod?.OverriddenMethod is not null,
        IEventSymbol @event => @event.OverriddenEvent is not null ||
                              @event.AddMethod?.OverriddenMethod is not null || @event.RemoveMethod?.OverriddenMethod is not null,
        _ => false
    };

    private static bool OverridesObject(ISymbol member)
    {
        if (member is not IMethodSymbol method)
            return false;
        for (var current = method.OverriddenMethod; current is not null; current = current.OverriddenMethod)
        {
            if (current.ContainingType.SpecialType == SpecialType.System_Object &&
                (current.Name is "ToString" or "GetHashCode" && current.Parameters.Length == 0 ||
                 current.Name == "Equals" && current.Parameters is [{ Type.SpecialType: SpecialType.System_Object }]))
                return true;
        }

        return false;
    }

    private static bool HasBody(ISymbol member)
    {
        if (member.IsAbstract || member.IsExtern ||
            member is IEventSymbol { ExplicitInterfaceImplementations.IsEmpty: false })
            return false;
        return member.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() switch
        {
            MethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
            BasePropertyDeclarationSyntax property => property.AccessorList is not null || property switch
            {
                PropertyDeclarationSyntax p => p.ExpressionBody is not null,
                IndexerDeclarationSyntax i => i.ExpressionBody is not null,
                _ => false
            },
            VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } => true,
            _ => false
        });
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns) =>
        ns.GetTypeMembers().SelectMany(NestedAndSelf).Concat(ns.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
