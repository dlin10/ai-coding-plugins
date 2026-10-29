using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>
/// The analysis contract served from one workspace: Visual Studio's own, or a solution the headless daemon
/// loaded. Each call leaves one line through <see cref="Log"/> once it has finished.
/// </summary>
/// <param name="workspace">The workspace whose current solution every call is answered from.</param>
public class AnalysisService(Workspace workspace) : IRoslynAnalysisRpc
{
	private readonly DocumentFinder _documentFinder = new(workspace);
	private readonly OperationLog _log = new(workspace);

	public virtual Task LogAsync(string message) => Task.CompletedTask;

	public virtual Task ReadyAsync() => Task.CompletedTask;

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
		=> InvokeAsync(nameof(ValidateFileAsync), _log.RelativePath(filePath),
			(result, request) => OperationLog.Validation(result, request, includeWarnings),
			() => new ValidateFileService(_documentFinder).ValidateFileAsync(filePath, includeWarnings, runAnalyzers));

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
		=> InvokeAsync(nameof(GetDiagnosticsAsync), _log.Scope(filePaths, projectName),
			(result, request) => OperationLog.Diagnostics(result, request, includeWarnings),
			() => new DiagnosticsService(_documentFinder, TimeSpan.FromSeconds(DiagnosticsService.BudgetSeconds))
				.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, ClampMaxResults(maxResults, 1000)));

	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
		=> InvokeAsync(nameof(FindReferencesAsync), _log.Request(filePath, line, column, symbolId),
			(result, request) => OperationLog.Found(result, request, "ref"),
			() => new FindReferencesService(_documentFinder).FindReferencesAsync(filePath, line, column, symbolId, projectName,
			                                                                     ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> InvokeAsync(nameof(FindImplementationsAsync), _log.Request(filePath, line, column, symbolId),
			(result, request) => OperationLog.Found(result, request, "implementation"),
			() => new ImplementationsService(_documentFinder).FindImplementationsAsync(filePath, line, column, symbolId, projectName,
			                                                                           ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> InvokeAsync(nameof(FindCallersAsync), _log.Request(filePath, line, column, symbolId),
			(result, request) => OperationLog.Found(result, request, "call site"),
			() => new FindCallersService(_documentFinder).FindCallersAsync(filePath, line, column, symbolId, projectName,
			                                                               ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column)
		=> InvokeAsync(nameof(GoToDefinitionAsync), _log.Request(filePath, line, column, null), _log.Definition,
			() => new GoToDefinitionService(_documentFinder).GoToDefinitionAsync(filePath, line, column));

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
		=> InvokeAsync(nameof(GetDocumentSymbolsAsync), _log.RelativePath(filePath),
			(result, request) => OperationLog.Found(result, request, "symbol"),
			() => new DocumentSymbolsService(_documentFinder).GetDocumentSymbolsAsync(filePath));

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
		=> InvokeAsync(nameof(SearchSymbolsAsync), $"\"{query}\"" + (includeMetadata ? " +metadata" : ""),
			(result, request) => OperationLog.Found(result, request, "match", "matches"),
			() => new SearchSymbolsService(_documentFinder).SearchSymbolsAsync(query, includeMetadata, ClampMaxResults(maxResults, 500)));

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic)
		=> InvokeAsync(nameof(FindDeadCodeAsync), OperationLog.DeadCodeScope(includeInternal, includePublic),
			(result, request) => OperationLog.Found(result, request, "candidate"),
			() => new DeadCodeAnalysisService(_documentFinder).FindDeadCodeAsync(ClampMaxResults(maxResults, 1000), includeInternal, includePublic));

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> InvokeAsync(nameof(GetSymbolInfoAsync), _log.Request(filePath, line, column, symbolId), OperationLog.SymbolInfo,
			() => new SymbolInfoService(_documentFinder).GetSymbolInfoAsync(filePath, line, column, symbolId, projectName));

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> InvokeAsync(nameof(DescribeTypeAsync), _log.Request(filePath, line, column, symbolId),
			(result, request) => OperationLog.TypeDescription(result, request, memberFilter),
			() => new DescribeTypeService(_documentFinder, TimeSpan.FromSeconds(DescribeTypeService.BudgetSeconds))
				.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, ClampMaxResults(maxResults, 500)));

	/// <summary>Receives the one line a finished call leaves; writes nothing unless a host overrides it.</summary>
	/// <param name="line">The line, laid out by <see cref="OperationLog.Line"/>.</param>
	protected virtual void Log(string line)
	{
	}

	/// <summary>Runs a tool and leaves one line through <see cref="Log"/> once it has finished.</summary>
	/// <param name="toolName">The tool's name in the line.</param>
	/// <param name="request">What the client asked about, written when the result names no symbol or the call throws.</param>
	/// <param name="describe">Writes what the call was about and what it found from its result and <paramref name="request"/>.</param>
	/// <param name="action">The tool itself.</param>
	private async Task<T> InvokeAsync<T>(string toolName, string request, Func<T, string, string> describe, Func<Task<T>> action)
		where T : IToolResult
	{
		var sw = Stopwatch.StartNew();
		try
		{
			var result = await action();
			CompleteResult(result);
			Log(OperationLog.Line(sw.ElapsedMilliseconds, toolName, describe(result, request)));
			return result;
		}
		catch (Exception ex)
		{
			Log(OperationLog.Line(sw.ElapsedMilliseconds, toolName,
			                      OperationLog.Failed(request, "exception", $"{ex.GetType().Name}: {ex.Message}")));
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
