using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The receiver seed access matrix (R7): one compiled fixture, one setter per channel/origin/region cell, each access made on
/// the <c>Leaf</c> below <c>Child</c>. The receiver's <c>_item</c> holds what its constructor was given and the seed setup placed there;
/// a setter that also keeps <c>value</c> there makes the heap, which does not follow order, put what setup stored below <c>_item</c>
/// below <c>value</c> too, so the region the access reaches is shared. An access with a path of its own — a witness's is the value the
/// call that entered it hands it — is the access of the input its path starts at, so a shared region changes no answer. An access whose
/// path does not say — a parameter of a helper body the receiver's seed was handed to, an alternative from each side — is decided by the
/// region, and a region the receiver reaches past no argument's own object stays the receiver's: its read of what it held stays.</summary>
/// <param name="cache">The class fixture that keeps the matrix's answers for every test of the class.</param>
public sealed class ReceiverSeedAccessMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private Matrix Results => cache.Get("results", Run);

    /// <summary>How the setter accesses the region.</summary>
    public enum Channel
    {
        Load,
        Store,
        KnownCallDeepRead,
        KnownCallEnumerate,
        Witness
    }

    /// <summary>Where the access's path starts.</summary>
    public enum Origin
    {
        FromReceiver,
        FromArgument,
        // The receiver's _item through a local alias.
        ThroughLocal,
        // The receiver's _item handed to a helper body, which accesses its parameter.
        ThroughHelper,
        // _item ?? value: one alternative from each side.
        Joined
    }

    /// <summary>Who reaches the accessed region.</summary>
    public enum Region
    {
        OneSide,
        Shared
    }

    /// <summary>One matrix cell.</summary>
    /// <param name="Channel">How the setter accesses the region.</param>
    /// <param name="Origin">Where the access's path starts.</param>
    /// <param name="Region">Who reaches the accessed region.</param>
    public sealed record Cell(Channel Channel, Origin Origin, Region Region)
    {
        public string Name => $"{Channel}/{Origin}/{Region}";

        public string Class => $"C_{Channel}_{Origin}_{Region}";

        public override string ToString() => Name;
    }

    /// <summary>The generated answers of all cells.</summary>
    /// <param name="Answers">The answer by cell.</param>
    private sealed record Matrix(IReadOnlyDictionary<Cell, GeneratedAnswer> Answers);

    private const string PRELUDE = """
        using System;
        using System.Collections.Generic;

        namespace Lib
        {
            public class Leaf { public object Value; public List<object> Items; }
            public class Item { public Leaf Child; }

        """;

    [Fact]
    public void A_shared_region_leaves_each_access_to_its_own_path()
    {
        var failures = new List<string>();
        foreach (var cell in Results.Answers.Keys.Where(cell => cell.Region == Region.Shared && PathSays(cell.Origin))
                                    .OrderBy(cell => cell.Name, StringComparer.Ordinal))
        {
            var twin = cell with { Region = Region.OneSide };
            var shared = Shape(Results.Answers[cell]);
            var expected = Shape(Results.Answers[twin], keepsValue: true);
            if (shared != expected)
                failures.Add($"{cell.Name}: {shared}; {twin.Name}: {expected}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} shared cell(s) answer apart from their twin:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Results.Answers.Keys.ToArray();

        Assert.All(Enum.GetValues<Channel>(), channel => Assert.Contains(cells, cell => cell.Channel == channel));
        Assert.All(Enum.GetValues<Origin>(), origin => Assert.Contains(cells, cell => cell.Origin == origin));
        Assert.All(Enum.GetValues<Region>(), region => Assert.Contains(cells, cell => cell.Region == region));
        Assert.Equal(Cells().Select(cell => cell.Name).Order(StringComparer.Ordinal), cells.Select(cell => cell.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_cell_narrows_unsafely()
    {
        var failures = Results.Answers.Select(pair => (pair.Key, Comparison: EntryComparator.Compare(pair.Value, Expected(pair.Key))))
                              .Where(pair => pair.Comparison.IsUnsafeNarrowing)
                              .Select(pair => $"{pair.Key.Name}: {pair.Comparison.Detail}; got {Shape(Results.Answers[pair.Key])}")
                              .Order(StringComparer.Ordinal).ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} unsafe narrowing(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void A_read_of_the_receivers_seed_whose_path_does_not_say_stays_a_read_of_this()
    {
        var failures = new List<string>();
        foreach (var cell in Results.Answers.Keys.Where(cell => !PathSays(cell.Origin) && cell.Channel != Channel.Store)
                                    .OrderBy(cell => cell.Name, StringComparer.Ordinal))
        {
            var answer = Results.Answers[cell];
            if (answer.Model?.Effects.Contains(LibraryEffect.DeepReadOf("this")) != true)
                failures.Add($"{cell.Name}: {Shape(answer)}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cell(s) lost the receiver's read:\n" + string.Join("\n", failures));
    }

    /// <summary>Whether an access from an origin has a path that says whose it is (R7): one that starts at the receiver's seeded field,
    /// directly or through a local, or at the argument. A helper's parameter, whose binding the path does not trace, and a join of the
    /// two sides do not say.</summary>
    /// <param name="origin">Where the access's path starts.</param>
    private static bool PathSays(Origin origin) => origin is Origin.FromReceiver or Origin.FromArgument or Origin.ThroughLocal;

    /// <summary>What no answer may claim less than (R7): a read through the argument reads the argument deep, a write into what it holds
    /// has no form; a read of a user object the receiver held before the call is a deep read of this, a write into it has no form; a
    /// join of the two reads both; and a setter that keeps <c>value</c> keeps it on this.</summary>
    /// <param name="cell">The cell.</param>
    private static EntryTruth Expected(Cell cell)
    {
        if (cell.Channel == Channel.Store)
            return EntryTruth.NoEntry(ModelReasons.VOCABULARY);
        var effects = new List<LibraryEffect>();
        if (cell.Origin != Origin.FromArgument)
            effects.Add(LibraryEffect.DeepReadOf("this"));
        if (cell.Origin is Origin.FromArgument or Origin.Joined)
            effects.Add(LibraryEffect.DeepReadOf("value"));
        var keeps = new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        if (cell.Region == Region.Shared)
            keeps["this"] = [LibraryValue.Parse("arg:value")!];
        return EntryTruth.Entry(new LibraryModel("M:Lib.Cell.set_P", [], effects) { Keeps = keeps });
    }

    /// <summary>An answer without the member it is of, so two cells' answers compare.</summary>
    /// <param name="answer">The generated answer.</param>
    /// <param name="keepsValue">Whether to add what a shared cell's own <c>_item = value</c> keeps: <c>arg:value</c> on this.</param>
    private static string Shape(GeneratedAnswer answer, bool keepsValue = false)
    {
        if (answer.Model is not { } model)
            return $"no entry: {answer.ModelReason ?? answer.Reason}";
        if (keepsValue)
            model = model with { Keeps = new Dictionary<string, IReadOnlyList<LibraryValue>>(model.Keeps, StringComparer.Ordinal) { ["this"] = [LibraryValue.Parse("arg:value")!] } };
        var entry = ModelEntryWriter.Write(model);
        entry.Remove("member");
        return entry.ToJsonString();
    }

    private static IEnumerable<Cell> Cells() =>
        from channel in Enum.GetValues<Channel>()
        from origin in Enum.GetValues<Origin>()
        from region in Enum.GetValues<Region>()
        select new Cell(channel, origin, region);

    /// <summary>Compiles the fixture once and generates every cell in parallel, as the model generator is used per member.</summary>
    private static Matrix Run()
    {
        var cells = Cells().ToArray();
        var library = Compile(Source(cells));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Equal(0, library.ExternBodies);
        var answers = new ConcurrentDictionary<Cell, GeneratedAnswer>();
        Parallel.ForEach(cells, CellParallelism, cell =>
        {
            var setter = library.Compilation!.GetTypeByMetadataName($"Lib.{cell.Class}")!.GetMembers("set_P").OfType<IMethodSymbol>().Single();
            var request = new GenerationRequest(ASSEMBLY, "1.0", setter.GetDocumentationCommentId()!, null, null);
            answers[cell] = ModelGenerator.Trace(request, library, CancellationToken.None).Answer;
        });
        return new Matrix(answers);
    }

    private static string Source(IEnumerable<Cell> cells)
    {
        var source = new StringBuilder(PRELUDE);
        foreach (var cell in cells)
        {
            source.Append("    public sealed class ").Append(cell.Class).Append("\n    {\n")
                  .Append("        private Item _item;\n")
                  .Append("        public ").Append(cell.Class).Append("(Item item) { _item = item; }\n")
                  .Append("        public Item P { set { ").Append(Body(cell)).Append(" } }\n");
            if (cell.Origin == Origin.ThroughHelper)
                source.Append("        private static void Use(Item item) { ").Append(Access(cell.Channel, "item")).Append(" }\n");
            source.Append("    }\n");
        }
        return source.Append("}\n").ToString();
    }

    /// <summary>The setter's body: the access on the path from <c>_item</c>, from <c>value</c>, from a local that holds <c>_item</c> or
    /// <c>_item ?? value</c>, or in a helper handed <c>_item</c>; and for a shared region the store of <c>value</c> into <c>_item</c> —
    /// after an access that reaches what <c>_item</c> held before, before a read of what the setter was handed.</summary>
    /// <param name="cell">The cell.</param>
    private static string Body(Cell cell)
    {
        var access = cell.Origin switch
        {
            Origin.FromReceiver => Access(cell.Channel, "_item"),
            Origin.FromArgument => Access(cell.Channel, "value"),
            Origin.ThroughLocal => $"var item = _item; {Access(cell.Channel, "item")}",
            Origin.ThroughHelper => "Use(_item);",
            Origin.Joined => $"var item = _item ?? value; {Access(cell.Channel, "item")}",
            _ => throw new InvalidOperationException($"Unknown origin {cell.Origin}.")
        };
        if (cell.Region == Region.OneSide)
            return access;
        return cell.Origin == Origin.FromArgument ? $"_item = value; {access}" : $"{access} _item = value;";
    }

    /// <summary>The access a channel makes on the <c>Leaf</c> below <paramref name="start"/>'s <c>Child</c>.</summary>
    /// <param name="channel">How the access is made.</param>
    /// <param name="start">The expression the path starts at.</param>
    private static string Access(Channel channel, string start) => channel switch
    {
        Channel.Load => $"_ = {start}.Child.Value;",
        Channel.Store => $"{start}.Child.Value = null;",
        Channel.KnownCallDeepRead => $"_ = Array.IndexOf<object>(new object[0], {start}.Child);",
        Channel.KnownCallEnumerate => $"foreach (var element in {start}.Child.Items) {{ }}",
        Channel.Witness => $"_ = {start}.Child.ToString();",
        _ => throw new InvalidOperationException($"Unknown channel {channel}.")
    };
}
