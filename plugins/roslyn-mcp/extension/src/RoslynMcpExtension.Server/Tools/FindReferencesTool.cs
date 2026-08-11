using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindReferencesTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_references")]
	[Description("Finds deduplicated references to a symbol, ordered by file and line. The referenced symbol appears once at the response level; each location includes its fully-qualified containingSymbol plus enclosingStartLine/enclosingEndLine, the declaration span of that containing member — read those lines directly to see the surrounding code instead of calling roslyn_get_document_symbols. Also returns document-scoped compilation identity; projectName includes the target framework for multi-targeted projects.")]
	public Task<SymbolListResult> FindReferences(
		[Description("Absolute path to the C# file")] string filePath,
		[Description("Line number (1-based)")] int line,
		[Description("Column number (1-based)")] int column,
		[Description("Maximum number of results to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindReferencesAsync(filePath, line, column, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
