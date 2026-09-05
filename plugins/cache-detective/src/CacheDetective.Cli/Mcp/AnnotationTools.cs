using System.ComponentModel;
using System.Text.Json;
using CacheDetective.Serialization;
using ModelContextProtocol.Server;

namespace CacheDetective.Mcp;

[McpServerToolType]
internal sealed class AnnotationTools
{
    [McpServerTool(Name = "annotate"), Description("Resolves an unresolved analysis entry with an explicit graph annotation.")]
    public static async Task<string> Annotate(WorkspaceSession session,
                                               [Description("Unresolved id in the form u:N.")] string unresolvedId,
                                               JsonElement resolution,
                                               CancellationToken cancellationToken,
                                               string? note = null)
    {
        var result = await session.AnnotateAsync(unresolvedId, resolution, note, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.AnnotateResult);
    }
}

internal sealed record AffectedKey(string Id, string Change);
internal sealed record AffectedFinding(string Id, string Rule, string Change);
internal sealed record AnnotateResult(string UnresolvedId, int AnnotationId, string Kind, string? Reindexed,
                                      int AffectedKeysTotal, IReadOnlyList<AffectedKey> AffectedKeys,
                                      int AffectedFindingsTotal, IReadOnlyList<AffectedFinding> AffectedFindings,
                                      bool Truncated, string? Notice);
