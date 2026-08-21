using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Services;

internal delegate Task<ServerStartResult> McpServerSessionFactory(McpServerSessionOptions options);

/// <summary>
/// Single owner of the current <see cref="IMcpServerSession"/>. Every start/stop request goes
/// through one serialized queue, so the close-then-open burst a solution reload produces runs in
/// submission order instead of racing.
/// </summary>
internal sealed class McpServerController(McpServerSessionFactory sessionFactory, IExtensionLogger? logger)
{
	private readonly object _gate = new();
	private Task _queue = Task.CompletedTask;
	private IMcpServerSession? _current;
	private bool _disposed;

	/// <summary>Invoked when a start attempt fails in a way worth surfacing to the user.</summary>
	public Func<McpServerSessionOptions, ServerStartResult, Task>? StartFailedAsync { get; set; }

	public void EnqueueEnsure(McpServerSessionOptions options) => Enqueue(() => EnsureCoreAsync(options));

	public void EnqueueStop() => Enqueue(() => StopCoreAsync(graceful: true));

	/// <summary>
	/// Terminates the current session immediately without waiting, and stops accepting work.
	/// Used while VS is shutting down, where blocking the UI thread is not acceptable.
	/// </summary>
	public void ShutdownFast()
	{
		IMcpServerSession? session;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			session = _current;
			_current = null;
		}

		// Anything still queued no-ops now that _disposed is set, so it can simply be abandoned.
		if (session == null) return;

		session.KillNow();
		logger?.Log("MCP Server terminated");
	}

	/// <summary>Completes once everything queued so far has run.</summary>
	internal Task DrainAsync()
	{
		#pragma warning disable VSTHRD003 // The queue deliberately represents work started elsewhere.
		lock (_gate)
		{
			return _queue;
		}
		#pragma warning restore VSTHRD003
	}

	private void Enqueue(Func<Task> operation)
	{
		lock (_gate)
		{
			if (_disposed) return;

			// Chaining is what guarantees ordering. A semaphore would only keep the operations
			// from overlapping, not from running in the wrong order.
			_queue = _queue.ContinueWith(_ => RunSafelyAsync(operation),
										 CancellationToken.None,
										 TaskContinuationOptions.None,
										 TaskScheduler.Default)
						   .Unwrap();
		}
	}

	private async Task RunSafelyAsync(Func<Task> operation)
	{
		try
		{
			await operation().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			logger?.Log($"MCP server operation failed: {ex.Message}");
		}
	}

	private async Task EnsureCoreAsync(McpServerSessionOptions options)
	{
		IMcpServerSession? current;
		lock (_gate)
		{
			if (_disposed) return;
			current = _current;
		}

		if (current != null && current.IsRunning && current.Port == options.Port)
			return;

		if (current != null)
		{
			logger?.Log(current.IsRunning
				? $"Solution changed; restarting MCP server on port {options.Port}..."
				: "Previous MCP server session had ended; starting a new one...");
			await StopCoreAsync(graceful: true).ConfigureAwait(false);
		}

		var result = await sessionFactory(options).ConfigureAwait(false);

		lock (_gate)
		{
			if (_disposed)
			{
				result.Session?.KillNow();
				return;
			}

			_current = result.Session;
		}

		if (result.Succeeded) return;

		var handler = StartFailedAsync;
		if (handler != null && result.FailureKind is ServerStartFailureKind.ProcessExited
												  or ServerStartFailureKind.ReadinessTimeout)
		{
			await handler(options, result).ConfigureAwait(false);
		}
	}

	private async Task StopCoreAsync(bool graceful)
	{
		IMcpServerSession? session;
		lock (_gate)
		{
			session = _current;
			_current = null;
		}

		if (session == null) return;

		await session.StopAsync(graceful).ConfigureAwait(false);
		logger?.Log("MCP Server stopped");
	}
}
