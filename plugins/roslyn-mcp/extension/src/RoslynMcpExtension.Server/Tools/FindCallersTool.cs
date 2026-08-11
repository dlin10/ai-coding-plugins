using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindCallersTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_callers")]
	[Description("Finds call sites grouped by calling member. Direct calls are marked caller; calls through interface or override relationships are marked indirect-caller. Each call site carries enclosingStartLine/enclosingEndLine, the declaration span of the calling member — read those lines directly to see the call in context instead of calling roslyn_get_document_symbols. Includes document-scoped compilation identity; projectName carries the target framework.")]
	public Task<SymbolListResult> FindCallers(
		[Description("Absolute path to the C# file containing the method or property")] string filePath,
		[Description("Line number (1-based)")] int line,
		[Description("Column number (1-based)")] int column,
		[Description("Maximum number of call sites to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindCallersAsync(filePath, line, column, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
