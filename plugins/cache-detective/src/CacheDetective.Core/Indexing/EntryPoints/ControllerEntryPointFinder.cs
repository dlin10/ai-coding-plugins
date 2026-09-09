using Microsoft.CodeAnalysis;
using CacheDetective.External;
using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>MVC and Web API actions. Not a uniform form: the entry point is every public action on the type
/// rather than one named method, and each carries the routes its attributes and its controller's prefixes
/// spell out, so the route reading lives here with the branch that needs it.</summary>
internal sealed class ControllerEntryPointFinder : IEntryPointFinder
{
    public void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints)
    {
        if (TypeShapes.DerivesFrom(type, "ControllerBase", 0) || TypeShapes.HasAttribute(type, "ApiControllerAttribute", 0))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(TypeShapes.IsPublicAction))
            {
                entryPoints.Add(new EntryPoint(method, "controller", GetControllerRoutes(type, method)));
            }
        }
    }

    private static IReadOnlyList<HandlerRoute> GetControllerRoutes(INamedTypeSymbol type, IMethodSymbol method)
    {
        var prefixes = type.GetAttributes().Where(attribute => attribute.AttributeClass?.Name == "RouteAttribute")
                           .Select(AttributeTemplate).DefaultIfEmpty(string.Empty).ToArray();
        var routes = new List<HandlerRoute>();
        var attributes = method.GetAttributes();
        var routeTemplates = attributes.Where(attribute => attribute.AttributeClass?.Name == "RouteAttribute")
                                       .Select(AttributeTemplate).ToArray();
        var verbs = attributes.Select(attribute => (Method: HttpMethod(attribute), Template: AttributeTemplate(attribute)))
                              .Where(attribute => attribute.Method is not null).ToArray();
        var bareVerbs = verbs.Where(verb => verb.Template.Length == 0).Select(verb => verb.Method!).ToArray();

        foreach (var template in routeTemplates)
        {
            foreach (var httpMethod in bareVerbs.DefaultIfEmpty("*"))
                AddRoute(httpMethod, template);
        }
        foreach (var verb in verbs.Where(verb => verb.Template.Length > 0))
            AddRoute(verb.Method!, verb.Template);
        if (routeTemplates.Length == 0)
        {
            foreach (var httpMethod in bareVerbs)
                AddRoute(httpMethod, string.Empty);
        }

        return routes;

        void AddRoute(string httpMethod, string template)
        {
            var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^10] : type.Name;
            foreach (var prefix in prefixes)
            {
                var combined = $"{prefix}/{template}".Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                                                   .Replace("[action]", method.Name, StringComparison.OrdinalIgnoreCase);
                routes.Add(new HandlerRoute("http", httpMethod, PathTemplates.Normalize(combined)));
            }
        }
    }

    private static string? HttpMethod(AttributeData attribute) => attribute.AttributeClass?.Name switch
    {
        "HttpGetAttribute" => "GET",
        "HttpPostAttribute" => "POST",
        "HttpPutAttribute" => "PUT",
        "HttpDeleteAttribute" => "DELETE",
        "HttpPatchAttribute" => "PATCH",
        "HttpHeadAttribute" => "HEAD",
        _ => null
    };

    private static string AttributeTemplate(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault() is { Kind: not TypedConstantKind.Array } argument &&
        argument.Value is string template ? template : string.Empty;
}
