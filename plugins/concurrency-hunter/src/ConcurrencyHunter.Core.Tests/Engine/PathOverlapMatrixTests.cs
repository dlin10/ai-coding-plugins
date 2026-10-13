using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// The whole of a struct's storage meets each part of it (R3): storage A × storage B × base relation, through the candidate index and
/// its reference. A field storage stands on a region, a cell storage on a collection; two meet when their bases meet and either chain of
/// struct members is a prefix of the other, cells compared by selector overlap within one collection and by selector text across an
/// alias group; a collection's structure meets only itself and the wildcard (ADR 0010), and the wildcard meets as TD-042 says. A pair of
/// two lengths is named by the longer path, a pair of one length and a pair with the wildcard as before.
/// </summary>
public sealed class PathOverlapMatrixTests
{
    public enum Storage
    {
        /// <summary><c>[s]</c>: the whole struct a field holds.</summary>
        Whole,

        /// <summary><c>[s, X]</c>.</summary>
        Field,

        /// <summary><c>[s, Inner, Y]</c>.</summary>
        Nested,

        /// <summary><c>[s, Z]</c>.</summary>
        Sibling,

        /// <summary><c>[t]</c>: a scalar field.</summary>
        Scalar,

        /// <summary><c>[a, [0]]</c>: a struct cell's whole storage.</summary>
        CellWhole,

        /// <summary><c>[a, [0], X]</c>.</summary>
        CellField,

        /// <summary><c>[a, [1], X]</c>.</summary>
        OtherCellField,

        /// <summary><c>[a, [?], X]</c>.</summary>
        UnknownCellField,

        /// <summary><c>[a]</c>: the collection's structure.</summary>
        Structure,

        /// <summary><c>["*"]</c>: the region's wildcard.</summary>
        Wildcard,

        /// <summary><c>[]</c>: a box's whole storage.</summary>
        Box
    }

    public enum Relation
    {
        OneRegion,
        AliasGroup,
        UnrelatedRegions,
        OneCollectionOneField,
        OneCollectionTwoFields,
        TwoCollections,
        AliasGroupCollections,
        RegionHoldsCollection,
        RegionAndUnrelatedCollection
    }

    /// <summary>Which side names a meeting pair: A, B, or either where the two are one resource.</summary>
    private enum Side
    {
        A,
        B,
        Either
    }

    public sealed record Cell(Storage A, Storage B, Relation Relation);

    private sealed record Expectation(bool Meets, Side Named);

    private const string SCOPE = "scope:Fixture";
    private const string EXECUTION = "root:a";
    private const string HOLDER = "Fixture.Holder";
    private const string STRUCT = "Fixture.S";
    private const string INNER = "Fixture.Inner";
    private const string GROUP = "static|Fixture:Holder";

    /// <summary>Cells the engine meets though the rule says they do not, each a known over-approximation with its reason.</summary>
    private static readonly Cell[] KnownWider = [];

    private static IEnumerable<Cell> Cells() =>
        from relation in Enum.GetValues<Relation>()
        from a in Enum.GetValues<Storage>()
        from b in Enum.GetValues<Storage>()
        let cell = new Cell(a, b, relation)
        where Allowed(cell)
        select cell;

    private static bool IsCollection(Storage storage) =>
        storage is Storage.CellWhole or Storage.CellField or Storage.OtherCellField or Storage.UnknownCellField or Storage.Structure;

    private static bool Allowed(Cell cell) => cell.Relation switch
    {
        Relation.OneRegion or Relation.AliasGroup or Relation.UnrelatedRegions => !IsCollection(cell.A) && !IsCollection(cell.B),
        Relation.OneCollectionOneField or Relation.OneCollectionTwoFields or Relation.TwoCollections or Relation.AliasGroupCollections =>
            IsCollection(cell.A) && IsCollection(cell.B),
        // A field storage and a box live in another object than a collection's cells; only the wildcard reaches them.
        _ => IsCollection(cell.A) != IsCollection(cell.B) && (cell.A == Storage.Wildcard || cell.B == Storage.Wildcard)
    };

    /// <summary>The struct members below a storage's base, outermost first.</summary>
    /// <param name="storage">The storage.</param>
    private static string[] Chain(Storage storage) => storage switch
    {
        Storage.Whole => ["s"],
        Storage.Field => ["s", "X"],
        Storage.Nested => ["s", "Inner", "Y"],
        Storage.Sibling => ["s", "Z"],
        Storage.Scalar => ["t"],
        Storage.CellField or Storage.OtherCellField or Storage.UnknownCellField => ["X"],
        _ => []
    };

    private static ElementSelector? Selector(Storage storage) => storage switch
    {
        Storage.CellWhole or Storage.CellField => ElementSelector.Exact(0),
        Storage.OtherCellField => ElementSelector.Exact(1),
        Storage.UnknownCellField => ElementSelector.Unknown,
        _ => null
    };

    private static bool IsPrefix(string[] shorter, string[] longer) => shorter.Length <= longer.Length && longer.Take(shorter.Length).SequenceEqual(shorter);

    private static Expectation None { get; } = new(false, Side.Either);

    private static Expectation Expected(Cell cell)
    {
        var (a, b) = (cell.A, cell.B);
        var (chainA, chainB) = (Chain(a), Chain(b));
        var continues = IsPrefix(chainA, chainB) || IsPrefix(chainB, chainA);
        Side Longer() => chainA.Length > chainB.Length ? Side.A : Side.B;
        switch (cell.Relation)
        {
            case Relation.UnrelatedRegions or Relation.TwoCollections or Relation.RegionAndUnrelatedCollection:
                return None;
            case Relation.OneRegion:
                if (a == Storage.Wildcard || b == Storage.Wildcard)
                    return new(true, a == b ? Side.Either : a == Storage.Wildcard ? Side.A : Side.B);
                return !continues ? None : new(true, chainA.Length == chainB.Length ? Side.Either : Longer());
            case Relation.AliasGroup:
                // A is on the open region, B on the closed one.
                if (a == Storage.Wildcard || b == Storage.Wildcard)
                    return new(true, Side.B);
                return !continues ? None : new(true, chainA.Length == chainB.Length ? Side.B : Longer());
            case Relation.RegionHoldsCollection:
                return new(true, a == Storage.Wildcard ? Side.A : Side.B);
            case Relation.OneCollectionOneField or Relation.OneCollectionTwoFields:
                if (a == Storage.Structure || b == Storage.Structure)
                    return a == b ? new(true, Side.Either) : None;
                if (!Selector(a)!.MayOverlap(Selector(b)!) || !continues)
                    return None;
                if (chainA.Length != chainB.Length)
                    return new(true, Longer());
                return Selector(a)!.Text == Selector(b)!.Text ? new(true, Side.Either)
                    : Selector(a)!.IsProven != Selector(b)!.IsProven ? new(true, Selector(a)!.IsProven ? Side.A : Side.B)
                    : new(true, string.CompareOrdinal(Selector(b)!.Text, Selector(a)!.Text) < 0 ? Side.B : Side.A);
            case Relation.AliasGroupCollections:
                // A is in the open region's collection, B in the closed region's: the same field, the same selector text.
                if (a == Storage.Structure || b == Storage.Structure)
                    return a == b ? new(true, Side.B) : None;
                if (Selector(a)!.Text != Selector(b)!.Text || !continues)
                    return None;
                return new(true, chainA.Length == chainB.Length ? Side.B : Longer());
            default:
                throw new ArgumentOutOfRangeException(nameof(cell));
        }
    }

    [Fact]
    public void Every_cell_meets_as_expected()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells())
        {
            var (heap, a, b) = Place(cell);
            var expected = Expected(cell).Meets;
            var indexed = IndexPair(heap, a, b) is not null;
            var reference = ReferenceAdmits(a, b, heap) is not null;
            if (indexed != reference)
                wrong.Add($"{cell}: index {indexed}, reference {reference}");
            else if (indexed != expected && !(indexed && KnownWider.Contains(cell)))
                wrong.Add($"{cell}: expected {expected}, got {indexed}");
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_meeting_pair_is_named_as_expected()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells().Where(cell => Expected(cell).Meets))
        {
            var (heap, a, b) = Place(cell);
            var named = Expected(cell).Named;
            var indexed = IndexPair(heap, a, b)?.Reported;
            var reference = ReferenceAdmits(a, b, heap)?.Reported;
            foreach (var (source, reported) in new[] { ("index", indexed), ("reference", reference) })
            {
                if (!Named(reported, a, b, named))
                    wrong.Add($"{cell}: {source} named {(reported is null ? "nothing" : string.Join(", ", reported.AccessPath))}, expected {named}");
            }
        }

        Assert.Empty(wrong);
    }

    /// <summary>Every storage of a relation on its two bases at once, through one index: each expected meeting is enumerated exactly
    /// once among all the others, none is missed for another bucket and none is added.</summary>
    [Fact]
    public void No_cell_misses_a_meeting()
    {
        var wrong = new List<string>();
        foreach (var relation in Enum.GetValues<Relation>())
        {
            var cells = Cells().Where(cell => cell.Relation == relation).ToArray();
            var heap = Heap();
            var sideA = cells.Select(cell => cell.A).Distinct().Select(storage => (Storage: storage, Access: Write(Resource(storage, relation, sideA: true)))).ToArray();
            var sideB = cells.Select(cell => cell.B).Distinct().Select(storage => (Storage: storage, Access: Write(Resource(storage, relation, sideA: false)))).ToArray();
            var accesses = sideA.Select(item => item.Access).Concat(sideB.Select(item => item.Access)).ToArray();
            var pairs = CandidateIndex.Build(accesses, heap).Pairs().ToArray();
            var keys = pairs.Select(pair => Key(Position(accesses, pair.First), Position(accesses, pair.Second))).ToList();
            if (keys.Distinct().Count() != keys.Count)
                wrong.Add($"{relation}: a pair is enumerated twice");

            foreach (var cell in cells)
            {
                var first = Position(accesses, sideA.Single(item => item.Storage == cell.A).Access);
                var second = Position(accesses, sideB.Single(item => item.Storage == cell.B).Access);
                var met = keys.Contains(Key(first, second));
                if (met != Expected(cell).Meets && !(met && KnownWider.Contains(cell)))
                    wrong.Add($"{cell}: expected {Expected(cell).Meets}, got {met} among {accesses.Length} accesses");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(Enum.GetValues<Storage>().Order(), cells.Select(cell => cell.A).Distinct().Order());
        Assert.Equal(Enum.GetValues<Storage>().Order(), cells.Select(cell => cell.B).Distinct().Order());
        Assert.Equal(Enum.GetValues<Relation>().Order(), cells.Select(cell => cell.Relation).Distinct().Order());
        Assert.Contains(cells, cell => Expected(cell).Meets);
        Assert.Contains(cells, cell => !Expected(cell).Meets);
        Assert.Contains(cells, cell => Expected(cell) is { Meets: true, Named: Side.A });
        Assert.Contains(cells, cell => Expected(cell) is { Meets: true, Named: Side.B });
        Assert.Empty(KnownWider);
    }

    private static bool Named(AccessResource? reported, Access a, Access b, Side side) => reported is not null && side switch
    {
        Side.A => ReferenceEquals(reported, a.Resource),
        Side.B => ReferenceEquals(reported, b.Resource),
        _ => reported.Identity == a.Resource.Identity && reported.Identity == b.Resource.Identity
    };

    private static CandidatePair? IndexPair(HeapSolution heap, Access a, Access b)
    {
        var pairs = CandidateIndex.Build([a, b], heap).Pairs()
                                  .Where(pair => ReferenceEquals(pair.First, a) && ReferenceEquals(pair.Second, b) ||
                                                 ReferenceEquals(pair.First, b) && ReferenceEquals(pair.Second, a))
                                  .ToArray();
        return pairs.Length switch
        {
            0 => null,
            1 => pairs[0],
            _ => throw new InvalidOperationException("The pair is enumerated twice.")
        };
    }

    private static (HeapSolution Heap, Access A, Access B) Place(Cell cell) =>
        (Heap(), Write(Resource(cell.A, cell.Relation, sideA: true)), Write(Resource(cell.B, cell.Relation, sideA: false)));

    /// <summary>A storage on the base its side of a relation gives it. Regions: <c>R</c>, <c>U</c> unrelated to it, and <c>O</c> open and
    /// <c>C</c> closed in one alias group. Collections: <c>c</c> held by <c>R</c>'s field <c>a</c> (and by <c>U</c>'s field <c>b</c>),
    /// <c>d</c> held by <c>R</c>'s field <c>b</c> or by <c>U</c>'s field <c>a</c>, <c>o</c> and <c>k</c> held by the field <c>a</c> of
    /// <c>O</c> and of <c>C</c>.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="relation">The relation between the two sides' bases.</param>
    /// <param name="sideA">Whether the storage is side A's of the relation, rather than side B's.</param>
    private static AccessResource Resource(Storage storage, Relation relation, bool sideA)
    {
        if (!IsCollection(storage))
        {
            var region = relation switch
            {
                Relation.AliasGroup => sideA ? "O" : "C",
                Relation.UnrelatedRegions => sideA ? "R" : "U",
                _ => "R"
            };
            return FieldStorage(storage, region);
        }

        var (holder, field, collection) = relation switch
        {
            Relation.OneCollectionTwoFields => sideA ? ("R", "a", "c") : ("U", "b", "c"),
            Relation.TwoCollections => sideA ? ("R", "a", "c") : ("R", "b", "d"),
            Relation.AliasGroupCollections => sideA ? ("O", "a", "o") : ("C", "a", "k"),
            Relation.RegionAndUnrelatedCollection => ("U", "a", "d"),
            _ => ("R", "a", "c")
        };
        return CollectionStorage(storage, holder, field, collection);
    }

    private static AccessResource FieldStorage(Storage storage, string region)
    {
        if (storage == Storage.Wildcard)
        {
            return new AccessResource("Fixture", SCOPE, region, [PathValue.WILDCARD], new MemberKey(PathValue.WILDCARD, PathValue.WILDCARD, IrFieldKind.Field),
                                      region, true);
        }

        if (storage == Storage.Box)
            return new AccessResource("Fixture", SCOPE, region, [], new MemberKey(STRUCT, "", IrFieldKind.Field), region);

        var chain = Chain(storage);
        return new AccessResource("Fixture", SCOPE, region, chain, new MemberKey(HOLDER, chain[0], IrFieldKind.Field), region)
        {
            StructPath = Below(chain.Skip(1))
        };
    }

    private static AccessResource CollectionStorage(Storage storage, string holder, string field, string collection)
    {
        var member = new MemberKey(HOLDER, field, IrFieldKind.Field);
        if (storage == Storage.Structure)
            return new AccessResource("Fixture", SCOPE, holder, [field], member, holder) { CollectionId = collection };

        var selector = Selector(storage)!;
        var below = Chain(storage);
        return new AccessResource("Fixture", SCOPE, holder, [field, selector.Text, .. below], member, holder)
        {
            Selector = selector,
            CollectionId = collection,
            StructPath = Below(below)
        };
    }

    /// <summary>The struct fields of a chain below its first member: <c>X</c>, <c>Z</c> and <c>Inner</c> of <c>S</c>, <c>Y</c> of
    /// <c>Inner</c>.</summary>
    /// <param name="names">The chain's members below its first, outermost first.</param>
    private static MemberKey[] Below(IEnumerable<string> names) =>
        names.Select(name => new MemberKey(name == "Y" ? INNER : STRUCT, name, IrFieldKind.Field)).ToArray();

    private static Access Write(AccessResource resource) =>
        new(resource, AccessOperation.Write, ROOT, "S.Write", SPAN, [], [], [], [], []) { ExecutionId = EXECUTION };

    private static (int, int) Key(int first, int second) => first <= second ? (first, second) : (second, first);

    private static int Position(IReadOnlyList<Access> accesses, Access access)
    {
        for (var index = 0; index < accesses.Count; index++)
        {
            if (ReferenceEquals(accesses[index], access))
                return index;
        }

        throw new InvalidOperationException("The access is not in the fixture.");
    }

    private static readonly InvocationPolicy POLICY = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, SCOPE);
    private static readonly AccessRoot ROOT = new(EXECUTION, "S.Root", "root", "test", "test", POLICY, SCOPE);
    private static readonly Analysis.SourceSpan SPAN = new("Case.cs", 1, 1, 1, 2);

    private static HeapRegion Static(string identity, string? group = null, bool isOpen = false) =>
        new(identity, HeapRegionKind.Static, identity, null, "", group ?? identity, isOpen, false);

    private static HeapSolution Heap()
    {
        HeapRegion[] regions = [Static("R"), Static("U"), Static("O", GROUP, isOpen: true), Static("C", GROUP)];
        return new(regions.ToDictionary(region => region.Identity, StringComparer.Ordinal), new Dictionary<string, MethodInstance>(), [], [], [],
                   new Dictionary<string, int>(), new HashSet<string>(), [], [], (_, _) => new HashSet<string>(), (_, _) => new HashSet<string>(),
                   new Dictionary<string, string>(), (_, _) => "", _ => [], _ => new HashSet<string>());
    }
}
