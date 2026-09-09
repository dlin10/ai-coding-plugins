using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using CacheDetective.Graph;
using CacheDetective.Indexing.EntryPoints;

namespace CacheDetective.Indexing;

/// <summary>The traversal from the entry points outwards: one walk over one solution, recording the call
/// edges and letting each analyzer read the methods it reaches. A walk is used once.</summary>
internal sealed class CallGraphWalk(Solution solution, string solutionName, CacheGraph graph, SolutionAnalyzers analyzers)
{
    private const int MAXIMUM_DEPTH = 12;

    private readonly HashSet<IMethodSymbol> _visited = new(MethodSymbols.Comparer);
    // Every method the walk expanded, so a publish can be attributed to its caller once the walk has
    // finished. Deciding that mid-walk would call a caller unreachable purely because the frontier had
    // not reached it yet. See docs/adr/0017.
    private readonly Dictionary<IMethodSymbol, Handler> _walked = new(MethodSymbols.Comparer);

    // Read from the entry points the walk is given, so they are settled at the top of WalkAsync rather than
    // in the constructor. A walk is used once, so they are written once.
    private IReadOnlyDictionary<IMethodSymbol, string> _entryPointKinds = new Dictionary<IMethodSymbol, string>(MethodSymbols.Comparer);
    private IReadOnlyDictionary<IMethodSymbol, HandlerRoute[]> _entryPointRoutes =
        new Dictionary<IMethodSymbol, HandlerRoute[]>(MethodSymbols.Comparer);

    /// <summary>Walks every method reachable from <paramref name="entryPoints"/> and returns the handler of
    /// each method the walk expanded, which is what attributes a publish to its caller afterwards.</summary>
    internal async Task<IReadOnlyDictionary<IMethodSymbol, Handler>> WalkAsync(IReadOnlyList<EntryPoint> entryPoints,
                                                                               CancellationToken cancellationToken)
    {
        _entryPointKinds = entryPoints.GroupBy(entry => entry.Method, MethodSymbols.Comparer)
                                      .ToDictionary(group => group.Key, group => group.First().Kind, MethodSymbols.Comparer);
        _entryPointRoutes = entryPoints.GroupBy(entry => entry.Method, MethodSymbols.Comparer)
                                       .ToDictionary(group => group.Key, group => group.SelectMany(entry => entry.Routes).ToArray(),
                                                     MethodSymbols.Comparer);
        // The walk is breadth-first, and that is what makes it a function of the solution rather than of
        // the order the solution arrived in. Depth-first with a "walk it again if we found it shallower"
        // memo reached a method at whatever depth the first path happened to offer: a method first met at
        // the depth limit recorded the cut and was then walked again from a shallower caller, recording
        // every edge below it a second time, while the same solution enumerated the other way round
        // recorded them once. Breadth-first expands every method exactly once, at its shortest distance
        // from any entry point, so the limit falls on the same methods every run. See docs/adr/0014.
        var frontier = new List<IMethodSymbol>();

        foreach (var entryPoint in entryPoints)
        {
            graph.AddHandler(EntryPointSymbols.CreateHandler(entryPoint.Method, solutionName, entryPoint.Kind, entryPoint.Routes));
            if (_visited.Add(entryPoint.Method))
            {
                frontier.Add(entryPoint.Method);
            }
        }

        for (var depth = 0; frontier.Count > 0; depth++)
        {
            var next = new List<IMethodSymbol>();
            foreach (var method in frontier)
            {
                await ExpandAsync(method, depth, next, cancellationToken);
            }

            frontier = next;
        }

        return _walked;
    }

    private async Task ExpandAsync(IMethodSymbol method, int depth, ICollection<IMethodSymbol> next, CancellationToken cancellationToken)
    {
        var currentHandler = EntryPointSymbols.CreateHandler(method, solutionName, GetKind(method, _entryPointKinds),
                                                             GetRoutes(method, _entryPointRoutes));
        _walked[method] = currentHandler;
        analyzers.CacheCalls.RecordUnsupportedAttributes(graph, currentHandler, method);
        await analyzers.EfReads.AnalyzeAsync(graph, currentHandler, method, cancellationToken);
        await analyzers.EfWrites.AnalyzeAsync(solution, currentHandler, method, cancellationToken);
        await analyzers.Sql.AnalyzeAsync(graph, currentHandler, method, cancellationToken);

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

            if (await analyzers.CacheCalls.TryAnalyzeAsync(graph, currentHandler, invocation, semanticModel, cancellationToken))
            {
                continue;
            }

            if (await analyzers.EventCalls.TryAnalyzeAsync(graph, currentHandler, method, invocation, semanticModel, cancellationToken))
            {
                continue;
            }

            if (await analyzers.HttpCalls.TryAnalyzeAsync(graph, currentHandler, invocation, semanticModel, cancellationToken))
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
            var evidence = EntryPointSymbols.CreateEvidence(invocation);

            foreach (var target in targets.Methods)
            {
                analyzers.EfWrites.RecordCall(method, target);
                var to = EntryPointSymbols.CreateHandler(target, solutionName, GetKind(target, _entryPointKinds),
                                                         GetRoutes(target, _entryPointRoutes));
                graph.AddEdge(new Calls(currentHandler, to, confidence, [evidence]));

                if (_visited.Add(target))
                {
                    next.Add(target);
                }
            }
        }
    }

    private static async Task<(bool IsInterfaceCall, IReadOnlyList<IMethodSymbol> Methods)> ResolveTargetsAsync(
        IMethodSymbol calledMethod, Solution solution, CancellationToken cancellationToken)
    {
        if (calledMethod.ContainingType.TypeKind != TypeKind.Interface)
        {
            return (false, calledMethod.Locations.Any(location => location.IsInSource) ? [calledMethod] : []);
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(calledMethod, solution, cancellationToken: cancellationToken);
        // SymbolFinder searches the projects in parallel and does not specify the order it hands the
        // results back in. Sorting them is what keeps two runs over one solution recording the same edges
        // in the same order.
        var methods = implementations.OfType<IMethodSymbol>()
                                     .Where(method => !method.IsAbstract)
                                     .Where(method => method.Locations.Any(location => location.IsInSource))
                                     .Distinct(MethodSymbols.Comparer)
                                     .OrderBy(SortKey, StringComparer.Ordinal)
                                     .ToArray();
        return (true, methods);

        static string SortKey(IMethodSymbol method)
        {
            var lineSpan = EntryPointSymbols.GetSourceLocation(method).GetLineSpan();
            return $"{method.ToDisplayString()} {lineSpan.Path} {lineSpan.StartLinePosition.Line}";
        }
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

    private static string GetKind(IMethodSymbol method, IReadOnlyDictionary<IMethodSymbol, string> entryPointKinds) =>
        entryPointKinds.GetValueOrDefault(method, "method");

    private static IReadOnlyList<HandlerRoute> GetRoutes(IMethodSymbol method,
                                                          IReadOnlyDictionary<IMethodSymbol, HandlerRoute[]> entryPointRoutes) =>
        entryPointRoutes.GetValueOrDefault(method, []);

    private static void AddUnresolved(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation, string reason)
    {
        var evidence = EntryPointSymbols.CreateEvidence(invocation);
        graph.AddUnresolved(UnresolvedKind.Call, handler, evidence, invocation.ToString(), reason);
    }
}
