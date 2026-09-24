using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindCallersTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_callers")]
	[Description("Finds call sites grouped by calling member. Direct calls are marked caller; calls through interface or override relationships are marked indirect-caller. Each call site carries enclosingStartLine/enclosingEndLine, the declaration span of the calling member, so the surrounding code can be read straight from the file, and containingSymbolId, which names the calling member for the next call up the chain. Includes document-scoped compilation identity; projectName carries the target framework." + SymbolAddress.Summary)]
	public Task<SymbolListResult> FindCallers(
		[Description(SymbolAddress.FilePath)] string? filePath = null,
		[Description(SymbolAddress.Line)] int line = 0,
		[Description(SymbolAddress.Column)] int column = 0,
		[Description(SymbolAddress.SymbolId)] string? symbolId = null,
		[Description(SymbolAddress.ProjectName)] string? projectName = null,
		[Description("Maximum number of call sites to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(filePath, line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindCallersAsync(filePath, line, column, symbolId, projectName, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
