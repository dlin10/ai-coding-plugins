using System.Runtime.CompilerServices;
using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Access collection on several workers folds the accesses in the executions' order, so what it collects does not depend on
/// how many workers walk the executions or which of them finishes first (ADR 0040).</summary>
public sealed class ParallelCollectionTests
{
    private const int WORKERS = 4;
    private const int REPEATS = 3;

    [Fact]
    public async Task Demo_accesses_fold_on_four_workers_as_on_one() =>
        await ForEachDemoScope(input =>
        {
            var expected = Describe(InterproceduralAccesses.Collect(input, CancellationToken.None));
            Assert.NotEmpty(expected);
            for (var repeat = 0; repeat < REPEATS; repeat++)
                Assert.Equal(expected, Describe(InterproceduralAccesses.Collect(input with { Workers = WORKERS }, CancellationToken.None)));
        });

    /// <summary>The workers' path with one worker thread: the loop and the workers share the walk and the fold, and this pins that the
    /// workers' dispatch, window and ordered fold add nothing of their own. One thread walks at a time, so it reads the solved heap as the
    /// loop does.</summary>
    [Fact]
    public async Task Demo_accesses_fold_through_the_workers_on_one_worker_as_in_the_loop() =>
        await ForEachDemoScope(input =>
        {
            var expected = Describe(InterproceduralAccesses.Collect(input, CancellationToken.None));
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Describe(InterproceduralAccesses.Collect(input with { ThroughWorkers = true }, CancellationToken.None)));
        });

    /// <summary>The model makes an execution's entries anew on each request and keeps none (ADR 0039): the workers make them when they
    /// take an execution and the fold lets them go, so no more executions' entries are alive at once than the window of two executions
    /// per worker lets the workers take.</summary>
    [Fact]
    public async Task Workers_hold_no_more_executions_entries_at_once_than_their_window() =>
        await ForEachDemoScope(input =>
        {
            var most = 0;
            var made = 0;
            InterproceduralAccesses.Collect(input with
            {
                Workers = WORKERS,
                EntryListsHeld = held =>
                {
                    Interlocked.Increment(ref made);
                    // bounded: retries only while another worker raised the maximum in between.
                    for (var seen = Volatile.Read(ref most); held > seen; seen = Volatile.Read(ref most))
                        Interlocked.CompareExchange(ref most, held, seen);
                }
            }, CancellationToken.None);
            Assert.True(made > 0);
            Assert.InRange(most, 1, 2 * WORKERS);
        });

    /// <summary>Runs a check over the input of each demo scope, after the pipeline has collected it once, while its solution is
    /// loaded.</summary>
    /// <param name="check">The check of one scope's input.</param>
    internal static async Task ForEachDemoScope(Action<InterproceduralInput> check)
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var path = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(path);
        Assert.True(loaded.Coverage.LoadComplete, string.Join(", ", loaded.Coverage.MissingProjects));
        var rootDirectory = Path.GetDirectoryName(path)!;
        var repository = RepositoryRoot.Find(rootDirectory);
        var projectModels = ProjectModelFiles.Read(repository);
        var modelLock = ModelLock.Read(repository, projectModels);
        var cache = new Dictionary<LoweringKey, IrLoweredMethod?>();
        var scopes = ProcessScopes.Discover(loaded.Solution, rootDirectory).Scopes;
        Assert.NotEmpty(scopes);
        foreach (var scoped in scopes)
        {
            var compiled = new List<(Compilation Compilation, string? ProjectFilePath)>();
            foreach (var project in scoped.Projects)
                compiled.Add(((await project.GetCompilationAsync())!, project.FilePath));
            var compilations = compiled.Select(item => item.Compilation).ToArray();
            var (models, _) = ProjectModelResolver.Resolve(projectModels, compilations, modelLock);
            var run = ScopePipeline.Run(scoped.Scope.Id, compilations, compiled, rootDirectory, ProviderRegistry.BuiltIn, models,
                                        AnalysisLimits.Default, cache, null, CancellationToken.None);
            Assert.False(run.Stopped);
            // The pipeline has collected once, so every region a collection names is named already, and each collection reads the
            // same heap.
            check(new InterproceduralInput(run.ScopeProgram!, run.Heap!, run.Executions!));
        }
    }

    /// <summary>Each folded access in order, with its executions in order: the execution, the position, the variant's content, and
    /// which variant object it is, numbered in order of first use, since the fold tells variants apart by reference.</summary>
    /// <param name="collection">The collection.</param>
    private static string[] Describe(InterproceduralCollection collection)
    {
        var variants = new Dictionary<AccessVariant, int>(ReferenceEqualityComparer.Instance);
        int VariantNumber(AccessVariant variant)
        {
            if (!variants.TryGetValue(variant, out var number))
                variants.Add(variant, number = variants.Count);
            return number;
        }

        var accesses = collection.Folded.Select(access =>
            $"{access.BodyId}#{access.OperationId} {access.InstanceId} {access.Operation} {access.Resource.Identity} " +
            $"held=[{string.Join(",", access.HeldProtectionIds)}] local={access.IsConstructionLocal} fresh={access.IsFresh} " +
            $"term={access.SelectorTerm} conditions={access.Conditions.Count} " +
            string.Join(" ", access.Executions.Select(execution =>
                $"{execution.ExecutionId}@{execution.Position}:v{VariantNumber(execution.Variant)}" +
                $"({execution.Variant.Root.RootId}>{execution.Variant.PathRoot.RootId} [{string.Join("/", execution.Variant.CallPath)}] " +
                $"spawns={execution.Variant.SpawnSites.Count} flow=[{string.Join("/", execution.Variant.CodeFlow)}] " +
                $"reads=[{string.Join("/", execution.Variant.ReadSources)}])")));
        return [.. accesses, $"unseen-filled=[{string.Join(",", collection.UnseenFilledStorage)}]"];
    }
}
