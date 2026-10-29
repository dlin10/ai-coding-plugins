using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// A git worktree holds the same relative paths as the checkout whose solution is loaded, so only the exact
/// path tells their files apart; see docs/adr/0003.
/// </summary>
public sealed class DocumentFinderTests : IDisposable
{
	private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "checkout");
	private static readonly string Worktree = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", "worktree");
	private static readonly string SolutionPath = Path.Combine(Checkout, "Foo.slnx");
	private static readonly string InSolution = Path.Combine(Checkout, "src", "Foo", "Bar.cs");
	private static readonly string InWorktree = Path.Combine(Worktree, "src", "Foo", "Bar.cs");

	private readonly TestWorkspace _solution = new();

	public DocumentFinderTests()
	{
		var workspace = _solution.Workspace;
		workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), SolutionPath));
		var project = workspace.AddProject("Foo", LanguageNames.CSharp);
		workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Bar.cs",
		                                          loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class Bar { }"), VersionStamp.Create())),
		                                          filePath: InSolution));
	}

	[Fact]
	public async Task TheSameRelativePathUnderAnotherRootIsNotFound()
	{
		var result = await new DocumentSymbolsService(_solution.Finder).GetDocumentSymbolsAsync(InWorktree);
		RoslynAnalysisService.CompleteResult(result);

		Assert.False(result.RequestSucceeded);
		Assert.Equal(ToolErrorCodes.DocumentNotFound, result.ErrorCode);
		Assert.Contains(SolutionPath, result.ErrorMessage);
		Assert.Empty(result.Members);
	}

	[Fact]
	public async Task DiagnosticsForTheSameRelativePathUnderAnotherRootAreNotFound()
	{
		var result = await new DiagnosticsService(_solution.Finder, TimeSpan.FromMinutes(1))
			.GetDiagnosticsAsync([InWorktree], null, false, false, 50);

		Assert.Equal(ToolErrorCodes.DocumentNotFound, result.ErrorCode);
		Assert.Contains(SolutionPath, result.ErrorMessage);
		Assert.Empty(result.CheckedProjects);
	}

	[Fact]
	public void PathsDifferingOnlyInLetterCaseOrSlashDirectionResolve()
	{
		foreach (var variant in new[] { InSolution.ToUpperInvariant(), InSolution.ToLowerInvariant(), InSolution.Replace('\\', '/') })
			Assert.Equal(InSolution, _solution.Finder.FindDocument(variant).FilePath);
	}

	public void Dispose() => _solution.Dispose();
}
