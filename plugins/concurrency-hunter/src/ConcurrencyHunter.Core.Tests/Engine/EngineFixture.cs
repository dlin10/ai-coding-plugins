using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed record WholeProgramRun(string ScopeId, ReachabilityInput Input, ReachableSetResult Result, IReadOnlyList<string> LoweredMembers)
{
    public bool Reaches(string bodyId) => Result.ReachedBodies.ContainsKey(bodyId);

    public Construction Construction(string typeName) =>
        Assert.Single(Result.Constructions, construction => construction.TypeKey == $"Fixture:{typeName}");

    public IReadOnlyList<Construction> Constructions(string typeName) =>
        Result.Constructions.Where(construction => construction.TypeKey == $"Fixture:{typeName}").ToArray();

    public ExecutionRootDescriptor Root(string symbol) => Assert.Single(Input.Roots, root => root.Entry.Symbol == symbol);
}

public sealed record HeapRun(WholeProgramRun Program, HeapSolution Heap, SummaryCache Summaries)
{
    public IReadOnlyList<HeapRegion> Regions(string display) => Heap.Regions.Values.Where(region => region.Display == display).ToArray();

    public HeapRegion Region(string display) => Assert.Single(Regions(display));

    /// <summary>The displays of the regions a field of a region points to, ordered.</summary>
    public IReadOnlyList<string> Targets(HeapRegion region, string field) =>
        Heap.PointsTo(region.Identity, field).Select(id => Heap.Regions[id].Display).Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<MethodInstance> Instances(string bodyId) => Heap.Instances.Values.Where(instance => instance.BodyId == bodyId).ToArray();

    public int Counter(string name) => Heap.Counters.GetValueOrDefault(name);
}

public sealed record ExecutionRun(HeapRun Heap, ExecutionAnalysis Analysis)
{
    public IReadOnlyList<CollectedAccess> Accesses(string field, SummaryAccessKind kind) =>
        Analysis.Accesses.Where(access => access.Access.Field.Name == field && access.Access.Kind == kind).ToArray();

    public RegionOwnership Ownership(string display) => Analysis.Ownership[Heap.Region(display).Identity];

    public int Counter(string name) => Analysis.Counters.GetValueOrDefault(name);
}

/// <summary>A full engine run of one scope: its executions, interprocedural accesses and pairs.</summary>
public sealed record EngineRun(ExecutionRun Execution, InterproceduralCollection Collection, PairAnalysis Pairs)
{
    public IReadOnlyList<Access> Accesses(string field) =>
        Collection.Accesses.Where(access => access.Resource.AccessPath.SequenceEqual([field])).ToArray();

    public IReadOnlyList<AccessPair> PairsOn(string field) =>
        Pairs.Pairs.Where(pair => pair.Resource.AccessPath.SequenceEqual([field])).ToArray();

    /// <summary>The one access of a member with an operation, construction-local accesses aside.</summary>
    public Access Single(string member, AccessOperation operation) =>
        Assert.Single(Of(member), access => access.Operation == operation);

    /// <summary>The accesses of a member that can pair: construction-local accesses aside.</summary>
    public IReadOnlyList<Access> Of(string member) =>
        Collection.Accesses.Where(access => access.Resource.Member.Name == member && !access.IsConstructionLocal).ToArray();

    public int Skipped(string reason) => Pairs.Skips.GetValueOrDefault(reason);

    public int Counter(string name) => Collection.Coverage.Counters.GetValueOrDefault(name, Collection.Coverage.Ordering.GetValueOrDefault(name));
}

/// <summary>Runs the real pipeline on one source file the way the analyzer does: the DI index, both built-in providers, injection
/// bindings, the program index, the reachable set over a member provider that lowers each member once, summaries, the whole-program
/// heap, executions and ownership, then interprocedural accesses and pairs.</summary>
public static class EngineFixture
{
    public const string ROOT_DIRECTORY = @"C:\fixture";

    public const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        """;

    /// <summary>Registers and maps controllers so controller roots exist; append the case's registrations inside
    /// <c>Register</c> through <paramref name="registrations"/>.</summary>
    public static string Startup(string registrations = "") => $$"""

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
                {{registrations}}
            }
        }
        """;

    /// <summary>Runs the whole engine over one source file.</summary>
    public static EngineRun Analyze(string source, AnalysisLimits? limits = null) => Analyze(Execute(source, limits));

    public static EngineRun AnalyzeScope(Solution solution, string scopeId) => Analyze(Execute(Solve(ReachScope(solution, scopeId))));

    private static EngineRun Analyze(ExecutionRun execution)
    {
        var input = new InterproceduralInput(Scope(execution.Heap.Program, execution.Heap.Summaries), execution.Heap.Heap, execution.Analysis);
        var collection = InterproceduralAccesses.Collect(input);
        return new EngineRun(execution, collection, InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap));
    }

    /// <summary>Solves the whole-program heap of one source file over its reachable set.</summary>
    public static HeapRun Solve(string source, AnalysisLimits? limits = null) => Solve(Reach(source), limits);

    public static HeapRun Solve(WholeProgramRun run, AnalysisLimits? limits = null)
    {
        var summaries = new SummaryCache(run.Result.Bodies, run.Input.Program, limits ?? AnalysisLimits.Default);
        return new HeapRun(run, WholeProgram.Solve(Scope(run, summaries), limits ?? AnalysisLimits.Default), summaries);
    }

    /// <summary>Builds the executions, ownership and construction facts of one source file over its solved heap.</summary>
    public static ExecutionRun Execute(string source, AnalysisLimits? limits = null) => Execute(Solve(source, limits));

    public static ExecutionRun Execute(HeapRun heap) =>
        new(heap, ExecutionModel.Build(Scope(heap.Program, heap.Summaries), heap.Heap));

    private static ScopeProgram Scope(WholeProgramRun run, SummaryCache summaries) =>
        new(run.ScopeId, run.Input.Roots, run.Result, summaries, run.Input.Program, run.Input.DiIndex, run.Input.InjectionBindings);

    /// <summary>Builds the reachable set of one source file.</summary>
    public static WholeProgramRun Reach(string source) =>
        ReachScope(FixtureSolution.Create(("Case.cs", Usings + source)), "scope:Fixture");

    public static WholeProgramRun ReachScope(Solution solution, string scopeId)
    {
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();
        var index = DiIndexBuilder.Build(scopeId, compilations, ROOT_DIRECTORY, CancellationToken.None);
        var context = new RootDiscoveryContext(scopeId, compilations, ROOT_DIRECTORY, index, CancellationToken.None);
        var roots = ProviderRegistry.BuiltIn.Providers.SelectMany(provider => provider.Discover(context).Roots).ToArray();
        var bindings = InjectionBindings.Discover(compilations, index, ROOT_DIRECTORY, CancellationToken.None);
        var program = ProgramIndexBuilder.Build(scopeId, compilations, ROOT_DIRECTORY, CancellationToken.None);

        var methods = new Dictionary<string, (IMethodSymbol Method, Compilation Compilation)>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var method in Types(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
                methods.TryAdd(IrLowering.RootBodyId(method), (method, compilation));
        }

        var lowered = new List<string>();
        IReadOnlyList<IrBody> Lower(string memberId)
        {
            Assert.DoesNotContain(memberId, lowered);
            lowered.Add(memberId);
            if (!methods.TryGetValue(memberId, out var member))
                return [];
            try
            {
                var result = IrLowering.Lower(member.Method, member.Compilation, ROOT_DIRECTORY, CancellationToken.None);
                return result.NestedBodies.Prepend(result.Body).ToArray();
            }
            catch (ArgumentException)
            {
                return [];
            }
        }

        var input = new ReachabilityInput(program, roots, index, bindings, Lower);
        return new WholeProgramRun(scopeId, input, ReachableSet.Build(input), lowered);
    }

    /// <summary>The pairing before the candidate index, kept as the tests' reference: every unordered pair of non-construction-local
    /// accesses is tested against the rules that admit it (one resource; one region with a wildcard on either side; an open region and a
    /// closed region of its group, on one path and member or with a wildcard on either side), then compared as the index's
    /// <c>Consider</c> compares it.</summary>
    internal static PairAnalysis ReferencePair(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap)
    {
        var candidates = accesses.Where(access => !access.IsConstructionLocal).ToArray();
        var pairs = new List<AccessPair>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var counted = 0;
        var suppressed = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            for (var j = i; j < candidates.Length; j++)
            {
                if (ReferenceAdmits(candidates[i], candidates[j], heap) is { } admitted)
                    Consider(admitted.First, admitted.Second, admitted.Reported, admitted.Uncertainties);
            }
        }

        return new PairAnalysis(pairs, counted, suppressed, skips) { CartesianBound = candidates.Length * (candidates.Length + 1) / 2 };

        void Consider(Access first, Access second, AccessResource reported, IReadOnlyList<string> uncertainties)
        {
            counted++;
            var skip = first.Operation == AccessOperation.Read && second.Operation == AccessOperation.Read ? InterproceduralPairing.SKIP_READ_READ
                : !first.Operation.Conflicts() && !second.Operation.Conflicts() ? InterproceduralPairing.SKIP_NO_CONFLICTING_OPERATION
                : first.Resource.Scope != second.Resource.Scope || !executions.Overlaps(first.ExecutionId, second.ExecutionId) ||
                  first.ExecutionId == second.ExecutionId && heap.Regions[first.Resource.RegionId!] is { Kind: HeapRegionKind.Di } region &&
                  region.Context.StartsWith($"di|{ConcurrencyHunter.Di.DiIndex.HOSTED_SERVICE_KEY}|", StringComparison.Ordinal)
                    ? InterproceduralPairing.SKIP_NO_OVERLAP
                : executions.Ordered(first, second) ? InterproceduralPairing.SKIP_ORDERED
                : IsConfined(first) || IsConfined(second) ? InterproceduralPairing.SKIP_CONFINED
                : PathConditions.Contradict(first.Conditions, second.Conditions) ? InterproceduralPairing.SKIP_UNSATISFIABLE_PATH
                : InterproceduralPairing.IsDisjointIteration(first, second) ? InterproceduralPairing.SKIP_DISJOINT_ITERATION
                : null;
            if (skip is not null)
            {
                skips[skip] = skips.GetValueOrDefault(skip) + 1;
                return;
            }

            var protection = PairProtection.Of(first, second);
            if (protection == PairProtection.SUFFICIENT)
            {
                suppressed++;
                return;
            }

            pairs.Add(new AccessPair(first, second, protection) { Resource = reported, Uncertainties = uncertainties });
        }

        bool IsConfined(Access access) =>
            executions.Ownership.TryGetValue(access.Resource.RegionId!, out var ownership) && ownership.Kind == OwnershipKind.ThreadConfined;
    }

    /// <summary>Whether the reference admits two accesses, oriented and reported as the candidate index does, or null.</summary>
    internal static (Access First, Access Second, AccessResource Reported, IReadOnlyList<string> Uncertainties)? ReferenceAdmits(Access one, Access other,
                                                                                                                                HeapSolution heap)
    {
        if (one.Resource.Scope != other.Resource.Scope)
            return null;
        var wildcard = one.Resource.IsWildcard || other.Resource.IsWildcard;
        if (one.Resource.RegionId == other.Resource.RegionId)
        {
            if (!wildcard)
            {
                if (one.Resource.Identity == other.Resource.Identity)
                    return (one, other, one.Resource, []);
                // Two cells of one collection meet where their selectors may name one cell, and are reported on the proven one (TD-075).
                return one.Resource.StructuralIdentity == other.Resource.StructuralIdentity &&
                       one.Resource.Selector is { } selector && other.Resource.Selector is { } otherSelector &&
                       selector.MayOverlap(otherSelector)
                    ? (one, other, CandidateIndex.ReportedCell(one.Resource, other.Resource), [])
                    : null;
            }
            var (first, second) = one.Resource.IsWildcard ? (one, other) : (other, one);
            return (first, second, first.Resource, []);
        }

        var (oneRegion, otherRegion) = (heap.Regions[one.Resource.RegionId!], heap.Regions[other.Resource.RegionId!]);
        if (oneRegion.Group != otherRegion.Group || oneRegion.IsOpen == otherRegion.IsOpen)
            return null;
        var (open, closed) = oneRegion.IsOpen ? (one, other) : (other, one);
        var admitted = wildcard || open.Resource.Member.Identity == closed.Resource.Member.Identity &&
                                   open.Resource.AccessPath.SequenceEqual(closed.Resource.AccessPath);
        return admitted ? (open, closed, closed.Resource, open.Uncertainties) : null;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf).Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
