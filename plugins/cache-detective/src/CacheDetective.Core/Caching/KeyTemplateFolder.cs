using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Caching;

/// <summary>A folded cache key: the template, whether it holds a literal segment, and the substitutions
/// with their places in the template.</summary>
internal sealed record KeyTemplateResult(string Template, bool HasLiteralSegment, string? Reason,
                                         IReadOnlyList<FoldedPart> Parts);

/// <summary>The key-template policy over <see cref="StringConstantFolder"/>: a known substitution becomes
/// <c>{name}</c>, an unknown one <c>{?}</c>, and a key with no literal segment is not a template at all.</summary>
internal sealed class KeyTemplateFolder(Solution solution)
{
    private static readonly FoldedPlaceholders PLACEHOLDERS = new(name => $"{{{name}}}", "{?}");

    private readonly StringConstantFolder _folder = new(solution, PLACEHOLDERS);

    public async Task<KeyTemplateResult> FoldAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                                   CancellationToken cancellationToken)
    {
        var folded = await _folder.FoldAsync(expression, semanticModel, cancellationToken);
        return new KeyTemplateResult(folded.Value, folded.HasLiteralPart, folded.Reason, folded.Parts);
    }
}
