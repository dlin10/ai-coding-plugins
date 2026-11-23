using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The census of a diagnostic run's <c>census</c> checkpoint (ADR 0042, step 1): each execution's own entries, its
/// construction entries and its class, counted over the construction entries' bits and agreeing with the lists
/// <see cref="ExecutionModel.EntryLists"/> gives.</summary>
public sealed class EntryCensusTests
{
    [Fact]
    public void Executions_with_one_list_of_entries_are_one_class_and_the_counts_are_those_of_their_lists()
    {
        var ids = new ExecutionIds();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in new[] { "root:first", "root:second", "construction:part", "spawn:other", "root:third", "root:fourth", "root:fifth" })
            numbers[id] = ids.Number(id);
        var firstRoot = new ExecutionEntry("first", ExecutionEntryKind.Root, null);
        var secondRoot = new ExecutionEntry("second", ExecutionEntryKind.Root, null);
        var spawn = new ExecutionEntry("work", ExecutionEntryKind.Spawn, null);
        var part = new ExecutionModel.ConstructionEntry(new ExecutionEntry("part-ctor", ExecutionEntryKind.Construction, "di|Part"),
                                                        Bits.Of([numbers["root:first"], numbers["root:second"], numbers["construction:part"], numbers["root:third"]]));
        var holder = new ExecutionModel.ConstructionEntry(new ExecutionEntry("holder-ctor", ExecutionEntryKind.Construction, "di|Thing"),
                                                          Bits.Of([numbers["root:second"], numbers["root:fifth"]]));
        var own = new Dictionary<string, List<ExecutionEntry>>(StringComparer.Ordinal)
        {
            ["root:first"] = [firstRoot],
            ["root:second"] = [secondRoot],
            ["spawn:other"] = [spawn],
            // The first one's own entries and construction entries.
            ["root:third"] = [firstRoot],
            // As many own entries as the spawn and no construction entry, but other ones.
            ["root:fourth"] = [secondRoot],
            // The first one's own entries and as many construction entries, but another one.
            ["root:fifth"] = [firstRoot]
        };
        var executions = numbers.Keys.Select(id => new ExecutionInstance(id, id.StartsWith("root:", StringComparison.Ordinal) ? ExecutionKind.Root
                                                                             : id.StartsWith("spawn:", StringComparison.Ordinal) ? ExecutionKind.Spawn
                                                                             : ExecutionKind.LazyConstruction,
                                                                         id, POLICY, null, null))
                                    .ToArray();

        var census = ExecutionModel.EntryCensus(executions, own, [part, holder], ids);

        Assert.Equal(executions.Select(execution => execution.Id), census.Select(row => row.Execution));
        Assert.Equal(executions.Select(execution => execution.Kind), census.Select(row => row.Kind));
        Assert.Equal([1, 1, 0, 1, 1, 1, 1], census.Select(row => row.Own));
        Assert.Equal([1, 2, 1, 0, 1, 0, 1], census.Select(row => row.Constructions));
        Assert.Equal([0, 1, 2, 3, 0, 4, 5], census.Select(row => row.Class));
        // Without the hashes the spawn and the fourth meet as candidates, and so do the first, the third and the fifth.
        Assert.Equal(census, ExecutionModel.EntryCensus(executions, own, [part, holder], ids, hashes: false));

        // The counts and the classes are what the lists the model gives each execution say.
        var lists = ExecutionModel.EntryLists(own, [part, holder], ids);
        foreach (var row in census)
        {
            var list = lists.ContainsKey(row.Execution) ? lists[row.Execution] : [];
            Assert.Equal(list.Count, row.Own + row.Constructions);
            Assert.Equal(list.Count(entry => entry.Kind == ExecutionEntryKind.Construction), row.Constructions);
            foreach (var other in census)
            {
                var otherList = lists.ContainsKey(other.Execution) ? lists[other.Execution] : [];
                Assert.Equal(list.SequenceEqual(otherList), row.Class == other.Class);
            }
        }
    }

    /// <summary>The classes are exact. With the hashes turned off, every execution here meets the others as a candidate, since each has
    /// two construction entries and one own entry, and only the exact comparison tells apart those that other construction entries
    /// list or whose own entry is another: the classes are those the hashes give.</summary>
    [Fact]
    public void Candidates_share_a_class_only_when_the_same_construction_entries_list_them()
    {
        var ids = new ExecutionIds();
        string[] names = ["root:first", "root:second", "root:third", "root:fourth", "root:fifth"];
        var numbers = names.ToDictionary(id => id, ids.Number, StringComparer.Ordinal);
        ExecutionModel.ConstructionEntry Construction(string name, params string[] listed) =>
            new(new ExecutionEntry($"{name}-ctor", ExecutionEntryKind.Construction, $"di|{name}"), Bits.Of(listed.Select(id => numbers[id])));
        // The first, the fourth and the fifth are listed by a and c; the second by a and d, so it differs from the first only at the
        // last construction entry; the third by b and d.
        ExecutionModel.ConstructionEntry[] constructions =
        [
            Construction("a", "root:first", "root:second", "root:fourth", "root:fifth"),
            Construction("b", "root:third"),
            Construction("c", "root:first", "root:fourth", "root:fifth"),
            Construction("d", "root:second", "root:third")
        ];
        // Every execution has one own entry; the fifth's is another.
        var own = names.ToDictionary(id => id, id => new List<ExecutionEntry> { new(id == "root:fifth" ? "other" : "same", ExecutionEntryKind.Root, null) },
                                     StringComparer.Ordinal);
        var executions = names.Select(id => new ExecutionInstance(id, ExecutionKind.Root, id, POLICY, null, null)).ToArray();

        var candidates = ExecutionModel.EntryCensus(executions, own, constructions, ids, hashes: false);

        Assert.Equal([2, 2, 2, 2, 2], candidates.Select(row => row.Constructions));
        Assert.Equal([0, 1, 2, 0, 3], candidates.Select(row => row.Class));
        Assert.Equal(ExecutionModel.EntryCensus(executions, own, constructions, ids), candidates);
    }

    /// <summary>The census of a built model covers every execution the model ends with: the spawned work and the tail of an async call
    /// nobody awaits at once, which the walk makes, and startup, which comes after the walk, each with the counts of the entry list
    /// the model gives it.</summary>
    [Fact]
    public void A_built_model_has_a_census_row_for_every_execution_with_its_entry_list()
    {
        var analysis = EngineFixture.Execute("""
            public sealed class Store { public int Value; }
            public sealed class Worker(Store store) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = Task.Run(() => store.Value = 1);
                    _ = SetAsync(stoppingToken);
                    return Task.CompletedTask;
                }

                private async Task SetAsync(CancellationToken token)
                {
                    await Task.Delay(1, token);
                    store.Value = 2;
                }
            }
            """ + EngineFixture.Startup("services.AddSingleton<Store>(); services.AddHostedService<Worker>();")).Analysis;

        var census = analysis.EntryCensus();

        Assert.Equal(analysis.Executions.Select(execution => execution.Id), census.Select(row => row.Execution));
        Assert.Contains(census, row => row.Kind == ExecutionKind.Spawn);
        Assert.Contains(census, row => row.Execution.Contains(">async:", StringComparison.Ordinal));
        Assert.Contains(census, row => row.Kind == ExecutionKind.Startup);
        foreach (var row in census)
        {
            var list = analysis.Entries.TryGetValue(row.Execution, out var entries) ? entries : [];
            Assert.Equal(list.Count, row.Own + row.Constructions);
            Assert.Equal(list.Count(entry => entry.Kind == ExecutionEntryKind.Construction), row.Constructions);
            foreach (var other in census)
            {
                var otherList = analysis.Entries.TryGetValue(other.Execution, out var otherEntries) ? otherEntries : [];
                Assert.Equal(list.SequenceEqual(otherList), row.Class == other.Class);
            }
        }
    }

    private static readonly InvocationPolicy POLICY = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, "scope:Fixture");
}
