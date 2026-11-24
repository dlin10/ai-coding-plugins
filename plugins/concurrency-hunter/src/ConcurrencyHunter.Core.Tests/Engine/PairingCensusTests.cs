using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The census of pairing (issue 492): what a scope's pairing will enumerate and ask, counted before it asks anything, in three
/// parts — the index, linear and exact, then the pairs and the searches under a budget of work. Hand-made scopes pin its lines with
/// numbers worked out by hand and its decisions against pairing's own skips; on the demo and eShop it equals a pair-by-pair count of
/// the same pairs, and it bounds what pairing then checks and finds.</summary>
public sealed class PairingCensusTests
{
    private const string SCOPE = "scope:Fixture";
    private const string STARTUP = "startup";
    private const string DELEGATE = "unknown:delegate";
    private const string ROOT_A = "root:a";
    private const string SPAWN_A = "spawn:a";
    private const string ROOT_B = "root:b";

    private static readonly InvocationPolicy POLICY = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, SCOPE);
    private static readonly SourceSpan SPAN = new("Case.cs", 1, 1, 1, 2);
    private static readonly Regex PROGRESS = new(@"considered=(\d+) comparisons=(\d+) executionPairs=(\d+) candidates=\d+ witnesses=\d+", RegexOptions.Compiled);
    private static readonly AccessRoot ROOT_OF_A = Root(ROOT_A);
    private static readonly AccessRoot ROOT_OF_B = Root(ROOT_B);
    private static readonly AccessRoot ROOT_OF_STARTUP = Root(STARTUP);
    private static readonly AccessRoot ROOT_OF_DELEGATE = Root(DELEGATE);

    /// <summary>One bucket: a write made by a root, a task it spawns, another root and a delegate startup hands over; a read made by the
    /// first root and startup; a read made by the second root and the task — each execution of its own variant. Every number is worked
    /// out by hand, and the lines say each under its name: six pairs, three of two reads; of the three searched, 26 pairs of executions
    /// (7 of one execution, 3 of one tree, 9 with one side starting during startup, 1 with both, 6 of other trees) and 26 of variants
    /// (10, 9, 1, 6), 42 searches, since the write is searched against the reads' other resource objects in both orientations, and 30
    /// pairs of path roots. The two reads are one class, so the counts the searches made hold 11 entries for the write and 12 for the
    /// reads, 23 at once.</summary>
    [Fact]
    public void A_bucket_of_a_write_and_two_reads_writes_each_count_under_its_name()
    {
        Access[] accesses =
        [
            Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (SPAWN_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B), (DELEGATE, ROOT_OF_DELEGATE)),
            Make("a", "X", AccessOperation.Read, (ROOT_A, ROOT_OF_A), (STARTUP, ROOT_OF_STARTUP)),
            Make("a", "X", AccessOperation.Read, (ROOT_B, ROOT_OF_B), (SPAWN_A, ROOT_OF_A))
        ];

        var census = Census(accesses, Heap(Static("a")));

        Assert.Equal(
        [
            "buckets=1 resourceBuckets=1 wildcardRegions=0 wildcardAccesses=0 accesses=3 accessExecutions=8 duplicateAccesses=0 largest=3 " +
            "sizes=1:0,2-9:1,10-99:0,100-999:0,1000-9999:0,10000+:0",
            "withinBuckets resource=6 wildcardWithinBeforeDrops=0 wildcardAcrossBeforeDrops=0",
            "candidates mostConditions=0 mostHeldObjects=0"
        ], census.IndexLines().Take(3));
        Assert.Equal("pairs complete=true total=6 resource=6 cells=0 fields=0 wildcardWithin=0 wildcardAcross=0 openRegion=0 blocks=1 " +
                     "rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0 bucketPairsTested=0 steps=1", census.PairsLines()[0]);
        Assert.Equal(
        [
            $"coverage complete=true stopped=none blocks=1/1 pairs=6/6 steps={census.Steps} budget={PairingCensus.LIMITS.Budget} executionsRead=8 " +
            "countEntriesPeak=23 decisionClears=0",
            "decided over=all noCheckReadRead=3 noCheckNoConflictingOperation=0 noCheckNoOverlap=0 oneCheckConfined=0 " +
            "oneCheckUnsatisfiablePath=0 oneCheckFreshObject=0 searchOnce=0 search=3",
            "executionPairs over=all search=26 sameExecution=7 sameTree=3 oneStartup=9 bothStartup=1 otherTrees=6 searchOnce=0",
            "variantPairs over=all search=26 sameTree=10 oneStartup=9 bothStartup=1 otherTrees=6 acrossTrees=0",
            "bounds over=all executionPairChecksAtLeast=3 executionPairChecksAtMost=26 witnessesAtMost=42 candidatesAtMost=30",
            "searches over=all search=42 searchOnceAtLeast=0 searchOnceAtMost=0"
        ], census.Lines().Take(6));
        Assert.Equal(Reference(accesses, Executions(), Heap(Static("a")), DuringStartup), Describe(census));
    }

    /// <summary>A bucket of each decision pairing makes before its questions, each with its own count — three pairs of two reads, six of
    /// no conflicting operation, ten on a region confined to one thread, one of contradicting conditions, fifteen of fresh objects,
    /// twenty-six whose search one witness ends (twenty-one under one lock, two of a write under another lock with itself and three of
    /// two library-internal writes) and three searched (two of the conditioned writes with themselves and the two writes under two
    /// locks) — which are pairing's own skips and suppressions on the same accesses; every access makes one check of each pair it is in,
    /// so the two bounds on the checks meet at 55, which is what pairing checks. The searches are 4 of the searched pairs — one for each
    /// conditioned write with itself and two for the two locks, two resource objects — and 26 to 42 of those one witness ends: 36 for
    /// the six writes under one lock (fifteen pairs of two objects twice and six writes once), one for each of the two other locked
    /// writes and four for the two library-internal ones.</summary>
    [Fact]
    public void Each_decision_before_the_questions_counts_what_pairing_skips_for_it()
    {
        Access[] accesses =
        [
            .. Enumerable.Range(0, 2).Select(_ => Make("a", "R", AccessOperation.Read, (ROOT_A, ROOT_OF_A))),
            .. Enumerable.Range(0, 3).Select(_ => Make("a", "N", AccessOperation.AtomicWrite, (ROOT_A, ROOT_OF_A))),
            .. Enumerable.Range(0, 4).Select(_ => Make("c", "C", AccessOperation.Write, (ROOT_A, ROOT_OF_A))),
            .. new[] { "1", "2" }.Select(value => Make("a", "U", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
            {
                Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}v", PathRelation.Equal, value, $"v == {value}")]
            }),
            .. Enumerable.Range(0, 5).Select(_ => Make("a", "F", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with { IsFresh = true }),
            .. Enumerable.Range(0, 6).Select(_ => Locked("L", "m")),
            Locked("M", "m"),
            Locked("M", "n"),
            .. Enumerable.Range(0, 2).Select(_ => Make("a", "I", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with { IsLibraryInternal = true })
        ];
        var ownership = new Dictionary<string, RegionOwnership>(StringComparer.Ordinal) { ["c"] = new(OwnershipKind.ThreadConfined, []) };
        var heap = Heap(Static("a"), Static("c"));

        var census = Census(accesses, heap, ownership);
        var run = PairWithProgress(accesses, Executions(ownership), heap);

        Assert.Equal("buckets=8 resourceBuckets=8 wildcardRegions=0 wildcardAccesses=0 accesses=26 accessExecutions=26 duplicateAccesses=0 " +
                     "largest=6,5,4,3,2 sizes=1:0,2-9:8,10-99:0,100-999:0,1000-9999:0,10000+:0", census.IndexLines()[0]);
        Assert.Equal("candidates mostConditions=1 mostHeldObjects=1", census.IndexLines()[2]);
        Assert.Equal("searches over=all search=4 searchOnceAtLeast=26 searchOnceAtMost=42", census.Lines()[5]);
        Assert.Equal("decided over=all noCheckReadRead=3 noCheckNoConflictingOperation=6 noCheckNoOverlap=0 oneCheckConfined=10 " +
                     "oneCheckUnsatisfiablePath=1 oneCheckFreshObject=15 searchOnce=26 search=3", census.Lines()[1]);
        Assert.Equal("executionPairs over=all search=3 sameExecution=3 sameTree=0 oneStartup=0 bothStartup=0 otherTrees=0 searchOnce=26",
                     census.Lines()[2]);
        Assert.Equal("bounds over=all executionPairChecksAtLeast=55 executionPairChecksAtMost=55 witnessesAtMost=4 candidatesAtMost=4",
                     census.Lines()[4]);
        Assert.Equal(55L, run.Checks);
        Assert.Equal(3, run.Pairs.Skips[InterproceduralPairing.SKIP_READ_READ]);
        Assert.Equal(6, run.Pairs.Skips[InterproceduralPairing.SKIP_NO_CONFLICTING_OPERATION]);
        Assert.Equal(10, run.Pairs.Skips[InterproceduralPairing.SKIP_CONFINED]);
        Assert.Equal(1, run.Pairs.Skips[InterproceduralPairing.SKIP_UNSATISFIABLE_PATH]);
        Assert.Equal(15, run.Pairs.Skips[InterproceduralPairing.SKIP_FRESH_OBJECT]);
        Assert.Equal(3, run.Pairs.Skips[InterproceduralPairing.SKIP_LIBRARY_INTERNAL]);
        Assert.Equal(23, run.Pairs.Suppressed);
        Assert.InRange(run.Pairs.Pairs.Count, 3, 4);
        Assert.Equal(Reference(accesses, Executions(ownership), heap, DuringStartup), Describe(census));

        static Access Locked(string member, string id) =>
            new(Make("a", member, AccessOperation.Write).Resource, AccessOperation.Write, ROOT_OF_A, "S.M", SPAN, [$"lock {id}"], [id], [], [], [])
            {
                ExecutionId = ROOT_A,
                HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
                {
                    [id] = [new HeldProtection(IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive, true)]
                },
                Executions = [new AccessExecution(ROOT_A, Variant(ROOT_OF_A), 0)]
            };
    }

    /// <summary>A write on an open region and one on a closed region of its alias group confined to one thread: their pair, one side
    /// confined, is decided as confined, as pairing skips it, with the closed write's pair with itself; the open write's pair with itself
    /// is searched.</summary>
    [Fact]
    public void A_pair_with_one_side_confined_is_decided_as_confined()
    {
        Access[] accesses = [Make("o", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), Make("k", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A))];
        var ownership = new Dictionary<string, RegionOwnership>(StringComparer.Ordinal) { ["k"] = new(OwnershipKind.ThreadConfined, []) };
        var heap = Heap(Static("o", "g", isOpen: true), Static("k", "g"));

        var census = Census(accesses, heap, ownership);
        var pairs = InterproceduralPairing.Pair(accesses, Executions(ownership), heap);

        Assert.Equal(1L, census.Pairs(PairBlockKind.OpenRegion));
        Assert.Equal(2L, census.Decided(PairDecision.Confined));
        Assert.Equal(1L, census.Decided(PairDecision.Search));
        Assert.Equal(2, pairs.Skips[InterproceduralPairing.SKIP_CONFINED]);
    }

    /// <summary>A write whose one variant is made by two roots, so that its executions are of two trees, and a write of the first root:
    /// three pairs, two of whose pairs of variants have that variant on a side and count across trees; the pairs of executions still count
    /// by execution and tree.</summary>
    [Fact]
    public void A_variant_whose_executions_are_of_two_trees_is_counted_across_trees()
    {
        var shared = Variant(ROOT_OF_A);
        Access[] accesses =
        [
            Make("a", "X", AccessOperation.Write) with { Executions = [new AccessExecution(ROOT_A, shared, 0), new AccessExecution(ROOT_B, shared, 1)] },
            Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A))
        ];

        var census = Census(accesses, Heap(Static("a")));

        Assert.Equal("executionPairs over=all search=6 sameExecution=4 sameTree=0 oneStartup=0 bothStartup=0 otherTrees=2 searchOnce=0",
                     census.Lines()[2]);
        Assert.Equal("variantPairs over=all search=3 sameTree=1 oneStartup=0 bothStartup=0 otherTrees=0 acrossTrees=2", census.Lines()[3]);
        Assert.Equal("bounds over=all executionPairChecksAtLeast=3 executionPairChecksAtMost=6 witnessesAtMost=4 candidatesAtMost=4",
                     census.Lines()[4]);
        Assert.Equal(Reference(accesses, Executions(), Heap(Static("a")), DuringStartup), Describe(census));
    }

    /// <summary>A wildcard of a region that excludes one field: the index keeps its pair with itself and with the other field's bucket,
    /// and drops, within the block of the wildcard and the excluded field's bucket, their one pair. The two field buckets are of one depth,
    /// so the pairs part compares no two of them.</summary>
    [Fact]
    public void A_wildcard_that_excludes_a_field_drops_its_pair_with_that_field_and_keeps_the_rest()
    {
        var (excluded, kept) = (Field("X"), Field("Y"));
        Access[] accesses =
        [
            OnField(Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), excluded),
            OnField(Make("a", "Y", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), kept),
            Wildcard("a", excluded)
        ];
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap);

        Assert.Equal("withinBuckets resource=2 wildcardWithinBeforeDrops=1 wildcardAcrossBeforeDrops=2", census.IndexLines()[1]);
        Assert.Equal("pairs complete=true total=4 resource=2 cells=0 fields=0 wildcardWithin=1 wildcardAcross=1 openRegion=0 blocks=5 " +
                     $"rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=1 bucketPairsTested=0 steps={census.PairsSteps}", census.PairsLines()[0]);
        Assert.Equal((long)CandidateIndex.Build(accesses, heap).Pairs().Count(), census.AllPairs);
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(census));
    }

    /// <summary>A wildcard of an open region that excludes the field of the one access of a closed region of its group: the enumeration
    /// leaves the whole block out on their first accesses, so the census counts it as a rejected block of one pair, none of the open
    /// region's pairs, and as many pairs as the index yields; the searches cover the two blocks yielded.</summary>
    [Fact]
    public void A_block_a_wildcard_rejects_on_its_first_accesses_is_counted_as_rejected()
    {
        var excluded = Field("X");
        Access[] accesses = [Wildcard("o", excluded), OnField(Make("k", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), excluded)];
        var heap = Heap(Static("o", "g", isOpen: true), Static("k", "g"));

        var census = Census(accesses, heap);

        Assert.Equal("pairs complete=true total=2 resource=1 cells=0 fields=0 wildcardWithin=1 wildcardAcross=0 openRegion=0 blocks=2 " +
                     $"rejectedBlocks=1 pairsOfRejectedBlocks=1 droppedInBlocks=0 bucketPairsTested=1 steps={census.PairsSteps}", census.PairsLines()[0]);
        Assert.StartsWith("coverage complete=true stopped=none blocks=2/2 pairs=2/2 ", census.Lines()[0]);
        Assert.Equal((long)CandidateIndex.Build(accesses, heap).Pairs().Count(), census.AllPairs);
    }

    /// <summary>Three wildcards of one region — one excluding two fields, one excluding the first of them, one excluding none — with an
    /// access on each field: of the wildcards' pairs with the two fields' buckets three are dropped and three kept, as the pair-by-pair
    /// count finds, and each wildcard is a class of its own for the searches. Library-internal, every pair is a search one witness ends,
    /// and its searches are the pair-by-pair count's: one orientation for each of the three pairs across two buckets.</summary>
    [Fact]
    public void Wildcards_of_one_region_that_exclude_different_fields_each_drop_their_own_pairs()
    {
        var (first, second) = (Field("X"), Field("Y"));
        Access[] accesses =
        [
            OnField(Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), first),
            OnField(Make("a", "Y", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), second),
            Wildcard("a", first, second),
            Wildcard("a", first),
            Wildcard("a")
        ];
        Access[] internals = accesses.Select(access => access with { IsLibraryInternal = true }).ToArray();
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap);
        var once = Census(internals, heap);

        Assert.Equal("pairs complete=true total=11 resource=2 cells=0 fields=0 wildcardWithin=6 wildcardAcross=3 openRegion=0 blocks=5 " +
                     $"rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=3 bucketPairsTested=0 steps={census.PairsSteps}", census.PairsLines()[0]);
        Assert.Equal((long)CandidateIndex.Build(accesses, heap).Pairs().Count(), census.AllPairs);
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(census));
        Assert.Equal(11L, once.Decided(PairDecision.SearchOnce));
        Assert.Equal(Reference(internals, Executions(), heap, DuringStartup), Describe(once));
    }

    /// <summary>The drops the frequencies of fields and exclusions count in both directions. An open region's access on X meets a closed
    /// region's wildcard bucket of its group, whose first wildcard excludes nothing and whose second excludes X: the block is yielded and
    /// the pair of X with the second wildcard, the wildcard on the block's other side, is dropped. Three wildcards with root fields — on
    /// F1 excluding F2, on F2 excluding F1, on F1 excluding F1 — drop four of their six pairs: the first two each exclude the other's field,
    /// a pair dropped both ways and counted once, the third excludes the first's field and its own, so its pair with itself goes too.</summary>
    [Fact]
    public void Drops_are_counted_on_either_side_of_a_block_and_a_pair_dropped_both_ways_once()
    {
        var (x, f1, f2) = (Field("X"), Field("F1"), Field("F2"));
        Access[] accesses =
        [
            OnField(Make("o", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), x),
            Wildcard("k"),
            Wildcard("k", x),
            OnField(Wildcard("a", f2), f1),
            OnField(Wildcard("a", f1), f2),
            OnField(Wildcard("a", f1), f1)
        ];
        var heap = Heap(Static("o", "g", isOpen: true), Static("k", "g"), Static("a"));

        var census = Census(accesses, heap);

        Assert.Equal("pairs complete=true total=7 resource=1 cells=0 fields=0 wildcardWithin=5 wildcardAcross=0 openRegion=1 blocks=4 " +
                     $"rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=5 bucketPairsTested=1 steps={census.PairsSteps}", census.PairsLines()[0]);
        Assert.Equal((long)CandidateIndex.Build(accesses, heap).Pairs().Count(), census.AllPairs);
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(census));
    }

    /// <summary>Three field buckets of one region: a struct field S, its field S.X and another struct's field T.X. Only buckets of two
    /// depths are compared — S with S.X and S with T.X, two comparisons — and only S.X continues S, so the census counts one block of
    /// two fields, as the index yields.</summary>
    [Fact]
    public void Field_buckets_of_two_depths_make_a_block_only_where_one_continues_the_other()
    {
        var inner = new MemberKey("Fixture.Inner", "X", IrFieldKind.Field);
        Access[] accesses =
        [
            Make("a", "S", AccessOperation.Write, (ROOT_A, ROOT_OF_A)),
            Below(Make("a", "S", AccessOperation.Write, (ROOT_A, ROOT_OF_A))),
            Below(Make("a", "T", AccessOperation.Write, (ROOT_A, ROOT_OF_A)))
        ];
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap);

        Assert.Equal("pairs complete=true total=4 resource=3 cells=0 fields=1 wildcardWithin=0 wildcardAcross=0 openRegion=0 blocks=4 " +
                     "rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0 bucketPairsTested=2 steps=6", census.PairsLines()[0]);
        Assert.Equal((long)CandidateIndex.Build(accesses, heap).Pairs().Count(), census.AllPairs);
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(census));

        Access Below(Access access) =>
            access with { Resource = access.Resource with { AccessPath = [.. access.Resource.AccessPath, "X"], StructPath = [inner] } };
    }

    /// <summary>Wildcards with root fields that drop nothing, next to 2,000 field buckets of their heap region: 2,000 that exclude no field,
    /// then 2,000 that each exclude a field no access has. Neither pairs their classes: the pairs part takes 8,001 steps — a block each,
    /// 2,000 to read the wildcards and one to read each field bucket — and 16,001 where the wildcards' 2,000 exclusions are read three
    /// times, to count them, to number their sets and to find none among the wildcards' fields, and each field bucket's field is looked up
    /// once.</summary>
    [Fact]
    public void Wildcards_with_root_fields_that_drop_nothing_cost_steps_linear_in_their_buckets()
    {
        Access[] fields = Enumerable.Range(0, 2_000).Select(at => OnField(Make("a", $"G{at}", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), Field($"G{at}")))
                                    .ToArray();
        var census = Census([.. fields, .. Enumerable.Range(0, 2_000).Select(at => OnField(Wildcard("a"), Field($"F{at}")))], Heap(Static("a")),
                            searches: false);

        Assert.Equal("pairs complete=true total=6003000 resource=2000 cells=0 fields=0 wildcardWithin=2001000 wildcardAcross=4000000 openRegion=0 " +
                     "blocks=4001 rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0 bucketPairsTested=0 steps=8001", census.PairsLines()[0]);

        census = Census([.. fields, .. Enumerable.Range(0, 2_000).Select(at => OnField(Wildcard("a", Field($"D{at}")), Field($"F{at}")))],
                        Heap(Static("a")), searches: false);

        Assert.Equal("pairs complete=true total=6003000 resource=2000 cells=0 fields=0 wildcardWithin=2001000 wildcardAcross=4000000 openRegion=0 " +
                     "blocks=4001 rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0 bucketPairsTested=0 steps=16001", census.PairsLines()[0]);
    }

    /// <summary>One class of 300 writes, each in an instance tree of its own, and thirty classes of one write each under a guard of its
    /// own, in one bucket: the census walks the smaller table of each pair of classes, so it counts the same and takes the same steps
    /// whether the large class comes first or last — searched, and library-internal, each pair a search one witness ends.</summary>
    [Fact]
    public void Skewed_classes_cost_the_same_steps_in_either_order()
    {
        Access[] large = Enumerable.Range(0, 300).Select(at => Make("a", "X", AccessOperation.Write, ($"b{at}", ROOT_OF_A))).ToArray();
        Access[] small = Enumerable.Range(0, 30).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
        {
            Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}s{at}", PathRelation.Equal, "1", $"s{at} == 1")]
        }).ToArray();
        var heap = Heap(Static("a"));

        var first = Census([.. large, .. small], heap);
        var last = Census([.. small, .. large], heap);
        var onceFirst = Census([.. Internal(large), .. Internal(small)], heap);
        var onceLast = Census([.. Internal(small), .. Internal(large)], heap);

        Assert.Equal(first.Steps, last.Steps);
        Assert.Equal(Describe(first), Describe(last));
        Assert.Equal(Reference([.. large, .. small], Executions(), heap, DuringStartup), Describe(first));
        Assert.Equal(330L * 331 / 2, onceFirst.Decided(PairDecision.SearchOnce));
        Assert.Equal(onceFirst.Steps, onceLast.Steps);
        Assert.Equal(Describe(onceFirst), Describe(onceLast));
        Assert.Equal(Reference([.. Internal(large), .. Internal(small)], Executions(), heap, DuringStartup), Describe(onceFirst));

        static IEnumerable<Access> Internal(Access[] accesses) => accesses.Select(access => access with { IsLibraryInternal = true });
    }

    /// <summary>A write under a lock, so that one witness ends its search, on a transient of a hosted service, made by one execution
    /// instance in 100 variants: no pair of its executions passes, so pairing searches each of its 5,050 pairs of variants, finds no
    /// witness and skips it as no overlap. The census counts its one pair as one search at least and 5,050 at most.</summary>
    [Fact]
    public void A_pair_one_witness_ends_is_searched_from_once_to_every_pair_of_its_variants()
    {
        var heap = Heap(new HeapRegion("h", HeapRegionKind.Di, "h", null, $"di|{DiIndex.HOSTED_SERVICE_KEY}|h", "h", false, false));
        Access[] accesses =
        [
            new(Make("h", "X", AccessOperation.Write).Resource, AccessOperation.Write, ROOT_OF_A, "S.M", SPAN, ["lock m"], ["m"], [], [], [])
            {
                ExecutionId = ROOT_A,
                HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
                {
                    ["m"] = [new HeldProtection(IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive, true)]
                },
                Executions = Enumerable.Range(0, 100).Select(at => new AccessExecution(ROOT_A, Variant(ROOT_OF_A), at)).ToArray()
            }
        ];

        var census = Census(accesses, heap);
        var run = PairWithProgress(accesses, Executions(), heap);

        Assert.Equal("decided over=all noCheckReadRead=0 noCheckNoConflictingOperation=0 noCheckNoOverlap=0 oneCheckConfined=0 " +
                     "oneCheckUnsatisfiablePath=0 oneCheckFreshObject=0 searchOnce=1 search=0", census.Lines()[1]);
        Assert.Equal("searches over=all search=0 searchOnceAtLeast=1 searchOnceAtMost=5050", census.Lines()[5]);
        Assert.Equal(0, run.Pairs.Suppressed);
        Assert.Equal(1, run.Pairs.Skips[InterproceduralPairing.SKIP_NO_OVERLAP]);
    }

    /// <summary>Forty library-internal writes of one bucket, each a class of its own by a guard on a subject of its own, each made by two
    /// roots in a variant apiece: every one of the 820 pairs of classes is a search one witness ends. The census reads their variants as
    /// pairing counts them and no execution, so a limit of one entry of counts, past which every class's counts would be let go at each
    /// pair, changes nothing: no execution read, no entry kept, the same steps. The searches are those of the same writes searched — a
    /// write with itself 3, two writes on their two resource objects 8, 6,360 in all — and the pairs of executions 3 and 4, 3,240. Told
    /// that pairing counts three variants of each, the census searches a write with itself 6 times and two writes 18, 14,280 in all.</summary>
    [Fact]
    public void Pairs_one_witness_ends_read_the_variants_pairing_counts_and_no_execution_whatever_the_limit_of_counts()
    {
        var heap = Heap(Static("a"));

        var kept = Census(Writes(libraryInternal: true), heap);
        var limited = Census(Writes(libraryInternal: true), heap, limits: PairingCensus.LIMITS with { CountsLimit = 1 });
        var searched = Census(Writes(libraryInternal: false), heap);
        var three = Census(Writes(libraryInternal: true), heap, variantsOf: _ => 3);

        Assert.Equal((0L, 0L, kept.Steps), (limited.ExecutionsRead, limited.CountEntriesPeak, limited.Steps));
        Assert.Equal("decided over=all noCheckReadRead=0 noCheckNoConflictingOperation=0 noCheckNoOverlap=0 oneCheckConfined=0 " +
                     "oneCheckUnsatisfiablePath=0 oneCheckFreshObject=0 searchOnce=820 search=0", limited.Lines()[1]);
        Assert.Equal("searches over=all search=0 searchOnceAtLeast=820 searchOnceAtMost=6360", limited.Lines()[5]);
        Assert.Equal("searches over=all search=0 searchOnceAtLeast=820 searchOnceAtMost=14280", three.Lines()[5]);
        Assert.Equal(3_240L, limited.OnceExecutionPairs);
        Assert.Equal((6_360L, 820L), (searched.Searches, searched.Decided(PairDecision.Search)));
        Assert.Equal(Describe(kept), Describe(limited));
        Assert.Equal(Reference(Writes(libraryInternal: true), Executions(), heap, DuringStartup), Describe(limited));

        static Access[] Writes(bool libraryInternal) =>
            Enumerable.Range(0, 40).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B)) with
            {
                Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}s{at}", PathRelation.Equal, "1", $"s{at} == 1")],
                IsLibraryInternal = libraryInternal
            }).ToArray();
    }

    /// <summary>Two writes of one bucket under one lock m, which the first holds twice and the second three times, each with a held object
    /// the other does not hold: their protection is sufficient, and each of its three new answers pays a step, a step for each id of either
    /// side and, for each id they share, the holdings of one side times the other's. Naming m twice on the first adds four steps: one to
    /// read it into its protection and one in each answer for each extra time it is named — two with itself, one with the second — but
    /// none for the holdings compared, since m is compared once, as <see cref="PairProtection.Of"/> intersects the ids. A fourth holding of
    /// m on the second adds ten: one to read it, and of the holdings compared 2 with the first and 7 with itself.</summary>
    [Fact]
    public void A_protection_decided_pays_each_id_named_and_the_holdings_of_each_shared_id_once()
    {
        var heap = Heap(Static("a"));

        var plain = Census([Held(["m", "a"], 2), Held(["m", "b"], 3)], heap);
        var named = Census([Held(["m", "m", "a"], 2), Held(["m", "b"], 3)], heap);
        var held = Census([Held(["m", "a"], 2), Held(["m", "b"], 4)], heap);

        Assert.All([plain, named, held], census => Assert.Equal(3L, census.Decided(PairDecision.SearchOnce)));
        Assert.Equal(plain.Steps + 4, named.Steps);
        Assert.Equal(plain.Steps + 10, held.Steps);

        static Access Held(string[] ids, int holdings) => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
        {
            HeldProtection = ids,
            HeldProtectionIds = ids,
            HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
            {
                ["m"] = [.. Enumerable.Repeat(new HeldProtection(IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive, true), holdings)]
            }
        };
    }

    /// <summary>A write holding 1,000 objects, decided first with itself, then twenty writes holding one object each, every one of its
    /// own: the long list gets a set of its own, so that the set the decisions share, which the lookups leave empty and each of the
    /// 230 decisions after it clears, keeps the capacity of the lists of one id and never that of 1,000. The counts are the pair-by-pair
    /// count's: the long write with itself and each single write with itself are sufficient, 21 pairs one witness ends, and every
    /// other pair holds different objects, 210 searched.</summary>
    [Fact]
    public void A_protection_of_many_held_objects_leaves_the_set_the_decisions_share_small()
    {
        Access[] accesses =
            [Holding([.. Enumerable.Range(0, 1_000).Select(at => $"a{at}")]), .. Enumerable.Range(0, 20).Select(at => Holding([$"s{at}"]))];
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap);

        Assert.Equal((21L, 210L), (census.Decided(PairDecision.SearchOnce), census.Decided(PairDecision.Search)));
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(census));
        Assert.True(census.HeldIdsCapacity < 1_000, $"the shared set keeps a capacity of {census.HeldIdsCapacity}");

        static Access Holding(string[] ids) =>
            Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with { HeldProtection = ids, HeldProtectionIds = ids };
    }

    /// <summary>Ten writes of ten execution instances each, every one in an instance tree of its own: the budget, 39 steps, runs out at
    /// the 26th execution its one class's counts read — after a step for the block, ten for the class, one for the pair of classes and
    /// one each for its conditions and its protection. The census covers no pair, and still says what it read and held: 25 executions,
    /// and 72 entries, the two first writes' 31 each and the third's 10 copies so far.</summary>
    [Fact]
    public void A_budget_stopping_inside_a_class_s_counts_keeps_the_entries_and_executions_it_read()
    {
        Access[] accesses = Enumerable.Range(0, 10).Select(at => Make("a", "X", AccessOperation.Write, [.. Copies(at)])).ToArray();

        var census = Census(accesses, Heap(Static("a")), limits: PairingCensus.LIMITS with { Budget = 39 });

        Assert.Equal("coverage complete=false stopped=budget in=searches blocks=0/1 pairs=0/55 steps=40 budget=39 executionsRead=25 " +
                     "countEntriesPeak=72 decisionClears=0", census.Lines()[0]);

        static IEnumerable<(string Execution, AccessRoot PathRoot)> Copies(int at) =>
            Enumerable.Range(0, 10).Select(copy => ($"x{at}-{copy}", ROOT_OF_A));
    }

    /// <summary>The budget a diagnostic run reads from <c>CH_CENSUS_BUDGET</c>: a positive whole number of steps up to 10^15, and
    /// nothing else, which leaves the default.</summary>
    [Fact]
    public void The_budget_variable_sets_the_budget_only_where_it_is_a_positive_whole_number_up_to_the_most()
    {
        Assert.Equal("CH_CENSUS_BUDGET", PairingCensus.BUDGET_VARIABLE);
        Assert.Equal(PairingCensus.LIMITS with { Budget = 600_000_000_000 }, PairingCensus.LimitsOf("600000000000"));
        Assert.Equal(123L, PairingCensus.LimitsOf("123").Budget);
        Assert.Equal(1_000_000_000_000_000L, PairingCensus.LimitsOf("1000000000000000").Budget);
        Assert.All(new string?[] { null, string.Empty, "0", "-5", " 7", "7 ", "1e9", "12,000", "abc", "1000000000000001", "9223372036854775807",
                                   "99999999999999999999" },
                   text => Assert.Same(PairingCensus.LIMITS, PairingCensus.LimitsOf(text)));
    }

    /// <summary>The two distributions the review constructed, each at 2,000: buckets of one depth in one region, which never meet, and a
    /// wildcard bucket with as many exclusion sets as wildcards. The index part is linear in the accesses and buckets, and the pairs part
    /// compares no two field buckets and counts the drops from how many wildcards exclude each field: 2,000 steps for the field buckets,
    /// one a block, and for the wildcards five blocks, 6,000 steps to read the 2,000 wildcards and their 4,000 exclusions, a step to read
    /// each field bucket and a step to look its field up — where pairing their classes would have taken some two million.</summary>
    [Fact]
    public void The_pairs_part_compares_no_buckets_of_one_depth_and_counts_drops_without_pairing_classes()
    {
        Access[] fields = Enumerable.Range(0, 2_000).Select(at => OnField(Make("a", $"F{at}", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), Field($"F{at}")))
                                    .ToArray();
        var census = Census(fields, Heap(Static("a")), searches: false);

        Assert.Equal("pairs complete=true total=2000 resource=2000 cells=0 fields=0 wildcardWithin=0 wildcardAcross=0 openRegion=0 blocks=2000 " +
                     "rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0 bucketPairsTested=0 steps=2000", census.PairsLines()[0]);

        var (x, y) = (Field("X"), Field("Y"));
        Access[] wildcards =
        [
            OnField(Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), x),
            OnField(Make("a", "Y", AccessOperation.Write, (ROOT_A, ROOT_OF_A)), y),
            .. Enumerable.Range(0, 2_000).Select(at => Wildcard("a", at < 1_000 ? x : y, Field($"D{at}")))
        ];
        census = Census(wildcards, Heap(Static("a")), searches: false);

        Assert.Equal("withinBuckets resource=2 wildcardWithinBeforeDrops=2001000 wildcardAcrossBeforeDrops=4000", census.IndexLines()[1]);
        Assert.Equal("pairs complete=true total=2003002 resource=2 cells=0 fields=0 wildcardWithin=2001000 wildcardAcross=2000 openRegion=0 " +
                     "blocks=5 rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=2000 bucketPairsTested=0 steps=6009", census.PairsLines()[0]);
    }

    /// <summary>A write on a transient of a hosted service, with itself in one execution: pairing checks its one pair of executions and
    /// skips it there as no overlap, without a timing question; the census bounds that check from both sides.</summary>
    [Fact]
    public void A_hosted_transient_with_itself_is_one_check_of_its_pair_of_executions()
    {
        Access[] accesses = [Make("h", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A))];
        var heap = Heap(new HeapRegion("h", HeapRegionKind.Di, "h", null, $"di|{DiIndex.HOSTED_SERVICE_KEY}|h", "h", false, false));

        var census = Census(accesses, heap);
        var run = PairWithProgress(accesses, Executions(), heap);

        Assert.Equal("bounds over=all executionPairChecksAtLeast=1 executionPairChecksAtMost=1 witnessesAtMost=1 candidatesAtMost=1",
                     census.Lines()[4]);
        Assert.Equal(1L, run.Considered);
        Assert.Equal(1L, run.Checks);
        Assert.Equal(1, run.Pairs.Skips[InterproceduralPairing.SKIP_NO_OVERLAP]);
    }

    /// <summary>A list naming one access twice: pairing sees one side and three pairs of it with itself; the census counts those three
    /// pairs in the index and pairs parts, names the duplicate, and leaves the searches out rather than count the two names as two
    /// accesses, every count line saying it covers none of the three.</summary>
    [Fact]
    public void An_access_named_twice_is_counted_in_the_index_and_left_out_of_the_searches()
    {
        var access = Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B));
        Access[] accesses = [access, access];
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap);
        var run = PairWithProgress(accesses, Executions(), heap);

        Assert.Equal(1L, census.DuplicateAccesses);
        Assert.Equal(3L, census.AllPairs);
        Assert.Equal(run.Considered, census.AllPairs);
        Assert.StartsWith("coverage complete=false stopped=duplicates blocks=0/1 pairs=0/3 ", census.Lines()[0]);
        Assert.All(census.Lines().Skip(1).Take(5), line => Assert.Contains(" over=covered:0/3 ", line));
    }

    /// <summary>Forty writes under forty conditions on different subjects, each of two executions in two trees: forty classes and 820 pairs
    /// of them, every one searched, whose counts are known by hand — 3 pairs of executions, variants, searches and pairs of roots for a
    /// class with itself, 4, 4, 8 and 8 for two classes. With the budget the whole census takes it is complete; with one step less it
    /// covers all but the last pair of classes, a class with itself, and every count line is that prefix's, over 819 of 820; with 300
    /// steps it covers a shorter prefix whose lines are again that prefix's. Progress lines followed both parts: those of the pairs part
    /// hold the pairs of the blocks finished, none of the one block under way, and every one of the searches part holds the count lines
    /// of the prefix it says it covered.</summary>
    [Fact]
    public void A_census_past_its_budget_counts_whole_pairs_of_classes_of_a_prefix_and_says_so_in_every_line()
    {
        Access[] accesses = Enumerable.Range(0, 40).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B)) with
        {
            Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}s{at}", PathRelation.Equal, "1", $"s{at} == 1")]
        }).ToArray();
        var heap = Heap(Static("a"));
        var whole = Census(accesses, heap);

        Assert.True(whole.Complete);
        Assert.Equal(PrefixLines(820, "over=all"), whole.Lines().Skip(1).Take(5));
        var exact = Census(accesses, heap, limits: PairingCensus.LIMITS with { Budget = whole.Steps });
        Assert.True(exact.Complete);

        var short1 = Census(accesses, heap, limits: PairingCensus.LIMITS with { Budget = whole.Steps - 1 });
        Assert.False(short1.Complete);
        Assert.Equal($"coverage complete=false stopped=budget in=searches blocks=0/1 pairs=819/820 steps={short1.Steps} budget={whole.Steps - 1} " +
                     "executionsRead=80 countEntriesPeak=280 decisionClears=0", short1.Lines()[0]);
        Assert.Equal(PrefixLines(819, "over=covered:819/820"), short1.Lines().Skip(1).Take(5));

        var progress = new List<IReadOnlyList<string>>();
        var limits = PairingCensus.LIMITS with { Budget = 300, PollSteps = 1 };
        var census = new PairingCensus(accesses, CandidateIndex.Build(accesses, heap), VariantsOf, Executions(), DuringStartup, limits,
                                       new CensusProgress(TimeSpan.Zero, progress.Add))
            .CountIndex().CountPairs().CountSearches();

        Assert.False(census.Complete);
        Assert.InRange(census.PairsCovered, 1L, 40L);
        Assert.StartsWith($"coverage complete=false stopped=budget in=searches blocks=0/1 pairs={census.PairsCovered}/820 ", census.Lines()[0]);
        Assert.Equal(PrefixLines(census.PairsCovered, $"over=covered:{census.PairsCovered}/820"), census.Lines().Skip(1).Take(5));
        var pairsPart = progress.Where(lines => lines[0].StartsWith("stage=pairs ", StringComparison.Ordinal)).ToArray();
        var searchesPart = progress.Where(lines => lines[0].StartsWith("stage=searches ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(progress.Count, pairsPart.Length + searchesPart.Length);
        Assert.NotEmpty(pairsPart);
        Assert.All(pairsPart, lines => Assert.Equal("pairs complete=false total=0 resource=0 cells=0 fields=0 wildcardWithin=0 wildcardAcross=0 " +
                                                    "openRegion=0 blocks=0 rejectedBlocks=0 pairsOfRejectedBlocks=0 droppedInBlocks=0", lines[1]));
        Assert.True(searchesPart.Length > 10, $"{searchesPart.Length} progress lines of the searches");
        Assert.All(searchesPart, lines =>
        {
            var covered = long.Parse(Regex.Match(lines[0], @"^stage=searches blocks=0/1 pairs=(\d+)/820 .*budget=300 ").Groups[1].Value);
            Assert.Equal(PrefixLines(covered, $"over=covered:{covered}/820"), lines.Skip(1));
        });
    }

    /// <summary>One write of one execution under a budget of two steps: its one pair of classes costs more, so the census stops without
    /// covering it and every count line covers none of the one pair; with no budget at all the pairs part stops on the first block, and
    /// the searches part, whose share would have no total, is not counted.</summary>
    [Fact]
    public void A_pair_of_classes_dearer_than_the_budget_adds_nothing_and_a_budget_spent_on_the_pairs_counts_no_searches()
    {
        Access[] accesses = [Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A))];
        var heap = Heap(Static("a"));

        var census = Census(accesses, heap, limits: PairingCensus.LIMITS with { Budget = 2 });

        Assert.True(census.PairsComplete);
        Assert.StartsWith("coverage complete=false stopped=budget in=searches blocks=0/1 pairs=0/1 ", census.Lines()[0]);
        Assert.All(census.Lines().Skip(1).Take(5), line => Assert.Contains(" over=covered:0/1 ", line));
        Assert.Equal(0L, census.Decided(PairDecision.Search));

        census = Census(accesses, heap, limits: PairingCensus.LIMITS with { Budget = 0 });

        Assert.False(census.PairsComplete);
        Assert.StartsWith("pairs complete=false total=0 ", census.PairsLines()[0]);
        Assert.StartsWith("coverage complete=false stopped=budget in=pairs blocks=0/unknown pairs=0/unknown ", census.Lines()[0]);
        Assert.All(census.Lines().Skip(1).Take(5), line => Assert.Contains(" over=covered:0/unknown ", line));
    }

    /// <summary>Three hundred writes under three hundred equalities of one subject: every two of them contradict and each with itself is
    /// searched, 45,150 pairs of conditions asked once each. A memo of sixteen answers forgets them 2,821 times and the counts are those of
    /// a memo that keeps them all, and those of the pair-by-pair count.</summary>
    [Fact]
    public void Many_guards_on_one_subject_keep_their_counts_when_the_memo_of_decisions_forgets()
    {
        Access[] accesses = Enumerable.Range(0, 300).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
        {
            Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}x", PathRelation.Equal, $"{at}", $"x == {at}")]
        }).ToArray();
        var heap = Heap(Static("a"));

        var kept = Census(accesses, heap);
        var forgetting = Census(accesses, heap, limits: PairingCensus.LIMITS with { DecisionsLimit = 16 });

        Assert.Equal(0L, kept.DecisionClears);
        Assert.Equal(2_821L, forgetting.DecisionClears);
        Assert.Equal(44_850L, forgetting.Decided(PairDecision.UnsatisfiablePath));
        Assert.Equal(300L, forgetting.Decided(PairDecision.Search));
        Assert.Equal(Describe(kept), Describe(forgetting));
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(forgetting));
    }

    /// <summary>Two hundred writes under equalities of one subject to fifty values, every other one under a lock m, with memos of sixteen
    /// answers: few numbers of conditions and of protections, the form whose memo keys a long's own hash puts into few chains. Their
    /// 20,100 pairs are decided every way the memos answer — contradicting, a search one witness ends and a search — and a census whose
    /// memos hash their keys as a long does counts what the census's own does: the same lines but the time, steps, decisions and memo
    /// clears. The hash changes what an answer costs in time, never what the census counts.</summary>
    [Fact]
    public void The_hash_of_the_memo_keys_changes_no_count()
    {
        Access[] accesses = Enumerable.Range(0, 200).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
        {
            Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}x", PathRelation.Equal, $"{at % 50}", $"x == {at % 50}")],
            HeldProtection = at % 2 == 0 ? ["lock m"] : [],
            HeldProtectionIds = at % 2 == 0 ? ["m"] : []
        }).ToArray();
        var heap = Heap(Static("a"));
        var limits = PairingCensus.LIMITS with { DecisionsLimit = 16 };

        var own = Census(accesses, heap, limits: limits);
        var folded = Census(accesses, heap, limits: limits, memoKeys: EqualityComparer<long>.Default);

        Assert.Equal(20_100L, own.AllPairs);
        Assert.All([PairDecision.UnsatisfiablePath, PairDecision.SearchOnce, PairDecision.Search],
                   decision => Assert.True(own.Decided(decision) > 0, $"{decision}"));
        Assert.True(own.DecisionClears > 0, "the memos forget");
        Assert.Equal((folded.Steps, folded.DecisionClears), (own.Steps, own.DecisionClears));
        Assert.Equal(folded.Lines().SkipLast(1), own.Lines().SkipLast(1));
        Assert.Equal(Describe(folded), Describe(own));
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(own));
    }

    /// <summary>Three classes whose counts hold 7, 7 and 19 entries, the last more than a limit of ten alone: past the limit the counts
    /// are let go but those of the pair being counted, made again when asked for, and the counts stay those of the pair-by-pair count.
    /// The peak counts every table alive while one is made — 33, the three at once, before the second is let go — with the limit as
    /// without it, while the executions read rise from 10 to 18 as two classes are counted twice.</summary>
    [Fact]
    public void Counts_let_go_past_their_limit_stay_exact_and_the_peak_counts_every_table_alive()
    {
        Access[] accesses =
        [
            Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B)) with { Conditions = [Guard("s0")] },
            Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B)) with { Conditions = [Guard("s1")] },
            Make("a", "X", AccessOperation.Write, [.. Enumerable.Range(1, 6).Select(at => ($"x{at}", ROOT_OF_A))]) with { Conditions = [Guard("s2")] }
        ];
        var heap = Heap(Static("a"));

        var unlimited = Census(accesses, heap);
        var limited = Census(accesses, heap, limits: PairingCensus.LIMITS with { CountsLimit = 10 });

        Assert.Equal((33L, 10L), (unlimited.CountEntriesPeak, unlimited.ExecutionsRead));
        Assert.Equal((33L, 18L), (limited.CountEntriesPeak, limited.ExecutionsRead));
        Assert.Equal(Describe(unlimited), Describe(limited));
        Assert.Equal(Reference(accesses, Executions(), heap, DuringStartup), Describe(limited));

        static PathPredicate Guard(string subject) => new($"{PathPredicate.CANONICAL}{subject}", PathRelation.Equal, "1", $"{subject} == 1");
    }

    /// <summary>The census writes its checkpoints into a diagnostic folder as a run does: the index first, before any work of the budget,
    /// then the pairs, then the searches, each with the lines the census gives, the progress lines between them, every name with the
    /// scope.</summary>
    [Fact]
    public void The_census_writes_the_index_before_any_budgeted_work_then_the_pairs_then_the_searches()
    {
        Access[] accesses = Enumerable.Range(0, 40).Select(at => Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B)) with
        {
            Conditions = [new PathPredicate($"{PathPredicate.CANONICAL}s{at}", PathRelation.Equal, "1", $"s{at} == 1")]
        }).ToArray();
        var heap = Heap(Static("a"));
        var folder = Directory.CreateTempSubdirectory("ch492-census-").FullName;
        try
        {
            var stepsAtIndex = -1L;
            PairingCensus? census = null;
            void Reach(string name, Func<IEnumerable<string>> details)
            {
                if (name.StartsWith("pairing-census-index:", StringComparison.Ordinal))
                    stepsAtIndex = census!.Steps;
                DiagnosticCheckpoint.ReachIn(folder, [], name, collect: false, details);
            }

            census = new PairingCensus(accesses, CandidateIndex.Build(accesses, heap), VariantsOf, Executions(), DuringStartup,
                                       PairingCensus.LIMITS with { Budget = 300, PollSteps = 1 },
                                       new CensusProgress(TimeSpan.Zero, details => Reach($"pairing-census-progress:{SCOPE}", () => details)));
            census.WriteTo(SCOPE, Reach);

            var written = File.ReadAllLines(Path.Combine(folder, "checkpoints.log")).Select(line => line[(line.IndexOf(' ') + 1)..]).ToArray();
            var names = written.Where(line => line.Contains(" collected=False ", StringComparison.Ordinal))
                               .Select(line => line[..line.IndexOf(' ')])
                               .Aggregate(new List<string>(), (list, name) =>
                               {
                                   if (list.Count == 0 || list[^1] != name)
                                       list.Add(name);
                                   return list;
                               });
            Assert.Equal(0L, stepsAtIndex);
            Assert.Equal(
            [
                $"pairing-census-index:{SCOPE}", $"pairing-census-progress:{SCOPE}", $"pairing-census-pairs:{SCOPE}",
                $"pairing-census-progress:{SCOPE}", $"pairing-census:{SCOPE}"
            ], names);
            Assert.Equal(census.IndexLines(), Payload(written, $"pairing-census-index:{SCOPE}"));
            Assert.Equal(census.PairsLines(), Payload(written, $"pairing-census-pairs:{SCOPE}"));
            Assert.Equal(census.Lines(), Payload(written, $"pairing-census:{SCOPE}"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static IEnumerable<string> Payload(IEnumerable<string> written, string name) =>
            written.Where(line => line.StartsWith(name + " ", StringComparison.Ordinal) && !line.Contains(" collected=False ", StringComparison.Ordinal))
                   .Select(line => line[(name.Length + 1)..]);
    }

    /// <summary>Pairing runs outside its caller's cancellation, as the analyzer calls it: inside that suspension, with the caller's work
    /// cancelled, pairing still pairs, while the census, which takes the caller's work back, stops at its first look.</summary>
    [Fact]
    public void A_census_within_pairings_suspension_stops_when_the_caller_cancels_while_pairing_goes_on()
    {
        Access[] accesses = [Make("a", "X", AccessOperation.Write, (ROOT_A, ROOT_OF_A), (ROOT_B, ROOT_OF_B))];
        var heap = Heap(Static("a"));
        var seen = 0;
        using var work = WorkCancellation.Enter(new CancellationToken(true), () => seen++);
        using (WorkCancellation.Outside())
        {
            var pairs = InterproceduralPairing.Pair(accesses, Executions(), heap);

            Assert.Equal(0, seen);
            Assert.NotEmpty(pairs.Pairs);
            Assert.Throws<OperationCanceledException>(() => PairingCensus.Write(SCOPE, accesses, CandidateIndex.Build(accesses, heap), VariantsOf,
                                                                                Executions()));
            Assert.Equal(1, seen);
        }
    }

    /// <summary>On every demo scope the census is the pair-by-pair count of the index's pairs, and it bounds what pairing then checks and
    /// finds: the pairs of executions it checks, the witnesses it keeps and the candidates they make; its pairs are the pairs pairing
    /// considers, its buckets and largest bucket pairing's, and the pairs of two reads and of no conflicting operation its skips exactly.</summary>
    [Fact]
    public async Task Demo_census_is_a_pair_by_pair_count_and_bounds_what_pairing_checks_and_finds()
    {
        var seen = new Seen();
        await ParallelCollectionTests.ForEachDemoScope(input => AssertCensus(input, seen));

        Assert.True(seen.Searched > 0, "a pair pairing searches");
        Assert.True(seen.ReadRead > 0, "a pair of two reads");
        Assert.True(seen.OtherTrees > 0, "a pair of executions of two trees");
        Assert.True(seen.SameExecution > 0, "a pair of one execution");
        Assert.True(seen.Startup > 0, "a scope with startup");
    }

    /// <summary>The same on every eShop scope.</summary>
    [RequiresEShopFact]
    public async Task EShop_census_is_a_pair_by_pair_count_and_bounds_what_pairing_checks_and_finds()
    {
        var seen = new Seen();
        await ParallelCollectionTests.ForEachScope(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src",
                                                                "eShopOnContainers-ServicesAndWebApps.sln"),
                                                   input => AssertCensus(input, seen));

        Assert.True(seen.Searched > 0, "a pair pairing searches");
        Assert.True(seen.Confined > 0, "a pair on a confined region");
    }

    /// <summary>Checks a scope's census against a pair-by-pair count and against what pairing checks and finds.</summary>
    /// <param name="input">The scope's input, after the pipeline has collected it once.</param>
    /// <param name="seen">What the checks met.</param>
    private static void AssertCensus(InterproceduralInput input, Seen seen)
    {
        var executions = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        var collection = InterproceduralAccesses.Collect(input with { Executions = executions }, CancellationToken.None);
        var candidates = collection.Folded.Where(access => !access.IsConstructionLocal).ToArray();
        var census = new PairingCensus(candidates, CandidateIndex.Build(candidates, input.Heap), VariantsOf, executions,
                                       executions.StartsDuringStartup, PairingCensus.LIMITS, null).CountIndex().CountPairs().CountSearches();

        Assert.True(census.Complete);
        Assert.Equal(Reference(candidates, executions, input.Heap, executions.StartsDuringStartup), Describe(census));
        // Startup starts during startup, and a root after it.
        if (executions.Executions.Any(execution => execution.Id == ExecutionModel.STARTUP))
        {
            Assert.True(executions.StartsDuringStartup(ExecutionModel.STARTUP));
            seen.Startup++;
        }
        Assert.All(executions.Executions.Where(execution => execution.Kind == ExecutionKind.Root),
                   root => Assert.False(executions.StartsDuringStartup(root.Id), root.Id));

        var run = PairWithProgress(collection.Folded, executions, input.Heap);
        Assert.Equal(run.Considered, census.AllPairs);
        // Pairing ends every pair it considers in a skip, a suppression or the candidates it gives, and counts a comparison for each of
        // them: the comparisons pairing-end writes pass the census's pairs by the candidates beyond the first of each pair that stands.
        var standing = census.AllPairs - run.Pairs.Skips.Values.Sum() - run.Pairs.Suppressed;
        Assert.Equal(run.Pairs.Candidates - standing, run.Comparisons - census.AllPairs);
        Assert.Equal(run.Pairs.Buckets, census.Buckets);
        Assert.Equal(run.Pairs.LargestBucket, census.Largest.DefaultIfEmpty(0).First());
        Assert.InRange(run.Checks, census.ExecutionPairChecksAtLeast, census.ExecutionPairChecksAtMost);
        Assert.InRange(run.Pairs.Pairs.Count, 0, census.WitnessesAtMost);
        Assert.InRange(run.Pairs.Candidates, 0, census.CandidatesAtMost);
        Assert.Equal(census.Decided(PairDecision.ReadRead), run.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_READ_READ));
        Assert.Equal(census.Decided(PairDecision.NoConflictingOperation),
                     run.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_NO_CONFLICTING_OPERATION));
        Assert.InRange(run.Pairs.Suppressed, 0, census.Decided(PairDecision.SearchOnce));
        Assert.InRange(run.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_CONFINED), 0, census.Decided(PairDecision.Confined));
        Assert.InRange(run.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_FRESH_OBJECT), 0, census.Decided(PairDecision.FreshObject));
        seen.Searched += census.Decided(PairDecision.Search);
        seen.ReadRead += census.Decided(PairDecision.ReadRead);
        seen.Confined += census.Decided(PairDecision.Confined);
        seen.OtherTrees += census.SearchExecutionPairsOf(PairRelation.OtherTrees);
        seen.SameExecution += census.SearchExecutionPairsOf(PairRelation.SameExecution);
    }

    /// <summary>The count lines of the forty-class bucket's searches over its first pairs of classes, in the order the census takes them:
    /// row by row, each row a class with itself and then with each later class; a class with itself gives 3 pairs of executions, of
    /// variants, searches and pairs of roots, two classes 4, 4, 8 and 8; of one execution 2 pairs either way, the rest of two trees.</summary>
    /// <param name="covered">The pairs of classes covered, each one pair of accesses.</param>
    /// <param name="over">What the lines say they are over.</param>
    private static IReadOnlyList<string> PrefixLines(long covered, string over)
    {
        var (selves, left) = (0L, covered);
        for (var row = 0; row < 40 && left > 0; row++)
        {
            selves++;
            left -= 40 - row;
        }

        var others = covered - selves;
        var (executionPairs, searches) = (3 * selves + 4 * others, 3 * selves + 8 * others);
        return
        [
            $"decided {over} noCheckReadRead=0 noCheckNoConflictingOperation=0 noCheckNoOverlap=0 oneCheckConfined=0 oneCheckUnsatisfiablePath=0 " +
            $"oneCheckFreshObject=0 searchOnce=0 search={covered}",
            $"executionPairs {over} search={executionPairs} sameExecution={2 * covered} sameTree=0 oneStartup=0 bothStartup=0 " +
            $"otherTrees={selves + 2 * others} searchOnce=0",
            $"variantPairs {over} search={executionPairs} sameTree={2 * covered} oneStartup=0 bothStartup=0 otherTrees={selves + 2 * others} acrossTrees=0",
            $"bounds {over} executionPairChecksAtLeast={covered} executionPairChecksAtMost={executionPairs} witnessesAtMost={searches} " +
            $"candidatesAtMost={searches}",
            $"searches {over} search={searches} searchOnceAtLeast=0 searchOnceAtMost=0"
        ];
    }

    /// <summary>Pairs a scope's accesses with a progress line due at every chance, and reads the last line: after the last pair of
    /// accesses, it counts every pair considered, every comparison and every pair of executions checked.</summary>
    /// <param name="accesses">The accesses.</param>
    /// <param name="executions">The executions.</param>
    /// <param name="heap">The heap.</param>
    private static PairingRun PairWithProgress(IReadOnlyList<Access> accesses, ExecutionAnalysis executions, HeapSolution heap)
    {
        var lines = new List<string>();
        var pairs = InterproceduralPairing.Pair(accesses, executions, heap, StringComparer.Ordinal,
                                                new InterproceduralPairing.PairingProgress(TimeSpan.Zero, (_, details) => lines.Add(details[0])));
        if (lines.Count == 0)
            return new PairingRun(pairs, 0, 0, 0);
        var match = PROGRESS.Match(lines[^1]);
        Assert.True(match.Success, lines[^1]);
        return new PairingRun(pairs, long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value), long.Parse(match.Groups[3].Value));
    }

    /// <summary>The census of a hand-made scope with the executions of <see cref="Executions"/>, its parts counted.</summary>
    /// <param name="accesses">The scope's accesses.</param>
    /// <param name="heap">The scope's heap.</param>
    /// <param name="ownership">The regions' ownership, or none.</param>
    /// <param name="limits">The census's limits; those of a diagnostic run by default.</param>
    /// <param name="searches">Whether the searches part is counted too.</param>
    /// <param name="variantsOf">How many variants pairing counts of an access; <see cref="VariantsOf"/> by default.</param>
    /// <param name="memoKeys">How the memos of decisions hash their keys; the census's own by default.</param>
    private static PairingCensus Census(IReadOnlyList<Access> accesses, HeapSolution heap, IReadOnlyDictionary<string, RegionOwnership>? ownership = null,
                                        CensusLimits? limits = null, bool searches = true, Func<Access, int>? variantsOf = null,
                                        IEqualityComparer<long>? memoKeys = null)
    {
        var census = new PairingCensus(accesses, CandidateIndex.Build(accesses, heap), variantsOf ?? VariantsOf, Executions(ownership), DuringStartup,
                                       limits ?? PairingCensus.LIMITS, null, memoKeys).CountIndex().CountPairs();
        return searches ? census.CountSearches() : census;
    }

    /// <summary>How many variants pairing counts of an access's executions: its executions' variants, each once, or one for an access
    /// built for one execution alone.</summary>
    /// <param name="access">The access.</param>
    private static int VariantsOf(Access access) =>
        access.Executions.Count == 0 ? 1 : access.Executions.Select(execution => execution.Variant).Distinct(ReferenceEqualityComparer.Instance).Count();

    /// <summary>The census's counts as one text, in the order of <see cref="Reference"/>.</summary>
    /// <param name="census">The census.</param>
    private static string Describe(PairingCensus census) =>
        Describe(Enum.GetValues<PairBlockKind>().Select(census.Pairs), census.DroppedInBlocks, Enum.GetValues<PairDecision>().Select(census.Decided),
                 Enum.GetValues<PairRelation>().Select(census.SearchExecutionPairsOf), census.OnceExecutionPairs,
                 Enum.GetValues<PairRelation>().Select(census.SearchVariantPairsOf), census.WitnessesAtMost, census.CandidatesAtMost,
                 census.SearchOnceSearchesAtMost);

    /// <summary>Counts as one text.</summary>
    /// <param name="pairs">The pairs yielded, by enumeration.</param>
    /// <param name="dropped">The pairs dropped within the blocks yielded.</param>
    /// <param name="decided">The pairs by decision.</param>
    /// <param name="executionPairs">The searched pairs' pairs of executions by relation.</param>
    /// <param name="onceExecutionPairs">The pairs of executions of the pairs one witness ends.</param>
    /// <param name="variantPairs">The searched pairs' pairs of variants by relation.</param>
    /// <param name="witnesses">The bound on witnesses.</param>
    /// <param name="candidates">The bound on candidates.</param>
    /// <param name="onceSearches">The most searches of the pairs one witness ends.</param>
    private static string Describe(IEnumerable<long> pairs, long dropped, IEnumerable<long> decided, IEnumerable<long> executionPairs, long onceExecutionPairs,
                                   IEnumerable<long> variantPairs, long witnesses, long candidates, long onceSearches) =>
        $"pairs={string.Join(",", pairs)} dropped={dropped} decided={string.Join(",", decided)} executionPairs={string.Join(",", executionPairs)} " +
        $"once={onceExecutionPairs} variantPairs={string.Join(",", variantPairs)} witnesses<={witnesses} candidates<={candidates} " +
        $"onceSearches<={onceSearches}";

    /// <summary>The census of a scope counted pair by pair: every pair <see cref="CandidateIndex.Pairs"/> yields, by its enumeration, with
    /// what pairing decides of it before its questions, and of each pair it searches every pair of executions and of variants by how they
    /// stand, every search with its orientations and every pair of path roots, of each pair one witness ends its pairs of executions and
    /// every search with its orientations, as many as where none ends it; and the pairs dropped within the blocks, each pair of each
    /// block tested with this test's own reading of a wildcard's exclusion.</summary>
    /// <param name="candidates">The scope's accesses pairing pairs.</param>
    /// <param name="executions">The scope's executions.</param>
    /// <param name="heap">The scope's heap.</param>
    /// <param name="duringStartup">Whether an execution may start before startup ends.</param>
    private static string Reference(IReadOnlyList<Access> candidates, ExecutionAnalysis executions, HeapSolution heap, Func<string, bool> duringStartup)
    {
        var index = CandidateIndex.Build(candidates, heap);
        var trees = executions.Executions.ToDictionary(execution => execution.Id, execution => execution.TreeRootId, StringComparer.Ordinal);
        var dropped = 0L;
        foreach (var block in index.Blocks().Where(block => !block.Rejected))
        {
            var others = block.Other ?? block.One;
            for (var one = 0; one < block.One.Count; one++)
            {
                for (var other = block.Other is null ? one : 0; other < others.Count; other++)
                {
                    if (!Covers(block.One[one].Resource, others[other].Resource) || !Covers(others[other].Resource, block.One[one].Resource))
                        dropped++;
                }
            }
        }

        var pairs = new long[Enum.GetValues<PairBlockKind>().Length];
        var decided = new long[Enum.GetValues<PairDecision>().Length];
        var (executionPairs, variantPairs) = (new long[Enum.GetValues<PairRelation>().Length], new long[Enum.GetValues<PairRelation>().Length]);
        var (once, witnesses, found, onceSearches) = (0L, 0L, 0L, 0L);
        foreach (var pair in index.Pairs())
        {
            pairs[(int)Kind(pair)]++;
            var decision = Decide(pair.First, pair.Second, executions.Ownership);
            decided[(int)decision]++;
            var self = ReferenceEquals(pair.First, pair.Second);
            var (ones, others) = (ExecutionIds(pair.First), ExecutionIds(pair.Second));
            var orientations = pair.ReportsFirst && !ReferenceEquals(pair.First.Resource, pair.Second.Resource) ? 2 : 1;
            if (decision == PairDecision.SearchOnce)
            {
                once += self ? (long)ones.Count * (ones.Count + 1) / 2 : (long)ones.Count * others.Count;
                var (oneCount, otherCount) = (Variants(pair.First).Count, Variants(pair.Second).Count);
                onceSearches += orientations * (self ? (long)oneCount * (oneCount + 1) / 2 : (long)oneCount * otherCount);
            }
            if (decision != PairDecision.Search)
                continue;
            for (var one = 0; one < ones.Count; one++)
            {
                for (var other = self ? one : 0; other < others.Count; other++)
                    executionPairs[(int)ExecutionRelation(ones[one], others[other])]++;
            }

            var (oneVariants, otherVariants) = (Variants(pair.First), Variants(pair.Second));
            for (var one = 0; one < oneVariants.Count; one++)
            {
                for (var other = self ? one : 0; other < otherVariants.Count; other++)
                    variantPairs[(int)VariantRelation(oneVariants[one].Keys, otherVariants[other].Keys)]++;
            }

            var (oneRoots, otherRoots) = (oneVariants.Select(variant => variant.Root).Distinct().Count(), otherVariants.Select(variant => variant.Root).Distinct().Count());
            witnesses += orientations * (self ? (long)oneVariants.Count * (oneVariants.Count + 1) / 2 : (long)oneVariants.Count * otherVariants.Count);
            found += orientations * (self ? (long)oneRoots * (oneRoots + 1) / 2 : (long)oneRoots * otherRoots);
        }

        return Describe(pairs, dropped, decided, executionPairs, once, variantPairs, witnesses, found, onceSearches);

        (string Tree, bool Startup) Place(string execution) =>
            trees.TryGetValue(execution, out var tree) ? (tree, duringStartup(execution)) : (execution, false);

        PairRelation ExecutionRelation(string one, string other)
        {
            if (one == other)
                return PairRelation.SameExecution;
            var (first, second) = (Place(one), Place(other));
            return first.Tree == second.Tree ? PairRelation.SameTree
                : first.Startup != second.Startup ? PairRelation.OneStartup
                : first.Startup ? PairRelation.BothStartup
                : PairRelation.OtherTrees;
        }

        PairRelation VariantRelation(IReadOnlySet<(string Tree, bool Startup)> one, IReadOnlySet<(string Tree, bool Startup)> other)
        {
            if (one.Count > 1 || other.Count > 1)
                return PairRelation.AcrossTrees;
            var (first, second) = (one.Single(), other.Single());
            return first.Tree == second.Tree ? PairRelation.SameTree
                : first.Startup != second.Startup ? PairRelation.OneStartup
                : first.Startup ? PairRelation.BothStartup
                : PairRelation.OtherTrees;
        }

        List<(IReadOnlySet<(string Tree, bool Startup)> Keys, string Root)> Variants(Access access)
        {
            if (access.Executions.Count == 0)
                return [(new HashSet<(string, bool)> { Place(access.ExecutionId) }, access.PathRoot.RootId)];
            return access.Executions.GroupBy(execution => execution.Variant, ReferenceEqualityComparer.Instance)
                         .Select(group => ((IReadOnlySet<(string, bool)>)group.Select(execution => Place(execution.ExecutionId)).ToHashSet(),
                                           ((AccessVariant)group.Key!).PathRoot.RootId))
                         .ToList();
        }
    }

    /// <summary>Whether a wildcard reaches the other resource's root storage field, as this test reads a wildcard's exclusion: a resource
    /// that is no wildcard, or one without such a field, reaches everything.</summary>
    /// <param name="wildcard">The possible wildcard.</param>
    /// <param name="other">The other resource.</param>
    private static bool Covers(AccessResource wildcard, AccessResource other) =>
        !wildcard.IsWildcard || other.Storage is not { Fields.Count: > 0 } storage || !wildcard.WildcardExclusions.Contains(FieldSlot.Key(storage.Fields[0]));

    /// <summary>The ids of an access's executions, one execution named twice where it makes the access twice.</summary>
    /// <param name="access">The access.</param>
    private static List<string> ExecutionIds(Access access) =>
        access.Executions.Count == 0 ? [access.ExecutionId] : access.Executions.Select(execution => execution.ExecutionId).ToList();

    /// <summary>The enumeration a pair the index yields comes from.</summary>
    /// <param name="pair">The pair.</param>
    private static PairBlockKind Kind(CandidatePair pair) => (pair.Enumeration, pair.ReportsFirst) switch
    {
        (PairEnumeration.Resource, true) => PairBlockKind.Resource,
        (PairEnumeration.Resource, false) => pair.First.Resource.Selector is not null ? PairBlockKind.Cells : PairBlockKind.Fields,
        (PairEnumeration.Wildcard, true) => PairBlockKind.WildcardWithin,
        (PairEnumeration.Wildcard, false) => PairBlockKind.WildcardAcross,
        _ => PairBlockKind.OpenRegion
    };

    /// <summary>What pairing decides of a pair before its questions, as its filters run: two reads, no conflicting operation, two scopes,
    /// a confined side, contradicting conditions, two fresh objects, then a search that one witness ends or one that keeps them.</summary>
    /// <param name="one">One access.</param>
    /// <param name="other">The other access.</param>
    /// <param name="ownership">The regions' ownership.</param>
    private static PairDecision Decide(Access one, Access other, IReadOnlyDictionary<string, RegionOwnership> ownership)
    {
        if (one.Operation == AccessOperation.Read && other.Operation == AccessOperation.Read)
            return PairDecision.ReadRead;
        if (!one.Operation.Conflicts() && !other.Operation.Conflicts())
            return PairDecision.NoConflictingOperation;
        if (one.Resource.Scope != other.Resource.Scope)
            return PairDecision.NoOverlap;
        if (Confined(one) || Confined(other))
            return PairDecision.Confined;
        if (PathConditions.Contradict(one.Conditions, other.Conditions))
            return PairDecision.UnsatisfiablePath;
        if (one.IsFresh && other.IsFresh)
            return PairDecision.FreshObject;
        return PairProtection.Of(one, other) == PairProtection.SUFFICIENT || one.IsLibraryInternal && other.IsLibraryInternal
            ? PairDecision.SearchOnce
            : PairDecision.Search;

        bool Confined(Access access) =>
            ownership.TryGetValue(access.IsReferenceAccess ? access.Resource.CollectionId ?? access.Resource.RegionId! : access.Resource.RegionId!,
                                  out var owned) && owned.Kind == OwnershipKind.ThreadConfined;
    }

    /// <summary>An access to a field of a region, each execution given with its variant's path root, each execution of a variant of its
    /// own.</summary>
    /// <param name="region">The region.</param>
    /// <param name="member">The field.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="executions">The executions, in collection order, each with its variant's path root.</param>
    private static Access Make(string region, string member, AccessOperation operation, params (string Execution, AccessRoot PathRoot)[] executions) =>
        new Access(new AccessResource("Fixture", SCOPE, region, [member], new MemberKey("Fixture.State", member, IrFieldKind.Field), region),
                   operation, executions.Length == 0 ? ROOT_OF_A : executions[0].PathRoot, "S.M", SPAN, [], [], [], [], [])
        {
            ExecutionId = executions.Length == 0 ? ROOT_A : executions[0].Execution,
            Executions = executions.Select((execution, at) => new AccessExecution(execution.Execution, Variant(execution.PathRoot), at)).ToArray()
        };

    /// <summary>An access whose resource is stored in a field.</summary>
    /// <param name="access">The access.</param>
    /// <param name="field">The field.</param>
    private static Access OnField(Access access, IrFieldRef field) =>
        access with { Resource = access.Resource with { Storage = new StoragePath([field], 1, access.Resource.Region, field.Name) } };

    /// <summary>A write of a region's wildcard that excludes the given fields.</summary>
    /// <param name="region">The region.</param>
    /// <param name="excluded">The fields it excludes.</param>
    private static Access Wildcard(string region, params IrFieldRef[] excluded) =>
        Make(region, "*", AccessOperation.Write, (ROOT_A, ROOT_OF_A)) with
        {
            Resource = new AccessResource("Fixture", SCOPE, region, [PathValue.WILDCARD], new MemberKey(PathValue.WILDCARD, PathValue.WILDCARD, IrFieldKind.Field),
                                          region, true) { WildcardExclusions = excluded.Select(field => FieldSlot.Key(field)).ToHashSet(StringComparer.Ordinal) }
        };

    /// <summary>A variant of its own with a path root.</summary>
    /// <param name="pathRoot">The path root.</param>
    private static AccessVariant Variant(AccessRoot pathRoot) => new(pathRoot, pathRoot, [], [], [], []);

    /// <summary>A root.</summary>
    /// <param name="id">Its id.</param>
    private static AccessRoot Root(string id) => new(id, "S.Root", id, "test", "test", POLICY, SCOPE);

    /// <summary>A field of the fixture's state type.</summary>
    /// <param name="name">The field's name.</param>
    private static IrFieldRef Field(string name) => new("Fixture", "Fixture.State", name, IrFieldKind.Field, false, false, "int");

    /// <summary>Whether a hand-made execution starts during startup: startup's own, and the delegate it hands over.</summary>
    /// <param name="execution">The execution.</param>
    private static bool DuringStartup(string execution) => execution is STARTUP or DELEGATE;

    /// <summary>The hand-made scope's executions — a root and the task it spawns, another root, startup and an unknown delegate's
    /// execution, each tree its own — and the regions' ownership.</summary>
    /// <param name="ownership">The regions' ownership, or none.</param>
    private static ExecutionAnalysis Executions(IReadOnlyDictionary<string, RegionOwnership>? ownership = null) =>
        new([new ExecutionInstance(ROOT_A, ExecutionKind.Root, "a", POLICY, ROOT_A, null),
             new ExecutionInstance(SPAWN_A, ExecutionKind.Spawn, "a spawns", POLICY, null, null) { TreeRootId = ROOT_A },
             new ExecutionInstance(ROOT_B, ExecutionKind.Root, "b", POLICY, ROOT_B, null),
             new ExecutionInstance(STARTUP, ExecutionKind.Startup, "startup", POLICY, null, null) { TreeRootId = STARTUP },
             new ExecutionInstance(DELEGATE, ExecutionKind.UnknownDelegateCall, "delegate", POLICY, null, null) { TreeRootId = DELEGATE }],
            [], ownership ?? new Dictionary<string, RegionOwnership>(), new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(),
            new Dictionary<string, IReadOnlySet<string>>(), new Dictionary<string, IReadOnlyList<ExecutionEntry>>());

    /// <summary>A static region, of its own group or of one given, open or closed.</summary>
    /// <param name="identity">The region.</param>
    /// <param name="group">Its alias group; its own by default.</param>
    /// <param name="isOpen">Whether it is open.</param>
    private static HeapRegion Static(string identity, string? group = null, bool isOpen = false) =>
        new(identity, HeapRegionKind.Static, identity, null, string.Empty, group ?? identity, isOpen, false);

    /// <summary>A hand-made scope's heap of the given regions.</summary>
    /// <param name="regions">The regions.</param>
    private static HeapSolution Heap(params HeapRegion[] regions) =>
        new(regions.ToDictionary(region => region.Identity, StringComparer.Ordinal), new Dictionary<string, MethodInstance>(), [], [], [],
            new Dictionary<string, int>(), new HashSet<string>(), [], [], (_, _) => new HashSet<string>(), (_, _) => new HashSet<string>(),
            new Dictionary<string, string>(), (_, _) => string.Empty, _ => [], _ => new HashSet<string>());

    /// <summary>What pairing did on a scope, as its last progress line counts it.</summary>
    /// <param name="Pairs">What pairing found and skipped.</param>
    /// <param name="Considered">The pairs of accesses it considered.</param>
    /// <param name="Comparisons">Its comparisons, which <c>pairing-end</c> writes: each pair skipped or suppressed once, each pair that
    /// stands once for each candidate it gives.</param>
    /// <param name="Checks">The pairs of executions it checked.</param>
    private sealed record PairingRun(PairAnalysis Pairs, long Considered, long Comparisons, long Checks);

    /// <summary>What the checks of a corpus met.</summary>
    private sealed class Seen
    {
        internal long Searched;
        internal long ReadRead;
        internal long Confined;
        internal long OtherTrees;
        internal long SameExecution;
        internal long Startup;
    }
}
