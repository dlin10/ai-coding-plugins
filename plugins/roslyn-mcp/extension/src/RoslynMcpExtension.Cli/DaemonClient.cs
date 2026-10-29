using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// The shim's side of the daemon. Holds the session's active solution and forwards every call to the daemon, which
/// it contacts, and starts when none answers, on the first call only: an MCP initialize with nothing after it, as a
/// host's server listing sends, must not start a daemon.
/// </summary>
/// <param name="pipeName">The pipe the daemon listens on.</param>
internal sealed class DaemonClient(string pipeName) : IRoslynAnalysisRpc, IDisposable
{
	private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

	private readonly SemaphoreSlim _connecting = new(1, 1);
	private JsonRpc? _rpc;
	private IRoslynDaemonRpc? _daemon;
	private volatile string? _solution;

	/// <summary>Loads a solution in the daemon and makes it the session's active one unless the load failed.</summary>
	/// <param name="solutionPath">The absolute path of the .sln or .slnx file.</param>
	public async Task<LoadResult> LoadAsync(string solutionPath)
	{
		var daemon = await ConnectAsync(rebind: false).ConfigureAwait(false);
		var result = await daemon.LoadAsync(solutionPath).ConfigureAwait(false);
		if (result.RequestSucceeded)
			_solution = result.Solution;
		return result;
	}

	/// <summary>
	/// Unloads a solution from the daemon. The session keeps it as its active solution, which its next call loads again.
	/// </summary>
	/// <param name="solutionPath">The absolute path of the .sln or .slnx file; null for the session's active solution.</param>
	public async Task<UnloadResult> UnloadAsync(string? solutionPath)
	{
		if ((solutionPath ?? _solution) is not { } path)
			return new UnloadResult { ErrorCode = ToolErrorCodes.NoSolution, ErrorMessage = "No solution is loaded in this session; name the solution to unload." };

		var daemon = await ConnectAsync(rebind: false).ConfigureAwait(false);
		return await daemon.UnloadAsync(path).ConfigureAwait(false);
	}

	public Task LogAsync(string message) => Task.CompletedTask;

	public Task ReadyAsync() => Task.CompletedTask;

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
		=> ForwardAsync([filePath], daemon => daemon.ValidateFileAsync(filePath, includeWarnings, runAnalyzers));

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
		=> ForwardAsync(filePaths ?? [], daemon => daemon.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, maxResults));

	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
		=> ForwardAsync([filePath], daemon => daemon.FindReferencesAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> ForwardAsync([filePath], daemon => daemon.FindImplementationsAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> ForwardAsync([filePath], daemon => daemon.FindCallersAsync(filePath, line, column, symbolId, projectName, maxResults));

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column)
		=> ForwardAsync([filePath], daemon => daemon.GoToDefinitionAsync(filePath, line, column));

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
		=> ForwardAsync([filePath], daemon => daemon.GetDocumentSymbolsAsync(filePath));

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
		=> ForwardAsync([], daemon => daemon.SearchSymbolsAsync(query, includeMetadata, maxResults));

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic)
		=> ForwardAsync([], daemon => daemon.FindDeadCodeAsync(maxResults, includeInternal, includePublic));

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> ForwardAsync([filePath], daemon => daemon.GetSymbolInfoAsync(filePath, line, column, symbolId, projectName));

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> ForwardAsync([filePath], daemon => daemon.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, maxResults));

	public void Dispose()
	{
		_rpc?.Dispose();
		_connecting.Dispose();
	}

	/// <summary>
	/// Forwards an analysis call, or tells the client to load a solution first. When the active solution does not hold
	/// a file the call names and the solution found from the file's folder does, the session switches to that one and
	/// the call answers from it, saying so; see docs/adr/0005.
	/// </summary>
	/// <param name="filePaths">The files the call names, with null for a file it may name and does not.</param>
	/// <param name="call">The call, on the daemon.</param>
	private async Task<T> ForwardAsync<T>(string?[] filePaths, Func<IRoslynDaemonRpc, Task<T>> call) where T : IToolResult, new()
	{
		if (_solution is not { } active)
		{
			return new T
			{
				ErrorCode = ToolErrorCodes.NoSolution,
				ErrorMessage = "No solution is loaded. Call roslyn_load first, with your working directory as directory."
			};
		}

		var daemon = await ConnectAsync(rebind: true).ConfigureAwait(false);
		var result = await call(daemon).ConfigureAwait(false);
		if (result.ErrorCode != ToolErrorCodes.DocumentNotFound || OwnerOf(filePaths) is not { } owner
		    || owner.Equals(active, StringComparison.OrdinalIgnoreCase))
			return result;

		var load = await daemon.LoadAsync(owner).ConfigureAwait(false);
		if (!load.RequestSucceeded)
		{
			result.ErrorMessage += $" Discovery from its folder found {owner}, which could not be loaded: {load.ErrorMessage}";
			return result;
		}

		var switched = await call(daemon).ConfigureAwait(false);
		if (switched.ErrorCode == ToolErrorCodes.DocumentNotFound)
		{
			// The solution found from the file's folder does not hold the file either, so the session stays where it was.
			await daemon.LoadAsync(active).ConfigureAwait(false);
			return result;
		}

		_solution = load.Solution;
		switched.SolutionSwitch = new SolutionSwitch { From = active, To = load.Solution };
		return switched;
	}

	/// <summary>The one solution discovery finds for every file a call names; null when it names none, or they differ.</summary>
	/// <param name="filePaths">The files the call names.</param>
	private static string? OwnerOf(string?[] filePaths)
		=> filePaths.OfType<string>().Where(Path.IsPathFullyQualified).Select(SolutionDiscovery.FindOwner)
		            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [{ } owner] ? owner : null;

	/// <summary>
	/// The connection to the daemon, made on first use and again after the daemon went away; starts a daemon when
	/// none answers.
	/// </summary>
	/// <param name="rebind">Load the active solution on a new connection, which the daemon knows nothing of yet.</param>
	private async Task<IRoslynDaemonRpc> ConnectAsync(bool rebind)
	{
		await _connecting.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_daemon != null && _rpc is { Completion.IsCompleted: false })
				return _daemon;

			_rpc?.Dispose();
			var stream = await TryConnectAsync(AnswerTimeout).ConfigureAwait(false);
			if (stream == null)
			{
				DaemonLauncher.Start(pipeName);
				stream = await TryConnectAsync(StartTimeout).ConfigureAwait(false)
				         ?? throw new TimeoutException($"The Roslyn MCP daemon started for pipe {pipeName} did not answer within {StartTimeout.TotalSeconds:0} seconds.");
			}

			_rpc = new JsonRpc(stream);
			_daemon = _rpc.Attach<IRoslynDaemonRpc>();
			_rpc.StartListening();
			if (rebind && _solution is { } solution)
				await _daemon.LoadAsync(solution).ConfigureAwait(false);
			return _daemon;
		}
		finally
		{
			_connecting.Release();
		}
	}

	/// <summary>A connection to the daemon's pipe, or null when no daemon answers in time.</summary>
	/// <param name="timeout">How long to wait for a listener.</param>
	private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout)
	{
		// CurrentUserOnly on the client refuses a pipe that another account created under this name.
		var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await stream.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
			return stream;
		}
		catch (TimeoutException)
		{
			stream.Dispose();
			return null;
		}
	}
}
