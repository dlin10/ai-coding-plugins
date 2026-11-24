using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Accesses;

/// <summary>The enumerations of the candidate index, as the blocks of pairs it yields (<see cref="CandidateIndex.Blocks"/>).</summary>
internal enum PairBlockKind
{
    /// <summary>The pairs within one resource bucket, each access with itself among them.</summary>
    Resource,

    /// <summary>The pairs of two cell buckets of one collection whose selectors may name one cell.</summary>
    Cells,

    /// <summary>The pairs of two field buckets of one region whose storage one continues the other's.</summary>
    Fields,

    /// <summary>The pairs within one region's wildcard bucket, each access with itself among them.</summary>
    WildcardWithin,

    /// <summary>The pairs of a region's wildcard bucket with one of its resource buckets.</summary>
    WildcardAcross,

    /// <summary>The pairs of a bucket of an open region with a bucket of a closed region of its alias group.</summary>
    OpenRegion
}

/// <summary>One block of the pairs the candidate index yields: every pair within one bucket, or every pair of an access of one bucket
/// with an access of another, before the enumeration drops a pair a wildcard's field exclusion rules out.</summary>
/// <param name="Kind">The enumeration the block is of.</param>
/// <param name="One">The bucket, or the first bucket of a block of two.</param>
/// <param name="Other">The second bucket of a block of two; null for the pairs within one bucket.</param>
/// <param name="Rejected">Whether the enumeration drops the whole block, because a wildcard among the two buckets' first accesses
/// excludes the other's field.</param>
internal readonly record struct PairBlock(PairBlockKind Kind, IReadOnlyList<Access> One, IReadOnlyList<Access>? Other, bool Rejected = false)
{
    /// <summary>Whether the block is the pairs within one bucket, which <see cref="CandidateIndex.Pairs"/> reports on their first
    /// access's resource and orients by position.</summary>
    internal bool IsWithin => Other is null;

    /// <summary>The pairs of accesses the block holds before any is dropped: n(n+1)/2 within one bucket of n, n·m between two.</summary>
    internal long Pairs => IsWithin ? (long)One.Count * (One.Count + 1) / 2 : (long)One.Count * Other!.Count;
}

internal sealed partial class CandidateIndex
{
    /// <summary>The blocks of the pairs <see cref="Pairs"/> yields, after the same tests of the buckets' first accesses (issue 492): the
    /// census of pairing counts a block's pairs from what its buckets add up to instead of enumerating them. The families come in the
    /// order of <see cref="Pairs"/>, and so do the blocks of each but the field buckets of a region: two of those meet only at two depths
    /// (<see cref="Meets"/>), so a bucket is compared only with the buckets of the other depths, depth by depth, and never with one of its
    /// own depth. A block of an open and a closed bucket that <see cref="Pairs"/> leaves out because a wildcard first access excludes the
    /// other's field is given as rejected; a pair the enumeration drops within a block it yields is still in that block
    /// (<see cref="Keeps"/>).</summary>
    /// <param name="tested">Told of each pair of buckets compared to find a block of two, so that a caller can bound that work; null for
    /// no one.</param>
    internal IEnumerable<PairBlock> Blocks(Action? tested = null)
    {
        foreach (var bucket in _resources)
        {
            WorkCancellation.Check();
            yield return new PairBlock(PairBlockKind.Resource, bucket, null);
        }

        foreach (var cells in _cells.Values)
        {
            WorkCancellation.Check();
            for (var first = 0; first < cells.Count; first++)
            {
                WorkCancellation.Check();
                for (var second = first + 1; second < cells.Count; second++)
                {
                    WorkCancellation.Check();
                    tested?.Invoke();
                    if (Meets(cells[first][0].Resource, cells[second][0].Resource, _heap) is not null)
                        yield return new PairBlock(PairBlockKind.Cells, cells[first], cells[second]);
                }
            }
        }

        foreach (var fields in _fields.Values)
        {
            WorkCancellation.Check();
            // Each depth's buckets by their places in the list, so that two buckets are met in the list's order, as Pairs meets them.
            var depths = new SortedDictionary<int, List<int>>();
            for (var at = 0; at < fields.Count; at++)
            {
                WorkCancellation.Check();
                var depth = Length(fields[at][0].Resource);
                if (!depths.TryGetValue(depth, out var places))
                    depths.Add(depth, places = []);
                places.Add(at);
            }

            var groups = new List<List<int>>(depths.Values);
            for (var group = 0; group < groups.Count; group++)
            {
                WorkCancellation.Check();
                for (var later = group + 1; later < groups.Count; later++)
                {
                    WorkCancellation.Check();
                    foreach (var one in groups[group])
                    {
                        WorkCancellation.Check();
                        foreach (var other in groups[later])
                        {
                            WorkCancellation.Check();
                            tested?.Invoke();
                            var (first, second) = one < other ? (fields[one], fields[other]) : (fields[other], fields[one]);
                            if (Meets(first[0].Resource, second[0].Resource, _heap) is not null)
                                yield return new PairBlock(PairBlockKind.Fields, first, second);
                        }
                    }
                }
            }
        }

        foreach (var region in _regionOrder)
        {
            WorkCancellation.Check();
            if (region.Wildcard.Count == 0)
                continue;
            yield return new PairBlock(PairBlockKind.WildcardWithin, region.Wildcard, null);
            foreach (var bucket in region.Resources)
            {
                WorkCancellation.Check();
                yield return new PairBlock(PairBlockKind.WildcardAcross, region.Wildcard, bucket);
            }
        }

        // The open regions in region order, and the closed regions of each scope and group in region order, as Pairs lists them.
        var open = new List<RegionBuckets>();
        var closedOf = new Dictionary<(string Scope, string Group), List<RegionBuckets>>();
        foreach (var region in _regionOrder)
        {
            WorkCancellation.Check();
            var heapRegion = _heap.Regions[region.RegionId];
            if (heapRegion.IsOpen)
            {
                open.Add(region);
                continue;
            }

            if (!closedOf.TryGetValue((region.Scope, heapRegion.Group), out var closed))
                closedOf.Add((region.Scope, heapRegion.Group), closed = []);
            closed.Add(region);
        }

        foreach (var openRegion in open)
        {
            WorkCancellation.Check();
            if (!closedOf.TryGetValue((openRegion.Scope, _heap.Regions[openRegion.RegionId].Group), out var closedRegions))
                continue;
            foreach (var closed in closedRegions)
            {
                WorkCancellation.Check();
                foreach (var openBucket in Filled(openRegion))
                {
                    WorkCancellation.Check();
                    foreach (var closedBucket in Filled(closed))
                    {
                        WorkCancellation.Check();
                        tested?.Invoke();
                        // Meets asks the same before anything else, so a block it leaves out for this is one it would have met.
                        if (!Keeps(openBucket[0].Resource, closedBucket[0].Resource))
                            yield return new PairBlock(PairBlockKind.OpenRegion, openBucket, closedBucket, Rejected: true);
                        else if (Meets(openBucket[0].Resource, closedBucket[0].Resource, _heap) is { Enumeration: PairEnumeration.OpenRegion })
                            yield return new PairBlock(PairBlockKind.OpenRegion, openBucket, closedBucket);
                    }
                }
            }
        }
    }

    /// <summary>A region's buckets that hold accesses, its resource buckets and then its wildcard bucket, as the open-region enumeration
    /// takes them.</summary>
    /// <param name="region">The region.</param>
    private static IEnumerable<List<Access>> Filled(RegionBuckets region)
    {
        foreach (var bucket in region.Resources)
        {
            WorkCancellation.Check();
            if (bucket.Count != 0)
                yield return bucket;
        }

        if (region.Wildcard.Count != 0)
            yield return region.Wildcard;
    }

    /// <summary>The resource buckets, one for each resource identity.</summary>
    internal IReadOnlyList<IReadOnlyList<Access>> ResourceBuckets => _resources;

    /// <summary>The wildcard bucket of each region that has wildcard accesses, with the region's resource buckets.</summary>
    internal IEnumerable<WildcardRegion> WildcardRegions()
    {
        foreach (var region in _regionOrder)
        {
            WorkCancellation.Check();
            if (region.Wildcard.Count != 0)
                yield return new WildcardRegion(region.Wildcard, region.Resources);
        }
    }

    /// <summary>Whether the enumerations keep a pair of accesses on two resources: neither is a wildcard that excludes the other's root
    /// storage field.</summary>
    /// <param name="one">One resource.</param>
    /// <param name="other">The other resource.</param>
    internal static bool Keeps(AccessResource one, AccessResource other) => WildcardCovers(one, other) && WildcardCovers(other, one);
}

/// <summary>A region's wildcard bucket and its resource buckets.</summary>
/// <param name="Wildcard">The wildcard bucket.</param>
/// <param name="Resources">The region's resource buckets.</param>
internal readonly record struct WildcardRegion(IReadOnlyList<Access> Wildcard, IReadOnlyList<IReadOnlyList<Access>> Resources);

/// <summary>What pairing decides of a pair of accesses before it asks about their executions (<c>InterproceduralPairing.Pair</c>): an
/// early skip, which checks no pair of executions; a skip after one check of the first pair of executions, whose recorded reason may be
/// that check's own; or a search for witnesses, which stops at the first witness where the pair is sufficiently protected or both sides
/// are library-internal.</summary>
internal enum PairDecision
{
    /// <summary>Two reads; no check.</summary>
    ReadRead,

    /// <summary>Neither side writes; no check.</summary>
    NoConflictingOperation,

    /// <summary>The two resources are of two scopes; no check.</summary>
    NoOverlap,

    /// <summary>One side's region is confined to one thread; one check.</summary>
    Confined,

    /// <summary>The two sides' path conditions contradict; one check.</summary>
    UnsatisfiablePath,

    /// <summary>Both sides touch fresh objects; one check.</summary>
    FreshObject,

    /// <summary>A search that one witness ends: a sufficient protection, or two library-internal sides; it keeps no witness.</summary>
    SearchOnce,

    /// <summary>A search for a witness of each pair of variants and orientation, whose witnesses pairing keeps.</summary>
    Search
}

/// <summary>How two executions, or two variants, of a pair of accesses stand to each other in the execution trees: one execution, one
/// tree, different trees with exactly one of them starting during startup, different trees both starting during startup, different
/// trees neither of which does, or, for variants only, a variant whose executions are of more than one tree.</summary>
internal enum PairRelation
{
    /// <summary>One execution (pairs of executions only).</summary>
    SameExecution,

    /// <summary>Two executions, or two variants, of one tree.</summary>
    SameTree,

    /// <summary>Two trees, exactly one of which starts during startup.</summary>
    OneStartup,

    /// <summary>Two trees that both start during startup.</summary>
    BothStartup,

    /// <summary>Two trees neither of which starts during startup.</summary>
    OtherTrees,

    /// <summary>A variant whose executions are of more than one tree, or start some during startup and some after (variants only).</summary>
    AcrossTrees
}

/// <summary>Where and when the census of pairing writes its progress: a line at most once an interval, looked for as its work
/// passes a number of steps (<see cref="CensusLimits.PollSteps"/>).</summary>
/// <param name="interval">The least time between two lines.</param>
/// <param name="report">Writes one line's details.</param>
internal sealed class CensusProgress(TimeSpan interval, Action<IReadOnlyList<string>> report)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Whether a line is due.</summary>
    internal bool Due => _clock.Elapsed >= interval;

    /// <summary>Writes a line and starts the interval anew.</summary>
    /// <param name="details">The line's details.</param>
    internal void Report(IReadOnlyList<string> details)
    {
        report(details);
        _clock.Restart();
    }
}

/// <summary>How much the census of pairing may do and keep.</summary>
/// <param name="Budget">The most work its pairs and searches parts do together, in steps (<see cref="PairingCensus.Steps"/>).</param>
/// <param name="CountsLimit">The most table entries the counts of the classes a search reached keep; past it every class's are let
/// go but those of the pair of classes being counted, and made anew when asked for.</param>
/// <param name="DecisionsLimit">The most answers each memo of decisions keeps; past it the memo forgets them all.</param>
/// <param name="PollSteps">The steps between two looks for cancellation and for a due progress line.</param>
internal sealed record CensusLimits(long Budget, long CountsLimit, int DecisionsLimit, long PollSteps);

/// <summary>
/// The census of a scope's pairing (issue 492): what <c>InterproceduralPairing.Pair</c> will enumerate and ask, counted before it asks
/// anything, for a diagnostic run. It has three parts, each a checkpoint of its own, written as soon as it is counted.
/// <list type="bullet">
/// <item>The index (<c>pairing-census-index</c>): the buckets, their sizes, the wildcard accesses and the pairs within buckets before a
/// wildcard drops any, exact, in work linear in the accesses and buckets.</item>
/// <item>The pairs (<c>pairing-census-pairs</c>): the pairs of each enumeration of the candidate index's blocks
/// (<see cref="CandidateIndex.Blocks"/>) and those a wildcard's field exclusion drops — a whole block of an open and a closed region, or
/// pairs within a block, counted from how many accesses exclude each field and how many have it. It is under the budget, which bounds
/// the comparisons of buckets that may meet.</item>
/// <item>The searches (<c>pairing-census</c>): each block's accesses split into classes that agree on everything pairing's decisions
/// before its questions read — operation, scope, confinement to one thread, the supported canonical predicates of the path conditions,
/// freshness, held protection as <see cref="PairProtection.Of"/> reads it, library-internality and what a wildcard's exclusion reads —
/// each pair of classes decided, and for the pairs it searches the pairs of executions and of variants summed in closed form from
/// counts by execution, by tree and startup and by resource object, made only for classes a search reaches; the pairs one witness ends
/// read only the variants pairing counted of each access, summed by class and by resource object once. A pair of classes adds its
/// counts whole or not at all.</item>
/// </list>
/// The pairs and searches parts share a budget of work; where it runs out the lines say so, and how many of the pairs they cover. The
/// census asks no question about a pair of executions, so it changes nothing pairing answers; what only the questions tell — the pairs
/// that stand, the witnesses and the candidates — it bounds. Every access must be in the candidates once: a list naming one twice is
/// counted in the index and pairs parts and left out of the searches.
/// </summary>
internal sealed class PairingCensus
{
    /// <summary>The fewest seconds between two progress lines of a diagnostic run.</summary>
    internal const int PROGRESS_SECONDS = 60;

    /// <summary>The environment variable that sets a diagnostic run's budget in steps, read as the census starts: a positive whole number
    /// up to <see cref="MOST_BUDGET"/> replaces the budget of <see cref="LIMITS"/>, anything else leaves it.</summary>
    internal const string BUDGET_VARIABLE = "CH_CENSUS_BUDGET";

    /// <summary>The largest budget <see cref="BUDGET_VARIABLE"/> sets, 10^15 steps, far below where the steps, which a spending adds
    /// before it compares them with the budget, and the next look at them could overflow.</summary>
    internal const long MOST_BUDGET = 1_000_000_000_000_000;

    /// <summary>The limits of a diagnostic run: 60,000 million steps, 20 million entries of counts, a million answers in each memo of
    /// decisions, and a look for cancellation and progress each 1,048,576 steps.</summary>
    internal static readonly CensusLimits LIMITS = new(60_000_000_000, 20_000_000, 1_000_000, 1 << 20);

    /// <summary>How many of the largest buckets the census names.</summary>
    private const int LARGEST = 5;

    /// <summary>The upper ends of the bucket-size classes, each excluded: 1, 2–9, 10–99, 100–999, 1,000–9,999 and the rest.</summary>
    private static readonly long[] SIZE_CLASSES = [2, 10, 100, 1_000, 10_000];

    /// <summary>A variant whose executions are of more than one tree, or of one tree but some starting during startup.</summary>
    private const int ACROSS = -1;

    /// <summary>The most entries a scratch table is cleared at; one holding more is made anew, since clearing costs its capacity.</summary>
    private const int SCRATCH_LIMIT = 256;

    /// <summary>The pairs part, as the lines and the progress name the part being counted.</summary>
    private const string PAIRS = "pairs";

    /// <summary>The searches part, as the lines and the progress name the part being counted.</summary>
    private const string SEARCHES = "searches";

    private readonly IReadOnlyList<Access> _candidates;
    private readonly CandidateIndex _index;
    private readonly Func<Access, int> _variantsOf;
    private readonly IReadOnlyDictionary<string, RegionOwnership> _ownership;
    private readonly Func<string, bool> _duringStartup;
    private readonly CensusLimits _limits;
    private readonly CensusProgress? _progress;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextPoll;
    private string _stage = PAIRS;

    private readonly long[] _pairs = new long[Enum.GetValues<PairBlockKind>().Length];
    private readonly long[] _decided = new long[Enum.GetValues<PairDecision>().Length];
    private readonly long[] _executionPairs = new long[Enum.GetValues<PairRelation>().Length];
    private readonly long[] _variantPairs = new long[Enum.GetValues<PairRelation>().Length];
    private readonly long[] _sizes = new long[SIZE_CLASSES.Length + 1];

    /// <summary>The pairs of executions and of variants of the pair of classes being counted, added to the totals when it is done.</summary>
    private readonly long[] _pendingExecutionPairs = new long[Enum.GetValues<PairRelation>().Length];
    private readonly long[] _pendingVariantPairs = new long[Enum.GetValues<PairRelation>().Length];

    /// <summary>The execution, tree and startup of each execution id, by id.</summary>
    private readonly Dictionary<string, ExecutionPlace> _places = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _trees = new(StringComparer.Ordinal);

    /// <summary>The numbers of the classes' parts, and for conditions and protection the access each number stands for.</summary>
    private readonly Dictionary<string, int> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<PredicateKey[], int> _conditions = new(ArrayByContent<PredicateKey>.INSTANCE);
    private readonly List<IReadOnlyList<PathPredicate>> _conditionsOf = [];
    private readonly Dictionary<int[], int> _protections = new(ArrayByContent<int>.INSTANCE);
    private readonly List<Access> _protectionOf = [];
    private readonly Dictionary<string, int> _protectionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string[], int> _exclusions = new(ArrayByContent<string>.INSTANCE);
    private readonly Dictionary<CoverKey, int> _covers = [];

    /// <summary>What two numbers of conditions, or of protections, decide, once asked, up to <see cref="CensusLimits.DecisionsLimit"/>,
    /// keyed by <see cref="Pair"/> and hashed by <see cref="MemoKeys"/>.</summary>
    private readonly Dictionary<long, bool> _contradicts;
    private readonly Dictionary<long, bool> _sufficient;

    /// <summary>Each bucket's classes for the searches, and each bucket's exclusions and fields for the pairs part, made once; and the
    /// classes whose counts are kept.</summary>
    private readonly Dictionary<IReadOnlyList<Access>, List<ClassSums>> _classes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<Access>, BucketCover> _bucketCovers = new(ReferenceEqualityComparer.Instance);
    private readonly List<ClassSums> _counted = [];

    /// <summary>One access's variants while it is counted: the tree key of each (<see cref="ExecutionPlace.TreeKey"/>), or
    /// <see cref="ACROSS"/>; and the ids of their path roots.</summary>
    private Dictionary<AccessVariant, int> _variantTrees = new(ReferenceEqualityComparer.Instance);
    private HashSet<string> _pathRoots = new(StringComparer.Ordinal);

    /// <summary>The held objects' ids of the second access of a protection being decided, while the first's are looked up, where the
    /// second holds at most <see cref="SCRATCH_LIMIT"/>; so it never holds more.</summary>
    private readonly HashSet<string> _heldIds = new(StringComparer.Ordinal);

    private long _countEntries;

    /// <summary>A census of a scope's pairing, before it counts anything.</summary>
    /// <param name="candidates">The scope's accesses pairing pairs: every one that is not construction-local.</param>
    /// <param name="index">The candidate index pairing enumerates.</param>
    /// <param name="variantsOf">How many variants pairing counts of a candidate's executions, one for an access built for one execution
    /// alone: what a search one witness ends reads, where it reads no execution.</param>
    /// <param name="executions">The scope's executions.</param>
    /// <param name="duringStartup">Whether an execution may start before startup ends.</param>
    /// <param name="limits">How much the census may do and keep.</param>
    /// <param name="progress">Where the pairs and searches parts write their progress, or null for nowhere.</param>
    /// <param name="memoKeys">How the memos of decisions hash their keys: <see cref="MemoKeys"/> unless given, another only for a test
    /// that compares what the hash costs with what the census counts, which it never changes.</param>
    internal PairingCensus(IReadOnlyList<Access> candidates, CandidateIndex index, Func<Access, int> variantsOf, ExecutionAnalysis executions,
                           Func<string, bool> duringStartup, CensusLimits limits, CensusProgress? progress, IEqualityComparer<long>? memoKeys = null)
    {
        _candidates = candidates;
        _index = index;
        _variantsOf = variantsOf;
        _ownership = executions.Ownership;
        _duringStartup = duringStartup;
        _limits = limits;
        _progress = progress;
        _nextPoll = limits.PollSteps;
        _contradicts = new Dictionary<long, bool>(memoKeys ?? MemoKeys.INSTANCE);
        _sufficient = new Dictionary<long, bool>(memoKeys ?? MemoKeys.INSTANCE);
        foreach (var execution in executions.Executions)
        {
            WorkCancellation.Check();
            Place(execution.Id, execution.TreeRootId);
        }
    }

    /// <summary>Writes a scope's census in a diagnostic run, under the limits of <see cref="LIMITS"/> with the budget
    /// <see cref="BUDGET_VARIABLE"/> gives, if any, and a progress line at most once a minute. Pairing runs outside the caller's
    /// cancellation, so the census takes it back for its own work (issue 492).</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="candidates">The scope's accesses pairing pairs.</param>
    /// <param name="index">The candidate index pairing enumerates.</param>
    /// <param name="variantsOf">How many variants pairing counted of a candidate's executions.</param>
    /// <param name="executions">The scope's executions.</param>
    internal static void Write(string scope, IReadOnlyList<Access> candidates, CandidateIndex index, Func<Access, int> variantsOf,
                               ExecutionAnalysis executions)
    {
        using var resumed = WorkCancellation.Resumed();
        var progress = new CensusProgress(TimeSpan.FromSeconds(PROGRESS_SECONDS), details => Report($"pairing-census-progress:{scope}", () => details));
        new PairingCensus(candidates, index, variantsOf, executions, executions.StartsDuringStartup,
                          LimitsOf(Environment.GetEnvironmentVariable(BUDGET_VARIABLE)), progress).WriteTo(scope, Report);

        static void Report(string name, Func<IEnumerable<string>> details) => DiagnosticCheckpoint.Reach(name, collect: false, details);
    }

    /// <summary>The limits of a diagnostic run with the budget a variable's text gives: <see cref="LIMITS"/> with that budget where the
    /// text is a positive whole number of steps up to <see cref="MOST_BUDGET"/>, and as they are otherwise.</summary>
    /// <param name="budget">The text of <see cref="BUDGET_VARIABLE"/>, or null where it is unset.</param>
    internal static CensusLimits LimitsOf(string? budget) =>
        long.TryParse(budget, NumberStyles.None, CultureInfo.InvariantCulture, out var steps) && steps is > 0 and <= MOST_BUDGET
            ? LIMITS with { Budget = steps }
            : LIMITS;

    /// <summary>Counts the three parts and writes each as soon as it is counted: <c>pairing-census-index:&lt;scope&gt;</c>, then
    /// <c>pairing-census-pairs:&lt;scope&gt;</c>, then <c>pairing-census:&lt;scope&gt;</c>.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="reach">Writes a checkpoint: its name and its lines.</param>
    internal void WriteTo(string scope, Action<string, Func<IEnumerable<string>>> reach)
    {
        var index = CountIndex().IndexLines();
        reach($"pairing-census-index:{scope}", () => index);
        var pairs = CountPairs().PairsLines();
        reach($"pairing-census-pairs:{scope}", () => pairs);
        var lines = CountSearches().Lines();
        reach($"pairing-census:{scope}", () => lines);
    }

    /// <summary>The accesses the census counted: the scope's candidates.</summary>
    internal long Accesses => _candidates.Count;

    /// <summary>The executions of the candidates: every one each lists, one that makes an access twice twice.</summary>
    internal long AccessExecutions { get; private set; }

    /// <summary>The candidates named more than once in the list; with any, the searches part is not counted.</summary>
    internal long DuplicateAccesses { get; private set; }

    /// <summary>The index's buckets: one for each resource identity and one for each region with wildcard accesses.</summary>
    internal long Buckets { get; private set; }

    /// <summary>The index's resource buckets.</summary>
    internal long ResourceBuckets { get; private set; }

    /// <summary>The wildcard accesses, all in their regions' wildcard buckets.</summary>
    internal long WildcardAccesses { get; private set; }

    /// <summary>The sizes of the largest buckets, largest first.</summary>
    internal IReadOnlyList<long> Largest { get; private set; } = [];

    /// <summary>How many buckets of a size class there are (<see cref="SIZE_CLASSES"/>).</summary>
    /// <param name="sizeClass">The class: 0 for size 1, then 2–9, 10–99, 100–999, 1,000–9,999 and the rest.</param>
    internal long BucketsOfSize(int sizeClass) => _sizes[sizeClass];

    /// <summary>The pairs within the resource buckets: every pair of each, none of which a wildcard can drop.</summary>
    internal long ResourcePairs { get; private set; }

    /// <summary>The pairs within the wildcard buckets before a wildcard's exclusion drops any.</summary>
    internal long WildcardWithinBeforeDrops { get; private set; }

    /// <summary>The pairs of each wildcard bucket with its region's resource buckets before a wildcard's exclusion drops any.</summary>
    internal long WildcardAcrossBeforeDrops { get; private set; }

    /// <summary>The time the index part took.</summary>
    internal long IndexMilliseconds { get; private set; }

    /// <summary>Whether the pairs part enumerated every block: the budget did not run out first.</summary>
    internal bool PairsComplete { get; private set; }

    /// <summary>The blocks the enumeration yields: the blocks of <see cref="CandidateIndex.Blocks"/> it does not reject.</summary>
    internal long Blocks { get; private set; }

    /// <summary>The blocks of an open and a closed bucket the enumeration leaves out whole, because a wildcard among their first accesses
    /// excludes the other's field.</summary>
    internal long RejectedBlocks { get; private set; }

    /// <summary>The pairs those rejected blocks hold.</summary>
    internal long PairsOfRejectedBlocks { get; private set; }

    /// <summary>The pairs the enumeration drops within the blocks it yields, because a wildcard excludes the other side's field.</summary>
    internal long DroppedInBlocks { get; private set; }

    /// <summary>The pairs of buckets the pairs part compared to find blocks of two.</summary>
    internal long BucketPairsTested { get; private set; }

    /// <summary>The steps of the pairs part.</summary>
    internal long PairsSteps { get; private set; }

    /// <summary>The time the pairs part took.</summary>
    internal long PairsMilliseconds { get; private set; }

    /// <summary>The pairs an enumeration yields: those <see cref="CandidateIndex.Pairs"/> yields, none it drops.</summary>
    /// <param name="kind">The enumeration.</param>
    internal long Pairs(PairBlockKind kind) => _pairs[(int)kind];

    /// <summary>The pairs <see cref="CandidateIndex.Pairs"/> yields; of the blocks enumerated where the pairs part is not complete.</summary>
    internal long AllPairs => _pairs.Sum();

    /// <summary>Whether the searches part covered every pair: the budget did not run out and no access is named twice.</summary>
    internal bool Complete { get; private set; }

    /// <summary>Why the census stopped short: <c>budget</c> or <c>duplicates</c>; empty where it covered every pair.</summary>
    internal string Stopped { get; private set; } = string.Empty;

    /// <summary>The blocks the searches part finished.</summary>
    internal long BlocksCovered { get; private set; }

    /// <summary>The pairs yielded that the searches part decided: of <see cref="AllPairs"/>, all of them where it is complete.</summary>
    internal long PairsCovered { get; private set; }

    /// <summary>The work the pairs and searches parts did, in steps: a pair of buckets compared, a block counted, an access put in a
    /// summary or a class, a field, exclusion, predicate, held object or holding read, a sort of n items n·(log₂ n + 1), a pair of
    /// classes decided, a memo of decisions asked anew for each pair of what it compares, an execution of an access read, an entry of a
    /// table made or visited.</summary>
    internal long Steps { get; private set; }

    /// <summary>The executions of accesses it read to count the classes a search reached.</summary>
    internal long ExecutionsRead { get; private set; }

    /// <summary>The most entries of counts alive at once: those kept and the class's being made, before any is let go.</summary>
    internal long CountEntriesPeak { get; private set; }

    /// <summary>How many times a memo of decisions forgot its answers at <see cref="CensusLimits.DecisionsLimit"/>.</summary>
    internal long DecisionClears { get; private set; }

    /// <summary>The capacity of the set the decisions of protection share (<see cref="_heldIds"/>), which every clearing sweeps: what a
    /// test reads to see a long list of held objects leave it small.</summary>
    internal int HeldIdsCapacity => _heldIds.EnsureCapacity(0);

    /// <summary>The time the searches part took.</summary>
    internal long SearchMilliseconds { get; private set; }

    /// <summary>The pairs covered that pairing decides so before it asks about their executions.</summary>
    /// <param name="decision">The decision.</param>
    internal long Decided(PairDecision decision) => _decided[(int)decision];

    /// <summary>The pairs of executions of the covered pairs pairing searches and keeps witnesses of: for a pair of two accesses every
    /// execution of one with every execution of the other, for an access with itself each pair of its executions once and each
    /// execution with itself.</summary>
    internal long SearchExecutionPairs => _executionPairs.Sum();

    /// <summary>Those pairs of executions by how they stand to each other.</summary>
    /// <param name="relation">The relation; never <see cref="PairRelation.AcrossTrees"/>.</param>
    internal long SearchExecutionPairsOf(PairRelation relation) => _executionPairs[(int)relation];

    /// <summary>The pairs of executions of the covered pairs whose search one witness ends.</summary>
    internal long OnceExecutionPairs { get; private set; }

    /// <summary>The pairs of variants of the covered pairs pairing searches and keeps witnesses of: for a pair of two accesses every
    /// variant of one with every variant of the other, for an access with itself each pair of its variants once and each variant with
    /// itself.</summary>
    internal long SearchVariantPairs => _variantPairs.Sum();

    /// <summary>Those pairs of variants by how they stand to each other.</summary>
    /// <param name="relation">The relation; never <see cref="PairRelation.SameExecution"/>.</param>
    internal long SearchVariantPairsOf(PairRelation relation) => _variantPairs[(int)relation];

    /// <summary>The searches pairing makes on the covered pairs it searches, exactly: a call of its search for the first pair of
    /// executions (<c>ExecutionPairs.Witnesses</c>) for each pair of the two sides' variants — of an access with itself each pair once
    /// and each variant with itself — and each orientation, two where the pair is reported on its earlier execution's resource and
    /// the two sides hold different resource objects.</summary>
    internal long Searches { get; private set; }

    /// <summary>The witnesses those searches keep at most: each search gives at most one.</summary>
    internal long WitnessesAtMost => Searches;

    /// <summary>The searches pairing makes on the covered pairs one witness ends at least: the first, of each such pair.</summary>
    internal long SearchOnceSearchesAtLeast => Decided(PairDecision.SearchOnce);

    /// <summary>The searches it makes on them at most: every pair of variants and orientation, as <see cref="Searches"/> counts them,
    /// where no search passes.</summary>
    internal long SearchOnceSearchesAtMost { get; private set; }

    /// <summary>The most path conditions one candidate holds: the length of the longest list a contradiction is asked of, which one
    /// call compares with another's predicate by predicate between two looks for cancellation.</summary>
    internal long MostConditions { get; private set; }

    /// <summary>The most held objects one candidate holds, whose holdings a sufficiency is asked of.</summary>
    internal long MostHeldObjects { get; private set; }

    /// <summary>The distinct pairs of path roots of those pairs, once for each orientation: a pair of two accesses gives every root of one
    /// with every root of the other, an access with itself each unordered pair of its roots. A candidate is one pair of roots and one
    /// orientation, so this bounds the candidates of the covered pairs from above.</summary>
    internal long CandidatesAtMost { get; private set; }

    /// <summary>The checks of pairs of executions pairing makes on the covered pairs at least (its <c>executionPairs</c> counter, which
    /// counts each pair of executions it decides, timing asked or not): one for each pair it skips after one check, and the first pair of
    /// executions of each pair it searches, whether one witness ends the search or not.</summary>
    internal long ExecutionPairChecksAtLeast =>
        Decided(PairDecision.Confined) + Decided(PairDecision.UnsatisfiablePath) + Decided(PairDecision.FreshObject) +
        Decided(PairDecision.SearchOnce) + Decided(PairDecision.Search);

    /// <summary>The checks it makes at most: one for each pair it skips after one check, and every pair of executions of each pair it
    /// searches, those one witness ends included, none twice.</summary>
    internal long ExecutionPairChecksAtMost =>
        Decided(PairDecision.Confined) + Decided(PairDecision.UnsatisfiablePath) + Decided(PairDecision.FreshObject) +
        OnceExecutionPairs + SearchExecutionPairs;

    /// <summary>The index part as the lines of its checkpoint: exact counts.</summary>
    internal IReadOnlyList<string> IndexLines() =>
    [
        $"buckets={Buckets} resourceBuckets={ResourceBuckets} wildcardRegions={Buckets - ResourceBuckets} wildcardAccesses={WildcardAccesses} " +
        $"accesses={Accesses} accessExecutions={AccessExecutions} duplicateAccesses={DuplicateAccesses} largest={string.Join(",", Largest)} " +
        $"sizes=1:{_sizes[0]},2-9:{_sizes[1]},10-99:{_sizes[2]},100-999:{_sizes[3]},1000-9999:{_sizes[4]},10000+:{_sizes[5]}",
        $"withinBuckets resource={ResourcePairs} wildcardWithinBeforeDrops={WildcardWithinBeforeDrops} " +
        $"wildcardAcrossBeforeDrops={WildcardAcrossBeforeDrops}",
        $"candidates mostConditions={MostConditions} mostHeldObjects={MostHeldObjects}",
        $"time ms={IndexMilliseconds}"
    ];

    /// <summary>The pairs part as the lines of its checkpoint: exact counts where it is complete, else of the blocks it enumerated.</summary>
    internal IReadOnlyList<string> PairsLines() =>
    [
        $"pairs complete={Flag(PairsComplete)} {PairTotals()} bucketPairsTested={BucketPairsTested} steps={PairsSteps}",
        $"time ms={PairsMilliseconds}"
    ];

    /// <summary>The searches part as the lines of its checkpoint: exact counts over the pairs it covered, save the bounds named so; each
    /// count line says whether it is over all the pairs or over the ones covered, of how many.</summary>
    internal IReadOnlyList<string> Lines()
    {
        var total = PairsComplete ? $"{AllPairs}" : "unknown";
        var blocks = PairsComplete ? $"{Blocks}" : "unknown";
        var stopped = Stopped.Length == 0 ? "none" : Stopped == "budget" ? $"budget in={_stage}" : Stopped;
        return
        [
            $"coverage complete={Flag(Complete)} stopped={stopped} blocks={BlocksCovered}/{blocks} pairs={PairsCovered}/{total} steps={Steps} " +
            $"budget={_limits.Budget} executionsRead={ExecutionsRead} countEntriesPeak={CountEntriesPeak} decisionClears={DecisionClears}",
            .. CountLines(Complete ? "over=all" : $"over=covered:{PairsCovered}/{total}"),
            $"time ms={SearchMilliseconds}"
        ];
    }

    /// <summary>The pairs of each enumeration and those dropped, of the blocks counted so far, as one line's fields.</summary>
    private string PairTotals() =>
        $"total={AllPairs} resource={Pairs(PairBlockKind.Resource)} cells={Pairs(PairBlockKind.Cells)} fields={Pairs(PairBlockKind.Fields)} " +
        $"wildcardWithin={Pairs(PairBlockKind.WildcardWithin)} wildcardAcross={Pairs(PairBlockKind.WildcardAcross)} " +
        $"openRegion={Pairs(PairBlockKind.OpenRegion)} blocks={Blocks} rejectedBlocks={RejectedBlocks} pairsOfRejectedBlocks={PairsOfRejectedBlocks} " +
        $"droppedInBlocks={DroppedInBlocks}";

    /// <summary>The count lines of the searches part, over the pairs of classes counted whole so far.</summary>
    /// <param name="over">What the lines are over: all the pairs, or the ones covered, of how many.</param>
    private IReadOnlyList<string> CountLines(string over) =>
    [
        $"decided {over} noCheckReadRead={Decided(PairDecision.ReadRead)} noCheckNoConflictingOperation={Decided(PairDecision.NoConflictingOperation)} " +
        $"noCheckNoOverlap={Decided(PairDecision.NoOverlap)} oneCheckConfined={Decided(PairDecision.Confined)} " +
        $"oneCheckUnsatisfiablePath={Decided(PairDecision.UnsatisfiablePath)} oneCheckFreshObject={Decided(PairDecision.FreshObject)} " +
        $"searchOnce={Decided(PairDecision.SearchOnce)} search={Decided(PairDecision.Search)}",
        $"executionPairs {over} search={SearchExecutionPairs} sameExecution={SearchExecutionPairsOf(PairRelation.SameExecution)} " +
        $"sameTree={SearchExecutionPairsOf(PairRelation.SameTree)} oneStartup={SearchExecutionPairsOf(PairRelation.OneStartup)} " +
        $"bothStartup={SearchExecutionPairsOf(PairRelation.BothStartup)} otherTrees={SearchExecutionPairsOf(PairRelation.OtherTrees)} " +
        $"searchOnce={OnceExecutionPairs}",
        $"variantPairs {over} search={SearchVariantPairs} sameTree={SearchVariantPairsOf(PairRelation.SameTree)} " +
        $"oneStartup={SearchVariantPairsOf(PairRelation.OneStartup)} bothStartup={SearchVariantPairsOf(PairRelation.BothStartup)} " +
        $"otherTrees={SearchVariantPairsOf(PairRelation.OtherTrees)} acrossTrees={SearchVariantPairsOf(PairRelation.AcrossTrees)}",
        $"bounds {over} executionPairChecksAtLeast={ExecutionPairChecksAtLeast} executionPairChecksAtMost={ExecutionPairChecksAtMost} " +
        $"witnessesAtMost={WitnessesAtMost} candidatesAtMost={CandidatesAtMost}",
        $"searches {over} search={Searches} searchOnceAtLeast={SearchOnceSearchesAtLeast} searchOnceAtMost={SearchOnceSearchesAtMost}"
    ];

    /// <summary>Counts the index part: the candidates, the buckets, their sizes and the pairs within buckets before any is dropped.</summary>
    internal PairingCensus CountIndex()
    {
        var clock = Stopwatch.StartNew();
        var seen = new HashSet<Access>(ReferenceEqualityComparer.Instance);
        foreach (var access in _candidates)
        {
            WorkCancellation.Check();
            AccessExecutions += access.ExecutionCount;
            MostConditions = Math.Max(MostConditions, access.Conditions.Count);
            MostHeldObjects = Math.Max(MostHeldObjects, access.HeldProtectionIds.Count);
            if (!seen.Add(access))
                DuplicateAccesses++;
        }

        var largest = new List<long>(LARGEST + 1);
        foreach (var bucket in _index.ResourceBuckets)
        {
            WorkCancellation.Check();
            ResourceBuckets++;
            ResourcePairs += Triangle(bucket.Count);
            AddSize(bucket.Count, largest);
        }

        foreach (var region in _index.WildcardRegions())
        {
            WorkCancellation.Check();
            WildcardAccesses += region.Wildcard.Count;
            WildcardWithinBeforeDrops += Triangle(region.Wildcard.Count);
            AddSize(region.Wildcard.Count, largest);
            foreach (var bucket in region.Resources)
            {
                WorkCancellation.Check();
                WildcardAcrossBeforeDrops += (long)region.Wildcard.Count * bucket.Count;
            }
        }

        Buckets = _sizes.Sum();
        Largest = largest;
        IndexMilliseconds = clock.ElapsedMilliseconds;
        return this;
    }

    /// <summary>Counts the pairs part, under the budget: each block of the index's enumeration with the pairs it holds and those a
    /// wildcard's exclusion drops from it, each block whole or not at all.</summary>
    internal PairingCensus CountPairs()
    {
        var clock = Stopwatch.StartNew();
        var before = Steps;
        _stage = PAIRS;
        try
        {
            foreach (var block in _index.Blocks(Tested))
            {
                WorkCancellation.Check();
                Spend(1);
                if (block.Rejected)
                {
                    RejectedBlocks++;
                    PairsOfRejectedBlocks += block.Pairs;
                    continue;
                }

                var dropped = Dropped(block);
                Blocks++;
                _pairs[(int)block.Kind] += block.Pairs - dropped;
                DroppedInBlocks += dropped;
            }

            PairsComplete = true;
        }
        catch (BudgetSpent)
        {
            Stopped = "budget";
        }

        PairsSteps = Steps - before;
        PairsMilliseconds = clock.ElapsedMilliseconds;
        return this;
    }

    /// <summary>Counts the searches part, under what is left of the budget: what pairing decides of each covered pair before its
    /// questions, and of the pairs it searches their pairs of executions and of variants and the bounds on what the search finds. It is
    /// not counted where the pairs part did not finish or an access is named twice.</summary>
    internal PairingCensus CountSearches()
    {
        var clock = Stopwatch.StartNew();
        if (!PairsComplete)
        {
            SearchMilliseconds = clock.ElapsedMilliseconds;
            return this;
        }

        if (DuplicateAccesses != 0)
        {
            Stopped = "duplicates";
            SearchMilliseconds = clock.ElapsedMilliseconds;
            return this;
        }

        _stage = SEARCHES;
        try
        {
            foreach (var block in _index.Blocks(Tested))
            {
                WorkCancellation.Check();
                if (block.Rejected)
                    continue;
                CountBlock(block);
                BlocksCovered++;
            }

            Complete = true;
        }
        catch (BudgetSpent)
        {
            Stopped = "budget";
        }

        SearchMilliseconds = clock.ElapsedMilliseconds;
        return this;
    }

    /// <summary>Spends work, and every <see cref="CensusLimits.PollSteps"/> steps looks for cancellation and writes a progress line where
    /// one is due.</summary>
    /// <param name="steps">The steps.</param>
    /// <exception cref="BudgetSpent">The budget ran out.</exception>
    private void Spend(long steps)
    {
        Steps += steps;
        if (Steps > _limits.Budget)
            throw new BudgetSpent();
        if (Steps < _nextPoll)
            return;
        _nextPoll = Steps + _limits.PollSteps;
        WorkCancellation.Check();
        if (_progress?.Due == true)
            _progress.Report(ProgressLines());
    }

    /// <summary>Spends the step of a pair of buckets compared, which the pairs part counts.</summary>
    private void Tested()
    {
        if (_stage == PAIRS)
            BucketPairsTested++;
        Spend(1);
    }

    /// <summary>Counts one block's pairs of classes.</summary>
    /// <param name="block">The block.</param>
    private void CountBlock(PairBlock block)
    {
        var ones = Classes(block.One);
        var others = block.Other is null ? ones : Classes(block.Other);
        for (var first = 0; first < ones.Count; first++)
        {
            WorkCancellation.Check();
            for (var second = block.IsWithin ? first : 0; second < others.Count; second++)
            {
                WorkCancellation.Check();
                CountClasses(block, ones[first], others[second], block.IsWithin && first == second);
            }
        }
    }

    /// <summary>Counts the pairs of a block between two classes of accesses, or within one class of a bucket, and adds them to the
    /// totals only once they are all counted, so that a pair of classes the budget interrupts adds nothing.</summary>
    /// <param name="block">The block.</param>
    /// <param name="one">One class.</param>
    /// <param name="other">The other class, or the same one.</param>
    /// <param name="self">Whether the two are one class of one bucket, whose pairs are each pair of its accesses once and each access
    /// with itself.</param>
    private void CountClasses(PairBlock block, ClassSums one, ClassSums other, bool self)
    {
        Spend(1);
        if (!CandidateIndex.Keeps(one.Representative.Resource, other.Representative.Resource))
            return;
        var pairs = self ? Triangle(one.Accesses) : one.Accesses * other.Accesses;
        var decision = Decide(one.Key, other.Key);
        var (once, searches, candidates) = (0L, 0L, 0L);
        Array.Clear(_pendingExecutionPairs);
        Array.Clear(_pendingVariantPairs);
        if (decision == PairDecision.SearchOnce)
        {
            searches = OnceSearches(one, other, self, block.IsWithin);
            once = self ? Triangle(one.Copies) : one.Copies * other.Copies;
        }
        else if (decision == PairDecision.Search)
        {
            var oneCounts = CountsOf(one, null);
            var otherCounts = self ? oneCounts : CountsOf(other, one);
            (searches, candidates) = Oriented(oneCounts, otherCounts, self, block.IsWithin);
            CountExecutionPairs(oneCounts, otherCounts, self);
            CountVariantPairs(oneCounts, otherCounts, self);
        }

        PairsCovered += pairs;
        _decided[(int)decision] += pairs;
        OnceExecutionPairs += once;
        if (decision == PairDecision.Search)
        {
            Searches += searches;
            CandidatesAtMost += candidates;
        }
        else
        {
            SearchOnceSearchesAtMost += searches;
        }
        // bounded: one entry for each of the six relations.
        for (var relation = 0; relation < _executionPairs.Length; relation++)
        {
            _executionPairs[relation] += _pendingExecutionPairs[relation];
            _variantPairs[relation] += _pendingVariantPairs[relation];
        }
    }

    /// <summary>What pairing decides of a pair of classes' accesses before it asks about their executions, in the order its filters run.</summary>
    /// <param name="one">One class's key.</param>
    /// <param name="other">The other's.</param>
    private PairDecision Decide(ClassKey one, ClassKey other)
    {
        if (one.Operation == AccessOperation.Read && other.Operation == AccessOperation.Read)
            return PairDecision.ReadRead;
        if (!one.Operation.Conflicts() && !other.Operation.Conflicts())
            return PairDecision.NoConflictingOperation;
        if (one.Scope != other.Scope)
            return PairDecision.NoOverlap;
        if (one.Confined || other.Confined)
            return PairDecision.Confined;
        if (Contradicts(one.Conditions, other.Conditions))
            return PairDecision.UnsatisfiablePath;
        if (one.Fresh && other.Fresh)
            return PairDecision.FreshObject;
        return Sufficient(one.Protection, other.Protection) || one.LibraryInternal && other.LibraryInternal
            ? PairDecision.SearchOnce
            : PairDecision.Search;
    }

    /// <summary>Whether two numbers of conditions contradict, as <see cref="PathConditions.Contradict"/> decides it of the conditions
    /// they stand for; a new answer costs a step for each pair of their predicates, spent before the call, which itself looks for no
    /// cancellation: one call compares at most that many pairs between two looks (the longest list is the index part's
    /// <see cref="MostConditions"/>). An empty list contradicts nothing, and the other is not walked.</summary>
    /// <param name="one">One number.</param>
    /// <param name="other">The other.</param>
    private bool Contradicts(int one, int other)
    {
        var key = Pair(one, other);
        if (_contradicts.TryGetValue(key, out var known))
            return known;
        Spend(1 + (long)_conditionsOf[one].Count * _conditionsOf[other].Count);
        var answer = _conditionsOf[one].Count != 0 && _conditionsOf[other].Count != 0 &&
                     PathConditions.Contradict(_conditionsOf[one], _conditionsOf[other]);
        Remember(_contradicts, key, answer);
        return answer;
    }

    /// <summary>Whether two numbers of held protection make a sufficient protection, as <see cref="PairProtection.Of"/> decides it of
    /// the accesses they stand for; a new answer costs a step and the work <see cref="SpendHoldings"/> spends, before the call.</summary>
    /// <param name="one">One number.</param>
    /// <param name="other">The other.</param>
    private bool Sufficient(int one, int other)
    {
        var key = Pair(one, other);
        if (_sufficient.TryGetValue(key, out var known))
            return known;
        Spend(1);
        SpendHoldings(_protectionOf[one], _protectionOf[other]);
        var answer = PairProtection.Of(_protectionOf[one], _protectionOf[other]) == PairProtection.SUFFICIENT;
        Remember(_sufficient, key, answer);
        return answer;
    }

    /// <summary>Spends the work of <see cref="PairProtection.Of"/> on two accesses, as it does it: a step for each held object's id of
    /// either, spent before the ids they share are found with a set, as it intersects them; then, for each id they share, once however
    /// often the first lists it, a step for each pair of holdings it compares on that id, a missing holding being one. The set is
    /// <see cref="_heldIds"/> for a second side of at most <see cref="SCRATCH_LIMIT"/> ids and one of its own for a longer list: the
    /// shared one, which the lookups below leave empty, would otherwise keep a long list's capacity, and every later clearing would
    /// sweep it, a cost no step pays.</summary>
    /// <param name="first">One access.</param>
    /// <param name="second">The other.</param>
    private void SpendHoldings(Access first, Access second)
    {
        Spend((long)first.HeldProtectionIds.Count + second.HeldProtectionIds.Count);
        var ids = second.HeldProtectionIds.Count > SCRATCH_LIMIT ? new HashSet<string>(StringComparer.Ordinal) : _heldIds;
        ids.Clear();
        foreach (var id in second.HeldProtectionIds)
        {
            WorkCancellation.Check();
            ids.Add(id);
        }

        foreach (var id in first.HeldProtectionIds)
        {
            WorkCancellation.Check();
            if (ids.Remove(id))
                Spend(Holdings(first, id) * Holdings(second, id));
        }
    }

    /// <summary>How many holdings <see cref="PairProtection.Of"/> reads of an access's held object: its own, or one where it has none.</summary>
    /// <param name="access">The access.</param>
    /// <param name="id">The held object's id.</param>
    private static long Holdings(Access access, string id) =>
        access.HeldProtections.TryGetValue(id, out var held) && held.Count != 0 ? held.Count : 1;

    /// <summary>The steps a sort of so many items is paid: each item once for each halving of their number.</summary>
    /// <param name="items">The items.</param>
    private static long SortWork(int items) => items < 2 ? 0 : (long)items * (BitOperations.Log2((uint)items) + 1);

    /// <summary>Keeps an answer in a memo of decisions, which forgets all it holds once it holds <see cref="CensusLimits.DecisionsLimit"/>.</summary>
    /// <param name="memo">The memo.</param>
    /// <param name="key">The pair of numbers asked.</param>
    /// <param name="answer">The answer.</param>
    private void Remember(Dictionary<long, bool> memo, long key, bool answer)
    {
        if (memo.Count >= _limits.DecisionsLimit)
        {
            memo.Clear();
            DecisionClears++;
        }

        memo.Add(key, answer);
    }

    /// <summary>Counts the pairs of executions of a searched pair of classes by how the two executions stand to each other.</summary>
    /// <param name="one">One class's counts.</param>
    /// <param name="other">The other's, or the same.</param>
    /// <param name="self">Whether the two are one class of one bucket.</param>
    private void CountExecutionPairs(Counts one, Counts other, bool self)
    {
        var same = Dot(one.CopiesByExecution, other.CopiesByExecution);
        var tree = SameTree(one.CopiesByTree, other.CopiesByTree) - same;
        Split(_pendingExecutionPairs, one.CopiesByTree, other.CopiesByTree, one.CopiesStarting, other.CopiesStarting, one.Copies, other.Copies,
              self, same, tree, one.Copies, PairRelation.SameExecution);
    }

    /// <summary>Counts the pairs of variants of a searched pair of classes by how the two variants stand to each other.</summary>
    /// <param name="one">One class's counts.</param>
    /// <param name="other">The other's, or the same.</param>
    /// <param name="self">Whether the two are one class of one bucket.</param>
    private void CountVariantPairs(Counts one, Counts other, bool self)
    {
        var (oneInTree, otherInTree) = (one.Variants - one.VariantsAcrossTrees, other.Variants - other.VariantsAcrossTrees);
        var across = one.Variants * other.Variants - oneInTree * otherInTree;
        // A variant with itself is of one tree, or across trees.
        _pendingVariantPairs[(int)PairRelation.AcrossTrees] += self ? (across + one.VariantsAcrossTrees) / 2 : across;
        var tree = SameTree(one.VariantsByTree, other.VariantsByTree);
        Split(_pendingVariantPairs, one.VariantsByTree, other.VariantsByTree, one.VariantsStarting, other.VariantsStarting, oneInTree, otherInTree,
              self, 0, tree, oneInTree, PairRelation.SameTree);
    }

    /// <summary>Adds the pairs of two classes' members by relation: one member and one tree as given, and the pairs of two trees by
    /// whether each starts during startup. Of one class, the ordered pairs of its members are halved after each member's pair with
    /// itself is added once more.</summary>
    /// <param name="counts">The counts by relation.</param>
    /// <param name="oneByTree">The first class's members by tree key.</param>
    /// <param name="otherByTree">The second class's.</param>
    /// <param name="oneStarting">The first class's members of trees starting during startup.</param>
    /// <param name="otherStarting">The second class's.</param>
    /// <param name="oneInTrees">The first class's members counted by tree.</param>
    /// <param name="otherInTrees">The second class's.</param>
    /// <param name="self">Whether the two are one class of one bucket.</param>
    /// <param name="same">The ordered pairs of one member with itself.</param>
    /// <param name="tree">The ordered pairs of two members of one tree, those of one member left out.</param>
    /// <param name="diagonal">The pairs of a member with itself, which one class's halving counts once more.</param>
    /// <param name="diagonalRelation">How a member stands to itself.</param>
    private void Split(long[] counts, Dictionary<int, long> oneByTree, Dictionary<int, long> otherByTree, long oneStarting, long otherStarting,
                       long oneInTrees, long otherInTrees, bool self, long same, long tree, long diagonal, PairRelation diagonalRelation)
    {
        var (onePlain, otherPlain) = (oneInTrees - oneStarting, otherInTrees - otherStarting);
        var bothStartup = oneStarting * otherStarting - Flagged(oneByTree, otherByTree, true, true);
        var otherTrees = onePlain * otherPlain - Flagged(oneByTree, otherByTree, false, false);
        var oneStartup = oneStarting * otherPlain + onePlain * otherStarting - Flagged(oneByTree, otherByTree, true, false) -
                         Flagged(oneByTree, otherByTree, false, true);
        var (halve, extra) = (self ? 2 : 1, self ? diagonal : 0);
        counts[(int)PairRelation.SameExecution] += (same + (diagonalRelation == PairRelation.SameExecution ? extra : 0)) / halve;
        counts[(int)PairRelation.SameTree] += (tree + (diagonalRelation == PairRelation.SameTree ? extra : 0)) / halve;
        counts[(int)PairRelation.OneStartup] += oneStartup / halve;
        counts[(int)PairRelation.BothStartup] += bothStartup / halve;
        counts[(int)PairRelation.OtherTrees] += otherTrees / halve;
    }

    /// <summary>The searches and the pairs of path roots of a searched pair of classes, with the second orientation where pairing
    /// searches one: a pair within one bucket whose two sides hold different resource objects. The resource objects the two share are
    /// found by looking the smaller table's up in the larger one, once for both.</summary>
    /// <param name="one">One class's counts.</param>
    /// <param name="other">The other's, or the same.</param>
    /// <param name="self">Whether the two are one class of one bucket.</param>
    /// <param name="within">Whether the pairs are within one bucket.</param>
    private (long Searches, long Roots) Oriented(Counts one, Counts other, bool self, bool within)
    {
        if (!within)
            return (one.Variants * other.Variants, one.Roots * other.Roots);
        var (small, large) = one.ByResource.Count <= other.ByResource.Count ? (one.ByResource, other.ByResource) : (other.ByResource, one.ByResource);
        var (sameVariants, sameRoots) = (0L, 0L);
        foreach (var (resource, sums) in small)
        {
            WorkCancellation.Check();
            Spend(1);
            if (!large.TryGetValue(resource, out var otherSums))
                continue;
            sameVariants += sums.Variants * otherSums.Variants;
            sameRoots += sums.Roots * otherSums.Roots;
        }

        return (Orient(one.Variants, other.Variants, sameVariants, self), Orient(one.Roots, other.Roots, sameRoots, self));
    }

    /// <summary>The ordered members of two classes within one bucket: two members on one resource object, and a member with itself, give
    /// one orientation; two on different objects give two. Of one class, each pair of members is taken once and each member with itself.</summary>
    /// <param name="first">The first class's members.</param>
    /// <param name="second">The second class's.</param>
    /// <param name="sameObject">The pairs of their members on one resource object, ordered.</param>
    /// <param name="self">Whether the two are one class.</param>
    private static long Orient(long first, long second, long sameObject, bool self) =>
        self ? (2 * first * first - sameObject + first) / 2 : 2 * first * second - sameObject;

    /// <summary>The searches of a pair of classes one witness ends, where none ends them: every pair of their variants, with the second
    /// orientation as <see cref="Oriented"/> takes it, from the variants pairing counted of each access, summed by class as the classes
    /// are made and by resource object once (<see cref="VariantsByResource"/>), so that no execution is read for them.</summary>
    /// <param name="one">One class.</param>
    /// <param name="other">The other, or the same.</param>
    /// <param name="self">Whether the two are one class of one bucket.</param>
    /// <param name="within">Whether the pairs are within one bucket.</param>
    private long OnceSearches(ClassSums one, ClassSums other, bool self, bool within)
    {
        if (!within)
            return one.Variants * other.Variants;
        var (oneByResource, otherByResource) = (VariantsByResource(one), VariantsByResource(other));
        var (small, large) = oneByResource.Count <= otherByResource.Count ? (oneByResource, otherByResource) : (otherByResource, oneByResource);
        var sameObject = 0L;
        foreach (var (resource, variants) in small)
        {
            WorkCancellation.Check();
            Spend(1);
            if (large.TryGetValue(resource, out var otherVariants))
                sameObject += variants * otherVariants;
        }

        return Orient(one.Variants, other.Variants, sameObject, self);
    }

    /// <summary>A class's variants by resource object, summed when a search one witness ends first reaches the class within its bucket,
    /// a step for each access, and kept: they hold an entry at most for each access of the class.</summary>
    /// <param name="sums">The class.</param>
    private Dictionary<AccessResource, long> VariantsByResource(ClassSums sums)
    {
        if (sums.VariantsByResource is { } known)
            return known;
        var byResource = new Dictionary<AccessResource, long>(ReferenceEqualityComparer.Instance);
        foreach (var access in sums.Members)
        {
            WorkCancellation.Check();
            Spend(1);
            CollectionsMarshal.GetValueRefOrAddDefault(byResource, access.Resource, out _) += _variantsOf(access);
        }

        return sums.VariantsByResource = byResource;
    }

    /// <summary>The pairs a wildcard's exclusion drops from a block, counted from how many of each bucket's wildcards exclude each field
    /// and how many of its accesses have each field as their root: a pair is dropped where either side is a wildcard that excludes the
    /// other's root field. A pair of two wildcards that both have root fields can be dropped both ways; those are counted class by class
    /// and taken off once, so that such a pair is dropped once.</summary>
    /// <param name="block">The block.</param>
    private long Dropped(PairBlock block)
    {
        // The index puts every wildcard in its region's wildcard bucket, so a block of resource, cell or field buckets has none.
        if (block.Kind is PairBlockKind.Resource or PairBlockKind.Cells or PairBlockKind.Fields)
            return 0;
        var one = CoverOfBucket(block.One);
        // Of one bucket the ordered pairs, an access with itself among them, count a pair of two accesses dropped both ways twice. A
        // pair dropped both ways is among the pairs dropped either way, so where none is dropped one way none is dropped both ways.
        if (block.Other is null)
        {
            var ordered = one.HasWildcard ? Excluded(one, one) : 0;
            return ordered == 0 ? 0 : ordered - BothWays(one, one, within: true);
        }

        var other = CoverOfBucket(block.Other);
        if (!one.HasWildcard && !other.HasWildcard)
            return 0;
        var (forward, backward) = (Excluded(one, other), Excluded(other, one));
        return forward == 0 || backward == 0 ? forward + backward : forward + backward - BothWays(one, other, within: false);
    }

    /// <summary>The ordered pairs of an access of one bucket that is a wildcard excluding the root field of an access of the other.</summary>
    /// <param name="wildcards">The bucket of the wildcard side.</param>
    /// <param name="fields">The bucket of the other side.</param>
    private long Excluded(BucketCover wildcards, BucketCover fields)
    {
        var (small, large) = wildcards.Excluding.Count <= fields.Fields.Count
            ? (wildcards.Excluding, fields.Fields)
            : (fields.Fields, wildcards.Excluding);
        var sum = 0L;
        foreach (var (field, count) in small)
        {
            WorkCancellation.Check();
            Spend(1);
            if (large.TryGetValue(field, out var otherCount))
                sum += count * otherCount;
        }

        return sum;
    }

    /// <summary>The pairs dropped both ways: two wildcards with root fields, each excluding the other's; of one bucket, each unordered
    /// pair of two accesses once. Each class of one side looks up, for each field it excludes, the classes of the other side whose root
    /// field it is, so that only the pairs dropped one way are tried the other way.</summary>
    /// <param name="one">One bucket.</param>
    /// <param name="other">The other bucket, or the same one.</param>
    /// <param name="within">Whether the two are one bucket.</param>
    private long BothWays(BucketCover one, BucketCover other, bool within)
    {
        if (one.FieldedWildcards.Count == 0 || other.FieldedWildcards.Count == 0)
            return 0;
        var sum = 0L;
        for (var first = 0; first < one.FieldedWildcards.Count; first++)
        {
            WorkCancellation.Check();
            var a = one.FieldedWildcards[first];
            foreach (var field in a.Representative.Resource.WildcardExclusions)
            {
                WorkCancellation.Check();
                Spend(1);
                if (!other.FieldedByRoot.TryGetValue(field, out var places))
                    continue;
                foreach (var second in places)
                {
                    WorkCancellation.Check();
                    Spend(1);
                    var b = other.FieldedWildcards[second];
                    if (within && second < first || !Excludes(b.Representative.Resource, a.Representative.Resource))
                        continue;
                    sum += within && second == first ? a.Count * (a.Count - 1) / 2 : a.Count * b.Count;
                }
            }
        }

        return sum;
    }

    /// <summary>Whether a resource is a wildcard that excludes the other's root storage field.</summary>
    /// <param name="wildcard">The possible wildcard.</param>
    /// <param name="other">The other resource.</param>
    private static bool Excludes(AccessResource wildcard, AccessResource other) =>
        wildcard.IsWildcard && RootField(other) is { Length: > 0 } field && wildcard.WildcardExclusions.Contains(field);

    /// <summary>A bucket's wildcards and fields as the drops of its blocks read them, made once for a bucket.</summary>
    /// <param name="bucket">The bucket.</param>
    private BucketCover CoverOfBucket(IReadOnlyList<Access> bucket)
    {
        if (_bucketCovers.TryGetValue(bucket, out var known))
            return known;
        var cover = new BucketCover();
        var fielded = new Dictionary<int, CoverClass>();
        foreach (var access in bucket)
        {
            WorkCancellation.Check();
            Spend(1);
            var resource = access.Resource;
            var field = RootField(resource);
            if (field.Length != 0)
                CollectionsMarshal.GetValueRefOrAddDefault(cover.Fields, field, out _)++;
            if (!resource.IsWildcard)
                continue;
            cover.HasWildcard = true;
            foreach (var excluded in resource.WildcardExclusions)
            {
                WorkCancellation.Check();
                Spend(1);
                CollectionsMarshal.GetValueRefOrAddDefault(cover.Excluding, excluded, out _)++;
            }

            // A wildcard that excludes nothing drops no pair either way.
            if (field.Length == 0 || resource.WildcardExclusions.Count == 0)
                continue;
            ref var sums = ref CollectionsMarshal.GetValueRefOrAddDefault(fielded, CoverOf(resource), out var met);
            sums = met ? sums with { Count = sums.Count + 1 } : new CoverClass(access, 1);
        }

        foreach (var sums in fielded.Values)
        {
            WorkCancellation.Check();
            ref var places = ref CollectionsMarshal.GetValueRefOrAddDefault(cover.FieldedByRoot, RootField(sums.Representative.Resource), out _);
            (places ??= []).Add(cover.FieldedWildcards.Count);
            cover.FieldedWildcards.Add(sums);
        }

        _bucketCovers.Add(bucket, cover);
        return cover;
    }

    /// <summary>The key of a resource's root storage field, as a wildcard's exclusion reads it; empty for none.</summary>
    /// <param name="resource">The resource.</param>
    private static string RootField(AccessResource resource) =>
        resource.Storage?.Fields.FirstOrDefault() is { } root ? FieldSlot.Key(root) : string.Empty;

    /// <summary>A bucket's accesses in classes that agree on what pairing decides before its questions read, made once.</summary>
    /// <param name="bucket">The bucket.</param>
    private List<ClassSums> Classes(IReadOnlyList<Access> bucket)
    {
        if (_classes.TryGetValue(bucket, out var known))
            return known;
        var classes = new Dictionary<ClassKey, ClassSums>();
        var list = new List<ClassSums>();
        foreach (var access in bucket)
        {
            WorkCancellation.Check();
            Spend(1);
            var key = KeyOf(access);
            if (!classes.TryGetValue(key, out var sums))
            {
                classes.Add(key, sums = new ClassSums(key, access));
                list.Add(sums);
            }

            sums.Members.Add(access);
            sums.Copies += access.ExecutionCount;
            sums.Variants += _variantsOf(access);
        }

        _classes.Add(bucket, list);
        return list;
    }

    /// <summary>What pairing's decisions before its questions read of an access, by number.</summary>
    /// <param name="access">The access.</param>
    private ClassKey KeyOf(Access access) =>
        new(access.Operation, Number(_scopes, access.Resource.Scope), ConditionsOf(access), ProtectionOf(access), CoverOf(access.Resource),
            IsConfined(access), access.IsFresh, access.IsLibraryInternal);

    /// <summary>The number of an access's path conditions as <see cref="PathConditions.Contradict"/> reads them: the subject, relation
    /// and value of each supported predicate over a canonical subject, each once.</summary>
    /// <param name="access">The access.</param>
    private int ConditionsOf(Access access)
    {
        var read = new List<PredicateKey>();
        foreach (var predicate in access.Conditions)
        {
            WorkCancellation.Check();
            Spend(1);
            if (predicate.IsSupported && predicate.IsCanonical)
                read.Add(new PredicateKey(predicate.Subject, predicate.Relation, predicate.Value));
        }

        Spend(SortWork(read.Count));
        read.Sort(PredicateKey.ORDER);
        var distinct = new List<PredicateKey>(read.Count);
        foreach (var key in read)
        {
            WorkCancellation.Check();
            if (distinct.Count == 0 || !distinct[^1].Equals(key))
                distinct.Add(key);
        }

        ref var number = ref CollectionsMarshal.GetValueRefOrAddDefault(_conditions, distinct.ToArray(), out var met);
        if (!met)
        {
            number = _conditionsOf.Count;
            _conditionsOf.Add(access.Conditions);
        }

        return number;
    }

    /// <summary>The number of an access's held protection as <see cref="PairProtection.Of"/> reads it: whether it holds any protection
    /// at all, and for each held object's id what it is held by.</summary>
    /// <param name="access">The access.</param>
    private int ProtectionOf(Access access)
    {
        var ids = new List<HeldId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in access.HeldProtectionIds)
        {
            WorkCancellation.Check();
            Spend(1);
            if (seen.Add(id))
                ids.Add(new HeldId(Number(_protectionIds, id), id));
        }

        Spend(SortWork(ids.Count));
        ids.Sort((one, other) => one.Number.CompareTo(other.Number));
        var key = new List<int> { access.HeldProtection.Count == 0 ? 0 : 1 };
        foreach (var id in ids)
        {
            WorkCancellation.Check();
            var held = access.HeldProtections.TryGetValue(id.Id, out var holdings) ? holdings : [];
            key.Add(id.Number);
            key.Add(held.Count);
            foreach (var holding in held)
            {
                WorkCancellation.Check();
                Spend(1);
                key.Add((int)holding.Primitive);
                key.Add((int)holding.Mode);
                key.Add(holding.IsExclusive ? 1 : 0);
            }
        }

        ref var protection = ref CollectionsMarshal.GetValueRefOrAddDefault(_protections, key.ToArray(), out var met);
        if (!met)
        {
            protection = _protectionOf.Count;
            _protectionOf.Add(access);
        }

        return protection;
    }

    /// <summary>The number of what decides whether a wildcard keeps a pair of a resource: whether it is a wildcard, the fields it
    /// excludes, by content, and its root storage field.</summary>
    /// <param name="resource">The resource.</param>
    private int CoverOf(AccessResource resource)
    {
        var excluded = 0;
        if (resource.IsWildcard)
        {
            var fields = new string[resource.WildcardExclusions.Count];
            var at = 0;
            foreach (var field in resource.WildcardExclusions)
            {
                WorkCancellation.Check();
                Spend(1);
                fields[at++] = field;
            }

            Spend(SortWork(fields.Length));
            Array.Sort(fields, StringComparer.Ordinal);
            excluded = Number(_exclusions, fields) + 1;
        }

        return Number(_covers, new CoverKey(excluded, RootField(resource)));
    }

    /// <summary>A class's counts by execution, tree and resource object, made when a search first reaches the class and kept while the
    /// counts kept stay under <see cref="CensusLimits.CountsLimit"/>; past it every class's are let go but the pinned one's, which the
    /// pair being counted reads.</summary>
    /// <param name="sums">The class.</param>
    /// <param name="pinned">The other class of the pair being counted, whose counts stay; null for none.</param>
    private Counts CountsOf(ClassSums sums, ClassSums? pinned)
    {
        if (sums.Counts is { } known)
            return known;
        var counts = new Counts();
        try
        {
            foreach (var access in sums.Members)
            {
                WorkCancellation.Check();
                Count(counts, access);
            }
        }
        finally
        {
            // Every table kept is alive while this one is made, and so is this one where the budget stops it half made.
            CountEntriesPeak = Math.Max(CountEntriesPeak, _countEntries + counts.Entries);
        }

        var entries = counts.Entries;
        Spend(entries);
        if (_countEntries + entries > _limits.CountsLimit)
            LetGo(pinned);
        sums.Counts = counts;
        _counted.Add(sums);
        _countEntries += entries;
        return counts;
    }

    /// <summary>Lets go of every class's counts but the pinned one's.</summary>
    /// <param name="pinned">The class whose counts stay; null for none.</param>
    private void LetGo(ClassSums? pinned)
    {
        foreach (var held in _counted)
        {
            WorkCancellation.Check();
            if (!ReferenceEquals(held, pinned))
                held.Counts = null;
        }

        _counted.Clear();
        _countEntries = 0;
        if (pinned?.Counts is not { } kept)
            return;
        _counted.Add(pinned);
        _countEntries = kept.Entries;
    }

    /// <summary>Adds an access to its class's counts: its executions by execution and by tree, its variants by tree, and its path roots.</summary>
    /// <param name="counts">The class's counts.</param>
    /// <param name="access">The access.</param>
    private void Count(Counts counts, Access access)
    {
        if (_variantTrees.Count > SCRATCH_LIMIT)
            _variantTrees = new Dictionary<AccessVariant, int>(ReferenceEqualityComparer.Instance);
        else
            _variantTrees.Clear();
        if (_pathRoots.Count > SCRATCH_LIMIT)
            _pathRoots = new HashSet<string>(StringComparer.Ordinal);
        else
            _pathRoots.Clear();
        if (access.Executions.Count == 0)
        {
            Spend(1);
            ExecutionsRead++;
            var place = Place(access.ExecutionId, null);
            counts.AddCopy(place);
            _variantTrees.Add(new AccessVariant(access.Root, access.PathRoot, access.CallPath, access.SpawnSites, access.CodeFlow, access.ReadSources),
                              place.TreeKey);
            _pathRoots.Add(access.PathRoot.RootId);
        }
        else
        {
            foreach (var execution in access.Executions)
            {
                WorkCancellation.Check();
                Spend(1);
                ExecutionsRead++;
                var place = Place(execution.ExecutionId, null);
                counts.AddCopy(place);
                ref var tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_variantTrees, execution.Variant, out var met);
                tree = !met || tree == place.TreeKey ? place.TreeKey : ACROSS;
                _pathRoots.Add(execution.Variant.PathRoot.RootId);
            }
        }

        foreach (var tree in _variantTrees.Values)
        {
            WorkCancellation.Check();
            counts.AddVariant(tree);
        }

        counts.Roots += _pathRoots.Count;
        ref var sums = ref CollectionsMarshal.GetValueRefOrAddDefault(counts.ByResource, access.Resource, out _);
        sums = new ResourceSums(sums.Variants + _variantTrees.Count, sums.Roots + _pathRoots.Count);
    }

    /// <summary>Adds a bucket's size to its size class and to the largest sizes, which keep their order.</summary>
    /// <param name="size">The bucket's size.</param>
    /// <param name="largest">The largest sizes so far, largest first.</param>
    private void AddSize(long size, List<long> largest)
    {
        var sizeClass = 0;
        // bounded: the five upper ends of the size classes.
        while (sizeClass < SIZE_CLASSES.Length && size >= SIZE_CLASSES[sizeClass])
            sizeClass++;
        _sizes[sizeClass]++;
        var at = largest.Count;
        // bounded: the at most five largest sizes.
        while (at > 0 && largest[at - 1] < size)
            at--;
        if (at >= LARGEST)
            return;
        largest.Insert(at, size);
        if (largest.Count > LARGEST)
            largest.RemoveAt(LARGEST);
    }

    /// <summary>The progress of the pairs or searches part as the lines of a checkpoint: how far it got, and what it counted whole so
    /// far — the pairs of the blocks finished, or the count lines over the pairs of classes finished — the block or pair of classes
    /// under way left out, so that a run stopped from outside keeps what its last progress line wrote.</summary>
    private IReadOnlyList<string> ProgressLines()
    {
        if (_stage == PAIRS)
        {
            return
            [
                $"stage=pairs bucketPairsTested={BucketPairsTested} steps={Steps} budget={_limits.Budget} ms={_clock.ElapsedMilliseconds}",
                $"pairs complete=false {PairTotals()}"
            ];
        }

        return
        [
            $"stage=searches blocks={BlocksCovered}/{Blocks} pairs={PairsCovered}/{AllPairs} steps={Steps} budget={_limits.Budget} " +
            $"executionsRead={ExecutionsRead} countEntries={_countEntries} decisionClears={DecisionClears} ms={_clock.ElapsedMilliseconds}",
            .. CountLines($"over=covered:{PairsCovered}/{AllPairs}")
        ];
    }

    /// <summary>A flag as the lines write it.</summary>
    /// <param name="flag">The flag.</param>
    private static string Flag(bool flag) => flag ? "true" : "false";

    /// <summary>Where an execution stands: its number, its tree and whether it may start during startup; an execution the scope's list
    /// does not name is a tree of its own that starts after startup.</summary>
    /// <param name="execution">The execution's id.</param>
    /// <param name="treeRoot">Its tree's root, or null for one met only through an access.</param>
    private ExecutionPlace Place(string execution, string? treeRoot)
    {
        if (_places.TryGetValue(execution, out var place))
            return place;
        if (!_trees.TryGetValue(treeRoot ?? execution, out var tree))
            _trees.Add(treeRoot ?? execution, tree = _trees.Count);
        _places.Add(execution, place = new ExecutionPlace(_places.Count, tree, treeRoot is not null && _duringStartup(execution)));
        return place;
    }

    /// <summary>Whether pairing skips an access's pairs as confined to one thread: its region, or for a reference access its collection,
    /// is owned by one thread.</summary>
    /// <param name="access">The access.</param>
    private bool IsConfined(Access access) =>
        _ownership.TryGetValue(access.IsReferenceAccess ? access.Resource.CollectionId ?? access.Resource.RegionId! : access.Resource.RegionId!,
                               out var ownership) &&
        ownership.Kind == OwnershipKind.ThreadConfined;

    /// <summary>A value's number in a table, given anew where the table has none.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="numbers">The table.</param>
    /// <param name="value">The value.</param>
    private static int Number<T>(Dictionary<T, int> numbers, T value) where T : notnull
    {
        ref var number = ref CollectionsMarshal.GetValueRefOrAddDefault(numbers, value, out var met);
        if (!met)
            number = numbers.Count - 1;
        return number;
    }

    /// <summary>Two numbers as one key, in order, so that a pair asked either way round is one.</summary>
    /// <param name="one">One number.</param>
    /// <param name="other">The other.</param>
    private static long Pair(int one, int other) => one <= other ? (long)one << 32 | (uint)other : (long)other << 32 | (uint)one;

    /// <summary>The pairs of a set with itself, each once and each member with itself: n(n+1)/2 for n members.</summary>
    /// <param name="members">The members.</param>
    private static long Triangle(long members) => (long)(((Int128)members * members + members) / 2);

    /// <summary>The sum of the products of two tables' counts on their common keys.</summary>
    /// <param name="one">One table.</param>
    /// <param name="other">The other.</param>
    private long Dot(Dictionary<int, long> one, Dictionary<int, long> other)
    {
        var (small, large) = one.Count <= other.Count ? (one, other) : (other, one);
        var sum = 0L;
        foreach (var (key, count) in small)
        {
            WorkCancellation.Check();
            Spend(1);
            if (large.TryGetValue(key, out var otherCount))
                sum += count * otherCount;
        }

        return sum;
    }

    /// <summary>The ordered pairs of two tables' members of one tree, startup or not: the tables count by tree key, which keeps a tree's
    /// members that start during startup apart from the rest. The smaller table is walked and the larger looked up.</summary>
    /// <param name="one">One table, by tree key.</param>
    /// <param name="other">The other.</param>
    private long SameTree(Dictionary<int, long> one, Dictionary<int, long> other)
    {
        var (small, large) = one.Count <= other.Count ? (one, other) : (other, one);
        var sum = 0L;
        foreach (var (key, count) in small)
        {
            WorkCancellation.Check();
            Spend(1);
            var tree = key & ~1;
            sum += count * (large.GetValueOrDefault(tree) + large.GetValueOrDefault(tree | 1));
        }

        return sum;
    }

    /// <summary>The ordered pairs of two tables' members of one tree with the given startup flags: what the pairs of two flags must leave
    /// out to be the pairs of two trees. The smaller table is walked, with its own flag, and the larger looked up.</summary>
    /// <param name="one">One table, by tree key.</param>
    /// <param name="other">The other.</param>
    /// <param name="oneStarting">The first member's flag.</param>
    /// <param name="otherStarting">The second member's flag.</param>
    private long Flagged(Dictionary<int, long> one, Dictionary<int, long> other, bool oneStarting, bool otherStarting) =>
        one.Count <= other.Count ? FlaggedFrom(one, other, oneStarting, otherStarting) : FlaggedFrom(other, one, otherStarting, oneStarting);

    /// <summary>The ordered pairs of <see cref="Flagged"/>, walking the first table and looking the second up.</summary>
    /// <param name="walked">The table walked, by tree key.</param>
    /// <param name="looked">The table looked up.</param>
    /// <param name="walkedStarting">The walked table's member's flag.</param>
    /// <param name="lookedStarting">The looked-up table's member's flag.</param>
    private long FlaggedFrom(Dictionary<int, long> walked, Dictionary<int, long> looked, bool walkedStarting, bool lookedStarting)
    {
        var sum = 0L;
        foreach (var (key, count) in walked)
        {
            WorkCancellation.Check();
            Spend(1);
            if ((key & 1) == (walkedStarting ? 1 : 0))
                sum += count * looked.GetValueOrDefault(key & ~1 | (lookedStarting ? 1 : 0));
        }

        return sum;
    }

    /// <summary>The budget ran out: the part counting stops where it is, and what it counted whole stays.</summary>
    private sealed class BudgetSpent : Exception;

    /// <summary>Where an execution stands.</summary>
    /// <param name="Number">Its number in the census.</param>
    /// <param name="Tree">Its tree's number.</param>
    /// <param name="Startup">Whether it may start during startup.</param>
    private readonly record struct ExecutionPlace(int Number, int Tree, bool Startup)
    {
        /// <summary>The tree and the startup flag as one key: the tree's number shifted by one bit, the flag in that bit.</summary>
        internal int TreeKey => Tree << 1 | (Startup ? 1 : 0);
    }

    /// <summary>What a class of accesses agrees on: what pairing's decisions before its questions read, by number.</summary>
    /// <param name="Operation">The operation.</param>
    /// <param name="Scope">The resource's scope.</param>
    /// <param name="Conditions">The path conditions as <see cref="PathConditions.Contradict"/> reads them.</param>
    /// <param name="Protection">The held protection as <see cref="PairProtection.Of"/> reads it.</param>
    /// <param name="Cover">What a wildcard's exclusion reads of the resource.</param>
    /// <param name="Confined">Whether the region is confined to one thread.</param>
    /// <param name="Fresh">Whether the access touches a fresh object.</param>
    /// <param name="LibraryInternal">Whether the access is library-internal.</param>
    private readonly record struct ClassKey(AccessOperation Operation, int Scope, int Conditions, int Protection, int Cover, bool Confined,
                                            bool Fresh, bool LibraryInternal);

    /// <summary>What a wildcard's exclusion reads of a resource.</summary>
    /// <param name="Excluded">The number of the fields a wildcard resource excludes, by content, plus one; 0 for a resource that is no
    /// wildcard.</param>
    /// <param name="Field">The key of its root storage field; empty for none.</param>
    private readonly record struct CoverKey(int Excluded, string Field);

    /// <summary>A held object's id with its number in the census.</summary>
    /// <param name="Number">The number.</param>
    /// <param name="Id">The id.</param>
    private readonly record struct HeldId(int Number, string Id);

    /// <summary>A predicate as <see cref="PathConditions.Contradict"/> reads it.</summary>
    /// <param name="Subject">The canonical subject.</param>
    /// <param name="Relation">The relation.</param>
    /// <param name="Value">The value.</param>
    private readonly record struct PredicateKey(string Subject, PathRelation Relation, string? Value)
    {
        /// <summary>An order of predicates, so that one set of them gives one key.</summary>
        internal static readonly IComparer<PredicateKey> ORDER = Comparer<PredicateKey>.Create((one, other) =>
            string.CompareOrdinal(one.Subject, other.Subject) is var subject and not 0 ? subject
            : one.Relation.CompareTo(other.Relation) is var relation and not 0 ? relation
            : string.CompareOrdinal(one.Value, other.Value));
    }

    /// <summary>What one class's accesses on one resource object add up to.</summary>
    /// <param name="Variants">Their variants.</param>
    /// <param name="Roots">Their path roots.</param>
    private readonly record struct ResourceSums(long Variants, long Roots);

    /// <summary>A bucket's wildcards with root fields that agree on what decides a wildcard's exclusion: how many, and the first.</summary>
    /// <param name="Representative">The first of them.</param>
    /// <param name="Count">How many.</param>
    private readonly record struct CoverClass(Access Representative, long Count);

    /// <summary>A bucket's wildcards and fields, as the drops of its blocks read them.</summary>
    private sealed class BucketCover
    {
        /// <summary>Whether the bucket holds a wildcard.</summary>
        internal bool HasWildcard { get; set; }

        /// <summary>For each field key, how many of the bucket's wildcards exclude it.</summary>
        internal Dictionary<string, long> Excluding { get; } = new(StringComparer.Ordinal);

        /// <summary>For each field key, how many of the bucket's accesses have it as their root storage field.</summary>
        internal Dictionary<string, long> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>The bucket's wildcards with root fields that exclude any field, by what decides their exclusion.</summary>
        internal List<CoverClass> FieldedWildcards { get; } = [];

        /// <summary>For each root field, the places in <see cref="FieldedWildcards"/> of the classes with it.</summary>
        internal Dictionary<string, List<int>> FieldedByRoot { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One class of a bucket's accesses.</summary>
    /// <param name="key">What the class agrees on.</param>
    /// <param name="representative">The class's first access, which stands for every access of the class in pairing's decisions.</param>
    private sealed class ClassSums(ClassKey key, Access representative)
    {
        /// <summary>What the class agrees on.</summary>
        internal ClassKey Key { get; } = key;

        /// <summary>The class's first access.</summary>
        internal Access Representative { get; } = representative;

        /// <summary>The class's accesses.</summary>
        internal List<Access> Members { get; } = [];

        /// <summary>How many accesses the class has.</summary>
        internal long Accesses => Members.Count;

        /// <summary>Their executions: every one each lists.</summary>
        internal long Copies { get; set; }

        /// <summary>Their variants, as pairing counts them of each access.</summary>
        internal long Variants { get; set; }

        /// <summary>Their variants by resource object, summed when a search one witness ends first reaches the class within its bucket;
        /// null until then.</summary>
        internal Dictionary<AccessResource, long>? VariantsByResource { get; set; }

        /// <summary>The counts a search needs, made when one first reaches the class; null until then, or after they were let go.</summary>
        internal Counts? Counts { get; set; }
    }

    /// <summary>What a class's accesses add up to by execution, tree and resource object.</summary>
    private sealed class Counts
    {
        /// <summary>Their executions.</summary>
        internal long Copies { get; private set; }

        /// <summary>Those executions by execution number.</summary>
        internal Dictionary<int, long> CopiesByExecution { get; } = [];

        /// <summary>Those executions by tree key (<see cref="ExecutionPlace.TreeKey"/>).</summary>
        internal Dictionary<int, long> CopiesByTree { get; } = [];

        /// <summary>Those executions that may start during startup.</summary>
        internal long CopiesStarting { get; private set; }

        /// <summary>The accesses' variants.</summary>
        internal long Variants { get; private set; }

        /// <summary>Those variants whose executions are of more than one tree key.</summary>
        internal long VariantsAcrossTrees { get; private set; }

        /// <summary>The other variants by tree key.</summary>
        internal Dictionary<int, long> VariantsByTree { get; } = [];

        /// <summary>Those other variants whose tree starts during startup.</summary>
        internal long VariantsStarting { get; private set; }

        /// <summary>The accesses' distinct path roots, each access's own counted.</summary>
        internal long Roots { get; set; }

        /// <summary>The variants and path roots by resource object.</summary>
        internal Dictionary<AccessResource, ResourceSums> ByResource { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The entries of the tables.</summary>
        internal long Entries => CopiesByExecution.Count + CopiesByTree.Count + VariantsByTree.Count + ByResource.Count;

        /// <summary>Adds one execution of an access.</summary>
        /// <param name="place">Where the execution stands.</param>
        internal void AddCopy(ExecutionPlace place)
        {
            Copies++;
            CollectionsMarshal.GetValueRefOrAddDefault(CopiesByExecution, place.Number, out _)++;
            CollectionsMarshal.GetValueRefOrAddDefault(CopiesByTree, place.TreeKey, out _)++;
            if (place.Startup)
                CopiesStarting++;
        }

        /// <summary>Adds one variant of an access.</summary>
        /// <param name="tree">Its tree key, or <see cref="ACROSS"/>.</param>
        internal void AddVariant(int tree)
        {
            Variants++;
            if (tree == ACROSS)
            {
                VariantsAcrossTrees++;
                return;
            }

            CollectionsMarshal.GetValueRefOrAddDefault(VariantsByTree, tree, out _)++;
            if ((tree & 1) == 1)
                VariantsStarting++;
        }
    }

    /// <summary>The keys of the memos of decisions, two numbers in one long (<see cref="Pair"/>), hashed so that small numbers spread
    /// over a table. A long's own hash folds its halves with XOR, which of two numbers below 2^k gives at most 2^k values: a memo of a
    /// million answers about a scope with a few thousand numbers of conditions or of protections would keep them in a few thousand chains,
    /// and each step of the census would take some ten times longer. The key times <see cref="SPREAD"/>, its upper half taken, spreads
    /// them; what a memo answers it never changes, only what an answer costs.</summary>
    private sealed class MemoKeys : IEqualityComparer<long>
    {
        /// <summary>2^64 over the golden ratio, odd: multiplying by it carries every bit of a key into the upper half.</summary>
        private const ulong SPREAD = 0x9E3779B97F4A7C15;

        /// <summary>The one comparer.</summary>
        internal static readonly MemoKeys INSTANCE = new();

        /// <summary>Whether two keys are one.</summary>
        /// <param name="x">One key.</param>
        /// <param name="y">The other.</param>
        public bool Equals(long x, long y) => x == y;

        /// <summary>A key's hash: the upper half of its product with <see cref="SPREAD"/>.</summary>
        /// <param name="key">The key.</param>
        public int GetHashCode(long key) => (int)((ulong)key * SPREAD >> 32);
    }

    /// <summary>Arrays compared by content, element by element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    private sealed class ArrayByContent<T> : IEqualityComparer<T[]>
    {
        internal static readonly ArrayByContent<T> INSTANCE = new();

        public bool Equals(T[]? x, T[]? y)
        {
            WorkCancellation.Check();
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null || x.Length != y.Length)
                return false;
            for (var at = 0; at < x.Length; at++)
            {
                WorkCancellation.Check();
                if (!EqualityComparer<T>.Default.Equals(x[at], y[at]))
                    return false;
            }

            return true;
        }

        public int GetHashCode(T[] obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Length);
            foreach (var item in obj)
            {
                WorkCancellation.Check();
                hash.Add(item);
            }

            return hash.ToHashCode();
        }
    }
}
