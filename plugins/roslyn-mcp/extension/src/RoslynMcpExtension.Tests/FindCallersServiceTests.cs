using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Tests;

public sealed class FindCallersServiceTests : IDisposable
{
	private readonly AdhocWorkspace _workspace = new();

	[Fact]
	public async Task NonCallableSymbolIsAnInvalidArgument()
	{
		var path = Path.Combine(Path.GetTempPath(), "Callers.cs");
		var project = _workspace.AddProject("Callers", LanguageNames.CSharp);
		_workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Callers.cs",
		                                           loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class Target { }"), VersionStamp.Create())),
		                                           filePath: path));

		var result = await new FindCallersService(new DocumentFinder(_workspace)).FindCallersAsync(path, 1, 7, 50);
		RoslynAnalysisService.CompleteResult(result);

		Assert.False(result.RequestSucceeded);
		Assert.Equal(ToolErrorCodes.InvalidArgument, result.ErrorCode);
		Assert.Equal("Target", result.Symbol?.Name);
	}

	public void Dispose() => _workspace.Dispose();
}
