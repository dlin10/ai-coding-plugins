using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A restore removes the keys a collection gained since its snapshot and no others; one that comes after the restore of an
/// earlier snapshot throws.</summary>
public sealed class KeyJournalTests
{
    [Fact]
    public void Nested_restores_leave_the_keys_each_snapshot_saw()
    {
        var set = new JournaledSet();
        set.Add("a");
        var outer = set.Snapshot();
        set.Add("b");
        var inner = set.Snapshot();
        set.Add("c");
        set.Add("a");
        set.Add("d");

        inner();
        Assert.Equal(["a", "b"], set.Keys);
        set.Add("e");
        outer();
        Assert.Equal(["a"], set.Keys);
    }

    [Fact]
    public void A_restore_after_the_restore_of_an_earlier_snapshot_throws()
    {
        // The earlier restore removed b, which the later snapshot holds, whether or not the set gained keys again since.
        // bounded: the three counts.
        foreach (var regained in new[] { 0, 1, 3 })
        {
            var set = new JournaledSet();
            var earlier = set.Snapshot();
            set.Add("b");
            var later = set.Snapshot();
            earlier();
            // bounded: at most three keys.
            for (var index = 0; index < regained; index++)
                set.Add($"x{index}");

            Assert.Throws<InvalidOperationException>(later);
        }
    }

    /// <summary>A set that records the keys it gains, as the driver writer's names and unseeded paths do.</summary>
    private sealed class JournaledSet
    {
        private readonly DriverSynthesizer.KeyJournal _journal = new();

        public SortedSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public void Add(string key)
        {
            if (Keys.Add(key))
                _journal.Gained(key);
        }

        public Action Snapshot() => _journal.Snapshot(Keys.Remove);
    }
}
