using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <remarks>
/// Takes the base Workspace rather than VisualStudioWorkspace, whose internal constructor keeps tests
/// from creating one; the services only need the current solution.
/// </remarks>
internal class DocumentFinder(Workspace workspace)
{
	public Workspace Workspace => workspace;

	public Document FindDocument(string filePath)
	{
		if (!workspace.CurrentSolution.Projects.Any())
		{
			throw new ToolRequestException(ToolErrorCodes.WorkspaceNotReady,
			                               "No solution is loaded or the workspace is still initializing.");
		}

		var allDocuments = workspace.CurrentSolution.Projects
			.SelectMany(p => p.Documents)
			.Where(d => d.FilePath != null)
			.ToList();

		var normalizedPath = Path.GetFullPath(filePath);
		var exact = allDocuments.FirstOrDefault(d =>
			string.Equals(d.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase));

		if (exact != null)
			return exact;

		var fileName = Path.GetFileName(filePath);
		var candidates = allDocuments
			.Where(d => string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (candidates.Count == 1)
			return candidates[0];

		if (candidates.Count > 1)
		{
			var inputSegments = GetSegments(filePath);
			Document? best = null;
			var bestScore = 0;
			foreach (var candidate in candidates)
			{
				var docSegments = GetSegments(candidate.FilePath!);
				var score = CountMatchingTrailingSegments(inputSegments, docSegments);
				if (score > bestScore)
				{
					bestScore = score;
					best = candidate;
				}
			}
			return best ?? throw DocumentNotFound(filePath);
		}

		throw DocumentNotFound(filePath);
	}

	/// <summary>
	/// Names the solution that was searched, so a client holding several Roslyn servers can tell
	/// "wrong server" apart from "Visual Studio has not loaded this file".
	/// </summary>
	private ToolRequestException DocumentNotFound(string filePath)
	{
		var solution = workspace.CurrentSolution.FilePath;
		var scope = solution == null ? "any project" : $"any project of solution {solution}";
		return new ToolRequestException(ToolErrorCodes.DocumentNotFound, $"File not found in {scope}: {filePath}");
	}

	/// <summary>
	/// A project by name. A multi-targeted project is one project per framework, named "Name (tfm)"; its bare name
	/// matches every one of them.
	/// </summary>
	public static IReadOnlyList<Project> ProjectsNamed(Solution solution, string projectName)
	{
		var named = solution.Projects
			.Where(project => string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase)
			                  || project.Name.StartsWith(projectName + " (", StringComparison.OrdinalIgnoreCase))
			.ToList();
		return named.Count > 0
			? named
			: throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                                 $"No project named '{projectName}' in solution {solution.FilePath}.");
	}

	public static int GetPosition(SyntaxTree syntaxTree, int line, int column)
	{
		var text = syntaxTree.GetText();
		if (line < 1)
		{
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                               "Line must be at least 1.");
		}

		if (column < 1)
		{
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                               "Column must be at least 1.");
		}

		if (line > text.Lines.Count)
		{
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                               $"Line {line} exceeds document length ({text.Lines.Count} lines).");
		}

		var lineInfo = text.Lines[line - 1];
		if (column - 1 > lineInfo.Span.Length)
		{
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                               $"Column {column} exceeds line {line} length ({lineInfo.Span.Length} characters).");
		}

		return lineInfo.Start + (column - 1);
	}

	public static CompilationInfo CreateCompilationInfo(Document document, SemanticModel semanticModel)
		=> CreateCompilationInfo(document.Project, semanticModel);

	/// <param name="semanticModel">The model of the document the request was about; null for a symbol from metadata.</param>
	public static CompilationInfo CreateCompilationInfo(Project project, SemanticModel? semanticModel)
	{
		var parseOptions = project.ParseOptions as CSharpParseOptions;

		return new CompilationInfo
		{
			ProjectName = project.Name,
			AssemblyName = project.AssemblyName ?? project.Name,
			LanguageVersion = parseOptions?.LanguageVersion.ToString() ?? "Unknown",
			Defines = parseOptions?.PreprocessorSymbolNames.ToList() ?? [],
			DocumentErrorCount = semanticModel?.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
		};
	}

	private static string[] GetSegments(string path)
	{
		return path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
	}

	private static int CountMatchingTrailingSegments(string[] a, string[] b)
	{
		var count = 0;
		var ai = a.Length - 1;
		var bi = b.Length - 1;
		while (ai >= 0 && bi >= 0)
		{
			if (string.Equals(a[ai], b[bi], StringComparison.OrdinalIgnoreCase))
			{
				count++;
				ai--;
				bi--;
			}
			else
			{
				break;
			}
		}
		return count;
	}
}
