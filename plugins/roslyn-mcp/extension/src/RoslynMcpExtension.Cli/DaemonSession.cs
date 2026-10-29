using System;
using System.IO;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// One shim's connection to the daemon. The analysis calls answer from the solution the connection last loaded, waiting
/// for its load as <see cref="LoadAsync"/> does, and loading it again when the daemon has unloaded it meanwhile. Every
/// call first brings the solution up to date with the disk; see docs/adr/0009.
/// </summary>
/// <param name="registry">The solutions the daemon holds, shared by every connection.</param>
/// <param name="loadWait">How long a call waits for a load, or for the solution to catch up, before answering that it is still loading.</param>
/// <param name="daemon">What the daemon reports about itself in every load result.</param>
internal sealed class DaemonSession(SolutionRegistry registry, TimeSpan loadWait, DaemonInfo daemon) : IRoslynDaemonRpc
{
	private volatile string? _bound;

	public async Task<LoadResult> LoadAsync(string solutionPath)
	{
		var path = Path.GetFullPath(solutionPath);
		var ready = ReadyAsync(path, []);
		var result = new LoadResult { Solution = path, Daemon = daemon };
		if (!await FinishesInTimeAsync(ready).ConfigureAwait(false))
		{
			_bound = path;
			result.Status = LoadStatus.Loading;
			result.RequestSucceeded = true;
			return result;
		}

		try
		{
			if (await ready.ConfigureAwait(false) is not { } loaded)
			{
				result.ErrorCode = ToolErrorCodes.LoadFailed;
				result.ErrorMessage = Vanished(path);
				return result;
			}

			_bound = path;
			result.Status = LoadStatus.Loaded;
			result.ProjectCount = loaded.Solution.ProjectIds.Count;
			result.DocumentCount = loaded.DocumentCount;
			result.LoadedAt = loaded.LoadedAt;
			result.Restore = loaded.Restore;
			result.RequestSucceeded = true;
		}
		catch (Exception ex)
		{
			result.ErrorCode = ToolErrorCodes.LoadFailed;
			result.ErrorMessage = $"Could not load {path}: {ex.Message}";
		}

		return result;
	}

	public Task<UnloadResult> UnloadAsync(string solutionPath)
	{
		var path = Path.GetFullPath(solutionPath);
		return Task.FromResult(new UnloadResult { Solution = path, Unloaded = registry.Unload(path), RequestSucceeded = true });
	}

	public Task LogAsync(string message) => Task.CompletedTask;

	public Task ReadyAsync() => Task.CompletedTask;

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
		=> CallAsync([filePath], analysis => analysis.ValidateFileAsync(filePath, includeWarnings, runAnalyzers));

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
		=> CallAsync(filePaths ?? [], analysis => analysis.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, maxResults));

	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
		=> CallAsync([filePath], analysis => analysis.FindReferencesAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> CallAsync([filePath], analysis => analysis.FindImplementationsAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> CallAsync([filePath], analysis => analysis.FindCallersAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column)
		=> CallAsync([filePath], analysis => analysis.GoToDefinitionAsync(filePath, line, column));

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
		=> CallAsync([filePath], analysis => analysis.GetDocumentSymbolsAsync(filePath));

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
		=> CallAsync([], analysis => analysis.SearchSymbolsAsync(query, includeMetadata, maxResults));

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic)
		=> CallAsync([], analysis => analysis.FindDeadCodeAsync(maxResults, includeInternal, includePublic));

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> CallAsync([filePath], analysis => analysis.GetSymbolInfoAsync(filePath, line, column, symbolId, projectName));

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> CallAsync([filePath], analysis => analysis.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, maxResults));

	/// <summary>Runs an analysis call on this connection's solution once it has loaded and caught up, or says why it cannot.</summary>
	/// <param name="files">The files the call names, with null for a file it may name and does not.</param>
	/// <param name="call">The call, on the solution's analysis service.</param>
	private async Task<T> CallAsync<T>(string?[] files, Func<AnalysisService, Task<T>> call) where T : IToolResult, new()
	{
		if (_bound is not { } path)
			return Failed<T>(ToolErrorCodes.NoSolution, "No solution is loaded on this connection.");

		var ready = ReadyAsync(path, files);
		if (!await FinishesInTimeAsync(ready).ConfigureAwait(false))
			return Failed<T>(ToolErrorCodes.WorkspaceNotReady, $"Solution {path} is still loading or catching up with the disk; call again shortly.");

		LoadedSolution? loaded;
		try
		{
			loaded = await ready.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return Failed<T>(ToolErrorCodes.LoadFailed, $"Could not load {path}: {ex.Message}");
		}

		return loaded == null
			? Failed<T>(ToolErrorCodes.LoadFailed, Vanished(path))
			: await call(loaded.Analysis).ConfigureAwait(false);
	}

	/// <summary>
	/// A solution loaded, again if the daemon unloaded it meanwhile, and caught up with the disk; null when its file is gone,
	/// in which case it is being unloaded.
	/// </summary>
	/// <param name="path">The full path of the solution file.</param>
	/// <param name="files">The files the call names, checked on disk whether or not a change to them was reported.</param>
	private async Task<LoadedSolution?> ReadyAsync(string path, string?[] files)
	{
		var loaded = await registry.Load(path).ConfigureAwait(false);
		return await loaded.CatchUpAsync(files).ConfigureAwait(false) ? loaded : null;
	}

	private async Task<bool> FinishesInTimeAsync(Task work)
		=> await Task.WhenAny(work, Task.Delay(loadWait)).ConfigureAwait(false) == work;

	private static string Vanished(string path) => $"Could not load {path}: the solution file is gone, so the daemon unloaded it.";

	private static T Failed<T>(string code, string message) where T : IToolResult, new()
		=> new() { ErrorCode = code, ErrorMessage = message };
}
