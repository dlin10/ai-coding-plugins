using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// A git branch switch makes VS close and reopen the solution back to back. These cover that the
/// resulting burst is serialized rather than racing, which is what left the old code restarting
/// two servers over one another.
/// </summary>
public sealed class McpServerControllerTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

	[Fact]
	public async Task ACloseOpenBurstRunsInOrderAndLeavesOneSession()
	{
		var recorder = new SessionRecorder(slowPort: 5056);
		var controller = new McpServerController(recorder.StartAsync, null);

		controller.EnqueueEnsure(Options(5056));
		controller.EnqueueStop();
		controller.EnqueueEnsure(Options(5057));
		await WithTimeoutAsync(controller.DrainAsync());

		// Interleaving would break these pairs apart: the slow start must finish before the
		// second one is even entered.
		Assert.Equal(new[] { "entered:5056", "started:5056", "stopped:5056", "entered:5057", "started:5057" },
					 recorder.Log);
		Assert.Equal(2, recorder.Sessions.Count);
		Assert.False(recorder.Sessions[0].IsRunning);
		Assert.True(recorder.Sessions[1].IsRunning);
	}

	[Fact]
	public async Task RestartingOnANewPortStopsThePreviousSessionGracefully()
	{
		var recorder = new SessionRecorder();
		var controller = new McpServerController(recorder.StartAsync, null);

		controller.EnqueueEnsure(Options(5056));
		controller.EnqueueEnsure(Options(5057));
		await WithTimeoutAsync(controller.DrainAsync());

		Assert.Equal(2, recorder.Sessions.Count);
		Assert.Equal(1, recorder.Sessions[0].StopCount);
		Assert.True(recorder.Sessions[0].LastStopWasGraceful);
		Assert.Equal(0, recorder.Sessions[0].KillCount);
	}

	[Fact]
	public async Task EnsuringTheSamePortAgainDoesNotRestart()
	{
		var recorder = new SessionRecorder();
		var controller = new McpServerController(recorder.StartAsync, null);

		controller.EnqueueEnsure(Options(5056));
		controller.EnqueueEnsure(Options(5056));
		await WithTimeoutAsync(controller.DrainAsync());

		Assert.Single(recorder.Sessions);
		Assert.Equal(0, recorder.Sessions[0].StopCount);
		Assert.True(recorder.Sessions[0].IsRunning);
	}

	[Fact]
	public async Task AnEndedSessionIsReplacedRatherThanReused()
	{
		var recorder = new SessionRecorder();
		var controller = new McpServerController(recorder.StartAsync, null);

		controller.EnqueueEnsure(Options(5056));
		await WithTimeoutAsync(controller.DrainAsync());

		// The child died or the pipe dropped: the same port must still produce a fresh session.
		recorder.Sessions[0].SimulateUnexpectedEnd();

		controller.EnqueueEnsure(Options(5056));
		await WithTimeoutAsync(controller.DrainAsync());

		Assert.Equal(2, recorder.Sessions.Count);
		Assert.True(recorder.Sessions[1].IsRunning);
	}

	[Fact]
	public async Task ShutdownFastKillsTheSessionAndStopsAcceptingWork()
	{
		var recorder = new SessionRecorder();
		var controller = new McpServerController(recorder.StartAsync, null);

		controller.EnqueueEnsure(Options(5056));
		await WithTimeoutAsync(controller.DrainAsync());

		controller.ShutdownFast();

		Assert.Equal(1, recorder.Sessions[0].KillCount);
		Assert.Equal(0, recorder.Sessions[0].StopCount);

		controller.EnqueueEnsure(Options(5057));
		await WithTimeoutAsync(controller.DrainAsync());

		Assert.Single(recorder.Sessions);
	}

	[Fact]
	public async Task AFailedStartIsReportedAndLeavesNoSession()
	{
		ServerStartResult failure = ServerStartResult.Failed(ServerStartFailureKind.ProcessExited,
															 "port in use");
		var reported = new List<int>();
		var controller = new McpServerController(_ => Task.FromResult(failure), null)
		{
			StartFailedAsync = (options, _) =>
			{
				reported.Add(options.Port);
				return Task.CompletedTask;
			}
		};

		controller.EnqueueEnsure(Options(5056));
		await WithTimeoutAsync(controller.DrainAsync());

		Assert.Equal(new[] { 5056 }, reported);
	}

	private static McpServerSessionOptions Options(int port)
		=> new(port, "Roslyn MCP Server", @"C:\repo", null);

	// The awaited tasks belong to the code under test, which is the point of the helper.
	#pragma warning disable VSTHRD003
	private static async Task WithTimeoutAsync(Task task)
	{
		Assert.Same(task, await Task.WhenAny(task, Task.Delay(TestTimeout)));
		await task;
	}
	#pragma warning restore VSTHRD003

	private sealed class SessionRecorder(int slowPort = -1)
	{
		public List<string> Log { get; } = [];
		public List<FakeSession> Sessions { get; } = [];

		public async Task<ServerStartResult> StartAsync(McpServerSessionOptions options)
		{
			Log.Add($"entered:{options.Port}");

			// Only the first start is slow, so an unserialized queue would let the second one
			// overtake it and scramble the log.
			if (options.Port == slowPort)
				await Task.Delay(250);

			var session = new FakeSession(options.Port, Log);
			Sessions.Add(session);
			Log.Add($"started:{options.Port}");
			return ServerStartResult.Started(session);
		}
	}

	private sealed class FakeSession(int port, List<string> log) : IMcpServerSession
	{
		public int Port { get; } = port;
		public bool IsRunning { get; private set; } = true;
		public int StopCount { get; private set; }
		public int KillCount { get; private set; }
		public bool LastStopWasGraceful { get; private set; }

		public void SimulateUnexpectedEnd() => IsRunning = false;

		public Task StopAsync(bool graceful)
		{
			StopCount++;
			LastStopWasGraceful = graceful;
			IsRunning = false;
			log.Add($"stopped:{Port}");
			return Task.CompletedTask;
		}

		public void KillNow()
		{
			KillCount++;
			IsRunning = false;
		}

		public void Dispose() => IsRunning = false;
	}
}
