using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using CacheDetective.Caching;
using CacheDetective.Data;
using CacheDetective.Events;
using CacheDetective.External;
using CacheDetective.Graph;

namespace CacheDetective.Indexing;

public sealed class CallGraphIndexer
{
    private const int MAXIMUM_DEPTH = 12;
    private static readonly IEqualityComparer<IMethodSymbol> METHOD_COMPARER = new MethodSymbolComparer();

    private static readonly HashSet<string> MINIMAL_API_METHODS =
    [
        "MapGet",
        "MapPost",
        "MapPut",
        "MapDelete",
        "MapPatch",
        "MapMethods"
    ];

    private readonly IndexerOptions _options;

    public CallGraphIndexer()
        : this(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All))
    {
    }

    public CallGraphIndexer(IndexerOptions options)
    {
        _options = options;
    }

    public async Task<CacheGraph> IndexAsync(Solution solution, string solutionName, CancellationToken cancellationToken = default)
    {
        var graph = new CacheGraph();
        var cacheCallAnalyzer = new CacheCallAnalyzer(solution, _options.CacheRecognizers);
        var eventCallAnalyzer = new EventCallAnalyzer(solution, _options.EventRecognizers);
        var httpCallAnalyzer = new HttpCallAnalyzer(solution);
        var efReadAnalyzer = new EfReadAnalyzer(solution);
        var efWriteAnalyzer = new EfWriteAnalyzer(efReadAnalyzer);
        var sqlAnalyzer = new SqlAnalyzer(solution);
        var entryPoints = await FindEntryPointsAsync(solution, solutionName, graph, _options.EventRecognizers, cancellationToken);
        var entryPointKinds = entryPoints.GroupBy(entry => entry.Method, METHOD_COMPARER)
                                         .ToDictionary(group => group.Key, group => group.First().Kind, METHOD_COMPARER);
        var entryPointRoutes = entryPoints.GroupBy(entry => entry.Method, METHOD_COMPARER)
                                          .ToDictionary(group => group.Key, group => group.SelectMany(entry => entry.Routes).ToArray(), METHOD_COMPARER);
        var shallowestDepth = new Dictionary<IMethodSymbol, int>(METHOD_COMPARER);

        foreach (var entryPoint in entryPoints)
        {
            graph.AddHandler(CreateHandler(entryPoint.Method, solutionName, entryPoint.Kind, entryPoint.Routes));
            await WalkAsync(entryPoint.Method, 0, new HashSet<IMethodSymbol>(METHOD_COMPARER));
        }

        await efWriteAnalyzer.AddEdgesAsync(graph, cancellationToken);
        new CacheRoleClassifier().Classify(graph, solutionName);

        return graph;

        async Task WalkAsync(IMethodSymbol method, int depth, HashSet<IMethodSymbol> activePath)
        {
            if (activePath.Contains(method))
            {
                return;
            }

            if (shallowestDepth.TryGetValue(method, out var previousDepth) && previousDepth <= depth)
            {
                return;
            }

            shallowestDepth[method] = depth;
            activePath.Add(method);
            var currentHandler = CreateHandler(method, solutionName, GetKind(method, entryPointKinds), GetRoutes(method, entryPointRoutes));
            cacheCallAnalyzer.RecordUnsupportedAttributes(graph, currentHandler, method);
            await efReadAnalyzer.AnalyzeAsync(graph, currentHandler, method, cancellationToken);
            await efWriteAnalyzer.AnalyzeAsync(solution, currentHandler, method, cancellationToken);
            await sqlAnalyzer.AnalyzeAsync(graph, currentHandler, method, cancellationToken);

            foreach (var invocation in await GetInvocationsAsync(method, cancellationToken))
            {
                var document = solution.GetDocument(invocation.SyntaxTree);
                if (document is null)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel is null)
                {
                    continue;
                }

                if (await cacheCallAnalyzer.TryAnalyzeAsync(graph, currentHandler, invocation, semanticModel, cancellationToken))
                {
                    continue;
                }

                if (await eventCallAnalyzer.TryAnalyzeAsync(graph, currentHandler, method, invocation, semanticModel, cancellationToken))
                {
                    continue;
                }

                if (await httpCallAnalyzer.TryAnalyzeAsync(graph, currentHandler, invocation, semanticModel, cancellationToken))
                {
                    continue;
                }

                if (depth == MAXIMUM_DEPTH)
                {
                    AddUnresolved(graph, currentHandler, invocation, $"Maximum call depth of {MAXIMUM_DEPTH} reached.");
                    continue;
                }

                var calledMethod = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (calledMethod is null)
                {
                    continue;
                }

                var targets = await ResolveTargetsAsync(calledMethod, solution, cancellationToken);
                if (targets.IsInterfaceCall && targets.Methods.Count == 0)
                {
                    AddUnresolved(graph, currentHandler, invocation, $"No implementation found for {calledMethod.ToDisplayString()}.");
                    continue;
                }

                var confidence = targets.IsInterfaceCall && targets.Methods.Count > 1 ? Confidence.Likely : Confidence.Confirmed;
                var from = CreateHandler(method, solutionName, GetKind(method, entryPointKinds), GetRoutes(method, entryPointRoutes));
                var evidence = CreateEvidence(invocation);

                foreach (var target in targets.Methods)
                {
                    efWriteAnalyzer.RecordCall(method, target);
                    var to = CreateHandler(target, solutionName, GetKind(target, entryPointKinds), GetRoutes(target, entryPointRoutes));
                    graph.AddEdge(new Calls(from, to, confidence, [evidence]));

                    if (!activePath.Contains(target))
                    {
                        await WalkAsync(target, depth + 1, new HashSet<IMethodSymbol>(activePath, METHOD_COMPARER));
                    }
                }
            }
        }
    }

    private static async Task<IReadOnlyList<EntryPoint>> FindEntryPointsAsync(
        Solution solution, string solutionName, CacheGraph graph, IReadOnlyList<EventRecognizer> eventRecognizers,
        CancellationToken cancellationToken)
    {
        var entryPoints = new List<EntryPoint>();
        var consumerForms = ConsumerForms(eventRecognizers);

        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
            {
                AddTypeEntryPoints(type, solutionName, graph, consumerForms, entryPoints);
            }

            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || semanticModel is null)
                {
                    continue;
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var mappedMethod = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                    if (mappedMethod is null || !MINIMAL_API_METHODS.Contains(mappedMethod.Name))
                    {
                        continue;
                    }

                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        var handler = GetHandlerMethod(semanticModel, argument.Expression, cancellationToken);
                        if (handler is not null && handler.Locations.Any(location => location.IsInSource))
                        {
                            entryPoints.Add(new EntryPoint(handler, "minimal_api", GetMinimalRoutes(mappedMethod, invocation, semanticModel)));
                        }
                    }
                }
            }
        }

        return entryPoints;
    }

    private static void AddTypeEntryPoints(INamedTypeSymbol type, string solutionName, CacheGraph graph,
                                           IReadOnlyList<EventRecognizer> consumerForms,
                                           ICollection<EntryPoint> entryPoints)
    {
        if (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return;
        }

        if (DerivesFrom(type, "ControllerBase", 0) || HasAttribute(type, "ApiControllerAttribute", 0))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(IsPublicAction))
            {
                entryPoints.Add(new EntryPoint(method, "controller", GetControllerRoutes(type, method)));
            }
        }

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

        AddHandlingMethods(type, "IRequestHandler", 2, "Handle", "request_handler", entryPoints);
        AddHandlingMethods(type, "IRequestHandler", 1, "Handle", "request_handler", entryPoints);
        foreach (var recognizer in consumerForms)
        {
            AddEventHandlingMethods(type, solutionName, graph, recognizer, entryPoints);
        }

        if (DerivesFrom(type, "BackgroundService", 0))
        {
            AddMethods(type, "ExecuteAsync", "background_service", entryPoints);
        }
        else
        {
            AddHandlingMethods(type, "IHostedService", 0, "StartAsync", "hosted_service", entryPoints);
        }

        AddHandlingMethods(type, "IJob", 0, "Execute", "job", entryPoints);
    }

    private static void AddHandlingMethods(INamedTypeSymbol type, string shapeName, int arity, string methodName, string kind,
                                           ICollection<EntryPoint> entryPoints)
    {
        var interfaces = type.AllInterfaces.Where(candidate => HasShape(candidate, shapeName, arity)).ToArray();
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

        if (interfaces.Length == 0 && DerivesFrom(type, shapeName, arity))
        {
            AddMethods(type, methodName, kind, entryPoints);
        }
    }

    private static IReadOnlyList<EventRecognizer> ConsumerForms(IReadOnlyList<EventRecognizer> recognizers) =>
        recognizers.Select((recognizer, index) => (Recognizer: recognizer, Index: index))
                   .GroupBy(item => (item.Recognizer.ConsumerInterfaceName, item.Recognizer.ConsumerArity,
                                     item.Recognizer.HandleMethod))
                   .Select(group => group.OrderBy(item => item.Recognizer.Confidence)
                                         .ThenBy(item => item.Recognizer.AnnotationId is null ? 0 : 1)
                                         .ThenBy(item => item.Index)
                                         .First()
                                         .Recognizer)
                   .ToArray();

    private static void AddEventHandlingMethods(INamedTypeSymbol type, string solutionName, CacheGraph graph,
                                                EventRecognizer recognizer,
                                                ICollection<EntryPoint> entryPoints)
    {
        var implementations = new HashSet<IMethodSymbol>(METHOD_COMPARER);
        var interfaces = type.AllInterfaces.Where(candidate => HasShape(candidate, recognizer.ConsumerInterfaceName,
                                                                          recognizer.ConsumerArity));
        foreach (var interfaceType in interfaces)
        {
            var contract = interfaceType.TypeArguments[0];
            foreach (var member in interfaceType.GetMembers().OfType<IMethodSymbol>().Where(method => method.Name == recognizer.HandleMethod))
            {
                if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation ||
                    !implementation.Locations.Any(location => location.IsInSource) || !implementations.Add(implementation))
                {
                    continue;
                }

                var handlerKind = string.IsNullOrWhiteSpace(recognizer.HandlerKind) ? "consumer" : recognizer.HandlerKind;
                var handler = CreateHandler(implementation, solutionName, handlerKind);
                entryPoints.Add(new EntryPoint(implementation, handlerKind, []));
                var evidence = CreateEvidence(type.Locations.First(location => location.IsInSource));
                if (contract is ITypeParameterSymbol)
                {
                    var unresolved = graph.AddUnresolved(UnresolvedKind.Event, handler, evidence, interfaceType.ToDisplayString(),
                                                         "Open generic consumer: name its events.");
                    graph.MarkEventSite(unresolved.Id, EventSiteRole.Consume);
                    continue;
                }

                graph.AddEdge(new Consumes(new Event(GetFullName(contract)), handler, recognizer.Confidence, [evidence])
                {
                    AnnotationId = recognizer.AnnotationId
                });
            }
        }
    }

    private static void AddMethods(INamedTypeSymbol type, string methodName, string kind, ICollection<EntryPoint> entryPoints)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var methods = current.GetMembers()
                                 .OfType<IMethodSymbol>()
                                 .Where(method => method.Name == methodName || method.Name.EndsWith($".{methodName}", StringComparison.Ordinal))
                                 .Where(method => method.Locations.Any(location => location.IsInSource))
                                 .ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                entryPoints.Add(new EntryPoint(method, kind, []));
            }

            return;
        }
    }

    private static bool IsPublicAction(IMethodSymbol method) =>
        method.DeclaredAccessibility == Accessibility.Public && method.MethodKind == MethodKind.Ordinary && !method.IsStatic &&
        !method.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType && HasShape(attributeType, "NonActionAttribute", 0)) &&
        method.Locations.Any(location => location.IsInSource);

    private static bool DerivesFrom(INamedTypeSymbol type, string name, int arity)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (HasShape(current, name, arity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAttribute(INamedTypeSymbol type, string name, int arity)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType && HasShape(attributeType, name, arity)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasShape(INamedTypeSymbol type, string name, int arity) =>
        type.Name == name && type.Arity == arity;

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
        attribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;

    private static IReadOnlyList<HandlerRoute> GetMinimalRoutes(IMethodSymbol method, InvocationExpressionSyntax invocation,
                                                                 SemanticModel semanticModel)
    {
        var httpMethod = method.Name switch
        {
            "MapGet" => "GET", "MapPost" => "POST", "MapPut" => "PUT", "MapDelete" => "DELETE", "MapPatch" => "PATCH", _ => "*"
        };
        var template = invocation.ArgumentList.Arguments.FirstOrDefault() is { Expression: var expression } &&
                       semanticModel.GetConstantValue(expression).Value is string value ? value : string.Empty;
        return [new HandlerRoute("http", httpMethod, PathTemplates.Normalize(template))];
    }

    private static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var memberNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetSourceTypes(memberNamespace))
            {
                yield return type;
            }
        }

        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var sourceType in GetSourceTypes(type))
            {
                yield return sourceType;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamedTypeSymbol type)
    {
        if (type.Locations.Any(location => location.IsInSource))
        {
            yield return type;
        }

        foreach (var nestedType in type.GetTypeMembers())
        {
            foreach (var sourceType in GetSourceTypes(nestedType))
            {
                yield return sourceType;
            }
        }
    }

    private static IMethodSymbol? GetHandlerMethod(SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken)
    {
        var operation = semanticModel.GetOperation(expression, cancellationToken);

        return operation switch
               {
                   IAnonymousFunctionOperation anonymousFunction => anonymousFunction.Symbol,
                   IDelegateCreationOperation { Target: IAnonymousFunctionOperation anonymousFunction } => anonymousFunction.Symbol,
                   IDelegateCreationOperation { Target: IMethodReferenceOperation methodReference } => methodReference.Method,
                   IMethodReferenceOperation methodReference => methodReference.Method,
                   _ => semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol as IMethodSymbol
               };
    }

    private static async Task<(bool IsInterfaceCall, IReadOnlyList<IMethodSymbol> Methods)> ResolveTargetsAsync(
        IMethodSymbol calledMethod, Solution solution, CancellationToken cancellationToken)
    {
        if (calledMethod.ContainingType.TypeKind != TypeKind.Interface)
        {
            return (false, calledMethod.Locations.Any(location => location.IsInSource) ? [calledMethod] : []);
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(calledMethod, solution, cancellationToken: cancellationToken);
        var methods = implementations.OfType<IMethodSymbol>()
                                     .Where(method => !method.IsAbstract)
                                     .Where(method => method.Locations.Any(location => location.IsInSource))
                                     .Distinct(METHOD_COMPARER)
                                     .ToArray();
        return (true, methods);
    }

    private static async Task<IReadOnlyList<InvocationExpressionSyntax>> GetInvocationsAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var invocations = new List<InvocationExpressionSyntax>();

        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            var root = await syntaxReference.GetSyntaxAsync(cancellationToken);
            invocations.AddRange(root.DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                                     .OfType<InvocationExpressionSyntax>());
        }

        return invocations;
    }

    private static Handler CreateHandler(IMethodSymbol method, string solutionName, string kind, IReadOnlyList<HandlerRoute>? routes = null)
    {
        var location = GetSourceLocation(method);
        var lineSpan = location.GetLineSpan();
        var symbol = method.MethodKind == MethodKind.AnonymousFunction
                         ? $"{method.ContainingSymbol.ToDisplayString()}::<lambda>@{lineSpan.StartLinePosition.Line + 1}:" +
                           $"{lineSpan.StartLinePosition.Character + 1}"
                         : method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        return new Handler(solutionName, symbol, kind, lineSpan.Path, lineSpan.StartLinePosition.Line + 1)
        {
            Project = method.ContainingAssembly.Name,
            Routes = routes ?? []
        };
    }

    private static string GetKind(IMethodSymbol method, IReadOnlyDictionary<IMethodSymbol, string> entryPointKinds) =>
        entryPointKinds.GetValueOrDefault(method, "method");

    private static IReadOnlyList<HandlerRoute> GetRoutes(IMethodSymbol method,
                                                          IReadOnlyDictionary<IMethodSymbol, HandlerRoute[]> entryPointRoutes) =>
        entryPointRoutes.GetValueOrDefault(method, []);

    private static Location GetSourceLocation(IMethodSymbol method) =>
        method.Locations.First(location => location.IsInSource);

    private static Evidence CreateEvidence(SyntaxNode syntax)
    {
        return CreateEvidence(syntax.GetLocation());
    }

    private static Evidence CreateEvidence(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    private static string GetFullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);

    private static void AddUnresolved(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation, string reason)
    {
        var evidence = CreateEvidence(invocation);
        graph.AddUnresolved(UnresolvedKind.Call, handler, evidence, invocation.ToString(), reason);
    }

    private sealed class MethodSymbolComparer : IEqualityComparer<IMethodSymbol>
    {
        public bool Equals(IMethodSymbol? x, IMethodSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(IMethodSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
    }

    private sealed record EntryPoint(IMethodSymbol Method, string Kind, IReadOnlyList<HandlerRoute> Routes);
}
