using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the identity of a lock (issue 506), each with what run time calls for. A C# 13
/// <c>System.Threading.Lock</c> locked where the expression's static type is <c>Lock</c> takes the lock's own scope, and locked
/// through any other static type (<c>object</c>, a type parameter) takes a monitor on the same object, which does not exclude the
/// scope (compiler warning CS9216: a <c>Lock</c> converted to a different type uses monitor-based locking), while one held in an
/// <c>object</c> field and locked through it on both sides takes the monitor on both (the cast form is
/// <c>SynchronizationPrimitiveTests</c>'). A lock whose object a field holds and the program replaces: a gate field one service
/// reassigns while it holds the old gate, so the other may take the new one and run its section at the same time, the read of the
/// field in the <c>lock</c> statement itself unprotected; and a list locked on itself and replaced by a copy under that lock. Apart
/// from the lock, a list the body creates, fills and only then stores, whose cells no other execution can reach while they are
/// written — the collection form of an array filled before its body publishes it, which makes no access
/// (<c>ReferenceAccessTests.Cells_an_array_gets_before_its_body_publishes_it_are_no_access</c>). Every probe runs through the whole
/// analysis (<see cref="HostedServiceProbe"/>) and names each finding's pair of sides; a probe the analysis answers otherwise is named
/// in <see cref="Differences"/> with its answer and why.</summary>
public sealed class LockIdentityProbeTests
{
    /// <summary>Why a list filled before it is stored pairs its structure and cells across the two services.</summary>
    private const string FRESH_LIST_CELLS_PAIR =
        "issue 509: the structure and cells a body writes into a List<T> it created, before it stores it, are named through the " +
        "field that later holds it and pair across executions, where the cells of an array filled the same way make no access";

    /// <summary>The lost update of <c>Add</c>'s <c>_count++</c> against <c>Reset</c>'s write under a monitor and a lock scope.</summary>
    private const string MONITOR_AGAINST_SCOPE = "DCA1002 incompatible-mode _count: ReadModifyWrite Registry.Add | Write Registry.Reset";

    /// <summary>The write of <c>_items</c> by both services.</summary>
    private const string ITEMS_WRITTEN_TWICE = "DCA1001 unprotected _items: Write Registry.Add | Write Registry.Add";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["lock-object-taken-through-an-object-local"] = (Registry("""
                                                            private readonly Lock _gate = new();
                                                            private int _count;
                                                            public void Add() { lock (_gate) { _count++; } }
                                                            public void Reset() { object gate = _gate; lock (gate) { _count = 0; } }
                                                            """, "_registry.Add();", "_registry.Reset();"), [MONITOR_AGAINST_SCOPE]),
        ["lock-object-taken-through-a-type-parameter"] = (Registry("""
                                                            private readonly Lock _gate = new();
                                                            private int _count;
                                                            public void Add() { lock (_gate) { _count++; } }
                                                            public void Reset() => ResetUnder(_gate);
                                                            private void ResetUnder<T>(T gate) where T : class { lock (gate) { _count = 0; } }
                                                            """, "_registry.Add();", "_registry.Reset();"),
                                                         ["DCA1002 incompatible-mode _count: ReadModifyWrite Registry.Add | Write Registry.ResetUnder<T>"]),
        ["lock-object-taken-through-an-object-field-aliasing-it"] = (Registry("""
                                                                    private readonly Lock _gate = new();
                                                                    private readonly object _asObject;
                                                                    private int _count;
                                                                    public Registry() => _asObject = _gate;
                                                                    public void Add() { lock (_gate) { _count++; } }
                                                                    public void Reset() { lock (_asObject) { _count = 0; } }
                                                                    """, "_registry.Add();", "_registry.Reset();"), [MONITOR_AGAINST_SCOPE]),
        ["lock-object-held-in-an-object-field-on-both-sides"] = (Registry("""
                                                                private readonly object _gate = new Lock();
                                                                private int _count;
                                                                public void Add() { lock (_gate) { _count++; } }
                                                                public void Reset() { lock (_gate) { _count = 0; } }
                                                                """, "_registry.Add();", "_registry.Reset();"), []),
        ["lock-on-a-reassigned-field"] = (Registry("""
                                              private object _gate = new();
                                              private int _count;
                                              public void Add() { lock (_gate) { _count++; } }
                                              public void Reset() { lock (_gate) { _gate = new object(); _count = 0; } }
                                              """, "_registry.Add();", "_registry.Reset();"),
                                          ["DCA1002 different-identity _count: ReadModifyWrite Registry.Add | Write Registry.Reset",
                                           "DCA1003 partial _gate: Read Registry.Add | Write Registry.Reset"]),
        ["lock-on-a-reassigned-field-control"] = (Registry("""
                                                      private readonly object _gate = new();
                                                      private int _count;
                                                      public void Add() { lock (_gate) { _count++; } }
                                                      public void Reset() { lock (_gate) { _count = 0; } }
                                                      """, "_registry.Add();", "_registry.Reset();"), []),
        ["lock-on-the-list-it-replaces"] = (Registry("""
                                                private List<int> _items = new();
                                                public void Add(int x) { lock (_items) { _items = new List<int>(_items) { x }; } }
                                                """, "_registry.Add(1);", "_registry.Add(2);"),
                                            ["DCA1003 different-identity _items: Read Registry.Add | Write Registry.Add",
                                             "DCA1003 different-identity _items: Write Registry.Add | Write Registry.Add",
                                             "DCA1003 partial _items: Read Registry.Add | Write Registry.Add"]),
        ["list-filled-before-it-is-stored"] = (Registry("""
                                                  private List<int> _items = new();
                                                  public void Add(int x) { var next = new List<int>(); next.Add(x); _items = next; }
                                                  """, "_registry.Add(1);", "_registry.Add(2);"), [ITEMS_WRITTEN_TWICE]),
        ["list-initializer-before-it-is-stored"] = (Registry("""
                                                       private List<int> _items = new();
                                                       public void Add(int x) { _items = new List<int> { x }; }
                                                       """, "_registry.Add(1);", "_registry.Add(2);"), [ITEMS_WRITTEN_TWICE]),
        ["array-filled-before-it-is-stored"] = (Registry("""
                                                   private int[] _items = new int[1];
                                                   public void Add(int x) { var next = new int[1]; next[0] = x; _items = next; }
                                                   """, "_registry.Add(1);", "_registry.Add(2);"), [ITEMS_WRITTEN_TWICE])
    };

    /// <summary>The structure and cell pairs of two lists each service created, which issue 509 adds.</summary>
    private static readonly string[] FreshListPairs =
        ["DCA1001 unprotected _items structure: Write Registry.Add | Write Registry.Add", "DCA1001 unprotected _items.[?]: Write Registry.Add | Write Registry.Add"];

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["lock-on-the-list-it-replaces"] = new([.. Probes["lock-on-the-list-it-replaces"].Truth,
                                                "DCA1003 different-identity _items structure: Read Registry.Add | Write Registry.Add",
                                                "DCA1003 different-identity _items structure: Write Registry.Add | Write Registry.Add",
                                                "DCA1003 different-identity _items.[?]: Read Registry.Add | Write Registry.Add",
                                                "DCA1003 different-identity _items.[?]: Write Registry.Add | Write Registry.Add"], FRESH_LIST_CELLS_PAIR),
        ["list-filled-before-it-is-stored"] = new([ITEMS_WRITTEN_TWICE, .. FreshListPairs], FRESH_LIST_CELLS_PAIR),
        ["list-initializer-before-it-is-stored"] = new([ITEMS_WRITTEN_TWICE, .. FreshListPairs], FRESH_LIST_CELLS_PAIR)
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

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

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
