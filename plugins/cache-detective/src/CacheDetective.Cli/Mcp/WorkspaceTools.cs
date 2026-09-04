using System.ComponentModel;
using System.Text.Json;
using CacheDetective.Serialization;
using ModelContextProtocol.Server;

namespace CacheDetective.Mcp;

[McpServerToolType]
internal sealed class WorkspaceTools
{
    [McpServerTool(Name = "workspace_init"),
     Description("Loads or creates the cache-detective workspace configuration and makes it active for this session.")]
    public static async Task<string> Initialize(WorkspaceSession session,
                                                [Description("Absolute path to the scanned repository root.")] string root,
                                                CancellationToken cancellationToken,
                                                [Description("Solution or project paths relative to root. Omit to retain existing paths.")]
                                                string[]? solutions = null,
                                                [Description("Table staleness budgets in seconds. Omit to retain existing budgets.")]
                                                Dictionary<string, double>? budgets = null)
    {
        var result = await session.InitializeAsync(root, solutions, budgets, cancellationToken)
                                  .ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.WorkspaceInitResult);
    }

    [McpServerTool(Name = "workspace_status"),
     Description("Reports configured and indexed solutions plus current graph, finding and unresolved counts.")]
    public static async Task<string> Status(WorkspaceSession session,
                                            CancellationToken cancellationToken,
                                            [Description("One-based solutions page. Defaults to 1.")]
                                            int page = PageArguments.DefaultPage,
                                            [Description("Solutions per page. Defaults to 50.")]
                                            int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.GetStatusAsync(new PageArguments { Page = page, PageSize = pageSize },
                                                  cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.WorkspaceStatusResult);
    }

    [McpServerTool(Name = "index_solution"),
     Description("Loads and indexes one configured solution or project without discarding a previous successful index on failure.")]
    public static async Task<string> IndexSolution(WorkspaceSession session,
                                                   [Description("Solution or project path, absolute or relative to the active repository root.")] string path,
                                                   CancellationToken cancellationToken,
                                                   [Description("One-based diagnostics page. Defaults to 1.")]
                                                   int page = PageArguments.DefaultPage,
                                                   [Description("Diagnostics per page. Defaults to 50.")]
                                                   int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.IndexSolutionAsync(path,
            new PageArguments { Page = page, PageSize = pageSize }, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.IndexSolutionResult);
    }

    [McpServerTool(Name = "index_database"),
     Description("Reads one configured database's catalogue into the graph over a read-only connection. Safe to repeat: a second call replaces that database's half of the graph instead of adding to it, and may be run before or after index_solution.")]
    public static async Task<string> IndexDatabase(WorkspaceSession session,
                                                   [Description("Name of the database as configured in the workspace 'databases' array.")] string name,
                                                   CancellationToken cancellationToken)
    {
        var result = await session.IndexDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.IndexDatabaseResult);
    }
}
