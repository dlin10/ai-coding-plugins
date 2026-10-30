using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using static RoslynMcpExtension.Cli.NativeMethods;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// A solution a Visual Studio has open, whose calls the daemon forwards to that Visual Studio over the pipe its extension
/// serves the solution on. The daemon holds no workspace for it, restores nothing and follows no file: Visual Studio
/// does all of that. See docs/adr/0007.
/// </summary>
internal sealed class VisualStudioSolution : HeldSolution
{
	/// <summary>Visual Studio keeps an instance of the pipe listening; this covers the moment it creates the next one.</summary>
	private const int ConnectTimeoutMilliseconds = 1000;

	private readonly NamedPipeClientStream _pipe;
	private readonly JsonRpc _rpc;

	/// <summary>Speaks the analysis contract over a connected pipe.</summary>
	/// <param name="pipe">The pipe, connected to Visual Studio.</param>
	/// <param name="connectedAt">When the daemon connected.</param>
	private VisualStudioSolution(NamedPipeClientStream pipe, DateTimeOffset connectedAt)
		: base(connectedAt)
	{
		_pipe = pipe;
		_rpc = new JsonRpc(pipe);
		Analysis = _rpc.Attach<IRoslynAnalysisRpc>();
		_rpc.Disconnected += (_, _) => Vanished?.Invoke();
		_rpc.StartListening();
	}

	public override IRoslynAnalysisRpc Analysis { get; }
	public override string ServedBy => SolutionServer.VisualStudio;

	/// <summary>Visual Studio closed the solution or exited, or the daemon released the connection.</summary>
	public bool Gone => _rpc.Completion.IsCompleted;

	/// <summary>Raised when Visual Studio closes the solution or exits.</summary>
	public override event Action? Vanished;

	/// <summary>
	/// Whether a Visual Studio of this plugin version serves the solution. Asks whether the pipe exists without opening it,
	/// so it takes no connection from Visual Studio, as opening it, or File.Exists on it, would; about 15 microseconds
	/// whether or not it exists.
	/// </summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	public static bool IsOpen(string solutionPath)
		// The error tells a pipe whose every instance is busy from one that does not exist.
		=> WaitNamedPipe(@"\\.\pipe\" + VisualStudioPipe.Name(solutionPath), 1) || Marshal.GetLastPInvokeError() == ErrorSemTimeout;

	/// <summary>A connection to the Visual Studio serving the solution; null when it does not accept one.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	/// <param name="now">The time to report the solution as loaded at.</param>
	public static VisualStudioSolution? TryConnect(string solutionPath, DateTimeOffset now)
	{
		// CurrentUserOnly refuses a pipe that another account created under this name.
		var pipe = new NamedPipeClientStream(".", VisualStudioPipe.Name(solutionPath), PipeDirection.InOut,
		                                     PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			pipe.Connect(ConnectTimeoutMilliseconds);
			return new VisualStudioSolution(pipe, now);
		}
		catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
		{
			pipe.Dispose();
			return null;
		}
	}

	public override void Dispose()
	{
		_rpc.Dispose();
		_pipe.Dispose();
	}
}
