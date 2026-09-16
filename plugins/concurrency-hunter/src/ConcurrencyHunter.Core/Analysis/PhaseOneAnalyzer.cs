using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using ConcurrencyHunter.Scopes;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

public static class PhaseOneAnalyzer
{
    public static Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, CancellationToken cancellationToken) =>
        AnalyzeAsync(solution, rootDirectory, ProviderRegistry.BuiltIn, cancellationToken);

    /// <summary>Scopes, then per scope the DI index, root providers in registration order, injection bindings, the program index,
    /// the reachable set (lowering once per method across scopes), summaries, the whole-program heap, executions and ownership,
    /// interprocedural accesses and pairs; findings are built over the pairs of every scope.</summary>
    public static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, ProviderRegistry registry,
                                                          CancellationToken cancellationToken)
    {
        var discovery = ProcessScopes.Discover(solution, rootDirectory);
        var lowered = new Dictionary<(Compilation Compilation, string BodyId), IrLoweredMethod?>();
        var scopes = new List<ProcessScope>();
        var roots = new List<ExecutionRootDescriptor>();
        var accesses = new List<Access>();
        var pairs = new List<AccessPair>();
        var coverage = new List<ScopeCoverage>();
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
            foreach (var project in scoped.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation)
                    compilations.Add(compilation);
            }

            var index = DiIndexBuilder.Build(scope.Id, compilations, rootDirectory, cancellationToken);
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

            var program = ProgramIndexBuilder.Build(scope.Id, compilations, rootDirectory, cancellationToken);
            var members = Members(compilations, rootDirectory, lowered, diagnostics, cancellationToken);
            var reachable = ReachableSet.Build(new ReachabilityInput(program, scopeRoots, index, bindings, members));
            var summaries = new SummaryCache(reachable.Bodies, program, AnalysisLimits.Default);
            var scopeProgram = new ScopeProgram(scope.Id, scopeRoots, reachable, summaries, program, index, bindings);
            var heap = WholeProgram.Solve(scopeProgram, AnalysisLimits.Default);
            var executions = ExecutionModel.Build(scopeProgram, heap);
            var collection = InterproceduralAccesses.Collect(new InterproceduralInput(scopeProgram, heap, executions));
            var scopePairs = InterproceduralPairing.Pair(collection.Accesses, executions, heap);

            roots.AddRange(scopeRoots);
            accesses.AddRange(collection.Accesses);
            pairs.AddRange(scopePairs.Pairs);
            candidates += scopePairs.CandidatePairs;
            suppressed += scopePairs.Suppressed;
            foreach (var (reason, count) in scopePairs.Skips)
                pairSkips[reason] = pairSkips.GetValueOrDefault(reason) + count;
            coverage.Add(new ScopeCoverage(scope.Id, rootsPerProvider, diagnostics, index.Registrations.Count, collection.Coverage.Counters)
            {
                TopOpaqueCallees = collection.Coverage.TopOpaqueCallees,
                LoweredNotReached = collection.Coverage.LoweredNotReached,
                OutsideLoweredSet = collection.Coverage.OutsideLoweredSet
                                              .SelectMany(member => member.NestedBodyIds.Prepend(member.MemberId))
                                              .ToArray()
            });
        }

        var conflicts = ConflictFindings.Create(pairs, cancellationToken);
        var providers = registry.Providers
                                .Select(provider => new ProviderSummary(
                                    provider.ProviderId,
                                    provider.SupportedAssemblyVersions
                                            .Select(range => $"{range.AssemblyName} {range.Minimum}..{range.MaximumExclusive}")
                                            .ToArray()))
                                .ToArray();
        return new AnalysisResult(scopes, roots, accesses, conflicts.Findings, conflicts.Groups, coverage,
                                  new PairCounters(candidates, suppressed, pairSkips), providers);
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
