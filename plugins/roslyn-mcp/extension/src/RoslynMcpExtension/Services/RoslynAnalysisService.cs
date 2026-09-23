using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServices;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

[Export(typeof(RoslynAnalysisService))]
[PartCreationPolicy(CreationPolicy.Shared)]
[method: ImportingConstructor]
public class RoslynAnalysisService(VisualStudioWorkspace workspace) : IRoslynAnalysisRpc
{
	private readonly DocumentFinder _documentFinder = new(workspace);

	internal IExtensionLogger? Logger { get; set; }
	internal Action? ServerReadyHandler { get; set; }

	public Task LogAsync(string message)
	{
		Logger?.Log($"[MCP Server] {message}");
		return Task.CompletedTask;
	}

	public Task ReadyAsync()
	{
		ServerReadyHandler?.Invoke();
		return Task.CompletedTask;
	}

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
		=> InvokeAsync(nameof(ValidateFileAsync),
			() => new ValidateFileService(_documentFinder).ValidateFileAsync(filePath, includeWarnings, runAnalyzers));

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
		=> InvokeAsync(nameof(GetDiagnosticsAsync),
			() => new DiagnosticsService(_documentFinder, TimeSpan.FromSeconds(DiagnosticsService.BudgetSeconds))
				.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, ClampMaxResults(maxResults, 1000)));

	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
		=> InvokeAsync(nameof(FindReferencesAsync),
			() => new FindReferencesService(_documentFinder).FindReferencesAsync(filePath, line, column, symbolId, projectName,
			                                                                     ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> InvokeAsync(nameof(FindImplementationsAsync),
			() => new ImplementationsService(_documentFinder).FindImplementationsAsync(filePath, line, column, symbolId, projectName,
			                                                                           ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> InvokeAsync(nameof(FindCallersAsync),
			() => new FindCallersService(_documentFinder).FindCallersAsync(filePath, line, column, symbolId, projectName,
			                                                               ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column)
		=> InvokeAsync(nameof(GoToDefinitionAsync),
			() => new GoToDefinitionService(_documentFinder).GoToDefinitionAsync(filePath, line, column));

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
		=> InvokeAsync(nameof(GetDocumentSymbolsAsync),
			() => new DocumentSymbolsService(_documentFinder).GetDocumentSymbolsAsync(filePath));

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
		=> InvokeAsync(nameof(SearchSymbolsAsync),
			() => new SearchSymbolsService(_documentFinder).SearchSymbolsAsync(query, includeMetadata, ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic)
		=> InvokeAsync(nameof(FindDeadCodeAsync),
			() => new DeadCodeAnalysisService(_documentFinder).FindDeadCodeAsync(ClampMaxResults(maxResults, 1000), includeInternal, includePublic));

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> InvokeAsync(nameof(GetSymbolInfoAsync),
			() => new SymbolInfoService(_documentFinder).GetSymbolInfoAsync(filePath, line, column, symbolId, projectName));

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> InvokeAsync(nameof(DescribeTypeAsync),
			() => new DescribeTypeService(_documentFinder, TimeSpan.FromSeconds(DescribeTypeService.BudgetSeconds))
				.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, ClampMaxResults(maxResults, 500)));

	private async Task<T> InvokeAsync<T>(string toolName, Func<Task<T>> action) where T : IToolResult
	{
		Logger?.Log($"Tool '{toolName}' invoked");
		var sw = Stopwatch.StartNew();
		try
		{
			var result = await action();
			CompleteResult(result);
			Logger?.Log($"Tool '{toolName}' completed in {sw.ElapsedMilliseconds}ms");
			return result;
		}
		catch (Exception ex)
		{
			Logger?.Log($"Tool '{toolName}' failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
			throw;
		}
	}

	private static int ClampMaxResults(int maxResults, int maximum)
	{
		return maxResults < 1 ? 1 : maxResults > maximum ? maximum : maxResults;
	}

	internal static void CompleteResult<T>(T result) where T : IToolResult
	{
		if (result.ErrorMessage != null)
			result.ErrorCode ??= ToolErrorCodes.InternalError;
		result.RequestSucceeded = result.ErrorMessage == null;
		if (result is SymbolListResult symbolListResult)
			symbolListResult.ReturnedCount = symbolListResult.Members.Count;
	}
}
