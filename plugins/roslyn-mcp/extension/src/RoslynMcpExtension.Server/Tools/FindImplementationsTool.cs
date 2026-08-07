using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindImplementationsTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_implementations")]
	[Description("Finds implementing classes and derived interfaces for an interface, derived classes for a class, implementations of interface members, or overrides of virtual and abstract members. Includes document-scoped compilation identity; projectName carries the target framework.")]
	public Task<SymbolListResult> FindImplementations(
		[Description("Absolute path to the C# file containing the symbol")] string filePath,
		[Description("Line number (1-based)")] int line,
		[Description("Column number (1-based)")] int column,
		[Description("Maximum number of results to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindImplementationsAsync(filePath, line, column, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
