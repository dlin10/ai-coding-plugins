using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.External;
using CacheDetective.Graph;
using CacheDetective.Indexing.EntryPoints;

namespace CacheDetective.Indexing;

public sealed class CallGraphIndexer
{
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
    private readonly IReadOnlyList<IEntryPointFinder> _finders;

    public CallGraphIndexer()
        : this(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All), EntryPointTables.Default)
    {
    }

    public CallGraphIndexer(IndexerOptions options)
        : this(options, EntryPointTables.Default)
    {
    }

    internal CallGraphIndexer(IndexerOptions options, EntryPointTables tables)
    {
        _options = options;
        // Built once. ConsumerForms collapses the event recognizers, and a finder list rebuilt inside the
        // type loop would collapse them again for every type in the solution to reach the same answer.
        _finders = Finders(options, tables);
    }

    /// <summary>The forms of entry point, in the order they are asked about a type. The order is the whole
    /// content of this list: it decides the order entry points reach the graph, and an unresolved row's id is
    /// what an annotation binds to.</summary>
    internal static IReadOnlyList<IEntryPointFinder> Finders(IndexerOptions options, EntryPointTables tables) =>
    [
        new ControllerEntryPointFinder(),
        new GrpcEntryPointFinder(),
        new RecognizerEntryPointFinder(tables.RequestHandlers),
        new EventConsumerEntryPointFinder(ConsumerForms(options.EventRecognizers)),
        new HostedServiceEntryPointFinder(tables.HostedServices),
        new RecognizerEntryPointFinder(tables.Jobs)
    ];

    public async Task<CacheGraph> IndexAsync(Solution solution, string solutionName, CancellationToken cancellationToken = default)
    {
        var graph = new CacheGraph();
        var analyzers = SolutionAnalyzers.Create(solution, _options);
        var entryPoints = await FindEntryPointsAsync(solution, solutionName, graph, _finders, cancellationToken);
        var walked = await new CallGraphWalk(solution, solutionName, graph, analyzers).WalkAsync(entryPoints, cancellationToken);

        analyzers.EventCalls.Resolve(graph, walked);
        await analyzers.EfWrites.AddEdgesAsync(graph, cancellationToken);
        new CacheRoleClassifier().Classify(graph, solutionName);

        return graph;
    }

    private static async Task<IReadOnlyList<EntryPoint>> FindEntryPointsAsync(
        Solution solution, string solutionName, CacheGraph graph, IReadOnlyList<IEntryPointFinder> finders,
        CancellationToken cancellationToken)
    {
        var entryPoints = new List<EntryPoint>();
        var context = new EntryPointContext(graph, solutionName);

        // Ordered by path, because Solution.Projects yields whatever order the workspace loaded them in.
        // The set of entry points does not depend on it, but the order rows reach the graph does, and an
        // unresolved row's id is what an annotation binds to: an id that moves between runs binds the
        // annotation to a different site.
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp)
                                        .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
            {
                // One decision, applied once. Dropping it would give abstract classes and interfaces entry
                // points and move the graph; repeating it inside every finder would write it five times.
                if (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
                {
                    continue;
                }

                foreach (var finder in finders)
                {
                    finder.Find(type, context, entryPoints);
                }
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

}
