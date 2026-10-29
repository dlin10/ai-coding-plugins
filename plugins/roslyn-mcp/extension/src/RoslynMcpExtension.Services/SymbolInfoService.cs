using System;
using System.Linq;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class SymbolInfoService(DocumentFinder documentFinder)
{
	public async Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
	{
		var result = new SymbolInfoResult();

		try
		{
			var resolved = await new SymbolResolver(documentFinder).ResolveAsync(filePath, line, column, symbolId, projectName);
			// Set before a missing symbol throws, so the caller still learns which compilation was searched.
			result.Compilation = resolved.Compilation;
			var symbol = resolved.Required;

			result = CodeMemberInfoFactory.CreateSymbolInfo(symbol, symbol.Locations.FirstOrDefault(l => l.IsInSource));
			result.Compilation = resolved.Compilation;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}
}
