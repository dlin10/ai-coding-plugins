using Microsoft.CodeAnalysis;
using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>gRPC service implementations. The shape is not an interface but the generated base the type
/// derives from, which carries <c>BindServiceMethodAttribute</c>; the entry points are the overrides on it,
/// and the route is the service name the generated base is nested in.</summary>
internal sealed class GrpcEntryPointFinder : IEntryPointFinder
{
    public void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints)
    {
        if (type.BaseType is { } baseType && baseType.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "BindServiceMethodAttribute"))
        {
            var service = baseType.ContainingType?.Name;
            if (service is not null)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>()
                                    .Where(method => method.DeclaredAccessibility == Accessibility.Public && method.IsOverride &&
                                                     method.Locations.Any(location => location.IsInSource)))
                {
                    entryPoints.Add(new EntryPoint(method, "grpc", [new HandlerRoute("grpc", "*", $"{service}/{method.Name}")]));
                }
            }
        }
    }
}
