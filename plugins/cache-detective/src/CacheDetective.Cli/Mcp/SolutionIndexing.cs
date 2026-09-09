using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Workspaces;
using System.Diagnostics.CodeAnalysis;

namespace CacheDetective.Mcp;

/// <summary>What one load-and-index of a solution produced, before any of it has reached the session's
/// graph. <see cref="Replacement"/> is the solution's half of the graph, ready to replace the half already
/// there; a failed index carries no replacement and no coverage, only the diagnostics the load managed to
/// report and the message it stopped with.</summary>
internal sealed record SolutionIndex(string SolutionName, CacheGraph? Replacement,
                                     IReadOnlyList<WorkspaceDiagnosticResult> Diagnostics,
                                     LoadCoverage? Coverage, string? Error)
{
    [MemberNotNullWhen(true, nameof(Replacement), nameof(Coverage))]
    internal bool Succeeded => Replacement is not null && Coverage is not null;
}

/// <summary>Loading one solution and indexing it into a graph, and the path arithmetic that turns whatever
/// the caller typed into the solution's name inside the workspace.
/// <para>Nothing here reaches the session. The repository root, the loader, the configuration and the
/// declared recognizers all arrive as arguments, and what comes back is a value: the caller decides whether
/// to put it into a graph. That is the seam — indexing is work, replacing the graph is state.</para>
/// </summary>
internal static class SolutionIndexing
{
    /// <summary>Loads <paramref name="path"/> and indexes it, returning what it produced without touching
    /// any graph.
    /// <para>The whole of the load and the index is guarded, not just the load. An indexing failure is as
    /// much a partial answer as a load failure, and the diagnostics the load had already reported are the
    /// only evidence of how far it got — so both become a failed <see cref="SolutionIndex"/> carrying that
    /// account. Cancellation is not a failure and travels on.</para></summary>
    internal static async Task<SolutionIndex> IndexAsync(string path, string repositoryRoot, MsBuildSolutionLoader loader,
                                                         WorkspaceConfiguration configuration,
                                                         IReadOnlyList<CacheRecognizer> declaredCacheRecognizers,
                                                         IReadOnlyList<EventRecognizer> declaredEventRecognizers,
                                                         CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
                                            ? path
                                            : Path.Combine(repositoryRoot, path));
        var solutionName = NormalizePath(Path.GetRelativePath(repositoryRoot, fullPath));
        MsBuildLoadResult? loaded = null;
        try
        {
            loaded = await loader.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var configuredEvents = (configuration.Events ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null));
            // A recognizer the workspace declares is confirmed, as a configured event bus already is: the
            // repository is stating what its own library does. One arriving through annotate stays likely,
            // because every edge an annotation creates is likely.
            var configuredCaches = (configuration.Caches ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null));
            // The same merge cachedet metrics uses, through the same helper, so a declaration means the
            // same thing measured as it does scanned. See CacheRecognizers.Merge.
            var indexer = new CallGraphIndexer(new IndexerOptions(CacheRecognizers.Merge(configuredCaches.Concat(declaredCacheRecognizers)),
                                                                   EventRecognizers.All.Concat(configuredEvents).Concat(declaredEventRecognizers).ToArray()));
            var replacement = await indexer.IndexAsync(loaded.Solution, solutionName, cancellationToken).ConfigureAwait(false);
            return new SolutionIndex(solutionName, replacement, ResponseFitting.Describe(loaded.Diagnostics), loaded.Coverage, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // A load that threw still knows what it had already reported, and that account is the only
            // evidence of how far it got before it stopped.
            var diagnostics = error is MsBuildLoadException failure ? failure.Diagnostics : loaded?.Diagnostics ?? [];
            return new SolutionIndex(solutionName, null, ResponseFitting.Describe(diagnostics), null, error.Message);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    internal static string SolutionNameOf(string repositoryRoot, string path) =>
        NormalizePath(Path.GetRelativePath(repositoryRoot,
                                           Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path))));

    internal static string NormalizeSolution(string repositoryRoot, string solution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solution);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(solution)
                                            ? solution
                                            : Path.Combine(repositoryRoot,
                                                           solution));
        var relative = Path.GetRelativePath(repositoryRoot,
                                            fullPath);
        return NormalizePath(relative);
    }

    internal static string NormalizePath(string path) => path.Replace('\\',
                                                                      '/');
}
