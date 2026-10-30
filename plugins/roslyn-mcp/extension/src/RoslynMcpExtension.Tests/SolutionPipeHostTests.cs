using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// The pipe the extension serves its open solution on, as the solution events drive it: open once the solution has
/// loaded, closed when it closes. A client here plays the daemon. See docs/adr/0007.
/// </summary>
public sealed class SolutionPipeHostTests : IDisposable
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "roslyn-mcp-pipe-" + Guid.NewGuid().ToString("N"));
	private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly SolutionPipeHost _host;

	public SolutionPipeHostTests() => _host = new SolutionPipeHost(path => SolutionPipe.Open(path, new FakeAnalysis()), SolutionLoadedAsync, null);

	public void Dispose() => _host.Dispose();

	[Fact]
	public async Task ThePipeOpensOnceTheSolutionHasLoaded()
	{
		var solution = Solution("Repo.slnx");

		_host.SolutionOpened(solution);
		var beforeLoad = TryConnect(solution);
		_loaded.SetResult(true);
		using var daemon = await ConnectAsync(solution);
		var symbols = await daemon.Analysis.GetDocumentSymbolsAsync(Path.Combine(_folder, "Api.cs"));

		Assert.Null(beforeLoad);
		Assert.Equal(FakeAnalysis.SymbolName, symbols.Members.Single().Name);
	}

	[Fact]
	public async Task ClosingTheSolutionClosesThePipeAndDropsTheDaemon()
	{
		var solution = Solution("Repo.slnx");
		_loaded.SetResult(true);
		_host.SolutionOpened(solution);
		using var daemon = await ConnectAsync(solution);

		_host.SolutionClosing();
		// The connection's completion is what the test watches for.
		#pragma warning disable VSTHRD003
		var dropped = await Task.WhenAny(daemon.Rpc.Completion, Task.Delay(TestTimeout)) == daemon.Rpc.Completion;
		#pragma warning restore VSTHRD003

		Assert.True(dropped, "The daemon's connection outlived the solution.");
		Assert.Null(TryConnect(solution));
	}

	[Fact]
	public async Task ASolutionClosedBeforeItLoadedOpensNoPipe()
	{
		var solution = Solution("Repo.slnx");

		_host.SolutionOpened(solution);
		_host.SolutionClosing();
		_loaded.SetResult(true);
		await Task.Delay(500);

		Assert.Null(TryConnect(solution));
	}

	[Fact]
	public async Task TheNextSolutionOpenedHasAPipeOfItsOwn()
	{
		var first = Solution("First.slnx");
		var second = Solution("Second.slnx");
		_loaded.SetResult(true);

		_host.SolutionOpened(first);
		(await ConnectAsync(first)).Dispose();
		_host.SolutionClosing();
		_host.SolutionOpened(second);
		using var daemon = await ConnectAsync(second);

		Assert.Null(TryConnect(first));
	}

	[Fact]
	public async Task ADaemonThatLeftIsFollowedByAnotherAndTwoAreServedAtOnce()
	{
		var solution = Solution("Repo.slnx");
		var file = Path.Combine(_folder, "Api.cs");
		_loaded.SetResult(true);
		_host.SolutionOpened(solution);

		using (var gone = await ConnectAsync(solution))
			await gone.Analysis.GetDocumentSymbolsAsync(file);
		using var first = await ConnectAsync(solution);
		using var second = await ConnectAsync(solution);
		var answers = await Task.WhenAll(first.Analysis.GetDocumentSymbolsAsync(file), second.Analysis.GetDocumentSymbolsAsync(file));

		Assert.All(answers, answer => Assert.True(answer.RequestSucceeded));
	}

	[Fact]
	public async Task ThePipeOpensToTheCurrentUserOnly()
	{
		var solution = Solution("Repo.slnx");
		_loaded.SetResult(true);
		_host.SolutionOpened(solution);
		using var daemon = await ConnectAsync(solution);

		var security = daemon.Pipe.GetAccessControl();
		var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());

		using var identity = WindowsIdentity.GetCurrent();
		Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
		Assert.Equal(identity.Owner, rule.IdentityReference);
		Assert.Equal(identity.Owner, security.GetOwner(typeof(SecurityIdentifier)));
	}

	[Fact]
	public void TheExtensionNamesThePipeAsTheDaemonDoes()
	{
		// SHA-256 of C:\REPO\SAMPLE.SLNX, as the daemon's tests expect it of .NET.
		Assert.Equal($"roslyn-mcp-vs-{ExtensionVersion.Value}-0CC39A9A931FDEE0DFB8C30E489B34692BD0C57F9D9197DBE47D22F1EECEDF4E",
		             VisualStudioPipe.Name("c:/repo/SAMPLE.SLNX"));
	}

	private string Solution(string fileName) => Path.Combine(_folder, fileName);

	// The task stands for Visual Studio's solution load, which the test completes.
	#pragma warning disable VSTHRD003
	private async Task SolutionLoadedAsync(CancellationToken cancellationToken)
	{
		var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
			await Task.WhenAny(_loaded.Task, cancelled.Task);
		cancellationToken.ThrowIfCancellationRequested();
	}
	#pragma warning restore VSTHRD003

	/// <summary>Connects to a solution's pipe, which opens after the solution has loaded, within the test's time.</summary>
	/// <param name="solution">The solution's path.</param>
	private static async Task<Daemon> ConnectAsync(string solution)
	{
		var deadline = DateTime.UtcNow + TestTimeout;
		while (true)
		{
			if (TryConnect(solution) is { } daemon)
				return daemon;
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"No pipe for {solution} within {TestTimeout.TotalSeconds:0} seconds.");
			await Task.Delay(50);
		}
	}

	/// <summary>A connection to a solution's pipe; null when there is none to connect to.</summary>
	/// <param name="solution">The solution's path.</param>
	private static Daemon? TryConnect(string solution)
	{
		var pipe = new NamedPipeClientStream(".", VisualStudioPipe.Name(solution), PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			pipe.Connect(100);
			return new Daemon(pipe);
		}
		catch (TimeoutException)
		{
			pipe.Dispose();
			return null;
		}
	}

	/// <summary>A client playing the daemon, speaking the analysis contract over a connected pipe.</summary>
	private sealed class Daemon : IDisposable
	{
		/// <summary>Speaks the analysis contract over a connected pipe.</summary>
		/// <param name="pipe">The connected pipe.</param>
		public Daemon(NamedPipeClientStream pipe)
		{
			Pipe = pipe;
			Rpc = new JsonRpc(pipe);
			Analysis = Rpc.Attach<IRoslynAnalysisRpc>();
			Rpc.StartListening();
		}

		public NamedPipeClientStream Pipe { get; }
		public JsonRpc Rpc { get; }
		public IRoslynAnalysisRpc Analysis { get; }

		public void Dispose()
		{
			Rpc.Dispose();
			Pipe.Dispose();
		}
	}

	/// <summary>Answers the one call these tests make, without a workspace.</summary>
	private sealed class FakeAnalysis : IRoslynAnalysisRpc
	{
		public const string SymbolName = "FromFake";

		public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
			=> Task.FromResult(new SymbolListResult { Members = [new SymbolLocation { Name = SymbolName, FilePath = filePath }], RequestSucceeded = true });

		public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers) => throw new NotSupportedException();
		public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column) => throw new NotSupportedException();
		public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults) => throw new NotSupportedException();
		public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic) => throw new NotSupportedException();
		public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName) => throw new NotSupportedException();
		public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName, string? memberFilter, int maxResults) => throw new NotSupportedException();
	}
}
