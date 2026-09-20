using System.Diagnostics;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
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
                                                            CancellationToken cancellationToken)
    {
        using var solver = solverFactory();
        // One budget for the whole run, spent scope by scope: the share of the deadline belongs to the run (TD-094).
        var solverBudget = new SolverBudget(SolverPolicy.Budget);
        var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var sizes = new List<ScopeSize>();
        var step = Stopwatch.StartNew();
        var discovery = ProcessScopes.Discover(solution, rootDirectory);
        Record(timings, SCOPE_DISCOVERY, step);
        var lowered = new Dictionary<(Compilation Compilation, string BodyId), IrLoweredMethod?>();
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

        foreach (var scoped in discovery.Scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = scoped.Scope;
            scopes.Add(scope);
            var diagnostics = new List<string>(discovery.Diagnostics);
            var compilations = new List<Compilation>();
            var projectFiles = new List<(Compilation, string?)>();
            foreach (var project in scoped.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation)
                {
                    compilations.Add(compilation);
                    projectFiles.Add((compilation, project.FilePath));
                }
            }

            step.Restart();
            var index = DiIndexBuilder.Build(scope.Id, projectFiles, rootDirectory, cancellationToken);
            diagnostics.AddRange(index.Diagnostics.Select(diagnostic => $"di: {diagnostic.Code} {diagnostic.Subject}: {diagnostic.Message}"));

            var context = new RootDiscoveryContext(scope.Id, compilations, rootDirectory, index, cancellationToken);
            var scopeRoots = new List<ExecutionRootDescriptor>();
            var rootsPerProvider = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var rootIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in registry.Providers)
            {
                var result = provider.Discover(context);
                var providerDiagnostics = result.Diagnostics.ToList();
                foreach (var root in result.Roots)
                {
                    if (rootIds.Add(root.StableRootId))
                    {
                        scopeRoots.Add(root);
                        continue;
                    }

                    providerDiagnostics.Add(new RootDiscoveryDiagnostic(
                        provider.ProviderId, RootDiscoveryDiagnosticCode.DiscoveryFailed, root.StableRootId,
                        $"stable root id {root.StableRootId} was already produced by an earlier provider", []));
                }

                rootsPerProvider[provider.ProviderId] = scopeRoots.Count(root => root.ProviderId == provider.ProviderId);
                diagnostics.AddRange(providerDiagnostics.Select(diagnostic =>
                    $"{diagnostic.ProviderId}: {diagnostic.Code} {diagnostic.AffectedScope}: {diagnostic.Reason}"));
            }

            var bindings = InjectionBindings.Discover(compilations, index, rootDirectory, cancellationToken);
            diagnostics.AddRange(bindings.SelectMany(type => type.Diagnostics)
                                         .Select(diagnostic => $"bindings: {diagnostic.Code} {diagnostic.Subject}: {diagnostic.Message}"));

            Record(timings, SCOPE_DISCOVERY, step);
            var program = ProgramIndexBuilder.Build(scope.Id, compilations, rootDirectory, cancellationToken);
            Record(timings, PROGRAM_INDEX, step);
            var lowering = new Stopwatch();
            var lower = Members(compilations, rootDirectory, lowered, diagnostics, cancellationToken);
            IReadOnlyList<IrBody> TimedMembers(string bodyId)
            {
                lowering.Start();
                try
                {
                    return lower(bodyId);
                }
                finally
                {
                    lowering.Stop();
                }
            }

            var reachable = ReachableSet.Build(new ReachabilityInput(program, scopeRoots, index, bindings, TimedMembers));
            timings[REACHABLE_SET] = timings.GetValueOrDefault(REACHABLE_SET) + step.Elapsed - lowering.Elapsed;
            timings[LOWERING] = timings.GetValueOrDefault(LOWERING) + lowering.Elapsed;
            step.Restart();
            var summaries = new SummaryCache(reachable.Bodies, program, limits);
            var scopeProgram = new ScopeProgram(scope.Id, scopeRoots, reachable, summaries, program, index, bindings);
            var heap = WholeProgram.Solve(scopeProgram, limits);
            Record(timings, SUMMARIES_AND_FIXPOINT, step);
            var executions = ExecutionModel.Build(scopeProgram, heap);
            Record(timings, EXECUTIONS, step);
            var collection = InterproceduralAccesses.Collect(new InterproceduralInput(scopeProgram, heap, executions));
            Record(timings, ACCESSES, step);
            var scopePairs = pairing(collection.Accesses, executions, heap);
            Record(timings, PAIRING, step);
            // The solver is the last filter (TD-091): what the cheap ones left, asked one candidate at a time and within the
            // run's share of its deadline. A solver that never started answers every question Unknown (ADR 0004).
            var refined = SolverRefinement.Refine(scopePairs, solver, solverBudget, SolverPolicy.QueryLimit);
            scopePairs = refined.Pairs;
            diagnostics.AddRange(refined.Traces.Select(trace => $"solver: {trace}"));
            Record(timings, SOLVER, step);
            sizes.Add(new ScopeSize(scope.Id, heap.ReachableBodies.Count, summaries.Built, heap.Regions.Count, heap.Instances.Count,
                                    collection.Accesses.Count(access => !access.IsConstructionLocal)));

            roots.AddRange(scopeRoots);
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
            coverage.Add(new ScopeCoverage(scope.Id, rootsPerProvider, diagnostics, index.Registrations.Count,
                                           Merged(collection.Coverage.Counters, refined.Counters))
            {
                TopOpaqueCallees = collection.Coverage.TopOpaqueCallees,
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
                                                           IReadOnlyDictionary<string, int> solver)
    {
        var merged = new SortedDictionary<string, int>(counters.ToDictionary(), StringComparer.Ordinal);
        foreach (var (name, count) in solver)
            merged[name] = merged.GetValueOrDefault(name) + count;
        return merged;
    }

    private static void Record(Dictionary<string, TimeSpan> timings, string name, Stopwatch step)
    {
        timings[name] = timings.GetValueOrDefault(name) + step.Elapsed;
        step.Restart();
    }

    /// <summary>The member provider of one scope: a body id maps to the first source method of the scope's compilations that has it,
    /// lowered once and cached by compilation, since two projects can share an assembly name and a declaration, not a body.</summary>
    private static Func<string, IReadOnlyList<IrBody>> Members(IReadOnlyList<Compilation> compilations, string rootDirectory,
                                                               Dictionary<(Compilation Compilation, string BodyId), IrLoweredMethod?> lowered,
                                                               List<string> diagnostics, CancellationToken cancellationToken)
    {
        var methods = new Dictionary<string, (IMethodSymbol Method, Compilation Compilation)>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var method in Methods(compilation.Assembly.GlobalNamespace))
                methods.TryAdd(IrLowering.RootBodyId(method), (method, compilation));
        }

        return bodyId =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!methods.TryGetValue(bodyId, out var member))
                return [];
            if (!lowered.TryGetValue((member.Compilation, bodyId), out var loweredMethod))
            {
                try
                {
                    loweredMethod = IrLowering.Lower(member.Method, member.Compilation, rootDirectory, cancellationToken);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    loweredMethod = null;
                    diagnostics.Add($"lowering: {bodyId}: {error.Message}");
                }

                lowered[(member.Compilation, bodyId)] = loweredMethod;
            }

            return loweredMethod is null ? [] : loweredMethod.NestedBodies.Prepend(loweredMethod.Body).ToArray();
        };
    }

    private static IEnumerable<IMethodSymbol> Methods(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf)
                  .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                  .Concat(@namespace.GetNamespaceMembers().SelectMany(Methods));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
