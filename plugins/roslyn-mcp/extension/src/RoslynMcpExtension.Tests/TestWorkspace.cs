using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpExtension.Services;

namespace RoslynMcpExtension.Tests;

/// <summary>A solution of one-file projects, each file at <see cref="PathOf"/> its project's name.</summary>
internal sealed class TestWorkspace : IDisposable
{
	public static readonly MetadataReference Corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
	public static readonly MetadataReference SystemCore = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);

	public AdhocWorkspace Workspace { get; } = new();

	public DocumentFinder Finder => new(Workspace);

	public static string PathOf(string project) => Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", project + ".cs");

	public ProjectId AddProject(string name, string source, params ProjectId[] references)
		=> AddProject(ProjectId.CreateNewId(name), name, source, references, []);

	public ProjectId AddProject(ProjectId id, string name, string source, IEnumerable<ProjectId> references,
	                            IEnumerable<MetadataReference> metadataReferences)
	{
		Workspace.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
		                                        compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
		                                        metadataReferences: metadataReferences.Prepend(Corlib),
		                                        projectReferences: references.Select(reference => new ProjectReference(reference))));
		Workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(id), name + ".cs",
		                                          loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
		                                          filePath: PathOf(name)));
		return id;
	}

	public void Dispose() => Workspace.Dispose();
}
