using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Server.Tests;

public sealed class RpcClientTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task PipeDisconnectCancelsServerShutdownToken()
	{
		using var shutdownCts = new CancellationTokenSource();
		var pipeName = $"RoslynMcpTest_{Guid.NewGuid():N}";
		using var serverPipe = new NamedPipeServerStream(
			pipeName,
			PipeDirection.InOut,
			1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous);
		using var rpcClient = new RpcClient(shutdownCts);

		var connectTask = rpcClient.ConnectAsync(pipeName);
		await serverPipe.WaitForConnectionAsync().WaitAsync(TestTimeout);
		await connectTask.WaitAsync(TestTimeout);

		Assert.False(shutdownCts.IsCancellationRequested);

		serverPipe.Dispose();
		await WaitForCancellationAsync(shutdownCts.Token);
	}

	private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
	{
		var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(() => canceled.TrySetResult(true));
		await canceled.Task.WaitAsync(TestTimeout);
	}
}
