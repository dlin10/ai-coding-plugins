using System.Diagnostics;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Roots;
using ConcurrencyHunter.Scopes;
using ConcurrencyHunter.Solving;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

public static class PhaseOneAnalyzer
{
    public const string SCOPE_DISCOVERY = "scope-discovery";
    public const string PROGRAM_INDEX = "program-index";
    public const string LOWERING = "lowering";
    public const string REACHABLE_SET = "reachable-set";
    public const string SUMMARIES_AND_FIXPOINT = "summaries-and-fixpoint";
    public const string EXECUTIONS = "executions";
    public const string ACCESSES = "accesses";
    public const string PAIRING = "pairing";
    public const string SOLVER = "solver";
    public const string FINDINGS = "findings";
    public const string ANALYSIS_LIMITS_VARIABLE = "CH_ANALYSIS_LIMITS";

    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, ProviderRegistry.BuiltIn, cancellationToken);

    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, AnalysisLimits limits, CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair, limits, cancellationToken);

    /// <summary>Scopes, then per scope the DI index, root providers in registration order, injection bindings, the program index,
    /// the reachable set (lowering once per method across scopes), summaries, the whole-program heap, executions and ownership,
    /// interprocedural accesses and pairs; findings are built over the pairs of every scope.</summary>
    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                    CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, registry, InterproceduralPairing.Pair, EnvironmentLimits(), cancellationToken);

    /// <summary>The analysis with another pairing of each scope's accesses, as a test's reference pairing.</summary>
    internal static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                      Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                      CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, registry, pairing, EnvironmentLimits(), cancellationToken);

    /// <summary>The limits a caller that passes none runs under: <c>CH_ANALYSIS_LIMITS=depth,contexts,scc</c> when that variable is
    /// set, so the limits grid can run the demo matcher under other limits, else the defaults.</summary>
    private static AnalysisLimits EnvironmentLimits()
    {
        if (Environment.GetEnvironmentVariable(ANALYSIS_LIMITS_VARIABLE) is not { Length: > 0 } text)
            return AnalysisLimits.Default;
        var values = text.Split(',').Select(part => int.TryParse(part, System.Globalization.NumberStyles.None,
                                                                 System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0).ToArray();
        if (values.Length != 3 || values.Any(value => value <= 0))
            throw new InvalidOperationException($"{ANALYSIS_LIMITS_VARIABLE} must be three positive integers depth,contexts,scc; it is '{text}'.");
        return new AnalysisLimits(values[0], values[1], values[2]);
    }

    private static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                     Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                     AnalysisLimits limits, CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, registry, pairing, limits, Z3ConstraintSolver.Create, cancellationToken);

    /// <summary>The analysis with another solver behind the last filter: the seam a test uses to run as if the native library
    /// had never loaded, which no environment variable of a shipped build can do (ADR 0004).</summary>
    internal static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                            Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                            AnalysisLimits limits, Func<IConstraintSolver> solverFactory,
                                                            CancellationToken cancellationToken) =>
        await AnalyzeAsync(solution, rootDirectory, RepositoryRoot.Find(rootDirectory), registry, pairing, limits, solverFactory,
                           cancellationToken);

    internal static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, string repositoryRoot,
                                                      CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, repositoryRoot, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair,
                     EnvironmentLimits(), Z3ConstraintSolver.Create, cancellationToken);

    private static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, string repositoryRoot,
                                                           ProviderRegistry registry,
                                                           Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                           AnalysisLimits limits, Func<IConstraintSolver> solverFactory,
                                                           CancellationToken cancellationToken)
    {
        using var solver = solverFactory();
        // One budget for the whole run, spent scope by scope: the share of the deadline belongs to the run (TD-094).
        var solverBudget = new SolverBudget(SolverPolicy.Budget);
        var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var sizes = new List<ScopeSize>();
        var step = Stopwatch.StartNew();
        var projectModels = ProjectModelFiles.Read(repositoryRoot);
        var modelLock = ModelLock.Read(repositoryRoot, projectModels);
        var discovery = ProcessScopes.Discover(solution, rootDirectory);
        Record(timings, SCOPE_DISCOVERY, step);
        var lowered = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        var scopes = new List<ProcessScope>();
        var roots = new List<ExecutionRootDescriptor>();
        var accesses = new List<Access>();
        var pairs = new List<AccessPair>();
        var coverage = new List<ScopeCoverage>();
        var comparisons = 0;
        var cartesianBound = 0;
        var buckets = 0;
        var largestBucket = 0;
        var candidates = 0;
        var suppressed = 0;
        var pairSkips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // One out-of-range diagnostic per project and assembly in the whole report (R5): a project two scopes share is reported in
        // the first of them.
        var outOfRangeReported = new HashSet<(ProjectId, string)>();

        foreach (var scoped in discovery.Scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = scoped.Scope;
            scopes.Add(scope);
            var diagnostics = new List<string>(discovery.Diagnostics);
            var compilations = new List<Compilation>();
            var projectFiles = new List<(Compilation, string?)>();
            var compiledProjects = new List<(Project Project, Compilation Compilation)>();
            foreach (var project in scoped.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation)
                {
                    compilations.Add(compilation);
                    projectFiles.Add((compilation, project.FilePath));
                    compiledProjects.Add((project, compilation));
                }
            }

            var (models, modelRejections) = ProjectModelResolver.Resolve(projectModels, compilations, modelLock);
            diagnostics.AddRange(modelRejections.Select(rejection => rejection.Diagnostic));

            var run = ScopePipeline.Run(scope.Id, compilations, projectFiles, rootDirectory, registry, models, limits, lowered, null,
                                        cancellationToken);
            Debug.Assert(!run.Stopped, "the analysis run passes no reachable-body limit");
            diagnostics.AddRange(run.DiscoveryDiagnostics);
            step.Restart();
            diagnostics.AddRange(compiledProjects.SelectMany(compiled => LibraryModels.BuiltIn.OutOfRangeReferences(compiled.Compilation)
                                                                                              .Where(reference => outOfRangeReported.Add((compiled.Project.Id, reference.Assembly.Name)))
                                                                                              .Select(reference =>
                $"library-models: {RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion} {compiled.Project.Name}: " +
                $"{reference.Assembly.Name} {reference.Assembly.Version}: {compiled.Project.Name} references {reference.Assembly.Name} " +
                $"{reference.Assembly.Version}, outside the supported range {reference.Range.Minimum} up to {reference.Range.MaximumExclusive}; " +
                "the calls of its members only the built-in models describe are opaque calls.")));

            // Scope discovery still spans the DI index, the providers, the bindings and the out-of-range diagnostics.
            Add(timings, SCOPE_DISCOVERY, run.Times.RootDiscovery);
            Record(timings, SCOPE_DISCOVERY, step);
            diagnostics.AddRange(run.LoweringDiagnostics);
            Add(timings, PROGRAM_INDEX, run.Times.ProgramIndex);
            Add(timings, LOWERING, run.Times.Lowering);
            Add(timings, REACHABLE_SET, run.Times.ReachableSet);
            Add(timings, SUMMARIES_AND_FIXPOINT, run.Times.SummariesAndFixpoint);
            Add(timings, EXECUTIONS, run.Times.Executions);
            Add(timings, ACCESSES, run.Times.Accesses);
            var collection = run.Collection;
            // Pairing spans the pairing and the gap marks only, as it did before the pipeline: the bookkeeping above is not its own.
            step.Restart();
            var scopePairs = pairing(collection.Accesses, run.Executions, run.Heap);
            // Before the solver: the checks a semantic gap decides are part of what the budget is spent in the order of (TD-039).
            scopePairs = scopePairs with { Pairs = GapDecisions.Mark(scopePairs.Pairs, run.Interprocedural, collection.Coverage.Gaps) };
            Record(timings, PAIRING, step);
            // The solver is the last filter (TD-091): what the cheap ones left, asked one candidate at a time and within the
            // run's share of its deadline. A solver that never started answers every question Unknown (ADR 0004).
            var refined = SolverRefinement.Refine(scopePairs, solver, solverBudget, SolverPolicy.QueryLimit);
            scopePairs = refined.Pairs;
            diagnostics.AddRange(refined.Traces.Select(trace => $"solver: {trace}"));
            Record(timings, SOLVER, step);
            sizes.Add(new ScopeSize(scope.Id, run.Heap.ReachableBodies.Count, run.Summaries.Built, run.Heap.Regions.Count,
                                    run.Heap.Instances.Count, collection.Accesses.Count(access => !access.IsConstructionLocal)));

            roots.AddRange(run.Roots);
            accesses.AddRange(collection.Accesses);
            pairs.AddRange(scopePairs.Pairs);
            comparisons += scopePairs.Comparisons;
            cartesianBound += scopePairs.CartesianBound;
            buckets += scopePairs.Buckets;
            largestBucket = Math.Max(largestBucket, scopePairs.LargestBucket);
            candidates += scopePairs.Candidates;
            suppressed += scopePairs.Suppressed;
            foreach (var (reason, count) in scopePairs.Skips)
                pairSkips[reason] = pairSkips.GetValueOrDefault(reason) + count;
            coverage.Add(new ScopeCoverage(scope.Id, run.RootsPerProvider, diagnostics, run.Index.Registrations.Count,
                                           Merged(collection.Coverage.Counters, refined.Counters, modelRejections.Count))
            {
                TopOpaqueCallees = collection.Coverage.TopOpaqueCallees,
                Gaps = collection.Coverage.Gaps,
                SpawnSites = collection.Coverage.SpawnSites,
                TimerSites = collection.Coverage.TimerSites,
                UnprovenJoins = collection.Coverage.UnprovenJoins,
                Ordering = collection.Coverage.Ordering,
                LoweredNotReached = collection.Coverage.LoweredNotReached,
                OutsideLoweredSet = collection.Coverage.OutsideLoweredSet
                                              .SelectMany(member => member.NestedBodyIds.Prepend(member.MemberId))
                                              .ToArray()
            });
        }

        modelLock.Write();
        foreach (var scopeCoverage in coverage)
            if (scopeCoverage.Diagnostics is List<string> scopeDiagnostics)
                scopeDiagnostics.AddRange(modelLock.Diagnostics);

        step.Restart();
        var conflicts = ConflictFindings.Create(pairs, accesses, cancellationToken);
        Record(timings, FINDINGS, step);
        var providers = registry.Providers
                                .Select(provider => new ProviderSummary(
                                    provider.ProviderId,
                                    provider.SupportedAssemblyVersions
                                            .Select(range => $"{range.AssemblyName} {range.Minimum}..{range.MaximumExclusive}")
                                            .ToArray()))
                                .ToArray();
        return new AnalysisResult(scopes, roots, accesses, conflicts.Findings, conflicts.Groups, coverage,
                                  new PairCounters(comparisons, cartesianBound, buckets, largestBucket, candidates, suppressed, pairSkips),
                                  providers)
        {
            Timings = new[]
                      {
                          SCOPE_DISCOVERY, PROGRAM_INDEX, LOWERING, REACHABLE_SET, SUMMARIES_AND_FIXPOINT, EXECUTIONS, ACCESSES,
                          PAIRING, SOLVER, FINDINGS
                      }
                      .Select(name => new StepTiming(name, timings.GetValueOrDefault(name).TotalSeconds))
                      .ToArray(),
            ScopeSizes = sizes
        };
    }

    /// <summary>Adds the step's elapsed time to its total and restarts the stopwatch for the next step.</summary>
    /// <summary>The scope's coverage counters with what the solver did to its candidates beside them.</summary>
    private static IReadOnlyDictionary<string, int> Merged(IReadOnlyDictionary<string, int> counters,
                                                           IReadOnlyDictionary<string, int> solver, int rejected)
    {
        var merged = new SortedDictionary<string, int>(counters.ToDictionary(), StringComparer.Ordinal);
        foreach (var (name, count) in solver)
            merged[name] = merged.GetValueOrDefault(name) + count;
        merged[CoverageCounters.MODEL_ENTRY_REJECTED] = rejected;
        return merged;
    }

    private static void Record(Dictionary<string, TimeSpan> timings, string name, Stopwatch step)
    {
        timings[name] = timings.GetValueOrDefault(name) + step.Elapsed;
        step.Restart();
    }

    private static void Add(Dictionary<string, TimeSpan> timings, string name, TimeSpan elapsed) =>
        timings[name] = timings.GetValueOrDefault(name) + elapsed;
}
