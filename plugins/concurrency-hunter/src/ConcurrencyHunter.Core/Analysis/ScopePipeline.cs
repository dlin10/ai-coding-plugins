using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>The elapsed time of each step of one scope's pipeline.</summary>
/// <param name="RootDiscovery">The DI index, the root providers and the injection bindings.</param>
/// <param name="ProgramIndex">The program index.</param>
/// <param name="Lowering">The lowering the member provider did while the reachable set was built.</param>
/// <param name="ReachableSet">The reachable set, without <paramref name="Lowering"/>.</param>
/// <param name="SummariesAndFixpoint">The summaries and the whole-program heap.</param>
/// <param name="Executions">The executions and their ownership.</param>
/// <param name="Accesses">The interprocedural accesses.</param>
public sealed record ScopeStepTimes(TimeSpan RootDiscovery, TimeSpan ProgramIndex, TimeSpan Lowering, TimeSpan ReachableSet,
                                    TimeSpan SummariesAndFixpoint, TimeSpan Executions, TimeSpan Accesses);

/// <summary>Everything one scope's pipeline built. A run stopped by its reachable-body limit has no part after reachability:
/// <see cref="Summaries"/>, <see cref="ScopeProgram"/>, <see cref="Heap"/>, <see cref="Executions"/>,
/// <see cref="Interprocedural"/> and <see cref="Collection"/> are all set when it was not stopped, and all <c>null</c> when it
/// was.</summary>
/// <param name="Index">The scope's DI index.</param>
/// <param name="Roots">The roots of every provider in registry order, a duplicate stable root id kept only from the first.</param>
/// <param name="RootsPerProvider">The number of roots each provider contributed, by provider id.</param>
/// <param name="Bindings">The injection bindings.</param>
/// <param name="Program">The program index.</param>
/// <param name="Reachable">The reachable set.</param>
/// <param name="Summaries">The method summaries over the reachable bodies.</param>
/// <param name="ScopeProgram">The scope's program the heap is solved over.</param>
/// <param name="Heap">The whole-program heap.</param>
/// <param name="Executions">The executions and their ownership.</param>
/// <param name="Interprocedural">The input the interprocedural accesses were collected from.</param>
/// <param name="Collection">The interprocedural accesses with their coverage.</param>
/// <param name="DiscoveryDiagnostics">The DI index's, the providers' and the bindings' diagnostics, in that order.</param>
/// <param name="LoweringDiagnostics">The diagnostics of the members whose lowering failed in this run.</param>
/// <param name="Times">The elapsed time of each step.</param>
/// <param name="StoppedAtReachableBodies">The bodies the reachable set reached when that was over the run's limit, else
/// <c>null</c>.</param>
public sealed record ScopeRun(DiIndex Index, IReadOnlyList<ExecutionRootDescriptor> Roots, IReadOnlyDictionary<string, int> RootsPerProvider,
                              IReadOnlyList<TypeInjectionBindings> Bindings, ProgramIndex Program, ReachableSetResult Reachable,
                              SummaryCache? Summaries, ScopeProgram? ScopeProgram, HeapSolution? Heap, ExecutionAnalysis? Executions,
                              InterproceduralInput? Interprocedural, InterproceduralCollection? Collection,
                              IReadOnlyList<string> DiscoveryDiagnostics, IReadOnlyList<string> LoweringDiagnostics, ScopeStepTimes Times,
                              int? StoppedAtReachableBodies)
{
    [MemberNotNullWhen(false, nameof(Summaries), nameof(ScopeProgram), nameof(Heap), nameof(Executions), nameof(Interprocedural),
                       nameof(Collection))]
    public bool Stopped => StoppedAtReachableBodies is not null;

    /// <summary>The counters of each step that keeps them, as a cancellation reports them: the heap's, the walk visits of the
    /// executions, and how many executions the accesses went through and how many accesses they collected.</summary>
    public IReadOnlyDictionary<ScopeStep, IReadOnlyDictionary<string, int>> Counters { get; init; } =
        new Dictionary<ScopeStep, IReadOnlyDictionary<string, int>>();
}

/// <summary>The stages one scope runs through, the same for every caller: the DI index, root providers in registry order with
/// duplicate-id diagnostics, injection bindings, the program index, the reachable set (lowering once per method through the
/// shared cache), summaries, the whole-program heap, executions and ownership, and interprocedural accesses. What belongs to the
/// analysis run — project models, the model lock, scope discovery, pairing, the solver, findings — is the caller's.</summary>
public static class ScopePipeline
{
    /// <summary>Runs one scope's stages.</summary>
    /// <param name="scopeId">The scope's id.</param>
    /// <param name="compilations">The scope's compilations.</param>
    /// <param name="projectFiles">Each compilation with the project file it came from, when it has one.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to.</param>
    /// <param name="registry">The root providers, run in their registration order.</param>
    /// <param name="models">The library models lowering resolves known calls against.</param>
    /// <param name="limits">The summary and heap limits.</param>
    /// <param name="loweringCache">The lowered methods of the whole analysis, keyed by compilation, body id and models. A cache lives for
    /// one analysis or one member's generation, every run of which passes the same <paramref name="reachability"/>, so the key carries
    /// no left-out types.</param>
    /// <param name="reachableBodyLimit">The most bodies the reachable set may reach before the run stops after reachability;
    /// <c>null</c> for no limit.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="stepStarted">Notified before a step starts, so a caller can set its deadline or cancel before it runs.</param>
    /// <param name="reachability">The rules the reachable set is built by; <c>null</c> for class-hierarchy dispatch with no type
    /// left out.</param>
    public static ScopeRun Run(string scopeId, IReadOnlyList<Compilation> compilations,
                               IReadOnlyList<(Compilation Compilation, string? ProjectFilePath)> projectFiles, string rootDirectory,
                               ProviderRegistry registry, LibraryModels models, AnalysisLimits limits,
                               Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?> loweringCache,
                               int? reachableBodyLimit, CancellationToken cancellationToken, Action<ScopeStep>? stepStarted = null,
                               ReachabilityRules? reachability = null)
    {
        var step = new Stopwatch();
        var lowering = new Stopwatch();
        var current = ScopeStep.RootDiscovery;
        var started = false;
        int? reachableBodies = null;
        var completed = new Dictionary<ScopeStep, TimeSpan>();
        var counters = new Dictionary<ScopeStep, IReadOnlyDictionary<string, int>>();

        void Begin(ScopeStep next)
        {
            current = next;
            started = false;
            cancellationToken.ThrowIfCancellationRequested();
            stepStarted?.Invoke(next);
            cancellationToken.ThrowIfCancellationRequested();
            step.Restart();
            started = true;
        }

        TimeSpan Complete()
        {
            cancellationToken.ThrowIfCancellationRequested();
            step.Stop();
            completed[current] = step.Elapsed;
            return step.Elapsed;
        }

        try
        {
            Begin(ScopeStep.RootDiscovery);
            var discoveryDiagnostics = new List<string>();
            var index = DiIndexBuilder.Build(scopeId, projectFiles, rootDirectory, cancellationToken);
            discoveryDiagnostics.AddRange(index.Diagnostics.Select(diagnostic => $"di: {diagnostic.Code} {diagnostic.Subject}: {diagnostic.Message}"));

            var context = new RootDiscoveryContext(scopeId, compilations, rootDirectory, index, cancellationToken);
            var roots = new List<ExecutionRootDescriptor>();
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
                        roots.Add(root);
                        continue;
                    }

                    providerDiagnostics.Add(new RootDiscoveryDiagnostic(
                        provider.ProviderId, RootDiscoveryDiagnosticCode.DiscoveryFailed, root.StableRootId,
                        $"stable root id {root.StableRootId} was already produced by an earlier provider", []));
                }

                rootsPerProvider[provider.ProviderId] = roots.Count(root => root.ProviderId == provider.ProviderId);
                discoveryDiagnostics.AddRange(providerDiagnostics.Select(diagnostic =>
                    $"{diagnostic.ProviderId}: {diagnostic.Code} {diagnostic.AffectedScope}: {diagnostic.Reason}"));
            }

            var bindings = InjectionBindings.Discover(compilations, index, rootDirectory, cancellationToken);
            discoveryDiagnostics.AddRange(bindings.SelectMany(type => type.Diagnostics)
                                                  .Select(diagnostic => $"bindings: {diagnostic.Code} {diagnostic.Subject}: {diagnostic.Message}"));
            var rootDiscovery = Complete();

            Begin(ScopeStep.ProgramIndex);

            var program = ProgramIndexBuilder.Build(scopeId, compilations, rootDirectory, cancellationToken);
            var programIndex = Complete();

            Begin(ScopeStep.ReachableSet);
            var metadataSupertypes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            var loweringDiagnostics = new List<string>();
            var rules = reachability ?? ReachabilityRules.ClassHierarchy;
            var lower = Members(compilations, rootDirectory, models, rules.LeftOutTypeKeys, loweringCache, metadataSupertypes, loweringDiagnostics,
                                cancellationToken);
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

            var reachable = ReachableSet.Build(new ReachabilityInput(program, roots, index, bindings, TimedMembers)
            {
                Rules = rules
            });
            var reachableSet = Complete() - lowering.Elapsed;
            completed[ScopeStep.ReachableSet] = reachableSet;
            completed[ScopeStep.Lowering] = lowering.Elapsed;
            reachableBodies = reachable.ReachedBodies.Count;
            if (reachable.ReachedBodies.Count > reachableBodyLimit)
            {
                return new ScopeRun(index, roots, rootsPerProvider, bindings, program, reachable, null, null, null, null, null, null,
                                    discoveryDiagnostics, loweringDiagnostics,
                                    new ScopeStepTimes(rootDiscovery, programIndex, lowering.Elapsed, reachableSet, TimeSpan.Zero, TimeSpan.Zero,
                                                       TimeSpan.Zero),
                                    reachable.ReachedBodies.Count);
            }

            Begin(ScopeStep.SummariesAndFixpoint);
            var summaries = new SummaryCache(reachable.Bodies, program, limits, reachable);
            var scopeProgram = new ScopeProgram(scopeId, roots, reachable, summaries, program, index, bindings)
            {
                MetadataSupertypes = metadataSupertypes
            };
            var heap = WholeProgram.Solve(scopeProgram, limits, cancellationToken);
            counters[ScopeStep.SummariesAndFixpoint] = new Dictionary<string, int>(heap.Counters, StringComparer.Ordinal);
            var summariesAndFixpoint = Complete();

            Begin(ScopeStep.Executions);
            var executions = ExecutionModel.Build(scopeProgram, heap, cancellationToken);
            counters[ScopeStep.Executions] = new Dictionary<string, int>(StringComparer.Ordinal) { ["walkVisits"] = executions.WalkVisits };
            var executionTime = Complete();

            Begin(ScopeStep.Accesses);
            var interprocedural = new InterproceduralInput(scopeProgram, heap, executions);
            var collection = InterproceduralAccesses.Collect(interprocedural, cancellationToken);
            counters[ScopeStep.Accesses] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["executions"] = executions.Executions.Count,
                ["accesses"] = collection.Accesses.Count
            };
            var accesses = Complete();
            return new ScopeRun(index, roots, rootsPerProvider, bindings, program, reachable, summaries, scopeProgram, heap, executions,
                                interprocedural, collection, discoveryDiagnostics, loweringDiagnostics,
                                new ScopeStepTimes(rootDiscovery, programIndex, lowering.Elapsed, reachableSet, summariesAndFixpoint, executionTime,
                                                   accesses),
                                null)
            {
                Counters = counters
            };
        }
        catch (OperationCanceledException error)
        {
            if (error is EngineStageCancelledException engine)
                counters[current] = engine.Counters;
            var nested = current == ScopeStep.ReachableSet && started ? lowering.Elapsed : TimeSpan.Zero;
            var elapsed = started ? step.Elapsed - nested : TimeSpan.Zero;
            throw new ScopeCancelledException(current, started, elapsed, nested,
                                              new Dictionary<ScopeStep, TimeSpan>(completed), reachableBodies,
                                              new Dictionary<ScopeStep, IReadOnlyDictionary<string, int>>(counters), error, cancellationToken);
        }
    }

    /// <summary>The member provider of one scope: a body id maps to the first source method of the scope's compilations that has it,
    /// lowered once and cached by compilation, since two projects can share an assembly name and a declaration, not a body.</summary>
    /// <param name="compilations">The scope's compilations, in order.</param>
    /// <param name="rootDirectory">The directory source paths are reported relative to.</param>
    /// <param name="models">The library models lowering resolves known calls against.</param>
    /// <param name="leftOutTypeKeys">The types the reachable set leaves out, whose members lowering treats as members without a body.</param>
    /// <param name="lowered">The lowered methods of the whole analysis.</param>
    /// <param name="metadataSupertypes">Receives the supertypes of the metadata types the lowered methods name.</param>
    /// <param name="diagnostics">Receives one diagnostic per member whose lowering failed.</param>
    /// <param name="cancellationToken">Cancels the lowering.</param>
    private static Func<string, IReadOnlyList<IrBody>> Members(IReadOnlyList<Compilation> compilations, string rootDirectory,
                                                               LibraryModels models, IReadOnlySet<string> leftOutTypeKeys,
                                                               Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?> lowered,
                                                               Dictionary<string, IReadOnlySet<string>> metadataSupertypes,
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
            if (!lowered.TryGetValue((member.Compilation, bodyId, models), out var loweredMethod))
            {
                try
                {
                    loweredMethod = IrLowering.Lower(member.Method, member.Compilation, rootDirectory, cancellationToken,
                                                     models, leftOutTypeKeys);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    loweredMethod = null;
                    diagnostics.Add($"lowering: {bodyId}: {error.Message}");
                }

                lowered[(member.Compilation, bodyId, models)] = loweredMethod;
            }

            if (loweredMethod is null)
                return [];
            foreach (var (type, supertypes) in loweredMethod.MetadataSupertypes)
                metadataSupertypes[type] = supertypes;
            return loweredMethod.NestedBodies.Prepend(loweredMethod.Body).ToArray();
        };
    }

    private static IEnumerable<IMethodSymbol> Methods(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf)
                  .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                  .Concat(@namespace.GetNamespaceMembers().SelectMany(Methods));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
