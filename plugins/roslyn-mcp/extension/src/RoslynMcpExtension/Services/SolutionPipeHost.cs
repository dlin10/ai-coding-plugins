using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Services;

/// <summary>
/// Keeps the pipe of the solution Visual Studio has open: opens it once Visual Studio has loaded the solution, and closes
/// it before the solution closes, so that the daemon never forwards to a workspace that is filling up or emptying. Holds
/// none while no solution is open. See docs/adr/0007.
/// </summary>
/// <param name="openPipe">Opens the pipe of a solution, named by the full path of its file.</param>
/// <param name="solutionLoaded">Completes once Visual Studio has loaded the solution it opened, at once if it has.</param>
/// <param name="logger">Where the extension logs; null for nowhere.</param>
internal sealed class SolutionPipeHost(Func<string, IDisposable> openPipe,
                                       Func<CancellationToken, Task> solutionLoaded,
                                       IExtensionLogger? logger) : IDisposable
{
	private readonly object _gate = new();
	private CancellationTokenSource? _opening;
	private IDisposable? _pipe;
	private string? _solution;
	private bool _disposed;

	/// <summary>Opens the pipe of a solution Visual Studio has just opened, once it has loaded the solution.</summary>
	/// <param name="solutionPath">The full path of the solution file.</param>
	public void SolutionOpened(string solutionPath)
	{
		var opening = new CancellationTokenSource();
		lock (_gate)
		{
			if (_disposed)
				return;
			CloseCore();
			_opening = opening;
		}

		_ = OpenWhenLoadedAsync(solutionPath, opening);
	}

	/// <summary>Closes the pipe, and drops every daemon's connection, before Visual Studio closes the solution.</summary>
	public void SolutionClosing()
	{
		lock (_gate)
			CloseCore();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			CloseCore();
		}
	}

	/// <summary>Opens the pipe once the solution has loaded, unless it closed or another opened meanwhile.</summary>
	/// <param name="solutionPath">The full path of the solution file.</param>
	/// <param name="opening">Cancelled when the solution closes first.</param>
	private async Task OpenWhenLoadedAsync(string solutionPath, CancellationTokenSource opening)
	{
		try
		{
			await solutionLoaded(opening.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			// Serving a workspace that may still be filling up beats not serving it.
			logger?.Log($"Could not wait for {solutionPath} to load: {ex.Message}");
		}

		lock (_gate)
		{
			if (_opening != opening)
				return;
			_opening = null;
			try
			{
				_pipe = openPipe(solutionPath);
				_solution = solutionPath;
				logger?.Log($"Serving {solutionPath} to the Roslyn MCP daemon");
			}
			catch (Exception ex)
			{
				logger?.Log($"Could not serve {solutionPath} to the Roslyn MCP daemon: {ex.Message}");
			}
		}
	}

	/// <summary>Stops a pending open and closes the pipe. Under the gate.</summary>
	private void CloseCore()
	{
		// Cancelled, not disposed: the pending open may still read the token.
		_opening?.Cancel();
		_opening = null;
		if (_pipe == null)
			return;

		_pipe.Dispose();
		_pipe = null;
		logger?.Log($"Stopped serving {_solution} to the Roslyn MCP daemon");
		_solution = null;
	}
}
