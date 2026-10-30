using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpExtension.Cli.Tools;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// How a session's active solution is chosen by roslyn_load and switched by a call naming another solution's file,
/// through the shim's tool and client, over a daemon session served in this process from solutions that hold the
/// files a test lays out.
/// </summary>
public sealed class ActiveSolutionTests : IDisposable
{
	private readonly ScratchTree _tree = new();
	private readonly FakeLoader _loader = new();
	private readonly ConcurrentBag<IDisposable> _disposables = [];

	public void Dispose()
	{
		foreach (var disposable in _disposables) disposable.Dispose();
		_tree.Dispose();
	}

	[Fact]
	public async Task AnExplicitSolutionWinsOverDiscovery()
	{
		var app = _tree.In("app");
		var a = ScratchTree.Solution(app, "A.slnx");
		ScratchTree.Solution(app, "B.slnx");
		ScratchTree.Mark(app, """{ "solution": "B.slnx" }""");

		var load = await NewSession().Tool.Load(solution: a, directory: app);

		Assert.Equal((LoadStatus.Loaded, a, SolutionSource.Explicit), (load.Status, load.Solution, load.FoundBy));
		Assert.Equal([a], _loader.Loaded);
	}

	[Fact]
	public async Task ADirectoryLoadsTheSolutionDiscoveryFindsAndSaysHow()
	{
		var lib = ScratchTree.Solution(_tree.In("lib"), "Lib.slnx");

		var load = await NewSession().Tool.Load(directory: _tree.In("lib", "src"));

		Assert.Equal((LoadStatus.Loaded, lib, SolutionSource.Upward), (load.Status, load.Solution, load.FoundBy));
	}

	[Fact]
	public async Task NeedsChoiceListsTheCandidatesAndLoadsNothing()
	{
		var x = ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var y = ScratchTree.Solution(_tree.In("p", "y"), "Y.slnx");
		var session = NewSession();

		var load = await session.Tool.Load(directory: _tree.Repo);
		var call = await session.Client.SearchSymbolsAsync("Anything", false, 10);

		Assert.False(load.RequestSucceeded);
		Assert.Equal((LoadStatus.NeedsChoice, ToolErrorCodes.NeedsChoice), (load.Status, load.ErrorCode));
		Assert.Equal([x, y], load.Candidates!);
		Assert.Empty(_loader.Loaded);
		Assert.Equal(ToolErrorCodes.NoSolution, call.ErrorCode);
	}

	[Fact]
	public async Task ARememberedChoiceLoadsFromTheSameDirectoryWithoutAsking()
	{
		ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var y = ScratchTree.Solution(_tree.In("p", "y"), "Y.slnx");

		var asked = await NewSession().Tool.Load(directory: _tree.Repo);
		var chosen = await NewSession().Tool.Load(solution: y, remember: true);
		var later = await NewSession().Tool.Load(directory: _tree.Repo);

		Assert.Equal(LoadStatus.NeedsChoice, asked.Status);
		Assert.Equal((LoadStatus.Loaded, SolutionSource.Explicit), (chosen.Status, chosen.FoundBy));
		Assert.Equal(Path.Combine(_tree.Repo, "p", "y", SolutionDiscovery.MarkerFileName), chosen.Marker);
		Assert.Equal((LoadStatus.Loaded, y, SolutionSource.Downward, chosen.Marker), (later.Status, later.Solution, later.FoundBy, later.Marker));
	}

	[Fact]
	public async Task ACallNamingAFileOfAnotherSolutionSwitchesToItAndSaysSo()
	{
		var (x, xFile) = SolutionWithFile("x");
		var (y, yFile) = SolutionWithFile("y");
		var session = NewSession();
		await session.Tool.Load(solution: x);

		var toY = await session.Client.GetDocumentSymbolsAsync(yFile);
		var stay = await session.Client.GetDocumentSymbolsAsync(yFile);
		var toX = await session.Client.FindReferencesAsync(xFile, 1, 7, null, null, 10);

		Assert.True(toY.RequestSucceeded, toY.ErrorMessage);
		Assert.Equal((x, y), (toY.SolutionSwitch?.From, toY.SolutionSwitch?.To));
		Assert.True(stay.RequestSucceeded, stay.ErrorMessage);
		Assert.Null(stay.SolutionSwitch);
		Assert.True(toX.RequestSucceeded, toX.ErrorMessage);
		Assert.Equal((y, x), (toX.SolutionSwitch?.From, toX.SolutionSwitch?.To));
	}

	[Fact]
	public async Task AFileTheSolutionOfItsFolderDoesNotHoldLeavesTheSessionWhereItWas()
	{
		var (x, xFile) = SolutionWithFile("x");
		SolutionWithFile("y");
		var stray = Path.Combine(_tree.In("p", "y", "tools"), "Stray.cs");
		var session = NewSession();
		await session.Tool.Load(solution: x);

		var missing = await session.Client.GetDocumentSymbolsAsync(stray);
		var after = await session.Client.GetDocumentSymbolsAsync(xFile);

		Assert.Equal(ToolErrorCodes.DocumentNotFound, missing.ErrorCode);
		Assert.Contains(x, missing.ErrorMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Null(missing.SolutionSwitch);
		Assert.True(after.RequestSucceeded, after.ErrorMessage);
		Assert.Null(after.SolutionSwitch);
	}

	/// <summary>A solution under p/<paramref name="name"/> holding one C# file, both laid out on disk.</summary>
	/// <param name="name">The folder and the solution's name.</param>
	private (string Solution, string File) SolutionWithFile(string name)
	{
		var solution = ScratchTree.Solution(_tree.In("p", name), name.ToUpperInvariant() + ".slnx");
		var file = Path.Combine(_tree.In("p", name, "src"), name.ToUpperInvariant() + ".cs");
		File.WriteAllText(file, $"class {name.ToUpperInvariant()} {{ }}");
		_loader.Documents[solution] = file;
		return (solution, file);
	}

	/// <summary>A shim session, its client connected to a daemon session of its own served in this process.</summary>
	private Session NewSession()
	{
		var pipeName = Daemons.NewPipeName();
		var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
		                                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		var registry = new SolutionRegistry(_loader);
		_ = server.WaitForConnectionAsync().ContinueWith(
			_ => _disposables.Add(JsonRpc.Attach(server, new DaemonSession(registry, TimeSpan.FromMinutes(1), new DaemonInfo()))),
			CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		var client = new DaemonClient(pipeName);
		_disposables.Add(client);
		_disposables.Add(server);
		return new Session(client, new LoadTool(client));
	}

	/// <summary>A shim's session: the client every analysis tool calls, and roslyn_load.</summary>
	/// <param name="Client">The client the analysis tools call.</param>
	/// <param name="Tool">roslyn_load over that client.</param>
	private sealed record Session(DaemonClient Client, LoadTool Tool);

	/// <summary>Loads each solution as one project holding the file <see cref="Documents"/> gives it, if any.</summary>
	private sealed class FakeLoader : ISolutionLoader
	{
		private readonly ConcurrentQueue<string> _loaded = new();

		public Dictionary<string, string> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyList<string> Loaded => [.. _loaded];

		public Task<LoadedSolution> LoadAsync(string solutionPath)
		{
			_loaded.Enqueue(solutionPath);
			var workspace = new AdhocWorkspace();
			workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath));
			var project = workspace.AddProject(Path.GetFileNameWithoutExtension(solutionPath), LanguageNames.CSharp);
			if (Documents.TryGetValue(solutionPath, out var file))
			{
				workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), Path.GetFileName(file),
				                                          loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(file)), VersionStamp.Create())),
				                                          filePath: file));
			}

			return Task.FromResult(new LoadedSolution(workspace, DateTimeOffset.UtcNow, restore: null));
		}
	}
}
