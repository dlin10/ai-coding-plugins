using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>The bridge from the IR's assembly-qualified body id to a member symbol.</summary>
internal static class CallTargets
{
    private const string BODY_PREFIX = "body:";

    internal static IMethodSymbol? Resolve(Compilation compilation, string targetId)
    {
        if (Split(targetId) is not { } parts)
            return null;
        var (assemblyName, id) = parts;
        try
        {
            var named = DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                                              .OfType<IMethodSymbol>()
                                              .Select(symbol => (symbol.ReducedFrom ?? symbol).OriginalDefinition)
                                              .Where(symbol => symbol.ContainingAssembly.Name == assemblyName &&
                                                               string.Equals(symbol.GetDocumentationCommentId(), id, StringComparison.Ordinal))
                                              .Take(2).ToArray();
            return named is [{ } only] ? only : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static (string AssemblyName, string Id)? Split(string bodyId)
    {
        var separator = bodyId.StartsWith(BODY_PREFIX, StringComparison.Ordinal) ? bodyId.IndexOf(':', BODY_PREFIX.Length) : -1;
        return separator > BODY_PREFIX.Length ? (bodyId[BODY_PREFIX.Length..separator], bodyId[(separator + 1)..]) : null;
    }
}
