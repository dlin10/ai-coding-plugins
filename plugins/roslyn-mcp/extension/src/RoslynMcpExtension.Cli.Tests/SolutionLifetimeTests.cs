using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// How long the daemon keeps a solution, and itself, on a fake clock: through the contract a shim calls, over a registry
/// that unloads after 2 hours without calls and finds itself idle 30 minutes after its last unload.
/// </summary>
public sealed class SolutionLifetimeTests : IDisposable
{
	private static readonly string SolutionPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Lifetime.slnx");
	private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Lifetime.cs");

	private readonly FakeTimeProvider _clock = new();
	private readonly OneFileLoader _loader = new(FilePath);
	private readonly SolutionRegistry _registry;
	private readonly DaemonSession _session;

	public SolutionLifetimeTests()
	{
		_registry = new SolutionRegistry(_loader, _clock, SolutionRegistry.DefaultUnloadAfter, SolutionRegistry.DefaultExitAfter);
		_session = new DaemonSession(_registry, TimeSpan.FromMinutes(1), new DaemonInfo());
	}

	public void Dispose() => _registry.Dispose();

	[Fact]
	public async Task ASolutionUnloadsAfterTwoHoursWithoutCalls()
	{
		await _session.LoadAsync(SolutionPath);

		_clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
		var symbols = await _session.GetDocumentSymbolsAsync(FilePath);

		Assert.True(symbols.RequestSucceeded, symbols.ErrorMessage);
		Assert.Equal(2, _loader.Loads);
		Assert.True(_loader.Released(0));
	}

	[Fact]
	public async Task AnyCallResetsTheTimeWithoutCalls()
	{
		await _session.LoadAsync(SolutionPath);

		_clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
		await _session.SearchSymbolsAsync("OneFile", false, 10);
		_clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
		var stillLoaded = _loader.Loads;
		await _session.GetDocumentSymbolsAsync(FilePath);
		_clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
		await _session.GetDocumentSymbolsAsync(FilePath);

		Assert.Equal(1, stillLoaded);
		Assert.Equal(2, _loader.Loads);
	}

	[Fact]
	public async Task TheDaemonExitsThirtyMinutesAfterItsLastSolutionIsUnloaded()
	{
		await _session.LoadAsync(SolutionPath);

		_clock.Advance(TimeSpan.FromHours(1));
		var idleWhileHolding = _registry.Idle.IsCompleted;
		// The unload comes within a sweep, a minute at most, of the solution's 2 hours without calls.
		_clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
		var unloaded = _loader.Released(0);
		_clock.Advance(TimeSpan.FromMinutes(28));
		var idleTooEarly = _registry.Idle.IsCompleted;
		_clock.Advance(TimeSpan.FromMinutes(4));

		Assert.False(idleWhileHolding);
		Assert.True(unloaded);
		Assert.False(idleTooEarly);
		Assert.True(_registry.Idle.IsCompleted);
	}

	[Fact]
	public async Task AnUnloadedSolutionLoadedAgainKeepsTheDaemonRunning()
	{
		await _session.LoadAsync(SolutionPath);
		_clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

		_clock.Advance(TimeSpan.FromMinutes(20));
		await _session.GetDocumentSymbolsAsync(FilePath);
		_clock.Advance(TimeSpan.FromMinutes(40));

		Assert.Equal(2, _loader.Loads);
		Assert.False(_registry.Idle.IsCompleted);
	}
}
