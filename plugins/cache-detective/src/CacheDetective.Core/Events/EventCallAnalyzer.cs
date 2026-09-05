using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace CacheDetective.Events;

internal sealed class EventCallAnalyzer(Solution solution, IReadOnlyList<EventRecognizer> recognizers)
{
    private const int MAXIMUM_RECOVERY_DEPTH = 5;

    private readonly HashSet<string> _unknownTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, bool> _hasDerivedTypes = new(SymbolEqualityComparer.Default);

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

        var types = await EventTypesAsync(eventExpression, semanticModel, containingMethod, 0, [], cancellationToken);

        if (types.Count == 0)
        {
            var unresolved = graph.AddUnresolved(UnresolvedKind.Event, handler, CreateEvidence(invocation), invocation.ToString(),
                                                 "Event type not statically known: name its events.");
            graph.MarkEventSite(unresolved.Id, EventSiteRole.Publish);
            return true;
        }

        foreach (var type in types)
        {
            graph.AddEdge(new Publishes(handler, new Event(GetFullName(type)), recognizer.Confidence, [CreateEvidence(invocation)])
            {
                AnnotationId = recognizer.AnnotationId
            });
        }

        return true;
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

    private async Task<IReadOnlyList<INamedTypeSymbol>> EventTypesAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                                                          IMethodSymbol containingMethod, int depth,
                                                                          HashSet<IMethodSymbol> activeMethods,
                                                                          CancellationToken cancellationToken)
    {
        var recovered = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        await AddEventTypesAsync(expression, semanticModel, containingMethod, depth, activeMethods, recovered, cancellationToken);
        return recovered.Values.ToArray();
    }

    private async Task AddEventTypesAsync(ExpressionSyntax expression, SemanticModel semanticModel, IMethodSymbol containingMethod,
                                          int depth, HashSet<IMethodSymbol> activeMethods,
                                          Dictionary<string, INamedTypeSymbol> recovered, CancellationToken cancellationToken)
    {
        expression = Unwrap(expression);
        switch (expression)
        {
            case ObjectCreationExpressionSyntax:
                AddConcrete(semanticModel.GetTypeInfo(expression, cancellationToken).Type, recovered);
                return;
            case ConditionalExpressionSyntax conditional:
                await AddEventTypesAsync(conditional.WhenTrue, semanticModel, containingMethod, depth, activeMethods, recovered, cancellationToken);
                await AddEventTypesAsync(conditional.WhenFalse, semanticModel, containingMethod, depth, activeMethods, recovered, cancellationToken);
                return;
        }

        var symbol = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
        if (symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, containingMethod))
        {
            await RecoverAsync(containingMethod, parameter, depth, activeMethods, recovered, cancellationToken);
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
                await AddEventTypesAsync(value, model, containingMethod, depth, activeMethods, recovered, cancellationToken);
            }
            return;
        }

        var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
        if (IsConcrete(type) && !await HasDerivedTypesAsync((INamedTypeSymbol)type!, cancellationToken))
            AddConcrete(type, recovered);
    }

    private async Task RecoverAsync(IMethodSymbol method, IParameterSymbol parameter, int depth,
                                    HashSet<IMethodSymbol> activeMethods,
                                    Dictionary<string, INamedTypeSymbol> recovered,
                                    CancellationToken cancellationToken)
    {
        if (depth >= MAXIMUM_RECOVERY_DEPTH || !activeMethods.Add(method))
        {
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
                if (containing is not null)
                    await AddEventTypesAsync(expression, semanticModel, containing, depth + 1,
                                             new HashSet<IMethodSymbol>(activeMethods, SymbolEqualityComparer.Default), recovered,
                                             cancellationToken);
            }
        }
    }

    private async Task<bool> HasDerivedTypesAsync(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (_hasDerivedTypes.TryGetValue(type.OriginalDefinition, out var hasDerived))
            return hasDerived;
        hasDerived = (await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken)).Any();
        _hasDerivedTypes[type.OriginalDefinition] = hasDerived;
        return hasDerived;
    }

    private static void AddConcrete(ITypeSymbol? type, Dictionary<string, INamedTypeSymbol> recovered)
    {
        if (IsConcrete(type))
            recovered.TryAdd(GetFullName(type!), (INamedTypeSymbol)type!);
    }

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

    private static IParameterSymbol? FindParameter(ExpressionSyntax expression, SemanticModel semanticModel, IMethodSymbol containingMethod)
    {
        var symbol = semanticModel.GetSymbolInfo(Unwrap(expression)).Symbol;
        if (symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, containingMethod))
        {
            return parameter;
        }

        if (symbol is not ILocalSymbol local)
        {
            return null;
        }

        var initializer = local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                               .OfType<VariableDeclaratorSyntax>()
                               .Select(declarator => declarator.Initializer?.Value)
                               .FirstOrDefault(value => value is not null);
        if (initializer is null)
        {
            return null;
        }

        var initializerModel = initializer.SyntaxTree == expression.SyntaxTree
                                   ? semanticModel
                                   : semanticModel.Compilation.GetSemanticModel(initializer.SyntaxTree);
        return FindParameter(initializer, initializerModel, containingMethod);
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
