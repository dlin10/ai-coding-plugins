using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli.Tools;

[McpServerToolType]
public sealed class DocumentSymbolsTool(IRoslynAnalysisRpc rpc)
{
	[McpServerTool(Name = "roslyn_get_document_symbols", ReadOnly = true, Destructive = false, OpenWorld = false)]
	[Description("Lists all symbols in a C# file as a tree (namespaces, types, members) with accessibility, modifiers, line spans, and document-scoped compilation identity. projectName includes the target framework for multi-targeted projects.")]
	public Task<SymbolListResult> GetDocumentSymbols(
		[Description("Absolute path to the C# file")] string filePath)
		=> rpc.GetDocumentSymbolsAsync(filePath);
}
