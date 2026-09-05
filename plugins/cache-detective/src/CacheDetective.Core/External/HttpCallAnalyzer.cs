using CacheDetective.Caching;
using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CacheDetective.External;

internal sealed class HttpCallAnalyzer(Solution solution)
{
    private static readonly FoldedPlaceholders PLACEHOLDERS = new(name => $"{{{name}}}", "{?}");
    private readonly StringConstantFolder _folder = new(solution, PLACEHOLDERS);
    private Dictionary<INamedTypeSymbol, string>? _typedClients;

    public async Task<bool> TryAnalyzeAsync(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation,
                                            SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
            return false;

        var grpc = TryGetGrpc(operation.TargetMethod, operation.Instance?.Type as INamedTypeSymbol);
        if (grpc is not null)
        {
            AddRead(graph, handler, "grpc", "*", $"{grpc.Value.Service}/{grpc.Value.Method}", grpc.Value.Service,
                    CreateEvidence(invocation));
            return true;
        }

        var refit = TryGetRefit(operation.TargetMethod);
        if (refit is not null)
        {
            AddRead(graph, handler, "http", refit.Value.Method, PathTemplates.Normalize(refit.Value.Template),
                    operation.TargetMethod.ContainingType.Name, CreateEvidence(invocation));
            return true;
        }

        var method = GetHttpMethod(operation.TargetMethod, operation.Instance?.Type as INamedTypeSymbol);
        if (method is null)
            return false;

        var offset = operation.TargetMethod.IsExtensionMethod && operation.TargetMethod.ReducedFrom is null ? 1 : 0;
        if (method == "SendAsync")
        {
            var request = GetArgument(operation, 0, offset);
            var details = TryGetRequest(request, semanticModel);
            await AddHttpReadAsync(graph, handler, invocation, semanticModel, details.Method, details.Url, cancellationToken);
            return true;
        }

        var url = GetArgument(operation, 0, offset);
        await AddHttpReadAsync(graph, handler, invocation, semanticModel, method, url, cancellationToken);
        return true;
    }

    private async Task AddHttpReadAsync(CacheGraph graph, Handler handler, InvocationExpressionSyntax invocation,
                                        SemanticModel semanticModel, string method, ExpressionSyntax? url,
                                        CancellationToken cancellationToken)
    {
        var expression = GetUriExpression(url, semanticModel);
        var folded = expression is null ? null : await _folder.FoldAsync(expression, semanticModel, cancellationToken);
        var template = folded is null ? "{?}" : PathTemplates.Normalize(ExternalTemplate(folded));
        if (string.IsNullOrEmpty(template)) template = "{?}";
        var source = AddRead(graph, handler, "http", method, template,
                             await FindClientNameAsync(invocation, semanticModel, cancellationToken), CreateEvidence(invocation));
        if (folded is null || !folded.HasLiteralPart)
        {
            graph.AddUnresolvedExternal(UnresolvedKind.Call, handler, CreateEvidence(invocation), invocation.ToString(),
                                        "HTTP URL has no literal segment: name the endpoint it reaches.", source);
        }
    }

    private static ExternalSource AddRead(CacheGraph graph, Handler handler, string kind, string method, string template,
                                          string? clientName, Evidence evidence)
    {
        var source = new ExternalSource(kind, method, template, clientName, handler.ServiceId());
        graph.AddEdge(new Reads(handler, source, Confidence.Confirmed, [evidence]));
        return source;
    }

    private static string ExternalTemplate(FoldedString folded) => folded.Value;

    private static string? GetHttpMethod(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        if (GetFullName(method.ContainingType) == "System.Net.Http.Json.HttpClientJsonExtensions")
        {
            return method.Name switch
            {
                "GetFromJsonAsync" => "GET",
                "PostAsJsonAsync" => "POST",
                "PutAsJsonAsync" => "PUT",
                "DeleteFromJsonAsync" => "DELETE",
                "PatchAsJsonAsync" => "PATCH",
                _ => null
            };
        }

        return GetApiTypes(method, instanceType).Any(type => GetFullName(type) == "System.Net.Http.HttpClient") ? method.Name switch
        {
            "GetAsync" or "GetStringAsync" or "GetStreamAsync" or "GetByteArrayAsync" => "GET",
            "PostAsync" => "POST",
            "PutAsync" => "PUT",
            "DeleteAsync" => "DELETE",
            "PatchAsync" => "PATCH",
            "SendAsync" => "SendAsync",
            _ => null
        } : null;
    }

    private static (string Method, string Template)? TryGetRefit(IMethodSymbol method)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface)
            return null;

        var attribute = method.GetAttributes().FirstOrDefault(candidate => candidate.AttributeClass?.ContainingNamespace.ToDisplayString() == "Refit" &&
            candidate.AttributeClass.Name is "GetAttribute" or "PostAttribute" or "PutAttribute" or "DeleteAttribute" or "PatchAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is not string path)
            return null;
        return (attribute.AttributeClass!.Name[..^"Attribute".Length].ToUpperInvariant(), path);
    }

    private static (string Service, string Method)? TryGetGrpc(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        var type = instanceType;
        if (type is null || !DerivesFromGrpcClient(type))
            return null;
        var service = type.ContainingType?.Name;
        if (service is null)
            return null;
        var methodName = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name;
        return (service, methodName);
    }

    private static bool DerivesFromGrpcClient(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (GetFullName(current.OriginalDefinition).StartsWith("Grpc.Core.ClientBase", StringComparison.Ordinal)) return true;
        return false;
    }

    private static (string Method, ExpressionSyntax? Url) TryGetRequest(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        expression = ResolveInitializer(expression, semanticModel);
        if (expression is not ObjectCreationExpressionSyntax creation ||
            semanticModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor ||
            GetFullName(constructor.ContainingType) != "System.Net.Http.HttpRequestMessage")
            return ("*", null);

        var arguments = creation.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count < 2)
            return ("*", null);
        var method = semanticModel.GetSymbolInfo(arguments.Value[0].Expression).Symbol as IPropertySymbol;
        return (method?.ContainingType is { } type && GetFullName(type) == "System.Net.Http.HttpMethod" ? method.Name.ToUpperInvariant() : "*",
                arguments.Value[1].Expression);
    }

    private static ExpressionSyntax? GetUriExpression(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        if (expression is null) return null;
        var type = semanticModel.GetTypeInfo(expression).Type;
        if (GetFullName(type) != "System.Uri") return expression;
        expression = ResolveInitializer(expression, semanticModel);
        return expression is ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } creation
            ? creation.ArgumentList.Arguments[0].Expression : null;
    }

    private static ExpressionSyntax? ResolveInitializer(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        if (expression is null) return null;
        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        return symbol?.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                     .OfType<VariableDeclaratorSyntax>().Select(declarator => declarator.Initializer?.Value)
                     .FirstOrDefault(value => value is not null) ?? expression;
    }

    private async Task<string?> FindClientNameAsync(InvocationExpressionSyntax invocation, SemanticModel semanticModel,
                                                    CancellationToken cancellationToken)
    {
        var enclosing = semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) as IMethodSymbol;
        if (enclosing?.ContainingType is { } type && (await TypedClientsAsync(cancellationToken)).TryGetValue(type.OriginalDefinition, out var typed))
            return typed;
        return FindNamedClient(invocation, semanticModel);
    }

    private async Task<Dictionary<INamedTypeSymbol, string>> TypedClientsAsync(CancellationToken cancellationToken)
    {
        if (_typedClients is not null) return _typedClients;
        var result = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null) continue;
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol { Name: "AddHttpClient" } method ||
                    method.TypeArguments.Length is not (1 or 2)) continue;
                var implementation = (method.TypeArguments.Length == 2 ? method.TypeArguments[1] : method.TypeArguments[0]) as INamedTypeSymbol;
                if (implementation is not null) result[implementation.OriginalDefinition] = method.TypeArguments[0].Name;
            }
        }

        return _typedClients = result;
    }

    private static string? FindNamedClient(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access)
            return null;
        var symbol = semanticModel.GetSymbolInfo(access.Expression).Symbol;
        var initializer = symbol?.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                               .OfType<VariableDeclaratorSyntax>().Select(declarator => declarator.Initializer?.Value)
                               .FirstOrDefault(value => value is not null);
        if (initializer is null && symbol is IFieldSymbol field)
        {
            initializer = field.ContainingType.InstanceConstructors.SelectMany(constructor => constructor.DeclaringSyntaxReferences)
                               .SelectMany(reference => reference.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                               .FirstOrDefault(assignment => SymbolEqualityComparer.Default.Equals(
                                   semanticModel.Compilation.GetSemanticModel(assignment.SyntaxTree).GetSymbolInfo(assignment.Left).Symbol, field))?.Right;
        }
        if (initializer is not InvocationExpressionSyntax creation ||
            semanticModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol method || method.Name != "CreateClient")
            return null;
        return creation.ArgumentList.Arguments.FirstOrDefault() is { Expression: var name } &&
               semanticModel.GetConstantValue(name).Value is string value ? value : null;
    }

    private static ExpressionSyntax? GetArgument(IInvocationOperation operation, int index, int offset)
    {
        var argument = operation.Arguments.FirstOrDefault(candidate => !candidate.IsImplicit && candidate.Parameter?.Ordinal == index + offset);
        return argument?.Syntax is ArgumentSyntax syntax ? syntax.Expression : argument?.Value.Syntax as ExpressionSyntax;
    }

    private static IEnumerable<INamedTypeSymbol> GetApiTypes(IMethodSymbol method, INamedTypeSymbol? instanceType)
    {
        var direct = new List<INamedTypeSymbol>();
        if (instanceType is not null) direct.Add(instanceType);
        if (method.ReducedFrom?.Parameters.FirstOrDefault()?.Type is INamedTypeSymbol reduced) direct.Add(reduced);
        else if (method.IsExtensionMethod && method.Parameters.FirstOrDefault()?.Type is INamedTypeSymbol receiver) direct.Add(receiver);
        direct.Add(method.ContainingType);
        foreach (var type in direct)
        {
            yield return type.OriginalDefinition;
            foreach (var @interface in type.AllInterfaces) yield return @interface.OriginalDefinition;
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType) yield return baseType.OriginalDefinition;
        }
    }

    private static string GetFullName(ITypeSymbol? type) => type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                                               .Replace("global::", "", StringComparison.Ordinal) ?? string.Empty;

    private static Evidence CreateEvidence(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return new Evidence(span.Path, span.StartLinePosition.Line + 1);
    }
}
