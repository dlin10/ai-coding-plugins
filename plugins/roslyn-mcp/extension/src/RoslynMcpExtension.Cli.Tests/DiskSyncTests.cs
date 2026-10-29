using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// How a solution's documents follow the disk, on a solution laid out under the temporary folder and opened without
/// MSBuild. No watcher runs: each test reports the changes it wants reported, so that a change it does not report is one
/// whose event has not arrived.
/// </summary>
public sealed class DiskSyncTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-sync-tests", Guid.NewGuid().ToString("N"));
	private readonly SolutionWorkspace _workspace = new();
	private readonly DiskSync _sync;
	private readonly string _solution;
	private readonly string _a;
	private readonly string _b;

	public DiskSyncTests()
	{
		_solution = Write("Sync.slnx", "<Solution />");
		_a = Write(@"P\A.cs", "class A { }");
		_b = Write(@"P\Sub\B.cs", "class B { }");
		var projectId = ProjectId.CreateNewId();
		var project = ProjectInfo.Create(projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp, filePath: Path.Combine(_root, "P", "P.csproj"),
		                                 documents: [Document(projectId, _a), Document(projectId, _b)]);
		_sync = new DiskSync(_workspace, new MSBuildProjectLoader(_workspace), new PackageRestore(), _solution);
		_sync.Open(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), _solution, [project]));
	}

	public void Dispose()
	{
		_sync.Dispose();
		_workspace.Dispose();
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task TheFileACallNamesIsCheckedBeforeTheWatcherReportsItsChange()
	{
		await TextOf(_a);
		await TextOf(_b);
		Write(@"P\A.cs", "class A { int Edited; }");
		Write(@"P\Sub\B.cs", "class B { int Edited; }");

		await _sync.CatchUpAsync([]);
		var unnamed = await TextOf(_a);
		await _sync.CatchUpAsync([_a]);

		Assert.Equal("class A { }", unnamed);
		Assert.Equal("class A { int Edited; }", await TextOf(_a));
		Assert.Equal("class B { }", await TextOf(_b));
	}

	[Fact]
	public async Task AReportedChangeIsSeenByTheNextCall()
	{
		await TextOf(_b);
		Write(@"P\Sub\B.cs", "class B { int Edited; }");

		_sync.Changed(_b);
		await _sync.CatchUpAsync([]);

		Assert.Equal("class B { int Edited; }", await TextOf(_b));
	}

	[Fact]
	public async Task AfterAWatcherOverflowTheNextCallComparesEveryFile()
	{
		await TextOf(_a);
		await TextOf(_b);
		Write(@"P\A.cs", "class A { int Edited; }");
		Write(@"P\Sub\B.cs", "class B { int Edited; }");

		_sync.Overflowed();
		await _sync.CatchUpAsync([]);

		Assert.Equal("class A { int Edited; }", await TextOf(_a));
		Assert.Equal("class B { int Edited; }", await TextOf(_b));
	}

	[Fact]
	public async Task TheDocumentsOfADeletedFolderLeaveTheSolution()
	{
		Directory.Delete(Path.Combine(_root, "P", "Sub"), recursive: true);

		// The watcher reports the folder, not the files that were in it.
		_sync.Changed(Path.Combine(_root, "P", "Sub"));
		await _sync.CatchUpAsync([]);

		Assert.Empty(_workspace.CurrentSolution.GetDocumentIdsWithFilePath(_b));
		Assert.Single(_workspace.CurrentSolution.GetDocumentIdsWithFilePath(_a));
	}

	[Fact]
	public async Task AGoneSolutionFileIsReportedAtOnceAndNoCallCatchesUp()
	{
		var vanished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_sync.Vanished += () => vanished.TrySetResult();
		File.Delete(_solution);

		_sync.Changed(_solution);
		await vanished.Task.WaitAsync(TimeSpan.FromSeconds(10));
		var caughtUp = await _sync.CatchUpAsync([_a]);

		Assert.False(caughtUp);
	}

	private async Task<string> TextOf(string path)
	{
		var id = Assert.Single(_workspace.CurrentSolution.GetDocumentIdsWithFilePath(path));
		return (await _workspace.CurrentSolution.GetDocument(id)!.GetTextAsync()).ToString();
	}

	private string Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	private static DocumentInfo Document(ProjectId projectId, string path)
		=> DocumentInfo.Create(DocumentId.CreateNewId(projectId), Path.GetFileName(path), loader: new FileTextLoader(path, defaultEncoding: null),
		                       filePath: path);
}
