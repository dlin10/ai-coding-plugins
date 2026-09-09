using Microsoft.CodeAnalysis;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>The finder for the uniform forms: a type implements the shape, and the named method on it is the
/// entry point. Two instances sit at two different positions in the finder order, so the rows each was given
/// are exposed to tell them apart.</summary>
internal sealed class RecognizerEntryPointFinder(IReadOnlyList<EntryPointRecognizer> recognizers) : IEntryPointFinder
{
    internal IReadOnlyList<EntryPointRecognizer> Recognizers { get; } = recognizers;

    public void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints)
    {
        foreach (var recognizer in Recognizers)
        {
            AddHandlingMethods(type, recognizer.ShapeName, recognizer.Arity, recognizer.MethodName, recognizer.Kind, entryPoints);
        }
    }

    private static void AddHandlingMethods(INamedTypeSymbol type, string shapeName, int arity, string methodName, string kind,
                                           ICollection<EntryPoint> entryPoints)
    {
        var interfaces = type.AllInterfaces.Where(candidate => TypeShapes.HasShape(candidate, shapeName, arity)).ToArray();
        foreach (var interfaceType in interfaces)
        {
            foreach (var member in interfaceType.GetMembers().OfType<IMethodSymbol>().Where(method => method.Name == methodName))
            {
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                    implementation.Locations.Any(location => location.IsInSource))
                {
                    entryPoints.Add(new EntryPoint(implementation, kind, []));
                }
            }
        }

        if (interfaces.Length == 0 && TypeShapes.DerivesFrom(type, shapeName, arity))
        {
            EntryPointSymbols.AddMethods(type, methodName, kind, entryPoints);
        }
    }
}
