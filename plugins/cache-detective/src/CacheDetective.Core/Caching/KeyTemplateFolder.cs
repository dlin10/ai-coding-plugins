using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Caching;

/// <summary>A folded cache key: the template, whether it holds a literal segment, and the substitutions
/// with their places in the template.</summary>
internal sealed record KeyTemplateResult(string Template, bool HasLiteralSegment, string? Reason,
                                         IReadOnlyList<FoldedPart> Parts);

/// <summary>Every template one call site may key on, and whether the fold that produced them named them
/// or merely bounded them. See <see cref="FoldedSet"/>.</summary>
internal sealed record KeyTemplateSet(IReadOnlyList<KeyTemplateResult> Templates, bool Collapsed);

/// <summary>The key-template policy over <see cref="StringConstantFolder"/>: a known substitution becomes
/// <c>{name}</c>, an unknown one <c>{?}</c>, and a key with no literal segment is not a template at all.
/// <para>A key is not always a string. When the recognizer describes a key object
/// (<see cref="KeyObjectRecognizer"/>, <c>docs/adr/0016</c>) the template is a literal inside a
/// construction of that object, and the arguments a factory substitutes into its positional holes are
/// named the same way an interpolation hole is. Only argument <em>names</em> are claimed: what the library
/// derives from an argument at run time — an entity collapsed to its id, a list to a hash — is out of
/// reach and is never guessed.</para></summary>
internal sealed class KeyTemplateFolder(Solution solution)
{
    /// <summary>How far a key object may be followed through properties, fields and locals before the
    /// chain is called unresolvable. The same bound the string folder puts on helper hops.</summary>
    private const int MAXIMUM_HOPS = 5;

    private static readonly FoldedPlaceholders PLACEHOLDERS = new(name => $"{{{name}}}", "{?}");

    private readonly StringConstantFolder _folder = new(solution, PLACEHOLDERS);

    public async Task<KeyTemplateSet> FoldAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                                CancellationToken cancellationToken,
                                                KeyObjectRecognizer? keyObject = null)
    {
        var folded = keyObject is null
            ? await _folder.FoldAsync(expression, semanticModel, cancellationToken)
            : await FoldKeyObjectAsync(expression, semanticModel, keyObject, cancellationToken);
        return new KeyTemplateSet([.. folded.Values.Select(value =>
            new KeyTemplateResult(value.Value, value.HasLiteralPart, value.Reason, value.Parts))], folded.Collapsed);
    }

    /// <summary>A declared key object reaches the call site in one of three shapes: a factory call that
    /// substitutes into it, the object itself, or neither — a library that takes a key object may take a
    /// string too, and that key is folded exactly as it was before. The first two are the same walk, because
    /// either may be reached directly or through a local, so both are handed to
    /// <see cref="FoldTemplateAsync"/> and it decides at each step which it is looking at.</summary>
    private async Task<FoldedSet> FoldKeyObjectAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                                     KeyObjectRecognizer keyObject, CancellationToken cancellationToken)
    {
        return FindFactory(expression, semanticModel, keyObject, cancellationToken) is not null ||
               IsKeyObject(expression, semanticModel, keyObject, cancellationToken)
            ? await FoldTemplateAsync(expression, semanticModel, keyObject, 0, cancellationToken)
            : await _folder.FoldAsync(expression, semanticModel, cancellationToken);
    }

    private async Task<FoldedSet> FoldFactoryAsync(InvocationExpressionSyntax invocation, KeyObjectFactory factory,
                                                   SemanticModel semanticModel, KeyObjectRecognizer keyObject,
                                                   int hops, CancellationToken cancellationToken)
    {
        var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.Expression).ToArray();
        if (arguments.ElementAtOrDefault(factory.KeyObjectArgumentIndex) is not { } keyArgument)
            return UnknownSet($"The call to {factory.TypeName}.{invocation.Expression} carries no key object at argument {factory.KeyObjectArgumentIndex}.");

        var templates = await FoldTemplateAsync(keyArgument, semanticModel, keyObject, hops + 1, cancellationToken);
        var substitutions = StringConstantFolder.FlattenArguments(arguments.Skip(factory.ArgumentsArgumentIndex)).ToArray();
        var folded = new FoldedSet[substitutions.Length];
        for (var index = 0; index < substitutions.Length; index++)
            folded[index] = await _folder.FoldAsync(substitutions[index], semanticModel, cancellationToken);

        // Each template the key object may carry is substituted into on its own; one with nothing to
        // substitute into keeps its own reason, which says why.
        var results = templates.Values.Select(template => template.HasLiteralPart
            ? Substitute(template, folded)
            : StringConstantFolder.One(template)).ToArray();
        var union = _folder.Union(results);
        return templates.Collapsed ? union with { Collapsed = true } : union;
    }

    /// <summary>Substitutes into the template's positional holes. Only the parts proved literally can hold
    /// a hole: one the folder already wrote as <c>{name}</c> or <c>{?}</c> is a substitution of its own and
    /// passes through untouched.</summary>
    private FoldedSet Substitute(FoldedString template, IReadOnlyList<FoldedSet> values)
    {
        var result = StringConstantFolder.EmptySet();
        foreach (var part in template.Parts)
        {
            if (part.Kind != FoldedPartKind.Literal)
            {
                result = _folder.Product(result, StringConstantFolder.One(new FoldedString(part.Text, [part with { Start = 0 }], null)));
                continue;
            }

            var position = 0;
            foreach (Match match in StringConstantFolder.FORMAT_ITEM.Matches(part.Text))
            {
                result = _folder.Product(result, StringConstantFolder.One(StringConstantFolder.Literal(part.Text[position..match.Index])));
                var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
                result = _folder.Product(result, index < values.Count
                    ? values[index]
                    : UnknownSet("The key-object template hole has no matching argument."));
                position = match.Index + match.Length;
            }

            result = _folder.Product(result, StringConstantFolder.One(StringConstantFolder.Literal(part.Text[position..])));
        }

        return result;
    }

    /// <summary>Follows the expression to the constructions of the key object and folds each declared
    /// constructor argument as a string. A defaults class holding <c>static</c> properties is the shape
    /// nopCommerce uses, so a property, a field and a local are all followed to every value they may hold —
    /// a branch reaches a key object as readily as it reaches a string, and yields a set just the same.
    /// </summary>
    private async Task<FoldedSet> FoldTemplateAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                                    KeyObjectRecognizer keyObject, int hops,
                                                    CancellationToken cancellationToken)
    {
        if (hops > MAXIMUM_HOPS)
            return UnknownSet($"The key object was followed through more than {MAXIMUM_HOPS} declarations.");

        // A key object may arrive already substituted into, and not only at the call site: nopCommerce's
        // CategoryService assigns the factory's result to a local and passes that. Recognising the factory
        // only at the outermost expression left such a site wholly unresolved even though its template and
        // its factory were both in the compilation.
        if (FindFactory(expression, semanticModel, keyObject, cancellationToken) is var (factoryCall, factory))
            return await FoldFactoryAsync(factoryCall, factory, semanticModel, keyObject, hops, cancellationToken);

        var unwrapped = StringConstantFolder.Unwrap(expression);
        if (unwrapped is BaseObjectCreationExpressionSyntax creation)
        {
            var argument = creation.ArgumentList?.Arguments.ElementAtOrDefault(keyObject.TemplateArgumentIndex)?.Expression;
            return argument is null
                ? UnknownSet($"The {keyObject.TypeName} construction carries no template at argument {keyObject.TemplateArgumentIndex}.")
                : await _folder.FoldAsync(argument, semanticModel, cancellationToken);
        }

        // A conditional is a branch written as one expression, and a key object may be built by one exactly
        // as a string may. Both arms are followed and united; neither is preferred. It costs no hop, because
        // an arm is strictly smaller syntax and the bound is on declarations followed, not on nesting.
        if (unwrapped is ConditionalExpressionSyntax conditional)
        {
            return _folder.Union(await FoldTemplateAsync(conditional.WhenTrue, semanticModel, keyObject, hops, cancellationToken),
                                 await FoldTemplateAsync(conditional.WhenFalse, semanticModel, keyObject, hops, cancellationToken));
        }

        if (unwrapped is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            return UnknownSet($"The {keyObject.TypeName} key could not be followed to a construction.");

        var (declarations, collapsed) = FindDeclarations(semanticModel.GetSymbolInfo(unwrapped, cancellationToken).Symbol,
                                                         semanticModel, cancellationToken);
        if (declarations.Count == 0)
            return UnknownSet($"The {keyObject.TypeName} key has no source initializer to follow.");

        var folded = new FoldedSet[declarations.Count];
        for (var index = 0; index < declarations.Count; index++)
        {
            var declarationModel = await GetSemanticModelAsync(declarations[index], semanticModel, cancellationToken);
            folded[index] = declarationModel is null
                ? UnknownSet($"The {keyObject.TypeName} key's initializer could not be resolved.")
                : await FoldTemplateAsync(declarations[index], declarationModel, keyObject, hops + 1, cancellationToken);
        }

        var union = _folder.Union(folded);
        return collapsed ? union with { Collapsed = true } : union;
    }

    /// <summary>Every expression a property, field or local may hold, and whether that set is a choice the
    /// fold could not name. A key object reached through a local assigned in several places is the same
    /// branch a string local is (<c>docs/adr/0015</c>): all of its values are followed, so the site names the
    /// set of templates it may key on rather than the first one written. A compound assignment is not a value
    /// the fold can read, so it collapses the set rather than leaving the initialiser standing alone as a
    /// certainty the site may never produce.</summary>
    private static (IReadOnlyList<ExpressionSyntax> Values, bool Collapsed) FindDeclarations(
        ISymbol? symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (symbol is not (IPropertySymbol or IFieldSymbol or ILocalSymbol))
            return ([], false);

        var values = symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                           .Select(syntax => syntax switch
                           {
                               PropertyDeclarationSyntax { ExpressionBody.Expression: { } body } => body,
                               PropertyDeclarationSyntax { Initializer.Value: { } value } => value,
                               VariableDeclaratorSyntax { Initializer.Value: { } value } => value,
                               _ => null
                           })
                           .OfType<ExpressionSyntax>()
                           .ToList();

        if (symbol is not ILocalSymbol local)
            return (values, false);

        var writes = local.ContainingSymbol.DeclaringSyntaxReferences
                          .SelectMany(reference => reference.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                          .Where(assignment => SymbolEqualityComparer.Default.Equals(
                              semanticModel.Compilation.GetSemanticModel(assignment.Left.SyntaxTree)
                                           .GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local))
                          .ToArray();
        values.AddRange(writes.Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                              .Select(assignment => assignment.Right));
        return (values, writes.Any(assignment => !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)));
    }

    private static (InvocationExpressionSyntax Invocation, KeyObjectFactory Factory)? FindFactory(
        ExpressionSyntax expression, SemanticModel semanticModel, KeyObjectRecognizer keyObject,
        CancellationToken cancellationToken)
    {
        if (StringConstantFolder.Unwrap(expression) is not InvocationExpressionSyntax invocation ||
            semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        // The same type walk the recognizer itself is matched by, so a factory declared on an interface is
        // found through the type the caller actually holds.
        var names = CacheCallAnalyzer.GetApiTypes(method, null).Select(CacheCallAnalyzer.GetFullName).ToHashSet(StringComparer.Ordinal);
        var factory = keyObject.Factories.FirstOrDefault(candidate => names.Contains(candidate.TypeName) &&
                                                                      candidate.Methods.Contains(method.Name, StringComparer.Ordinal));
        return factory is null ? null : (invocation, factory);
    }

    private static bool IsKeyObject(ExpressionSyntax expression, SemanticModel semanticModel,
                                    KeyObjectRecognizer keyObject, CancellationToken cancellationToken)
    {
        if (semanticModel.GetTypeInfo(StringConstantFolder.Unwrap(expression), cancellationToken).Type is not INamedTypeSymbol type)
            return false;

        for (var candidate = type.OriginalDefinition; candidate is not null; candidate = candidate.BaseType?.OriginalDefinition)
        {
            if (CacheCallAnalyzer.GetFullName(candidate) == keyObject.TypeName)
                return true;
        }

        return false;
    }

    private async Task<SemanticModel?> GetSemanticModelAsync(SyntaxNode syntax, SemanticModel current, CancellationToken cancellationToken)
    {
        if (syntax.SyntaxTree == current.SyntaxTree)
            return current;

        var document = solution.GetDocument(syntax.SyntaxTree);
        if (document is not null)
            return await document.GetSemanticModelAsync(cancellationToken);

        return current.Compilation.SyntaxTrees.Contains(syntax.SyntaxTree)
            ? current.Compilation.GetSemanticModel(syntax.SyntaxTree)
            : null;
    }

    private static FoldedSet UnknownSet(string reason) => StringConstantFolder.One(
        new FoldedString(PLACEHOLDERS.Unknown, [new FoldedPart(FoldedPartKind.Unknown, PLACEHOLDERS.Unknown, 0, null, reason)], reason));
}
