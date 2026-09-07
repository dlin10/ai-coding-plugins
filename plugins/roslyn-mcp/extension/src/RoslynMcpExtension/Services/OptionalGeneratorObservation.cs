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
		// Roslyn may do synchronous work before returning its Task; include that in the budget.
		var pending = Task.Run(() => operation(token));
		if (await Task.WhenAny(pending, timeout).ConfigureAwait(false) == pending)
		{
			timer.Cancel();
			try { return await pending.ConfigureAwait(false); }
			catch (Exception) { return null; }
			finally { cancellation.Dispose(); }
		}

		// Cancellation callbacks must not extend the caller's budget either.
		_ = Task.Run(() =>
		{
			try { cancellation.Cancel(); }
			catch (Exception) { /* Optional cancellation callbacks cannot fail validation. */ }
			finally { cancellation.Dispose(); }
		});
		_ = pending.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		return null;
	}
}
