using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Frontend;

/// <summary>
/// The members of a C# 14 extension block, <c>extension(R r) { … }</c>, as the compiler emits them: each is a static implementation
/// method of the enclosing static class (<see cref="IMethodSymbol.AssociatedExtensionImplementation"/>) whose parameters are the
/// receiver <c>r</c>, for an instance member, followed by the member's own. A call of the member, <c>x.M(a)</c> or an access of its
/// property, is the call <c>E.M(x, a)</c> of that method, the receiver passed as its parameter's kind says (by value, <c>ref</c>,
/// <c>in</c> or <c>ref readonly</c>), and the method's body is the member's. Roslyn binds that body to the member's symbols, not to the
/// method's: the receiver is the block's <see cref="INamedTypeSymbol.ExtensionParameter"/>, which the block contains, and the member's
/// parameters and locals are contained in the member. This is the one answer to which method a call of such a member runs and to which
/// variables that method's body declares.
/// </summary>
internal static class ExtensionMembers
{
    /// <summary>The method a call of <paramref name="method"/> runs: for a member of an extension block, its implementation, constructed
    /// as the call is; null for any other method.</summary>
    /// <param name="method">The method called.</param>
    public static IMethodSymbol? ImplementationOf(IMethodSymbol method) =>
        method.ContainingType is { IsExtension: true } ? method.AssociatedExtensionImplementation : null;

    /// <summary>The parameter a call of an instance member of an extension block passes its receiver as, the implementation's first;
    /// null for a static member and for a member of any other type.</summary>
    /// <param name="member">The method or property.</param>
    public static IParameterSymbol? ReceiverOf(ISymbol member) =>
        member is { IsStatic: false, ContainingType: { IsExtension: true, ExtensionParameter: { } receiver } } ? receiver : null;

    /// <summary>How far a call's parameter ordinals move from the method named to the method run: one for an instance member of an
    /// extension block, whose implementation takes the receiver first; zero for any other method.</summary>
    /// <param name="method">The method called.</param>
    public static int OrdinalOffsetOf(IMethodSymbol method) => ImplementationOf(method) is not null && ReceiverOf(method) is not null ? 1 : 0;

    /// <summary>The member of an extension block an implementation method was emitted for, as the block declares it; null for any other
    /// method.</summary>
    /// <param name="method">The method whose body is lowered.</param>
    public static IMethodSymbol? MemberOf(IMethodSymbol method)
    {
        if (!method.IsStatic || method.MethodKind == MethodKind.AnonymousFunction || method.ContainingType is not { IsStatic: true } type)
            return null;
        var definition = method.OriginalDefinition;
        foreach (var block in type.GetTypeMembers())
        {
            WorkCancellation.Check();
            if (!block.IsExtension)
                continue;
            foreach (var member in block.GetMembers().OfType<IMethodSymbol>())
            {
                WorkCancellation.Check();
                if (SymbolEqualityComparer.Default.Equals(member.AssociatedExtensionImplementation?.OriginalDefinition, definition))
                    return member;
            }
        }
        return null;
    }

    /// <summary>The parameters a method's body names, in the order of the method's own: for the implementation of an extension block
    /// member, the member's receiver, for an instance member, then the member's parameters; for any other method, its parameters.</summary>
    /// <param name="method">The method whose body is lowered.</param>
    /// <param name="member">The extension block member <paramref name="method"/> implements (<see cref="MemberOf"/>), or null.</param>
    public static ImmutableArray<IParameterSymbol> ParametersOf(IMethodSymbol method, IMethodSymbol? member) =>
        member is null ? method.Parameters : ReceiverOf(member) is { } receiver ? [receiver, .. member.Parameters] : member.Parameters;

    /// <summary>Whether a method's body declares a local or a parameter, rather than capturing it from an enclosing body: one the method
    /// contains, or, for the implementation of an extension block member, the member's receiver or one the member contains.</summary>
    /// <param name="method">The method whose body is lowered.</param>
    /// <param name="member">The extension block member <paramref name="method"/> implements (<see cref="MemberOf"/>), or null.</param>
    /// <param name="symbol">The local or parameter.</param>
    public static bool Declares(IMethodSymbol method, IMethodSymbol? member, ISymbol symbol) =>
        SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, method) ||
        member is not null && (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, member) ||
                               SymbolEqualityComparer.Default.Equals(symbol, ReceiverOf(member)));
}
