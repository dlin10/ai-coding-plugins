using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of operation patterns on a shared collection (issue 532), each with what run time calls for. Every probe
/// is one singleton <c>Registry</c> shared by two hosted services, each running one statement on it, analysed through the whole
/// pipeline (<see cref="HostedServiceProbe"/>). The truths rest on these guarantees. The plain collections are not safe for a writer
/// running with any other access («a Dictionary can support multiple readers concurrently, as long as the collection is not
/// modified», and the same for <c>List</c>, <c>HashSet</c> and <c>Queue</c>, Learn, thread safety of each): an addition, a removal
/// and <c>Clear</c> change the structure, which a count, a lookup and an enumeration read; an element store writes its cell, which an
/// index or an enumeration reads (a <c>List&lt;string&gt;</c>'s <c>Clear</c> clears the cells it held). A check and the change it
/// decides are two operations, and the state may change between them. <c>ReadOnlyCollection&lt;T&gt;</c>, which
/// <c>AsReadOnly</c> gives, is a live view: «if changes are made to the underlying collection, the read-only collection reflects
/// those changes» (Learn); a property that returns the field gives the same instance; <c>ToList</c> and a deferred <c>Where</c>
/// enumerate the source when they run (Learn, <c>Enumerable</c>: deferred execution). Two array cells at different indices are
/// different storage locations, and an element of an array of arrays is a reference to a separate array (C# specification, arrays);
/// <c>Memory&lt;T&gt;.Span</c> over an array writes that array's cells. <c>ConcurrentDictionary.TryAdd</c> adds only where the key is
/// absent, atomically, so a check before it loses nothing; its <c>Count</c> before a <c>TryAdd</c> of another key is a check the
/// other service's addition may invalidate (Learn, <c>ConcurrentDictionary</c>); <c>GetOrAdd</c> hands every caller the one value it
/// holds. Every truth is the behaviour of the program the fixture stands for, its registrations taken as written. A probe the
/// analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class CollectionPatternProbeTests
{
    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["count-then-index-against-clear"] = (Registry("""
                                                  private readonly List<string> _items = new() { "a" };
                                                  public string? Take() => _items.Count > 0 ? _items[0] : null;
                                                  public void Clear() => _items.Clear();
                                                  """, "_registry.Take();", "_registry.Clear();"),
                                              ["DCA1001 unprotected _items structure: Read Registry.Take | Write Registry.Clear",
                                               "DCA1001 unprotected _items.[0]: Read Registry.Take | Write Registry.Clear"]),
        ["contains-key-then-add"] = (Registry("""
                                         private readonly Dictionary<string, int> _map = new();
                                         public void Add() { if (!_map.ContainsKey("a")) _map.Add("a", 1); }
                                         """, "_registry.Add();", "_registry.Add();"),
                                     ["DCA1002 unprotected _map.[\"a\"]: CompoundOperation Registry.Add | CompoundOperation Registry.Add",
                                      "DCA1001 unprotected _map structure: Read Registry.Add | Write Registry.Add",
                                      "DCA1001 unprotected _map structure: Write Registry.Add | Write Registry.Add"]),
        ["static-readonly-dictionary"] = (Registry("""
                                              private static readonly Dictionary<string, int> s_map = new();
                                              public void SetA() => s_map["a"] = 1;
                                              public void SetB() => s_map["b"] = 1;
                                              """, "_registry.SetA();", "_registry.SetB();"),
                                          ["DCA1001 unprotected s_map structure: Write Registry.SetA | Write Registry.SetB"]),
        ["dictionary-key-from-a-parameter"] = (Registry("""
                                                   private readonly Dictionary<string, int> _map = new();
                                                   public void Set(string key) => _map[key] = 1;
                                                   """, "_registry.Set(\"a\");", "_registry.Set(\"b\");"),
                                               ["DCA1001 unprotected _map structure: Write Registry.Set | Write Registry.Set"]),
        ["property-returning-the-list"] = (Registry("""
                                               private readonly List<string> _items = new();
                                               public List<string> Items => _items;
                                               """, "_registry.Items.Add(\"a\");", "_ = _registry.Items.Count;"),
                                           ["DCA1001 unprotected _items structure: Read SecondWorker.ExecuteAsync | Write FirstWorker.ExecuteAsync"]),
        ["as-read-only-view"] = (Registry("""
                                     private readonly List<string> _items = new();
                                     public IReadOnlyList<string> View => _items.AsReadOnly();
                                     public void Add() => _items.Add("a");
                                     """, "_registry.Add();", "_ = _registry.View.Count;"),
                                 ["DCA1001 unprotected _items structure: Read SecondWorker.ExecuteAsync | Write Registry.Add"]),
        ["to-list-against-an-add"] = (Registry("""
                                          private readonly List<string> _items = new();
                                          public void Add() => _items.Add("a");
                                          public int Copy() => _items.ToList().Count;
                                          """, "_registry.Add();", "_registry.Copy();"),
                                      ["DCA1001 unprotected _items structure: Read Registry.Copy | Write Registry.Add",
                                       "DCA1001 unprotected _items.[?]: Read Registry.Copy | Write Registry.Add"]),
        ["deferred-where-against-an-add"] = (Registry("""
                                                 private readonly List<string> _items = new();
                                                 private readonly IEnumerable<string> _query;
                                                 public Registry() => _query = _items.Where(item => item.Length > 0);
                                                 public void Add() => _items.Add("a");
                                                 public int Count() => _query.Count();
                                                 """, "_registry.Add();", "_registry.Count();"),
                                             ["DCA1001 unprotected _items structure: Read Registry.Count | Write Registry.Add",
                                              "DCA1001 unprotected _items.[?]: Read Registry.Count | Write Registry.Add"]),
        ["queue-enqueue-against-dequeue"] = (Registry("""
                                                 private readonly Queue<string> _queue = new();
                                                 public void Put() => _queue.Enqueue("a");
                                                 public void Take() => _queue.TryDequeue(out _);
                                                 """, "_registry.Put();", "_registry.Take();"),
                                             ["DCA1001 unprotected _queue structure: Write Registry.Put | Write Registry.Take",
                                              "DCA1001 unprotected _queue.[?]: Write Registry.Put | Write Registry.Take"]),
        ["static-array-disjoint-cells"] = (Registry("""
                                               private static readonly int[] s_cells = new int[2];
                                               public void Set(int index) => s_cells[index] = 1;
                                               """, "_registry.Set(0);", "_registry.Set(1);"), []),
        ["jagged-array-disjoint-rows"] = (Registry("""
                                              private readonly int[][] _grid = [new int[1], new int[1]];
                                              public void Set(int row) => /*@cell*/_grid[row][0] = 1;
                                              """, "_registry.Set(0);", "_registry.Set(1);"), []),
        ["jagged-array-same-row"] = (Registry("""
                                         private readonly int[][] _grid = [new int[1], new int[1]];
                                         public void Set(int value) => /*@cell*/_grid[0][0] = value;
                                         """, "_registry.Set(1);", "_registry.Set(2);"),
                                     ["DCA1001 unprotected _grid.[0].[0]: Write Registry.Set @cell | Write Registry.Set @cell"]),
        ["memory-span-against-an-index"] = (Registry("""
                                                private readonly int[] _cells = new int[4];
                                                public void Fill() => /*@fill*/_cells.AsMemory(0, 2).Span[0] = 1;
                                                public void Set() => _cells[0] = 2;
                                                """, "_registry.Fill();", "_registry.Set();"),
                                            ["DCA1001 unprotected _cells.[0]: Write Registry.Fill @fill | Write Registry.Set"]),
        ["memory-slices-disjoint"] = (Registry("""
                                          private readonly int[] _cells = new int[4];
                                          public void Fill(int start) => /*@fill*/_cells.AsMemory(start, 2).Span[0] = 1;
                                          """, "_registry.Fill(0);", "_registry.Fill(2);"), []),
        ["contains-key-then-try-add"] = (Registry("""
                                             private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _map = new();
                                             public void Add() { if (!_map.ContainsKey("a")) _map.TryAdd("a", 1); }
                                             """, "_registry.Add();", "_registry.Add();"), []),
        ["count-then-try-add"] = (Registry("""
                                      private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _map = new();
                                      public void Add(string key) { if (_map.Count < 1) _map.TryAdd(key, 1); }
                                      """, "_registry.Add(\"a\");", "_registry.Add(\"b\");"),
                                  ["DCA1004 unprotected _map structure: CompoundOperation Registry.Add | CompoundOperation Registry.Add"]),
        ["list-values-of-a-concurrent-map"] = (Registry("""
                                                  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<int>> _map = new();
                                                  public void Add(int x) => /*@add*/_map.GetOrAdd("k", _ => new List<int>()).Add(x);
                                                  """, "_registry.Add(1);", "_registry.Add(2);"),
                                              ["DCA1001 unprotected _map.[\"k\"] structure: Write Registry.Add @add | Write Registry.Add @add",
                                               "DCA1001 unprotected _map.[\"k\"].[?]: Write Registry.Add @add | Write Registry.Add @add"])
    };

    /// <summary>Why a list a dictionary's cell holds, or an array an array's cell holds, makes no access.</summary>
    private const string NO_FIELD_HOLDER =
        "issue 516: the collection the member works on is reached through another collection's cell, not loaded from a field " +
        "(MethodSummaryBuilder.HolderLoad and Cell name none), so the member stands as a standing effect, which " +
        "InterproceduralAccesses.StandingMemberAccesses drops for a collection no field holds (Holders registers no element " +
        "slot): the holder boundary of TD-043 and ADR 0010; whether StandingTargets resolves the inner collection at all is not " +
        "established";

    /// <summary>Why a check-then-act on a plain dictionary is classed DCA1004 rather than the lost update PRD FR-07 names.</summary>
    private const string DCA1004_ON_A_PLAIN_COLLECTION =
        "issue 539: a rule mismatch, an owner question: PRD FR-07 gives DCA1004 to a non-atomic sequence of " +
        "individually safe operations and puts it before DCA1002 only where both operations are atomic on a modelled cell, and a " +
        "plain Dictionary is atomic on neither resource (ADR 0010), so the cell's lost update is DCA1002; SPEC 7's table and " +
        "ConflictFindings.OperationRule, whose IsIndivisible counts every CompoundOperation as indivisible, give DCA1004; the " +
        "structural pairs agree";

    /// <summary>Why a check before <c>TryAdd</c> on a thread-safe dictionary is a compound operation.</summary>
    private const string TRY_ADD_AS_OVERWRITE =
        "issue 534: MethodSummaryBuilder.CollectionAccesses and Checks make the element access compound wherever a read of the " +
        "same collection guards a change of the same proven cell, deciding from the effect kinds and the dependency alone and " +
        "ignoring which member makes the change, which IrLowering's member effects still name: TryAdd shares set_Item's effect " +
        "row (structure and element write), so an addition only where the key is absent reads as an overwrite";

    /// <summary>Why a dictionary key that is a parameter stays an unknown cell.</summary>
    private const string KEY_NOT_BOUND =
        "issue 535: at selector emission the access MethodSummaryBuilder.CollectionAccesses makes carries only a Selector, a " +
        "symbol for a parameter, and no SelectorTerm, so at binding InterproceduralAccesses does not bind it to the caller's " +
        "argument as it binds an array index (Bind to ElementSelector.Exact); ConstantTerm is numeric only, so a string key could " +
        "not bind that way either";

    /// <summary>Why a deferred query held in a field pairs from an unknown enumeration as well.</summary>
    private const string ESCAPED_SEQUENCE =
        "by rule, SPEC TD-060b and ADR 0011: an iterator or a library sequence a field keeps escapes, whatever the field's " +
        "visibility (WholeProgram adds every sequence a field carries to UnknownIterators), and an escaped library sequence is " +
        "enumerated by an unknown execution (ExecutionModel), so the Where held in the singleton's private field reads _items " +
        "there too, at its own site in the constructor, and pairs with Add beside the enumeration Count makes; at run time only " +
        "Count enumerates it; exempting a sequence only private code consumes would change that rule";

    /// <summary>Why the view <c>AsReadOnly</c> returns reads as an unknown effect.</summary>
    private const string AS_READ_ONLY_UNMODELLED =
        "limit: ADR 0010 (amendment of 2026-10-16) gives AsReadOnly the unknown effect, as code the table does not follow " +
        "(IrLowering UnmodelledEffects.Of), so the view's read is an unknown effect on the list's structure and every cell rather " +
        "than a read of its structure";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why, listed by
    /// hand.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["contains-key-then-add"] = new(["DCA1004 unprotected _map.[\"a\"]: CompoundOperation Registry.Add | CompoundOperation Registry.Add",
                                         "DCA1001 unprotected _map structure: Read Registry.Add | Write Registry.Add",
                                         "DCA1001 unprotected _map structure: Write Registry.Add | Write Registry.Add"], DCA1004_ON_A_PLAIN_COLLECTION),
        ["list-values-of-a-concurrent-map"] = new([], NO_FIELD_HOLDER),
        ["jagged-array-same-row"] = new([], NO_FIELD_HOLDER),
        ["contains-key-then-try-add"] = new(["DCA1004 unprotected _map.[\"a\"]: CompoundOperation Registry.Add | CompoundOperation Registry.Add"],
                                            TRY_ADD_AS_OVERWRITE),
        ["dictionary-key-from-a-parameter"] = new(["DCA1001 unprotected _map structure: Write Registry.Set | Write Registry.Set",
                                                   "DCA1001 unprotected _map.[?]: Write Registry.Set | Write Registry.Set"], KEY_NOT_BOUND),
        ["deferred-where-against-an-add"] = new([.. Probes["deferred-where-against-an-add"].Truth,
                                                 "DCA1001 unprotected _items structure: Read Registry..ctor | Write Registry.Add",
                                                 "DCA1001 unprotected _items.[?]: Read Registry..ctor | Write Registry.Add"], ESCAPED_SEQUENCE),
        ["as-read-only-view"] = new(["DCA1001 unprotected _items structure: UnknownEffect Registry.get_View | Write Registry.Add",
                                     "DCA1001 unprotected _items.[?]: UnknownEffect Registry.get_View | Write Registry.Add"], AS_READ_ONLY_UNMODELLED)
    };

    /// <summary>What a probe's marked site collected, exactly: where the claim is that a write is lost, the marked site holds only
    /// the reads that reach the collection, and where two disjoint writes stand, both were collected with their cells.</summary>
    private static readonly Dictionary<string, (string Marker, string[] Accesses)> Sites = new(StringComparer.Ordinal)
    {
        ["list-values-of-a-concurrent-map"] = ("@add", ["AtomicReadModifyWrite _map.[\"k\"] Registry.Add @add", "AtomicWrite _map structure Registry.Add @add"]),
        ["jagged-array-same-row"] = ("@cell", ["Read _grid Registry.Set @cell", "Read _grid.[0] Registry.Set @cell"]),
        ["jagged-array-disjoint-rows"] = ("@cell", ["Read _grid Registry.Set @cell", "Read _grid.[0] Registry.Set @cell", "Read _grid.[1] Registry.Set @cell"]),
        ["memory-span-against-an-index"] = ("@fill", ["Read _cells Registry.Fill @fill", "Write _cells.[0] Registry.Fill @fill"]),
        ["memory-slices-disjoint"] = ("@fill", ["Read _cells Registry.Fill @fill", "Write _cells.[0] Registry.Fill @fill", "Write _cells.[2] Registry.Fill @fill"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (probe, truth) = Probes[name];

        var findings = await probe.FindingsAsync();

        HostedServiceProbe.AssertAnswer(findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why; the differences number
    /// what the record counts, reason by reason.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth()
    {
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
        Assert.Equal(17, Probes.Count);
        Assert.Equal(2, Differences.Count(difference => difference.Value.Reason == NO_FIELD_HOLDER));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == TRY_ADD_AS_OVERWRITE));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == KEY_NOT_BOUND));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == ESCAPED_SEQUENCE));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == AS_READ_ONLY_UNMODELLED));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == DCA1004_ON_A_PLAIN_COLLECTION));
        Assert.Equal(7, Differences.Count);
    }

    /// <summary>The names of the probes whose marked site the record describes access by access.</summary>
    public static TheoryData<string> SiteNames() => new(Sites.Keys);

    /// <summary>A probe's marked site collected exactly the accesses the record names, so that a lost write is shown lost and two
    /// disjoint writes are shown collected; the static array's two cells are collected with their indices.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task Marked_site_collected_what_the_record_names(string name)
    {
        var (marker, accesses) = Sites[name];

        var answer = await Probes[name].Probe.AnalyzeAsync(new ProbeInputs());

        Assert.Equal(accesses.Order(StringComparer.Ordinal), answer.Accesses.Where(access => access.EndsWith(" " + marker, StringComparison.Ordinal)));
    }

    /// <summary>The two writes of the static array land on their own cells, so its empty answer is a proof of disjoint cells.</summary>
    [Fact]
    public async Task Static_array_writes_are_collected_on_their_cells() =>
        HostedServiceProbe.AssertAccessClaims(await Probes["static-array-disjoint-cells"].Probe.AnalyzeAsync(new ProbeInputs()),
                                              ["Write s_cells.[0] Registry.Set", "Write s_cells.[1] Registry.Set"], []);

    /// <summary>A probe of a <c>Registry</c> with the given members, its two hosted services running one statement each.</summary>
    /// <param name="members">The members of <c>Registry</c>.</param>
    /// <param name="first">The statement the first hosted service runs.</param>
    /// <param name="second">The statement the second hosted service runs.</param>
    private static HostedServiceProbe Registry(string members, string first, string second) => new("Registry", $$"""
        public sealed class Registry
        {
            {{members}}
        }
        """, first, second);
}
