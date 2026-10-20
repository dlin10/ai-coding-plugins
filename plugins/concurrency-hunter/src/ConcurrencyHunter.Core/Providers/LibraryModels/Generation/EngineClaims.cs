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

    /// <summary>The recognizer claiming a method: the lowering's claim of the member itself, as on a task's awaiter, or else the claim
    /// by the metadata name of its outermost containing type, wherever that type is declared; <c>null</c> when none does.</summary>
    /// <param name="method">The method, from metadata or from source.</param>
    public static string? Of(IMethodSymbol method)
    {
        if (IrLowering.RecognizerOf(method) is { } member)
            return member;
        var type = method.ContainingType?.OriginalDefinition;
        while (type?.ContainingType is { } outer)
        {
            WorkCancellation.Check();
            type = outer.OriginalDefinition;
        }
        if (type is null)
            return null;
        var name = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
        return IrLowering.RecognizerOf(name) ?? (DiIndexBuilder.IsRegistrationType(name) ? DI_REGISTRATION : null);
    }

    /// <summary>The first method of an assembly's own types, in declaration order, that a recognizer claims; <c>null</c> when the
    /// engine claims none of them.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="cancellationToken">Cancels the scan at the next type.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    public static EngineClaim? FirstIn(IAssemblySymbol assembly, CancellationToken cancellationToken = default) =>
        FirstIn(assembly, new GenerationChecks(cancellationToken));

    /// <summary>The first method of an assembly's own types, in declaration order, that a recognizer claims, as a stage of a generation:
    /// it checks before each type it scans.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="checks">The generation's checks, which cancel the scan.</param>
    internal static EngineClaim? FirstIn(IAssemblySymbol assembly, GenerationChecks checks)
    {
        foreach (var type in Types(assembly.GlobalNamespace))
        {
            checks.Check(GenerationStage.EngineClaims);
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                WorkCancellation.Check();
                if (Of(method) is { } recognizer)
                    return new EngineClaim(method.GetDocumentationCommentId() ?? method.ToDisplayString(), recognizer);
            }
        }

        return null;
    }

    /// <summary>The definition keys of the types of CoreLib a recognizer owns, those nested in them included
    /// (<see cref="IrLowering.IsRecognizedCoreLibType"/>): CoreLib's generation leaves them without bodies and reads their calls as a
    /// program reads them from metadata (question 144, ADR 0019). Empty for any other assembly.</summary>
    /// <param name="assembly">The assembly the generation compiles.</param>
    public static IEnumerable<string> RecognizedCoreLibTypeKeys(IAssemblySymbol assembly) =>
        Types(assembly.GlobalNamespace).Where(IrLowering.IsRecognizedCoreLibType).Select(type => SymbolNames.TypeKey(type.OriginalDefinition));

    /// <summary>Every type a namespace declares, in it or in the namespaces in it, with the types nested in them.</summary>
    /// <param name="namespace">The namespace.</param>
    internal static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(Nested).Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);
}
