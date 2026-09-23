using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class SymbolInfoTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_get_symbol_info")]
	[Description("Gets detailed information for a symbol, including a Roslyn display string, XML documentation, where it is declared, and document-scoped compilation identity. projectName includes the target framework for multi-targeted projects; for a symbol from metadata, assembly names the referenced assembly and its version." + SymbolAddress.Summary)]
	public Task<SymbolInfoResult> GetSymbolInfo(
		[Description(SymbolAddress.FilePath)] string? filePath = null,
		[Description(SymbolAddress.Line)] int line = 0,
		[Description(SymbolAddress.Column)] int column = 0,
		[Description(SymbolAddress.SymbolId)] string? symbolId = null,
		[Description(SymbolAddress.ProjectName)] string? projectName = null)
	{
		if (ToolGuards.HasInvalidPosition(filePath, line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolInfo("Line and column must be at least 1."));

		return rpc.GetSymbolInfoAsync(filePath, line, column, symbolId, projectName);
	}
}
