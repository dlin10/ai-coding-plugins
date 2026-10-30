using System.Threading.Tasks;

namespace RoslynMcpExtension.Shared;

public interface IRoslynAnalysisRpc
{
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

/// <summary>
/// The headless daemon's contract with a shim, one connection per agent session. The analysis calls answer from
/// the solution this connection last loaded; see docs/adr/0004.
/// </summary>
public interface IRoslynDaemonRpc : IRoslynAnalysisRpc
{
    /// <summary>
    /// Makes a solution this connection's, loading it unless the daemon already holds it. Waits for the load
    /// about a minute at most, then answers <see cref="LoadStatus.Loading"/> while the load goes on.
    /// </summary>
    /// <param name="solutionPath">The absolute path of the .sln or .slnx file.</param>
    Task<LoadResult> LoadAsync(string solutionPath);

    /// <summary>
    /// Unloads a solution at once, whichever connections use it; each of them loads it again on its next call. See
    /// docs/adr/0009.
    /// </summary>
    /// <param name="solutionPath">The absolute path of the .sln or .slnx file.</param>
    Task<UnloadResult> UnloadAsync(string solutionPath);
}
