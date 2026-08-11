using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class SymbolInfoService(DocumentFinder documentFinder)
{
	public async Task<SymbolInfoResult> GetSymbolInfoAsync(string filePath, int line, int column)
	{
		try
		{
			var document = documentFinder.FindDocument(filePath);
			var semanticModel = await document.GetSemanticModelAsync();
			var syntaxTree = await document.GetSyntaxTreeAsync();
			if (semanticModel == null || syntaxTree == null)
				return new SymbolInfoResult { ErrorMessage = "Failed to get semantic model" };

			var compilation = DocumentFinder.CreateCompilationInfo(document, semanticModel);
			var position = DocumentFinder.GetPosition(syntaxTree, line, column);
			var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, documentFinder.Workspace);
			// Reported inline rather than thrown so the caller still learns which compilation was searched.
			if (symbol == null)
				return new SymbolInfoResult
				{
					Compilation = compilation,
					ErrorCode = ToolErrorCodes.InvalidArgument,
					ErrorMessage = $"No symbol found at line {line}, column {column}"
				};

			var result = CodeMemberInfoFactory.CreateSymbolInfo(symbol, symbol.Locations.FirstOrDefault(l => l.IsInSource));
			result.Compilation = compilation;
			return result;
		}
		catch (Exception ex)
		{
			var result = new SymbolInfoResult();
			ToolResultErrors.Set(result, ex);
			return result;
		}
	}
}
