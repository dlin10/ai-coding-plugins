using System.Globalization;
using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CacheDetective.Caching;

internal sealed class CacheCallAnalyzer
{
    private readonly IReadOnlyList<CacheRecognizer> _recognizers;
    private readonly HashSet<string> _unknownTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsupportedAttributes = new(StringComparer.Ordinal);
    private readonly KeyTemplateFolder _keyTemplateFolder;

    public CacheCallAnalyzer(Solution solution, IReadOnlyList<CacheRecognizer> recognizers)
    {
        _recognizers = recognizers;
        _keyTemplateFolder = new KeyTemplateFolder(solution);
    }

    public void RecordUnsupportedAttributes(CacheGraph graph, Handler handler, IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attributeType = attribute.AttributeClass;
            if (attributeType is null || attributeType.Name is not ("OutputCacheAttribute" or "ResponseCacheAttribute"))
            {
                continue;
            }

            var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
            var location = syntax?.GetLocation() ?? method.Locations.First(location => location.IsInSource);
            var lineSpan = location.GetLineSpan();
            var identity = $"{handler.Solution}:{GetFullName(attributeType)}";
            if (!_unsupportedAttributes.Add(identity))
            {
                continue;
            }

            graph.AddUnresolved(UnresolvedKind.CacheApi, handler, lineSpan.Path, lineSpan.StartLinePosition.Line + 1,
                                syntax?.ToString() ?? attributeType.Name, $"{attributeType.Name} response caching is outside this analysis phase.");
        }
    }

    public async Task<bool> TryAnalyzeAsync(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation, SemanticModel semanticModel,
                                            CancellationToken cancellationToken)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
        {
            return false;
        }

        var method = operation.TargetMethod;
        var instanceType = operation.Instance?.Type as INamedTypeSymbol;
        var match = FindRecognizer(method, instanceType);
        if (match is null)
        {
            return RecordUnknownCacheType(graph, handler, invocation, method, instanceType);
        }

        var methodRecognizer = match.Value.Recognizer.Methods.FirstOrDefault(candidate => MethodNameMatches(candidate.Name, method.Name));
        if (methodRecognizer is null)
        {
            return true;
        }

        var keyExpression = GetArgumentExpression(operation, methodRecognizer.KeyArgumentIndex, match.Value.ArgumentOffset);
        if (keyExpression is null)
        {
            return true;
        }

        var foldedKey = await _keyTemplateFolder.FoldAsync(keyExpression, semanticModel, cancellationToken);
        var ttl = methodRecognizer.TtlOrOptionsArgumentIndex is { } ttlIndex
                      ? ExtractTtl(GetArgumentExpression(operation, ttlIndex, match.Value.ArgumentOffset), semanticModel)
                      : null;
        var tags = methodRecognizer.TagsArgumentIndex is { } tagsIndex
                       ? ExtractTags(GetArgumentExpression(operation, tagsIndex, match.Value.ArgumentOffset), semanticModel)
                       : [];
        var conditional = methodRecognizer.ConditionalSet is { } condition &&
                          (IsConstant(GetArgumentExpression(operation, condition.ArgumentIndex, match.Value.ArgumentOffset), condition.ConstantName,
                                      semanticModel) || IsConstant(GetArgumentExpression(operation, "when"), condition.ConstantName, semanticModel));
        var evidence = CreateEvidence(invocation);
        if (!foldedKey.HasLiteralSegment)
        {
            var keyEvidence = CreateEvidence(keyExpression);
            var unresolved = graph.AddUnresolved(UnresolvedKind.Key, handler, keyEvidence, keyExpression.ToString(),
                                                 foldedKey.Reason ?? "The key contains no literal segment.");
            graph.AddPendingCacheOperation(new PendingCacheOperation(unresolved.Id, handler, match.Value.Recognizer.Store,
                                                                      methodRecognizer.Semantic, ttl, tags, conditional, [evidence]));
            return true;
        }
        var key = new CacheKey(foldedKey.Template, match.Value.Recognizer.Store, ttl, tags, role: null);

        graph.AddHandler(handler);
        switch (methodRecognizer.Semantic)
        {
            case CacheSemantic.Get:
                graph.AddEdge(new Reads(handler, key, match.Value.Recognizer.Confidence, [evidence])
                {
                    AnnotationId = match.Value.Recognizer.AnnotationId
                });
                break;
            case CacheSemantic.Set:
                graph.AddEdge(new Caches(handler, key, match.Value.Recognizer.Confidence, [evidence], conditional)
                {
                    AnnotationId = match.Value.Recognizer.AnnotationId
                });
                break;
            case CacheSemantic.Remove:
            case CacheSemantic.RemoveByTag:
            case CacheSemantic.RemoveByPrefix:
                graph.AddEdge(new Invalidates(handler, key, match.Value.Recognizer.Confidence, [evidence], methodRecognizer.Semantic)
                {
                    AnnotationId = match.Value.Recognizer.AnnotationId
                });
                break;
            default:
                graph.AddCacheKeyObservation(handler.Solution, key);
                break;
        }

        graph.AddCacheOperation(new CacheOperation(handler, key, methodRecognizer.Semantic, conditional, [evidence]));
        return true;
    }

    private bool RecordUnknownCacheType(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation, IMethodSymbol method,
                                        INamedTypeSymbol? instanceType)
    {
        var type = new[] { instanceType, method.ContainingType }
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.OriginalDefinition)
            .FirstOrDefault(candidate => candidate.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase) &&
                                         !candidate.ContainingNamespace.ToDisplayString().Split('.').Contains("Internal", StringComparer.Ordinal));
        if (type is null)
        {
            return false;
        }

        var typeName = GetFullName(type);
        if (_unknownTypes.Add(typeName))
        {
            var evidence = CreateEvidence(invocation);
            graph.AddUnresolved(UnresolvedKind.CacheApi, handler, evidence, invocation.ToString(), $"Unknown cache API type {typeName}.");
        }

        return true;
    }

    private (CacheRecognizer Recognizer, int ArgumentOffset)? FindRecognizer(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        foreach (var type in GetApiTypes(method, instanceType))
        {
            var typeName = GetFullName(type);
            var recognizer = _recognizers.FirstOrDefault(candidate => candidate.TypeName == typeName);
            if (recognizer is not null)
            {
                var argumentOffset = method.IsExtensionMethod && method.ReducedFrom is null ? 1 : 0;
                return (recognizer, argumentOffset);
            }
        }

        return null;
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

    private static string GetFullName(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);

    private static bool MethodNameMatches(string recognizedName, string methodName) =>
        recognizedName.EndsWith('*') ? methodName.StartsWith(recognizedName[..^1], StringComparison.Ordinal) : methodName == recognizedName;

    private static ExpressionSyntax? GetArgumentExpression(IInvocationOperation operation, int index, int offset)
    {
        var argument = operation.Arguments.FirstOrDefault(candidate => !candidate.IsImplicit && candidate.Parameter?.Ordinal == index + offset);
        return argument?.Syntax is ArgumentSyntax argumentSyntax ? argumentSyntax.Expression : argument?.Value.Syntax as ExpressionSyntax;
    }

    private static ExpressionSyntax? GetArgumentExpression(IInvocationOperation operation, string parameterName)
    {
        var argument = operation.Arguments.FirstOrDefault(candidate => !candidate.IsImplicit && candidate.Parameter?.Name == parameterName);
        return argument?.Syntax is ArgumentSyntax argumentSyntax ? argumentSyntax.Expression : argument?.Value.Syntax as ExpressionSyntax;
    }

    private static TimeSpan? ExtractTtl(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        if (expression is null)
        {
            return null;
        }

        return TryExtractTimeSpan(expression, semanticModel, []) is { } ttl ? ttl : null;
    }

    private static TimeSpan? TryExtractTimeSpan(ExpressionSyntax expression, SemanticModel semanticModel, HashSet<SyntaxNode> visited)
    {
        if (!visited.Add(expression))
        {
            return null;
        }

        expression = Unwrap(expression);

        if (expression is MemberAccessExpressionSyntax memberAccess && memberAccess.ToString() == "TimeSpan.Zero")
        {
            return TimeSpan.Zero;
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            var calledMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (calledMethod?.ContainingType.Name == nameof(TimeSpan) && invocation.ArgumentList.Arguments.Count == 1 &&
                TryGetNumber(invocation.ArgumentList.Arguments[0].Expression, semanticModel, out var value))
            {
                return calledMethod.Name switch
                       {
                           "FromDays" => TimeSpan.FromDays(value),
                           "FromHours" => TimeSpan.FromHours(value),
                           "FromMinutes" => TimeSpan.FromMinutes(value),
                           "FromSeconds" => TimeSpan.FromSeconds(value),
                           "FromMilliseconds" => TimeSpan.FromMilliseconds(value),
                           "FromMicroseconds" => TimeSpan.FromMicroseconds(value),
                           _ => null
                       };
            }

            if (calledMethod?.Name is "SetAbsoluteExpiration" or "SetSlidingExpiration" &&
                invocation.ArgumentList.Arguments.LastOrDefault()?.Expression is { } ttlExpression)
            {
                return TryExtractTimeSpan(ttlExpression, semanticModel, visited);
            }
        }

        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            var initializedTtls = objectCreation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                                                .Where(assignment => assignment.Left.ToString() is "AbsoluteExpirationRelativeToNow" or "SlidingExpiration"
                                                                         or "Expiration" or "LocalCacheExpiration")
                                                .Select(assignment => TryExtractTimeSpan(assignment.Right, semanticModel, visited))
                                                .Where(ttl => ttl is not null)
                                                .Cast<TimeSpan>()
                                                .ToArray();
            if (initializedTtls is { Length: > 0 })
            {
                return initializedTtls.Max();
            }

            if (semanticModel.GetSymbolInfo(objectCreation).Symbol is IMethodSymbol constructor && constructor.ContainingType.Name == nameof(TimeSpan))
            {
                var values = objectCreation.ArgumentList?.Arguments
                                           .Select(argument => TryGetNumber(argument.Expression, semanticModel, out var number) ? (long?)number : null)
                                           .ToArray();
                if (values is not null && values.All(value => value is not null))
                {
                    return values.Length switch
                           {
                               1 => TimeSpan.FromTicks(values[0]!.Value),
                               3 => new TimeSpan((int)values[0]!.Value, (int)values[1]!.Value, (int)values[2]!.Value),
                               4 => new TimeSpan((int)values[0]!.Value, (int)values[1]!.Value, (int)values[2]!.Value, (int)values[3]!.Value),
                               5 => new TimeSpan((int)values[0]!.Value, (int)values[1]!.Value, (int)values[2]!.Value, (int)values[3]!.Value,
                                                 (int)values[4]!.Value),
                               _ => null
                           };
                }
            }
        }

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        var initializer = symbol?.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                                 .Select(GetInitializer)
                                 .FirstOrDefault(candidate => candidate is not null);
        return initializer is null ? null : TryExtractTimeSpan(initializer, GetSemanticModel(semanticModel, initializer), visited);
    }

    private static IReadOnlyList<string> ExtractTags(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        if (expression is null)
        {
            return [];
        }

        expression = Unwrap(expression);
        IEnumerable<ExpressionSyntax>? elements = expression switch
                                                  {
                                                      CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>()
                                                         .Select(element => element.Expression),
                                                      ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                                                      ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                                                      ObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                                                      _ => null
                                                  };
        if (elements is not null)
        {
            return elements.Select(element => semanticModel.GetConstantValue(element))
                           .Where(constant => constant.HasValue && constant.Value is string)
                           .Select(constant => (string)constant.Value!)
                           .ToArray();
        }

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        var initializerExpression = symbol?.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                                           .Select(GetInitializer)
                                           .FirstOrDefault(candidate => candidate is not null);
        return initializerExpression is null ? [] : ExtractTags(initializerExpression, GetSemanticModel(semanticModel, initializerExpression));
    }

    private static bool IsConstant(ExpressionSyntax? expression, string constantName, SemanticModel semanticModel)
    {
        if (expression is null)
        {
            return false;
        }

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        if (symbol is IFieldSymbol field)
        {
            var containingName = field.ContainingType.Name;
            return $"{containingName}.{field.Name}" == constantName;
        }

        return expression.ToString().EndsWith(constantName, StringComparison.Ordinal);
    }

    private static bool TryGetNumber(ExpressionSyntax expression, SemanticModel semanticModel, out double value)
    {
        var constant = semanticModel.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is not null)
        {
            try
            {
                value = Convert.ToDouble(constant.Value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        value = 0;
        return false;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            expression = expression switch
                         {
                             ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                             CastExpressionSyntax cast => cast.Expression,
                             _ => expression
                         };

            if (expression is not (ParenthesizedExpressionSyntax or CastExpressionSyntax))
            {
                return expression;
            }
        }
    }

    private static ExpressionSyntax? GetInitializer(SyntaxNode syntax) => syntax switch
                                                                          {
                                                                              VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
                                                                              PropertyDeclarationSyntax property => property.Initializer?.Value,
                                                                              _ => null
                                                                          };

    private static SemanticModel GetSemanticModel(SemanticModel current, SyntaxNode syntax) =>
        current.SyntaxTree == syntax.SyntaxTree ? current : current.Compilation.GetSemanticModel(syntax.SyntaxTree);

    private static Evidence CreateEvidence(SyntaxNode syntax)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }
}
