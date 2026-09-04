using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Data;

internal sealed class EfWriteAnalyzer
{
    private static readonly HashSet<string> DIRECT_MUTATION_METHODS =
    [
        "Add",
        "AddRange",
        "Update",
        "UpdateRange",
        "Remove",
        "RemoveRange"
    ];

    private static readonly HashSet<string> EXECUTE_MUTATION_METHODS =
    [
        "ExecuteUpdate",
        "ExecuteUpdateAsync",
        "ExecuteDelete",
        "ExecuteDeleteAsync"
    ];

    private static readonly WriteEvent[] EVERY_WRITE_EVENT =
        [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete];

    private readonly EfReadAnalyzer _tables;
    private readonly Dictionary<IMethodSymbol, MethodFacts> _facts = new(MethodSymbolComparer.Instance);
    private readonly Dictionary<IMethodSymbol, Handler> _handlers = new(MethodSymbolComparer.Instance);
    private readonly Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> _calls = new(MethodSymbolComparer.Instance);

    public EfWriteAnalyzer(EfReadAnalyzer tables)
    {
        _tables = tables;
    }

    public async Task AnalyzeAsync(Solution solution, Handler handler, IMethodSymbol method,
                                   CancellationToken cancellationToken)
    {
        var facts = new MethodFacts();

        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(declaration.SyntaxTree);
            var semanticModel = document is null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                continue;

            var nodes = declaration.DescendantNodes(node =>
                node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).ToArray();
            var invocations = nodes.OfType<InvocationExpressionSyntax>().ToArray();
            var assignments = nodes.OfType<AssignmentExpressionSyntax>().ToArray();
            var queries = FindQueries(nodes, invocations, semanticModel, cancellationToken);
            var savePositions = new List<int>();
            var handledInvocations = new HashSet<int>();

            foreach (var invocation in invocations)
            {
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                    continue;

                if (called.Name is "SaveChanges" or "SaveChangesAsync")
                {
                    facts.HasSaveChanges = true;
                    savePositions.Add(invocation.SpanStart);
                    handledInvocations.Add(invocation.SpanStart);
                    continue;
                }

                if (EXECUTE_MUTATION_METHODS.Contains(called.Name) &&
                    TryGetExecuteEntity(invocation, called, semanticModel, cancellationToken, out var executedEntity))
                {
                    facts.AddMutation(CreateMutation(executedEntity, invocation, Confidence.Confirmed,
                        WriteEventsFor(called.Name), requiresSaveChanges: false));
                    handledInvocations.Add(invocation.SpanStart);
                    continue;
                }

                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;
                if (TryGetDbSetEntity(receiverType, out var setEntity))
                {
                    if (DIRECT_MUTATION_METHODS.Contains(called.Name))
                    {
                        facts.AddMutation(CreateMutation(setEntity, invocation, Confidence.Confirmed,
                            WriteEventsFor(called.Name), requiresSaveChanges: true));
                        handledInvocations.Add(invocation.SpanStart);
                    }
                    else if (called.Name == "Attach" &&
                             HasLaterPropertyAssignment(setEntity, invocation.SpanStart, assignments,
                                                        semanticModel, cancellationToken))
                    {
                        facts.AddMutation(CreateMutation(setEntity, invocation, Confidence.Confirmed,
                            [WriteEvent.Update], requiresSaveChanges: true));
                        handledInvocations.Add(invocation.SpanStart);
                    }
                }

                if (called.Name == "Add" && memberAccess.Expression is MemberAccessExpressionSyntax navigation &&
                    semanticModel.GetSymbolInfo(navigation, cancellationToken).Symbol is IPropertySymbol &&
                    TryGetCollectionEntity(receiverType, out var childEntity) &&
                    _tables.HasKnownTable(childEntity))
                {
                    facts.AddMutation(CreateMutation(childEntity, invocation, Confidence.Confirmed,
                        WriteEventsFor(called.Name), requiresSaveChanges: true));
                    handledInvocations.Add(invocation.SpanStart);
                }
            }

            foreach (var assignment in assignments)
            {
                if (TryGetEntryStateEntity(assignment, semanticModel, cancellationToken, out var entryEntity))
                {
                    facts.AddMutation(CreateMutation(entryEntity, assignment, Confidence.Confirmed,
                        EVERY_WRITE_EVENT, requiresSaveChanges: true));
                    continue;
                }

                if (!TryGetAssignedObjectEntity(assignment, semanticModel, cancellationToken,
                                                out var assignedEntity))
                {
                    continue;
                }

                if (queries.Any(query => query.Position < assignment.SpanStart &&
                                         SameEntity(query.Entity, assignedEntity)))
                {
                    facts.AddMutation(CreateMutation(assignedEntity, assignment, Confidence.Confirmed,
                        [WriteEvent.Update], requiresSaveChanges: true));
                }
            }

            foreach (var query in queries)
            {
                var savePosition = savePositions.Where(position => position > query.Position)
                    .DefaultIfEmpty(-1)
                    .Min();
                if (savePosition < 0)
                    continue;

                var likelyMutation = invocations.FirstOrDefault(invocation =>
                    invocation.SpanStart > query.Position && invocation.SpanStart < savePosition &&
                    !handledInvocations.Contains(invocation.SpanStart) &&
                    IsInvocationOnOrWithEntity(invocation, query.Entity, semanticModel, cancellationToken));
                if (likelyMutation is not null)
                {
                    facts.AddMutation(CreateMutation(query.Entity, likelyMutation, Confidence.Likely,
                        EVERY_WRITE_EVENT, requiresSaveChanges: true));
                }
            }
        }

        _facts[method] = facts;
        _handlers[method] = handler;
    }

    public void RecordCall(IMethodSymbol from, IMethodSymbol to)
    {
        if (!_calls.TryGetValue(from, out var targets))
        {
            targets = new HashSet<IMethodSymbol>(MethodSymbolComparer.Instance);
            _calls.Add(from, targets);
        }

        targets.Add(to);
    }

    public async Task AddEdgesAsync(CacheGraph graph, CancellationToken cancellationToken)
    {
        await _tables.EnsureInitializedAsync(cancellationToken);
        var closure = _facts.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(),
            MethodSymbolComparer.Instance);

        bool changed;
        do
        {
            changed = false;
            foreach (var (method, targets) in _calls)
            {
                if (!closure.TryGetValue(method, out var methodFacts))
                    continue;

                foreach (var target in targets)
                {
                    if (closure.TryGetValue(target, out var targetFacts))
                        changed |= methodFacts.UnionWith(targetFacts);
                }
            }
        }
        while (changed);

        foreach (var (method, methodFacts) in closure)
        {
            if (!_handlers.TryGetValue(method, out var handler))
                continue;

            var eligible = methodFacts.Mutations.Values
                .Where(mutation => !mutation.RequiresSaveChanges || methodFacts.HasSaveChanges)
                .GroupBy(mutation => mutation.EntityId, StringComparer.Ordinal);

            foreach (var mutations in eligible)
            {
                var first = mutations.First();
                var table = _tables.ResolveTable(first.Entity);
                var confidence = mutations.Any(mutation => mutation.Confidence == Confidence.Confirmed)
                    ? Confidence.Confirmed
                    : Confidence.Likely;
                var evidence = mutations.Select(mutation => mutation.Evidence).Distinct().ToArray();
                var events = mutations.SelectMany(mutation => mutation.Events).Distinct().ToArray();
                graph.AddEdge(new Writes(handler, table, confidence, evidence, events));
            }
        }
    }

    private static IReadOnlyList<EntityUse> FindQueries(IEnumerable<SyntaxNode> nodes,
                                                         IEnumerable<InvocationExpressionSyntax> invocations,
                                                         SemanticModel semanticModel,
                                                         CancellationToken cancellationToken)
    {
        var queries = new Dictionary<(string Entity, int Position), EntityUse>();

        foreach (var name in nodes.OfType<SimpleNameSyntax>())
        {
            if (semanticModel.GetSymbolInfo(name, cancellationToken).Symbol is IPropertySymbol property &&
                TryGetDbSetEntity(property.Type, out var entity))
            {
                var use = new EntityUse(entity, name.SpanStart);
                queries.TryAdd((GetEntityId(entity), use.Position), use);
            }
        }

        foreach (var invocation in invocations)
        {
            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                continue;

            INamedTypeSymbol? entity = null;
            if (called.Name == "Set" && called.Arity == 1 &&
                TryGetDbSetEntity(called.ReturnType, out var setEntity))
            {
                entity = setEntity;
            }
            else if (called.Name is "Find" or "FindAsync")
            {
                entity = GetFindEntity(invocation, called, semanticModel, cancellationToken);
            }

            if (entity is not null)
            {
                var use = new EntityUse(entity, invocation.SpanStart);
                queries.TryAdd((GetEntityId(entity), use.Position), use);
            }
        }

        return queries.Values.OrderBy(query => query.Position).ToArray();
    }

    private static INamedTypeSymbol? GetFindEntity(InvocationExpressionSyntax invocation, IMethodSymbol called,
                                                    SemanticModel semanticModel,
                                                    CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            TryGetDbSetEntity(semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type,
                              out var setEntity))
        {
            return setEntity;
        }

        return called.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
    }

    private static bool TryGetExecuteEntity(InvocationExpressionSyntax invocation, IMethodSymbol called,
                                             SemanticModel semanticModel,
                                             CancellationToken cancellationToken,
                                             out INamedTypeSymbol entity)
    {
        if (called.TypeArguments.FirstOrDefault() is INamedTypeSymbol typeArgument)
        {
            entity = typeArgument;
            return true;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            TryGetSequenceEntity(semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type,
                                 out entity))
        {
            return true;
        }

        entity = null!;
        return false;
    }

    private static bool TryGetEntryStateEntity(AssignmentExpressionSyntax assignment,
                                                SemanticModel semanticModel,
                                                CancellationToken cancellationToken,
                                                out INamedTypeSymbol entity)
    {
        if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "State" } state &&
            semanticModel.GetTypeInfo(state.Expression, cancellationToken).Type is INamedTypeSymbol entryType &&
            entryType.Name == "EntityEntry" && entryType.Arity == 1 &&
            entryType.TypeArguments[0] is INamedTypeSymbol entryEntity)
        {
            entity = entryEntity;
            return true;
        }

        entity = null!;
        return false;
    }

    private static bool TryGetAssignedObjectEntity(AssignmentExpressionSyntax assignment,
                                                    SemanticModel semanticModel,
                                                    CancellationToken cancellationToken,
                                                    out INamedTypeSymbol entity)
    {
        if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
            semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol &&
            semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type is INamedTypeSymbol type)
        {
            entity = type;
            return true;
        }

        entity = null!;
        return false;
    }

    private static bool HasLaterPropertyAssignment(INamedTypeSymbol entity, int position,
                                                    IEnumerable<AssignmentExpressionSyntax> assignments,
                                                    SemanticModel semanticModel,
                                                    CancellationToken cancellationToken) =>
        assignments.Any(assignment => assignment.SpanStart > position &&
            TryGetAssignedObjectEntity(assignment, semanticModel, cancellationToken, out var assigned) &&
            SameEntity(entity, assigned));

    private static bool IsInvocationOnOrWithEntity(InvocationExpressionSyntax invocation,
                                                    INamedTypeSymbol entity,
                                                    SemanticModel semanticModel,
                                                    CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type is INamedTypeSymbol receiver &&
            SameEntity(entity, receiver))
        {
            return true;
        }

        return invocation.ArgumentList.Arguments.Any(argument =>
            semanticModel.GetTypeInfo(argument.Expression, cancellationToken).Type is INamedTypeSymbol argumentType &&
            SameEntity(entity, argumentType));
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

    private static bool TryGetSequenceEntity(ITypeSymbol? type, out INamedTypeSymbol entity)
    {
        if (TryGetDbSetEntity(type, out entity))
            return true;

        if (type is INamedTypeSymbol named)
        {
            if (IsSequenceType(named) && named.TypeArguments[0] is INamedTypeSymbol directEntity)
            {
                entity = directEntity;
                return true;
            }

            var sequence = named.AllInterfaces.FirstOrDefault(IsSequenceType);
            if (sequence?.TypeArguments[0] is INamedTypeSymbol interfaceEntity)
            {
                entity = interfaceEntity;
                return true;
            }
        }

        entity = null!;
        return false;
    }

    private static bool TryGetCollectionEntity(ITypeSymbol? type, out INamedTypeSymbol entity) =>
        TryGetSequenceEntity(type, out entity);

    private static bool IsSequenceType(INamedTypeSymbol type) =>
        type.Arity == 1 && type.Name is "IQueryable" or "IEnumerable" or "ICollection" or "IList" or "List";

    private static MutationFact CreateMutation(INamedTypeSymbol entity, SyntaxNode site,
                                                Confidence confidence,
                                                IReadOnlyList<WriteEvent> events, bool requiresSaveChanges)
    {
        var lineSpan = site.GetLocation().GetLineSpan();
        return new MutationFact(GetEntityId(entity), entity, confidence, events,
            new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1),
            requiresSaveChanges, site.SpanStart);
    }

    /// <summary>The events the named EF method performs; every event where the method does not say.</summary>
    private static IReadOnlyList<WriteEvent> WriteEventsFor(string method) => method switch
    {
        "Add" or "AddRange" => [WriteEvent.Insert],
        "Update" or "UpdateRange" or "ExecuteUpdate" or "ExecuteUpdateAsync" => [WriteEvent.Update],
        "Remove" or "RemoveRange" or "ExecuteDelete" or "ExecuteDeleteAsync" => [WriteEvent.Delete],
        _ => EVERY_WRITE_EVENT
    };

    private static bool SameEntity(INamedTypeSymbol left, INamedTypeSymbol right) =>
        GetEntityId(left) == GetEntityId(right);

    private static string GetEntityId(INamedTypeSymbol entity) =>
        entity.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private sealed record EntityUse(INamedTypeSymbol Entity, int Position);

    private sealed record MutationFact(string EntityId, INamedTypeSymbol Entity, Confidence Confidence,
                                       IReadOnlyList<WriteEvent> Events, Evidence Evidence,
                                       bool RequiresSaveChanges, int SiteStart)
    {
        public string Key => $"{EntityId}|{Evidence.File}|{Evidence.Line}|{SiteStart}|{RequiresSaveChanges}";
    }

    private sealed class MethodFacts
    {
        public bool HasSaveChanges { get; set; }
        public Dictionary<string, MutationFact> Mutations { get; } = new(StringComparer.Ordinal);

        public void AddMutation(MutationFact mutation)
        {
            if (!Mutations.TryGetValue(mutation.Key, out var existing) ||
                existing.Confidence != Confidence.Confirmed && mutation.Confidence == Confidence.Confirmed)
            {
                Mutations[mutation.Key] = mutation;
            }
        }

        public MethodFacts Clone()
        {
            var clone = new MethodFacts { HasSaveChanges = HasSaveChanges };
            foreach (var mutation in Mutations.Values)
                clone.AddMutation(mutation);
            return clone;
        }

        public bool UnionWith(MethodFacts other)
        {
            var changed = false;
            if (other.HasSaveChanges && !HasSaveChanges)
            {
                HasSaveChanges = true;
                changed = true;
            }

            foreach (var mutation in other.Mutations.Values)
            {
                var count = Mutations.Count;
                var existingConfidence = Mutations.TryGetValue(mutation.Key, out var existing)
                    ? existing.Confidence
                    : (Confidence?)null;
                AddMutation(mutation);
                changed |= Mutations.Count != count || existingConfidence != Mutations[mutation.Key].Confidence;
            }

            return changed;
        }
    }

    private sealed class MethodSymbolComparer : IEqualityComparer<IMethodSymbol>
    {
        public static MethodSymbolComparer Instance { get; } = new();

        public bool Equals(IMethodSymbol? x, IMethodSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(IMethodSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
    }
}
