using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpExtension.Services;

internal static class OptionalGeneratorObservation
{
	internal static async Task<int?> RunAsync(Func<CancellationToken, Task<int>> operation, TimeSpan budget)
	{
		var cancellation = new CancellationTokenSource();
		using var timer = new CancellationTokenSource();
		var timeout = Task.Delay(budget, timer.Token);
		var token = cancellation.Token;

		// Include synchronous work before the operation returns its Task in the waiting budget.
		var pending = Task.Run(() => ObserveAsync(operation, token));

		if (await Task.WhenAny(pending, timeout).ConfigureAwait(false) == pending)
		{
			timer.Cancel();
			cancellation.Dispose();
			return await pending.ConfigureAwait(false);
		}

		// Cancel invokes callbacks synchronously; even requesting cancellation may block.
		// Stop waiting now and transfer ownership of the source to background cleanup.
		_ = Task.Run(() => CancelAndDrainAsync(pending, cancellation));
		return null;
	}

	private static async Task<int?> ObserveAsync(Func<CancellationToken, Task<int>> operation, CancellationToken token)
	{
		try
		{
			return await operation(token).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Await observes faults even after RunAsync has timed out; no separate
			// Task.Exception read or fault-only continuation is needed.
			return null;
		}
	}

	private static async Task CancelAndDrainAsync(Task<int?> pending, CancellationTokenSource cancellation)
	{
		try
		{
			try
			{
				cancellation.Cancel();
			}
			catch (Exception)
			{
				// A failing cancellation callback must not prevent draining the operation.
			}

			await pending.ConfigureAwait(false);
		}
		finally
		{
			// Keep the source alive until both cancellation callbacks and the operation finish.
			// If either never finishes, cleanup remains pending; RunAsync does not wait for it.
			cancellation.Dispose();
		}
	}
}
