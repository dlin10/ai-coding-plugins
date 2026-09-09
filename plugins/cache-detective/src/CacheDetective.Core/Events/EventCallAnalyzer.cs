using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace CacheDetective.Events;

/// <summary>
/// A publish is attributed to the caller that named the event type, not to the body that physically
/// contains the call. A shared helper taking the event as a parameter — eShop's
/// <c>PublishThroughEventBusAsync</c> — publishes nothing of its own: it stays a link on the chain
/// through its <c>Calls</c> edge, exactly as a stored procedure is. See <c>docs/adr/0017</c>.
/// <para>Attribution is decided after the walk, not during it. The walk is still discovering handlers
/// while this analyzer runs, so a caller met later would look unreachable purely for being met later.
/// The triples are collected here and resolved in <see cref="Resolve"/>, where the handler is looked up
/// and never created: a chain head nothing reaches is a finding addressed to nobody.</para>
/// </summary>
internal sealed class EventCallAnalyzer(Solution solution, IReadOnlyList<EventRecognizer> recognizers)
{
    private const int MAXIMUM_RECOVERY_DEPTH = 5;

    private readonly HashSet<string> _unknownTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, bool> _hasDerivedTypes = new(SymbolEqualityComparer.Default);
    private readonly List<PendingPublish> _publishes = [];
    private readonly List<PendingFailure> _failures = [];

    /// <summary>One recovered publish, waiting for the walk to finish so its publisher can be looked up.</summary>
    private sealed record PendingPublish(IMethodSymbol Publisher, string EventType, Handler Site, Evidence Evidence,
                                         string Snippet, Confidence Confidence, int? AnnotationId);

    /// <summary>A caller branch that produced no type. A failure is a result too: without it a helper with
    /// one good caller and one exhausted branch would leave no trace of the second publisher at all.</summary>
    private sealed record PendingFailure(Handler Site, Evidence Evidence, string Snippet, string Reason);

    /// <summary>One method's contribution: the type it named, and the method that named it.</summary>
    private sealed record RecoveredType(IMethodSymbol Owner, INamedTypeSymbol Type);

    /// <summary>One place a helper is called from, with the stable key it is ordered by.</summary>
    private sealed record CallSite(IMethodSymbol? Containing, ExpressionSyntax Expression, SemanticModel SemanticModel,
                                   string Caller, string File, int Line, int Character);

    /// <summary>The one order everything order-sensitive in this analyzer uses: the caller's display
    /// string, then where the site is. Two runs of one solution must record the same edges and the same
    /// <see cref="Unresolved"/> ids in the same order, because an id is what an <c>annotate</c> binds
    /// to.</summary>
    private static IEnumerable<CallSite> Order(IEnumerable<CallSite> sites) =>
        sites.OrderBy(site => site.Caller, StringComparer.Ordinal)
             .ThenBy(site => site.File, StringComparer.Ordinal)
             .ThenBy(site => site.Line)
             .ThenBy(site => site.Character);

    public async Task<bool> TryAnalyzeAsync(CacheGraph graph, Handler handler, IMethodSymbol containingMethod,
                                            InvocationExpressionSyntax invocation, SemanticModel semanticModel,
                                            CancellationToken cancellationToken)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
        {
            return false;
        }

        var recognizer = FindRecognizer(operation.TargetMethod, operation.Instance?.Type as INamedTypeSymbol);
        if (recognizer is null)
        {
            return RecordUnknownEventBus(graph, handler, invocation, operation.TargetMethod,
                                         operation.Instance?.Type as INamedTypeSymbol);
        }

        var argumentOffset = operation.TargetMethod.IsExtensionMethod && operation.TargetMethod.ReducedFrom is null ? 1 : 0;
        var eventExpression = GetArgumentExpression(operation, recognizer.EventArgumentIndex, argumentOffset);
        if (eventExpression is null)
        {
            return true;
        }

        var recovered = new Dictionary<string, RecoveredType>(StringComparer.Ordinal);
        var failures = new List<string>();
        var unnamed = new List<string>();
        await AddEventTypesAsync(eventExpression, semanticModel, containingMethod, 0, [], recovered, failures, unnamed, cancellationToken);

        var evidence = CreateEvidence(invocation);
        var snippet = invocation.ToString();
        // A site that named nothing at all keeps its own single row, recorded here at walk time as it
        // always has been: the branches that named nothing are exactly what that row is already saying.
        if (recovered.Count == 0 && failures.Count == 0)
        {
            var unresolved = graph.AddUnresolved(UnresolvedKind.Event, handler, evidence, snippet,
                                                 "Event type not statically known: name its events.");
            graph.MarkEventSite(unresolved.Id, EventSiteRole.Publish);
            return true;
        }

        // Edges and unresolved rows from one site is the correct outcome, not a contradiction: one caller
        // may name its event while another's recovery runs out.
        foreach (var value in recovered.Values)
        {
            _publishes.Add(new PendingPublish(value.Owner, GetFullName(value.Type), handler, evidence, snippet,
                                              recognizer.Confidence, recognizer.AnnotationId));
        }

        // Once anything was said about this site, a branch that named nothing is a result of its own and is
        // recorded beside the rest: one branch of a conditional resolving does not excuse the other.
        foreach (var failure in failures.Concat(unnamed))
        {
            _failures.Add(new PendingFailure(handler, evidence, snippet, failure));
        }

        return true;
    }

    /// <summary>
    /// Turns what the walk recovered into edges, now that every handler the walk reached exists. A
    /// publisher the walk never reached from an entry point is recorded as unresolved rather than given a
    /// vertex of its own: creating one would put a chain head into the graph that nothing reaches.
    /// </summary>
    public void Resolve(CacheGraph graph, IReadOnlyDictionary<IMethodSymbol, Handler> walked)
    {
        // Collected in walk order, resolved in a stable one, so which handler an edge hangs on and which
        // id a row gets do not depend on when the walk met each site.
        foreach (var pending in _publishes.OrderBy(item => Describe(item.Publisher), StringComparer.Ordinal)
                                          .ThenBy(item => item.Evidence.File, StringComparer.Ordinal)
                                          .ThenBy(item => item.Evidence.Line)
                                          .ThenBy(item => item.EventType, StringComparer.Ordinal))
        {
            if (walked.TryGetValue(pending.Publisher, out var publisher))
            {
                graph.AddEdge(new Publishes(publisher, new Event(pending.EventType), pending.Confidence, [pending.Evidence])
                {
                    AnnotationId = pending.AnnotationId
                });
                continue;
            }

            AddEventRow(graph, pending.Site, pending.Evidence, pending.Snippet,
                        $"{Describe(pending.Publisher)} publishes {pending.EventType} here, and no entry point reaches it: " +
                        "name the handler that calls it.");
        }

        foreach (var failure in _failures.OrderBy(item => item.Reason, StringComparer.Ordinal)
                                         .ThenBy(item => item.Evidence.File, StringComparer.Ordinal)
                                         .ThenBy(item => item.Evidence.Line))
        {
            AddEventRow(graph, failure.Site, failure.Evidence, failure.Snippet, failure.Reason);
        }
    }

    private static void AddEventRow(CacheGraph graph, Handler site, Evidence evidence, string snippet, string reason)
    {
        var unresolved = graph.AddUnresolved(UnresolvedKind.Event, site, evidence, snippet, reason);
        graph.MarkEventSite(unresolved.Id, EventSiteRole.Publish);
    }

    private EventRecognizer? FindRecognizer(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        foreach (var type in GetApiTypes(method, instanceType))
        {
            var typeName = GetFullName(type);
            var recognizer = recognizers.FirstOrDefault(candidate => candidate.PublisherTypeNames.Contains(typeName, StringComparer.Ordinal) &&
                                                                        candidate.PublishMethods.Contains(method.Name, StringComparer.Ordinal));
            if (recognizer is not null)
            {
                return recognizer;
            }
        }

        return null;
    }

    private bool RecordUnknownEventBus(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation,
                                       IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        if (!method.Name.StartsWith("Publish", StringComparison.Ordinal))
        {
            return false;
        }

        var type = GetApiTypes(method, instanceType).FirstOrDefault(candidate =>
            GetFullName(candidate).Contains("Bus", StringComparison.Ordinal) ||
            GetFullName(candidate).Contains("Publisher", StringComparison.Ordinal));
        if (type is null)
        {
            return false;
        }

        var typeName = GetFullName(type);
        if (_unknownTypes.Add($"{handler.Solution}:{typeName}"))
        {
            graph.AddUnresolved(UnresolvedKind.EventApi, handler, CreateEvidence(invocation), invocation.ToString(),
                                $"Unknown event bus type {typeName}.");
        }

        return true;
    }

    private async Task AddEventTypesAsync(ExpressionSyntax expression, SemanticModel semanticModel, IMethodSymbol containingMethod,
                                          int depth, HashSet<IMethodSymbol> activeMethods,
                                          Dictionary<string, RecoveredType> recovered, List<string> failures,
                                          List<string> unnamed, CancellationToken cancellationToken)
    {
        expression = Unwrap(expression);
        switch (expression)
        {
            case ObjectCreationExpressionSyntax:
                AddConcrete(semanticModel.GetTypeInfo(expression, cancellationToken).Type, containingMethod, recovered);
                return;
            case ConditionalExpressionSyntax conditional:
                await AddEventTypesAsync(conditional.WhenTrue, semanticModel, containingMethod, depth, activeMethods, recovered, failures, unnamed, cancellationToken);
                await AddEventTypesAsync(conditional.WhenFalse, semanticModel, containingMethod, depth, activeMethods, recovered, failures, unnamed, cancellationToken);
                return;
        }

        var symbol = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
        if (symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, containingMethod))
        {
            await RecoverAsync(containingMethod, parameter, depth, activeMethods, recovered, failures, unnamed, cancellationToken);
            return;
        }

        if (symbol is ILocalSymbol local)
        {
            var values = local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()).OfType<VariableDeclaratorSyntax>()
                              .Select(declarator => declarator.Initializer?.Value).Where(value => value is not null).Cast<ExpressionSyntax>()
                              .Concat(containingMethod.DeclaringSyntaxReferences.SelectMany(reference => reference.GetSyntax().DescendantNodes()
                                  .OfType<AssignmentExpressionSyntax>().Where(assignment => assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression) &&
                                      SymbolEqualityComparer.Default.Equals(semanticModel.Compilation.GetSemanticModel(assignment.Left.SyntaxTree)
                                          .GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local)).Select(assignment => assignment.Right)))
                              .ToArray();
            foreach (var value in values)
            {
                var model = value.SyntaxTree == semanticModel.SyntaxTree ? semanticModel : semanticModel.Compilation.GetSemanticModel(value.SyntaxTree);
                await AddEventTypesAsync(value, model, containingMethod, depth, activeMethods, recovered, failures, unnamed, cancellationToken);
            }
            return;
        }

        var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
        if (IsConcrete(type) && !await HasDerivedTypesAsync((INamedTypeSymbol)type!, cancellationToken))
        {
            AddConcrete(type, containingMethod, recovered);
            return;
        }

        // A branch that names nothing is a result whether or not a sibling branch succeeded. A recovered
        // branch says so directly; the publish site itself says so through <paramref name="unnamed"/>,
        // which the caller reports only once something else was said about the site — because a site that
        // named nothing at all already has its own "event type not statically known" row, and saying it
        // twice would move every such row.
        (depth > 0 ? failures : unnamed)
            .Add($"{Describe(containingMethod)} passes an expression that names no concrete event type: name its events.");
    }

    private async Task RecoverAsync(IMethodSymbol method, IParameterSymbol parameter, int depth,
                                    HashSet<IMethodSymbol> activeMethods,
                                    Dictionary<string, RecoveredType> recovered, List<string> failures,
                                    List<string> unnamed, CancellationToken cancellationToken)
    {
        if (depth >= MAXIMUM_RECOVERY_DEPTH)
        {
            failures.Add($"{Describe(method)} takes its event as a parameter, and the search for the caller that names it " +
                         $"reached its limit of {MAXIMUM_RECOVERY_DEPTH} hops.");
            return;
        }

        if (!activeMethods.Add(method))
        {
            failures.Add($"{Describe(method)} takes its event as a parameter, and the call chain that would name it is a cycle.");
            return;
        }

        var callerGroups = await Task.WhenAll(GetRelatedMethods(method)
            .Select(candidate => SymbolFinder.FindCallersAsync(candidate, solution, cancellationToken: cancellationToken)));
        var documents = callerGroups.SelectMany(callers => callers).SelectMany(caller => caller.Locations)
                               .Select(location => solution.GetDocument(location.SourceTree))
                               .OfType<Document>()
                               .Distinct()
                               .ToArray();
        if (documents.Length == 0)
        {
            documents = solution.Projects.SelectMany(project => project.Documents).ToArray();
        }

        // SymbolFinder does not specify the order it returns callers in, and the order decides which
        // handler an edge hangs on and in what order rows are recorded — and an Unresolved id is what an
        // annotate binds to. Every call site is collected first and walked in a stable order. See
        // docs/adr/0014 for why the counts must be a function of the solution alone.
        var sites = new List<CallSite>();
        foreach (var document in documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null)
            {
                continue;
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation ||
                    !GetRelatedMethods(method).Any(candidate => SymbolEqualityComparer.Default.Equals(
                        operation.TargetMethod.OriginalDefinition, candidate.OriginalDefinition)))
                {
                    continue;
                }

                var argument = operation.Arguments.FirstOrDefault(candidate => !candidate.IsImplicit &&
                                                                               candidate.Parameter?.Ordinal == parameter.Ordinal);
                if (argument?.Value.Syntax is not ExpressionSyntax expression)
                {
                    continue;
                }

                var containing = semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) as IMethodSymbol;
                var span = invocation.GetLocation().GetLineSpan();
                sites.Add(new CallSite(containing, expression, semanticModel,
                                       containing is null ? string.Empty : Describe(containing), span.Path,
                                       span.StartLinePosition.Line, span.StartLinePosition.Character));
            }
        }

        var callers = sites.Count;
        foreach (var site in Order(sites))
        {
            if (site.Containing is null)
            {
                failures.Add($"{Describe(method)} is called from a place with no enclosing method, so the caller that names " +
                             "its event could not be identified.");
                continue;
            }

            await AddEventTypesAsync(site.Expression, site.SemanticModel, site.Containing, depth + 1,
                                     new HashSet<IMethodSymbol>(activeMethods, SymbolEqualityComparer.Default), recovered, failures,
                                     unnamed, cancellationToken);
        }

        if (callers == 0)
            failures.Add($"{Describe(method)} takes its event as a parameter, and no caller in this solution passes one: " +
                         "name its events.");
    }

    private async Task<bool> HasDerivedTypesAsync(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (_hasDerivedTypes.TryGetValue(type.OriginalDefinition, out var hasDerived))
            return hasDerived;
        hasDerived = (await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken)).Any();
        _hasDerivedTypes[type.OriginalDefinition] = hasDerived;
        return hasDerived;
    }

    /// <summary>Keyed by owner and type together, so two callers naming the same event stay two publishes
    /// and one caller naming two events stays one caller.</summary>
    private static void AddConcrete(ITypeSymbol? type, IMethodSymbol owner, Dictionary<string, RecoveredType> recovered)
    {
        if (IsConcrete(type))
            recovered.TryAdd($"{Describe(owner)}{GetFullName(type!)}", new RecoveredType(owner, (INamedTypeSymbol)type!));
    }

    /// <summary>How a method is named in a reason, and the same string a <see cref="Handler"/> carries as
    /// its symbol, so a reader can match the row to the handler it names.</summary>
    private static string Describe(IMethodSymbol method) =>
        method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static IEnumerable<IMethodSymbol> GetRelatedMethods(IMethodSymbol method)
    {
        var methods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        for (var current = method; current is not null; current = current.OverriddenMethod)
        {
            methods.Add(current.OriginalDefinition);
            foreach (var implementation in current.ExplicitInterfaceImplementations)
                methods.Add(implementation.OriginalDefinition);
        }

        foreach (var @interface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(member);
                if (implementation is IMethodSymbol implementationMethod &&
                    SymbolEqualityComparer.Default.Equals(implementationMethod.OriginalDefinition, method.OriginalDefinition))
                {
                    methods.Add(member.OriginalDefinition);
                }
            }
        }

        return methods;
    }

    private static IEnumerable<INamedTypeSymbol> GetApiTypes(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        var directTypes = new List<INamedTypeSymbol>();
        if (instanceType is not null)
        {
            directTypes.Add(instanceType);
        }

        if (method.ReducedFrom?.Parameters.FirstOrDefault()?.Type is INamedTypeSymbol reducedReceiver)
        {
            directTypes.Add(reducedReceiver);
        }
        else if (method.IsExtensionMethod && method.Parameters.FirstOrDefault()?.Type is INamedTypeSymbol receiver)
        {
            directTypes.Add(receiver);
        }

        directTypes.Add(method.ContainingType);
        foreach (var directType in directTypes)
        {
            yield return directType.OriginalDefinition;
            foreach (var interfaceType in directType.AllInterfaces)
            {
                yield return interfaceType.OriginalDefinition;
            }

            for (var baseType = directType.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                yield return baseType.OriginalDefinition;
            }
        }
    }

    private static ExpressionSyntax? GetArgumentExpression(IInvocationOperation operation, int index, int offset)
    {
        var argument = operation.Arguments.FirstOrDefault(candidate => !candidate.IsImplicit && candidate.Parameter?.Ordinal == index + offset);
        return argument?.Syntax is ArgumentSyntax argumentSyntax ? argumentSyntax.Expression : argument?.Value.Syntax as ExpressionSyntax;
    }

    private static bool IsConcrete(ITypeSymbol? type) => type is INamedTypeSymbol { IsAbstract: false } named &&
                                                         named.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum &&
                                                         named.SpecialType != SpecialType.System_Object;

    private static string GetFullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
        CastExpressionSyntax cast => Unwrap(cast.Expression),
        BinaryExpressionSyntax @as when @as.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AsExpression) => Unwrap(@as.Left),
        _ => expression
    };

    private static Evidence CreateEvidence(SyntaxNode syntax)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }
}
