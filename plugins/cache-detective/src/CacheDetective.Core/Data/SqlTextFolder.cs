using System.Text;
using CacheDetective.Caching;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CacheDetective.Data;

/// <summary>SQL folded for the parser: the text, and the parameters standing in for the fragments the fold
/// could not prove. A parameter is syntactically neutral, so the grammar decides whether an unknown
/// fragment mattered. See <c>docs/adr/0006</c>.</summary>
internal sealed record FoldedSql(string Text, IReadOnlySet<string> Parameters);

/// <summary>The SQL policy over <see cref="StringConstantFolder"/>: each fragment the fold could not prove
/// becomes the next parameter in <c>@__cd_p0</c>, <c>@__cd_p1</c>, … left to right.</summary>
internal sealed class SqlTextFolder(Solution solution)
{
    private const string PARAMETER_PREFIX = "@__cd_p";

    /// <summary>A fragment's name says nothing about the shape of a query, so a named substitution and an
    /// unreducible fragment fold to the same neutral marker; the numbering is assigned afterwards, in the
    /// order the parts lie in the text.</summary>
    private static readonly FoldedPlaceholders PLACEHOLDERS = new(_ => PARAMETER_PREFIX, PARAMETER_PREFIX);

    /// <summary>What a fold that named nothing at all reads as: one unknown part, which becomes one
    /// parameter below.</summary>
    private static readonly FoldedString UNRESOLVED =
        new(PARAMETER_PREFIX, [new FoldedPart(FoldedPartKind.Unknown, PARAMETER_PREFIX, 0, null, "the query folded to no value")], null);

    /// <summary>Multi-valued SQL is out of scope, and the bound of one is how that is said: a fragment
    /// standing for several collapses to one neutral parameter <em>where it arises</em>, and the statement
    /// around it keeps every literal it had. Reducing the whole query instead would throw away the
    /// <c>UPDATE</c> along with the branchy value in it, and with it the table and the write edge — a
    /// silent loss of findings, where a parameter in the value position is merely a value the parser was
    /// never going to read. See <c>docs/adr/0015</c>.</summary>
    private readonly StringConstantFolder _folder = new(solution, PLACEHOLDERS, maximumValues: 1);

    public async Task<FoldedSql> FoldAsync(ExpressionSyntax expression, SemanticModel semanticModel,
                                           CancellationToken cancellationToken)
    {
        var set = await _folder.FoldAsync(expression, semanticModel, cancellationToken);
        // The bound above leaves exactly one value in every reachable case; a fold that produced none at all
        // is the degenerate one, and it reads as a single parameter.
        var folded = set.Values.Count == 1 ? set.Single : UNRESOLVED;
        var text = new StringBuilder();
        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in folded.Parts)
        {
            if (part.Kind == FoldedPartKind.Literal)
            {
                text.Append(part.Text);
                continue;
            }

            var parameter = $"{PARAMETER_PREFIX}{parameters.Count}";
            parameters.Add(parameter);
            text.Append(parameter);
        }

        return new FoldedSql(text.ToString(), parameters);
    }
}
