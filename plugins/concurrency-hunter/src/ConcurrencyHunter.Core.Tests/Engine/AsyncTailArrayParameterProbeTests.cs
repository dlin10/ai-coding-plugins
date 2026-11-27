using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the cells of an array an async body gets as a parameter, read or written after an await (issue 518), in
/// the forms that reach that place: a second await, <c>ConfigureAwait(false)</c>, an await in a loop, in a <c>try</c> or on one branch,
/// a <c>ValueTask</c>, an async callee between the caller and the body, before or after its own await, or a synchronous helper, a local copy of the parameter, a
/// constant or a bound index, an index a guard of the call keeps off the touched cell, a <c>params</c> array, an array a getter of the
/// caller returns, directly, through a getter of an async callee or recursively, an async lambda, an async local function, an async
/// iterator, two calls in one prefix, one instance handing the array on from two sites or from sixteen sites and a seventeenth with
/// another array, recursion that swaps the arrays, or moves the index or cuts the window further, in the tail or in a caller,
/// a caller that swaps two arrays as it recurses handing both to a tail whose recursive helper writes one of them on either branch,
/// sixteen and seventeen calls starting one tail, sixteen values of an unused
/// argument, a tail a constructor starts before it sets the field a guard reads, a <c>Task.Run</c> lambda that awaits the body, two
/// sites that hand the same or different arrays and indices, under one guard or different ones, and sixteen and seventeen cells one
/// tail writes, at and past the references a tail's frame keeps apart.
/// The continuation after an await runs the rest of the same method with the
/// same parameter values ("Asynchronous programming with async and await", C# specification §15.14 on async functions), so the array it
/// indexes is the one the caller handed over, the field's own, and a cell it writes or reads is the cell another thread writes through
/// the field at the same time. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>): the first hosted service
/// starts <c>RunAsync</c> and does not wait for it, the second writes the field's first cell in <c>Touch</c>, at the site marked
/// <c>touch</c>; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class AsyncTailArrayParameterProbeTests
{
    /// <summary>The finding of the callee's write against <c>Touch</c>.</summary>
    private const string CALLEE_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.FillAsync @callee | Write Sender.Touch @touch";

    /// <summary>Why a tail's access to an array handed on by a synchronous helper is lost.</summary>
    private const string SYNCHRONOUS_HELPER =
        "issue 546: a tail's start is bound by the call that started its body, but a caller that runs whole is bound by nothing in " +
        "the tail's execution, its own call path being the parent execution's, so the array it got as a parameter makes no access";

    /// <summary>Why an array a recursive getter hands back makes no access, before an await as after one.</summary>
    private const string RECURSIVE_GETTER =
        "issue 555: an array a recursive method hands back to its caller makes no access in the async method the caller passes it " +
        "to, before its first await as after it, where a getter that does not recurse is followed";

    /// <summary>Why a tail started by calls that disagree pairs where none of them would.</summary>
    private const string FRAME_AGREEMENT =
        "issue 559: a tail's frame is bound by all the calls that started it, naming the places any of them hands over but keeping a " +
        "value or a guard only where every call agrees, so one call's array meets another's index and a guard one call stands under " +
        "is lost";

    /// <summary>The callee that writes the first cell after its first await.</summary>
    private const string FILL = "private static async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["written-after-a-second-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { await Task.Yield(); await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["written-after-an-await-configured-not-to-capture-the-context"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { await Task.Delay(1).ConfigureAwait(false); /*@callee*/cells[0] = 1; }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["written-in-a-loop-after-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { for (var i = 0; i < 2; i++) { await Task.Yield(); /*@callee*/cells[0] = i; } }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["called-in-a-loop-of-the-caller"] = (Toucher(FILL, "for (var i = 0; i < 2; i++) await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["written-in-a-finally-after-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { try { await Task.Yield(); } finally { /*@callee*/cells[0] = 1; } }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["written-in-a-try-after-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { try { await Task.Yield(); /*@callee*/cells[0] = 1; } catch (InvalidOperationException) { } }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["written-after-an-await-on-one-branch"] = (Toucher(
            "private static async Task FillAsync(int[] cells, bool yield) { if (yield) await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "await FillAsync(_cells, true);"), [CALLEE_WRITE]),
        ["written-by-a-value-task-callee"] = (Toucher(
            "private static async ValueTask FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["handed-on-by-an-async-callee-before-its-own-await"] = (Toucher(
            FILL + " private static async Task PassAsync(int[] cells) { await FillAsync(cells); }", "await PassAsync(_cells);"),
            [CALLEE_WRITE]),
        ["handed-on-by-an-async-callee-after-its-own-await"] = (Toucher(
            FILL + " private static async Task PassAsync(int[] cells) { await Task.Yield(); await FillAsync(cells); }", "await PassAsync(_cells);"),
            [CALLEE_WRITE]),
        ["handed-on-by-a-synchronous-helper"] = (Toucher(
            FILL + " private static Task StartAsync(int[] cells) => FillAsync(cells);", "await StartAsync(_cells);"), [CALLEE_WRITE]),
        ["copied-to-a-local-before-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { var local = cells; await Task.Yield(); /*@callee*/local[0] = 1; }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["index-bound-to-the-written-cell"] = (Toucher(
            "private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; }",
            "await FillAsync(_cells, 0);"), [CALLEE_WRITE]),
        ["index-bound-to-another-cell"] = (Toucher(
            "private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; }",
            "await FillAsync(_cells, 1);"), []),
        ["params-array-handed-the-field-array"] = (Toucher(
            "private static async Task FillAsync(params int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["params-array-made-at-the-call"] = (Toucher(
            "private static async Task FillAsync(params int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "await FillAsync(1, 2);"), []),
        ["async-lambda-with-an-array-parameter"] = (Toucher(string.Empty,
            "Func<int[], Task> fill = async cells => { await Task.Yield(); /*@callee*/cells[0] = 1; }; await fill(_cells);"),
            ["DCA1001 unprotected _cells.[0]: Write Sender.RunAsync @callee | Write Sender.Touch @touch"]),
        ["async-local-function-with-an-array-parameter"] = (Toucher(string.Empty,
            "async Task Fill(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; } await Fill(_cells);"),
            ["DCA1001 unprotected _cells.[0]: Write Sender.RunAsync @callee | Write Sender.Touch @touch"]),
        ["async-iterator-reads-after-its-await"] = (Toucher(
            "public int Seen; private static async IAsyncEnumerable<int> CellsAsync(int[] cells) { await Task.Yield(); yield return /*@callee*/cells[0]; }",
            "await foreach (var seen in CellsAsync(_cells)) Seen = seen;"),
            ["DCA1001 unprotected _cells.[0]: Read Sender.CellsAsync @callee | Write Sender.Touch @touch"]),
        ["two-calls-in-one-prefix-hand-different-arrays"] = (Toucher(
            "public bool Other; private readonly int[] _other = new int[1]; " +
            "private async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }",
            "if (Other) await FillAsync(_other); else await FillAsync(_cells);"), [CALLEE_WRITE]),
        ["awaited-by-a-task-run-lambda"] = (Toucher(FILL, "await Task.Run(async () => await FillAsync(_cells));"), [CALLEE_WRITE]),
        ["awaited-by-a-task-run-lambda-nobody-waits-for"] = (Toucher(FILL, "_ = Task.Run(async () => await FillAsync(_cells)); await Task.Yield();"),
                                                             [CALLEE_WRITE]),
        ["handed-on-by-a-synchronous-helper-written-before-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { /*@callee*/cells[0] = 1; await Task.Yield(); } " +
            "private static Task StartAsync(int[] cells) => FillAsync(cells);", "await StartAsync(_cells);"), [CALLEE_WRITE]),
        ["sixteen-calls-start-one-tail-with-two-paths-below-it"] = (Toucher(
            "public int Pick; private readonly int[] _other = new int[1]; " +
            "private async Task FillAsync() { await Task.Yield(); Write(_other); Pass(_cells); } " +
            "private void Pass(int[] cells) => Write(cells); private void Write(int[] cells) => /*@callee*/cells[0] = 1;",
            Switch(16, _ => "await FillAsync();")),
            ["DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Write @callee"]),
        ["seventeen-calls-hand-a-tail-seventeen-cells"] = (Toucher(
            "public int Pick; private async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; }",
            Switch(17, arm => $"await FillAsync(_cells, {(arm + 1) % 17});"), cells: 17), [CALLEE_WRITE]),
        ["one-instance-hands-the-field-on-from-two-sites"] = (Toucher(
            FILL + " public bool Other; private async Task PassAsync(int[] cells) { await FillAsync(cells); }",
            "if (Other) await PassAsync(_cells); else await PassAsync(_cells);"), [CALLEE_WRITE]),
        ["recursion-swaps-the-written-array"] = (Toucher(
            "private readonly int[] _other = new int[1]; " +
            "private static async Task FillAsync(int[] a, int[] b, bool again) { await Task.Yield(); if (again) await FillAsync(b, a, false); /*@callee*/a[0] = 1; }",
            "await FillAsync(_other, _cells, true);"), [CALLEE_WRITE]),
        ["array-returned-by-a-getter-of-the-caller"] = (Toucher(FILL + " private int[] Cells() => _cells;", "await FillAsync(Cells());"),
                                                        [CALLEE_WRITE]),
        ["array-returned-by-a-getter-of-the-caller-written-before-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { /*@callee*/cells[0] = 1; await Task.Yield(); } private int[] Cells() => _cells;",
            "await FillAsync(Cells());"), [CALLEE_WRITE]),
        ["guarded-index-kept-off-the-touched-cell"] = (Toucher(
            "public int Index; private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; }",
            "var i = Index; if (i == 1) await FillAsync(_cells, i);", cells: 2), []),
        ["unused-argument-sixteen-values-and-two-paths-below"] = (Toucher(
            "public int Pick; private readonly int[] _other = new int[1]; " +
            "private async Task FillAsync(int unused) { await Task.Yield(); Write(_other); Pass(_cells); } " +
            "private void Pass(int[] cells) => Write(cells); private void Write(int[] cells) => /*@callee*/cells[0] = 1;",
            Switch(16, arm => $"await FillAsync({arm});")),
            ["DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Write @callee"]),
        ["one-instance-hands-two-arrays-through-a-getter"] = (Toucher(
            FILL + " public bool Other; private readonly int[] _other = new int[1]; private static int[] Same(int[] cells) => cells; " +
            "private async Task PassAsync(int[] cells) { await FillAsync(Same(cells)); }",
            "if (Other) await PassAsync(_other); else await PassAsync(_cells);"), [CALLEE_WRITE]),
        ["sixteen-sites-hand-one-array-and-a-seventeenth-another"] = (Toucher(
            FILL + " public int Pick; private readonly int[] _other = new int[1]; private async Task PassAsync(int[] cells) { await FillAsync(cells); }",
            Switch(17, arm => arm < 16 ? "await PassAsync(_other);" : "await PassAsync(_cells);")), [CALLEE_WRITE]),
        ["array-a-recursive-getter-hands-back"] = (Toucher(
            FILL + " private int Turns; private static int[] Pick(int[] cells, int n) => n <= 0 ? cells : Pick(cells, n - 1);",
            "await FillAsync(Pick(_cells, Turns));"), [CALLEE_WRITE]),
        ["array-a-recursive-getter-hands-back-written-before-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { /*@callee*/cells[0] = 1; await Task.Yield(); } " +
            "private int Turns; private static int[] Pick(int[] cells, int n) => n <= 0 ? cells : Pick(cells, n - 1);",
            "await FillAsync(Pick(_cells, Turns));"), [CALLEE_WRITE]),
        ["constructor-starts-the-tail-before-it-sets-the-guarded-field"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly int _index;
                public Sender() { Start(); _index = 1; }
                private void Start() { if (_index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
                public void Touch() { if (_index == 1) /*@touch*/_cells[0] = 2; }
            }
            """, "_ = _sender;", "_sender.Touch();"), [CALLEE_WRITE]),
        ["guarded-index-kept-off-the-touched-cell-written-before-its-await"] = (Toucher(
            "public int Index; private static async Task FillAsync(int[] cells, int index) { /*@callee*/cells[index] = 1; await Task.Yield(); }",
            "var i = Index; if (i == 1) await FillAsync(_cells, i);", cells: 2), []),
        ["window-starts-the-caller-computes-differ-by-branch"] = (Toucher(
            WINDOWS + " public bool Pick; private async Task Lead(int i) { if (Pick) await Pass(_cells.AsMemory(i + 1, 1)); else await Pass(_cells.AsMemory(i, 1)); }",
            "await Lead(0);", cells: 2), [WINDOW_WRITE]),
        ["window-starts-the-caller-computes-differ-by-branch-swapped"] = (Toucher(
            WINDOWS + " public bool Pick; private async Task Lead(int i) { if (Pick) await Pass(_cells.AsMemory(i, 1)); else await Pass(_cells.AsMemory(i + 1, 1)); }",
            "await Lead(0);", cells: 2), [WINDOW_WRITE]),
        ["sixteen-sites-hand-one-array-with-unused-values-and-a-seventeenth-another"] = (Toucher(
            FILL + " public int Pick; private readonly int[] _other = new int[1]; " +
            "private static async Task PassAsync(int[] cells, int unused) { await FillAsync(cells); }",
            Switch(17, arm => arm < 16 ? $"await PassAsync(_other, {arm});" : "await PassAsync(_cells, 16);")), [CALLEE_WRITE]),
        ["sixteen-windows-of-one-array-and-a-seventeenth-of-another"] = (Toucher(
            "public int Pick; private readonly int[] _other = new int[16]; " +
            "private async Task FillAsync(Memory<int> cells) { await Task.Yield(); /*@callee*/cells.Span[0] = 1; }",
            Switch(17, arm => arm < 16 ? $"await FillAsync(_other.AsMemory({arm}, 1));" : "await FillAsync(_cells.AsMemory(0, 1));")), [CALLEE_WRITE]),
        ["readonly-guard-keeps-two-writes-apart"] = (Picker(
            "private static async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }"), []),
        ["readonly-guard-keeps-two-writes-apart-written-before-its-await"] = (Picker(
            "private static async Task FillAsync(int[] cells) { /*@callee*/cells[0] = 1; await Task.Yield(); }"), []),
        ["one-instance-hands-two-indices-on-from-two-sites"] = (Toucher(
            "public bool Other; private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; } " +
            "private async Task PassAsync(int[] cells, int index) { await FillAsync(cells, index); }",
            "if (Other) await PassAsync(_cells, 1); else await PassAsync(_cells, 0);", cells: 2), [CALLEE_WRITE]),
        ["seventeenth-window-reaches-a-helper-below-the-tail"] = (Toucher(
            HELPER_WINDOWS, Switch(17, arm => arm < 16 ? $"await FillAsync(_other.AsMemory({arm}, 1));" : "await FillAsync(_cells.AsMemory(0, 1));")),
            [HELPER_WRITE]),
        ["first-window-reaches-a-helper-below-the-tail"] = (Toucher(
            HELPER_WINDOWS, Switch(17, arm => arm == 0 ? "await FillAsync(_cells.AsMemory(0, 1));" : $"await FillAsync(_other.AsMemory({arm - 1}, 1));")),
            [HELPER_WRITE]),
        ["six-levels-alternate-two-arrays"] = (Toucher(FILL + " public bool Other; private readonly int[] _other = new int[1]; " + Alternating(6),
                                                       "await L0(_other);"), [CALLEE_WRITE]),
        ["two-sites-hand-the-same-array-and-index"] = (Toucher(
            "public bool Other; private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; } " +
            "private async Task PassAsync(int[] cells, int index) { await FillAsync(cells, index); }",
            "if (Other) await PassAsync(_cells, 1); else await PassAsync(_cells, 1);", cells: 2), []),
        ["two-sites-under-one-readonly-guard"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private readonly bool _enabled = Environment.TickCount == 0;
                private readonly int[] _cells = new int[1];
                public bool Other;
                private static async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }
                private async Task PassAsync(int[] cells) { await FillAsync(cells); }
                public async Task RunAsync() { if (_enabled) { if (Other) await PassAsync(_cells); else await PassAsync(_cells); } }
                public void Touch() { if (!_enabled) /*@touch*/_cells[0] = 2; }
            }
            """, "_ = _sender.RunAsync();", "_sender.Touch();"), []),
        ["two-sites-hand-different-arrays-and-indices"] = (Toucher(
            "public bool Other; private readonly int[] _other = new int[2]; " +
            "private static async Task FillAsync(int[] cells, int index) { await Task.Yield(); /*@callee*/cells[index] = 1; } " +
            "private async Task PassAsync(int[] cells, int index) { await FillAsync(cells, index); }",
            "if (Other) await PassAsync(_cells, 1); else await PassAsync(_other, 0);", cells: 2), []),
        ["two-sites-under-different-readonly-guards"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private readonly int _mode = Environment.TickCount % 3;
                private readonly int[] _cells = new int[1];
                private static async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }
                private async Task PassAsync(int[] cells) { await FillAsync(cells); }
                public async Task RunAsync() { if (_mode == 1) await PassAsync(_cells); else if (_mode == 2) await PassAsync(_cells); }
                public void Touch() { if (_mode == 0) /*@touch*/_cells[0] = 2; }
            }
            """, "_ = _sender.RunAsync();", "_sender.Touch();"), []),
        ["one-method-reads-the-guard-in-construction-and-after"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly int _index;
                public Sender() { Start(); _index = 1; }
                public void Start() { if (_index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
                public void Touch() { if (_index == 1) /*@touch*/_cells[0] = 2; }
            }
            """, "_sender.Start();", "_sender.Touch();"), [CALLEE_WRITE]),
        ["four-levels-of-twenty-four-sites"] = (Toucher(FILL + " public int Pick; " + Levels(4, 24), "await L0(_cells);"), [CALLEE_WRITE]),
        ["five-levels-of-two-sites"] = (Toucher(FILL + " public int Pick; " + Levels(5, 2), "await L0(_cells);"), [CALLEE_WRITE]),
        ["recursion-slices-the-window-in-its-tail"] = (Toucher(
            "private static async Task Fill(Memory<int> p, int n) { await Task.Yield(); /*@callee*/p.Span[0] = 1; if (n > 0) await Fill(p.Slice(1), n - 1); }",
            "await Fill(_cells.AsMemory(), 2);", cells: 3), [WINDOW_WRITE]),
        ["recursion-moves-the-index-in-its-tail"] = (Toucher(
            "private static async Task FillAsync(int[] cells, int i) { await Task.Yield(); /*@callee*/cells[i] = 1; if (i < 2) await FillAsync(cells, i + 1); }",
            "await FillAsync(_cells, 0);", cells: 3), [CALLEE_WRITE]),
        ["recursive-caller-slices-the-window"] = (Toucher(
            "private static async Task Fill(Memory<int> p) { await Task.Yield(); /*@callee*/p.Span[0] = 1; } " +
            "private static async Task Lead(Memory<int> p, int n) { if (n > 0) await Lead(p.Slice(1), n - 1); else await Fill(p); }",
            "await Lead(_cells.AsMemory(), 0);", cells: 3), [WINDOW_WRITE]),
        ["recursive-caller-moves-the-index"] = (Toucher(
            "private static async Task FillAsync(int[] cells, int i) { await Task.Yield(); /*@callee*/cells[i] = 1; } " +
            "private static async Task Lead(int[] cells, int i, int n) { if (n > 0) await Lead(cells, i + 1, n - 1); else await FillAsync(cells, i); }",
            "await Lead(_cells, 0, 0);", cells: 3), [CALLEE_WRITE]),
        ["swapped-arrays-reach-a-recursive-helper"] = (Swapper("if (Pick) Loop(p, q, w, n - 1); else Loop(q, q, q, n - 1);"),
                                                                       [LOOP_WRITE]),
        ["swapped-arrays-reach-a-recursive-helper-reordered"] = (Swapper("if (!Pick) Loop(q, q, q, n - 1); else Loop(p, q, w, n - 1);"),
                                                                               [LOOP_WRITE]),
        ["windows-of-unknown-length-differ-by-start"] = (Recutter(string.Empty), [LOOP_WRITE]),
        ["windows-of-known-length-differ-by-start"] = (Recutter(", 1"), [LOOP_WRITE]),
        ["sixteen-cells-written-in-one-tail"] = (Toucher(Written(16), "await FillAsync(_cells);", cells: 18), []),
        ["seventeen-cells-written-in-one-tail"] = (Toucher(Written(17), "await FillAsync(_cells);", cells: 18), [])
    };

    /// <summary>The write of the first cell by the recursive helper <c>Loop</c> the tail calls, against <c>Touch</c>.</summary>
    private const string LOOP_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Loop @callee | Write Sender.Touch @touch";

    /// <summary>A probe whose <c>Lead</c> swaps its two arrays as it calls itself and then hands both to an async body, whose tail
    /// calls a recursive helper that writes its third array's first cell, reached with <c>_cells</c> only on one branch.</summary>
    /// <param name="branches">The helper's recursive call on its two branches.</param>
    private static HostedServiceProbe Swapper(string branches) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly int[] _cells = new int[1];
            private readonly int[] _other = new int[1];
            public bool Pick;
            public async Task RunAsync() { await Lead(_cells, null!, 1); }
            private async Task Lead(int[] p, int[] q, int n) { if (n > 0) await Lead(q, p, n - 1); else await Fill(p, q); }
            private async Task Fill(int[] p, int[] q) { await Task.Yield(); Loop(p, q, _other, 1); }
            private void Loop(int[] p, int[] q, int[] w, int n) { if (n == 0) { /*@callee*/w[0] = 1; return; } {{branches}} }
            public void Touch() { /*@touch*/_cells[0] = 2; }
        }
        """, "_ = _sender.RunAsync();", "_sender.Touch();");

    /// <summary>A probe whose tail calls a recursive helper <c>Loop</c> with a window of <c>_cells</c> from its second cell, which calls
    /// itself with the same window and then with one from the first cell, and writes that window's first cell, <c>_cells[0]</c>.</summary>
    /// <param name="length">What follows the start in each <c>AsMemory</c> call: nothing, or the window's length.</param>
    private static HostedServiceProbe Recutter(string length) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly int[] _cells = new int[2];
            public async Task RunAsync() { await Task.Yield(); Loop(_cells.AsMemory(1{{length}}), 0); }
            private void Loop(Memory<int> p, int n)
            {
                if (n == 0) Loop(_cells.AsMemory(1{{length}}), 1);
                else if (n == 1) Loop(_cells.AsMemory(0{{length}}), 2);
                else /*@callee*/p.Span[0] = 1;
            }
            public void Touch() => /*@touch*/_cells[0] = 2;
        }
        """, "_ = _sender.RunAsync();", "_sender.Touch();");

    /// <summary>The write of a window's first cell by the helper <c>Write</c> the tail calls, against <c>Touch</c>.</summary>
    private const string HELPER_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Write @callee";

    /// <summary>An async body that hands a window, after its first await, to a helper that writes the window's first cell; sixteen cells
    /// of another array to cut windows from.</summary>
    private const string HELPER_WINDOWS = "public int Pick; private readonly int[] _other = new int[16]; " +
                                          "private async Task FillAsync(Memory<int> cells) { await Task.Yield(); Write(cells); } " +
                                          "private static void Write(Memory<int> cells) => /*@callee*/cells.Span[0] = 1;";

    /// <summary>The write of a window's first cell through <c>Memory&lt;T&gt;.Span</c> in the tail of <c>Fill</c> against <c>Touch</c>.</summary>
    private const string WINDOW_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Fill @callee | Write Sender.Touch @touch";

    /// <summary>An async callee that hands a window on to one that writes its first cell after its first await.</summary>
    private const string WINDOWS = "private static async Task Pass(Memory<int> p) { await Fill(p); } " +
                                   "private static async Task Fill(Memory<int> p) { await Task.Yield(); /*@callee*/p.Span[0] = 1; }";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["handed-on-by-a-synchronous-helper"] = new([], SYNCHRONOUS_HELPER),
        ["array-a-recursive-getter-hands-back"] = new([], RECURSIVE_GETTER),
        ["array-a-recursive-getter-hands-back-written-before-its-await"] = new([], RECURSIVE_GETTER),
        ["one-method-reads-the-guard-in-construction-and-after"] = new(
            ["DCA1001 unprotected _cells.[0]: Write Sender.FillAsync @callee | Write Sender.FillAsync @callee", CALLEE_WRITE],
            "issue 557: the value the constructor stores in the readonly field after it calls the method is not known, so the tail the " +
            "method starts after construction under the field's default value, which never runs, pairs with the tail the constructor " +
            "started; the same pair stood before the tail's binding, which carried no guards"),
        ["two-sites-hand-different-arrays-and-indices"] = new([CALLEE_WRITE], FRAME_AGREEMENT),
        ["two-sites-under-different-readonly-guards"] = new([CALLEE_WRITE], FRAME_AGREEMENT),
        ["seventeen-cells-written-in-one-tail"] = new(
            [CALLEE_WRITE],
            "issue 558: a tail frame keeps sixteen references apart and makes a cell past them the unknown cell of the array its calls " +
            "hand over, so the write of a cell nobody else writes pairs with every cell of that array")
    };

    /// <summary>For each probe of a lost access: the accesses that were collected, as <see cref="HostedServiceProbe.DescribeAccess"/>
    /// writes them, and the resources at marked sites that have none, as <c>resource @marker</c>.</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["handed-on-by-a-synchronous-helper"] = (["Write _cells.[0] Sender.Touch @touch"], ["_cells.[0] @callee"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

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

    /// <summary>The probe collected the accesses its claim names and none on the resources it says were lost at their marked sites.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_the_accesses_its_claim_names(string name)
    {
        var (collected, lost) = AccessClaims[name];

        var answer = await Probes[name].Probe.AnalyzeAsync(new ProbeInputs());

        HostedServiceProbe.AssertAccessClaims(answer, collected, lost);
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A probe of a <c>Sender</c> holding an array in <c>_cells</c>, whose <c>RunAsync</c> hands it to an async body while the
    /// second hosted service writes its first cell in <c>Touch</c>, at the site marked <c>touch</c>.</summary>
    /// <param name="callee">The async body and what it needs.</param>
    /// <param name="call">The body of <c>RunAsync</c>.</param>
    /// <param name="cells">How many cells the array has.</param>
    private static HostedServiceProbe Toucher(string callee, string call, int cells = 1) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly int[] _cells = new int[{{cells}}];
            {{callee}}
            public async Task RunAsync() { {{call}} }
            public void Touch() => /*@touch*/_cells[0] = 2;
        }
        """, "_ = _sender.RunAsync();", "_sender.Touch();");

    /// <summary>A probe of a <c>Sender</c> whose <c>readonly</c> field <c>_pick</c> is set where the object is built and only read after:
    /// <c>RunAsync</c> hands the array to the async body when it is true, and <c>Touch</c> writes the first cell when it is false, so
    /// the two writes never both run.</summary>
    /// <param name="callee">The async body <c>FillAsync</c>.</param>
    private static HostedServiceProbe Picker(string callee) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly bool _pick = Environment.TickCount == 0;
            private readonly int[] _cells = new int[1];
            {{callee}}
            public async Task RunAsync() { if (_pick) await FillAsync(_cells); }
            public void Touch() { if (!_pick) /*@touch*/_cells[0] = 2; }
        }
        """, "_ = _sender.RunAsync();", "_sender.Touch();");

    /// <summary>A chain of async callers <c>L0</c> to <c>Ln-1</c>, each calling the next with its array from every arm of a
    /// <c>switch</c> on <c>Pick</c>, the last calling <c>FillAsync</c>.</summary>
    /// <param name="levels">How many callers.</param>
    /// <param name="sites">How many sites each calls the next from.</param>
    private static string Levels(int levels, int sites) =>
        string.Concat(Enumerable.Range(0, levels).Select(level =>
            $"private async Task L{level}(int[] p) {{ " +
            Switch(sites, _ => $"await {(level == levels - 1 ? "FillAsync" : $"L{level + 1}")}(p);") + " } "));

    /// <summary>A chain of async callers <c>L0</c> to <c>Ln-1</c>, each ignoring the array it gets and calling the next with
    /// <c>_other</c> or <c>_cells</c> on the two branches of <c>Other</c>, the last calling <c>FillAsync</c>.</summary>
    /// <param name="levels">How many callers.</param>
    internal static string Alternating(int levels) =>
        string.Concat(Enumerable.Range(0, levels).Select(level =>
        {
            var next = level == levels - 1 ? "FillAsync" : $"L{level + 1}";
            return $"private async Task L{level}(int[] unused) {{ if (Other) await {next}(_other); else await {next}(_cells); }} ";
        }));

    /// <summary>An async body <c>FillAsync</c> that writes the cells from the second on of the array it gets, after its first await,
    /// each at a site marked <c>callee</c>.</summary>
    /// <param name="cells">How many cells it writes.</param>
    private static string Written(int cells) =>
        "private static async Task FillAsync(int[] cells) { await Task.Yield(); " +
        string.Concat(Enumerable.Range(1, cells).Select(cell => $"/*@callee*/cells[{cell}] = 1; ")) + "}";

    /// <summary>A <c>switch</c> on <c>Pick</c> with one arm per case, each running its statement.</summary>
    /// <param name="arms">How many arms.</param>
    /// <param name="statement">The statement of each arm, by its number.</param>
    private static string Switch(int arms, Func<int, string> statement) =>
        $"switch (Pick) {{ {string.Concat(Enumerable.Range(0, arms).Select(arm => $"case {arm}: {statement(arm)} break; "))}}}";
}
