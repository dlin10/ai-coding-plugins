using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;

namespace RoslynMcpExtension.Services;

/// <summary>
/// Serves the analysis contract of Visual Studio's open solution to the Roslyn MCP daemon over the pipe named for that
/// solution, until disposed: to one daemon after another, and to several at once. See docs/adr/0007.
/// </summary>
internal sealed class SolutionPipe : IDisposable
{
	private readonly object _gate = new();
	private readonly HashSet<JsonRpc> _connections = [];
	private readonly IRoslynAnalysisRpc _analysis;
	private readonly PipeSecurity _security;
	private NamedPipeServerStream? _listening;

	/// <summary>Creates the pipe's first instance.</summary>
	/// <param name="name">The pipe's name.</param>
	/// <param name="analysis">Answers the calls that come over the pipe.</param>
	private SolutionPipe(string name, IRoslynAnalysisRpc analysis)
	{
		Name = name;
		_analysis = analysis;
		_security = CurrentUserOnly();
		_listening = CreateInstance();
	}

	public string Name { get; }

	/// <summary>Opens the pipe of a solution and accepts connections on it.</summary>
	/// <param name="solutionPath">The full path of the solution file Visual Studio has open.</param>
	/// <param name="analysis">Answers the calls that come over the pipe.</param>
	public static SolutionPipe Open(string solutionPath, IRoslynAnalysisRpc analysis)
	{
		var pipe = new SolutionPipe(VisualStudioPipe.Name(solutionPath), analysis);
		_ = pipe.AcceptAsync();
		return pipe;
	}

	/// <summary>Closes the pipe and every connection on it.</summary>
	public void Dispose()
	{
		NamedPipeServerStream? listening;
		List<JsonRpc> connections;
		lock (_gate)
		{
			listening = _listening;
			_listening = null;
			connections = [.. _connections];
			_connections.Clear();
		}

		listening?.Dispose();
		foreach (var rpc in connections)
			rpc.Dispose();
	}

	/// <summary>Serves each connection on an instance of its own, keeping another instance listening for the next.</summary>
	private async Task AcceptAsync()
	{
		while (true)
		{
			NamedPipeServerStream? listening;
			lock (_gate)
				listening = _listening;
			if (listening == null)
				return;

			Exception? failure = null;
			try
			{
				await listening.WaitForConnectionAsync().ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				// Disposed, or a client that connected and left before the wait began.
				failure = ex;
			}

			lock (_gate)
			{
				if (_listening == null)
					return;

				// The next instance exists before this one is served, so that a daemon connecting meanwhile finds a listener.
				_listening = CreateInstance();
				if (failure == null)
					Serve(listening);
			}

			if (failure != null)
				listening.Dispose();
		}
	}

	/// <summary>Answers the calls on a connected instance until the daemon leaves or the pipe is disposed. Under the gate.</summary>
	/// <param name="connected">The connected instance.</param>
	private void Serve(NamedPipeServerStream connected)
	{
		var rpc = new JsonRpc(connected);
		rpc.AddLocalRpcTarget(_analysis);
		_connections.Add(rpc);
		_ = rpc.Completion.ContinueWith(_ =>
		{
			lock (_gate)
				_connections.Remove(rpc);
			connected.Dispose();
		}, TaskScheduler.Default);
		rpc.StartListening();
	}

	/// <summary>An instance of the pipe, any number of which may exist at once.</summary>
	private NamedPipeServerStream CreateInstance()
		=> new(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
		       PipeOptions.Asynchronous, 0, 0, _security);

	/// <summary>
	/// Access for the current Windows user only, owned by that user: what PipeOptions.CurrentUserOnly sets on .NET, which
	/// .NET Framework lacks. The daemon's own CurrentUserOnly refuses a pipe another account owns.
	/// </summary>
	private static PipeSecurity CurrentUserOnly()
	{
		using var identity = WindowsIdentity.GetCurrent();
		var user = identity.Owner!;
		var security = new PipeSecurity();
		// Without CreateNewInstance the pipe's second instance is refused, and only one daemon is ever served.
		security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
		                                          AccessControlType.Allow));
		security.SetOwner(user);
		return security;
	}
}
