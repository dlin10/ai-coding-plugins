using CacheDetective.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Data;

internal sealed class UnparsedSqlAnalyzer
{
    private const string ReasonPrefix = "SQL parsing is out of scope for this phase";

    public async Task AnalyzeAsync(Solution solution, CacheGraph graph, Handler handler,
                                   IMethodSymbol method, CancellationToken cancellationToken)
    {
        var recordedSites = new HashSet<(SyntaxTree Tree, int Start)>();

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

            foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                    continue;

                if (IsDapperCall(invocation, called, semanticModel, cancellationToken))
                {
                    Record(graph, handler, invocation, "Dapper call", recordedSites);
                }
                else if (IsEfSqlCall(invocation, called, semanticModel, cancellationToken))
                {
                    Record(graph, handler, invocation, "EF Core SQL call", recordedSites);
                }
                else if (IsSqlClientInvocation(invocation, semanticModel, cancellationToken))
                {
                    Record(graph, handler, invocation, "SQL client command", recordedSites);
                }
            }

            foreach (var creation in nodes.OfType<ObjectCreationExpressionSyntax>())
            {
                if (IsSqlClientType(semanticModel.GetTypeInfo(creation, cancellationToken).Type))
                    Record(graph, handler, creation, "SQL client command", recordedSites);
            }

            foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>())
            {
                if (IsStoredProcedureAssignment(assignment, semanticModel, cancellationToken))
                    Record(graph, handler, assignment, "stored procedure command", recordedSites);
            }
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

    private static void Record(CacheGraph graph, Handler handler, SyntaxNode site, string subject,
                               ISet<(SyntaxTree Tree, int Start)> recordedSites)
    {
        if (!recordedSites.Add((site.SyntaxTree, site.SpanStart)))
            return;

        var lineSpan = site.GetLocation().GetLineSpan();
        graph.AddUnresolved(UnresolvedKind.Sql, handler, lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1, site.ToString(), $"{ReasonPrefix} ({subject}).");
    }
}
