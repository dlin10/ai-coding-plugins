using System.Threading.Tasks;

namespace RoslynMcpExtension.Shared;

public interface IRoslynAnalysisRpc
{
    Task LogAsync(string message);
    Task ReadyAsync();
    Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers);
    Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers, int maxResults);
    // A symbol is named by a position (filePath, line, column) or by symbolId, never both; see docs/adr/0001.
    Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults);
    Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults);
    Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName, int maxResults);
    Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column);
    Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath);
    Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults);
    Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic);
    Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName);
    Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
                                                  string? memberFilter, int maxResults);
}

public interface IServerRpc
{
    Task ShutdownAsync();
}
