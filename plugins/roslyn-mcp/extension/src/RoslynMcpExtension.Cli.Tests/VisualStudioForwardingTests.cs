using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// The daemon forwarding a solution to the Visual Studio that has it open, through the contract a shim calls: a fake
/// Visual Studio serves the pipe named for one solution path, and the daemon loads everything else into a one-file
/// workspace, without MSBuild. See docs/adr/0007.
/// </summary>
public sealed class VisualStudioForwardingTests : IDisposable
{
	private const string GetInt = "M:Ns.Api.Get(System.Int32)";

	private readonly string _solution;
	private readonly string _worktreeSolution;
	private readonly string _file;
	private readonly OneFileLoader _loader;
	private readonly FakeTimeProvider _clock = new();
	private readonly SolutionRegistry _registry;
	private readonly DaemonSession _session;

	public VisualStudioForwardingTests()
	{
		// A folder of its own per test, so that no two tests share a pipe name.
		var root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "vs-" + Guid.NewGuid().ToString("N"));
		_solution = Path.Combine(root, "main", "Repo.slnx");
		_worktreeSolution = Path.Combine(root, "worktrees", "feature", "Repo.slnx");
		_file = Path.Combine(root, "main", "Api.cs");
		_loader = new OneFileLoader(_file);
		_registry = new SolutionRegistry(_loader, _clock, SolutionRegistry.DefaultUnloadAfter, SolutionRegistry.DefaultExitAfter);
		_session = new DaemonSession(_registry, TimeSpan.FromMinutes(1), new DaemonInfo());
	}

	public void Dispose() => _registry.Dispose();

	[Fact]
	public async Task CallsForTheExactPathGoToVisualStudioAndLoadNothingInTheDaemon()
	{
		using var visualStudio = new FakeVisualStudio(_solution);

		var load = await _session.LoadAsync(_solution.ToUpperInvariant().Replace('\\', '/'));
		var symbols = await _session.GetDocumentSymbolsAsync(_file);

		Assert.True(load.RequestSucceeded, load.ErrorMessage);
		Assert.Equal(LoadStatus.Loaded, load.Status);
		Assert.Equal(SolutionServer.VisualStudio, load.ServedBy);
		Assert.Null(load.Restore);
		Assert.Equal(FakeAnalysis.SymbolName, Assert.Single(symbols.Members).Name);
		Assert.Equal([$"GetDocumentSymbols({_file})"], visualStudio.Analysis.Calls);
		Assert.Equal(0, _loader.Loads);
	}

	[Fact]
	public async Task TheSameSolutionAtAnotherPathLoadsInTheDaemon()
	{
		using var visualStudio = new FakeVisualStudio(_solution);

		var load = await _session.LoadAsync(_worktreeSolution);
		var symbols = await _session.GetDocumentSymbolsAsync(_file);

		Assert.Equal(SolutionServer.Daemon, load.ServedBy);
		Assert.Equal(1, load.ProjectCount);
		Assert.Contains(symbols.Members, member => member.Name == "OneFile");
		Assert.Equal(1, _loader.Loads);
		Assert.Equal(0, visualStudio.Connections);
		Assert.Empty(visualStudio.Analysis.Calls);
	}

	[Fact]
	public async Task ASolutionTheDaemonLoadedGoesToVisualStudioOnceVisualStudioOpensIt()
	{
		var before = await _session.LoadAsync(_solution);
		using var visualStudio = new FakeVisualStudio(_solution);

		var symbols = await _session.GetDocumentSymbolsAsync(_file);
		var released = _loader.Released(0);
		var after = await _session.LoadAsync(_solution);

		Assert.Equal(SolutionServer.Daemon, before.ServedBy);
		Assert.Equal(FakeAnalysis.SymbolName, Assert.Single(symbols.Members).Name);
		Assert.True(released, "The daemon's workspace outlived the switch to Visual Studio.");
		Assert.Equal(SolutionServer.VisualStudio, after.ServedBy);
		Assert.Equal(1, _loader.Loads);
		Assert.Equal(1, visualStudio.Connections);
	}

	[Fact]
	public async Task OnceVisualStudioClosesTheSolutionTheNextCallLoadsItInTheDaemon()
	{
		var visualStudio = new FakeVisualStudio(_solution);
		var before = await _session.LoadAsync(_solution);

		visualStudio.Dispose();
		var symbols = await CallUntilAnsweredAsync(() => _session.GetDocumentSymbolsAsync(_file));
		var after = await _session.LoadAsync(_solution);

		Assert.Equal(SolutionServer.VisualStudio, before.ServedBy);
		Assert.True(symbols.RequestSucceeded, symbols.ErrorMessage);
		Assert.Contains(symbols.Members, member => member.Name == "OneFile");
		Assert.Equal(SolutionServer.Daemon, after.ServedBy);
		Assert.Equal(1, _loader.Loads);
	}

	[Fact]
	public async Task APipeThatRefusesTheDaemonIsLeftAloneForAMinute()
	{
		LoadResult refused;
		var name = VisualStudioPipe.Name(_solution);
		// A pipe whose one instance is taken: it exists, but no connection gets through.
		using (var taken = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
		                                             PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
		using (var holder = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
		{
			var accepted = taken.WaitForConnectionAsync();
			await holder.ConnectAsync(5_000);
			await accepted;

			refused = await _session.LoadAsync(_solution);
		}

		using var visualStudio = new FakeVisualStudio(_solution);
		await _session.GetDocumentSymbolsAsync(_file);
		var connectedWithinTheMinute = visualStudio.Connections;
		_clock.Advance(TimeSpan.FromMinutes(1));
		var symbols = await _session.GetDocumentSymbolsAsync(_file);

		Assert.Equal(SolutionServer.Daemon, refused.ServedBy);
		Assert.Equal(0, connectedWithinTheMinute);
		Assert.Equal(FakeAnalysis.SymbolName, Assert.Single(symbols.Members).Name);
		Assert.True(_loader.Released(0));
	}

	[Fact]
	public async Task ArgumentsAndResultsCrossThePipeToVisualStudioUnchanged()
	{
		using var visualStudio = new FakeVisualStudio(_solution);
		await _session.LoadAsync(_solution);

		var references = await _session.FindReferencesAsync(null, 0, 0, GetInt, null, 50);
		var diagnostics = await _session.GetDiagnosticsAsync([_file, "C:/b.cs"], null, true, false, 100);
		visualStudio.Analysis.GeneratedDocumentCount = null;
		var unobserved = await _session.ValidateFileAsync(_file, true, false);
		visualStudio.Analysis.GeneratedDocumentCount = 0;
		var noneGenerated = await _session.ValidateFileAsync(_file, true, false);

		Assert.True(references.RequestSucceeded, "An omitted filePath must arrive as null, not as an empty path");
		Assert.Equal(GetInt, references.Symbol!.SymbolId);
		Assert.Equal([_file, "C:/b.cs"], diagnostics.CheckedProjects);
		Assert.Null(unobserved.SourceGeneratedDocumentCount);
		Assert.Equal(0, noneGenerated.SourceGeneratedDocumentCount);
	}

	/// <summary>
	/// Calls until a call answers. Until the daemon has seen Visual Studio leave, a call may say the solution is not ready,
	/// and nothing else: Visual Studio leaving is no error.
	/// </summary>
	/// <param name="call">The call.</param>
	private static async Task<T> CallUntilAnsweredAsync<T>(Func<Task<T>> call) where T : IToolResult
	{
		for (var attempt = 1; ; attempt++)
		{
			var result = await call();
			if (result.RequestSucceeded || attempt == 50)
				return result;
			Assert.Equal(ToolErrorCodes.WorkspaceNotReady, result.ErrorCode);
			await Task.Delay(100);
		}
	}
}
