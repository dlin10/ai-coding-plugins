using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of a guard over a readonly field whose value a constructor stores (issue 557): the guard decides a write
/// only where the field's value at the read is the one value every construction of the object leaves in it. A readonly instance field
/// is assigned only in a constructor or a variable initializer of its own type (C# specification §15.5.3, "readonly (C# Reference)"),
/// a field initializer runs at the start of each constructor that does not chain to another of its type with <c>this(…)</c> (§15.11.3),
/// and until the store runs the field holds its default value (§9.3): a read during the construction, from a method the constructor
/// calls, a task or a timer it starts, sees zero. The value a construction leaves is the stored one only when the store runs on every
/// path that completes the constructor normally; a path around it — a condition, an early <c>return</c>, a <c>goto</c>, a loop that
/// runs no time, a <c>throw</c> a <c>catch</c> of the constructor swallows — leaves zero, and a store through a <c>ref</c> or
/// <c>out</c> argument may change it again. The service provider builds a singleton before it hands it to anyone (it resolves a
/// constructor's dependencies first, and uses the constructor with the most parameters it can resolve, "Dependency injection in .NET"),
/// and the hosted services run after the host has built them, so a read there is after the construction; a reader the construction
/// itself starts is not. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>): the first hosted service calls
/// <c>Run</c>, which writes the first cell of <c>_cells</c> where the guard holds, and the second writes it in <c>Touch</c>; a probe
/// the analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class ConstructedFieldProbeTests
{
    /// <summary>The finding of the guarded write in <c>Run</c> against <c>Touch</c>.</summary>
    private const string RUN_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch";

    /// <summary>The finding of two runs of the guarded write in <c>Run</c> against each other: a timer's callback may run on two
    /// thread-pool threads at once ("System.Threading.Timer", remarks) and requests are served concurrently.</summary>
    private const string RUN_WRITES_OVERLAP = "DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Run @run";

    /// <summary>The finding of the guarded write in <c>Opt.Run</c>, on an object the service builds with <c>new</c>, against <c>Touch</c>.</summary>
    private const string OPT_WRITE = "DCA1001 unprotected _cells.[0]: Write Opt.Run @run | Write Sender.Touch @touch";

    /// <summary>The finding of the write in <c>Use</c> against <c>Touch</c>.</summary>
    private const string USE_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Use @use";

    /// <summary>The finding of the write in the tail of <c>Opt.FillAsync</c> against <c>Touch</c>.</summary>
    private const string OPT_TAIL_WRITE = "DCA1001 unprotected _cells.[0]: Write Opt.FillAsync @callee | Write Sender.Touch @touch";

    /// <summary>The finding of a write by a task a constructor starts, against <c>Touch</c>.</summary>
    private const string WORK_WRITE = "DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Work @work";

    /// <summary>Why the value a constructor leaves in a readonly field is not taken, so a guard over it keeps a pair it would take away.</summary>
    private const string NOT_TAKEN =
        "issue 557: the value a constructor or an initializer leaves in a readonly field is taken only for an object a heap call of a " +
        "constructor builds, where the constructor's one store runs on every path that completes it, not for one the container builds, " +
        "a static field, a field an init accessor sets or a slice of a struct field, so a guard over it keeps the pair";

    /// <summary>Why the tail a method starts under a readonly field's default value pairs though it never runs.</summary>
    private const string CONSTRUCTOR_TAIL =
        "issue 557: a caller frame of an async tail reads a readonly field by its canonical identity only, never by the value its " +
        "constructor stores, so the tail the method starts after construction under the default value, which never runs, pairs with " +
        "the tail the constructor started";

    /// <summary>Why a value a constructor stores is taken where the field holds another.</summary>
    private const string VALUE_UNSOUND =
        "issue 571: the value a constructor stores in a readonly field is taken where the field may hold another: from a store a path " +
        "that completes the constructor goes around, for a read under the object's construction, past a later store through a ref and " +
        "from the constructor a this(...) chain reaches although the outer one stores again, so a guard or a cell over it loses a pair";

    /// <summary>Why a value a constructor leaves in a readonly field is not proven, so a guard over it keeps a pair it would take away.</summary>
    private const string NOT_PROVEN =
        "issue 566: the value a constructor or an initializer leaves in a readonly field is not taken in this form, or is bound to a value " +
        "of each execution instead of the object's one value, so a guard over it keeps a pair that cannot happen";

    /// <summary>Why a read in an execution a construction starts takes the field's value after the construction.</summary>
    private const string STARTED_IN_CONSTRUCTION =
        "issue 564: whether an object is under construction is asked of the reading execution's own walk, which an execution the " +
        "construction starts (a task, a timer, an async tail) does not see, so its read takes the value the constructor stores later, " +
        "or the field's settled identity, and a pair the default value makes is lost";

    /// <summary>Why a write to a shared array through a field of an object under construction makes no pair.</summary>
    private const string CONSTRUCTION_LOCAL_ARRAY =
        "issue 565: a write to a cell of an array an object under construction holds in its field is taken as construction-local, " +
        "though the array is shared with another object, so its race with a writer outside is missed";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        // The service provider builds the singleton in startup; the hosted services read after it.
        ["stored-in-the-constructor"] = (Guarded("public Sender() { _index = 1; }"), []),
        ["stored-zero-in-the-constructor"] = (Guarded("public Sender() { _index = 0; }"), [RUN_WRITE]),
        ["stored-by-a-field-initializer"] = (Guarded(string.Empty, " = 1"), []),
        ["stored-by-an-initializer-in-a-primary-constructor"] = (Plain(Service("Sender()", "private readonly int _index = 1;")), []),
        ["stored-under-a-condition"] = (Guarded("public Sender() { if (Environment.TickCount == 0) _index = 1; }"), [RUN_WRITE]),
        ["stored-after-an-early-return"] = (Guarded("public Sender() { if (Environment.TickCount == 0) return; _index = 1; }"), [RUN_WRITE]),
        ["stored-past-a-goto"] = (Guarded("public Sender() { if (Environment.TickCount == 0) goto done; _index = 1; done: ; }"), [RUN_WRITE]),
        ["stored-in-a-try-a-catch-swallows"] = (Guarded(
            "public Sender() { try { Init(); _index = 1; } catch (InvalidOperationException) { } } " + THROWER), [RUN_WRITE]),
        ["stored-after-a-throw-caught-in-the-constructor"] = (Guarded(
            "public Sender() { try { if (Environment.TickCount == 0) throw new InvalidOperationException(); _index = 1; } " +
            "catch (InvalidOperationException) { } }"), [RUN_WRITE]),
        ["stored-after-a-throw-that-leaves-the-constructor"] = (Guarded(
            "public Sender() { if (Environment.TickCount == 0) throw new InvalidOperationException(); _index = 1; }"), []),
        ["stored-after-a-loop-it-breaks-out-of"] = (Guarded(
            "public Sender() { for (var i = 0; i < Environment.TickCount; i++) { if (i == 3) break; } _index = 1; }"), []),
        ["stored-in-a-loop-body"] = (Guarded("public Sender() { for (var i = 0; i < Environment.TickCount; i++) _index = 1; }"), [RUN_WRITE]),
        ["stored-in-a-finally"] = (Guarded("public Sender() { try { Init(); } finally { _index = 1; } } " + THROWER), []),
        ["stored-in-the-try-of-a-finally"] = (Guarded("public Sender() { try { _index = 1; } finally { Init(); } } " + THROWER), []),
        ["stored-then-reset-through-a-ref-on-one-path"] = (Guarded(
            "public Sender() { _index = 1; if (Environment.TickCount == 0) Reset(ref _index); } " +
            "private static void Reset(ref int index) => index = 0;"), [RUN_WRITE]),
        ["stored-then-reset-through-an-out-on-one-path"] = (Guarded(
            "public Sender() { _index = 1; if (Environment.TickCount == 0) Reset(out _index); } " +
            "private static void Reset(out int index) => index = 0;"), [RUN_WRITE]),
        ["stored-through-a-ref-on-every-path"] = (Guarded(
            "public Sender() { Set(ref _index); } private static void Set(ref int index) => index = 1;"), []),
        ["stored-from-a-call-that-may-return-zero"] = (Guarded(
            "public Sender() { _index = Pick(); } private static int Pick() => Environment.TickCount == 0 ? 0 : 1;"), [RUN_WRITE]),
        ["stored-from-a-call-that-returns-one"] = (Guarded("public Sender() { _index = One(); } private static int One() => 1;"), []),
        ["stored-a-value-not-known"] = (Guarded(string.Empty, " = Environment.TickCount % 2"), [RUN_WRITE]),
        ["stored-by-the-constructor-this-chains-to"] = (Guarded(
            "public Sender() : this(1) { } private Sender(int index) { _index = index; }"), []),
        ["chained-and-overwritten-by-the-outer"] = (Guarded(
            "public Sender() : this(0) { _index = 1; } private Sender(int index) { _index = index; }"), []),
        ["chained-and-overwritten-with-zero"] = (Guarded(
            "public Sender() : this(1) { _index = 0; } private Sender(int index) { _index = index; }"), [RUN_WRITE]),
        ["stored-by-the-base-constructor"] = (Plain(
            "public class Base { protected readonly int _index; protected Base(int index) { _index = index; } }\n" +
            Service("Sender : Base", "public Sender() : base(1) { }")), []),
        ["two-constructors-the-provider-uses-stores-one"] = (Guarded(
            "public Sender() { _index = 0; } public Sender(Box box) { _index = 1; }", registrations: "services.AddSingleton<Box>();"), []),
        ["two-constructors-the-provider-uses-stores-none"] = (Guarded(
            "public Sender() { _index = 1; } public Sender(Box box) { }", registrations: "services.AddSingleton<Box>();"), [RUN_WRITE]),
        ["static-field-initializer"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private static readonly int s_index = 1;
                private readonly int[] _cells = new int[1];
                public void Run() { if (s_index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["static-constructor"] = (new HostedServiceProbe("Sender", """
            public sealed class Sender
            {
                private static readonly int s_index;
                static Sender() { s_index = 1; }
                private readonly int[] _cells = new int[1];
                public void Run() { if (s_index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["readonly-struct-in-a-readonly-field"] = (new HostedServiceProbe("Sender", """
            public readonly struct Pair { public readonly int Index; public Pair(int index) { Index = index; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Pair _pair = new(1);
                public void Run() { var copy = _pair; if (copy.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["init-accessor-a-factory-sets"] = (new HostedServiceProbe("Sender", """
            public sealed class Options { public int Index { get; init; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Options _options;
                public Sender(Options options) { _options = options; }
                public void Run() { if (_options.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();", "services.AddSingleton(_ => new Options { Index = 1 });"), []),
        ["record-with-a-factory-sets"] = (new HostedServiceProbe("Sender", """
            public sealed record Options(int Index);
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Options _options;
                public Sender(Options options) { _options = options; }
                public void Run() { if (_options.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();", "services.AddSingleton(_ => new Options(0) with { Index = 1 });"), []),
        ["built-by-a-factory"] = (new HostedServiceProbe("Holder",
            "public sealed class Holder(Sender sender) { public void Run() => sender.Run(); public void Touch() => sender.Touch(); }\n" +
            Service("Sender", Stores("1")), "_holder.Run();", "_holder.Touch();", "services.AddSingleton(_ => new Sender());"), []),

        // Who reads: a reader that starts after startup, or one a construction starts.
        ["read-in-start-async"] = (Reader(STARTER, "1"), []),
        ["read-in-start-async-of-a-stored-zero"] = (Reader(STARTER, "0"), [RUN_WRITE]),
        ["read-by-a-timer-another-singleton-starts"] = (Reader(TICKER, "1"), []),
        ["read-by-a-timer-of-a-stored-zero"] = (Reader(TICKER, "0"), [RUN_WRITE, RUN_WRITES_OVERLAP]),
        ["read-by-a-task-another-singleton-starts"] = (Reader(RUNNER, "1"), []),
        ["read-by-a-task-of-a-stored-zero"] = (Reader(RUNNER, "0"), [RUN_WRITE]),
        ["read-by-a-callback-registered-in-startup"] = (Reader(CALLBACK, "1"), []),
        ["read-by-a-callback-of-a-stored-zero"] = (Reader(CALLBACK, "0"), [RUN_WRITE]),
        ["read-in-a-request"] = (Reader(CONTROLLER, "1"), []),
        ["read-in-a-request-of-a-stored-zero"] = (Reader(CONTROLLER, "0"), [RUN_WRITE, RUN_WRITES_OVERLAP]),
        ["read-by-a-task-before-the-store"] = (Worked("Task.Run(Work); _index = 1;", null), [WORK_WRITE]),
        ["read-by-a-task-after-the-store"] = (Worked("_index = 1; Task.Run(Work);", null), []),
        ["read-by-a-task-before-a-store-not-known"] = (Worked("Task.Run(Work); _index = Environment.TickCount == 0 ? 2 : 1;", " == 1"), [WORK_WRITE]),

        // An object the service builds with new.
        ["new-object-stored-in-its-constructor"] = (Built("Index = 1;"), []),
        ["new-object-stored-under-a-condition"] = (Built("if (Environment.TickCount == 0) Index = 1;"), [OPT_WRITE]),
        ["new-object-stored-after-an-early-return"] = (Built("if (Environment.TickCount == 0) return; Index = 1;"), [OPT_WRITE]),
        ["new-object-stored-in-a-try-a-catch-swallows"] = (Built("try { Init(); Index = 1; } catch (InvalidOperationException) { }"), [OPT_WRITE]),
        ["new-object-stored-past-a-goto"] = (Built("if (Environment.TickCount == 0) goto done; Index = 1; done: ;"), [OPT_WRITE]),
        ["new-object-stored-in-a-loop-body"] = (Built("for (var i = 0; i < Environment.TickCount; i++) Index = 1;"), [OPT_WRITE]),
        ["new-object-stored-after-a-caught-throw"] = (Built(
            "try { if (Environment.TickCount == 0) throw new InvalidOperationException(); Index = 1; } catch (InvalidOperationException) { }"),
            [OPT_WRITE]),
        ["new-object-stored-in-a-try-whose-catch-loops"] = (Built(
            "try { Init(); Index = 1; } catch (InvalidOperationException) { while (Environment.TickCount == 123) { } }"), [OPT_WRITE]),
        ["new-object-stored-before-a-call-a-catch-swallows"] = (Built("try { Index = 1; Init(); } catch (InvalidOperationException) { }"), []),
        ["new-object-stored-in-a-finally"] = (Built("try { Init(); } finally { Index = 1; }"), []),
        ["new-object-stored-in-a-try-a-finally-closes"] = (Built(
            "try { if (Environment.TickCount == 0) Init(); Index = 1; } finally { GC.KeepAlive(this); }"), []),
        ["new-object-stored-before-a-finally-that-loops"] = (Built(
            "try { Index = 1; } finally { while (Environment.TickCount == 123) { } }"), []),
        ["new-object-stored-after-a-throw-leaving-it"] = (Built(
            "if (Environment.TickCount == 0) throw new InvalidOperationException(); Index = 1;"), []),
        ["new-object-stored-after-a-loop-it-breaks"] = (Built(
            "for (var i = 0; i < Environment.TickCount; i++) { if (i == 3) break; } Index = 1;"), []),
        ["new-object-reset-through-a-ref-on-one-path"] = (Built(
            "Index = 1; if (Environment.TickCount == 0) Reset(ref Index); } private static void Reset(ref int index) { index = 0;"), [OPT_WRITE]),
        ["new-object-read-by-a-task-before-the-store"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Task.Run(Work); Index = 1; }
                private void Work() { if (Index == 0) /*@work*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_cells); }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_ = _sender;", "_sender.Touch();"), ["DCA1001 unprotected _cells.[0]: Write Opt.Work @work | Write Sender.Touch @touch"]),
        ["new-object-read-in-its-own-constructor"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly Sender _sender;
                public Opt(Sender sender) { _sender = sender; Check(); Index = 1; }
                private void Check() { if (Index == 0) _sender.Bump(); }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                public void Run() { _ = new Opt(this); }
                public void Bump() => /*@bump*/_cells[0] = 1;
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), ["DCA1001 unprotected _cells.[0]: Write Sender.Bump @bump | Write Sender.Touch @touch"]),
        ["new-object-writes-a-shared-array-it-holds"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Fill(); }
                private void Fill() => /*@fill*/_cells[0] = 1;
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                public void Run() { _ = new Opt(_cells); }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), ["DCA1001 unprotected _cells.[0]: Write Opt.Fill @fill | Write Sender.Touch @touch"]),
        ["new-object-starts-a-tail-before-the-store"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Start(); Index = 1; }
                public void Start() { if (Index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_cells); }
                public void Run() => _opt.Start();
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), ["DCA1001 unprotected _cells.[0]: Write Opt.FillAsync @callee | Write Sender.Touch @touch"]),
        ["new-object-built-in-startup-and-in-a-worker"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Task.Run(Work); Index = 1; }
                private void Work() { if (Index == 0) /*@work*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = Make(_cells); }
                private static Opt Make(int[] cells) => new(cells);
                public void Run() { _ = Make(_cells); }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"),
            ["DCA1001 unprotected _cells.[0]: Write Opt.Work @work | Write Sender.Touch @touch",
             "DCA1001 unprotected _cells.[0]: Write Opt.Work @work | Write Opt.Work @work"]),
        ["new-object-joined-in-startup-and-in-a-worker"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Task.Run(Work).Wait(); Index = 1; }
                private void Work() { if (Index == 0) /*@work*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = Make(_cells); }
                private static Opt Make(int[] cells) => new(cells);
                public void Run() { _ = Make(_cells); }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), ["DCA1001 unprotected _cells.[0]: Write Opt.Work @work | Write Sender.Touch @touch"]),
        ["stored-from-a-field-the-constructor-sets-later"] = (Guarded(
            "private readonly int _next; public Sender() { _index = _next; _next = 1; }"), [RUN_WRITE]),
        ["stored-in-a-try-whose-catch-loops"] = (Guarded(
            "public Sender() { try { Init(); _index = 1; } catch (InvalidOperationException) { while (Environment.TickCount == 123) { } } } " +
            THROWER), [RUN_WRITE]),
        ["new-object-task-starts-a-tail-before-the-store"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Task.Run(Work); Index = 1; }
                private void Work() { if (Index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                public void Run() { _ = new Opt(_cells); }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), [OPT_TAIL_WRITE]),
        ["escaped-object-whose-constructor-threw"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(Sender owner, int[] cells)
                {
                    _cells = cells;
                    owner.Saved = this;
                    if (Environment.TickCount == 0) throw new InvalidOperationException();
                    Index = 1;
                }
                public void Start() { if (Index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                public Opt? Saved;
                public Sender() { try { _ = new Opt(this, _cells); } catch (InvalidOperationException) { } }
                public void Run() => Saved?.Start();
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), [OPT_TAIL_WRITE]),
        ["init-accessor-resets-the-field-through-a-ref"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; Index = 1; }
                public int Setting { init { Store(ref Index, value); } }
                private static void Store(ref int target, int value) => target = value;
                public void Start() { if (Index == 0) _ = FillAsync(); }
                private async Task FillAsync() { await Task.Yield(); /*@callee*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_cells) { Setting = 0 }; }
                public void Run() => _opt.Start();
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), [OPT_TAIL_WRITE]),
        ["unsigned-sum-crosses-the-sign-bit"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly ulong Index; public Opt(ulong n) { Index = n + 1UL; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt = new(9223372036854775807UL);
                public void Run() { if (_opt.Index > 0UL) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), [RUN_WRITE]),
        ["new-object-read-in-the-tail-of-its-builder"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt() { Index = 1; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                public async Task RunAsync() { var opt = new Opt(); await Task.Yield(); if (opt.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_ = _sender.RunAsync();", "_sender.Touch();"), []),
        ["handed-to-a-constructor-before-its-store"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt(int index) { Index = index; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly int _index;
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_index); _index = 1; }
                public void Run() { if (_opt.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), [RUN_WRITE]),
        ["guarded-branch-beside-a-catch-that-loops"] = (Guarded(
            "public Sender() { _index = 1; } " +
            "public void Use() { try { if (_index == 0) Log(); Init(); } " +
            "catch (InvalidOperationException) { while (Environment.TickCount == 123) { } /*@use*/_cells[0] = 1; } } " +
            "private static void Log() { } " + THROWER) with { First = "_sender.Use();" }, [USE_WRITE]),
        ["two-objects-write-their-cells-through-a-helper"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt(int i) { if (Environment.TickCount != 0) Index = i; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[4];
                private readonly Opt _a = new(1);
                private readonly Opt _b = new(2);
                public void Run() { Write(_a.Index); Write(_b.Index); }
                private void Write(int i) => /*@write*/_cells[i] = 1;
            }
            """, "_sender.Run();", "_sender.Run();"),
            ["DCA1001 unprotected _cells.[0]: Write Sender.Write @write | Write Sender.Write @write",
             "DCA1001 unprotected _cells.[1]: Write Sender.Write @write | Write Sender.Write @write",
             "DCA1001 unprotected _cells.[2]: Write Sender.Write @write | Write Sender.Write @write"]),
        ["inner-finally-stores-under-an-outer-catch"] = (Built(
            "try { try { Init(); } finally { Index = 1; } } catch (InvalidOperationException) { }"), []),
        ["optional-store-against-a-guard-on-two"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { _cells = cells; if (Environment.TickCount == 0) Index = 1; }
                public void Run() { if (Index == 2) /*@run*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_cells); }
                public void Run() => _opt.Run();
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["store-after-a-field-copy-in-a-try"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                private readonly int[] _cells;
                public Opt(int[] cells) { try { _cells = cells; Index = 1; } catch (InvalidOperationException) { _cells = cells; } }
                public void Run() { if (Index == 0) /*@run*/_cells[0] = 1; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt;
                public Sender() { _opt = new Opt(_cells); }
                public void Run() => _opt.Run();
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["parameter-value-under-two-exclusive-guards"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt(int i) { if (Environment.TickCount != 5) Index = i; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt = new(Environment.TickCount);
                public void Run() { if (_opt.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() { if (_opt.Index == 1) /*@touch*/_cells[0] = 2; }
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["new-object-chained-and-overwritten-by-the-outer"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt
            {
                public readonly int Index;
                public Opt() : this(0) { Index = 1; }
                private Opt(int index) { Index = index; }
            }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _opt = new();
                public void Run() { if (_opt.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() => /*@touch*/_cells[0] = 2;
            }
            """, "_sender.Run();", "_sender.Touch();"), []),
        ["scoped-object-read-by-a-task-before-the-store"] = (new HostedServiceProbe("Holder", """
            public sealed class Holder { }
            public sealed class Scoped
            {
                private static readonly int[] s_cells = new int[1];
                private readonly int _index;
                public Scoped() { Task.Run(Work); _index = 1; }
                private void Work() { if (_index == 0) /*@work*/s_cells[0] = 1; }
            }
            [ApiController, Route("run")]
            public sealed class RunController(Scoped scoped) : ControllerBase { [HttpGet] public void Get() => GC.KeepAlive(scoped); }
            """, "_ = _holder;", "_ = _holder;", "services.AddScoped<Scoped>();"),
            ["DCA1001 unprotected s_cells.[0]: Write Scoped.Work @work | Write Scoped.Work @work"]),
        ["new-objects-of-a-loop-store-different-values"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt(int index) { Index = index; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt[] _opts = new Opt[2];
                public Sender() { for (var i = 0; i < 2; i++) _opts[i] = new Opt(i); }
                public void Run() { if (_opts[0].Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() { if (_opts[1].Index == 1) /*@touch*/_cells[0] = 2; }
            }
            """, "_sender.Run();", "_sender.Touch();"), [RUN_WRITE]),
        ["new-objects-of-one-site-store-different-values"] = (new HostedServiceProbe("Sender", """
            public sealed class Opt { public readonly int Index; public Opt(int index) { Index = index; } }
            public sealed class Sender
            {
                private readonly int[] _cells = new int[1];
                private readonly Opt _a = Make(0);
                private readonly Opt _b = Make(1);
                private static Opt Make(int index) => new(index);
                public void Run() { if (_a.Index == 0) /*@run*/_cells[0] = 1; }
                public void Touch() { if (_b.Index == 1) /*@touch*/_cells[0] = 2; }
            }
            """, "_sender.Run();", "_sender.Touch();"), [RUN_WRITE])
    };

    /// <summary>A helper that throws on one path.</summary>
    private const string THROWER = "private static void Init() { if (Environment.TickCount == 0) throw new InvalidOperationException(); }";

    /// <summary>A hosted service whose <c>StartAsync</c> calls <c>Run</c>.</summary>
    private const string STARTER = """
        public sealed class Starter(Sender sender) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) { sender.Run(); return Task.CompletedTask; }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        |services.AddHostedService<Starter>();
        """;

    /// <summary>A singleton whose constructor starts a timer that calls <c>Run</c>, held by a hosted service.</summary>
    private const string TICKER = """
        public sealed class Ticker { private readonly Timer _timer; public Ticker(Sender sender) { _timer = new Timer(_ => sender.Run(), null, 0, 10); } }
        public sealed class TickerHost(Ticker ticker) : BackgroundService { protected override Task ExecuteAsync(CancellationToken stoppingToken) { GC.KeepAlive(ticker); return Task.CompletedTask; } }
        |services.AddSingleton<Ticker>(); services.AddHostedService<TickerHost>();
        """;

    /// <summary>A singleton whose constructor starts a task that calls <c>Run</c>, held by a hosted service.</summary>
    private const string RUNNER = """
        public sealed class Runner { public Runner(Sender sender) { Task.Run(sender.Run); } }
        public sealed class RunnerHost(Runner runner) : BackgroundService { protected override Task ExecuteAsync(CancellationToken stoppingToken) { GC.KeepAlive(runner); return Task.CompletedTask; } }
        |services.AddSingleton<Runner>(); services.AddHostedService<RunnerHost>();
        """;

    /// <summary>A singleton whose constructor registers a callback that calls <c>Run</c> when the application has started.</summary>
    private const string CALLBACK = """
        public sealed class Callback { public Callback(Sender sender, IHostApplicationLifetime lifetime) { lifetime.ApplicationStarted.Register(sender.Run); } }
        public sealed class CallbackHost(Callback callback) : BackgroundService { protected override Task ExecuteAsync(CancellationToken stoppingToken) { GC.KeepAlive(callback); return Task.CompletedTask; } }
        |services.AddSingleton<Callback>(); services.AddHostedService<CallbackHost>();
        """;

    /// <summary>A controller whose action calls <c>Run</c>.</summary>
    private const string CONTROLLER = """
        [ApiController, Route("run")]
        public sealed class RunController(Sender sender) : ControllerBase { [HttpGet] public void Get() => sender.Run(); }
        |
        """;

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

    /// <summary>The write in a <c>catch</c> whose entry a loop goes back to carries the guard of a branch of its <c>try</c> that does not
    /// decide it (issue 572): the dominators the guards of TD-090 stand on take a block for an entry only where nothing
    /// precedes it, so the handler's entry stays dominated by every block. The fact pins that wrong condition as it stands today; the pair
    /// stays only because the field's value is not known, and the fix of the dominators turns this fact around.</summary>
    [Fact]
    public void A_write_in_a_catch_whose_entry_loops_carries_a_branch_of_the_try()
    {
        var run = EngineFixture.Analyze(Probes["guarded-branch-beside-a-catch-that-loops"].Probe.Source());

        var write = Assert.Single(run.Collection.Accesses, access => access.Symbol.Contains("Use", StringComparison.Ordinal) &&
                                                                     access.Resource.AccessPath.Contains("_cells") &&
                                                                     access.Operation == AccessOperation.Write);
        Assert.Contains(write.Conditions, condition => condition.Text.EndsWith("_index", StringComparison.Ordinal) &&
                                                       condition is { Relation: PathRelation.Equal, Value: "0" });
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["chained-and-overwritten-by-the-outer"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["chained-and-overwritten-with-zero"] = new([], VALUE_UNSOUND),
        ["init-accessor-a-factory-sets"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["new-object-built-in-startup-and-in-a-worker"] = new([], STARTED_IN_CONSTRUCTION),
        ["new-object-chained-and-overwritten-by-the-outer"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["new-object-joined-in-startup-and-in-a-worker"] = new([], STARTED_IN_CONSTRUCTION),
        ["new-object-read-by-a-task-before-the-store"] = new([], STARTED_IN_CONSTRUCTION),
        ["new-object-read-in-its-own-constructor"] = new([], VALUE_UNSOUND),
        ["new-object-reset-through-a-ref-on-one-path"] = new([], VALUE_UNSOUND),
        ["new-object-starts-a-tail-before-the-store"] = new(["DCA1001 unprotected _cells.[0]: Write Opt.FillAsync @callee | Write Opt.FillAsync @callee", "DCA1001 unprotected _cells.[0]: Write Opt.FillAsync @callee | Write Sender.Touch @touch"], CONSTRUCTOR_TAIL),
        ["new-object-stored-after-a-caught-throw"] = new([], VALUE_UNSOUND),
        ["new-object-stored-after-an-early-return"] = new([], VALUE_UNSOUND),
        ["new-object-stored-in-a-loop-body"] = new([], VALUE_UNSOUND),
        ["new-object-stored-in-a-try-a-catch-swallows"] = new([], VALUE_UNSOUND),
        ["new-object-stored-in-a-try-whose-catch-loops"] = new([], VALUE_UNSOUND),
        ["new-object-stored-past-a-goto"] = new([], VALUE_UNSOUND),
        ["new-object-stored-under-a-condition"] = new([], VALUE_UNSOUND),
        ["new-object-writes-a-shared-array-it-holds"] = new([], CONSTRUCTION_LOCAL_ARRAY),
        ["parameter-value-under-two-exclusive-guards"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["read-by-a-callback-registered-in-startup"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["read-by-a-task-after-the-store"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Touch @touch | Write Sender.Work @work"], NOT_PROVEN),
        ["read-by-a-task-another-singleton-starts"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["read-by-a-task-before-a-store-not-known"] = new([], STARTED_IN_CONSTRUCTION),
        ["read-by-a-timer-another-singleton-starts"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Run @run", "DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["read-in-a-request"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Run @run", "DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["read-in-start-async"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["readonly-struct-in-a-readonly-field"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["record-with-a-factory-sets"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["static-constructor"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["static-field-initializer"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["stored-after-a-loop-it-breaks-out-of"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-after-a-throw-that-leaves-the-constructor"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-by-a-field-initializer"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-by-an-initializer-in-a-primary-constructor"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-from-a-call-that-returns-one"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["stored-in-a-finally"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["stored-in-the-constructor"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-in-the-try-of-a-finally"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_TAKEN),
        ["stored-through-a-ref-on-every-path"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["two-constructors-the-provider-uses-stores-one"] = new(["DCA1001 unprotected _cells.[0]: Write Sender.Run @run | Write Sender.Touch @touch"], NOT_PROVEN),
        ["two-objects-write-their-cells-through-a-helper"] = new(["DCA1001 unprotected _cells.[1]: Write Sender.Write @write | Write Sender.Write @write", "DCA1001 unprotected _cells.[2]: Write Sender.Write @write | Write Sender.Write @write"], VALUE_UNSOUND)
    };

    /// <summary>For each probe of a missed pair: the accesses of the pair, which are collected, so the pair is lost after them (by a guard
    /// a value decides, or by a cell the value names), as <see cref="HostedServiceProbe.DescribeAccess"/> writes them; and the resources at
    /// marked sites that have no access, as <c>resource @marker</c>.</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["chained-and-overwritten-with-zero"] = (["Write _cells.[0] Sender.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-built-in-startup-and-in-a-worker"] = (["Write _cells.[0] Opt.Work @work", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-joined-in-startup-and-in-a-worker"] = (["Write _cells.[0] Opt.Work @work", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-read-by-a-task-before-the-store"] = (["Write _cells.[0] Opt.Work @work", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-read-in-its-own-constructor"] = (["Write _cells.[0] Sender.Bump @bump", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-reset-through-a-ref-on-one-path"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-after-a-caught-throw"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-after-an-early-return"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-in-a-loop-body"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-in-a-try-a-catch-swallows"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-in-a-try-whose-catch-loops"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-past-a-goto"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-stored-under-a-condition"] = (["Write _cells.[0] Opt.Run @run", "Write _cells.[0] Sender.Touch @touch"], []),
        ["new-object-writes-a-shared-array-it-holds"] = (["Write _cells.[0] Sender.Touch @touch"], ["_cells.[0] @fill"]),
        ["read-by-a-task-before-a-store-not-known"] = (["Write _cells.[0] Sender.Touch @touch", "Write _cells.[0] Sender.Work @work"], []),
        ["two-objects-write-their-cells-through-a-helper"] = (["Write _cells.[1] Sender.Write @write", "Write _cells.[2] Sender.Write @write"], ["_cells.[0] @write"])
    };

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

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

    /// <summary>A <c>Sender</c> whose readonly <c>_index</c> guards the write in <c>Run</c>, with the constructor given.</summary>
    /// <param name="constructor">The constructors and helpers of the class.</param>
    /// <param name="initializer">What follows the field's name in its declaration: an initializer, or nothing.</param>
    /// <param name="registrations">Registrations added before the service's own.</param>
    private static HostedServiceProbe Guarded(string constructor, string initializer = "", string registrations = "") =>
        new("Sender", Service("Sender", $"private readonly int _index{initializer}; {constructor}"), "_sender.Run();", "_sender.Touch();",
            registrations);

    /// <summary>A probe of the types given whose first hosted service calls <c>Run</c> and whose second calls <c>Touch</c>.</summary>
    /// <param name="types">The types of the case, <c>Sender</c> among them.</param>
    private static HostedServiceProbe Plain(string types) => new("Sender", types, "_sender.Run();", "_sender.Touch();");

    /// <summary>The class <c>Sender</c>, whose <c>Run</c> writes the first cell of <c>_cells</c> where <c>_index</c> is zero and whose
    /// <c>Touch</c> writes it unguarded.</summary>
    /// <param name="declaration">What follows <c>class</c>: the name, a primary constructor, a base class.</param>
    /// <param name="members">The members that declare and store <c>_index</c>.</param>
    private static string Service(string declaration, string members) => $$"""
        public sealed class {{declaration}}
        {
            private readonly int[] _cells = new int[1];
            {{members}}
            public void Run() { if (_index == 0) /*@run*/_cells[0] = 1; }
            public void Touch() => /*@touch*/_cells[0] = 2;
        }
        """;

    /// <summary>The declaration of <c>_index</c> and a constructor that stores <paramref name="value"/> in it.</summary>
    /// <param name="value">The value stored.</param>
    private static string Stores(string value) => $"private readonly int _index; public Sender() {{ _index = {value}; }}";

    /// <summary>A <c>Sender</c> whose constructor stores <paramref name="value"/> in <c>_index</c>, its <c>Run</c> called by the
    /// reader given; the first hosted service does nothing.</summary>
    /// <param name="reader">The reader's types, then a bar, then its registrations.</param>
    /// <param name="value">The value the constructor stores.</param>
    private static HostedServiceProbe Reader(string reader, string value)
    {
        var bar = reader.LastIndexOf('|');
        return new HostedServiceProbe("Sender", reader[..bar] + Service("Sender", Stores(value)), "_ = _sender;", "_sender.Touch();",
                                      reader[(bar + 1)..].Trim());
    }

    /// <summary>A <c>Sender</c> whose constructor starts <c>Work</c>, which writes the first cell where <c>_index</c> is zero, and
    /// whose <c>Touch</c> writes it where <paramref name="touch"/> holds.</summary>
    /// <param name="constructor">The constructor's body.</param>
    /// <param name="touch">What follows <c>_index</c> in the guard of <c>Touch</c>, or null for no guard.</param>
    private static HostedServiceProbe Worked(string constructor, string? touch) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly int[] _cells = new int[1];
            private readonly int _index;
            public Sender() { {{constructor}} }
            private void Work() { if (_index == 0) /*@work*/_cells[0] = 1; }
            public void Touch() { {{(touch is null ? string.Empty : $"if (_index{touch}) ")}}/*@touch*/_cells[0] = 2; }
        }
        """, "_ = _sender;", "_sender.Touch();");

    /// <summary>A <c>Sender</c> that builds an <c>Opt</c> with <c>new</c>, whose readonly <c>Index</c> guards the write in its
    /// <c>Run</c>, with the constructor body given.</summary>
    /// <param name="constructor">The body of <c>Opt</c>'s constructor.</param>
    private static HostedServiceProbe Built(string constructor) => new("Sender", $$"""
        public sealed class Opt
        {
            public readonly int Index;
            private readonly int[] _cells;
            public Opt(int[] cells) { _cells = cells; {{constructor}} }
            public void Run() { if (Index == 0) /*@run*/_cells[0] = 1; }
            {{THROWER}}
        }
        public sealed class Sender
        {
            private readonly int[] _cells = new int[1];
            private readonly Opt _opt;
            public Sender() { _opt = new Opt(_cells); }
            public void Run() => _opt.Run();
            public void Touch() => /*@touch*/_cells[0] = 2;
        }
        """, "_sender.Run();", "_sender.Touch();");
}
