using CacheDetective.Configuration;
using CacheDetective.Serialization;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Workspaces;

namespace CacheDetective.Mcp;

internal sealed class WorkspaceSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MsBuildSolutionLoader _loader = new();
    private readonly CallGraphIndexer _indexer = new();
    private readonly FindingCatalog _findingCatalog = new();
    private readonly Dictionary<string, DateTimeOffset> _indexedAt = new(StringComparer.OrdinalIgnoreCase);
    private string? _repositoryRoot;
    private WorkspaceConfiguration? _configuration;

    internal CacheGraph Graph { get; private set; } = new();

    internal async Task<WorkspaceInitResult> InitializeAsync(string root,
                                                             IReadOnlyList<string>? solutions,
                                                             IReadOnlyDictionary<string, double>? budgets,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repositoryRoot = Path.GetFullPath(root);
            var configurationPath = WorkspaceConfigurationStore.GetPath(repositoryRoot);
            var exists = File.Exists(configurationPath);
            var hasOverrides = solutions is not null || budgets is not null;
            if (!exists && solutions is null)
            {
                throw new InvalidOperationException(
                    $"No workspace configuration exists at '{configurationPath}', and no solutions were supplied.");
            }

            var existing = exists
                ? await WorkspaceConfigurationStore.ReadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)
                : null;
            var configuration = hasOverrides
                ? Merge(existing, repositoryRoot, solutions, budgets)
                : existing!;
            var written = hasOverrides &&
                await WorkspaceConfigurationStore.WriteAsync(repositoryRoot, configuration, cancellationToken)
                                                     .ConfigureAwait(false);

            if (!string.Equals(_repositoryRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                Graph = new CacheGraph();
                _indexedAt.Clear();
                _findingCatalog.Reset();
            }
            _repositoryRoot = repositoryRoot;
            _configuration = configuration;
            return new WorkspaceInitResult(configuration, written);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<WorkspaceStatusResult> GetStatusAsync(PageArguments? page = null,
                                                              CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildStatus(page);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IndexSolutionResult> IndexSolutionAsync(string path,
                                                                PageArguments? diagnosticsPage = null,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_repositoryRoot is null || _configuration is null)
            {
                return new IndexSolutionResult(path, false, null, CurrentCounts(),
                    PageDiagnostics([], diagnosticsPage),
                    "workspace_init must be called before index_solution.");
            }

            var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(_repositoryRoot, path));
            var solutionName = NormalizePath(Path.GetRelativePath(_repositoryRoot, fullPath));
            MsBuildLoadResult? loaded = null;
            try
            {
                loaded = await _loader.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                var replacement = await _indexer.IndexAsync(loaded.Solution, solutionName, cancellationToken)
                                                .ConfigureAwait(false);
                Graph.ReplaceSolution(solutionName, replacement);
                var indexedAt = DateTimeOffset.UtcNow;
                _indexedAt[solutionName] = indexedAt;
                return new IndexSolutionResult(solutionName, true, indexedAt, CurrentCounts(),
                    PageDiagnostics(loaded.Diagnostics, diagnosticsPage), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                return new IndexSolutionResult(solutionName, false, null, CurrentCounts(),
                    PageDiagnostics(loaded?.Diagnostics ?? [], diagnosticsPage), error.Message);
            }
            finally
            {
                loaded?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal IReadOnlyList<UnguardedWriteFinding> GetUnguardedWriteFindings() =>
        new UnguardedWriteRule().Evaluate(Graph, _configuration?.Budgets);

    internal async Task<T> ReadGraphAsync<T>(Func<CacheGraph, WorkspaceConfiguration?, T> read,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph, _configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<T> ReadFindingsAsync<T>(
        Func<CacheGraph, WorkspaceConfiguration?, string?, FindingCatalog, T> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph, _configuration, _repositoryRoot, _findingCatalog);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkspaceStatusResult BuildStatus(PageArguments? page)
    {
        if (_configuration is null)
        {
            return new WorkspaceStatusResult(PageSolutions([], page), CurrentCounts());
        }

        var solutions = _configuration.Solutions.Select(solution =>
        {
            var normalized = NormalizeSolution(_repositoryRoot!, solution);
            return _indexedAt.TryGetValue(normalized, out var indexedAt)
                ? new SolutionStatus(solution, true, indexedAt)
                : new SolutionStatus(solution, false, null);
        }).ToArray();
        return new WorkspaceStatusResult(PageSolutions(solutions, page), CurrentCounts());
    }

    private WorkspaceCounts CurrentCounts()
    {
        var invalidations = new OrphanInvalidationRule().Evaluate(Graph);
        var findings = GetUnguardedWriteFindings().Count +
                       invalidations.Orphans.Count +
                       invalidations.PatternMismatches.Count;
        var vertices = Graph.CacheKeys.Count + Graph.Tables.Count + Graph.Handlers.Count;
        return new WorkspaceCounts(vertices, Graph.Edges.Count, findings, Graph.Unresolved.Count);
    }

    private static WorkspaceConfiguration Merge(WorkspaceConfiguration? existing,
                                                string repositoryRoot,
                                                IReadOnlyList<string>? solutions,
                                                IReadOnlyDictionary<string, double>? budgets)
    {
        var mergedSolutions = (existing?.Solutions ?? [])
            .Concat(solutions ?? [])
            .Select(solution => NormalizeSolution(repositoryRoot, solution))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mergedSolutions.Length == 0)
        {
            throw new InvalidOperationException("At least one solution must be supplied.");
        }

        var mergedBudgets = new Dictionary<string, double>(existing?.Budgets ?? [], StringComparer.Ordinal);
        foreach (var (table, seconds) in budgets ?? new Dictionary<string, double>())
        {
            mergedBudgets[table] = seconds;
        }

        return new WorkspaceConfiguration
        {
            Version = WorkspaceConfiguration.CurrentVersion,
            Root = existing?.Root ?? repositoryRoot,
            Solutions = mergedSolutions,
            Budgets = mergedBudgets,
            Databases = existing?.Databases,
            Services = existing?.Services,
            Verify = existing?.Verify,
            Sensitive = existing?.Sensitive
        };
    }

    private static string NormalizeSolution(string repositoryRoot, string solution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solution);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(solution)
            ? solution
            : Path.Combine(repositoryRoot, solution));
        var relative = Path.GetRelativePath(repositoryRoot, fullPath);
        return NormalizePath(relative);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static ListEnvelope<SolutionStatus> PageSolutions(IReadOnlyList<SolutionStatus> solutions,
                                                              PageArguments? page) =>
        ResponseEnvelope.Create(solutions, page,
            CacheDetectiveJsonContext.Default.ListEnvelopeSolutionStatus);

    private static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(
        IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
        PageArguments? page)
    {
        var mapped = diagnostics.Select(diagnostic => new WorkspaceDiagnosticResult(
            diagnostic.Kind.ToString(), diagnostic.Message)).ToArray();
        return ResponseEnvelope.Create(mapped, page,
            CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);
    }
}

internal sealed record WorkspaceInitResult(WorkspaceConfiguration Configuration, bool Written);
internal sealed record SolutionStatus(string Path, bool Indexed, DateTimeOffset? IndexedAt);
internal sealed record WorkspaceCounts(int Vertices, int Edges, int Findings, int Unresolved);
internal sealed record WorkspaceStatusResult(ListEnvelope<SolutionStatus> Solutions, WorkspaceCounts Counts);
internal sealed record WorkspaceDiagnosticResult(string Kind, string Message);
internal sealed record IndexSolutionResult(string Path, bool Succeeded, DateTimeOffset? IndexedAt,
                                           WorkspaceCounts Counts,
                                           ListEnvelope<WorkspaceDiagnosticResult> Diagnostics,
                                           string? Error);
