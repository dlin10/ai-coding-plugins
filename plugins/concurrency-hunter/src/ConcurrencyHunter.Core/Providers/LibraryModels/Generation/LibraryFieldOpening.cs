using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>Opens seed and path fields in the generator's copy of a decompiled assembly, reverting the smallest responsible
/// opening units until the opening introduces no compilation error.</summary>
internal static class LibraryFieldOpening
{
    internal static (CSharpCompilation Compilation, IReadOnlySet<string> OpenedFields) Open(CSharpCompilation baseline,
                                                                                           CancellationToken cancellationToken)
    {
        var plan = Plan.Create(baseline, cancellationToken);
        var active = plan.Units.ToHashSet();
        var baselineErrors = Errors(baseline, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = plan.Apply(baseline, active, cancellationToken);
            var introduced = applied.Compilation.GetDiagnostics(cancellationToken)
                                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                                                         !baselineErrors.Contains(ErrorOf(applied.Compilation, diagnostic, cancellationToken)))
                                    .ToArray();
            if (introduced.Length == 0)
                return (applied.Compilation, plan.OpenedFields(active));

            var reverted = new HashSet<OpeningUnit>();
            foreach (var error in introduced)
                reverted.UnionWith(applied.UnitsToRevert(error, active, cancellationToken));
            if (reverted.Count == 0)
                reverted.UnionWith(active);
            active.ExceptWith(reverted);
        }
    }

    private static HashSet<CompilationError> Errors(CSharpCompilation compilation, CancellationToken cancellationToken) =>
        compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                   .Select(diagnostic => ErrorOf(compilation, diagnostic, cancellationToken)).ToHashSet();

    private static CompilationError ErrorOf(CSharpCompilation compilation, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var member = "";
        if (diagnostic.Location.SourceTree is { } tree)
        {
            var model = compilation.GetSemanticModel(tree);
            var node = tree.GetRoot(cancellationToken).FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var declaration = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            ISymbol? symbol = declaration switch
            {
                BaseFieldDeclarationSyntax field => field.Declaration.Variables
                    .FirstOrDefault(variable => variable.Span.Contains(diagnostic.Location.SourceSpan)) is { } variable
                        ? model.GetDeclaredSymbol(variable, cancellationToken)
                        : null,
                null => null,
                _ => model.GetDeclaredSymbol(declaration, cancellationToken)
            };
            member = symbol?.GetDocumentationCommentId() ?? "";
        }

        return new CompilationError(diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture), member);
    }

    private sealed record CompilationError(string Id, string Message, string MemberId);

    private sealed class Plan(IReadOnlyList<SyntaxTree> trees, IReadOnlyList<OpeningUnit> units, IReadOnlyList<OpeningMember> members,
                              int attributeTree)
    {
        public IReadOnlyList<OpeningUnit> Units { get; } = units;

        public static Plan Create(CSharpCompilation compilation, CancellationToken cancellationToken)
        {
            var trees = compilation.SyntaxTrees.ToArray();
            var units = new List<OpeningUnit>();
            var members = new List<OpeningMember>();
            for (var treeIndex = 0; treeIndex < trees.Length; treeIndex++)
            {
                var tree = trees[treeIndex];
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(cancellationToken);
                var unitByNode = new Dictionary<SyntaxNode, OpeningUnit>();
                foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(type, cancellationToken) is not { TypeKind: TypeKind.Class } typeSymbol)
                        continue;
                    foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                    {
                        if (model.GetTypeInfo(field.Declaration.Type, cancellationToken).Type is not { } fieldType ||
                            !SeedableFields.Seed(fieldType) && !SeedableFields.Path(fieldType))
                        {
                            continue;
                        }

                        var opened = OpenField(field, model, cancellationToken, SeedableFields.Seed(fieldType));
                        var own = Equivalent(field, opened) ? null : AddUnit(treeIndex, field, opened, units, unitByNode);
                        AddMember(field.Declaration.Variables.Select(variable => model.GetDeclaredSymbol(variable, cancellationToken)),
                                  field, own, type, treeIndex, model, units, unitByNode, members, cancellationToken);
                    }

                    foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>().Where(IsAutoProperty))
                    {
                        if (model.GetDeclaredSymbol(property, cancellationToken) is not { } propertySymbol ||
                            !SeedableFields.Seed(propertySymbol.Type) && !SeedableFields.Path(propertySymbol.Type))
                        {
                            continue;
                        }

                        var opened = OpenProperty(property, propertySymbol, SeedableFields.Seed(propertySymbol.Type));
                        var own = Equivalent(property, opened) ? null : AddUnit(treeIndex, property, opened, units, unitByNode);
                        AddMember([propertySymbol], property, own, type, treeIndex, model, units, unitByNode, members, cancellationToken);
                    }
                }
            }

            return new Plan(trees, units, members, Math.Max(0, trees.Length - 1));
        }

        public AppliedPlan Apply(CSharpCompilation baseline, IReadOnlySet<OpeningUnit> active, CancellationToken cancellationToken)
        {
            var compilation = baseline;
            var appliedTrees = new Dictionary<SyntaxTree, IReadOnlyDictionary<OpeningUnit, SyntaxNode>>();
            for (var index = 0; index < trees.Count; index++)
            {
                var original = trees[index];
                var relevant = Units.Where(unit => unit.TreeIndex == index).ToDictionary(unit => unit.Span);
                var rewriter = new OpeningRewriter(relevant, active);
                var root = rewriter.Visit(original.GetRoot(cancellationToken))!;
                if (index == attributeTree && root is CompilationUnitSyntax compilationUnit)
                    root = compilationUnit.AddAttributeLists(FriendAttribute());
                var rewritten = original.WithRootAndOptions(root, original.Options);
                compilation = compilation.ReplaceSyntaxTree(original, rewritten);
                appliedTrees.Add(rewritten, rewriter.Current(root));
            }

            return new AppliedPlan(compilation, appliedTrees);
        }

        public IReadOnlySet<string> OpenedFields(IReadOnlySet<OpeningUnit> active) =>
            new SortedSet<string>(members.Where(member => member.RequiredUnits.Count > 0 && member.RequiredUnits.All(active.Contains))
                                          .SelectMany(member => member.DeclarationIds), StringComparer.Ordinal);

        private static void AddMember(IEnumerable<ISymbol?> symbols, MemberDeclarationSyntax declaration, OpeningUnit? own,
                                      TypeDeclarationSyntax containingType, int treeIndex, SemanticModel model, List<OpeningUnit> units,
                                      Dictionary<SyntaxNode, OpeningUnit> unitByNode, List<OpeningMember> members,
                                      CancellationToken cancellationToken)
        {
            var required = new List<OpeningUnit>();
            if (own is not null)
                required.Add(own);
            for (SyntaxNode? current = containingType; current is TypeDeclarationSyntax type; current = type.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault())
            {
                if (model.GetDeclaredSymbol(type, cancellationToken) is not { } symbol ||
                    OpenTypeModifiers(type.Modifiers, symbol.DeclaredAccessibility) is not { } opened || Equivalent(type.Modifiers, opened))
                {
                    continue;
                }

                if (!unitByNode.TryGetValue(type, out var unit))
                {
                    unit = AddUnit(treeIndex, type, type.WithModifiers(opened), units, unitByNode);
                }
                required.Add(unit);
            }

            var ids = symbols.Select(symbol => symbol?.GetDocumentationCommentId()).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            if (ids.Length > 0)
                members.Add(new OpeningMember(ids, required.Distinct().ToArray()));
        }

        private static OpeningUnit AddUnit(int treeIndex, SyntaxNode original, SyntaxNode opened, List<OpeningUnit> units,
                                           Dictionary<SyntaxNode, OpeningUnit> unitByNode)
        {
            var unit = new OpeningUnit(units.Count, treeIndex, original.RawKind, original.Span, opened);
            units.Add(unit);
            unitByNode.Add(original, unit);
            return unit;
        }
    }

    private sealed class AppliedPlan(CSharpCompilation compilation,
                                     IReadOnlyDictionary<SyntaxTree, IReadOnlyDictionary<OpeningUnit, SyntaxNode>> nodes)
    {
        public CSharpCompilation Compilation { get; } = compilation;

        public IReadOnlySet<OpeningUnit> UnitsToRevert(Diagnostic diagnostic, IReadOnlySet<OpeningUnit> active,
                                                       CancellationToken cancellationToken)
        {
            if (diagnostic.Location.SourceTree is not { } tree || !nodes.TryGetValue(tree, out var current))
                return active.ToHashSet();
            var span = diagnostic.Location.SourceSpan;
            var containing = current.Where(pair => pair.Value.Span.Contains(span)).OrderBy(pair => pair.Value.Span.Length).FirstOrDefault();
            if (containing.Key is not null)
                return new HashSet<OpeningUnit> { containing.Key };

            var root = tree.GetRoot(cancellationToken);
            var type = root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (type is not null)
            {
                var inType = current.Where(pair => pair.Value.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault() == type)
                                    .Select(pair => pair.Key).ToHashSet();
                if (inType.Count > 0)
                    return inType;
            }

            return current.Keys.ToHashSet();
        }
    }

    private sealed class OpeningRewriter(IReadOnlyDictionary<TextSpan, OpeningUnit> units, IReadOnlySet<OpeningUnit> active)
        : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) => Open(node, base.VisitFieldDeclaration(node));

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) => Open(node, base.VisitPropertyDeclaration(node));

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Open(node, base.VisitClassDeclaration(node));

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Open(node, base.VisitRecordDeclaration(node));

        public IReadOnlyDictionary<OpeningUnit, SyntaxNode> Current(SyntaxNode root) =>
            active.Where(unit => unit.TreeIndex >= 0)
                  .Select(unit => (Unit: unit, Node: root.GetAnnotatedNodes(unit.Annotation).SingleOrDefault()))
                  .Where(pair => pair.Node is not null).ToDictionary(pair => pair.Unit, pair => pair.Node!);

        private SyntaxNode? Open(SyntaxNode original, SyntaxNode? visited)
        {
            if (visited is null || !units.TryGetValue(original.Span, out var unit) || unit.RawKind != original.RawKind || !active.Contains(unit))
                return visited;
            var opened = unit.Opened;
            if (opened is TypeDeclarationSyntax openedType && visited is TypeDeclarationSyntax visitedType)
                opened = visitedType.WithModifiers(openedType.Modifiers);
            return opened.WithAdditionalAnnotations(unit.Annotation);
        }
    }

    private sealed record OpeningUnit(int Id, int TreeIndex, int RawKind, TextSpan Span, SyntaxNode Opened)
    {
        public SyntaxAnnotation Annotation { get; } = new("field-opening", Id.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record OpeningMember(IReadOnlyList<string> DeclarationIds, IReadOnlyList<OpeningUnit> RequiredUnits);

    private static FieldDeclarationSyntax OpenField(FieldDeclarationSyntax field, SemanticModel model, CancellationToken cancellationToken,
                                                    bool seed)
    {
        var symbol = field.Declaration.Variables.Select(variable => model.GetDeclaredSymbol(variable, cancellationToken)).OfType<IFieldSymbol>().First();
        var modifiers = OpenMemberModifiers(field.Modifiers, symbol.DeclaredAccessibility);
        if (seed)
            modifiers = TokenList(modifiers.Where(modifier => !modifier.IsKind(SyntaxKind.ReadOnlyKeyword)));
        return field.WithModifiers(modifiers);
    }

    private static PropertyDeclarationSyntax OpenProperty(PropertyDeclarationSyntax property, IPropertySymbol symbol, bool seed)
    {
        // A path property only needs its getter reachable; its setter, init included, opens only for a seed (task 3).
        var accessors = property.AccessorList!.Accessors.Select(accessor =>
        {
            if (!seed && !accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                return accessor;
            var rewritten = accessor.WithModifiers(TokenList(accessor.Modifiers.Where(modifier => !IsAccessibility(modifier))));
            return accessor.IsKind(SyntaxKind.InitAccessorDeclaration)
                ? AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithAttributeLists(rewritten.AttributeLists)
                                                                         .WithModifiers(rewritten.Modifiers)
                                                                         .WithKeyword(Token(accessor.Keyword.LeadingTrivia,
                                                                                            SyntaxKind.SetKeyword,
                                                                                            accessor.Keyword.TrailingTrivia))
                                                                         .WithSemicolonToken(accessor.SemicolonToken)
                                                                         .WithTriviaFrom(accessor)
                : rewritten;
        }).ToList();
        if (seed && !accessors.Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration)))
            accessors.Add(AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        return property.WithModifiers(OpenMemberModifiers(property.Modifiers, symbol.DeclaredAccessibility))
                       .WithAccessorList(property.AccessorList.WithAccessors(List(accessors)));
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax property) =>
        !property.Modifiers.Any(SyntaxKind.AbstractKeyword) && !property.Modifiers.Any(SyntaxKind.ExternKeyword) &&
        property.AccessorList is { } accessors && accessors.Accessors.Count > 0 &&
        accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null && !accessor.SemicolonToken.IsMissing);

    private static SyntaxTokenList OpenMemberModifiers(SyntaxTokenList modifiers, Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private or Accessibility.ProtectedAndInternal => ReplaceAccessibility(modifiers, SyntaxKind.InternalKeyword),
        Accessibility.Protected => ReplaceAccessibility(modifiers, SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword),
        _ => modifiers
    };

    private static SyntaxTokenList? OpenTypeModifiers(SyntaxTokenList modifiers, Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal =>
            ReplaceAccessibility(modifiers, SyntaxKind.InternalKeyword),
        _ => null
    };

    private static SyntaxTokenList ReplaceAccessibility(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
    {
        var access = modifiers.Where(IsAccessibility).ToArray();
        var kept = modifiers.Where(modifier => !IsAccessibility(modifier)).ToList();
        var leading = access.FirstOrDefault().LeadingTrivia;
        var trailing = access.LastOrDefault().TrailingTrivia;
        var replacement = kinds.Select((kind, index) => Token(index == 0 ? leading : default, kind,
                                                               index == kinds.Length - 1 ? trailing : TriviaList(Space))).ToList();
        if (replacement.Count > 0 && access.Length == 0)
            replacement[^1] = replacement[^1].WithTrailingTrivia(Space);
        replacement.AddRange(kept);
        return TokenList(replacement);
    }

    private static bool IsAccessibility(SyntaxToken modifier) => modifier.IsKind(SyntaxKind.PublicKeyword) ||
        modifier.IsKind(SyntaxKind.InternalKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword);

    private static bool Equivalent(SyntaxNode left, SyntaxNode right) => SyntaxFactory.AreEquivalent(left, right);

    private static bool Equivalent(SyntaxTokenList left, SyntaxTokenList right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.IsKind(pair.Second.Kind()));

    private static AttributeListSyntax FriendAttribute() =>
        AttributeList(SingletonSeparatedList(Attribute(ParseName("global::System.Runtime.CompilerServices.InternalsVisibleTo"))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(AttributeArgument(
                LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(DriverSynthesizer.ASSEMBLY))))))))
        .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)));
}
