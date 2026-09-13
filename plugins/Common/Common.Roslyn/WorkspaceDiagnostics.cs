using Common.Mcp;
using Microsoft.CodeAnalysis;

namespace Common.Roslyn;

public static class WorkspaceDiagnostics
{
    public static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<WorkspaceDiagnostic> diagnostics,
                                                                     int numberedFrom = 0) =>
        DiagnosticFragments.Describe(diagnostics.Select(diagnostic => (diagnostic.Kind.ToString(), diagnostic.Message ?? string.Empty))
                                                 .ToArray(),
                                     numberedFrom);
}
