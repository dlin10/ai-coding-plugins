using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class GoToDefinitionTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_go_to_definition")]
	[Description("Navigates to the definition of a symbol at a given position. Returns source locations or metadata assembly names plus document-scoped compilation identity; projectName includes the target framework for multi-targeted projects.")]
	public Task<SymbolListResult> GoToDefinition(
		[Description("Absolute path to the C# file")] string filePath,
		[Description("Line number (1-based)")] int line,
		[Description("Column number (1-based)")] int column)
	{
		if (ToolGuards.HasInvalidPosition(line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.GoToDefinitionAsync(filePath, line, column);
	}
}
