using System;
using System.IO;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// One shim's connection to the daemon. The analysis calls answer from the solution the connection last loaded,
/// waiting for its load as <see cref="LoadAsync"/> does.
/// </summary>
/// <param name="registry">The solutions the daemon holds, shared by every connection.</param>
/// <param name="loadWait">How long a call waits for a load before answering that it is still loading.</param>
/// <param name="daemon">What the daemon reports about itself in every load result.</param>
internal sealed class DaemonSession(SolutionRegistry registry, TimeSpan loadWait, DaemonInfo daemon) : IRoslynDaemonRpc
{
	private volatile Bound? _bound;

	public async Task<LoadResult> LoadAsync(string solutionPath)
	{
		var path = Path.GetFullPath(solutionPath);
		var load = registry.Load(path);
		var result = new LoadResult { Solution = path, Daemon = daemon };
		if (!await FinishesInTimeAsync(load).ConfigureAwait(false))
		{
			_bound = new Bound(path, load);
			result.Status = LoadStatus.Loading;
			result.RequestSucceeded = true;
			return result;
		}

		try
		{
			var loaded = await load.ConfigureAwait(false);
			_bound = new Bound(path, load);
			result.Status = LoadStatus.Loaded;
			result.ProjectCount = loaded.Solution.ProjectIds.Count;
			result.DocumentCount = loaded.DocumentCount;
			result.LoadedAt = loaded.LoadedAt;
			result.RequestSucceeded = true;
		}
		catch (Exception ex)
		{
			result.ErrorCode = ToolErrorCodes.LoadFailed;
			result.ErrorMessage = $"Could not load {path}: {ex.Message}";
		}

		return result;
	}

	public Task LogAsync(string message) => Task.CompletedTask;

	public Task ReadyAsync() => Task.CompletedTask;

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
		=> CallAsync(analysis => analysis.ValidateFileAsync(filePath, includeWarnings, runAnalyzers));

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
		=> CallAsync(analysis => analysis.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, maxResults));

	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
		=> CallAsync(analysis => analysis.FindReferencesAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> CallAsync(analysis => analysis.FindImplementationsAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> CallAsync(analysis => analysis.FindCallersAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column)
		=> CallAsync(analysis => analysis.GoToDefinitionAsync(filePath, line, column));

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
		=> CallAsync(analysis => analysis.GetDocumentSymbolsAsync(filePath));

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
		=> CallAsync(analysis => analysis.SearchSymbolsAsync(query, includeMetadata, maxResults));

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic)
		=> CallAsync(analysis => analysis.FindDeadCodeAsync(maxResults, includeInternal, includePublic));

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> CallAsync(analysis => analysis.GetSymbolInfoAsync(filePath, line, column, symbolId, projectName));

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> CallAsync(analysis => analysis.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, maxResults));

	/// <summary>Runs an analysis call on this connection's solution once it has loaded, or says why it cannot.</summary>
	/// <param name="call">The call, on the solution's analysis service.</param>
	private async Task<T> CallAsync<T>(Func<AnalysisService, Task<T>> call) where T : IToolResult, new()
	{
		if (_bound is not { } bound)
			return Failed<T>(ToolErrorCodes.NoSolution, "No solution is loaded on this connection.");
		if (!await FinishesInTimeAsync(bound.Load).ConfigureAwait(false))
			return Failed<T>(ToolErrorCodes.WorkspaceNotReady, $"Solution {bound.Path} is still loading; call again shortly.");

		LoadedSolution loaded;
		try
		{
			loaded = await bound.Load.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return Failed<T>(ToolErrorCodes.LoadFailed, $"Could not load {bound.Path}: {ex.Message}");
		}

		return await call(loaded.Analysis).ConfigureAwait(false);
	}

	private async Task<bool> FinishesInTimeAsync(Task load)
		=> await Task.WhenAny(load, Task.Delay(loadWait)).ConfigureAwait(false) == load;

	private static T Failed<T>(string code, string message) where T : IToolResult, new()
		=> new() { ErrorCode = code, ErrorMessage = message };

	/// <summary>The solution a connection answers from, named by the path it was loaded under.</summary>
	/// <param name="Path">The full path of the solution file.</param>
	/// <param name="Load">Its load, under way or finished.</param>
	private sealed record Bound(string Path, Task<LoadedSolution> Load);
}
