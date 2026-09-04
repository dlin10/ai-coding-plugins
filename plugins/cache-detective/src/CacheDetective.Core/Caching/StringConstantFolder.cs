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
}

/// <summary>How the policy above the folder writes the parts it could not prove into the folded value.</summary>
internal sealed record FoldedPlaceholders(Func<string, string> Named, string Unknown);

/// <summary>
/// Folds a string-valued expression to as much text as the compilation can prove: literals, constants,
/// static readonly fields, interpolation, <c>string.Format</c>/<c>Concat</c>/<c>Join</c>, concatenation,
/// and a bounded number of hops through helper methods that build the string. Everything it cannot prove
/// becomes a placeholder part that carries its own place in the folded value.
/// </summary>
internal sealed class StringConstantFolder(Solution solution, FoldedPlaceholders placeholders)
{
    private const int MAXIMUM_HELPER_HOPS = 5;
    private static readonly Regex FORMAT_ITEM = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<FoldedString> FoldAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                        CancellationToken cancellationToken) =>
        FoldAsync(new BoundExpression(expression, semanticModel, EmptyBindings()), 0,
                  new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);

    private async Task<FoldedString> FoldAsync(BoundExpression bound, int helperHops,
                                               HashSet<ISymbol> visiting,
                                               CancellationToken cancellationToken)
    {
        var expression = Unwrap(bound.Expression);
        var constant = bound.SemanticModel.GetConstantValue(expression, cancellationToken);
        if (constant.HasValue && constant.Value is not null)
        {
            return Literal(Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return expression switch
        {
            InterpolatedStringExpressionSyntax interpolated =>
                await FoldInterpolatedAsync(interpolated, bound, helperHops, visiting, cancellationToken),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                await FoldBinaryAsync(binary, bound, helperHops, visiting, cancellationToken),
            InvocationExpressionSyntax invocation =>
                await FoldInvocationAsync(invocation, bound, helperHops, visiting, cancellationToken),
            IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                await FoldSymbolAsync(expression, bound, helperHops, visiting, cancellationToken),
            _ => Unknown("The key expression could not be reduced to a supported form.")
        };
    }

    private async Task<FoldedString> FoldInterpolatedAsync(InterpolatedStringExpressionSyntax interpolated, BoundExpression bound, int helperHops,
                                                           HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var result = Empty();

        foreach (var content in interpolated.Contents)
        {
            var part = content switch
            {
                InterpolatedStringTextSyntax text => Literal(text.TextToken.ValueText),
                InterpolationSyntax interpolation => await FoldAsync(
                    bound with { Expression = interpolation.Expression }, helperHops, visiting,
                    cancellationToken),
                _ => Unknown("The interpolated-string part could not be reduced.")
            };
            result = Combine(result, part);
        }

        return result;
    }

    private async Task<FoldedString> FoldBinaryAsync(BinaryExpressionSyntax binary, BoundExpression bound, int helperHops,
                                                     HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var left = await FoldAsync(bound with { Expression = binary.Left }, helperHops, visiting,
                                   cancellationToken);
        var right = await FoldAsync(bound with { Expression = binary.Right }, helperHops, visiting,
                                    cancellationToken);
        return Combine(left, right);
    }

    private async Task<FoldedString> FoldInvocationAsync(InvocationExpressionSyntax invocation, BoundExpression bound, int helperHops,
                                                         HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        if (bound.SemanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
        {
            return Unknown("The invoked key builder could not be resolved.");
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
                _ => Unknown($"String method {operation.TargetMethod.Name} is not a supported key builder.")
            };
        }

        if (!operation.TargetMethod.Locations.Any(location => location.IsInSource))
        {
            return Unknown("The invoked key builder has no source declaration.");
        }

        if (helperHops >= MAXIMUM_HELPER_HOPS)
        {
            return Unknown($"The key-builder hop limit of {MAXIMUM_HELPER_HOPS} was reached.");
        }

        if (!visiting.Add(operation.TargetMethod))
        {
            return Unknown("The key-builder call chain contains a cycle.");
        }

        var bindings = BindArguments(operation, bound);
        var returns = new List<FoldedString>();

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
        if (returns.Count == 0)
        {
            return Unknown("The key builder has no reducible return expression.");
        }

        var first = returns[0];
        return returns.All(candidate => candidate.Value == first.Value &&
                                        candidate.HasLiteralPart == first.HasLiteralPart)
            ? first
            : Unknown("The key builder can return more than one template.");
    }

    private async Task<FoldedString> FoldFormatAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                     HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var arguments = GetExplicitArgumentExpressions(operation).ToArray();
        if (arguments.Length == 0 ||
            bound.SemanticModel.GetConstantValue(arguments[0], cancellationToken) is not
                { HasValue: true, Value: string format })
        {
            return Unknown("string.Format requires a literal format string.");
        }

        var values = FlattenArguments(arguments.Skip(1)).ToArray();
        var foldedValues = new FoldedString[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            foldedValues[index] = await FoldAsync(bound with { Expression = values[index] }, helperHops,
                                                  visiting, cancellationToken);
        }

        var result = Empty();
        var position = 0;
        foreach (Match match in FORMAT_ITEM.Matches(format))
        {
            result = Combine(result, Literal(UnescapeBraces(format[position..match.Index])));
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            result = Combine(result, index < foldedValues.Length
                ? foldedValues[index]
                : Unknown("The format item has no matching argument."));
            position = match.Index + match.Length;
        }

        return Combine(result, Literal(UnescapeBraces(format[position..])));
    }

    private async Task<FoldedString> FoldConcatAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                     HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var result = Empty();
        foreach (var expression in FlattenArguments(GetExplicitArgumentExpressions(operation)))
        {
            result = Combine(result, await FoldAsync(bound with { Expression = expression }, helperHops,
                                                     visiting, cancellationToken));
        }

        return result;
    }

    private async Task<FoldedString> FoldJoinAsync(IInvocationOperation operation, BoundExpression bound, int helperHops,
                                                   HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var arguments = GetExplicitArgumentExpressions(operation).ToArray();
        if (arguments.Length < 2 ||
            bound.SemanticModel.GetConstantValue(arguments[0], cancellationToken) is not
                { HasValue: true, Value: string separator })
        {
            return Unknown("string.Join requires a literal separator and values.");
        }

        var values = FlattenArguments(arguments.Skip(1)).ToArray();
        var result = Empty();
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                result = Combine(result, Literal(separator));
            }

            result = Combine(result, await FoldAsync(bound with { Expression = values[index] }, helperHops,
                                                     visiting, cancellationToken));
        }

        return result;
    }

    private async Task<FoldedString> FoldSymbolAsync(ExpressionSyntax expression, BoundExpression bound, int helperHops,
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

                return Substitution(parameter.Name);
            case ILocalSymbol local:
                return Substitution(local.Name);
            case IPropertySymbol property:
                return Substitution(property.Name);
            case IFieldSymbol { IsStatic: true, IsReadOnly: true } field:
                var fieldPath = new HashSet<ISymbol>(visiting, SymbolEqualityComparer.Default);
                if (!fieldPath.Add(field))
                {
                    return Unknown("The static key field contains an initialization cycle.");
                }

                var initializer = field.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                                       .Select(GetInitializer)
                                       .FirstOrDefault(candidate => candidate is not null);
                if (initializer is null)
                {
                    return Unknown("The static readonly key field has no source initializer.");
                }

                var semanticModel = await GetSemanticModelAsync(initializer, bound.SemanticModel,
                                                                cancellationToken);
                return semanticModel is null
                    ? Unknown("The static readonly key field could not be resolved.")
                    : await FoldAsync(new BoundExpression(initializer, semanticModel, bound.Bindings),
                                      helperHops, fieldPath, cancellationToken);
            default:
                return Unknown("The key substitution could not be resolved.");
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

    private static IEnumerable<ExpressionSyntax> FlattenArguments(IEnumerable<ExpressionSyntax> expressions)
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

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
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

    private static FoldedString Empty() => new(string.Empty, [], null);

    private static FoldedString Literal(string value) => value.Length == 0
        ? Empty()
        : new(value, [new FoldedPart(FoldedPartKind.Literal, value, 0)], null);

    private FoldedString Substitution(string name)
    {
        var text = placeholders.Named(name);
        return new(text, [new FoldedPart(FoldedPartKind.Substitution, text, 0, name)], null);
    }

    private FoldedString Unknown(string reason) => new(placeholders.Unknown,
                                                       [new FoldedPart(FoldedPartKind.Unknown, placeholders.Unknown, 0, null, reason)], reason);

    /// <summary>Appends one folded string to another, moving the appended parts to their new places.</summary>
    private static FoldedString Combine(FoldedString left, FoldedString right) => new(left.Value + right.Value,
                                                                                      [..left.Parts, ..right.Parts.Select(part => part with { Start = part.Start + left.Value.Length })],
                                                                                      left.Reason ?? right.Reason);

    private static string UnescapeBraces(string value) => value.Replace("{{", "{", StringComparison.Ordinal)
                                                                        .Replace("}}", "}",
                                                                            StringComparison.Ordinal);

    private static IReadOnlyDictionary<ISymbol, BoundExpression> EmptyBindings() => new Dictionary<ISymbol, BoundExpression>(SymbolEqualityComparer.Default);

    private sealed record BoundExpression(ExpressionSyntax Expression, SemanticModel SemanticModel,
                                          IReadOnlyDictionary<ISymbol, BoundExpression> Bindings);
}
