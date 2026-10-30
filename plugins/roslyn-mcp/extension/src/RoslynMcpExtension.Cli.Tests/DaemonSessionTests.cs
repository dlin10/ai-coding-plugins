using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>The daemon's load rules, through the contract a shim calls, over a loader that loads when told to.</summary>
public sealed class DaemonSessionTests
{
	private static readonly string SolutionPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Fake.slnx");
	private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "Fake.cs");

	private readonly GatedLoader _loader = new();

	private DaemonSession NewSession(SolutionRegistry registry, TimeSpan? loadWait = null)
		=> new(registry, loadWait ?? TimeSpan.FromMinutes(1), new DaemonInfo());

	[Fact]
	public async Task ConcurrentLoadsOfOnePathLoadItOnce()
	{
		var registry = new SolutionRegistry(_loader);
		var spellings = new[] { SolutionPath, SolutionPath.ToUpperInvariant(), SolutionPath.Replace('\\', '/') };

		var loads = Enumerable.Range(0, 6).Select(i => NewSession(registry).LoadAsync(spellings[i % spellings.Length])).ToList();
		_loader.Open();
		var results = await Task.WhenAll(loads);

		Assert.All(results, result => Assert.Equal(LoadStatus.Loaded, result.Status));
		Assert.Single(results.Select(result => result.LoadedAt).Distinct());
		Assert.Equal(1, _loader.Loads);
	}

	[Fact]
	public async Task ALoadPastTheWaitAnswersLoadingAndLaterCallsSucceed()
	{
		var session = NewSession(new SolutionRegistry(_loader), TimeSpan.FromMilliseconds(100));

		var first = await session.LoadAsync(SolutionPath);
		var early = await session.GetDocumentSymbolsAsync(FilePath);
		_loader.Open();
		var second = await session.LoadAsync(SolutionPath);
		var symbols = await session.GetDocumentSymbolsAsync(FilePath);

		Assert.True(first.RequestSucceeded);
		Assert.Equal(LoadStatus.Loading, first.Status);
		Assert.Equal(ToolErrorCodes.WorkspaceNotReady, early.ErrorCode);
		Assert.Equal(LoadStatus.Loaded, second.Status);
		Assert.Equal(1, _loader.Loads);
		Assert.True(symbols.RequestSucceeded, symbols.ErrorMessage);
		Assert.Contains(symbols.Members, member => member.Name == "Fake");
	}

	[Fact]
	public async Task AFailedLoadIsTriedAgain()
	{
		var session = NewSession(new SolutionRegistry(_loader));
		_loader.FailNext = true;
		_loader.Open();

		var failed = await session.LoadAsync(SolutionPath);
		var loaded = await session.LoadAsync(SolutionPath);

		Assert.False(failed.RequestSucceeded);
		Assert.Equal(ToolErrorCodes.LoadFailed, failed.ErrorCode);
		Assert.Contains("broken on purpose", failed.ErrorMessage);
		Assert.Equal(LoadStatus.Loaded, loaded.Status);
		Assert.Equal(2, _loader.Loads);
	}

	[Fact]
	public async Task ACallBeforeAnyLoadNamesNoSolution()
	{
		var result = await NewSession(new SolutionRegistry(_loader)).GetDocumentSymbolsAsync(FilePath);

		Assert.False(result.RequestSucceeded);
		Assert.Equal(ToolErrorCodes.NoSolution, result.ErrorCode);
		Assert.Equal(0, _loader.Loads);
	}

	/// <summary>Loads a one-file solution, but only once <see cref="Open"/> is called.</summary>
	private sealed class GatedLoader : ISolutionLoader
	{
		private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _loads;

		public int Loads => Volatile.Read(ref _loads);
		public bool FailNext { get; set; }

		public void Open() => _gate.TrySetResult();

		public async Task<LoadedSolution> LoadAsync(string solutionPath)
		{
			Interlocked.Increment(ref _loads);
			await _gate.Task;
			if (FailNext)
			{
				FailNext = false;
				throw new InvalidOperationException("broken on purpose");
			}

			var workspace = new AdhocWorkspace();
			workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath));
			var project = workspace.AddProject("Fake", LanguageNames.CSharp);
			workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Fake.cs",
			                                          loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class Fake { }"), VersionStamp.Create())),
			                                          filePath: FilePath));
			return new LoadedSolution(workspace, DateTimeOffset.UtcNow, restore: null);
		}
	}
}
