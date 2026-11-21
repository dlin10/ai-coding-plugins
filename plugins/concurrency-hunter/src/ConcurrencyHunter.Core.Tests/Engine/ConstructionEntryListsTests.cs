using ConcurrencyHunter.Execution;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ConstructionEntryListsTests
{
    /// <summary>A construction entry is one object, with the executions of its region, and each of those executions lists that object
    /// among its own entries in canonical order through <see cref="ExecutionAnalysis.Entries"/>; an execution with nothing but
    /// construction entries is listed too, and no list is kept (ADR 0039).</summary>
    [Fact]
    public void Construction_entry_is_one_object_every_execution_of_its_region_lists()
    {
        var ids = new ExecutionIds();
        var (first, second, lazy) = (ids.Number("root:first"), ids.Number("root:second"), ids.Number("construction:part"));
        ids.Number("spawn:other");
        var firstRoot = new ExecutionEntry("first", ExecutionEntryKind.Root, null);
        var initializer = new ExecutionEntry("cctor", ExecutionEntryKind.TypeInitializer, "static:Part");
        var secondRoot = new ExecutionEntry("second", ExecutionEntryKind.Root, null);
        var spawn = new ExecutionEntry("work", ExecutionEntryKind.Spawn, null);
        var part = new ExecutionModel.ConstructionEntry(new ExecutionEntry("part-ctor", ExecutionEntryKind.Construction, "di|Part"),
                                                        Bits.Of([first, second, lazy]));
        var holder = new ExecutionModel.ConstructionEntry(new ExecutionEntry("holder-ctor", ExecutionEntryKind.Construction, "di|Thing"),
                                                          Bits.Of([second]));
        var own = new Dictionary<string, List<ExecutionEntry>>(StringComparer.Ordinal)
        {
            ["root:first"] = [firstRoot, initializer],
            ["root:second"] = [secondRoot],
            ["spawn:other"] = [spawn]
        };

        var entries = ExecutionModel.EntryLists(own, [part, holder], ids);

        Assert.Equal(["construction:part", "root:first", "root:second", "spawn:other"], entries.Keys);
        Assert.Equal([firstRoot, part.Entry, initializer], entries["root:first"]);
        Assert.Equal([secondRoot, part.Entry, holder.Entry], entries["root:second"]);
        Assert.Equal([part.Entry], entries["construction:part"]);
        Assert.Equal([spawn], entries["spawn:other"]);
        Assert.All(["root:first", "root:second", "construction:part"], execution => Assert.Same(part.Entry, entries[execution].Single(entry => entry.IntervalObject == "di|Part")));
        Assert.NotSame(entries["root:second"], entries["root:second"]);
        Assert.False(entries.ContainsKey("root:missing"));
    }
}
