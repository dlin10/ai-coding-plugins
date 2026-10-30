using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>The name the extension and the daemon both give a solution's pipe, and how the daemon looks for it. See docs/adr/0007.</summary>
public sealed class VisualStudioPipeTests
{
	/// <summary>SHA-256 of C:\REPO\SAMPLE.SLNX; the extension's tests expect the same of .NET Framework.</summary>
	private const string SampleHash = "0CC39A9A931FDEE0DFB8C30E489B34692BD0C57F9D9197DBE47D22F1EECEDF4E";

	[Fact]
	public void OneSolutionFileHasOnePipeNameInAnyLetterCaseAndWithEitherSlash()
	{
		var name = VisualStudioPipe.Name(@"C:\Repo\Sample.slnx");

		Assert.Equal($"roslyn-mcp-vs-{ExtensionVersion.Value}-{SampleHash}", name);
		Assert.Equal(name, VisualStudioPipe.Name("c:/repo/SAMPLE.SLNX"));
		Assert.Equal(name, VisualStudioPipe.Name(@"C:\Repo\Other\..\Sample.slnx"));
		Assert.NotEqual(name, VisualStudioPipe.Name(@"C:\Worktrees\feature\Sample.slnx"));
	}

	[Fact]
	public void LookingForThePipeTakesNoConnectionFromVisualStudio()
	{
		var solution = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "probe-" + Guid.NewGuid().ToString("N"), "Repo.slnx");
		var absent = VisualStudioSolution.IsOpen(solution);

		bool found, busy, gone;
		bool acceptedByProbes;
		using (var server = new NamedPipeServerStream(VisualStudioPipe.Name(solution), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
		                                              PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
		{
			var accepted = server.WaitForConnectionAsync();
			found = Enumerable.Range(0, 100).All(_ => VisualStudioSolution.IsOpen(solution));
			acceptedByProbes = accepted.IsCompleted;

			// The one instance is still there for the daemon, and while it serves the daemon the pipe still exists.
			using var connection = VisualStudioSolution.TryConnect(solution, DateTimeOffset.UtcNow);
			Assert.NotNull(connection);
			busy = VisualStudioSolution.IsOpen(solution);
		}

		gone = VisualStudioSolution.IsOpen(solution);

		Assert.False(absent);
		Assert.True(found);
		Assert.False(acceptedByProbes, "A probe took the connection Visual Studio was waiting for.");
		Assert.True(busy);
		Assert.False(gone);
	}
}
