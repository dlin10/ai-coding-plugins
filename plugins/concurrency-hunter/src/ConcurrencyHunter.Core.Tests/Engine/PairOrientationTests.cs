using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using Xunit.Abstractions;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Pairing puts first the side of a pair of executions whose execution comes first, and of a pair of one execution, which
/// has no earlier side, the side the content of the two puts first (ADR 0042). So what pairing decides of a pair of executions must not
/// read which side comes first: the decision pairing itself makes (<see cref="InterproceduralPairing.Decide"/>: the hosted-transient rule,
/// the timing with the assumptions it rests on and the disjoint iteration) answers every pair of executions of every pair of accesses
/// the candidate index compares, an execution with itself among them, alike in either orientation; and the content of the two sides of a
/// pair of one execution puts one first, the same whichever way it is asked. The demo and eShop have no pair of one execution with one
/// side on a hosted transient and the other not, so a hosted-transient rule that read the first side alone would leave this test green;
/// <see cref="SharingAndOverlapTests"/>' hosted worker with a transient and a singleton is the case that tells the two apart.</summary>
/// <param name="output">Where the counts of pairs asked are written.</param>
public sealed class PairOrientationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Every_pair_of_executions_of_the_demo_is_decided_alike_in_either_orientation()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        await AssertSymmetricAsync(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"),
                                   ModelGeneration.ReadOnly);
    }

    [RequiresEShopFact]
    public async Task Every_pair_of_executions_of_eShop_is_decided_alike_in_either_orientation()
    {
        await AssertSymmetricAsync(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src",
                                                "eShopOnContainers-ServicesAndWebApps.sln"),
                                   ModelGeneration.None);
    }

    /// <summary>Analyzes a solution and asks, before each scope's pairing, every pair of executions of every pair of accesses its
    /// candidate index compares, in both orientations.</summary>
    /// <param name="solution">The solution's path.</param>
    /// <param name="generation">What the run does with the generated model layer.</param>
    private async Task AssertSymmetricAsync(string solution, ModelGeneration generation)
    {
        var asked = 0;
        var oneExecution = 0;
        var differ = new List<string>();
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solution);
        Assert.True(loaded.Coverage.LoadComplete);
        await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, Path.GetDirectoryName(solution)!, ProviderRegistry.BuiltIn, (accesses, executions, heap) =>
        {
            // bounded: every pair of executions of every pair the index compares, each asked twice: the whole product of the two sides'
            // executions, more than pairing asks, which stops at the first pair of each pair of variants that passes.
            foreach (var pair in CandidateIndex.Build(accesses.Where(access => !access.IsConstructionLocal).ToArray(), heap).Pairs())
            {
                var self = ReferenceEquals(pair.First, pair.Second);
                var ones = Views(pair.First);
                var others = self ? ones : Views(pair.Second);
                // bounded: the first side's executions.
                for (var at = 0; at < ones.Length; at++)
                {
                    // bounded: the second side's executions, from the first's own on a self-pair, as pairing pairs them.
                    for (var other = self ? at : 0; other < others.Length; other++)
                    {
                        var (first, second) = (ones[at], others[other]);
                        asked++;
                        var forward = InterproceduralPairing.Decide((first, second), null, null, executions, heap, out var forwardAssumed);
                        var backward = InterproceduralPairing.Decide((second, first), null, null, executions, heap, out var backwardAssumed);
                        if (forward != backward || !forwardAssumed.SetEquals(backwardAssumed))
                            differ.Add($"decision {first.ExecutionId} {first.BodyId}#{first.OperationId} / {second.ExecutionId} {second.BodyId}#{second.OperationId}");
                        if (first.ExecutionId != second.ExecutionId)
                            continue;
                        oneExecution++;
                        var (order, reverse) = (PairContent.CompareViews(first, second), PairContent.CompareViews(second, first));
                        if (ReferenceEquals(first, second) ? order != 0 : order == 0 || Math.Sign(order) != -Math.Sign(reverse))
                            differ.Add($"content {first.ExecutionId} {first.BodyId}#{first.OperationId} / {second.BodyId}#{second.OperationId}");
                    }
                }
            }

            return InterproceduralPairing.Pair(accesses, executions, heap);
        }, CancellationToken.None, generation);

        output.WriteLine($"asked {asked} pairs of executions, {oneExecution} of one execution");
        Assert.True(asked > 0 && oneExecution > 0, $"asked {asked} pairs of executions, {oneExecution} of one execution");
        Assert.Empty(differ);
    }

    /// <summary>An access as each of its executions has it, in collection order; an access built for one execution alone is its
    /// own.</summary>
    /// <param name="access">The access.</param>
    private static Access[] Views(Access access) =>
        access.Executions.Count == 0 ? [access] : access.Executions.Select(access.View).ToArray();
}
