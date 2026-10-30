using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// Stands in for a Visual Studio with a solution open: serves a <see cref="FakeAnalysis"/> on the pipe named for that
/// solution, to the current user only, until disposed, which is Visual Studio closing the solution.
/// </summary>
internal sealed class FakeVisualStudio : IDisposable
{
	private readonly object _gate = new();
	private readonly List<JsonRpc> _connections = [];
	private readonly string _pipeName;
	private NamedPipeServerStream? _listening;
	private int _connected;

	/// <summary>Opens the pipe of a solution and serves every connection on it.</summary>
	/// <param name="solutionPath">The solution file's path.</param>
	public FakeVisualStudio(string solutionPath)
	{
		_pipeName = VisualStudioPipe.Name(solutionPath);
		_listening = CreateInstance();
		_ = AcceptAsync();
	}

	public FakeAnalysis Analysis { get; } = new();

	/// <summary>How many connections the pipe has accepted so far.</summary>
	public int Connections => Volatile.Read(ref _connected);

	/// <summary>Closes the pipe and drops every connection on it.</summary>
	public void Dispose()
	{
		List<JsonRpc> connections;
		lock (_gate)
		{
			_listening?.Dispose();
			_listening = null;
			connections = [.. _connections];
		}

		foreach (var rpc in connections)
			rpc.Dispose();
	}

	private async Task AcceptAsync()
	{
		while (true)
		{
			NamedPipeServerStream? listening;
			lock (_gate)
				listening = _listening;
			if (listening == null)
				return;

			try
			{
				await listening.WaitForConnectionAsync();
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				return;
			}

			lock (_gate)
			{
				if (_listening == null)
					return;
				_listening = CreateInstance();
				Interlocked.Increment(ref _connected);
				var rpc = JsonRpc.Attach(listening, Analysis);
				_connections.Add(rpc);
			}
		}
	}

	private NamedPipeServerStream CreateInstance()
		=> new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
		       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
