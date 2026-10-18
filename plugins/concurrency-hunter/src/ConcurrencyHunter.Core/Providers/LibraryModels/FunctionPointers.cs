using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>The one owner of whether a method's signature carries a function-pointer type. A declaration id writes nothing for such a
/// type, so a member that carries one has no id of its own: the run leaves its call opaque and the generator answers
/// <c>unnamed-member</c>. The text form of the same rule is <see cref="DeclarationId.HasEmptyTypeSegment"/>.</summary>
internal static class FunctionPointers
{
    /// <summary>Whether a parameter's type or the return type is a function pointer, directly or as an array's element or a pointer's
    /// target. A type argument cannot be one.</summary>
    /// <param name="member">The method, constructor, operator or accessor.</param>
    internal static bool In(IMethodSymbol member) =>
        member.Parameters.Select(parameter => parameter.Type).Append(member.ReturnType).Any(Holds);

    private static bool Holds(ITypeSymbol type) => type switch
    {
        IFunctionPointerTypeSymbol => true,
        IArrayTypeSymbol array => Holds(array.ElementType),
        IPointerTypeSymbol pointer => Holds(pointer.PointedAtType),
        _ => false
    };
}
