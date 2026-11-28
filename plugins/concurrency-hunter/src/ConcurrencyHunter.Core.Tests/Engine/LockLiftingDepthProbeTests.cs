using System.Runtime.ExceptionServices;
using ConcurrencyHunter.Accesses;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of a lock lifted through a long chain of calls (issue 561), which goes on on a fresh stack once the thread's
/// runs short (<see cref="FreshStack"/>): an entry, an exit and a holding at a <c>yield return</c> carried to the caller through two
/// and forty calls and enumerated iterators, a monitor lifted over a release of a semaphore whose first entry a deeper call makes, an
/// enumeration of two iterators one of which reaches that entry through a chain, calls that come back to the chain's start, recursively
/// or through forty methods, and chains of hundreds of calls on a small stack, on one worker and on four. A call carries what its callee leaves open or closes, so after a chain of
/// calls whose last one enters a semaphore of capacity one the caller holds it, and after one whose last one releases it the caller
/// holds it no longer ("SemaphoreSlim Class", Learn: <c>Wait</c> blocks until the count is above zero and takes one, <c>Release</c>
/// gives one back). A recursive chain whose argument counts down to zero runs its last branch exactly once, so what that branch does is
/// what the whole call does. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>): the first hosted service
/// calls <c>Run</c>, which writes <c>Value</c> at the site marked <c>run</c>, and the second writes it under the semaphore in
/// <c>Touch</c>; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class LockLiftingDepthProbeTests
{
    /// <summary>The finding of the write in <c>Run</c>, not under the semaphore, against the one under it in <c>Touch</c>.</summary>
    private const string PARTIAL = "DCA1003 partial Value: Write Gated.Run @run | Write Gated.Touch @touch";

    /// <summary>Why a recursion that enters the semaphore on its last step leaves its caller holding nothing.</summary>
    private const string RECURSION =
        "issue 562: a call back into a method whose lift is in progress lifts nothing, so the path through the recursive call holds " +
        "nothing at the method's exit and the entry its last step makes is lost to the caller";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["acquire-lifted-through-two-calls"] = (Gated(
            Chain(2, "private void E{0}() => E{1}();", "private void E{0}() => _gate.Wait();"),
            "E0(); try { /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["acquire-lifted-through-forty-calls"] = (Gated(
            Chain(40, "private void E{0}() => E{1}();", "private void E{0}() => _gate.Wait();"),
            "E0(); try { /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["release-lifted-through-two-calls"] = (Gated(
            Chain(2, "private void X{0}() => X{1}();", "private void X{0}() => _gate.Release();"),
            "_gate.Wait(); X0(); /*@run*/Value = 1;"), [PARTIAL]),
        ["release-lifted-through-forty-calls"] = (Gated(
            Chain(40, "private void X{0}() => X{1}();", "private void X{0}() => _gate.Release();"),
            "_gate.Wait(); X0(); /*@run*/Value = 1;"), [PARTIAL]),
        ["held-at-yield-through-two-iterators"] = (Gated(
            Chain(2, "private IEnumerable<int> I{0}() {{ foreach (var x in I{1}()) yield return x; }}", HELD_AT_YIELD),
            "foreach (var x in I0()) /*@run*/Value = x;"), []),
        ["held-at-yield-through-forty-iterators"] = (Gated(
            Chain(40, "private IEnumerable<int> I{0}() {{ foreach (var x in I{1}()) yield return x; }}", HELD_AT_YIELD),
            "foreach (var x in I0()) /*@run*/Value = x;"), []),
        ["self-recursion-acquires-at-its-end"] = (Gated(
            "private void E0(int n) { if (n > 0) E0(n - 1); else _gate.Wait(); }",
            "E0(1); try { /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["recursion-through-two-acquires-at-its-end"] = (Gated(
            Chain(2, "private void E{0}(int n) => E{1}(n);", "private void E{0}(int n) {{ if (n > 0) E0(n - 1); else _gate.Wait(); }}"),
            "E0(1); try { /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["recursion-through-forty-acquires-at-its-end"] = (Gated(
            Chain(40, "private void E{0}(int n) => E{1}(n);", "private void E{0}(int n) {{ if (n > 0) E0(n - 1); else _gate.Wait(); }}"),
            "E0(1); try { /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["monitor-lifted-over-a-release-and-two-calls"] = (Monitored(
            Chain(2, "private void E{0}() => E{1}();", RELEASED_AT_THE_END),
            "Enter(); try { /*@run*/Value = 1; } finally { Monitor.Exit(_monitor); }"), []),
        ["monitor-lifted-over-a-release-and-forty-calls"] = (Monitored(
            Chain(40, "private void E{0}() => E{1}();", RELEASED_AT_THE_END),
            "Enter(); try { /*@run*/Value = 1; } finally { Monitor.Exit(_monitor); }"), []),
        ["may-release-iterator-beside-a-two-call-iterator"] = (Gated(
            TwoIterators(2), "Enumerate(); _gate.Wait(); try { Enumerate(); /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["may-release-iterator-beside-a-36-call-iterator"] = (Gated(
            TwoIterators(36), "Enumerate(); _gate.Wait(); try { Enumerate(); /*@run*/Value = 1; } finally { _gate.Release(); }"), []),
        ["cycle-through-two-may-release"] = (Gated(
            MayRelease(2), "_gate.Wait(); B(1); /*@run*/Value = 1;"), [PARTIAL]),
        ["cycle-through-forty-may-release"] = (Gated(
            MayRelease(40), "_gate.Wait(); B(1); /*@run*/Value = 1;"), [PARTIAL]),
        ["cycle-through-two-entered-at-its-release"] = (Gated(
            MayRelease(2), "_gate.Wait(); A(1); _gate.Wait(); B(1); /*@run*/Value = 1;"), [PARTIAL]),
        ["cycle-through-forty-entered-at-its-release"] = (Gated(
            MayRelease(40), "_gate.Wait(); A(1); _gate.Wait(); B(1); /*@run*/Value = 1;"), [PARTIAL])
    };

    /// <summary>The last iterator of a chain: it holds the semaphore at its one <c>yield return</c> and lets it go in a <c>finally</c>.</summary>
    private const string HELD_AT_YIELD =
        "private IEnumerable<int> I{0}() {{ _gate.Wait(); try {{ yield return 1; }} finally {{ _gate.Release(); }} }}";

    /// <summary>The last method of a chain that takes the semaphore and gives it back.</summary>
    private const string RELEASED_AT_THE_END = "private void E{0}() {{ _gate.Wait(); _gate.Release(); }}";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["self-recursion-acquires-at-its-end"] = new([PARTIAL], RECURSION),
        ["recursion-through-two-acquires-at-its-end"] = new([PARTIAL], RECURSION),
        ["recursion-through-forty-acquires-at-its-end"] = new([PARTIAL], RECURSION)
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

    /// <summary>How deep the chains of <see cref="Lifting_through_a_chain_deeper_than_its_stack_completes"/> are.</summary>
    private const int DEEP_LEVELS = 300;

    /// <summary>The stack of the thread the deep chains are analysed on, in bytes: a lift made by recursion over the chain on one
    /// stack, five to seven kilobytes a level, needs half as much again or more at <see cref="DEEP_LEVELS"/> levels, so it goes on on a
    /// fresh stack (<see cref="FreshStack"/>), while the binding of the chain's references, about two kilobytes a level and with no
    /// fresh stack of its own, fits.</summary>
    private const int SMALL_STACK = 1024 * 1024;

    /// <summary>A chain of calls deeper than a lift made by recursion fits in the thread's stack is analysed as far as its pairs, with
    /// the answer of the chain's form: the issue's chain of awaited calls makes the race of its last callee's write against the second
    /// hosted service's, and an entry made at the end of a chain of calls or held at the <c>yield return</c> of a chain of iterators
    /// protects the caller's write. The analysis runs on a thread with a stack of its own, so the depth it reaches does not depend on
    /// the runner's threads. Without the fresh stack the case overflows the stack, which ends the test host.</summary>
    /// <param name="form">The form of the chain: <c>awaited</c>, <c>called</c> or <c>iterated</c>.</param>
    [Theory]
    [InlineData("awaited")]
    [InlineData("called")]
    [InlineData("iterated")]
    public void Lifting_through_a_chain_deeper_than_its_stack_completes(string form)
    {
        var source = form switch
        {
            "awaited" => Awaited(DEEP_LEVELS),
            "called" => Gated(Chain(DEEP_LEVELS, "private void E{0}() => E{1}();", "private void E{0}() => _gate.Wait();"),
                              "E0(); try { /*@run*/Value = 1; } finally { _gate.Release(); }").Source(),
            _ => Gated(Chain(DEEP_LEVELS, "private IEnumerable<int> I{0}() {{ foreach (var x in I{1}()) yield return x; }}", HELD_AT_YIELD),
                       "foreach (var x in I0()) /*@run*/Value = x;").Source()
        };

        var pairs = OnStack(SMALL_STACK, () => Pairs(Executed(source), workers: 1));

        if (form == "awaited")
            Assert.Contains("Chain.Fill(int[]) | SecondWorker.ExecuteAsync(CancellationToken)", Sides(pairs));
        else
            Assert.DoesNotContain(pairs, pair => pair.Resource.AccessPath.Contains("Value"));
    }

    /// <summary>The issue's chain collected on four workers, whose own threads have the default stack, gives the pairs it gives on one.
    /// A worker that goes on on a fresh stack waits for a thread of its own, never for one of the pool.</summary>
    [Fact]
    public void Lifting_on_workers_goes_on_on_fresh_stacks_with_the_same_pairs()
    {
        var execution = OnStack(SMALL_STACK, () => Executed(Awaited(DEEP_LEVELS)));

        var one = OnStack(SMALL_STACK, () => Pairs(execution, workers: 1));
        var four = Pairs(execution, workers: 4);

        Assert.Contains("Chain.Fill(int[]) | SecondWorker.ExecuteAsync(CancellationToken)", Sides(four));
        Assert.Equal(Sides(one), Sides(four));
    }

    /// <summary>A cancellation that a check on a fresh stack meets ends the collection as a cancellation: the exception the fresh
    /// thread throws is thrown again on the thread that waits for it.</summary>
    [Fact]
    public void A_cancellation_on_a_fresh_stack_cancels_the_collection()
    {
        var execution = OnStack(SMALL_STACK, () => Executed(Awaited(DEEP_LEVELS)));
        using var cancellation = new CancellationTokenSource();

        var error = Record.Exception(() => OnStack(SMALL_STACK, () =>
        {
            using var checks = WorkCancellation.Enter(cancellation.Token, () =>
            {
                if (Thread.CurrentThread.Name == "ch-fresh-stack")
                    cancellation.Cancel();
            });
            return Pairs(execution, workers: 1);
        }));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>How deep the <c>finally</c> blocks of <see cref="Solving_finally_blocks_nested_deeper_than_its_stack_completes"/> nest.</summary>
    private const int NESTED_FINALLY = 400;

    /// <summary>The stack of the thread <see cref="Solving_finally_blocks_nested_deeper_than_its_stack_completes"/> runs on, in bytes: the
    /// lock solver solves a <c>finally</c> inside the solve of the one around it, about two kilobytes a level, so it needs more than
    /// this at <see cref="NESTED_FINALLY"/> levels and goes on on a fresh stack, with no call in the body to start one.</summary>
    private const int FINALLY_STACK = 512 * 1024;

    /// <summary>A body whose <c>finally</c> blocks nest deeper than the lock solver fits in the thread's stack, each holding the next
    /// <c>try</c>, and whose innermost one writes <c>Value</c> under no lock, is analysed as far as its pairs: the write races the
    /// second hosted service's. The compiler and the lowering of so deep a nesting run on a large stack, the analysis after them on
    /// the small one. Without the fresh stack the case overflows the stack, which ends the test host.</summary>
    [Fact]
    public void Solving_finally_blocks_nested_deeper_than_its_stack_completes()
    {
        var nested = Enumerable.Range(0, NESTED_FINALLY).Aggregate("/*@run*/Value = 1;", (inner, _) => $"try {{ }} finally {{ {inner} }}");
        var reached = OnStack(16 * 1024 * 1024, () => Reach("using System.Collections.Generic;\n" + Gated(string.Empty, nested).Source()));

        var pairs = OnStack(FINALLY_STACK, () => Pairs(Execute(Solve(reached)), workers: 1));

        Assert.Contains("Gated.Run() | Gated.Touch()", Sides(pairs));
    }

    /// <summary>The pairs of an execution model, collected on as many workers as asked.</summary>
    /// <param name="execution">The execution model and its solved heap.</param>
    /// <param name="workers">How many workers walk the executions.</param>
    private static IReadOnlyList<AccessPair> Pairs(ExecutionRun execution, int workers)
    {
        var collection = InterproceduralAccesses.Collect(Input(execution) with { Workers = workers }, CancellationToken.None);
        return InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap).Pairs;
    }

    /// <summary>Each pair as the symbols of its two sides in ordinal order, the pairs in ordinal order.</summary>
    /// <param name="pairs">The pairs.</param>
    private static string[] Sides(IEnumerable<AccessPair> pairs) =>
        pairs.Select(pair => string.Join(" | ", new[] { pair.First.Symbol, pair.Second.Symbol }.Order(StringComparer.Ordinal)))
             .Order(StringComparer.Ordinal).ToArray();

    /// <summary>The execution model of a source file and the heap it was built on.</summary>
    /// <param name="source">The source file, without the fixture's usings.</param>
    private static ExecutionRun Executed(string source) => Execute(Solve(Reach("using System.Collections.Generic;\n" + source)));

    /// <summary>The issue's chain: <c>L0</c> to <c>Ln-1</c>, each awaiting the next with the array it got or with the field's own,
    /// the last <c>Fill</c>, which writes the first cell before its await; the first hosted service starts <c>L0</c> with the field's
    /// array and the second writes its first cell.</summary>
    /// <param name="levels">How many methods the chain has before <c>Fill</c>.</param>
    private static string Awaited(int levels) => $$"""
        public sealed class Chain
        {
            public bool Pick;
            public readonly int[] A = new int[1];
            {{Chain(levels + 1, "public async Task L{0}(int[] p) {{ if (Pick) await L{1}(p); else await L{1}(A); }}",
                    "public async Task L{0}(int[] p) {{ await Fill(p); }}")}}
            private static async Task Fill(int[] p) { p[0] = 1; await Task.Yield(); }
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Chain _chain;
            public FirstWorker(Chain chain) => _chain = chain;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { _ = _chain.L0(_chain.A); return Task.CompletedTask; }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Chain _chain;
            public SecondWorker(Chain chain) => _chain = chain;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { _chain.A[0] = 2; return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<Chain>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");

    /// <summary>Runs work on a thread with a stack of its own and gives back its result or rethrows what it threw.</summary>
    /// <param name="stack">The thread's stack, in bytes.</param>
    /// <param name="work">The work.</param>
    private static T OnStack<T>(int stack, Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        }, stack);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Two iterators an enumeration may run, each doing something only where the semaphore's count is three, which it never
    /// is: <c>First</c> gives the semaphore back and <c>Second</c> calls a chain of <paramref name="levels"/> methods whose last enters
    /// it, the first entry of it the analysis meets in that order. <c>Seq</c> hands out one of them by a field set at construction,
    /// and <c>Enumerate</c> enumerates it, the one enumeration <c>Run</c> calls twice.</summary>
    /// <param name="levels">How many methods the chain <c>Second</c> calls has.</param>
    private static string TwoIterators(int levels) =>
        "private readonly bool _pick = Environment.TickCount == 0;\n" +
        "private IEnumerable<int> Seq() => _pick ? First() : Second();\n" +
        "private void Enumerate() { foreach (var x in Seq()) { } }\n" +
        "private IEnumerable<int> First() { if (_gate.CurrentCount == 3) _gate.Release(); yield return 1; }\n" +
        "private IEnumerable<int> Second() { if (_gate.CurrentCount == 3) D0(); yield return 2; }\n" +
        Chain(levels, "private void D{0}() => D{1}();", "private void D{0}() => _gate.Wait();");

    /// <summary>A probe of a <c>Gated</c> service holding a semaphore of capacity one in <c>_gate</c>: the first hosted service runs
    /// <c>Run</c>, the second writes <c>Value</c> under the semaphore in <c>Touch</c>, at the site marked <c>touch</c>.</summary>
    /// <param name="members">The methods <c>Run</c> calls.</param>
    /// <param name="run">The body of <c>Run</c>.</param>
    private static HostedServiceProbe Gated(string members, string run) => new("Gated", $$"""
        public sealed class Gated
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            public int Value;
            {{members}}
            public void Run() { {{run}} }
            public void Touch() { _gate.Wait(); try { /*@touch*/Value = 2; } finally { _gate.Release(); } }
        }
        """, "_gated.Run();", "_gated.Touch();");

    /// <summary>A probe of a <c>Monitored</c> service whose writes of <c>Value</c> stand under the monitor <c>_monitor</c>, with a
    /// semaphore of capacity one in <c>_gate</c>: <c>Enter</c> enters the monitor and leaves it held, gives the semaphore back through
    /// <c>Free</c> where its count is zero, which it never is when <c>Enter</c> runs, and calls <c>E0</c>. The first hosted service runs
    /// <c>Run</c>, the second writes <c>Value</c> under the monitor in <c>Touch</c>, at the site marked <c>touch</c>.</summary>
    /// <param name="members">The methods <c>Enter</c> calls besides <c>Free</c>, <c>E0</c> first.</param>
    /// <param name="run">The body of <c>Run</c>.</param>
    private static HostedServiceProbe Monitored(string members, string run) => new("Monitored", $$"""
        public sealed class Monitored
        {
            private readonly object _monitor = new();
            private readonly SemaphoreSlim _gate = new(1, 1);
            public int Value;
            private void Free() => _gate.Release();
            private void Enter() { Monitor.Enter(_monitor); if (_gate.CurrentCount == 0) Free(); E0(); }
            {{members}}
            public void Run() { {{run}} }
            public void Touch() { lock (_monitor) { /*@touch*/Value = 2; } }
        }
        """, "_monitored.Run();", "_monitored.Touch();");

    /// <summary>A chain of methods numbered <c>0</c> to <c>levels - 1</c>, each written from a composite format whose <c>{0}</c> is its
    /// own number and <c>{1}</c> the next one's: every one but the last from <paramref name="link"/>, the last from
    /// <paramref name="last"/>.</summary>
    /// <param name="levels">How many methods the chain has.</param>
    /// <param name="link">The format of a method that calls the next one.</param>
    /// <param name="last">The format of the last method.</param>
    private static string Chain(int levels, string link, string last) =>
        string.Join("\n", Enumerable.Range(0, levels).Select(level =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, level == levels - 1 ? last : link, level, level + 1)));

    /// <summary>A cycle of <paramref name="levels"/> methods: <c>B</c> calls <c>C0</c>, each <c>C</c> the next, the last <c>A</c>, and
    /// <c>A</c> calls <c>B</c> again while its argument is above zero and releases the semaphore once it is zero, so <c>A</c> and
    /// <c>B</c> may each let it go; with two levels <c>B</c> calls <c>A</c> directly.</summary>
    /// <param name="levels">How many methods the cycle has, <c>A</c> and <c>B</c> among them.</param>
    private static string MayRelease(int levels) =>
        (levels == 2
            ? "private void B(int n) => A(n);"
            : "private void B(int n) => C0(n);\n" +
              Chain(levels - 2, "private void C{0}(int n) => C{1}(n);", "private void C{0}(int n) => A(n);")) +
        "\nprivate void A(int n) { if (n > 0) B(n - 1); else _gate.Release(); }";
}
