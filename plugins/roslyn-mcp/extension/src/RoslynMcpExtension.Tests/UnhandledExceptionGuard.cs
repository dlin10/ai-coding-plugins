using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// Records exceptions that escape to the CLR rather than to a caller. An unhandled exception on a
/// thread-pool callback terminates the host process, so a captured one here normally means the
/// test run is already doomed - but it also catches the milder unobserved-task case.
/// </summary>
internal sealed class UnhandledExceptionGuard : IDisposable
{
	private readonly List<string> _failures = [];

	public UnhandledExceptionGuard()
	{
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
	}

	public void AssertNone()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.Empty(_failures);
	}

	public void Dispose()
	{
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
		TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
	}

	private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		lock (_failures)
			_failures.Add($"unhandled: {e.ExceptionObject}");
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		lock (_failures)
			_failures.Add($"unobserved: {e.Exception}");
	}
}
