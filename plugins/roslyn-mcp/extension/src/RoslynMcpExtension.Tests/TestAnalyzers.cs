using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcpExtension.Tests;

// A test double handed to a workspace directly, never loaded by a compiler or shipped: the attribute and the
// release tracking these rules ask for exist for analyzers that are.
#pragma warning disable RS1001, RS2008

/// <summary>Reports every named type, so a test can tell whether a project's analyzers ran at all.</summary>
internal sealed class TypeNameAnalyzer : DiagnosticAnalyzer
{
	public const string Id = "TEST001";

	private static readonly DiagnosticDescriptor Rule = new(Id, "Type seen", "Type '{0}' seen", "Test",
	                                                        DiagnosticSeverity.Warning, isEnabledByDefault: true);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		context.RegisterSymbolAction(symbol => symbol.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Symbol.Locations[0], symbol.Symbol.Name)),
		                             SymbolKind.NamedType);
	}
}

internal sealed class TestAnalyzerReference(params DiagnosticAnalyzer[] analyzers) : AnalyzerReference
{
	public override string FullPath => "test-analyzers-in-memory";
	public override object Id => FullPath;
	public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [.. analyzers];
	public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [.. analyzers];
}
