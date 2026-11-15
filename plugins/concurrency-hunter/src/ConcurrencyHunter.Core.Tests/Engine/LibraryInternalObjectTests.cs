using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class LibraryInternalObjectTests
{
    private const string ITEM = "public sealed class Item { public int Hits; }";
    private const string SAVED = ITEM + "public static class Entry { private static readonly Item Saved = new Item(); public static void Write() { Saved.Hits++; } }";

    [Fact]
    public void Two_library_writes_to_an_object_a_library_static_keeps_make_no_pair() =>
        Internal(Run(SAVED, "Entry.Write();"));

    [Fact]
    public void Two_library_writes_to_the_inside_of_a_library_object_the_program_holds_make_no_pair() =>
        Internal(Run(ITEM + "public sealed class Held { private readonly Item Inside = new Item(); public void Write() { Inside.Hits++; } }" +
                     "public static class Entry { public static Held Make() => new Held(); }",
                     "_held.Write();", "private readonly Held _held = Entry.Make();"));

    [Fact]
    public void Two_library_writes_to_a_library_object_the_program_holds_keep_their_pair() =>
        Preserved(Run("public sealed class Held { public int Hits; public void Write() { Hits++; } }" +
                      "public static class Entry { public static Held Make() => new Held(); }",
                      "_held.Write();", "private readonly Held _held = Entry.Make();"));

    [Fact]
    public void A_library_write_and_a_program_write_keep_their_pair() =>
        Preserved(Run(ITEM + "public static class Entry { public static readonly Item Saved = new Item(); public static void Write() { Saved.Hits++; } }",
                      "Entry.Write(); Entry.Saved.Hits++;"));

    [Fact]
    public void Two_library_writes_to_an_object_the_program_created_keep_their_pair() =>
        Preserved(Run(ITEM + "public static class Entry { public static void Write(Item value) { value.Hits++; } }",
                      "Entry.Write(_item);", "private readonly Item _item = new Item();"));

    [Fact]
    public void A_library_object_handed_to_a_program_delegate_keeps_its_pair() =>
        Preserved(Run(ITEM + "public static class Entry { private static readonly Item Saved = new Item(); " +
                      "public static void Write(Action<Item> action) { action(Saved); Saved.Hits++; } }",
                      "Entry.Write(value => { _ = value; });"));

    [Fact]
    public void A_library_static_field_keeps_its_pair() =>
        Preserved(Run("public static class Entry { public static int Hits; public static void Write() { Hits++; } }", "Entry.Write();"));

    [Fact]
    public void A_merged_library_object_keeps_its_pair()
    {
        var run = Run(ITEM + "public static class Maker { public static Item Make(object marker) => new Item(); }" +
                      "public static class Entry { public static void Write() { var first = Maker.Make(new object()); " +
                      "var second = Maker.Make(new object()); var third = Maker.Make(new object()); " +
                      "Helpers.Kept = first; Helpers.Kept = second; Helpers.Kept = third; Touch(first); Touch(second); Touch(third); }" +
                      "private static void Touch(Item value) { value.Hits++; } }", "Entry.Write();",
                      limits: AnalysisLimits.Default with { MaxContextsPerMethod = 1 });
        Assert.Contains(run.Heap!.Regions.Values, region => region.IsMerged && region.TypeKey == "SiteLibrary:Item");
        Preserved(run);
    }

    [Fact]
    public void A_library_list_inside_a_library_object_the_program_holds_makes_no_pair() =>
        Internal(Run("public sealed class Held { private readonly List<int> Items = new List<int>(); public void Write() { Items.Add(1); } }" +
                     "public static class Entry { public static Held Make() => new Held(); }",
                     "_held.Write();", "private readonly Held _held = Entry.Make();"), "Items");

    [Fact]
    public void A_program_list_inside_a_library_internal_object_keeps_its_pair() =>
        Preserved(Run("public sealed class Inside { public List<int> Items = null!; } public static class Entry { " +
                      "private static readonly Inside Saved = new Inside(); public static void Write(List<int> items) { Saved.Items = items; Saved.Items.Add(1); } }",
                      "Entry.Write(_items);", "private readonly System.Collections.Generic.List<int> _items = new();"), "Items", collection: true);

    [Fact]
    public void A_protected_pair_on_a_library_internal_object_is_still_suppressed()
    {
        var run = Run(ITEM + "public static class Entry { private static readonly Item Saved = new Item(); private static readonly object Gate = new object(); " +
                      "public static void Write() { lock (Gate) { Saved.Hits++; } } }", "Entry.Write();");
        var observation = Observe(run, "Hits");
        Assert.All(observation.Accesses, access => Assert.True(access.IsLibraryInternal));
        Assert.True(observation.Before.Suppressed > 0);
        Assert.Equal(observation.Before.Suppressed, observation.After.Suppressed);
        Assert.Empty(observation.After.Pairs);
        SameExistingCounts(observation);
        Assert.Equal(0, observation.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
    }

    [Fact]
    public void A_pair_an_earlier_reason_skips_keeps_its_reason()
    {
        var reads = Observe(Run(ITEM + "public static class Entry { private static readonly Item Saved = new Item(); public static void Read() { _ = Saved.Hits; } }",
                                "Entry.Read();"), "Hits");
        Assert.All(reads.Accesses, access => Assert.True(access.IsLibraryInternal));
        Assert.True(reads.Before.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_READ_READ) > 0);
        SameExistingCounts(reads);
        Assert.Equal(0, reads.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));

        var run = Run(ITEM + "public static class Entry { private static readonly Item Saved = new Item(); public static async Task WriteThenRead() { " +
                      "await Task.Run(() => { Saved.Hits++; }); _ = Saved.Hits; } }", "await Entry.WriteThenRead();");
        var all = Observe(run, "Hits");
        var root = all.Accesses.First().Root.RootId;
        var oneTree = all.Accesses.Where(access => access.Root.RootId == root).ToArray();
        var ordered = Compare(run, oneTree);
        Assert.All(oneTree, access => Assert.True(access.IsLibraryInternal));
        Assert.True(ordered.Before.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_ORDERED) > 0);
        SameExistingCounts(ordered);
        Assert.Equal(0, ordered.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
    }

    [Fact]
    public void A_library_struct_that_carries_the_object_is_no_program_holder() =>
        Internal(Run(ITEM + "public struct Carrier { public Item Value; } public static class Entry { private static readonly Item Saved = new Item(); " +
                     "public static void Write() { var carrier = new Carrier { Value = Saved }; Copy(carrier); Saved.Hits++; } " +
                     "private static void Copy(Carrier carrier) { _ = carrier.Value; } }", "Entry.Write();"));

    [Fact]
    public void A_program_struct_that_carries_the_object_is_a_program_holder() =>
        Preserved(Run(ITEM + "public struct Carrier { public Item Value; } public static class Entry { private static readonly Item Saved = new Item(); " +
                      "public static Carrier Write() { Saved.Hits++; return new Carrier { Value = Saved }; } }", "var carrier = Entry.Write();"));

    private sealed record Observation(Access[] Accesses, PairAnalysis Before, PairAnalysis After);

    private static ScopeRun Run(string library, string work, string fields = "", AnalysisLimits? limits = null) =>
        CallSiteContextMatrixTests.Run(library, Usings + $$"""
            public sealed class Shared
            {
                {{fields}}
                public async Task Run() { {{work}} }
            }
            public sealed class LeftWorker(Shared shared) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken token) { await shared.Run(); }
            }
            public sealed class RightWorker(Shared shared) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken token) { await shared.Run(); }
            }
            """ + Startup("services.AddSingleton<Shared>(); services.AddHostedService<LeftWorker>(); services.AddHostedService<RightWorker>();"), limits);

    private static Observation Observe(ScopeRun run, string member, bool collection = false)
    {
        var accesses = run.Collection!.Accesses.Where(access => !access.IsConstructionLocal && access.Resource.Member.Name == member &&
                                                              (!collection || access.Resource.CollectionId is not null)).ToArray();
        Assert.NotEmpty(accesses);
        Assert.Equal(2, accesses.Select(access => access.Root.RootId).Distinct().Count());
        return Compare(run, accesses);
    }

    private static Observation Compare(ScopeRun run, Access[] accesses) =>
        new(accesses, InterproceduralPairing.Pair(accesses.Select(access => access with { IsLibraryInternal = false }).ToArray(), run.Executions!, run.Heap!),
            InterproceduralPairing.Pair(accesses, run.Executions!, run.Heap!));

    private static void Internal(ScopeRun run, string member = "Hits")
    {
        var observation = Observe(run, member);
        Assert.All(observation.Accesses, access => Assert.True(access.IsLibraryInternal));
        Assert.NotEmpty(observation.Before.Pairs);
        Assert.Empty(observation.After.Pairs);
        Assert.Equal(observation.Before.Pairs.Count, observation.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
        SameExistingCounts(observation);
    }

    private static void Preserved(ScopeRun run, string member = "Hits", bool collection = false)
    {
        var observation = Observe(run, member, collection);
        Assert.NotEmpty(observation.Before.Pairs);
        Assert.Equal(observation.Before.Pairs.Count, observation.After.Pairs.Count);
        Assert.Equal(0, observation.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
        SameExistingCounts(observation);
    }

    private static void SameExistingCounts(Observation observation)
    {
        Assert.Equal(observation.Before.Comparisons, observation.After.Comparisons);
        Assert.Equal(observation.Before.Suppressed, observation.After.Suppressed);
        Assert.Equal(observation.Before.Assumed, observation.After.Assumed);
        Assert.Equal(observation.Before.Skips, observation.After.Skips.Where(pair => pair.Key != InterproceduralPairing.SKIP_LIBRARY_INTERNAL)
                                                                    .ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}
