using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of lazy initialization (issue 506), each with what run time calls for: double-checked locking, whose
/// unlocked reads of the field race with the locked write (C# 15.5.4: a field shared without a lock), its form that publishes the
/// object before it initializes it, <c>??=</c>, <c>field ??=</c> and the explicit null check that decides a write, which run the
/// initializer on two threads and let the second write replace the object the first caller got — a check-then-act, which ADR 0010,
/// TD-073 and the owner's decision on issue 510 read as a read-modify-write, a lost update, beside which a separate later read of the
/// field races with the other caller's write — <c>Lazy&lt;T&gt;</c> in each thread-safety mode, whose factory runs once under
/// <c>ExecutionAndPublication</c> and may run on several threads at once under <c>PublicationOnly</c> and <c>None</c>
/// (<c>LazyThreadSafetyMode</c>), and <c>LazyInitializer.EnsureInitialized</c>, whose factory may run on several threads at once without
/// a lock and runs under the lock it is given with one. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>)
/// and names each finding's pair of sides; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer
/// and why.</summary>
public sealed class LazyInitializationProbeTests
{
    /// <summary>Why <c>??=</c>, <c>field ??=</c> and an explicit null check give a plain race rather than a lost update.</summary>
    private const string NULL_CHECK_IS_NO_DEPENDENCY =
        "issue 510: the null check that decides the write is no dependency of it, so the check-then-act the two services run is a " +
        "read and a write, not the read-modify-write ADR 0010, TD-073 and the owner's decision on the issue call it";

    /// <summary>Why every mode of <c>Lazy&lt;T&gt;</c> gives one answer.</summary>
    private const string LAZY_IS_NOT_MODELLED =
        "limit: no model describes Lazy<T> (the CoreLib generator answers unknown-execution for its valueFactory in every overload), so " +
        "the factory runs in one unknown execution (ADR 0011): ordered after nothing in startup, where the constructor hands it over, " +
        "and not overlapping itself, as a delegate handed over once by an execution that runs once; the mode decides nothing. So a " +
        "factory that reads a field its constructor writes, readonly or not, before the Lazy<T> or after it, pairs with that write, " +
        "while one in a static field's initializer reads statics the type initializer wrote without a pair, and in no form does a " +
        "PublicationOnly factory overlap itself";

    /// <summary>Why the locked overload of <c>LazyInitializer.EnsureInitialized</c> gives the answer of the unlocked one.</summary>
    private const string LAZY_INITIALIZER_IS_NOT_MODELLED =
        "limit: no model describes LazyInitializer.EnsureInitialized, so its factory runs in an unknown execution (ADR 0011) at each " +
        "call, overlapping itself across the two services, whether or not the call is given the lock it initializes under";

    /// <summary>A double-checked lazy field: the check outside the lock, again inside it, the write inside it.</summary>
    private const string DOUBLE_CHECKED = """
        public sealed class Cache
        {
            private readonly object _gate = new();
            private Box? _box;
            public Box Get()
            {
                if (/*@fast*/_box == null) { lock (_gate) { if (/*@check*/_box == null) /*@write*/_box = new Box(); } }
                return /*@return*/_box;
            }
        }
        """;

    /// <summary>A double-checked lazy field that stores the object before it sets its value.</summary>
    private const string DOUBLE_CHECKED_PUBLISHED_BEFORE_INIT = """
        public sealed class Cache
        {
            private readonly object _gate = new();
            private Box? _box;
            public Box Get()
            {
                if (/*@fast*/_box == null) { lock (_gate) { if (/*@check*/_box == null) { /*@write*/_box = new Box(); /*@init*/_box.Value = 1; } } }
                return /*@return*/_box;
            }
        }
        """;

    /// <summary>The same lazy field with every read and the write under the lock.</summary>
    private const string LOCKED_THROUGHOUT = """
        public sealed class Cache
        {
            private readonly object _gate = new();
            private Box? _box;
            public Box Get() { lock (_gate) { if (_box == null) _box = new Box(); return _box; } }
        }
        """;

    /// <summary>The lost update of two check-then-act operations on <c>Cache._box</c> in <paramref name="member"/>.</summary>
    /// <param name="field">The field's name as the analysis writes it.</param>
    /// <param name="member">The member that checks and writes it.</param>
    private static string CheckThenAct(string field, string member) =>
        $"DCA1002 unprotected {field}: ReadModifyWrite {member} | ReadModifyWrite {member}";

    /// <summary>The two unlocked reads of a double-checked field, on the fast path and in the <c>return</c>, each against the locked
    /// write.</summary>
    private static readonly string[] UnlockedReadsOfBox =
        ["DCA1003 partial _box: Read Cache.Get @fast | Write Cache.Get @write", "DCA1003 partial _box: Read Cache.Get @return | Write Cache.Get @write"];

    /// <summary>The finding a lazy factory's <c>_tally.Value++</c> calls for when the factory may run on two threads at once.</summary>
    private const string FACTORY_RUNS_TWICE = "DCA1002 unprotected Value: ReadModifyWrite Holder..ctor | ReadModifyWrite Holder..ctor";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["double-checked-locking"] = (Cache(DOUBLE_CHECKED, "_ = _cache.Get();"), UnlockedReadsOfBox),
        ["double-checked-locking-published-before-init"] = (Cache(DOUBLE_CHECKED_PUBLISHED_BEFORE_INIT, "_ = _cache.Get().Value;"),
            ["DCA1003 partial Value: Read FirstWorker.ExecuteAsync | Write Cache.Get @init", "DCA1003 partial Value: Read SecondWorker.ExecuteAsync | Write Cache.Get @init",
             .. UnlockedReadsOfBox]),
        ["double-checked-locking-control"] = (Cache(LOCKED_THROUGHOUT, "_ = _cache.Get();"), []),
        ["coalesce-assignment"] = (Cache("public sealed class Cache { private Box? _box; public Box Box => _box ??= new Box(); }", "_ = _cache.Box;"),
            [CheckThenAct("_box", "Cache.get_Box")]),
        ["coalesce-assignment-control"] = (Cache("""
                                                 public sealed class Cache
                                                 {
                                                     private readonly object _gate = new();
                                                     private Box? _box;
                                                     public Box Box { get { lock (_gate) return _box ??= new Box(); } }
                                                 }
                                                 """, "_ = _cache.Box;"), []),
        ["field-keyword-coalesce-assignment"] = (Cache("public sealed class Cache { public Box Box => field ??= new Box(); }", "_ = _cache.Box;"),
            [CheckThenAct("<Box>k__BackingField", "Cache.get_Box")]),
        ["field-keyword-coalesce-assignment-control"] = (Cache("""
                                                               public sealed class Cache
                                                               {
                                                                   private readonly object _gate = new();
                                                                   public Box Box { get { lock (_gate) return field ??= new Box(); } }
                                                               }
                                                               """, "_ = _cache.Box;"), []),
        ["null-check-then-write-then-read"] = (Cache(
            "public sealed class Cache { private Box? _box; public Box Box { get { if (/*@check*/_box == null) /*@write*/_box = new Box(); return /*@return*/_box; } } }",
            "_ = _cache.Box;"),
            [CheckThenAct("_box", "Cache.get_Box @write"), "DCA1001 unprotected _box: Read Cache.get_Box @return | ReadModifyWrite Cache.get_Box @write"]),
        ["null-check-then-write"] = (Cache(
            "public sealed class Cache { private Box? _box; public void Ensure() { if (_box == null) _box = new Box(); } }", "_cache.Ensure();"),
            [CheckThenAct("_box", "Cache.Ensure")]),
        ["lazy-default"] = (Lazy("new Lazy<Box>(() => { _tally.Value++; return new Box(); })"), []),
        ["lazy-execution-and-publication"] = (Lazy("new Lazy<Box>(() => { _tally.Value++; return new Box(); }, LazyThreadSafetyMode.ExecutionAndPublication)"), []),
        ["lazy-publication-only"] = (Lazy("new Lazy<Box>(() => { _tally.Value++; return new Box(); }, LazyThreadSafetyMode.PublicationOnly)"),
                                     [FACTORY_RUNS_TWICE]),
        ["lazy-none"] = (Lazy("new Lazy<Box>(() => { _tally.Value++; return new Box(); }, LazyThreadSafetyMode.None)"), [FACTORY_RUNS_TWICE]),
        ["lazy-not-thread-safe"] = (Lazy("new Lazy<Box>(() => { _tally.Value++; return new Box(); }, isThreadSafe: false)"), [FACTORY_RUNS_TWICE]),
        ["lazy-factory-reads-a-readonly-field-set-before-it"] =
            (Holder("private readonly int _seed; private readonly Lazy<Box> _box;",
                    "public Holder() { _seed = 5; _box = new Lazy<Box>(() => new Box { Value = _seed }); }"), []),
        ["lazy-factory-reads-a-readonly-field-set-after-it"] =
            (Holder("private readonly int _seed; private readonly Lazy<Box> _box;",
                    "public Holder() { _box = new Lazy<Box>(() => new Box { Value = _seed }); _seed = 5; }"), []),
        ["lazy-factory-counts-its-runs-in-a-field"] =
            (Holder("private int _runs; private readonly Lazy<Box> _box;",
                    "public Holder() { _box = new Lazy<Box>(() => { _runs++; return new Box(); }); }"), []),
        ["lazy-field-initializer-reads-a-primary-constructor-parameter"] = (new("Holder", """
            public sealed class Holder(Tally tally)
            {
                private readonly Lazy<Box> _box = new(() => new Box { Value = tally.Value });
                public Box Get() => _box.Value;
            }
            """, "_ = _holder.Get();", "_ = _holder.Get();", "services.AddSingleton<Tally>();"), []),
        ["lazy-field-initializer-reads-nothing"] =
            (Holder("private readonly Lazy<Box> _box = new(() => new Box());", string.Empty), []),
        ["static-lazy-reads-a-static-readonly-field"] =
            (StaticHolder("private static readonly int s_seed = 5; private static readonly Lazy<Box> s_box = new(() => new Box { Value = s_seed });"), []),
        ["static-lazy-counts-its-runs"] =
            (StaticHolder("private static int s_runs; private static readonly Lazy<Box> s_box = new(() => { s_runs++; return new Box(); });"), []),
        ["static-lazy-publication-only-counts-its-runs"] =
            (StaticHolder("private static int s_runs; private static readonly Lazy<Box> s_box; " +
                          "static Holder() { s_box = new(() => { s_runs++; return new Box(); }, LazyThreadSafetyMode.PublicationOnly); }"),
             ["DCA1002 unprotected s_runs: ReadModifyWrite Holder..cctor | ReadModifyWrite Holder..cctor"]),
        ["ensure-initialized"] = (Initializer("private Box? _box;", "LazyInitializer.EnsureInitialized(ref _box, () => { _tally.Value++; return new Box(); })"),
                                  ["DCA1002 unprotected Value: ReadModifyWrite Holder.Get | ReadModifyWrite Holder.Get"]),
        ["ensure-initialized-with-lock"] = (Initializer("private Box? _box; private bool _initialized; private object? _sync;",
                                                        "LazyInitializer.EnsureInitialized(ref _box, ref _initialized, ref _sync, () => { _tally.Value++; return new Box(); })!"),
                                            [])
    };

    /// <summary>The answer the analysis gives to the lazy probes whose factory reads <c>_tally</c> its constructor wrote.</summary>
    private static readonly string[] FactoryReadsTally = ["DCA1001 unprotected _tally: Read Holder..ctor | Write Holder..ctor"];

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["coalesce-assignment"] = new(["DCA1001 unprotected _box: Read Cache.get_Box | Write Cache.get_Box",
                                       "DCA1001 unprotected _box: Write Cache.get_Box | Write Cache.get_Box"], NULL_CHECK_IS_NO_DEPENDENCY),
        ["field-keyword-coalesce-assignment"] = new(["DCA1001 unprotected <Box>k__BackingField: Read Cache.get_Box | Write Cache.get_Box",
                                                     "DCA1001 unprotected <Box>k__BackingField: Write Cache.get_Box | Write Cache.get_Box"], NULL_CHECK_IS_NO_DEPENDENCY),
        ["null-check-then-write-then-read"] = new(["DCA1001 unprotected _box: Read Cache.get_Box @check | Write Cache.get_Box @write",
                                                   "DCA1001 unprotected _box: Read Cache.get_Box @return | Write Cache.get_Box @write",
                                                   "DCA1001 unprotected _box: Write Cache.get_Box @write | Write Cache.get_Box @write"], NULL_CHECK_IS_NO_DEPENDENCY),
        ["null-check-then-write"] = new(["DCA1001 unprotected _box: Read Cache.Ensure | Write Cache.Ensure",
                                         "DCA1001 unprotected _box: Write Cache.Ensure | Write Cache.Ensure"], NULL_CHECK_IS_NO_DEPENDENCY),
        ["lazy-default"] = new(FactoryReadsTally, LAZY_IS_NOT_MODELLED),
        ["lazy-execution-and-publication"] = new(FactoryReadsTally, LAZY_IS_NOT_MODELLED),
        ["lazy-publication-only"] = new(FactoryReadsTally, LAZY_IS_NOT_MODELLED),
        ["lazy-none"] = new(FactoryReadsTally, LAZY_IS_NOT_MODELLED),
        ["lazy-not-thread-safe"] = new(FactoryReadsTally, LAZY_IS_NOT_MODELLED),
        ["lazy-factory-reads-a-readonly-field-set-before-it"] = new(["DCA1001 unprotected _seed: Read Holder..ctor | Write Holder..ctor"], LAZY_IS_NOT_MODELLED),
        ["lazy-factory-reads-a-readonly-field-set-after-it"] = new(["DCA1001 unprotected _seed: Read Holder..ctor | Write Holder..ctor"], LAZY_IS_NOT_MODELLED),
        ["lazy-field-initializer-reads-a-primary-constructor-parameter"] = new(["DCA1001 unprotected tally: Read Holder..ctor | Write Holder..ctor"],
                                                                               LAZY_IS_NOT_MODELLED),
        ["static-lazy-publication-only-counts-its-runs"] = new([], LAZY_IS_NOT_MODELLED),
        ["ensure-initialized-with-lock"] = new(["DCA1002 unprotected Value: ReadModifyWrite Holder.Get | ReadModifyWrite Holder.Get"],
                                               LAZY_INITIALIZER_IS_NOT_MODELLED)
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

    /// <summary>A probe of <c>Cache</c> whose two hosted services run the same statement.</summary>
    /// <param name="types">The declaration of <c>Cache</c>.</param>
    /// <param name="statement">The statement both run.</param>
    private static HostedServiceProbe Cache(string types, string statement) => new("Cache", types, statement, statement);

    /// <summary>A probe of a holder whose <c>Lazy&lt;Box&gt;</c>, made in its constructor, has a factory that counts its runs in a
    /// singleton <c>Tally</c>; both hosted services read the value.</summary>
    /// <param name="creation">The expression that makes the <c>Lazy&lt;Box&gt;</c>.</param>
    private static HostedServiceProbe Lazy(string creation) => new("Holder", $$"""
        public sealed class Holder
        {
            private readonly Tally _tally;
            private readonly Lazy<Box> _box;
            public Holder(Tally tally) { _tally = tally; _box = {{creation}}; }
            public Box Get() => _box.Value;
        }
        """, "_ = _holder.Get();", "_ = _holder.Get();", "services.AddSingleton<Tally>();");

    /// <summary>A probe of a <c>Holder</c> with the given fields and constructor whose <c>Get</c> returns <c>_box.Value</c>, read by
    /// both hosted services.</summary>
    /// <param name="fields">The holder's fields, among them <c>_box</c>, a <c>Lazy&lt;Box&gt;</c>.</param>
    /// <param name="constructor">The holder's constructor, or nothing.</param>
    private static HostedServiceProbe Holder(string fields, string constructor) => new("Holder", $$"""
        public sealed class Holder
        {
            {{fields}}
            {{constructor}}
            public Box Get() => _box.Value;
        }
        """, "_ = _holder.Get();", "_ = _holder.Get();");

    /// <summary>A probe of a <c>Holder</c> whose static <c>Get</c> returns <c>s_box.Value</c>, called by both hosted services.</summary>
    /// <param name="members">The holder's static fields, among them <c>s_box</c>, a <c>Lazy&lt;Box&gt;</c>, and its static
    /// constructor where it has one.</param>
    private static HostedServiceProbe StaticHolder(string members) => new("Holder", $$"""
        public sealed class Holder
        {
            {{members}}
            public static Box Get() => s_box.Value;
        }
        """, "_ = Holder.Get();", "_ = Holder.Get();");

    /// <summary>A probe of a holder that initializes a field through <c>LazyInitializer</c> with a factory that counts its runs in a
    /// singleton <c>Tally</c>; both hosted services read the value.</summary>
    /// <param name="fields">The holder's fields besides the tally.</param>
    /// <param name="call">The call that initializes the field and returns its value.</param>
    private static HostedServiceProbe Initializer(string fields, string call) => new("Holder", $$"""
        public sealed class Holder
        {
            private readonly Tally _tally;
            {{fields}}
            public Holder(Tally tally) => _tally = tally;
            public Box Get() => {{call}};
        }
        """, "_ = _holder.Get();", "_ = _holder.Get();", "services.AddSingleton<Tally>();");
}
