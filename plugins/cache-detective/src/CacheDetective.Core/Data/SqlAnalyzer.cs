using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace CacheDetective.Data;

/// <summary>
/// Finds the raw-SQL call sites in a handler, folds each command text, parses it with the T-SQL grammar,
/// and reads the tables and procedures out of the parse tree. What the fold could not prove is a
/// substituted parameter, and where that parameter landed in the tree — a table source, an <c>EXEC</c>
/// target, a schema qualifier — is what sends a statement to <c>unresolved</c>. See <c>docs/adr/0006</c>.
/// </summary>
internal sealed class SqlAnalyzer(Solution solution)
{
    private const string DEFAULT_SCHEMA = "dbo";
    private const string UNKNOWN_TABLE = "unknown table name";
    private const string UNKNOWN_SCHEMA = "unknown schema name";
    private const string UNKNOWN_PROCEDURE = "unknown procedure name";
    private const string MISSING_TEXT = "The SQL command text could not be found.";

    private readonly SqlTextFolder _folder = new(solution);

    public async Task AnalyzeAsync(CacheGraph graph, Handler handler, IMethodSymbol method,
                                   CancellationToken cancellationToken)
    {
        var recordedSites = new HashSet<(SyntaxTree Tree, int Start, string Reason)>();

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
            var commands = CollectCommands(nodes, semanticModel, cancellationToken);
            var analyzedTexts = new HashSet<int>();

            foreach (var site in FindSites(nodes, semanticModel, commands, cancellationToken))
            {
                if (site.Text is null)
                {
                    Record(graph, handler, site.Node,
                        site.IsProcedureName ? Position(UNKNOWN_PROCEDURE) : MISSING_TEXT, recordedSites);
                    continue;
                }

                if (!analyzedTexts.Add(site.Text.SpanStart))
                    continue;

                var folded = await _folder.FoldAsync(site.Text, semanticModel, cancellationToken);
                if (site.IsProcedureName)
                {
                    AnalyzeProcedureName(graph, handler, site.Node, folded, recordedSites);
                }
                else
                {
                    AnalyzeBatch(graph, handler, site.Node, folded, recordedSites);
                }
            }
        }
    }

    /// <summary>A command whose type is <c>StoredProcedure</c> carries a bare procedure name, not a batch.</summary>
    private static void AnalyzeProcedureName(CacheGraph graph, Handler handler, SyntaxNode site,
                                             FoldedSql folded,
                                             ISet<(SyntaxTree Tree, int Start, string Reason)> recordedSites)
    {
        if (folded.Parameters.Count > 0 || !TryParseObjectName(folded.Text, out var name))
        {
            Record(graph, handler, site, Position(UNKNOWN_PROCEDURE), recordedSites);
            return;
        }

        graph.AddEdge(new Calls(handler, new StoredProcedure(name.Schema, name.Name, name.Database),
            Confidence.Confirmed, [SiteEvidence(site)]));
    }

    private static void AnalyzeBatch(CacheGraph graph, Handler handler, SyntaxNode site, FoldedSql folded,
                                     ISet<(SyntaxTree Tree, int Start, string Reason)> recordedSites)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(folded.Text), out var errors);
        if (errors.Count > 0)
        {
            Record(graph, handler, site, $"The SQL could not be parsed: {errors[0].Message}", recordedSites);
            return;
        }

        var evidence = SiteEvidence(site);
        foreach (var statement in GetStatements(fragment))
        {
            var walker = new StatementWalker(folded.Parameters);
            statement.Accept(walker);
            if (walker.UnknownPosition is not null)
            {
                Record(graph, handler, site, Position(walker.UnknownPosition), recordedSites);
                continue;
            }

            foreach (var write in walker.Writes)
            {
                graph.AddEdge(new Writes(handler, ToTable(walker.Resolve(write.Name)), Confidence.Confirmed,
                    [evidence], write.Events));
            }

            foreach (var reference in walker.References)
            {
                if (walker.IsTarget(reference) || walker.IsBound(reference.SchemaObject))
                    continue;

                graph.AddEdge(new Reads(handler, ToTable(reference.SchemaObject), Confidence.Confirmed,
                    [evidence]));
            }

            foreach (var procedure in walker.Procedures)
            {
                graph.AddEdge(new Calls(handler, ToProcedure(procedure), Confidence.Confirmed, [evidence]));
            }
        }
    }

    private static IEnumerable<TSqlFragment> GetStatements(TSqlFragment fragment) =>
        fragment is TSqlScript script
            ? script.Batches.SelectMany(batch => batch.Statements)
            : [fragment];

    /// <summary>The command objects of one method: where each one's text comes from, and which of them the
    /// code declared to hold a procedure name rather than a batch.</summary>
    private static CommandObjects CollectCommands(IReadOnlyList<SyntaxNode> nodes, SemanticModel semanticModel,
                                                  CancellationToken cancellationToken)
    {
        var texts = new Dictionary<ISymbol, ExpressionSyntax>(SymbolEqualityComparer.Default);
        var procedures = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>())
        {
            var command = GetReceiverSymbol(assignment.Left, semanticModel, cancellationToken);
            if (command is null)
                continue;

            if (IsStoredProcedureAssignment(assignment, semanticModel, cancellationToken))
            {
                procedures.Add(command);
            }
            else if (semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is IPropertySymbol
                     { Name: "CommandText" } property && IsCommandType(property.ContainingType))
            {
                texts.TryAdd(command, assignment.Right);
            }
        }

        foreach (var creation in nodes.OfType<ObjectCreationExpressionSyntax>())
        {
            if (!IsSqlClientType(semanticModel.GetTypeInfo(creation, cancellationToken).Type))
                continue;

            var text = FindSqlText(creation.ArgumentList?.Arguments, semanticModel, cancellationToken);
            var symbol = GetAssignedSymbol(creation, semanticModel, cancellationToken);
            if (text is not null && symbol is not null)
                texts.TryAdd(symbol, text);
        }

        return new CommandObjects(texts, procedures);
    }

    private static IEnumerable<SqlSite> FindSites(IReadOnlyList<SyntaxNode> nodes, SemanticModel semanticModel,
                                                  CommandObjects commands, CancellationToken cancellationToken)
    {
        foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                continue;

            if (IsDapperCall(invocation, called, semanticModel, cancellationToken) ||
                IsEfSqlCall(invocation, called, semanticModel, cancellationToken))
            {
                yield return new SqlSite(invocation,
                    FindSqlText(invocation.ArgumentList.Arguments, semanticModel, cancellationToken), false);
            }
            else if (IsSqlClientInvocation(invocation, semanticModel, cancellationToken))
            {
                var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                var symbol = GetReceiverSymbol(invocation.Expression, semanticModel, cancellationToken);
                var text = receiver is ObjectCreationExpressionSyntax created
                    ? FindSqlText(created.ArgumentList?.Arguments, semanticModel, cancellationToken)
                    : commands.TextFor(symbol);
                yield return new SqlSite(invocation, text, commands.IsProcedure(symbol));
            }
        }

        foreach (var creation in nodes.OfType<ObjectCreationExpressionSyntax>())
        {
            if (!IsSqlClientType(semanticModel.GetTypeInfo(creation, cancellationToken).Type))
                continue;

            var symbol = GetAssignedSymbol(creation, semanticModel, cancellationToken);
            var text = FindSqlText(creation.ArgumentList?.Arguments, semanticModel, cancellationToken)
                       ?? commands.TextFor(symbol);
            yield return new SqlSite(creation, text, commands.IsProcedure(symbol));
        }

        foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>())
        {
            if (!IsStoredProcedureAssignment(assignment, semanticModel, cancellationToken))
                continue;

            var symbol = GetReceiverSymbol(assignment.Left, semanticModel, cancellationToken);
            yield return new SqlSite(assignment, commands.TextFor(symbol), true);
        }
    }

    private static bool IsDapperCall(InvocationExpressionSyntax invocation, IMethodSymbol called,
                                     SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var definition = called.ReducedFrom ?? called;
        if (definition.ContainingType.Name != "SqlMapper" ||
            definition.ContainingNamespace.ToDisplayString() != "Dapper" ||
            !(definition.Name.StartsWith("Query", StringComparison.Ordinal) ||
              definition.Name.StartsWith("Execute", StringComparison.Ordinal)))
        {
            return false;
        }

        var receiver = GetExtensionReceiver(invocation, called);
        return receiver is not null &&
               IsDbConnection(semanticModel.GetTypeInfo(receiver, cancellationToken).Type);
    }

    private static bool IsEfSqlCall(InvocationExpressionSyntax invocation, IMethodSymbol called,
                                    SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (called.Name is not ("FromSql" or "FromSqlRaw" or "FromSqlInterpolated") &&
            !called.Name.StartsWith("ExecuteSql", StringComparison.Ordinal))
        {
            return false;
        }

        var definition = called.ReducedFrom ?? called;
        if (definition.ContainingNamespace.ToDisplayString()
            .StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
        {
            return true;
        }

        var receiver = invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type
            : null;
        return receiver is INamedTypeSymbol { Name: "DbSet" or "DatabaseFacade" };
    }

    private static bool IsSqlClientInvocation(InvocationExpressionSyntax invocation,
                                              SemanticModel semanticModel,
                                              CancellationToken cancellationToken) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
        IsSqlClientType(semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type);

    private static bool IsStoredProcedureAssignment(AssignmentExpressionSyntax assignment,
                                                     SemanticModel semanticModel,
                                                     CancellationToken cancellationToken)
    {
        if (semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is not IPropertySymbol
            { Name: "CommandType" })
            return false;

        if (semanticModel.GetSymbolInfo(assignment.Right, cancellationToken).Symbol is IFieldSymbol
            {
                Name: "StoredProcedure",
                ContainingType.Name: "CommandType"
            } field && field.ContainingType.ContainingNamespace.ToDisplayString() == "System.Data")
        {
            return true;
        }

        var type = semanticModel.GetTypeInfo(assignment.Right, cancellationToken).Type;
        var constant = semanticModel.GetConstantValue(assignment.Right, cancellationToken);
        return type is INamedTypeSymbol { Name: "CommandType" } commandType &&
               commandType.ContainingNamespace.ToDisplayString() == "System.Data" &&
               constant is { HasValue: true, Value: 4 };
    }

    private static ExpressionSyntax? GetExtensionReceiver(InvocationExpressionSyntax invocation,
                                                           IMethodSymbol called)
    {
        if (called.ReducedFrom is not null &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression;
        }

        return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
    }

    private static bool IsDbConnection(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        return IsDbConnectionShape(named) || named.AllInterfaces.Any(IsDbConnectionShape);
    }

    private static bool IsDbConnectionShape(INamedTypeSymbol type) =>
        type.Name == "IDbConnection" && type.Arity == 0 &&
        type.ContainingNamespace.ToDisplayString() == "System.Data";

    private static bool IsSqlClientType(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.Name is not ("SqlCommand" or "SqlDataAdapter"))
                continue;

            var namespaceName = current.ContainingNamespace.ToDisplayString();
            if (namespaceName is "System.Data.SqlClient" or "Microsoft.Data.SqlClient")
                return true;
        }

        return false;
    }

    private static bool IsCommandType(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name is "DbCommand" or "IDbCommand" || IsSqlClientType(current) ||
                current.AllInterfaces.Any(candidate => candidate.Name == "IDbCommand"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first argument that carries text: the receiver of an extension call is never one.</summary>
    private static ExpressionSyntax? FindSqlText(IEnumerable<ArgumentSyntax>? arguments,
                                                  SemanticModel semanticModel,
                                                  CancellationToken cancellationToken) =>
        arguments?.Select(argument => argument.Expression)
                  .FirstOrDefault(expression => IsSqlTextType(
                      semanticModel.GetTypeInfo(expression, cancellationToken).Type));

    private static bool IsSqlTextType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_String || type?.Name == "FormattableString";

    private static ISymbol? GetReceiverSymbol(ExpressionSyntax expression, SemanticModel semanticModel,
                                               CancellationToken cancellationToken) =>
        expression is MemberAccessExpressionSyntax memberAccess
            ? semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol
            : null;

    private static ISymbol? GetAssignedSymbol(ExpressionSyntax creation, SemanticModel semanticModel,
                                               CancellationToken cancellationToken) => creation.Parent switch
    {
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } =>
            semanticModel.GetDeclaredSymbol(declarator, cancellationToken),
        AssignmentExpressionSyntax assignment when assignment.Right == creation =>
            semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol,
        _ => null
    };

    private static bool TryParseObjectName(string text, out (string Schema, string Name, string? Database) name)
    {
        name = default;
        var parts = text.Trim().Split('.');
        if (parts.Length is 0 or > 3)
            return false;

        var identifiers = parts.Select(part => part.Trim().Trim('[', ']', '"')).ToArray();
        if (identifiers.Any(identifier => identifier.Length == 0 || identifier.Any(char.IsWhiteSpace)))
            return false;

        name = (identifiers.Length >= 2 ? identifiers[^2] : DEFAULT_SCHEMA, identifiers[^1],
                identifiers.Length == 3 ? identifiers[0] : null);
        return true;
    }

    private static Table ToTable(SchemaObjectName name) =>
        new(name.SchemaIdentifier?.Value ?? DEFAULT_SCHEMA, name.BaseIdentifier.Value,
            name.DatabaseIdentifier?.Value);

    private static StoredProcedure ToProcedure(SchemaObjectName name) =>
        new(name.SchemaIdentifier?.Value ?? DEFAULT_SCHEMA, name.BaseIdentifier.Value,
            name.DatabaseIdentifier?.Value);

    private static string Position(string position) => $"The SQL was not analyzed: {position}.";

    private static Evidence SiteEvidence(SyntaxNode site)
    {
        var lineSpan = site.GetLocation().GetLineSpan();
        return new Evidence(lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    private static void Record(CacheGraph graph, Handler handler, SyntaxNode site, string reason,
                               ISet<(SyntaxTree Tree, int Start, string Reason)> recordedSites)
    {
        if (!recordedSites.Add((site.SyntaxTree, site.SpanStart, reason)))
            return;

        graph.AddUnresolved(UnresolvedKind.Sql, handler, SiteEvidence(site), site.ToString(), reason);
    }

    private sealed record SqlSite(SyntaxNode Node, ExpressionSyntax? Text, bool IsProcedureName);

    private sealed record TableWrite(SchemaObjectName Name, IReadOnlyList<WriteEvent> Events);

    private sealed class CommandObjects(IReadOnlyDictionary<ISymbol, ExpressionSyntax> texts,
                                        ISet<ISymbol> procedures)
    {
        public ExpressionSyntax? TextFor(ISymbol? command) =>
            command is not null && texts.TryGetValue(command, out var text) ? text : null;

        public bool IsProcedure(ISymbol? command) => command is not null && procedures.Contains(command);
    }

    /// <summary>Reads one statement's tables, procedures and substituted positions out of the parse tree.</summary>
    private sealed class StatementWalker(IReadOnlySet<string> parameters) : TSqlFragmentVisitor
    {
        private static readonly WriteEvent[] INSERT_EVENTS = [WriteEvent.Insert];
        private static readonly WriteEvent[] UPDATE_EVENTS = [WriteEvent.Update];
        private static readonly WriteEvent[] DELETE_EVENTS = [WriteEvent.Delete];
        private static readonly WriteEvent[] MERGE_EVENTS =
            [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete];
        private static readonly WriteEvent[] TRUNCATE_EVENTS = [WriteEvent.Truncate];

        private readonly HashSet<TableReference> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, SchemaObjectName> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bound = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The position a substituted parameter landed in, when that position decides a vertex.</summary>
        public string? UnknownPosition { get; private set; }

        public List<NamedTableReference> References { get; } = [];

        public List<TableWrite> Writes { get; } = [];

        public List<SchemaObjectName> Procedures { get; } = [];

        public override void Visit(NamedTableReference node)
        {
            CheckObjectName(node.SchemaObject, UNKNOWN_TABLE);
            References.Add(node);
            if (node.Alias is not null)
            {
                _bound.Add(node.Alias.Value);
                _aliases[node.Alias.Value] = node.SchemaObject;
            }
        }

        public override void Visit(VariableTableReference node)
        {
            if (IsSubstituted(node.Variable))
                Unknown(UNKNOWN_TABLE);
            if (node.Alias is not null)
                _bound.Add(node.Alias.Value);
        }

        public override void Visit(CommonTableExpression node) => _bound.Add(node.ExpressionName.Value);

        public override void Visit(InsertSpecification node) => AddWrite(node.Target, INSERT_EVENTS);

        public override void Visit(UpdateSpecification node) => AddWrite(node.Target, UPDATE_EVENTS);

        public override void Visit(DeleteSpecification node) => AddWrite(node.Target, DELETE_EVENTS);

        public override void Visit(MergeSpecification node) => AddWrite(node.Target, MERGE_EVENTS);

        public override void Visit(TruncateTableStatement node)
        {
            CheckObjectName(node.TableName, UNKNOWN_TABLE);
            Writes.Add(new TableWrite(node.TableName, TRUNCATE_EVENTS));
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference is not { } reference)
                return;

            if (reference.ProcedureVariable is not null)
            {
                if (IsSubstituted(reference.ProcedureVariable))
                    Unknown(UNKNOWN_PROCEDURE);
                return;
            }

            if (reference.ProcedureReference?.Name is not { } name)
                return;

            CheckObjectName(name, UNKNOWN_PROCEDURE);
            Procedures.Add(name);
        }

        /// <summary>Resolves a one-part target name that the statement bound as an alias, as in
        /// <c>UPDATE t SET … FROM dbo.Products AS t</c>.</summary>
        public SchemaObjectName Resolve(SchemaObjectName name) =>
            name.Count == 1 && _aliases.TryGetValue(name.BaseIdentifier.Value, out var aliased)
                ? aliased
                : name;

        public bool IsTarget(TableReference reference) => _targets.Contains(reference);

        /// <summary>Whether a name is one the statement itself bound — a CTE or an alias — and so not a table.</summary>
        public bool IsBound(SchemaObjectName name) =>
            name.Count == 1 && _bound.Contains(name.BaseIdentifier.Value);

        private void AddWrite(TableReference target, IReadOnlyList<WriteEvent> events)
        {
            if (target is not NamedTableReference named)
                return;

            _targets.Add(named);
            Writes.Add(new TableWrite(named.SchemaObject, events));
        }

        private void CheckObjectName(SchemaObjectName name, string basePosition)
        {
            if (name.BaseIdentifier is { } baseIdentifier && IsSubstituted(baseIdentifier))
            {
                Unknown(basePosition);
            }
            else if (GetQualifiers(name).Any(IsSubstituted))
            {
                Unknown(UNKNOWN_SCHEMA);
            }
        }

        private static IEnumerable<Identifier> GetQualifiers(SchemaObjectName name) => new[] { name.SchemaIdentifier, name.DatabaseIdentifier, name.ServerIdentifier }
                .OfType<Identifier>();

        private bool IsSubstituted(Identifier identifier) => parameters.Contains(identifier.Value);

        private bool IsSubstituted(VariableReference variable) => parameters.Contains(variable.Name);

        private void Unknown(string position) => UnknownPosition ??= position;
    }
}
