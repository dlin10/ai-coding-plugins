using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Data;

internal sealed class EfReadAnalyzer
{
    private const string DEFAULT_DATABASE = "default";
    private const string DEFAULT_SCHEMA = "dbo";
    private readonly Solution _solution;
    private IReadOnlyDictionary<string, TableMapping>? _mappings;

    public EfReadAnalyzer(Solution solution)
    {
        _solution = solution;
    }

    public async Task AnalyzeAsync(CacheGraph graph, Handler handler, IMethodSymbol method, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var recordedSites = new HashSet<(SyntaxTree Tree, int Start, string Entity)>();

        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
            var document = _solution.GetDocument(declaration.SyntaxTree);
            var semanticModel = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                continue;

            var nodes = declaration.DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

            var syntaxNodes = nodes as SyntaxNode[] ?? nodes.ToArray();
            foreach (var name in syntaxNodes.OfType<SimpleNameSyntax>())
            {
                if (semanticModel.GetSymbolInfo(name, cancellationToken).Symbol is not IPropertySymbol property ||
                    !TryGetDbSetEntity(property.Type, out var entity))
                {
                    continue;
                }

                AddRead(graph, handler, name, entity, recordedSites);
            }

            foreach (var invocation in syntaxNodes.OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called || called.Name != "Set" ||
                    called.Arity != 1 || !TryGetDbSetEntity(called.ReturnType, out var entity))
                {
                    continue;
                }

                AddRead(graph, handler, invocation, entity, recordedSites);
            }
        }
    }

    private void AddRead(CacheGraph graph, Handler handler, SyntaxNode site, INamedTypeSymbol entity,
                         ISet<(SyntaxTree Tree, int Start, string Entity)> recordedSites)
    {
        var entityId = GetEntityId(entity);
        if (!recordedSites.Add((site.SyntaxTree, site.SpanStart, entityId)))
            return;

        var lineSpan = site.GetLocation().GetLineSpan();
        var table = ResolveTable(entity);
        graph.AddEdge(new Reads(handler, table, Confidence.Confirmed,
            [new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1)]));
    }

    internal async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        _mappings ??= await BuildMappingsAsync(cancellationToken);
    }

    internal Table ResolveTable(INamedTypeSymbol entity)
    {
        var mapping = _mappings!.TryGetValue(GetEntityId(entity), out var configured)
            ? configured
            : new TableMapping(DEFAULT_SCHEMA, entity.Name);
        return new Table(mapping.Schema, mapping.Name, DEFAULT_DATABASE);
    }

    internal bool HasKnownTable(INamedTypeSymbol entity) =>
        _mappings!.ContainsKey(GetEntityId(entity));

    private async Task<IReadOnlyDictionary<string, TableMapping>> BuildMappingsAsync(
        CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, TableMapping>(StringComparer.Ordinal);
        var fluent = new Dictionary<string, TableMapping>(StringComparer.Ordinal);
        var conventions = new Dictionary<string, TableMapping>(StringComparer.Ordinal);

        foreach (var project in _solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;

            foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
            {
                var entityId = GetEntityId(type);
                var tableAttribute = type.GetAttributes().FirstOrDefault(attribute =>
                    attribute.AttributeClass is { Name: "TableAttribute", Arity: 0 });
                if (tableAttribute is not null &&
                    tableAttribute.ConstructorArguments.FirstOrDefault().Value is string tableName)
                {
                    var schema = tableAttribute.NamedArguments.FirstOrDefault(argument =>
                        argument.Key == "Schema").Value.Value as string ?? DEFAULT_SCHEMA;
                    attributes.TryAdd(entityId, new TableMapping(schema, tableName));
                }

                foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (TryGetDbSetEntity(property.Type, out var entity))
                        conventions.TryAdd(GetEntityId(entity), new TableMapping(DEFAULT_SCHEMA, property.Name));
                }
            }

            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || semanticModel is null)
                    continue;

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol
                        { Name: "ToTable" })
                    {
                        continue;
                    }

                    var entity = GetConfiguredEntity(invocation, semanticModel, cancellationToken);
                    if (entity is null || !TryGetTableArguments(invocation, semanticModel,
                                                                cancellationToken, out var mapping))
                    {
                        continue;
                    }

                    fluent.TryAdd(GetEntityId(entity), mapping);
                }
            }
        }

        var result = new Dictionary<string, TableMapping>(conventions, StringComparer.Ordinal);
        foreach (var mapping in fluent)
            result[mapping.Key] = mapping.Value;
        foreach (var mapping in attributes)
            result[mapping.Key] = mapping.Value;
        return result;
    }

    private static INamedTypeSymbol? GetConfiguredEntity(InvocationExpressionSyntax invocation,
                                                           SemanticModel semanticModel,
                                                           CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type is INamedTypeSymbol receiver &&
            receiver.Name == "EntityTypeBuilder" && receiver.Arity == 1)
        {
            return receiver.TypeArguments[0] as INamedTypeSymbol;
        }

        return semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) is IMethodSymbol method &&
               method.ContainingType.AllInterfaces.FirstOrDefault(candidate =>
                   candidate.Name == "IEntityTypeConfiguration" && candidate.Arity == 1) is { } configuration
            ? configuration.TypeArguments[0] as INamedTypeSymbol
            : null;
    }

    private static bool TryGetTableArguments(InvocationExpressionSyntax invocation,
                                              SemanticModel semanticModel,
                                              CancellationToken cancellationToken,
                                              out TableMapping mapping)
    {
        mapping = default!;
        string? tableName = null;
        string? schema = null;

        for (var index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            var argument = invocation.ArgumentList.Arguments[index];
            var constant = semanticModel.GetConstantValue(argument.Expression, cancellationToken);
            if (!constant.HasValue || constant.Value is not string value)
                continue;

            var parameterName = argument.NameColon?.Name.Identifier.ValueText;
            if (parameterName is "schema")
                schema = value;
            else if (tableName is null && (index == 0 || parameterName is "name" or "table"))
                tableName = value;
            else if (schema is null)
                schema = value;
        }

        if (tableName is null)
            return false;

        mapping = new TableMapping(schema ?? DEFAULT_SCHEMA, tableName);
        return true;
    }

    private static bool TryGetDbSetEntity(ITypeSymbol? type, out INamedTypeSymbol entity)
    {
        if (type is INamedTypeSymbol { Name: "DbSet", Arity: 1 } dbSet &&
            dbSet.TypeArguments[0] is INamedTypeSymbol entityType)
        {
            entity = entityType;
            return true;
        }

        entity = null!;
        return false;
    }

    private static string GetEntityId(INamedTypeSymbol entity) =>
        entity.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var memberNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetSourceTypes(memberNamespace))
                yield return type;
        }

        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var sourceType in GetSourceTypes(type))
                yield return sourceType;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamedTypeSymbol type)
    {
        if (type.Locations.Any(location => location.IsInSource))
            yield return type;

        foreach (var nestedType in type.GetTypeMembers())
        {
            foreach (var sourceType in GetSourceTypes(nestedType))
                yield return sourceType;
        }
    }

    private sealed record TableMapping(string Schema, string Name);
}
