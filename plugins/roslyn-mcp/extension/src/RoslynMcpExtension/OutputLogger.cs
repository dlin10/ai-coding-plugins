using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RoslynMcpExtension;

/// <summary>
/// Sink for extension diagnostics. Keeps the server lifecycle types free of VS shell
/// dependencies so they can be exercised outside Visual Studio.
/// </summary>
internal interface IExtensionLogger
{
	void Log(string message);
}

internal sealed class OutputLogger : IExtensionLogger
{
	private static readonly Guid _paneGuid = new("d4e5f6a7-1b2c-3d4e-8f9a-0b1c2d3e4f5a");
	private readonly IVsOutputWindowPane _pane;

	private OutputLogger(IVsOutputWindowPane pane) => _pane = pane;

	public static OutputLogger? Create()
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		var outputWindow = (IVsOutputWindow?)Package.GetGlobalService(typeof(SVsOutputWindow));
		if (outputWindow == null) return null;

		var guid = _paneGuid;
		outputWindow.CreatePane(ref guid, "Roslyn MCP Extension", fInitVisible: 1, fClearWithSolution: 1);
		outputWindow.GetPane(ref guid, out var pane);

		return pane != null ? new OutputLogger(pane) : null;
	}

	public void Log(string message)
	{
		try
		{
			// OutputStringThreadSafe is explicitly designed to be called from any thread
#pragma warning disable VSTHRD010
			_pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
#pragma warning restore VSTHRD010
		}
		catch { /* Never crash VS */ }
	}
}
