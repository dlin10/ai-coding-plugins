using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Frontend;
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

    /// <summary>Scopes, then per scope the DI index, root providers in registration order, injection bindings, lowering
    /// (once per method across scopes), accesses and pairs; findings are built over the pairs of every scope.</summary>
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

            var bodies = Lower(compilations, scopeRoots, rootDirectory, lowered, diagnostics, cancellationToken);
            var extraction = AccessExtraction.Extract(new ScopeAnalysisInput(scope.Id, scopeRoots, bodies, index, bindings));
            var scopePairs = AccessPairing.Pair(extraction.Accesses);

            roots.AddRange(scopeRoots);
            accesses.AddRange(extraction.Accesses);
            pairs.AddRange(scopePairs.Pairs);
            candidates += scopePairs.CandidatePairs;
            suppressed += scopePairs.Suppressed;
            foreach (var (reason, count) in scopePairs.Skips)
                pairSkips[reason] = pairSkips.GetValueOrDefault(reason) + count;
            coverage.Add(new ScopeCoverage(scope.Id, rootsPerProvider, diagnostics, index.Registrations.Count, extraction.Skips));
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

    private static Dictionary<string, IrBody> Lower(IReadOnlyList<Compilation> compilations, IReadOnlyList<ExecutionRootDescriptor> roots,
                                                    string rootDirectory, Dictionary<(Compilation Compilation, string BodyId), IrLoweredMethod?> lowered,
                                                    List<string> diagnostics, CancellationToken cancellationToken)
    {
        var wanted = roots.Select(root => IrLowering.EnclosingMethodBodyId(root.Entry.BodyKey)).ToHashSet(StringComparer.Ordinal);
        var bodies = new Dictionary<string, IrBody>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var method in Methods(compilation.Assembly.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bodyId = IrLowering.RootBodyId(method);
                if (!wanted.Remove(bodyId))
                    continue;

                // Keyed by compilation as well: two projects can share an assembly name and a declaration, not a body.
                if (!lowered.TryGetValue((compilation, bodyId), out var loweredMethod))
                {
                    try
                    {
                        loweredMethod = IrLowering.Lower(method, compilation, rootDirectory, cancellationToken);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                    {
                        loweredMethod = null;
                        diagnostics.Add($"lowering: {bodyId}: {error.Message}");
                    }

                    lowered[(compilation, bodyId)] = loweredMethod;
                }

                foreach (var body in loweredMethod is null ? [] : loweredMethod.NestedBodies.Prepend(loweredMethod.Body))
                    bodies[body.BodyId] = body;
            }
        }

        return bodies;
    }

    private static IEnumerable<IMethodSymbol> Methods(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf)
                  .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                  .Where(method => method.DeclaringSyntaxReferences.Length != 0)
                  .Concat(@namespace.GetNamespaceMembers().SelectMany(Methods));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
