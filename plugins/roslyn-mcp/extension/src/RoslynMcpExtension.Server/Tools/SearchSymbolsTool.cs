using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class SearchSymbolsTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_search_symbols")]
	[Description("Searches source symbol declarations across the entire solution using substring and camel-hump matching (for example, OSvc finds OrderService). Each result carries its symbolId and enclosingStartLine/enclosingEndLine, the span of the whole declaration. With includeMetadata, also returns types and members of referenced assemblies whose name is exactly the query, each naming its assembly and version. Values above 500 are clamped.")]
	public Task<SymbolListResult> SearchSymbols(
		[Description("Substring or camel-hump pattern for source declarations (for example, OSvc finds OrderService); an exact name for metadata")] string query,
		[Description("Also search referenced assemblies, by exact name only (default: false)")] bool includeMetadata = false,
		[Description("Maximum number of results to return (default: 30, maximum: 500)")] int maxResults = 30)
	{
		if (string.IsNullOrWhiteSpace(query))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Query must not be empty."));

		return rpc.SearchSymbolsAsync(query, includeMetadata, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
