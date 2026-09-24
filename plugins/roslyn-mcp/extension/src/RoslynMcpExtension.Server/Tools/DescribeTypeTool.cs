using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class DescribeTypeTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_describe_type")]
	[Description("Describes the API of a type as the project sees it, including types from NuGet packages and the framework at exactly the version referenced: its base-type chain and interfaces, the members it declares that the project context can use (public, internal where visible, protected for a derived type), then the extension methods any referenced assembly declares for it, each naming the namespace to import and its assembly. Inherited members are not repeated; describe a base type through its symbolId. Named by position, it describes the type of the symbol there: a variable's or member's type, a method's return type. Declared members come before extensions when maxResults cuts, and memberCount and extensionCount count everything. Extensions a caller at the position already reaches, with no using to add, come first; then those nearest the type's own namespace. The extension scan stops at a budget of about 45 seconds: extensionsComplete is then false and unscannedAssemblies names what it did not scan. Find a type's symbolId with roslyn_search_symbols, using includeMetadata for a referenced type." + SymbolAddress.Summary)]
	public Task<TypeDescriptionResult> DescribeType(
		[Description(SymbolAddress.FilePath)] string? filePath = null,
		[Description(SymbolAddress.Line)] int line = 0,
		[Description(SymbolAddress.Column)] int column = 0,
		[Description(SymbolAddress.SymbolId)] string? symbolId = null,
		[Description(SymbolAddress.ProjectName)] string? projectName = null,
		[Description("Keep members whose name contains this, ignoring case, or has humps in a row starting with its pieces (TGA finds TryGetAsync, AddSin finds AddSingleton)")] string? memberFilter = null,
		[Description("Maximum number of members and extensions to return together (default: 100, maximum: 500)")] int maxResults = 100)
	{
		if (ToolGuards.HasInvalidPosition(filePath, line, column))
		{
			return Task.FromResult(new TypeDescriptionResult
			{
				ErrorCode = ToolErrorCodes.InvalidArgument,
				ErrorMessage = "Line and column must be at least 1."
			});
		}

		return rpc.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, ToolGuards.ClampMaxResults(maxResults, 500));
	}
}
