using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.LanguageServices;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class DocumentFinder(VisualStudioWorkspace workspace)
{
	public VisualStudioWorkspace Workspace => workspace;

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
			return best ?? throw new ToolRequestException(ToolErrorCodes.DocumentNotFound,
			                                              $"File not found in any project: {filePath}");
		}

		throw new ToolRequestException(ToolErrorCodes.DocumentNotFound,
		                               $"File not found in any project: {filePath}");
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
	{
		var parseOptions = document.Project.ParseOptions as CSharpParseOptions;

		return new CompilationInfo
		{
			ProjectName = document.Project.Name,
			AssemblyName = document.Project.AssemblyName ?? document.Project.Name,
			LanguageVersion = parseOptions?.LanguageVersion.ToString() ?? "Unknown",
			Defines = parseOptions?.PreprocessorSymbolNames.ToList() ?? [],
			DocumentErrorCount = semanticModel.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
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
