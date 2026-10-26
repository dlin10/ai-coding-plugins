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
    public const string GENERATION = "generation";
    public const string FINDINGS = "findings";
    public const string ANALYSIS_LIMITS_VARIABLE = "CH_ANALYSIS_LIMITS";

    /// <summary>The analysis of a solution with the built-in root providers and the default limits.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, CancellationToken cancellationToken,
                                                    ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, ProviderRegistry.BuiltIn, cancellationToken, generation);

    /// <summary>The analysis of a solution with the built-in root providers under the given limits.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="limits">The summary and heap limits.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, AnalysisLimits limits, CancellationToken cancellationToken,
                                                    ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair, limits, cancellationToken, generation);

    /// <summary>Scopes, then per scope the DI index, root providers in registration order, injection bindings, the program index,
    /// the reachable set (lowering once per method across scopes), summaries, the whole-program heap, executions and ownership,
    /// interprocedural accesses and pairs; findings are built over the pairs of every scope.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                    CancellationToken cancellationToken, ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, registry, InterproceduralPairing.Pair, EnvironmentLimits(), cancellationToken, generation);

    /// <summary>The analysis with another pairing of each scope's accesses, as a test's reference pairing.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="pairing">The pairing of each scope's accesses.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    internal static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                      Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                      CancellationToken cancellationToken, ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, registry, pairing, EnvironmentLimits(), cancellationToken, generation);

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

    /// <summary>The analysis with the production solver.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="pairing">The pairing of each scope's accesses.</param>
    /// <param name="limits">The summary and heap limits.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    private static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                     Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                     AnalysisLimits limits, CancellationToken cancellationToken,
                                                     ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, registry, pairing, limits, Z3ConstraintSolver.Create, cancellationToken, generation);

    /// <summary>The analysis with another solver behind the last filter: the seam a test uses to run as if the native library
    /// had never loaded, which no environment variable of a shipped build can do (ADR 0004).</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to; the repository root is found from it.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="pairing">The pairing of each scope's accesses.</param>
    /// <param name="limits">The summary and heap limits.</param>
    /// <param name="solverFactory">Makes the solver behind the last filter.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    internal static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                            Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                            AnalysisLimits limits, Func<IConstraintSolver> solverFactory,
                                                            CancellationToken cancellationToken,
                                                            ModelGeneration generation = ModelGeneration.ReadOnly) =>
        await AnalyzeAsync(solution, rootDirectory, RepositoryRoot.Find(rootDirectory), registry, pairing, limits, solverFactory,
                           new GenerationSetup(generation), cancellationToken);

    /// <summary>The analysis of a solution whose repository root the caller names.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to.</param>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/</c> the run reads, and, when the run generates,
    /// whose generated files it reads and writes.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <param name="generation">What the run does with the generated model layer; by default the generated files are read and applied, and
    /// nothing is generated or written.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    internal static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, string repositoryRoot,
                                                      CancellationToken cancellationToken, ModelGeneration generation = ModelGeneration.ReadOnly) =>
        AnalyzeAsync(solution, rootDirectory, repositoryRoot, new GenerationSetup(generation), cancellationToken);

    /// <summary>The analysis with the generated model layer set up as the caller says: the mode and, for a test, the install and the
    /// generator the run generates with. A caller that passes none of the three gets the product's.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to.</param>
    /// <param name="repositoryRoot">The repository root whose <c>.concurrency-hunter/models/</c> the run reads, and, unless the layer is off,
    /// whose generated files it reads and writes.</param>
    /// <param name="generation">What the run does with the generated layer.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    // bounded: forwards once to the analysis overload that owns the work.
    internal static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, string repositoryRoot, GenerationSetup generation,
                                                      CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, repositoryRoot, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair,
                     EnvironmentLimits(), Z3ConstraintSolver.Create, generation, cancellationToken);

    /// <summary>The analysis with everything a caller may set: the repository root, the pairing, the limits, the solver and the generated
    /// layer. The pairing is called once for each analysis of a scope, so a test that counts its calls counts the analyses.</summary>
    /// <param name="solution">The solution to analyse.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to.</param>
    /// <param name="repositoryRoot">The repository root whose models the run reads and, unless the generated layer is off, whose generated files
    /// it reads and writes.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="pairing">The pairing of each scope's accesses.</param>
    /// <param name="limits">The summary and heap limits.</param>
    /// <param name="solverFactory">Makes the solver behind the last filter.</param>
    /// <param name="generation">What the run does with the generated layer.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    internal static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, string repositoryRoot,
                                                            ProviderRegistry registry,
                                                            Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing,
                                                            AnalysisLimits limits, Func<IConstraintSolver> solverFactory,
                                                            GenerationSetup generation, CancellationToken cancellationToken)
    {
        using var work = WorkCancellation.Enter(cancellationToken);
        using var solver = solverFactory();
        // One budget for the whole run, spent scope by scope: the budget belongs to the run (TD-094).
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
        // Disposed however the analysis ends: the library compilations the run's own generations created leave the cache with it (R12).
        using var pass = GenerationPass.Create(generation, repositoryRoot, cancellationToken);
        var wanted = new HashSet<WantedGeneration>();

        foreach (var scoped in discovery.Scopes)
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            var scope = scoped.Scope;
            scopes.Add(scope);
            var diagnostics = new List<string>(discovery.Diagnostics);
            var compilations = new List<Compilation>();
            var projectFiles = new List<(Compilation, string?)>();
            var compiledProjects = new List<(Project Project, Compilation Compilation)>();
            foreach (var project in scoped.Projects)
            {
                WorkCancellation.Check();
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
            step.Restart();
            var outOfRange = compiledProjects.SelectMany(compiled => LibraryModels.BuiltIn.OutOfRangeReferences(compiled.Compilation)
                                                                                           .Where(reference => outOfRangeReported.Add((compiled.Project.Id, reference.Assembly.Name)))
                                                                                           .Select(reference =>
                $"library-models: {RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion} {compiled.Project.Name}: " +
                $"{reference.Assembly.Name} {reference.Assembly.Version}: {compiled.Project.Name} references {reference.Assembly.Name} " +
                $"{reference.Assembly.Version}, outside the supported range {reference.Range.Minimum} up to {reference.Range.MaximumExclusive}; " +
                "the calls of its members only the built-in models describe are opaque calls.")).ToArray();

            // Scope discovery still spans the DI index, the providers, the bindings and the out-of-range diagnostics.
            Record(timings, SCOPE_DISCOVERY, step);
            AddTimes(timings, run);
            // Pairing spans the pairing and the gap marks only, as it did before the pipeline: the bookkeeping above is not its own.
            step.Restart();
            PairAnalysis scopePairs;
            using (WorkCancellation.Outside())
                scopePairs = PairWithGaps(run, pairing);
            Record(timings, PAIRING, step);

            // Each round answers the latest analysis's targets; a change in applied models asks for another analysis.
            var (pairsRemoved, generatedInRun) = (0, 0);
            var analyses = 1;
            IReadOnlyList<GenerationRefusal> refusals = [];
            var generationDiagnostics = new List<string>();
            if (pass is not null)
            {
                var firstPairs = scopePairs.Pairs.Select(PairIdentity.Of).ToHashSet(StringComparer.Ordinal);
                var all = new Dictionary<(Compilation Compilation, string Assembly, string MemberId), GenerationCandidate>();
                ScopeLayer? previous = null;
                while (true)
                {
                    WorkCancellation.Check();
                    foreach (var candidate in pass.Candidates(run, compilations))
                    {
                        WorkCancellation.Check();
                        var key = (candidate.Compilation, candidate.Member.ContainingAssembly.Name, candidate.Target.MemberId);
                        all[key] = all.TryGetValue(key, out var earlier)
                            ? earlier with { HandedDelegate = earlier.HandedDelegate || candidate.HandedDelegate }
                            : candidate;
                    }

                    var generationCandidates = all.Values.ToArray();
                    var layer = pass.Prepare(models, generationCandidates);
                    wanted.UnionWith(pass.Wanted(models, generationCandidates));
                    generationDiagnostics.AddRange(pass.TakeDiagnostics());
                    generationDiagnostics.AddRange(layer.Diagnostics);
                    var changed = (previous is not null || layer.Describes) && generationCandidates.Any(candidate =>
                    {
                        var definition = (candidate.Member.ReducedFrom ?? candidate.Member).OriginalDefinition;
                        var before = previous?.Table.AppliedModelText(candidate.Compilation, definition, candidate.Target.LookupId);
                        var after = layer.Table.AppliedModelText(candidate.Compilation, definition, candidate.Target.LookupId);
                        return !string.Equals(before, after, StringComparison.Ordinal);
                    });
                    if (!changed && pass.HasWork)
                        continue;
                    if (!changed)
                    {
                        generatedInRun = pass.GeneratedInRun(generationCandidates);
                        refusals = GenerationCauses.Items(pass.Mode, pass.Reached(models, layer, generationCandidates));
                        break;
                    }

                    previous = layer;
                    run = ScopePipeline.Run(scope.Id, compilations, projectFiles, rootDirectory, registry, models.WithGenerated(layer.Table), limits,
                                            lowered, null, cancellationToken);
                    analyses++;
                    AddTimes(timings, run);
                    step.Restart();
                    using (WorkCancellation.Outside())
                        scopePairs = PairWithGaps(run, pairing);
                    Record(timings, PAIRING, step);
                }
                pairsRemoved = firstPairs.Except(scopePairs.Pairs.Select(PairIdentity.Of), StringComparer.Ordinal).Count();
            }

            Debug.Assert(!run.Stopped, "the analysis run passes no reachable-body limit");
            diagnostics.AddRange(run.DiscoveryDiagnostics);
            diagnostics.AddRange(outOfRange);
            diagnostics.AddRange(run.LoweringDiagnostics);
            diagnostics.AddRange(generationDiagnostics.Distinct(StringComparer.Ordinal));
            var collection = run.Collection;
            // The solver is the last filter (TD-091): what the cheap ones left, asked one candidate at a time and within the
            // run's solver budget (ADR 0021). A solver that never started answers every question Unknown (ADR 0004).
            step.Restart();
            var refined = SolverRefinement.Refine(scopePairs, solver, solverBudget, SolverPolicy.QueryLimit);
            scopePairs = refined.Pairs;
            diagnostics.AddRange(refined.Traces.Select(trace => $"solver: {trace}"));
            Record(timings, SOLVER, step);
            sizes.Add(new ScopeSize(scope.Id, run.Heap.ReachableBodies.Count, run.Summaries.Built, run.Heap.Regions.Count,
                                    run.Heap.Instances.Count, collection.Accesses.Count(access => !access.IsConstructionLocal)) { Analyses = analyses });

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
            {
                WorkCancellation.Check();
                pairSkips[reason] = pairSkips.GetValueOrDefault(reason) + count;
            }
            coverage.Add(new ScopeCoverage(scope.Id, run.RootsPerProvider, diagnostics, run.Index.Registrations.Count,
                                           Merged(collection.Coverage.Counters, refined.Counters, modelRejections.Count, generatedInRun, pairsRemoved))
            {
                GenerationRefusals = refusals,
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
        {
            WorkCancellation.Check();
            if (scopeCoverage.Diagnostics is List<string> scopeDiagnostics)
                scopeDiagnostics.AddRange(modelLock.Diagnostics);
        }

        // The time inside generations is the budget's measure, taken as the pass kept it: the analyses and the writes around them are
        // the steps' own.
        timings[GENERATION] = pass?.Spent ?? TimeSpan.Zero;
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
                          PAIRING, SOLVER, GENERATION, FINDINGS
                      }
                      .Select(name => new StepTiming(name, timings.GetValueOrDefault(name).TotalSeconds))
                      .ToArray(),
            ScopeSizes = sizes,
            GenerationWanted = wanted.OrderBy(item => item.MemberId, StringComparer.Ordinal).ThenBy(item => item.FileKey, StringComparer.Ordinal).ToArray()
        };
    }

    /// <summary>The scope's coverage counters with what the solver did to its candidates beside them, and with what the run knows and a scope's
    /// collection does not: the entries rejected, the models this run generated and the pairs they removed.</summary>
    /// <param name="counters">The counters the scope's collection kept.</param>
    /// <param name="solver">What the solver did to the scope's candidates.</param>
    /// <param name="rejected">The project model entries that were rejected.</param>
    /// <param name="generatedInRun">The scope's generation targets whose model this run generated.</param>
    /// <param name="pairsRemoved">The pair identities the generated layer took away from the scope.</param>
    private static IReadOnlyDictionary<string, int> Merged(IReadOnlyDictionary<string, int> counters, IReadOnlyDictionary<string, int> solver,
                                                           int rejected, int generatedInRun, int pairsRemoved)
    {
        var merged = new SortedDictionary<string, int>(counters.ToDictionary(), StringComparer.Ordinal);
        foreach (var (name, count) in solver)
        {
            WorkCancellation.Check();
            merged[name] = merged.GetValueOrDefault(name) + count;
        }
        merged[CoverageCounters.MODEL_ENTRY_REJECTED] = rejected;
        merged[CoverageCounters.GENERATED_IN_RUN] = generatedInRun;
        merged[CoverageCounters.PAIRS_REMOVED_GENERATED] = pairsRemoved;
        return merged;
    }

    /// <summary>The pairs of an analysis of a scope, after pairing and the gap marks and before the solver: the checks a semantic gap decides
    /// are part of what the solver's budget is spent in the order of (TD-039).</summary>
    /// <param name="run">The scope's analysis.</param>
    /// <param name="pairing">The pairing of the scope's accesses.</param>
    private static PairAnalysis PairWithGaps(ScopeRun run, Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis> pairing)
    {
        Debug.Assert(!run.Stopped, "the analysis run passes no reachable-body limit");
        var pairs = pairing(run.Collection.Accesses, run.Executions, run.Heap);
        return pairs with { Pairs = GapDecisions.Mark(pairs.Pairs, run.Interprocedural, run.Collection.Coverage.Gaps) };
    }

    /// <summary>Adds the time of each step of a scope's analysis to the totals over all its analyses.</summary>
    /// <param name="timings">The totals by step.</param>
    /// <param name="run">The scope's analysis.</param>
    private static void AddTimes(Dictionary<string, TimeSpan> timings, ScopeRun run)
    {
        Add(timings, SCOPE_DISCOVERY, run.Times.RootDiscovery);
        Add(timings, PROGRAM_INDEX, run.Times.ProgramIndex);
        Add(timings, LOWERING, run.Times.Lowering);
        Add(timings, REACHABLE_SET, run.Times.ReachableSet);
        Add(timings, SUMMARIES_AND_FIXPOINT, run.Times.SummariesAndFixpoint);
        Add(timings, EXECUTIONS, run.Times.Executions);
        Add(timings, ACCESSES, run.Times.Accesses);
    }

    /// <summary>Adds the step's elapsed time to its total and restarts the stopwatch for the next step.</summary>
    /// <param name="timings">The totals by step.</param>
    /// <param name="name">The step.</param>
    /// <param name="step">The stopwatch that measured it.</param>
    private static void Record(Dictionary<string, TimeSpan> timings, string name, Stopwatch step)
    {
        timings[name] = timings.GetValueOrDefault(name) + step.Elapsed;
        step.Restart();
    }

    private static void Add(Dictionary<string, TimeSpan> timings, string name, TimeSpan elapsed) =>
        timings[name] = timings.GetValueOrDefault(name) + elapsed;
}
