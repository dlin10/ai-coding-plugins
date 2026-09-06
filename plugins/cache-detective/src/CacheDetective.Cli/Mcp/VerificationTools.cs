using System.ComponentModel;
using System.Text.Json;
using CacheDetective.Serialization;
using ModelContextProtocol.Server;

namespace CacheDetective.Mcp;

[McpServerToolType]
internal sealed class VerificationTools
{
    [McpServerTool(Name = "verify_finding"),
     Description("Reads the live cache and database for one finding and reports whether staleness is refuted, " +
                 "possible, or not verifiable. It never changes the finding's confidence or whether it is reported.")]
    public static async Task<string> VerifyFinding(WorkspaceSession session,
                                                   [Description("Finding id returned by find_issues or find_unguarded_writes.")] string findingId,
                                                   CancellationToken cancellationToken,
                                                   [Description("Verify again instead of answering from the snapshot taken earlier. Defaults to false.")]
                                                   bool refresh = false,
                                                   [Description("One-based page of sampled keys. Defaults to 1.")]
                                                   int page = PageArguments.DefaultPage,
                                                   [Description("Sampled keys per page. Defaults to 50.")]
                                                   int pageSize = PageArguments.DefaultPageSize)
    {
        var result = await session.VerifyFindingAsync(findingId, refresh,
                                                      new PageArguments { Page = page, PageSize = pageSize },
                                                      cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.VerifyFindingResult);
    }
}
