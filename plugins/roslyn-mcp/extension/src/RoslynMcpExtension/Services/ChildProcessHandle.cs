using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Services;

/// <summary>
/// Owns a single child server process and exposes its exit as an awaitable task instead of a
/// blocking wait, so the UI thread is never held while the process goes away.
/// </summary>
internal sealed class ChildProcessHandle : IDisposable
{
	private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

	private readonly Process _process;
	private readonly IExtensionLogger? _logger;
	private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _disposed;

	private ChildProcessHandle(Process process, IExtensionLogger? logger)
	{
		_process = process;
		_logger = logger;
	}

	/// <summary>Completes with the child's exit code, or null when it could not be read.</summary>
	public Task<int?> Exited => _exited.Task;

	public bool IsRunning
	{
		get
		{
			if (Volatile.Read(ref _disposed) != 0) return false;
			try { return !_process.HasExited; }
			catch { return false; }
		}
	}

	public static ChildProcessHandle Start(ProcessStartInfo startInfo, IExtensionLogger? logger)
	{
		var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
		var handle = new ChildProcessHandle(process, logger);

		process.Exited += handle.OnExited;
		process.ErrorDataReceived += handle.OnErrorDataReceived;

		try
		{
			process.Start();
			process.BeginErrorReadLine();
		}
		catch
		{
			handle.Dispose();
			throw;
		}

		return handle;
	}

	/// <summary>
	/// Waits up to <paramref name="timeout"/> for the child to exit on its own, then kills it.
	/// The caller is expected to have already signalled the child to shut down.
	/// </summary>
	public async Task StopAsync(TimeSpan timeout)
	{
		if (Volatile.Read(ref _disposed) != 0) return;

		if (!await WaitForExitAsync(timeout).ConfigureAwait(false))
		{
			_logger?.Log($"MCP Server did not exit within {timeout.TotalSeconds:0.#}s; terminating it.");
			TryKill();
			await WaitForExitAsync(KillGrace).ConfigureAwait(false);
		}

		Dispose();
	}

	/// <summary>Terminates the child immediately without waiting. Used while VS is shutting down.</summary>
	public void KillNow()
	{
		TryKill();
		Dispose();
	}

	// Process.Exited is raised on a raw thread-pool callback. .NET provides no way to drain a
	// callback that is already queued, so this can still run after Dispose() has de-associated
	// the Process - and an exception escaping a thread-pool callback terminates devenv.exe
	// (issue #31). The catch here is the fix, not defensive noise: do not remove it.
	private void OnExited(object? sender, EventArgs e)
	{
		try
		{
			var exitCode = TryReadExitCode();
			if (_exited.TrySetResult(exitCode))
				_logger?.Log($"MCP Server process exited with code {exitCode?.ToString() ?? "unknown"}");
		}
		catch
		{
			_exited.TrySetResult(null);
		}
	}

	// Also a raw thread-pool callback, and subject to the same rule.
	private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
	{
		try
		{
			if (!string.IsNullOrEmpty(e.Data))
				_logger?.Log($"[MCP Server] {e.Data}");
		}
		catch { }
	}

	private int? TryReadExitCode()
	{
		try
		{
			return _process.HasExited ? _process.ExitCode : (int?)null;
		}
		catch
		{
			// A concurrent stop may already have disposed the Process.
			return null;
		}
	}

	private async Task<bool> WaitForExitAsync(TimeSpan timeout)
	{
		if (_exited.Task.IsCompleted) return true;

		#pragma warning disable VSTHRD003 // The exit task represents the child process, not our context.
		var completed = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
		#pragma warning restore VSTHRD003
		return completed == _exited.Task;
	}

	private void TryKill()
	{
		try
		{
			if (!_process.HasExited)
				_process.Kill();
		}
		catch
		{
			// Already gone, or already disposed by a concurrent stop.
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		try { _process.Exited -= OnExited; } catch { }
		try { _process.ErrorDataReceived -= OnErrorDataReceived; } catch { }

		// Release anyone awaiting Exited even if the callback never got the chance to run.
		_exited.TrySetResult(null);

		try { _process.Dispose(); } catch { }
	}
}
