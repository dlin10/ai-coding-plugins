using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class SearchSymbolsTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_search_symbols")]
	[Description("Searches source symbol declarations across the entire solution using substring and camel-hump matching (for example, OSvc finds OrderService). Values above 500 are clamped.")]
	public Task<SymbolListResult> SearchSymbols(
		[Description("Substring or camel-hump pattern for source declarations (for example, OSvc finds OrderService)")] string query,
		[Description("Maximum number of results to return (default: 30, maximum: 500)")] int maxResults = 30)
	{
		if (string.IsNullOrWhiteSpace(query))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Query must not be empty."));

		return rpc.SearchSymbolsAsync(query, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
