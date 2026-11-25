using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of protections that fail while they look like protection (issue 532), each with what run time calls for.
/// Every probe is one singleton <c>Registry</c> shared by two hosted services, the first running <c>Add</c> and the second
/// <c>Reset</c> unless the probe says otherwise, analysed through the whole pipeline (<see cref="HostedServiceProbe"/>). The truths
/// rest on these guarantees. A lock statement takes the monitor of the object its expression gives, so two sides exclude each other
/// exactly when they lock one object (Learn, <c>lock</c> statement): <c>typeof(T)</c> gives the one <c>Type</c> object of
/// <c>T</c> («two Type object references refer to the same object if and only if they represent the same type», Learn, <c>Type</c>
/// remarks); equal string literals of one assembly are one instance (C# specification, string literals); <c>(object)_id</c> boxes a
/// new object at each evaluation (C# specification, boxing conversions); <c>this</c> is the singleton itself.
/// <c>Monitor.TryEnter(object, int)</c>, <c>Lock.TryEnter()</c>, <c>SemaphoreSlim.Wait(int)</c> and
/// <c>ReaderWriterLockSlim.TryEnterWriteLock(int)</c> return whether they entered, and code that goes on without testing the result
/// may run without the lock (Learn, each method); <c>SemaphoreSlim.Wait(CancellationToken)</c> returns only once it entered and
/// throws <c>OperationCanceledException</c> otherwise (Learn); a <c>SemaphoreSlim(1, 2)</c> lets in one holder while every
/// <c>Release</c> follows its own <c>Wait</c>, the maximum bounding only the count <c>Release</c> may reach (Learn, constructor);
/// a lambda handed to <c>Task.Run</c> inside a protected region runs later, on the thread pool, without what the caller held.
/// <c>SpinLock</c> is a mutable struct, and a method called on a <c>readonly</c> field of struct type runs on a copy, so each
/// <c>Enter</c> takes a lock of its own («do not store SpinLock instances in readonly fields», Learn, <c>SpinLock</c> remarks).
/// <c>Volatile</c> reads and writes are atomic and ordered, but a read followed by a write of the value read is two operations, as
/// is <c>++</c> on a <c>volatile</c> field (Learn, <c>Volatile</c> and the <c>volatile</c> keyword). <c>ConcurrentDictionary</c>
/// calls the delegates of <c>AddOrUpdate</c> and <c>GetOrAdd</c> outside its locks («delegates for these methods are called outside
/// the locks», Learn, thread-safe collections), and a value it holds is shared by every caller that gets it. A frozen dictionary is
/// immutable once made (Learn, <c>FrozenDictionary</c>). <c>MethodImplOptions.Synchronized</c>: «the method can be executed by only one
/// thread at a time. Static methods lock on the type, whereas instance methods lock on the instance» (Learn). The protection a
/// finding names follows SPEC TD-083: sides that lock different objects give <c>different-identity</c>, a side without a proven
/// acquisition <c>partial</c>, sides holding nothing <c>unprotected</c>. Every truth is the behaviour of the program the fixture stands
/// for, its registrations taken as written. A probe the analysis answers otherwise is named in <see cref="Differences"/> with its
/// answer and why.</summary>
public sealed class ProtectionFailureProbeTests
{
    /// <summary>The lost update of <c>Add</c>'s <c>_count++</c> against <c>Reset</c>'s write under locks on different objects.</summary>
    private const string DIFFERENT_OBJECTS = "DCA1002 different-identity _count: ReadModifyWrite Registry.Add | Write Registry.Reset";

    /// <summary>The lost update of <c>Add</c>'s <c>_count++</c>, run without a proven acquisition, against <c>Reset</c>'s locked write.</summary>
    private const string ADD_UNPROVEN = "DCA1002 partial _count: ReadModifyWrite Registry.Add | Write Registry.Reset";

    /// <summary>The lost update of two runs of <c>Add</c>'s <c>_count++</c> with nothing held.</summary>
    private const string ADD_TWICE_UNPROTECTED = "DCA1002 unprotected _count: ReadModifyWrite Registry.Add | ReadModifyWrite Registry.Add";

    /// <summary>The lost update of the two add factories' <c>_calls++</c>: on an empty map both calls may find the key absent.</summary>
    private const string FACTORIES_ADD_ADD = "DCA1002 unprotected _calls: ReadModifyWrite Registry.Add @add | ReadModifyWrite Registry.Add @add";

    /// <summary>The pair of one call's add factory and the other's update factory, which run time orders: the update starts only
    /// once the key is present, after the add factory that made it returned.</summary>
    private const string FACTORIES_ADD_UPDATE = "DCA1002 unprotected _calls: ReadModifyWrite Registry.Add @add | ReadModifyWrite Registry.Add @update";

    /// <summary>The lost update of the two update factories' <c>_calls++</c>: on a key present from the start both calls update.</summary>
    private const string FACTORIES_UPDATE_UPDATE = "DCA1002 unprotected _calls: ReadModifyWrite Registry.Add @update | ReadModifyWrite Registry.Add @update";

    /// <summary>The statements of a probe whose first service runs <c>Add</c> and second <c>Reset</c>.</summary>
    private const string ADD = "_registry.Add();";

    /// <summary>The statement of the second service.</summary>
    private const string RESET = "_registry.Reset();";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["lock-on-typeof-both-sides"] = (Registry("""
                                             private int _count;
                                             public void Add() { lock (typeof(Registry)) { _count++; } }
                                             public void Reset() { lock (typeof(Registry)) { _count = 0; } }
                                             """), []),
        ["lock-on-two-types"] = (Registry("""
                                     private int _count;
                                     public void Add() { lock (typeof(Registry)) { _count++; } }
                                     public void Reset() { lock (typeof(Box)) { _count = 0; } }
                                     """), [DIFFERENT_OBJECTS]),
        ["lock-on-a-string-literal"] = (Registry("""
                                            private int _count;
                                            public void Add() { lock ("registry") { _count++; } }
                                            public void Reset() { lock ("registry") { _count = 0; } }
                                            """), []),
        ["lock-on-this-both-sides"] = (Registry("""
                                           private int _count;
                                           public void Add() { lock (this) { _count++; } }
                                           public void Reset() { lock (this) { _count = 0; } }
                                           """), []),
        ["lock-on-this-against-a-gate"] = (Registry("""
                                               private readonly object _gate = new();
                                               private int _count;
                                               public void Add() { lock (this) { _count++; } }
                                               public void Reset() { lock (_gate) { _count = 0; } }
                                               """), [DIFFERENT_OBJECTS]),
        ["lock-on-a-boxed-field"] = (Registry("""
                                         private readonly int _id = 1;
                                         private int _count;
                                         public void Add() { lock ((object)_id) { _count++; } }
                                         """, ADD, ADD),
                                     ["DCA1002 different-identity _count: ReadModifyWrite Registry.Add | ReadModifyWrite Registry.Add"]),
        ["monitor-try-enter-ms-unchecked"] = (Registry("""
                                                  private readonly object _gate = new();
                                                  private int _count;
                                                  public void Add() { Monitor.TryEnter(_gate, 100); _count++; Monitor.Exit(_gate); }
                                                  public void Reset() { lock (_gate) { _count = 0; } }
                                                  """), [ADD_UNPROVEN]),
        ["monitor-try-enter-ms-checked"] = (Registry("""
                                                private readonly object _gate = new();
                                                private int _count;
                                                public void Add() { if (Monitor.TryEnter(_gate, 100)) { try { _count++; } finally { Monitor.Exit(_gate); } } }
                                                public void Reset() { lock (_gate) { _count = 0; } }
                                                """), []),
        ["two-lock-objects"] = (Registry("""
                                    private readonly Lock _first = new();
                                    private readonly Lock _second = new();
                                    private int _count;
                                    public void Add() { lock (_first) { _count++; } }
                                    public void Reset() { lock (_second) { _count = 0; } }
                                    """), [DIFFERENT_OBJECTS]),
        ["lock-try-enter-unchecked"] = (Registry("""
                                            private readonly Lock _gate = new();
                                            private int _count;
                                            public void Add() { _gate.TryEnter(); _count++; _gate.Exit(); }
                                            public void Reset() { lock (_gate) { _count = 0; } }
                                            """), [ADD_UNPROVEN]),
        ["lock-try-enter-checked"] = (Registry("""
                                          private readonly Lock _gate = new();
                                          private int _count;
                                          public void Add() { if (_gate.TryEnter()) { try { _count++; } finally { _gate.Exit(); } } }
                                          public void Reset() { lock (_gate) { _count = 0; } }
                                          """), []),
        ["lock-scope-around-a-task-run"] = (Registry("""
                                                private readonly Lock _gate = new();
                                                private int _count;
                                                public void Add() { lock (_gate) { Task.Run(() => _count++); } }
                                                public void Reset() { lock (_gate) { _count = 0; } }
                                                """), [ADD_UNPROVEN]),
        ["spin-lock-in-a-readonly-field"] = (Registry("""
                                                 private readonly SpinLock _gate = new(false);
                                                 private int _count;
                                                 public void Add() { var taken = false; try { _gate.Enter(ref taken); _count++; } finally { if (taken) _gate.Exit(); } }
                                                 public void Reset() { var taken = false; try { _gate.Enter(ref taken); _count = 0; } finally { if (taken) _gate.Exit(); } }
                                                 """), ["DCA1002 unprotected _count: ReadModifyWrite Registry.Add | Write Registry.Reset"]),
        ["two-mutexes"] = (Registry("""
                               private readonly Mutex _first = new();
                               private readonly Mutex _second = new();
                               private int _count;
                               public void Add() { _first.WaitOne(); try { _count++; } finally { _first.ReleaseMutex(); } }
                               public void Reset() { _second.WaitOne(); try { _count = 0; } finally { _second.ReleaseMutex(); } }
                               """), [DIFFERENT_OBJECTS]),
        ["mutex-against-a-plain-write"] = (Registry("""
                                               private readonly Mutex _gate = new();
                                               private int _count;
                                               public void Add() { _gate.WaitOne(); try { _count++; } finally { _gate.ReleaseMutex(); } }
                                               public void Reset() { _count = 0; }
                                               """), ["DCA1002 partial _count: ReadModifyWrite Registry.Add | Write Registry.Reset"]),
        ["semaphore-wait-ms-unchecked"] = (Registry("""
                                               private readonly SemaphoreSlim _gate = new(1, 1);
                                               private int _count;
                                               public void Add() { _gate.Wait(100); try { _count++; } finally { _gate.Release(); } }
                                               public void Reset() { _gate.Wait(); try { _count = 0; } finally { _gate.Release(); } }
                                               """), [ADD_UNPROVEN]),
        ["semaphore-wait-ms-checked"] = (Registry("""
                                             private readonly SemaphoreSlim _gate = new(1, 1);
                                             private int _count;
                                             public void Add() { if (_gate.Wait(100)) { try { _count++; } finally { _gate.Release(); } } }
                                             public void Reset() { _gate.Wait(); try { _count = 0; } finally { _gate.Release(); } }
                                             """), []),
        ["semaphore-wait-token"] = (Registry("""
                                        private readonly SemaphoreSlim _gate = new(1, 1);
                                        private int _count;
                                        public void Add(CancellationToken token) { _gate.Wait(token); try { _count++; } finally { _gate.Release(); } }
                                        public void Reset() { _gate.Wait(); try { _count = 0; } finally { _gate.Release(); } }
                                        """, "_registry.Add(stoppingToken);", RESET), []),
        ["semaphore-one-of-two"] = (Registry("""
                                        private readonly SemaphoreSlim _gate = new(1, 2);
                                        private int _count;
                                        public void Add() { _gate.Wait(); try { _count++; } finally { _gate.Release(); } }
                                        public void Reset() { _gate.Wait(); try { _count = 0; } finally { _gate.Release(); } }
                                        """), []),
        ["semaphore-around-a-task-run"] = (Registry("""
                                               private readonly SemaphoreSlim _gate = new(1, 1);
                                               private int _count;
                                               public void Add() { _gate.Wait(); try { Task.Run(() => _count++); } finally { _gate.Release(); } }
                                               public void Reset() { _gate.Wait(); try { _count = 0; } finally { _gate.Release(); } }
                                               """), [ADD_UNPROVEN]),
        ["semaphore-class-of-one"] = (Registry("""
                                          private readonly Semaphore _gate = new(1, 1);
                                          private int _count;
                                          public void Add() { _gate.WaitOne(); try { _count++; } finally { _gate.Release(); } }
                                          public void Reset() { _gate.WaitOne(); try { _count = 0; } finally { _gate.Release(); } }
                                          """), []),
        ["two-reader-writer-locks"] = (Registry("""
                                           private readonly ReaderWriterLockSlim _first = new();
                                           private readonly ReaderWriterLockSlim _second = new();
                                           private int _count;
                                           public void Add() { _first.EnterWriteLock(); try { _count++; } finally { _first.ExitWriteLock(); } }
                                           public void Reset() { _second.EnterWriteLock(); try { _count = 0; } finally { _second.ExitWriteLock(); } }
                                           """), [DIFFERENT_OBJECTS]),
        ["write-lock-against-a-plain-write"] = (Registry("""
                                                    private readonly ReaderWriterLockSlim _gate = new();
                                                    private int _count;
                                                    public void Add() { _gate.EnterWriteLock(); try { _count++; } finally { _gate.ExitWriteLock(); } }
                                                    public void Reset() { _count = 0; }
                                                    """), ["DCA1002 partial _count: ReadModifyWrite Registry.Add | Write Registry.Reset"]),
        ["try-enter-write-lock-unchecked"] = (Registry("""
                                                  private readonly ReaderWriterLockSlim _gate = new();
                                                  private int _count;
                                                  public void Add() { _gate.TryEnterWriteLock(100); _count++; _gate.ExitWriteLock(); }
                                                  public void Reset() { _gate.EnterWriteLock(); try { _count = 0; } finally { _gate.ExitWriteLock(); } }
                                                  """), [ADD_UNPROVEN]),
        ["try-enter-write-lock-checked"] = (Registry("""
                                                private readonly ReaderWriterLockSlim _gate = new();
                                                private int _count;
                                                public void Add() { if (_gate.TryEnterWriteLock(100)) { try { _count++; } finally { _gate.ExitWriteLock(); } } }
                                                public void Reset() { _gate.EnterWriteLock(); try { _count = 0; } finally { _gate.ExitWriteLock(); } }
                                                """), []),
        ["volatile-read-then-write"] = (Registry("""
                                            private int _count;
                                            public void Add() { Volatile.Write(ref _count, Volatile.Read(ref _count) + 1); }
                                            """, ADD, ADD), [ADD_TWICE_UNPROTECTED]),
        ["volatile-field-increment"] = (Registry("""
                                            private volatile int _count;
                                            public void Add() { _count++; }
                                            """, ADD, ADD), [ADD_TWICE_UNPROTECTED]),
        ["add-or-update-on-an-empty-map"] = (Registry("""
                                                 private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _map = new();
                                                 private int _calls;
                                                 public void Add() => _map.AddOrUpdate("k", _ => { /*@add*/_calls++; return 1; }, (_, v) => { /*@update*/_calls++; return v + 1; });
                                                 """, ADD, ADD),
                                             [FACTORIES_ADD_ADD]),
        ["add-or-update-on-a-present-key"] = (Registry("""
                                                  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _map = new() { ["k"] = 0 };
                                                  private int _calls;
                                                  public void Add() => _map.AddOrUpdate("k", _ => { /*@add*/_calls++; return 1; }, (_, v) => { /*@update*/_calls++; return v + 1; });
                                                  """, ADD, ADD),
                                              [FACTORIES_UPDATE_UPDATE]),
        ["frozen-dictionary-read-by-both"] = (Registry("""
                                                  private readonly System.Collections.Frozen.FrozenDictionary<string, int> _map =
                                                      System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, int> { ["a"] = 1 });
                                                  public int Get() => _map.TryGetValue("a", out var value) ? value : 0;
                                                  """, "_registry.Get();", "_registry.Get();"), []),
        ["synchronized-both-sides"] = (Registry("""
                                           private int _count;
                                           [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                           public void Add() { _count++; }
                                           [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                           public void Reset() { _count = 0; }
                                           """), []),
        ["synchronized-against-lock-this"] = (Registry("""
                                                  private int _count;
                                                  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                                  public void Add() { _count++; }
                                                  public void Reset() { lock (this) { _count = 0; } }
                                                  """), []),
        ["synchronized-against-a-plain-write"] = (Registry("""
                                                      private int _count;
                                                      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                                      public void Add() { _count++; }
                                                      public void Reset() { _count = 0; }
                                                      """), ["DCA1002 partial _count: ReadModifyWrite Registry.Add | Write Registry.Reset"]),
        ["synchronized-static-and-instance"] = (Registry("""
                                                    private static int s_count;
                                                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                                    public static void AddStatic() { s_count++; }
                                                    public void Add() => AddStatic();
                                                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
                                                    public void Reset() { s_count = 0; }
                                                    """), ["DCA1002 different-identity s_count: ReadModifyWrite Registry.AddStatic | Write Registry.Reset"])
    };

    /// <summary>Why a lock on <c>typeof(T)</c> or on a string literal gives <c>different-identity</c> on both sides.</summary>
    private const string UNRESOLVED_LOCK_OBJECT =
        "issue 536: the lock's object points to no region - typeof(T) has no case in IrLowering.LowerValue and lowers as an " +
        "IrUnknownOperation, a string literal as a constant with no defining operation, and MethodSummaryBuilder.OwnPoints gives " +
        "neither a point - so InterproceduralAccesses.Effect keys the lock as unresolved per instance and site with no " +
        "single-object id, and PairProtection.Of reads two sides that each hold a lock and share no id as different-identity";

    /// <summary>Why both factories of <c>AddOrUpdate</c> pair with both of the other call's, whatever the key's presence.</summary>
    private const string FACTORIES_WITHOUT_KEY_GUARD =
        "issue 538: WholeProgram.RunFactories visits every factory an AddOrUpdate call " +
        "is handed, at every call, with no guard on the key's presence (SPEC TD-085 runs the factories at the call site, in the " +
        "calling execution), so the add and the update factory of one call each pair with both of the other's; at run time the add " +
        "factory runs only while the key is absent and the update factory only once it is present, so two calls on an empty map " +
        "overlap only in their add factories, the update factory of the second starting after the first inserted the key, and on " +
        "a key present from the start and never removed only the update factories run";

    /// <summary>Why a <c>[MethodImpl(MethodImplOptions.Synchronized)]</c> method holds nothing.</summary>
    private const string SYNCHRONIZED_NOT_MODELLED =
        "limit: MethodImplOptions.Synchronized is read nowhere in ConcurrencyHunter.Core or ConcurrencyHunter.Analysis; an acquire " +
        "comes only from a call IrLowering's Synchronization recognizes, and SPEC TD-083 lists the primitives without the attribute";

    /// <summary>Why <c>System.Threading.Semaphore</c> protects nothing.</summary>
    private const string SEMAPHORE_CLASS_NOT_MODELLED =
        "limit: System.Threading.Semaphore is not among the primitives IrLowering's Synchronization.ClaimsType recognizes (Monitor, " +
        "Lock, Mutex, SemaphoreSlim, ReaderWriterLockSlim), and SPEC TD-083 makes an unrecognized type no protection";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why, listed by
    /// hand.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["lock-on-typeof-both-sides"] = new([DIFFERENT_OBJECTS], UNRESOLVED_LOCK_OBJECT),
        ["lock-on-a-string-literal"] = new([DIFFERENT_OBJECTS], UNRESOLVED_LOCK_OBJECT),
        ["synchronized-both-sides"] = new(["DCA1002 unprotected _count: ReadModifyWrite Registry.Add | Write Registry.Reset"], SYNCHRONIZED_NOT_MODELLED),
        ["synchronized-against-lock-this"] = new(["DCA1002 partial _count: ReadModifyWrite Registry.Add | Write Registry.Reset"], SYNCHRONIZED_NOT_MODELLED),
        ["synchronized-against-a-plain-write"] = new(["DCA1002 unprotected _count: ReadModifyWrite Registry.Add | Write Registry.Reset"], SYNCHRONIZED_NOT_MODELLED),
        ["synchronized-static-and-instance"] = new(["DCA1002 unprotected s_count: ReadModifyWrite Registry.AddStatic | Write Registry.Reset"],
                                                   SYNCHRONIZED_NOT_MODELLED),
        ["semaphore-class-of-one"] = new(["DCA1002 unprotected _count: ReadModifyWrite Registry.Add | Write Registry.Reset"], SEMAPHORE_CLASS_NOT_MODELLED),
        ["add-or-update-on-an-empty-map"] = new([FACTORIES_ADD_ADD, FACTORIES_ADD_UPDATE, FACTORIES_UPDATE_UPDATE], FACTORIES_WITHOUT_KEY_GUARD),
        ["add-or-update-on-a-present-key"] = new([FACTORIES_ADD_ADD, FACTORIES_ADD_UPDATE, FACTORIES_UPDATE_UPDATE], FACTORIES_WITHOUT_KEY_GUARD)
    };

    /// <summary>The probes whose truth has no finding because both writes of <c>_count</c> hold one protection, listed by hand.</summary>
    private static readonly string[] Protected =
    [
        "lock-on-this-both-sides", "monitor-try-enter-ms-checked", "lock-try-enter-checked", "semaphore-wait-ms-checked",
        "semaphore-wait-token", "semaphore-one-of-two", "try-enter-write-lock-checked"
    ];

    /// <summary>The probe whose truth has no finding because both services only read.</summary>
    private const string READ_ONLY = "frozen-dictionary-read-by-both";

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
    /// what the record counts, reason by reason, and every probe whose truth has no finding names the accesses it collected.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth()
    {
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
        Assert.Equal(34, Probes.Count);
        Assert.Equal(2, Differences.Count(difference => difference.Value.Reason == UNRESOLVED_LOCK_OBJECT));
        Assert.Equal(4, Differences.Count(difference => difference.Value.Reason == SYNCHRONIZED_NOT_MODELLED));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == SEMAPHORE_CLASS_NOT_MODELLED));
        Assert.Equal(2, Differences.Count(difference => difference.Value.Reason == FACTORIES_WITHOUT_KEY_GUARD));
        Assert.Equal(9, Differences.Count);
        Assert.Equal(Probes.Where(probe => probe.Value.Truth.Length == 0 && !Differences.ContainsKey(probe.Key)).Select(probe => probe.Key).Order(StringComparer.Ordinal),
                     Protected.Append(READ_ONLY).Order(StringComparer.Ordinal));
    }

    /// <summary>The names of the probes whose writes are protected.</summary>
    public static TheoryData<string> ProtectedNames() => new(Protected);

    /// <summary>A protected probe's empty answer is protection: <c>Add</c>'s and <c>Reset</c>'s writes of <c>_count</c> were each
    /// collected once, on one resource, and the pair of them holds a sufficient common protection.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(ProtectedNames))]
    public async Task Protected_writes_are_one_resource_under_a_sufficient_common_protection(string name)
    {
        var accesses = await RawAccessesAsync(Probes[name].Probe);
        var add = Assert.Single(accesses, access => Writes(access, "Registry.Add("));
        var reset = Assert.Single(accesses, access => Writes(access, "Registry.Reset("));

        Assert.Equal(Identity(add.Resource), Identity(reset.Resource));
        Assert.Equal(PairProtection.SUFFICIENT, PairProtection.Of(add, reset));

        static bool Writes(Access access, string member) =>
            access.Resource.AccessPath is ["_count"] && access.Symbol.StartsWith(member, StringComparison.Ordinal);
    }

    /// <summary>The frozen dictionary's empty answer is two reads: the read of the field <c>_map</c> in <c>Get</c> runs in both
    /// services' executions, and the lookup itself makes no access of any kind on the dictionary (why it makes none is not read
    /// here).</summary>
    [Fact]
    public async Task Frozen_dictionary_is_read_by_both_services_and_its_lookup_makes_no_access()
    {
        var accesses = (await RawAccessesAsync(Probes[READ_ONLY].Probe)).Where(access => access.Symbol.StartsWith("Registry.Get(", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Equal(AccessOperation.Read, access.Operation));
        Assert.All(accesses, access => Assert.Equal(["_map"], access.Resource.AccessPath));
        var executions = accesses.SelectMany(access => access.ExecutionIds).ToArray();
        Assert.Contains(executions, id => id.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.Contains(executions, id => id.Contains("SecondWorker", StringComparison.Ordinal));
    }

    /// <summary>The accesses a probe's whole analysis gives, but those its construction makes on what it builds, with their resources,
    /// executions and protections, which <see cref="HostedServiceProbe.AnalyzeAsync(ProbeInputs)"/> writes as text without.</summary>
    /// <param name="probe">The probe.</param>
    private static async Task<Access[]> RawAccessesAsync(HostedServiceProbe probe)
    {
        var text = EngineFixture.Usings + "using System.Collections.Generic;\nusing System.Linq;\n" + probe.Source();
        var solution = FixtureSolution.Create(new FixtureOptions(), [("Case.cs", text)]);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, EngineFixture.ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
        return result.Accesses.Where(access => !access.IsConstructionLocal).ToArray();
    }

    /// <summary>A resource as the identity a pair compares: its region and the path from it.</summary>
    /// <param name="resource">The resource.</param>
    private static string Identity(AccessResource resource) => $"{resource.Region}|{resource.RegionId}|{string.Join(".", resource.AccessPath)}";

    /// <summary>A probe of a <c>Registry</c> with the given members, its first hosted service running <c>Add</c> and its second
    /// <c>Reset</c>, or the statements given.</summary>
    /// <param name="members">The members of <c>Registry</c>.</param>
    /// <param name="first">The statement the first hosted service runs.</param>
    /// <param name="second">The statement the second hosted service runs.</param>
    private static HostedServiceProbe Registry(string members, string first = ADD, string second = RESET) => new("Registry", $$"""
        public sealed class Registry
        {
            {{members}}
        }
        """, first, second);
}
