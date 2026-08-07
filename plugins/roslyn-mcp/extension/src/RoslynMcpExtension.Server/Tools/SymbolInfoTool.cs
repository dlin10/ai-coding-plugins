using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class SymbolInfoTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_get_symbol_info")]
	[Description("Gets detailed information for a symbol at a specific position, including a Roslyn display string, XML documentation, and document-scoped compilation identity. projectName includes the target framework for multi-targeted projects.")]
	public Task<SymbolInfoResult> GetSymbolInfo(
		[Description("Absolute path to the C# file")] string filePath,
		[Description("Line number (1-based)")] int line,
		[Description("Column number (1-based)")] int column)
	{
		if (ToolGuards.HasInvalidPosition(line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolInfo("Line and column must be at least 1."));

		return rpc.GetSymbolInfoAsync(filePath, line, column);
	}
}
