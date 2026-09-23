using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindReferencesTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_references")]
	[Description("Finds deduplicated references to a symbol, ordered by file and line. The referenced symbol appears once at the response level; each location includes its fully-qualified containingSymbol and containingSymbolId, plus enclosingStartLine/enclosingEndLine, the declaration span of that containing member, so the surrounding code can be read straight from the file. Also returns document-scoped compilation identity; projectName includes the target framework for multi-targeted projects." + SymbolAddress.Summary)]
	public Task<SymbolListResult> FindReferences(
		[Description(SymbolAddress.FilePath)] string? filePath = null,
		[Description(SymbolAddress.Line)] int line = 0,
		[Description(SymbolAddress.Column)] int column = 0,
		[Description(SymbolAddress.SymbolId)] string? symbolId = null,
		[Description(SymbolAddress.ProjectName)] string? projectName = null,
		[Description("Maximum number of results to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(filePath, line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindReferencesAsync(filePath, line, column, symbolId, projectName, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
