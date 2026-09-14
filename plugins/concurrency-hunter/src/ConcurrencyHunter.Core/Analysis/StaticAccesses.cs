using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Analysis;

internal static class StaticAccesses
{
    internal static IReadOnlyList<StaticAccess> Discover(Compilation compilation, ControllerRoots.DiscoveredRoot discoveredRoot, string rootDirectory,
                                                         CancellationToken cancellationToken)
    {
        var accesses = new List<StaticAccess>();
        foreach (var declarationReference in discoveredRoot.Method.DeclaringSyntaxReferences)
        {
            var declaration = declarationReference.GetSyntax(cancellationToken);
            SyntaxNode? bodySyntax = declaration switch
                                     {
                                         MethodDeclarationSyntax { Body: not null } method => method.Body,
                                         MethodDeclarationSyntax { ExpressionBody: not null } method => method.ExpressionBody.Expression,
                                         _ => null
                                     };
            if (bodySyntax is null)
                continue;

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
            var body = semanticModel.GetOperation(bodySyntax, cancellationToken);
            if (body is null)
                continue;

            foreach (var reference in body.DescendantsAndSelf().OfType<IFieldReferenceOperation>())
            {
                if (!reference.Field.IsStatic || reference.Field.IsConst || IsIgnored(reference))
                    continue;

                var protections = Protections(reference, body);
                var field = reference.Field;
                accesses.Add(new StaticAccess(new ResourceId(field.ContainingAssembly.Name, $"static:{SymbolNames.Type(field.ContainingType)}", [field.Name]),
                                              Operation(reference), discoveredRoot.Root, discoveredRoot.Root.Symbol, Source(reference, rootDirectory),
                                              protections.Names, protections.Ids));
            }
        }

        return accesses;
    }

    private static bool IsIgnored(IFieldReferenceOperation reference)
    {
        for (IOperation? current = reference; current is not null; current = current.Parent)
        {
            if (current is INameOfOperation)
                return true;

            if (current is IArgumentOperation argument && argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            {
                return true;
            }
        }

        return false;
    }

    private static AccessOperation Operation(IFieldReferenceOperation reference)
    {
        if (reference.Parent is ISimpleAssignmentOperation simple && ReferenceEquals(simple.Target, reference))
        {
            return AccessOperation.Write;
        }

        for (var current = reference.Parent; current is ITupleOperation; current = current.Parent)
        {
            if (current.Parent is IDeconstructionAssignmentOperation deconstruction && ReferenceEquals(deconstruction.Target, current))
            {
                return AccessOperation.Write;
            }
        }

        if (reference.Parent is ICompoundAssignmentOperation compound && ReferenceEquals(compound.Target, reference) ||
            reference.Parent is ICoalesceAssignmentOperation coalesce && ReferenceEquals(coalesce.Target, reference) ||
            reference.Parent is IIncrementOrDecrementOperation increment && ReferenceEquals(increment.Target, reference))
        {
            return AccessOperation.ReadModifyWrite;
        }

        return AccessOperation.Read;
    }

    private static (IReadOnlyList<string> Names, IReadOnlyList<string> Ids) Protections(IFieldReferenceOperation reference, IOperation body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var current = reference.Parent; current is not null && !ReferenceEquals(current, body); current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
                break;

            if (current is not ILockOperation @lock || !Contains(@lock.Body, reference))
                continue;

            var lockedValue = @lock.LockedValue;
            while (lockedValue is IConversionOperation conversion)
                lockedValue = conversion.Operand;

            if (lockedValue is not IFieldReferenceOperation { Field.IsStatic: true } lockField)
                continue;

            var name = $"static:{SymbolNames.Type(lockField.Field.ContainingType)}.{lockField.Field.Name}";
            names.Add(name);
            ids.Add($"{lockField.Field.ContainingAssembly.Name}:{name}");
        }

        return (names.Order(StringComparer.Ordinal).ToArray(), ids.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool Contains(IOperation operation, IOperation candidate) =>
        operation.DescendantsAndSelf().Any(descendant => ReferenceEquals(descendant, candidate));

    private static SourceSpan Source(IFieldReferenceOperation reference, string rootDirectory)
    {
        var lineSpan = reference.Syntax.GetLocation().GetLineSpan();
        var fullPath = Path.GetFullPath(lineSpan.Path);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(rootDirectory), fullPath);
        var outsideRoot = relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                          Path.IsPathRooted(relativePath);

        var path = (outsideRoot ? fullPath : relativePath).Replace('\\', '/');
        return new SourceSpan(path, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, lineSpan.EndLinePosition.Line + 1,
                              lineSpan.EndLinePosition.Character + 1);
    }
}
