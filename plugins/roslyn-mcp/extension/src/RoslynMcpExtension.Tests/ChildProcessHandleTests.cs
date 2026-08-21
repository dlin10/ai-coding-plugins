using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// Regression coverage for issue #31: an exception escaping the Process.Exited thread-pool
/// callback terminated devenv.exe. Every test here drives the callback against a Process that a
/// concurrent stop has already disposed.
/// </summary>
public sealed class ChildProcessHandleTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

	[Fact]
	public async Task KillingAndDisposingWhileTheExitCallbackIsQueuedNeverThrows()
	{
		using var guard = new UnhandledExceptionGuard();

		// KillNow disposes the Process immediately after Kill, which is exactly the window in
		// which the queued Exited callback used to read ExitCode on a de-associated Process.
		for (var i = 0; i < 40; i++)
		{
			var handle = ChildProcessHandle.Start(Sleeper(), null);
			handle.KillNow();
		}

		// A process that exits on its own puts the callback in flight before disposal even starts.
		for (var i = 0; i < 40; i++)
		{
			var handle = ChildProcessHandle.Start(ExitsImmediately(0), null);
			handle.Dispose();
		}

		await Task.Delay(TimeSpan.FromSeconds(1));
		guard.AssertNone();
	}

	[Fact]
	public async Task ExitedReportsTheChildExitCode()
	{
		using var handle = ChildProcessHandle.Start(ExitsImmediately(7), null);

		Assert.Equal(7, await WithTimeoutAsync(handle.Exited));
	}

	[Fact]
	public async Task StopAsyncOnAnAlreadyExitedChildCompletes()
	{
		using var guard = new UnhandledExceptionGuard();
		using var handle = ChildProcessHandle.Start(ExitsImmediately(0), null);

		await WithTimeoutAsync(handle.Exited);
		await WithTimeoutAsync(handle.StopAsync(TimeSpan.FromSeconds(2)));

		Assert.False(handle.IsRunning);
		guard.AssertNone();
	}

	[Fact]
	public async Task StopAndDisposeAreIdempotent()
	{
		using var guard = new UnhandledExceptionGuard();
		var handle = ChildProcessHandle.Start(Sleeper(), null);

		await WithTimeoutAsync(handle.StopAsync(TimeSpan.FromMilliseconds(200)));
		await WithTimeoutAsync(handle.StopAsync(TimeSpan.FromMilliseconds(200)));
		handle.Dispose();
		handle.Dispose();

		Assert.False(handle.IsRunning);
		guard.AssertNone();
	}

	[Fact]
	public async Task AChildThatIgnoresTheStopIsKilledAfterTheTimeout()
	{
		var handle = ChildProcessHandle.Start(Sleeper(), null);
		Assert.True(handle.IsRunning);

		var elapsed = Stopwatch.StartNew();
		await WithTimeoutAsync(handle.StopAsync(TimeSpan.FromMilliseconds(300)));
		elapsed.Stop();

		Assert.False(handle.IsRunning);
		Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"stop took {elapsed.Elapsed}");
	}

	[Fact]
	public async Task ExitedCompletesEvenWhenTheCallbackNeverRuns()
	{
		var handle = ChildProcessHandle.Start(Sleeper(), null);

		// Disposing unsubscribes the callback, so nothing else can complete the task; awaiters
		// must still be released rather than hanging forever.
		handle.Dispose();

		await WithTimeoutAsync(handle.Exited);
	}

	// Long enough to still be running when a test stops it, short enough that the one test
	// which deliberately abandons its child does not leave it around.
	private static ProcessStartInfo Sleeper() => new()
	{
		FileName = "cmd.exe",
		Arguments = "/c ping -n 6 127.0.0.1 > nul",
		UseShellExecute = false,
		CreateNoWindow = true,
		RedirectStandardError = true
	};

	private static ProcessStartInfo ExitsImmediately(int exitCode) => new()
	{
		FileName = "cmd.exe",
		Arguments = $"/c exit {exitCode}",
		UseShellExecute = false,
		CreateNoWindow = true,
		RedirectStandardError = true
	};

	// The awaited tasks belong to the code under test, which is the point of the helper.
	#pragma warning disable VSTHRD003
	private static async Task<T> WithTimeoutAsync<T>(Task<T> task)
	{
		Assert.Same(task, await Task.WhenAny(task, Task.Delay(TestTimeout)));
		return await task;
	}

	private static async Task WithTimeoutAsync(Task task)
	{
		Assert.Same(task, await Task.WhenAny(task, Task.Delay(TestTimeout)));
		await task;
	}
	#pragma warning restore VSTHRD003
}
