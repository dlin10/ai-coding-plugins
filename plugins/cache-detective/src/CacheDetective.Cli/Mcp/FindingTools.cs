using System.ComponentModel;
using System.Text.Json;
using CacheDetective.Serialization;
using ModelContextProtocol.Server;

namespace CacheDetective.Mcp;

[McpServerToolType]
internal sealed class FindingTools
{
    [McpServerTool(Name = "find_unguarded_writes"),
     Description("Returns unguarded database writes, filtered by confidence, table or solution.")]
    public static async Task<string> FindUnguardedWrites(WorkspaceSession session, CancellationToken cancellationToken,
                                                         [Description("confirmed, likely or unknown.")] string? confidence = null,
                                                         [Description("Exact schema.name table filter.")] string? table = null,
                                                         [Description("Exact indexed solution filter.")] string? solution = null,
                                                         [Description("Include findings suppressed by their TTL budget. Defaults to false.")]
                                                         bool includeSuppressed = false,
                                                         [Description("One-based page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                                         [Description("Findings per page. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadFindingsAsync((graph, configuration, _, catalog) =>
            FindingQueries.FindUnguardedWrites(graph, configuration?.Budgets, catalog,
                confidence, table, solution, includeSuppressed,
                new PageArguments { Page = page, PageSize = pageSize }), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.FindingEnvelope);
    }

    [McpServerTool(Name = "find_issues"),
     Description("Returns all cache findings, optionally filtered by rule or confidence.")]
    public static async Task<string> FindIssues(WorkspaceSession session, CancellationToken cancellationToken,
                                                [Description("UNGUARDED_WRITE, CROSS_SERVICE_GAP, EXTERNAL_NO_TTL, STALE_PARENT_KEY, ORPHAN_INVALIDATION or PATTERN_MISMATCH.")] string? rule = null,
                                                [Description("confirmed, likely or unknown.")] string? confidence = null,
                                                [Description("Include findings suppressed by their TTL budget. Defaults to false.")]
                                                bool includeSuppressed = false,
                                                [Description("One-based page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                                [Description("Findings per page. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadFindingsAsync((graph, configuration, _, catalog) =>
            FindingQueries.FindIssues(graph, configuration?.Budgets, catalog, rule, confidence,
                includeSuppressed, new PageArguments { Page = page, PageSize = pageSize }),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.FindingEnvelope);
    }

    [McpServerTool(Name = "get_unresolved"),
     Description("Returns unresolved analysis entries with ten source lines before and after each site.")]
    public static async Task<string> GetUnresolved(WorkspaceSession session, CancellationToken cancellationToken,
                                                   [Description("key, sql, call, cache_api, role, event or event_api.")] string? kind = null,
                                                   [Description("One-based page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                                   [Description("Entries per page. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadFindingsAsync((graph, _, repositoryRoot, _) =>
            FindingQueries.GetUnresolved(graph, repositoryRoot, kind,
                new PageArguments { Page = page, PageSize = pageSize }), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.ListEnvelopeUnresolvedItem);
    }

    [McpServerTool(Name = "get_evidence"),
     Description("Returns paged source fragments for the full edge chain of one session finding.")]
    public static async Task<string> GetEvidence(WorkspaceSession session,
                                                 [Description("Finding id returned by find_issues or find_unguarded_writes.")] string findingId,
                                                 CancellationToken cancellationToken,
                                                 [Description("One-based fragments page. Defaults to 1.")] int page = PageArguments.DefaultPage,
                                                 [Description("Fragments per page. Defaults to 50.")] int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.ReadFindingsAsync((graph, configuration, repositoryRoot, catalog) =>
            FindingQueries.GetEvidence(graph, configuration?.Budgets, repositoryRoot, catalog, findingId,
                new PageArguments { Page = page, PageSize = pageSize }), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.EvidenceResult);
    }
}
