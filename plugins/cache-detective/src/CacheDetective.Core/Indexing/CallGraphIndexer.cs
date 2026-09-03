using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using CacheDetective.Caching;
using CacheDetective.Data;
using CacheDetective.Graph;

namespace CacheDetective.Indexing;

public sealed class CallGraphIndexer
{
    private const int MaximumDepth = 12;
    private static readonly IEqualityComparer<IMethodSymbol> MethodComparer = new MethodSymbolComparer();

    private static readonly HashSet<string> MinimalApiMethods =
    [
        "MapGet",
        "MapPost",
        "MapPut",
        "MapDelete",
        "MapPatch",
        "MapMethods"
    ];

    public async Task<CacheGraph> IndexAsync(Solution solution, string solutionName,
                                              CancellationToken cancellationToken = default)
    {
        var graph = new CacheGraph();
        var cacheCallAnalyzer = new CacheCallAnalyzer(solution);
        var efReadAnalyzer = new EfReadAnalyzer(solution);
        var efWriteAnalyzer = new EfWriteAnalyzer(efReadAnalyzer);
        var unparsedSqlAnalyzer = new UnparsedSqlAnalyzer();
        var entryPoints = await FindEntryPointsAsync(solution, cancellationToken);
        var entryPointKinds = entryPoints
            .GroupBy(entry => entry.Method, MethodComparer)
            .ToDictionary(group => group.Key, group => group.First().Kind,
                          MethodComparer);
        var shallowestDepth = new Dictionary<IMethodSymbol, int>(MethodComparer);

        foreach (var entryPoint in entryPoints)
        {
            graph.AddHandler(CreateHandler(entryPoint.Method, solutionName, entryPoint.Kind));
            await WalkAsync(entryPoint.Method, 0, new HashSet<IMethodSymbol>(MethodComparer));
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
            var currentHandler = CreateHandler(method, solutionName, GetKind(method, entryPointKinds));
            cacheCallAnalyzer.RecordUnsupportedAttributes(graph, currentHandler, method);
            await efReadAnalyzer.AnalyzeAsync(graph, currentHandler, method, cancellationToken);
            await efWriteAnalyzer.AnalyzeAsync(solution, currentHandler, method, cancellationToken);
            await unparsedSqlAnalyzer.AnalyzeAsync(solution, graph, currentHandler, method,
                cancellationToken);

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

                if (await cacheCallAnalyzer.TryAnalyzeAsync(graph, currentHandler, invocation, semanticModel,
                                                            cancellationToken))
                {
                    continue;
                }

                if (depth == MaximumDepth)
                {
                    AddUnresolved(graph, currentHandler, invocation,
                        $"Maximum call depth of {MaximumDepth} reached.");
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
                    AddUnresolved(graph, currentHandler, invocation,
                        $"No implementation found for {calledMethod.ToDisplayString()}.");
                    continue;
                }

                var confidence = targets.IsInterfaceCall && targets.Methods.Count > 1
                    ? Confidence.Likely
                    : Confidence.Confirmed;
                var from = CreateHandler(method, solutionName, GetKind(method, entryPointKinds));
                var evidence = CreateEvidence(invocation);

                foreach (var target in targets.Methods)
                {
                    efWriteAnalyzer.RecordCall(method, target);
                    var to = CreateHandler(target, solutionName, GetKind(target, entryPointKinds));
                    graph.AddEdge(new Calls(from, to, confidence, [evidence]));

                    if (!activePath.Contains(target))
                    {
                        await WalkAsync(target, depth + 1, new HashSet<IMethodSymbol>(activePath,
                            MethodComparer));
                    }
                }
            }
        }
    }

    private static async Task<IReadOnlyList<(IMethodSymbol Method, string Kind)>> FindEntryPointsAsync(
        Solution solution, CancellationToken cancellationToken)
    {
        var entryPoints = new List<(IMethodSymbol Method, string Kind)>();

        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
            {
                AddTypeEntryPoints(type, entryPoints);
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
                    if (mappedMethod is null || !MinimalApiMethods.Contains(mappedMethod.Name))
                    {
                        continue;
                    }

                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        var handler = GetHandlerMethod(semanticModel, argument.Expression, cancellationToken);
                        if (handler is not null && handler.Locations.Any(location => location.IsInSource))
                        {
                            entryPoints.Add((handler, "minimal_api"));
                        }
                    }
                }
            }
        }

        return entryPoints;
    }

    private static void AddTypeEntryPoints(INamedTypeSymbol type,
                                           ICollection<(IMethodSymbol Method, string Kind)> entryPoints)
    {
        if (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return;
        }

        if (DerivesFrom(type, "ControllerBase", 0) || HasAttribute(type, "ApiControllerAttribute", 0))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(IsPublicAction))
            {
                entryPoints.Add((method, "controller"));
            }
        }

        AddHandlingMethods(type, "IRequestHandler", 2, "Handle", "request_handler", entryPoints);
        AddHandlingMethods(type, "IRequestHandler", 1, "Handle", "request_handler", entryPoints);
        AddHandlingMethods(type, "INotificationHandler", 1, "Handle", "notification_handler", entryPoints);
        AddHandlingMethods(type, "IConsumer", 1, "Consume", "consumer", entryPoints);
        AddHandlingMethods(type, "IHandleMessages", 1, "Handle", "message_handler", entryPoints);

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

    private static void AddHandlingMethods(INamedTypeSymbol type, string shapeName, int arity,
                                           string methodName, string kind,
                                           ICollection<(IMethodSymbol Method, string Kind)> entryPoints)
    {
        var interfaces = type.AllInterfaces.Where(candidate => HasShape(candidate, shapeName, arity)).ToArray();
        foreach (var interfaceType in interfaces)
        {
            foreach (var member in interfaceType.GetMembers().OfType<IMethodSymbol>()
                                                .Where(method => method.Name == methodName))
            {
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                    implementation.Locations.Any(location => location.IsInSource))
                {
                    entryPoints.Add((implementation, kind));
                }
            }
        }

        if (interfaces.Length == 0 && DerivesFrom(type, shapeName, arity))
        {
            AddMethods(type, methodName, kind, entryPoints);
        }
    }

    private static void AddMethods(INamedTypeSymbol type, string methodName, string kind,
                                   ICollection<(IMethodSymbol Method, string Kind)> entryPoints)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var methods = current.GetMembers().OfType<IMethodSymbol>()
                                 .Where(method => method.Name == methodName ||
                                                  method.Name.EndsWith($".{methodName}",
                                                      StringComparison.Ordinal))
                                 .Where(method => method.Locations.Any(location => location.IsInSource))
                                 .ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                entryPoints.Add((method, kind));
            }

            return;
        }
    }

    private static bool IsPublicAction(IMethodSymbol method) =>
        method.DeclaredAccessibility == Accessibility.Public &&
        method.MethodKind == MethodKind.Ordinary &&
        !method.IsStatic &&
        !method.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType &&
                                                   HasShape(attributeType, "NonActionAttribute", 0)) &&
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
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeType &&
                                                          HasShape(attributeType, name, arity)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasShape(INamedTypeSymbol type, string name, int arity) =>
        type.Name == name && type.Arity == arity;

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

    private static IMethodSymbol? GetHandlerMethod(SemanticModel semanticModel, ExpressionSyntax expression,
                                                    CancellationToken cancellationToken)
    {
        var operation = semanticModel.GetOperation(expression, cancellationToken);

        return operation switch
        {
            IAnonymousFunctionOperation anonymousFunction => anonymousFunction.Symbol,
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation anonymousFunction } =>
                anonymousFunction.Symbol,
            IDelegateCreationOperation { Target: IMethodReferenceOperation methodReference } =>
                methodReference.Method,
            IMethodReferenceOperation methodReference => methodReference.Method,
            _ => semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol as IMethodSymbol
        };
    }

    private static async Task<(bool IsInterfaceCall, IReadOnlyList<IMethodSymbol> Methods)> ResolveTargetsAsync(
        IMethodSymbol calledMethod, Solution solution, CancellationToken cancellationToken)
    {
        if (calledMethod.ContainingType.TypeKind != TypeKind.Interface)
        {
            return (false, calledMethod.Locations.Any(location => location.IsInSource)
                ? [calledMethod]
                : []);
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(calledMethod, solution,
            cancellationToken: cancellationToken);
        var methods = implementations.OfType<IMethodSymbol>()
                                     .Where(method => !method.IsAbstract)
                                     .Where(method => method.Locations.Any(location => location.IsInSource))
                                     .Distinct(MethodComparer)
                                     .ToArray();
        return (true, methods);
    }

    private static async Task<IReadOnlyList<InvocationExpressionSyntax>> GetInvocationsAsync(
        IMethodSymbol method, CancellationToken cancellationToken)
    {
        var invocations = new List<InvocationExpressionSyntax>();

        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            var root = await syntaxReference.GetSyntaxAsync(cancellationToken);
            invocations.AddRange(root.DescendantNodes(node =>
                                         node is not (AnonymousFunctionExpressionSyntax or
                                             LocalFunctionStatementSyntax))
                                     .OfType<InvocationExpressionSyntax>());
        }

        return invocations;
    }

    private static Handler CreateHandler(IMethodSymbol method, string solutionName, string kind)
    {
        var location = GetSourceLocation(method);
        var lineSpan = location.GetLineSpan();
        var symbol = method.MethodKind == MethodKind.AnonymousFunction
            ? $"{method.ContainingSymbol.ToDisplayString()}::<lambda>@{lineSpan.StartLinePosition.Line + 1}:" +
              $"{lineSpan.StartLinePosition.Character + 1}"
            : method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        return new Handler(solutionName, symbol, kind, lineSpan.Path,
                           lineSpan.StartLinePosition.Line + 1);
    }

    private static string GetKind(IMethodSymbol method,
                                  IReadOnlyDictionary<IMethodSymbol, string> entryPointKinds) =>
        entryPointKinds.TryGetValue(method, out var kind) ? kind : "method";

    private static Location GetSourceLocation(IMethodSymbol method) =>
        method.Locations.First(location => location.IsInSource);

    private static Evidence CreateEvidence(SyntaxNode syntax)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    private static void AddUnresolved(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation,
                                      string reason)
    {
        var evidence = CreateEvidence(invocation);
        graph.AddUnresolved(UnresolvedKind.Call, handler, evidence.File, evidence.Line,
            invocation.ToString(), reason);
    }

    private sealed class MethodSymbolComparer : IEqualityComparer<IMethodSymbol>
    {
        public bool Equals(IMethodSymbol? x, IMethodSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(IMethodSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
    }
}
