using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcpExtension.Services;

internal static class ProjectAnalyzers
{
	/// <summary>
	/// Attaches the analyzers the project references to its compilation, or returns null when it references
	/// none. Compiler diagnostics never come back from the result, only analyzer ones.
	/// </summary>
	public static CompilationWithAnalyzers? Attach(Project project, Compilation compilation)
	{
		var analyzers = project.AnalyzerReferences
			.SelectMany(reference => reference.GetAnalyzers(project.Language))
			.ToImmutableArray();
		return analyzers.IsEmpty ? null : compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
	}
}
