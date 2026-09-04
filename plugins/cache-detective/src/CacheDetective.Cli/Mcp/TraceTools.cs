using System.ComponentModel;
using System.Text.Json;
using CacheDetective.Serialization;
using ModelContextProtocol.Server;

namespace CacheDetective.Mcp;

[McpServerToolType]
internal sealed class TraceTools
{
    [McpServerTool(Name = "trace_key"),
     Description("Returns who caches and invalidates one key and the tables or keys it depends on, with paths and locations.")]
    public static async Task<string> TraceKey(WorkspaceSession session,
                                              [Description("Normalized cache key template.")] string template,
                                              CancellationToken cancellationToken,
                                              [Description("Store name. Required when this template exists in more than one store.")] string? store = null,
                                              [Description("One-based page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                              [Description("Items per response section. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadGraphAsync((graph, _) => TraceQueries.TraceKey(graph, template, store,
            new PageArguments { Page = page, PageSize = pageSize }), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.TraceKeyResult);
    }

    [McpServerTool(Name = "trace_table"),
     Description("Returns who reads and writes one table and the cache keys that depend on it, with paths and locations.")]
    public static async Task<string> TraceTable(WorkspaceSession session,
                                                [Description("Table name in schema.name form.")] string name,
                                                CancellationToken cancellationToken,
                                                [Description("One-based page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                                [Description("Items per response section. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadGraphAsync((graph, _) => TraceQueries.TraceTable(graph, name,
            new PageArguments { Page = page, PageSize = pageSize }), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.TraceTableResult);
    }

    [McpServerTool(Name = "export_graph"),
     Description("Exports the graph as section 4.3 JSON for comparing runs. This debugging export is explicitly exempt from the eight-kilobyte response ceiling.")]
    public static async Task<string> ExportGraph(WorkspaceSession session,
                                                 CancellationToken cancellationToken,
                                                 [Description("Optional case-insensitive node, edge or unresolved substring filter.")] string? filter = null)
    {
        var result = await session.ReadGraphAsync(
            (graph, configuration) => TraceQueries.ExportGraph(graph, configuration, filter), cancellationToken)
                                  .ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.GraphExport);
    }
}
