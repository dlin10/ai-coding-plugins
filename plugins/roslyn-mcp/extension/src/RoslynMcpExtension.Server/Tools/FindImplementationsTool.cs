using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class FindImplementationsTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_find_implementations")]
	[Description("Finds implementing classes and derived interfaces for an interface, derived classes for a class, implementations of interface members, or overrides of virtual and abstract members. Includes document-scoped compilation identity; projectName carries the target framework." + SymbolAddress.Summary)]
	public Task<SymbolListResult> FindImplementations(
		[Description(SymbolAddress.FilePath)] string? filePath = null,
		[Description(SymbolAddress.Line)] int line = 0,
		[Description(SymbolAddress.Column)] int column = 0,
		[Description(SymbolAddress.SymbolId)] string? symbolId = null,
		[Description(SymbolAddress.ProjectName)] string? projectName = null,
		[Description("Maximum number of results to return (default: 50, maximum: 500)")] int maxResults = 50)
	{
		if (ToolGuards.HasInvalidPosition(filePath, line, column))
			return Task.FromResult(ToolGuards.InvalidSymbolList("Line and column must be at least 1."));

		return rpc.FindImplementationsAsync(filePath, line, column, symbolId, projectName, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
