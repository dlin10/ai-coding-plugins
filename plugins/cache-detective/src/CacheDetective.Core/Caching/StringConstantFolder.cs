using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CacheDetective.Caching;

internal enum FoldedPartKind
{
    Literal,
    Substitution,
    Unknown
}

/// <summary>One part of a folded string: its text, where that text lies in the folded value, and, when the
/// folder could not prove the text, the name it substituted or the reason it could not.</summary>
internal sealed record FoldedPart(FoldedPartKind Kind, string Text, int Start, string? Name = null,
                                  string? Reason = null)
{
    public int Length => Text.Length;
}

/// <summary>A string reduced as far as the compilation can prove it, part by part.</summary>
internal sealed record FoldedString(string Value, IReadOnlyList<FoldedPart> Parts, string? Reason)
{
    /// <summary>Whether any part of the value was proved literally.</summary>
    public bool HasLiteralPart => Parts.Any(part => part.Kind == FoldedPartKind.Literal);

    /// <summary>Whether some part of the value could not be named at all.</summary>
    public bool HasUnknownPart => Parts.Any(part => part.Kind == FoldedPartKind.Unknown);
}

/// <summary>
/// Every value a site may produce, ordered by ordinal comparison of the folded values so that the order
/// a reader and a vertex both see is the same one twice running.
/// <para><see cref="Collapsed"/> is what separates a fold that <em>named</em> its values from one that
/// merely produced some: it is set when the set outgrew its bound, when a member could not be named, or
/// when the value came from a variable that builds itself. A composite over a collapsed part is itself
/// collapsed even when it holds one element — a nine-valued local past the bound becomes one <c>{?}</c>,
/// and <c>$"key:{choice}"</c> around it becomes the single template <c>key:{?}</c>, which without the
/// mark cannot be told from a site that always writes that key. See <c>docs/adr/0015</c>.</para>
/// </summary>
internal sealed record FoldedSet(IReadOnlyList<FoldedString> Values, bool Collapsed)
{
    /// <summary>The one value, for a consumer that cannot carry a set.</summary>
    public FoldedString Single => Values[0];
}

/// <summary>How the policy above the folder writes the parts it could not prove into the folded value.</summary>
internal sealed record FoldedPlaceholders(Func<string, string> Named, string Unknown);

/// <summary>
/// Folds a string-valued expression to as much text as the compilation can prove: literals, constants,
/// static readonly fields, interpolation, <c>string.Format</c>/<c>Concat</c>/<c>Join</c>, concatenation,
/// and a bounded number of hops through helper methods that build the string. Everything it cannot prove
/// becomes a placeholder part that carries its own place in the folded value.
/// </summary>
internal sealed class StringConstantFolder(Solution solution, FoldedPlaceholders placeholders,
                                           int maximumValues = StringConstantFolder.MAXIMUM_VALUES)
{
    private const int MAXIMUM_HELPER_HOPS = 5;

    /// <summary>How many values a fold may name before it stops naming them. See <see cref="Make"/>.
    /// <para>A consumer that cannot take a set at all passes 1, which is not a special case but the same
    /// bound at its floor: every piece that stands for several collapses to one unknown where it arises,
    /// and the composite around it keeps its literals. That is what the folder did before it returned sets,
    /// and it is how <see cref="Data.SqlTextFolder"/> keeps exactly that behaviour.</para></summary>
    internal const int MAXIMUM_VALUES = 8;

    /// <summary>Written once and read once: the local case recognizes its own cycle marker to tell a
    /// variable that builds itself from one that simply branches.</summary>
    private const string CYCLE_REASON = "The local string value contains an initialization cycle.";

    /// <summary>A positional hole, <c>{0}</c> or <c>{0,-4:x}</c>, and never a doubled brace. Shared with the
    /// key-object policy in <see cref="KeyTemplateFolder"/>, which substitutes into the same holes.</summary>
    internal static readonly Regex FORMAT_ITEM = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<FoldedSet> FoldAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                     CancellationToken cancellationToken) =>
        FoldAsync(new BoundExpression(expression, semanticModel, EmptyBindings()), 0,
                  new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);

    private async Task<FoldedSet> FoldAsync(BoundExpression bound, int helperHops,
                                            HashSet<ISymbol> visiting,
                                            CancellationToken cancellationToken)
    {
        var expression = Unwrap(bound.Expression);
        var constant = bound.SemanticModel.GetConstantValue(expression, cancellationToken);
        if (constant.HasValue && constant.Value is not null)
        {
            return One(Literal(Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return expression switch
        {
            InterpolatedStringExpressionSyntax interpolated =>
                await FoldInterpolatedAsync(interpolated, bound, helperHops, visiting, cancellationToken),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                await FoldBinaryAsync(binary, bound, helperHops, visiting, cancellationToken),
            // The same branching as a local assigned in two places, written differently. Reading one and
            // not the other would leave a distinction no corpus can see.
            ConditionalExpressionSyntax conditional =>
                Union(await FoldAsync(bound with { Expression = conditional.WhenTrue }, helperHops, visiting, cancellationToken),
                      await FoldAsync(bound with { Expression = conditional.WhenFalse }, helperHops, visiting, cancellationToken)),
            InvocationExpressionSyntax invocation =>
                await FoldInvocationAsync(invocation, bound, helperHops, visiting, cancellationToken),
            IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                await FoldSymbolAsync(expression, bound, helperHops, visiting, cancellationToken),
            _ => UnknownSet("The key expression could not be reduced to a supported form.")
        };
    }

    private async Task<FoldedSet> FoldInterpolatedAsync(InterpolatedStringExpressionSyntax interpolated, BoundExpression bound, int helperHops,
                                                        HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var result = EmptySet();

        foreach (var content in interpolated.Contents)
        {
            var part = content switch
            {
                // ValueText hands back the braces as written, doubled and all — it decodes the string
                // escapes and not the interpolation's own. A constant interpolation never arrives here,
                // because GetConstantValue above answers it with the value the compiler already decoded, so
                // the only templates that reach this line are the ones where the escape still has to be
                // read. nopCommerce's NopEntityCacheDefaults<TEntity> writes exactly that shape, and a
                // {{0}} left doubled matches no format item at all, so the factory substitutes into nothing
                // and drops its argument in silence.
                InterpolatedStringTextSyntax text => One(Literal(UnescapeBraces(text.TextToken.ValueText))),
                InterpolationSyntax interpolation => await FoldAsync(
                    bound with { Expression = interpolation.Expression }, helperHops, visiting,
                    cancellationToken),
                _ => UnknownSet("The interpolated-string part could not be reduced.")
            };
            result = Product(result, part);
        }

        return result;
    }

    private async Task<FoldedSet> FoldBinaryAsync(BinaryExpressionSyntax binary, BoundExpression bound, int helperHops,
                                                  HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var left = await FoldAsync(bound with { Expression = binary.Left }, helperHops, visiting,
                                   cancellationToken);
        var right = await FoldAsync(bound with { Expression = binary.Right }, helperHops, visiting,
                                    cancellationToken);
        return Product(left, right);
    }

    private async Task<FoldedSet> FoldInvocationAsync(InvocationExpressionSyntax invocation, BoundExpression bound, int helperHops,
                                                      HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        if (bound.SemanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
        {
            return UnknownSet("The invoked key builder could not be resolved.");
        }

        if (operation.TargetMethod.ContainingType.SpecialType == SpecialType.System_String)
        {
            return operation.TargetMethod.Name switch
            {
                "Format" => await FoldFormatAsync(operation, bound, helperHops, visiting,
                                                   cancellationToken),
                "Concat" => await FoldConcatAsync(operation, bound, helperHops, visiting,
                                                   cancellationToken),
                "Join" => await FoldJoinAsync(operation, bound, helperHops, visiting,
                                               cancellationToken),
                _ => UnknownSet($"String method {operation.TargetMethod.Name} is not a supported key builder.")
            };
        }

        if (!operation.TargetMethod.Locations.Any(location => location.IsInSource))
        {
            return UnknownSet("The invoked key builder has no source declaration.");
        }

        if (helperHops >= MAXIMUM_HELPER_HOPS)
        {
            return UnknownSet($"The key-builder hop limit of {MAXIMUM_HELPER_HOPS} was reached.");
        }

        if (!visiting.Add(operation.TargetMethod))
        {
            return UnknownSet("The key-builder call chain contains a cycle.");
        }

        var bindings = BindArguments(operation, bound);
        var returns = new List<FoldedSet>();

        foreach (var syntaxReference in operation.TargetMethod.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
            var semanticModel = await GetSemanticModelAsync(declaration, bound.SemanticModel,
                                                            cancellationToken);
            if (semanticModel is null)
            {
                continue;
            }

            foreach (var returnExpression in GetReturnExpressions(declaration))
            {
                returns.Add(await FoldAsync(new BoundExpression(returnExpression, semanticModel, bindings),
                                            helperHops + 1,
                                            new HashSet<ISymbol>(visiting, SymbolEqualityComparer.Default),
                                            cancellationToken));
            }
        }

        visiting.Remove(operation.TargetMethod);
        // Two returns are two values the site may produce, exactly as two assignments are.
        return returns.Count == 0
            ? UnknownSet("The key builder has no reducible return expression.")
            : Union([.. returns]);
    }

    private async Task<FoldedSet> FoldFormatAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                  HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var arguments = GetExplicitArgumentExpressions(operation).ToArray();
        if (arguments.Length == 0 ||
            bound.SemanticModel.GetConstantValue(arguments[0], cancellationToken) is not
                { HasValue: true, Value: string format })
        {
            return UnknownSet("string.Format requires a literal format string.");
        }

        var values = FlattenArguments(arguments.Skip(1)).ToArray();
        var foldedValues = new FoldedSet[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            foldedValues[index] = await FoldAsync(bound with { Expression = values[index] }, helperHops,
                                                  visiting, cancellationToken);
        }

        var result = EmptySet();
        var position = 0;
        foreach (Match match in FORMAT_ITEM.Matches(format))
        {
            result = Product(result, One(Literal(UnescapeBraces(format[position..match.Index]))));
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            result = Product(result, index < foldedValues.Length
                ? foldedValues[index]
                : UnknownSet("The format item has no matching argument."));
            position = match.Index + match.Length;
        }

        return Product(result, One(Literal(UnescapeBraces(format[position..]))));
    }

    private async Task<FoldedSet> FoldConcatAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                  HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var result = EmptySet();
        foreach (var expression in FlattenArguments(GetExplicitArgumentExpressions(operation)))
        {
            result = Product(result, await FoldAsync(bound with { Expression = expression }, helperHops,
                                                     visiting, cancellationToken));
        }

        return result;
    }

    private async Task<FoldedSet> FoldJoinAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var arguments = GetExplicitArgumentExpressions(operation).ToArray();
        if (arguments.Length < 2 ||
            bound.SemanticModel.GetConstantValue(arguments[0], cancellationToken) is not
                { HasValue: true, Value: string separator })
        {
            return UnknownSet("string.Join requires a literal separator and values.");
        }

        var values = FlattenArguments(arguments.Skip(1)).ToArray();
        var result = EmptySet();
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                result = Product(result, One(Literal(separator)));
            }

            result = Product(result, await FoldAsync(bound with { Expression = values[index] }, helperHops,
                                                     visiting, cancellationToken));
        }

        return result;
    }

    private async Task<FoldedSet> FoldSymbolAsync(ExpressionSyntax expression, BoundExpression bound, int helperHops,
                                                  HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var symbol = bound.SemanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
        switch (symbol)
        {
            case IParameterSymbol parameter:
                if (bound.Bindings.TryGetValue(parameter, out var argument))
                {
                    return await FoldAsync(argument, helperHops, visiting, cancellationToken);
                }

                return One(Substitution(parameter.Name));
            case ILocalSymbol local:
                var localPath = new HashSet<ISymbol>(visiting, SymbolEqualityComparer.Default);
                if (!localPath.Add(local))
                {
                    return UnknownSet(CYCLE_REASON);
                }

                var writes = local.ContainingSymbol.DeclaringSyntaxReferences
                                  .SelectMany(reference => reference.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                                  .Where(assignment => SymbolEqualityComparer.Default.Equals(
                                      bound.SemanticModel.Compilation.GetSemanticModel(assignment.Left.SyntaxTree)
                                           .GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local))
                                  .ToArray();
                // A compound assignment is not a value the fold can read, and the folder never has: it
                // simply does not see one. What it must not do is call the initialiser alone certain,
                // because the site may never produce it. See docs/adr/0015.
                var selfUpdated = writes.Any(assignment => !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression));
                var values = local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                                  .OfType<VariableDeclaratorSyntax>().Select(declarator => declarator.Initializer?.Value)
                                  .Where(value => value is not null).Cast<ExpressionSyntax>()
                                  .Concat(writes.Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                                                .Select(assignment => assignment.Right))
                                  .ToArray();
                if (values.Length == 0)
                {
                    return Mark(One(Substitution(local.Name)), selfUpdated);
                }
                if (values.Length == 1 && bound.SemanticModel.GetSymbolInfo(values[0], cancellationToken).Symbol is IParameterSymbol localParameter &&
                    !bound.Bindings.ContainsKey(localParameter))
                {
                    return Mark(One(Substitution(local.Name)), selfUpdated);
                }

                var folded = new List<FoldedSet>();
                foreach (var value in values)
                {
                    var valueModel = await GetSemanticModelAsync(value, bound.SemanticModel, cancellationToken);
                    if (valueModel is not null)
                        folded.Add(await FoldAsync(new BoundExpression(value, valueModel, bound.Bindings), helperHops, localPath, cancellationToken));
                }

                // A local that builds itself is a loop, not a branch, and a loop has no finite set of
                // values. It keeps exactly the outcome it had before this became a set.
                if (folded.Any(candidate => candidate.Values.Any(value => value.Parts.Any(part => part.Reason == CYCLE_REASON))))
                    return new FoldedSet([Unknown($"assigned differently in {values.Length} places")], true);

                return Mark(Union([.. folded]), selfUpdated);
            case IPropertySymbol property:
                return One(Substitution(property.Name));
            case IFieldSymbol { IsReadOnly: true } field:
                var fieldPath = new HashSet<ISymbol>(visiting, SymbolEqualityComparer.Default);
                if (!fieldPath.Add(field))
                {
                    return UnknownSet("The static key field contains an initialization cycle.");
                }

                var initializer = field.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                                       .Select(GetInitializer).FirstOrDefault(candidate => candidate is not null);
                if (initializer is null && !field.IsStatic)
                {
                    var assignments = field.ContainingType.InstanceConstructors.SelectMany(constructor => constructor.DeclaringSyntaxReferences)
                                           .SelectMany(reference => reference.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                                           .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                                               SymbolEqualityComparer.Default.Equals(bound.SemanticModel.Compilation.GetSemanticModel(assignment.Left.SyntaxTree)
                                                   .GetSymbolInfo(assignment.Left, cancellationToken).Symbol, field))
                                           .Select(assignment => assignment.Right).Take(2).ToArray();
                    initializer = assignments.Length == 1 ? assignments[0] : null;
                }
                if (initializer is null)
                {
                    return UnknownSet("The static readonly key field has no source initializer.");
                }

                var semanticModel = await GetSemanticModelAsync(initializer, bound.SemanticModel,
                                                                cancellationToken);
                return semanticModel is null
                    ? UnknownSet("The static readonly key field could not be resolved.")
                    : await FoldAsync(new BoundExpression(initializer, semanticModel, bound.Bindings),
                                      helperHops, fieldPath, cancellationToken);
            default:
                return UnknownSet("The key substitution could not be resolved.");
        }
    }

    private async Task<SemanticModel?> GetSemanticModelAsync(SyntaxNode syntax, SemanticModel current, CancellationToken cancellationToken)
    {
        if (syntax.SyntaxTree == current.SyntaxTree)
        {
            return current;
        }

        var document = solution.GetDocument(syntax.SyntaxTree);
        if (document is not null)
        {
            return await document.GetSemanticModelAsync(cancellationToken);
        }

        return current.Compilation.SyntaxTrees.Contains(syntax.SyntaxTree)
            ? current.Compilation.GetSemanticModel(syntax.SyntaxTree)
            : null;
    }

    private static IReadOnlyDictionary<ISymbol, BoundExpression> BindArguments(IInvocationOperation operation, BoundExpression caller)
    {
        var bindings = new Dictionary<ISymbol, BoundExpression>(SymbolEqualityComparer.Default);
        foreach (var argument in operation.Arguments.Where(argument => !argument.IsImplicit &&
                                                                       argument.Parameter is not null))
        {
            var expression = GetArgumentExpression(argument);
            if (expression is not null)
            {
                bindings[argument.Parameter!] = new BoundExpression(expression, caller.SemanticModel, caller.Bindings);
            }
        }

        return bindings;
    }

    private static IEnumerable<ExpressionSyntax> GetExplicitArgumentExpressions(IInvocationOperation operation) =>
        operation.Syntax is InvocationExpressionSyntax invocation
            ? invocation.ArgumentList.Arguments.Select(argument => argument.Expression)
            : operation.Arguments.Where(argument => !argument.IsImplicit)
                       .Select(GetArgumentExpression)
                       .Where(expression => expression is not null)
                       .Cast<ExpressionSyntax>();

    private static ExpressionSyntax? GetArgumentExpression(IArgumentOperation argument) =>
        argument.Syntax is ArgumentSyntax argumentSyntax
            ? argumentSyntax.Expression
            : argument.Value.Syntax as ExpressionSyntax;

    /// <summary>Spreads an explicit array or collection argument into the values it holds, so that a
    /// <c>params</c> parameter reads the same whether the caller wrote the elements or the array.</summary>
    internal static IEnumerable<ExpressionSyntax> FlattenArguments(IEnumerable<ExpressionSyntax> expressions)
    {
        foreach (var expression in expressions)
        {
            var initializer = expression switch
            {
                ArrayCreationExpressionSyntax array => array.Initializer,
                ImplicitArrayCreationExpressionSyntax array => array.Initializer,
                CollectionExpressionSyntax collection => null,
                _ => null
            };

            if (expression is CollectionExpressionSyntax collectionExpression)
            {
                foreach (var element in collectionExpression.Elements.OfType<ExpressionElementSyntax>())
                {
                    yield return element.Expression;
                }
            }
            else if (initializer is not null)
            {
                foreach (var element in initializer.Expressions)
                {
                    yield return element;
                }
            }
            else
            {
                yield return expression;
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> GetReturnExpressions(SyntaxNode declaration)
    {
        switch (declaration)
        {
            case MethodDeclarationSyntax { ExpressionBody.Expression: { } expression }:
                yield return expression;
                yield break;
            case LocalFunctionStatementSyntax { ExpressionBody.Expression: { } expression }:
                yield return expression;
                yield break;
        }

        foreach (var returnStatement in declaration.DescendantNodes(node =>
                     node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                 .OfType<ReturnStatementSyntax>())
        {
            if (returnStatement.Expression is not null)
            {
                yield return returnStatement.Expression;
            }
        }
    }

    internal static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                CastExpressionSyntax cast => cast.Expression,
                _ => expression
            };
        }

        return expression;
    }

    private static ExpressionSyntax? GetInitializer(SyntaxNode syntax) => syntax switch
    {
        VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
        _ => null
    };

    internal static FoldedString Empty() => new(string.Empty, [], null);

    internal static FoldedString Literal(string value) => value.Length == 0
        ? Empty()
        : new(value, [new FoldedPart(FoldedPartKind.Literal, value, 0)], null);

    private FoldedString Substitution(string name)
    {
        var text = placeholders.Named(name);
        return new(text, [new FoldedPart(FoldedPartKind.Substitution, text, 0, name)], null);
    }

    private FoldedString Unknown(string reason) => new(placeholders.Unknown,
                                                       [new FoldedPart(FoldedPartKind.Unknown, placeholders.Unknown, 0, null, reason)], reason);

    internal static FoldedSet One(FoldedString value) => new([value], value.HasUnknownPart);

    internal static FoldedSet EmptySet() => One(Empty());

    internal FoldedSet UnknownSet(string reason) => One(Unknown(reason));

    private static FoldedSet Mark(FoldedSet set, bool collapsed) => collapsed ? set with { Collapsed = true } : set;

    /// <summary>
    /// The one way a set is built: de-duplicated on the folded value, ordered ordinally because the order
    /// reaches vertex creation, and capped on the <em>result</em> rather than on the number of branches —
    /// two three-valued parts would otherwise pass a branch count and produce nine.
    /// <para>Over the cap the fold gives back today's single unknown, but with a reason naming the limit
    /// rather than the branch, so a reader can tell a bound reached from an expression never understood.
    /// </para>
    /// </summary>
    internal FoldedSet Make(IEnumerable<FoldedString> values, bool collapsed)
    {
        var distinct = new List<FoldedString>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (seen.Add(value.Value)) distinct.Add(value);
        }

        if (distinct.Count > maximumValues)
            return new FoldedSet([Unknown($"the fold reached its limit of {maximumValues} values")], true);

        distinct.Sort((left, right) => string.CompareOrdinal(left.Value, right.Value));
        // A piece beyond the fold collapses the set wherever it appears. A member that names an argument
        // and nothing else collapses it only among others — alone it is an ordinary part of a composite,
        // but beside a member that is a real template it is the mixed set: one of the values the site may
        // produce cannot be written down.
        var unnamed = distinct.Any(value => value.HasUnknownPart) ||
                      (distinct.Count > 1 && distinct.Any(Unnameable));
        return new FoldedSet(distinct, collapsed || unnamed);
    }

    /// <summary>Whether a value stands for something without ever saying what. The empty string does not:
    /// it has no parts because there was nothing to prove, not because proving failed.</summary>
    private static bool Unnameable(FoldedString value) => value.Parts.Count > 0 && !value.HasLiteralPart;

    internal FoldedSet Union(params FoldedSet[] sets) =>
        Make(sets.SelectMany(set => set.Values), sets.Any(set => set.Collapsed));

    /// <summary>Every pairing of a left value with a right one. A composite over a collapsed part is
    /// collapsed even when it holds one element, which is the whole reason the mark exists.</summary>
    internal FoldedSet Product(FoldedSet left, FoldedSet right) =>
        Make(left.Values.SelectMany(first => right.Values.Select(second => Combine(first, second))),
             left.Collapsed || right.Collapsed);

    /// <summary>Appends one folded string to another, moving the appended parts to their new places.</summary>
    internal static FoldedString Combine(FoldedString left, FoldedString right) => new(left.Value + right.Value,
                                                                                      [..left.Parts, ..right.Parts.Select(part => part with { Start = part.Start + left.Value.Length })],
                                                                                      left.Reason ?? right.Reason);

    private static string UnescapeBraces(string value) => value.Replace("{{", "{", StringComparison.Ordinal)
                                                                        .Replace("}}", "}",
                                                                            StringComparison.Ordinal);

    private static IReadOnlyDictionary<ISymbol, BoundExpression> EmptyBindings() => new Dictionary<ISymbol, BoundExpression>(SymbolEqualityComparer.Default);

    private sealed record BoundExpression(ExpressionSyntax Expression, SemanticModel SemanticModel,
                                          IReadOnlyDictionary<ISymbol, BoundExpression> Bindings);
}
