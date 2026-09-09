using CacheDetective.Configuration;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Serialization;
using CacheDetective.Database;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Verification;
using CacheDetective.Workspaces;
using StackExchange.Redis;
using System.Text.Json;

namespace CacheDetective.Mcp;

/// <summary>Opens a connection to one configured database and reads its catalogue. Substituted in tests,
/// which have no server; the live path is exercised by the integration tests.</summary>
internal delegate Task<DatabaseIndexResult> CatalogueSource(DatabaseConfiguration database, string name,
                                                            CancellationToken cancellationToken);

internal sealed class WorkspaceSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly MsBuildSolutionLoader _loader = new();
    private readonly Dictionary<string, RememberedIndex> _lastIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Workspace, string Finding, string Version), VerificationRun> _verifications = [];
    private readonly FindingCatalog _findingCatalog = new();
    private readonly Dictionary<string, DateTimeOffset> _indexedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CacheRecognizer> _declaredCacheRecognizers = [];
    private readonly List<EventRecognizer> _declaredEventRecognizers = [];
    private string? _repositoryRoot;
    private WorkspaceConfiguration? _configuration;

    internal CacheGraph Graph { get; private set; } = new();

    internal async Task<AnnotateResult> AnnotateAsync(string unresolvedId, JsonElement resolution, string? note,
                                                       CancellationToken cancellationToken = default)
    {
        if (!unresolvedId.StartsWith("u:", StringComparison.Ordinal) || !int.TryParse(unresolvedId[2..], out var id))
            throw new InvalidOperationException($"Unresolved '{unresolvedId}' is not in this session.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var procedureGap = ProcedureGaps.Derive(Graph).SingleOrDefault(item => item.Unresolved.Id == id);
            var eventGap = EventGaps.Derive(Graph).SingleOrDefault(item => item.Unresolved.Id == id);
            var serviceGap = ServiceJoins.Derive(Graph).Gaps.SingleOrDefault(item => item.Unresolved.Id == id);
            var unresolved = Graph.Unresolved.SingleOrDefault(item => item.Id == id) ?? procedureGap?.Unresolved ?? eventGap?.Unresolved ?? serviceGap?.Unresolved ??
                             throw new InvalidOperationException($"Unresolved '{unresolvedId}' is not in this session.");
            var beforeKeys = AnnotationApplication.KeySnapshots(Graph);
            var beforeFindings = AnnotationApplication.FindingSnapshots(Graph, _findingCatalog, _configuration);
            var blockedRoles = Graph.RoleRowsBlockedBy(id);
            var annotationId = Graph.NextAnnotationId();
            var reindexed = unresolved.Kind switch
            {
                UnresolvedKind.CacheApi => await DeclareCacheRecognizerAsync(unresolved, resolution, annotationId, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.EventApi => await DeclareEventRecognizerAsync(unresolved, resolution, annotationId, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (reindexed is null)
                AnnotationApplication.ApplyAnnotation(Graph, unresolved, resolution, annotationId, eventGap, serviceGap);
            if (procedureGap is not null || eventGap is not null || serviceGap is not null)
                Graph.SuppressDerivedUnresolved(id, annotationId, AnnotationApplication.IsExternalResolution(resolution));
            else
                Graph.RemoveUnresolved(id);
            AnnotationApplication.ReclassifyBlockedRoles(Graph, blockedRoles);
            Graph.AddAnnotation(new Annotation(annotationId, id, unresolved.Kind, unresolved.Solution, unresolved.Site,
                                               unresolved.Snippet, resolution.GetRawText(), note));
            var keys = AnnotationApplication.Changes(beforeKeys, AnnotationApplication.KeySnapshots(Graph),
                                                     (key, change) => new AffectedKey(key, change));
            var afterFindings = AnnotationApplication.FindingSnapshots(Graph, _findingCatalog, _configuration);
            var findings = AnnotationApplication.Changes(beforeFindings, afterFindings, (finding, change) => new AffectedFinding(finding,
                AnnotationApplication.FindingRule(afterFindings.TryGetValue(finding, out var after) ? after : beforeFindings[finding]), change));
            return AnnotationApplication.LimitResult(new AnnotateResult(unresolvedId, annotationId, FindingQueries.KindName(unresolved.Kind), reindexed,
                                                  keys.Count, keys, findings.Count, findings, false, null));
        }
        finally { _gate.Release(); }
    }

    private async Task<string> DeclareCacheRecognizerAsync(Unresolved unresolved, JsonElement resolution, int annotationId,
                                                            CancellationToken cancellationToken)
    {
        // The same schema the workspace's caches section and the --recognizers file are read with, so a
        // declaration made live here can be pasted into the config unchanged. See CacheRecognizerConfiguration.
        CacheRecognizerConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<CacheRecognizerConfiguration>(resolution.GetRawText()) ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new ArgumentException($"resolution for kind 'cache_api' must be {AnnotationApplication.ResolutionSchema(UnresolvedKind.CacheApi)}", error);
        }

        CacheRecognizer recognizer;
        try
        {
            recognizer = configuration.ToRecognizer(Confidence.Likely, annotationId);
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException($"resolution for kind 'cache_api' must be {AnnotationApplication.ResolutionSchema(UnresolvedKind.CacheApi)}", error);
        }

        _declaredCacheRecognizers.Add(recognizer);
        try { return await ReindexAnnotatedSolutionAsync(unresolved.Solution, cancellationToken).ConfigureAwait(false); }
        catch { _declaredCacheRecognizers.Remove(recognizer); throw; }
    }

    private async Task<string> DeclareEventRecognizerAsync(Unresolved unresolved, JsonElement resolution, int annotationId,
                                                            CancellationToken cancellationToken)
    {
        EventRecognizerConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<EventRecognizerConfiguration>(resolution.GetRawText()) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }");
        }

        if (string.IsNullOrWhiteSpace(configuration.Publisher) && (configuration.Publishers is null || configuration.Publishers.Length == 0))
        {
            var publisher = AnnotationApplication.EventApiType(unresolved.Reason);
            if (publisher is null)
                throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }");
            configuration = new EventRecognizerConfiguration
            {
                Name = configuration.Name,
                Publisher = publisher,
                Methods = configuration.Methods,
                EventArgument = configuration.EventArgument,
                Consumer = configuration.Consumer,
                ConfiguredArity = configuration.ConfiguredArity,
                Handle = configuration.Handle,
                HandlerKind = configuration.HandlerKind
            };
        }
        try
        {
            var recognizer = configuration.ToRecognizer(Confidence.Likely, annotationId);
            _declaredEventRecognizers.Add(recognizer);
            try { return await ReindexAnnotatedSolutionAsync(unresolved.Solution, cancellationToken).ConfigureAwait(false); }
            catch { _declaredEventRecognizers.Remove(recognizer); throw; }
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }", error);
        }
    }

    private async Task<string> ReindexAnnotatedSolutionAsync(string? solution, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(solution))
            throw new ArgumentException("this item has no solution to reindex");
        var result = await IndexSolutionCoreAsync(solution, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error ?? $"Could not reindex '{solution}'.");
        return result.Path;
    }

    internal async Task<WorkspaceInitResult> InitializeAsync(string root, IReadOnlyList<string>? solutions, IReadOnlyDictionary<string, double>? budgets,
                                                             CancellationToken cancellationToken = default,
                                                             IReadOnlyDictionary<string, string>? services = null,
                                                             EventRecognizerConfiguration[]? events = null,
                                                             CacheRecognizerConfiguration[]? caches = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repositoryRoot = Path.GetFullPath(root);
            var configurationPath = WorkspaceConfigurationStore.GetPath(repositoryRoot);
            var exists = File.Exists(configurationPath);
            var hasOverrides = solutions is not null || budgets is not null || services is not null || events is not null || caches is not null;
            if (!exists && solutions is null)
            {
                throw new InvalidOperationException($"No workspace configuration exists at '{configurationPath}', and no solutions were supplied.");
            }

            var existing = exists
                               ? await WorkspaceConfigurationStore.ReadAsync(repositoryRoot,
                                                                             cancellationToken)
                                                                  .ConfigureAwait(false)
                               : null;
            var configuration = hasOverrides
                                    ? Merge(existing,
                                            repositoryRoot,
                                            solutions,
                                            budgets, services, events, caches)
                                    : existing!;
            var written = hasOverrides && await WorkspaceConfigurationStore.WriteAsync(repositoryRoot,
                                                                                       configuration,
                                                                                       cancellationToken)
                                                                           .ConfigureAwait(false);

            if (!string.Equals(_repositoryRoot,
                               repositoryRoot,
                               StringComparison.OrdinalIgnoreCase))
            {
                Graph = new CacheGraph();
                _indexedAt.Clear();
                _findingCatalog.Reset();
                _declaredCacheRecognizers.Clear();
                _declaredEventRecognizers.Clear();
                // The verification snapshots were readings of another repository's cache and database.
                _verifications.Clear();
                // And the remembered indexes were another repository's. They are keyed by the solution's
                // name alone, so a solution of the same relative name in the new workspace would have been
                // answered for page two out of the old workspace's diagnostics.
                _lastIndex.Clear();
            }

            _repositoryRoot = repositoryRoot;
            _configuration = configuration;
            Graph.SetServiceMap(configuration.Services ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return new WorkspaceInitResult(configuration,
                                           written);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<WorkspaceStatusResult> GetStatusAsync(PageArguments? page = null, CancellationToken cancellationToken = default)
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

    internal async Task<IndexSolutionResult> IndexSolutionAsync(string path, PageArguments? diagnosticsPage = null,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Paging through the diagnostics of an index is reading, not indexing. Loading the solution
            // again to answer for page two would cost minutes and could answer differently.
            if ((diagnosticsPage?.Page ?? PageArguments.DefaultPage) > PageArguments.DefaultPage &&
                _repositoryRoot is not null && _lastIndex.TryGetValue(SolutionIndexing.SolutionNameOf(_repositoryRoot, path), out var remembered))
            {
                return Recall(SolutionIndexing.SolutionNameOf(_repositoryRoot, path), remembered, diagnosticsPage);
            }

            return await IndexSolutionCoreAsync(path, diagnosticsPage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One page of a remembered index. The whole diagnostic list — the real ones and any missing-project
    /// names that did not fit beside them — is assembled first and paged exactly once, with the weight of
    /// the result around it reserved.
    /// <para>Paging the diagnostics first and then appending to <em>that page</em> was two bugs at once:
    /// the pages of one index stopped agreeing with each other, because each page re-partitioned a
    /// different mixture, and diagnostics fell out of every page. The envelope also filled its own 8 KB
    /// with no room left for the wrapper, so the result went over the limit as soon as the diagnostics
    /// were numerous enough to fill a page.</para>
    /// </summary>
    internal IndexSolutionResult Recall(string solutionName, RememberedIndex remembered, PageArguments? diagnosticsPage)
    {
        var missing = remembered.MissingProjects ?? [];
        var skipped = remembered.SkippedProjects ?? [];
        var empty = remembered.EmptyProjects ?? [];

        // Every candidate shell carries the hidden counts the final one will carry, so that trimming a list
        // cannot make the result grow again by widening a number beside it.
        IndexSolutionResult Shell(string? error, IReadOnlyList<string> shownMissing, IReadOnlyList<string> shownSkipped,
                                  IReadOnlyList<string> shownEmpty) =>
            new(solutionName, remembered.Succeeded, remembered.IndexedAt, CurrentCounts(), ResponseFitting.Empty(),
                error, remembered.LoadComplete, remembered.ProjectsExpected,
                remembered.ProjectsLoaded, shownMissing, missing.Count - shownMissing.Count, shownSkipped, shownEmpty,
                skipped.Count - shownSkipped.Count, empty.Count - shownEmpty.Count);

        // The error is settled first, against a shell with every list empty. It is the one part that cannot
        // be carried into the diagnostics, so if it does not fit here nothing the lists do can help.
        var fittedError = ResponseFitting.FittedError(remembered.Error, error => Shell(error, [], [], []));

        // In order, each list fitted against the shell the ones before it already settled. missingProjects
        // goes first because it is the one that says the scan was partial.
        var fittedMissing = ResponseFitting.Fitted(missing, kept => Shell(fittedError, kept, [], []));
        var fittedSkipped = ResponseFitting.Fitted(skipped, kept => Shell(fittedError, fittedMissing, kept, []));
        var fittedEmpty = ResponseFitting.Fitted(empty, kept => Shell(fittedError, fittedMissing, fittedSkipped, kept));
        var counted = Shell(fittedError, fittedMissing, fittedSkipped, fittedEmpty);

        var diagnostics = remembered.Diagnostics
                                    .Concat(ResponseFitting.Carried(remembered.Diagnostics,
                                                    ("missingProjects", missing.Skip(fittedMissing.Count).ToArray()),
                                                    ("skippedProjects", skipped.Skip(fittedSkipped.Count).ToArray()),
                                                    ("emptyProjects", empty.Skip(fittedEmpty.Count).ToArray())))
                                    .ToArray();
        return counted with { Diagnostics = PageDiagnostics(diagnostics, diagnosticsPage, ResponseFitting.Weight(counted)) };
    }

    /// <summary>Named from the tests, which measure a shell against this budget. The budget itself, and
    /// everything derived from it, lives in <see cref="ResponseFitting"/>.</summary>
    internal const int MaximumShellBytes = ResponseFitting.MaximumShellBytes;

    /// <summary>The state half of indexing a solution: the load and the index are
    /// <see cref="SolutionIndexing"/>'s and come back as a value, and what happens here is only what the
    /// session owns — putting the replacement into the graph, stamping when it happened, and remembering the
    /// account so a second page of diagnostics can be answered without loading again.</summary>
    private async Task<IndexSolutionResult> IndexSolutionCoreAsync(string path, PageArguments? diagnosticsPage,
                                                                    CancellationToken cancellationToken)
    {
        if (_repositoryRoot is null || _configuration is null)
        {
            return new IndexSolutionResult(path,
                                           false,
                                           null,
                                           CurrentCounts(),
                                           PageDiagnostics([], diagnosticsPage),
                                           "workspace_init must be called before index_solution.");
        }

        var indexed = await SolutionIndexing.IndexAsync(path, _repositoryRoot, _loader, _configuration,
                                                        _declaredCacheRecognizers, _declaredEventRecognizers,
                                                        cancellationToken).ConfigureAwait(false);
        if (!indexed.Succeeded)
        {
            return Remember(indexed.SolutionName,
                            new RememberedIndex(false, null, indexed.Diagnostics, indexed.Error, false, 0, 0, [], [], []),
                            diagnosticsPage);
        }

        Graph.ReplaceSolution(indexed.SolutionName, indexed.Replacement);
        var indexedAt = DateTimeOffset.UtcNow;
        _indexedAt[indexed.SolutionName] = indexedAt;
        var coverage = indexed.Coverage;
        return Remember(indexed.SolutionName,
                        new RememberedIndex(true, indexedAt, indexed.Diagnostics, null, coverage.LoadComplete,
                                            coverage.ProjectsExpected, coverage.ProjectsLoaded, coverage.MissingProjects,
                                            coverage.SkippedProjects, coverage.EmptyProjects),
                        diagnosticsPage);
    }

    private IndexSolutionResult Remember(string solutionName, RememberedIndex remembered, PageArguments? diagnosticsPage)
    {
        _lastIndex[solutionName] = remembered;
        return Recall(solutionName, remembered, diagnosticsPage);
    }

    internal Task<IndexDatabaseResult> IndexDatabaseAsync(string name, CancellationToken cancellationToken = default) =>
        IndexDatabaseAsync(name,
                           DatabaseIndexing.ReadCatalogueAsync,
                           cancellationToken);

    /// <summary>Re-indexing replaces the database's half of the graph through
    /// <see cref="CacheGraph.ReplaceDatabase"/>, exactly as <see cref="IndexSolutionAsync"/> replaces a
    /// solution's. Without it a second call would double every catalogue edge, inflate the counts, and
    /// make the unguarded-write rule report each finding twice.</summary>
    internal async Task<IndexDatabaseResult> IndexDatabaseAsync(string name, CatalogueSource source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_repositoryRoot is null || _configuration is null)
            {
                return Failed(name,
                              "workspace_init must be called before index_database.");
            }

            var configured = DatabaseIndexing.FindDatabase(_configuration,
                                          name,
                                          out var error);
            if (configured is null)
            {
                return Failed(name,
                              error!);
            }

            var database = configured.Name ?? name;
            try
            {
                var indexed = await source(configured,
                                           database,
                                           cancellationToken)
                                 .ConfigureAwait(false);
                Graph.ReplaceDatabase(database,
                                      indexed.Graph);
                return new IndexDatabaseResult(database,
                                               true,
                                               DateTimeOffset.UtcNow,
                                               new DatabaseCounts(indexed.Graph.StoredProcedures.Count,
                                                                  indexed.Graph.Triggers.Count,
                                                                  indexed.Graph.Views.Count,
                                                                  indexed.Graph.Edges.Count,
                                                                  indexed.Graph.Unresolved.Count),
                                               CurrentCounts(),
                                               indexed.UnresolvableObjects,
                                               null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure)
            {
                return Failed(database,
                              failure.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        IndexDatabaseResult Failed(string database, string message) =>
            new(database,
                false,
                null,
                new DatabaseCounts(0,
                                   0,
                                   0,
                                   0,
                                   0),
                CurrentCounts(),
                [],
                message);
    }

    internal IReadOnlyList<UnguardedWriteFinding> GetUnguardedWriteFindings() => new UnguardedWriteRule().Evaluate(Graph,
     _configuration?.Budgets);

    internal async Task<T> ReadGraphAsync<T>(Func<CacheGraph, WorkspaceConfiguration?, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph,
                        _configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task<VerifyFindingResult> VerifyFindingAsync(string findingId, bool refresh, PageArguments? page,
                                                          CancellationToken cancellationToken = default) =>
        // A lambda and not a method group: Graph, the configuration and the reader factory are read when the
        // run happens, inside the gate the overload below takes, so a run can never work from state that was
        // current when this call was set up rather than when it executes.
        VerifyFindingAsync(findingId, refresh, page,
                           (snapshot, token) => VerificationRunner.RunVerificationAsync(Graph, _configuration, OpenCacheReader,
                                                                                        snapshot, token),
                           cancellationToken);

    /// <summary>
    /// Verification runs once for a finding and is remembered under the workspace it ran in, the finding,
    /// and the graph's version. A new version means a different graph and a different answer; a new
    /// workspace means the old answers are about somebody else's code.
    /// </summary>
    internal async Task<VerifyFindingResult> VerifyFindingAsync(string findingId, bool refresh, PageArguments? page,
                                                                Func<FindingSnapshot, CancellationToken, Task<VerificationRun>> run,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        ArgumentNullException.ThrowIfNull(run);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _findingCatalog.GetAll(Graph, _configuration?.Budgets);
            var snapshot = _findingCatalog.Get(findingId);
            var id = (_repositoryRoot ?? string.Empty, findingId, Graph.VersionToken);
            if (refresh)
            {
                _verifications.Remove(id);
            }

            if (!_verifications.TryGetValue(id, out var verification))
            {
                verification = await run(snapshot, cancellationToken).ConfigureAwait(false);
                _verifications[id] = verification;
            }

            return VerificationQueries.Present(findingId, verification, page);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How the cache reader is built over a live connection. An instance property rather than
    /// shared state, so that a test can make the server refuse a command and see what
    /// <see cref="VerificationRunner.RunVerificationAsync"/> does with the refusal — the catches there
    /// cannot be reached through the run seam, which answers before any of this happens.</summary>
    internal Func<IConnectionMultiplexer, ConfigurationOptions, string, RedisReader> OpenCacheReader { get; set; } =
        RedisReader.Create;

    /// <summary>Named from VerificationWorkingPathTests, which drives the working path through a session
    /// with fake readers, so this keeps that signature. The sample itself is <see cref="VerificationRunner"/>'s;
    /// what the session adds is the sensitive-field masks, read from its configuration at call time.</summary>
    internal Task<VerificationRun> VerifySampleAsync(VerificationReader database, VerifyConfiguration verify, CacheKey key,
                                                     Applicability applicability, IReadOnlyList<string> tables,
                                                     CacheScanResult scan, Func<double> now,
                                                     CancellationToken cancellationToken, string? refutingTable = null) =>
        VerificationRunner.VerifySampleAsync(database, verify, key, applicability, tables, scan, now,
                                             _configuration?.Sensitive, cancellationToken, refutingTable);

    internal async Task<T> ReadFindingsAsync<T>(Func<CacheGraph, WorkspaceConfiguration?, string?, FindingCatalog, T> read,
                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph,
                        _configuration,
                        _repositoryRoot,
                        _findingCatalog);
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
            return new WorkspaceStatusResult(ResponseFitting.PageSolutions([],
                                                           page),
                                             CurrentCounts());
        }

        var solutions = _configuration.Solutions.Select(solution =>
                                       {
                                           var normalized = SolutionIndexing.NormalizeSolution(_repositoryRoot!,
                                                                              solution);
                                           return _indexedAt.TryGetValue(normalized,
                                                                         out var indexedAt)
                                                      ? new SolutionStatus(solution,
                                                                           true,
                                                                           indexedAt)
                                                      : new SolutionStatus(solution,
                                                                           false,
                                                                           null);
                                       })
                                      .ToArray();
        return new WorkspaceStatusResult(ResponseFitting.PageSolutions(solutions,
                                                       page),
                                         CurrentCounts(),
                                         _configuration.Verify?.Auto ?? false);
    }

    private WorkspaceCounts CurrentCounts()
    {
        var invalidations = new OrphanInvalidationRule().Evaluate(Graph);
        var findings = GetUnguardedWriteFindings().Count + new ExternalNoTtlRule().Evaluate(Graph).Count +
                       new StaleParentKeyRule().Evaluate(Graph).Count + invalidations.Orphans.Count + invalidations.PatternMismatches.Count;
        var vertices = Graph.CacheKeys.Count + Graph.Tables.Count + Graph.Handlers.Count + Graph.StoredProcedures.Count + Graph.Triggers.Count +
                       Graph.Views.Count + Graph.Events.Count + Graph.ExternalSources.Count;
        return new WorkspaceCounts(vertices,
                                   Graph.Edges.Count,
                                   findings,
                                   Graph.Unresolved.Count,
                                   Graph.StoredProcedures.Count,
                                   Graph.Triggers.Count,
                                   Graph.Views.Count,
                                   Graph.Events.Count,
                                   Graph.ExternalSources.Count,
                                   Graph.Annotations.Count);
    }

    private static WorkspaceConfiguration Merge(WorkspaceConfiguration? existing, string repositoryRoot, IReadOnlyList<string>? solutions,
                                                IReadOnlyDictionary<string, double>? budgets,
                                                IReadOnlyDictionary<string, string>? services,
                                                EventRecognizerConfiguration[]? events,
                                                CacheRecognizerConfiguration[]? caches)
    {
        var mergedSolutions = (existing?.Solutions ?? []).Concat(solutions ?? [])
                                                         .Select(solution => SolutionIndexing.NormalizeSolution(repositoryRoot,
                                                                                               solution))
                                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                                         .ToArray();
        if (mergedSolutions.Length == 0)
        {
            throw new InvalidOperationException("At least one solution must be supplied.");
        }

        var mergedBudgets = new Dictionary<string, double>(existing?.Budgets ?? [],
                                                           StringComparer.Ordinal);
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
            Services = services is null ? existing?.Services : new Dictionary<string, string>(services, StringComparer.OrdinalIgnoreCase),
            Events = events ?? existing?.Events,
            Caches = caches ?? existing?.Caches,
            Verify = existing?.Verify,
            Sensitive = existing?.Sensitive
        };
    }


    /// <summary>Named from the tests. The fragment machinery is <see cref="ResponseFitting"/>'s.</summary>
    internal static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
                                                                      int numberedFrom = 0) =>
        ResponseFitting.Describe(diagnostics, numberedFrom);

    /// <summary>Named from the tests. The paging is <see cref="ResponseFitting"/>'s.</summary>
    internal static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(IReadOnlyList<WorkspaceDiagnosticResult> diagnostics,
                                                                            PageArguments? page, int reserve = 0) =>
        ResponseFitting.PageDiagnostics(diagnostics, page, reserve);
}

internal sealed record WorkspaceInitResult(WorkspaceConfiguration Configuration, bool Written);
internal sealed record SolutionStatus(string Path, bool Indexed, DateTimeOffset? IndexedAt);
internal sealed record WorkspaceCounts(int Vertices, int Edges, int Findings, int Unresolved,
                                       int Procedures, int Triggers, int Views, int Events, int ExternalSources, int Annotations);
internal sealed record DatabaseCounts(int Procedures, int Triggers, int Views, int Edges, int Unresolved);
internal sealed record IndexDatabaseResult(string Database, bool Succeeded, DateTimeOffset? IndexedAt,
                                           DatabaseCounts Added, WorkspaceCounts Counts,
                                           IReadOnlyList<string> UnresolvableObjects, string? Error);
/// <summary><paramref name="VerifyAuto"/> is the workspace's consent to runtime verification, surfaced
/// here so a caller can see whether it may verify without reading the configuration file itself.</summary>
internal sealed record WorkspaceStatusResult(ListEnvelope<SolutionStatus> Solutions, WorkspaceCounts Counts,
                                             bool VerifyAuto = false);
/// <summary>One diagnostic, or one fragment of one whose message is too long to travel whole: the
/// fragments of a message share an <paramref name="Id"/> and are numbered <paramref name="Part"/> of
/// <paramref name="Parts"/>, so a reader that pages to the end can put the message back together.</summary>
internal sealed record WorkspaceDiagnosticResult(string Id, string Kind, string Message, int Part, int Parts);
internal sealed record IndexSolutionResult(string Path, bool Succeeded, DateTimeOffset? IndexedAt,
                                           WorkspaceCounts Counts,
                                           ListEnvelope<WorkspaceDiagnosticResult> Diagnostics,
                                           string? Error,
                                           bool LoadComplete = true,
                                           int ProjectsExpected = 0,
                                           int ProjectsLoaded = 0,
                                           IReadOnlyList<string>? MissingProjects = null,
                                           int MissingProjectsHidden = 0,
                                           IReadOnlyList<string>? SkippedProjects = null,
                                           IReadOnlyList<string>? EmptyProjects = null,
                                           int SkippedProjectsHidden = 0,
                                           int EmptyProjectsHidden = 0);

/// <summary>What the last index of one solution said, so that asking for a second page of its
/// diagnostics reads them back instead of loading and indexing the solution all over again.</summary>
internal sealed record RememberedIndex(bool Succeeded, DateTimeOffset? IndexedAt,
                                       IReadOnlyList<WorkspaceDiagnosticResult> Diagnostics, string? Error,
                                       bool LoadComplete, int ProjectsExpected, int ProjectsLoaded,
                                       IReadOnlyList<string> MissingProjects, IReadOnlyList<string> SkippedProjects,
                                       IReadOnlyList<string> EmptyProjects);
