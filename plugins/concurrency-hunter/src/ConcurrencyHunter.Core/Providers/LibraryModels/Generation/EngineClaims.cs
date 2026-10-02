using ConcurrencyHunter.Frontend;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A method the engine claims by name, outside the library models, and the recognizer that claims it.</summary>
/// <param name="Method">The method's documentation id.</param>
/// <param name="Recognizer">The recognizer: <c>collections</c>, <c>spawn</c>, <c>timer</c>, <c>lock</c>, <c>service-call</c>,
/// <c>recognized</c> or <c>di-registration</c>.</param>
public sealed record EngineClaim(string Method, string Recognizer);

/// <summary>Which recognizer of the engine claims a method by its name (SPEC TD-034b, G-4). The engine claims calls outside the
/// models — the collection table of ADR 0010, the spawn, timer, lock and service-call recognizers, the types whose calls are never
/// unresolved, the DI registrations — and those claims cannot be filtered by assembly; a model the generator derived for a member
/// of such a type would never be read. Every rule that asks this question calls <see cref="Of"/>.</summary>
public static class EngineClaims
{
    private const string DI_REGISTRATION = "di-registration";

    /// <summary>The recognizer claiming a method by the metadata name of its outermost containing type, wherever that type is
    /// declared; <c>null</c> when none does.</summary>
    /// <param name="method">The method, from metadata or from source.</param>
    public static string? Of(IMethodSymbol method)
    {
        var type = method.ContainingType?.OriginalDefinition;
        while (type?.ContainingType is { } outer)
            type = outer.OriginalDefinition;
        if (type is null)
            return null;
        var name = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
        return IrLowering.RecognizerOf(name) ?? (DiIndexBuilder.IsRegistrationType(name) ? DI_REGISTRATION : null);
    }

    /// <summary>The first method of an assembly's own types, in declaration order, that a recognizer claims; <c>null</c> when the
    /// engine claims none of them.</summary>
    /// <param name="assembly">The assembly.</param>
    public static EngineClaim? FirstIn(IAssemblySymbol assembly)
    {
        foreach (var method in Types(assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
        {
            if (Of(method) is { } recognizer)
                return new EngineClaim(method.GetDocumentationCommentId() ?? method.ToDisplayString(), recognizer);
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(Nested).Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);
}
