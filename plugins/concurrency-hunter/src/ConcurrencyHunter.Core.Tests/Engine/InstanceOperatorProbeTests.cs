using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the C# 14 instance compound assignment and increment operators (<c>void operator +=(int)</c>,
/// <c>void operator ++()</c>) across the forms that call them (issue 512), each judged by the C# 14 feature specification "User Defined
/// Compound Assignment Operators". <c>x op= y</c>, <c>++x</c> and <c>x++</c> on a variable whose type declares the instance operator call
/// it, in preference to a static one: on a target of a type known to be a reference type — a class, an interface, a type parameter
/// constrained to a class — the target is evaluated to get an object, the operator is called on that object and nothing is assigned, so
/// the variable is only read and the operator's body writes the object; on a struct or a type parameter not constrained to a class whose
/// result is not used, the operator is called on the variable itself, and its body writes the variable's storage in place; where that
/// result is used, the variable is read into a copy, the operator called on the copy and the copy assigned back, so the variable is read
/// and written as a whole and the body writes only the copy; a null-conditional assignment <c>a?.b op= y</c> whose value the statement
/// discards calls the operator in place, as the compiled statement does when run. A postfix increment whose result is used, a property
/// and a target whose type declares only a static operator keep the static operator and its long form <c>x = x op y</c>. The checked
/// operators run in a <c>checked</c> context, and an operator declared by an interface or overridden runs as the call dispatches. An
/// instance operator an extension block declares runs as the static method the compiler emits for it, the receiver its first parameter
/// (C# 14 feature specifications "Extension operators" and "Extension members"): a receiver of a reference type is passed by value, the
/// object itself, and one of a value type by <c>ref</c>, the only forms C# allows (CS9322, CS9300, CS9323), so the operator updates the
/// same object or storage, in place or on the same copy, as an operator the type declares, as the compiled program does when run. Every
/// probe runs through the whole analysis (<see cref="HostedServiceProbe"/>, one singleton <c>Holder</c> both hosted services call) and
/// names each finding's pair of sides; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer and
/// why.</summary>
public sealed class InstanceOperatorProbeTests
{
    /// <summary>Why the objects of an array a collection expression creates make no access.</summary>
    private const string COLLECTION_EXPRESSION_IS_UNSUPPORTED =
        "issue 298: a collection expression stays an unsupported operation, so the array it creates holds no object the analysis " +
        "follows, the ordinary member access of the control included";

    /// <summary>Why a member called on a struct primary-constructor parameter writes no shared storage.</summary>
    private const string PRIMARY_CONSTRUCTOR_STRUCT_IS_A_COPY =
        "issue 550: a struct primary-constructor parameter a member reads is read as a captured variable and loaded by value from its " +
        "synthesized field, so a member called on it, an ordinary method as the control shows as well as an instance operator, runs on " +
        "a copy";

    /// <summary>Why a member called through a reference of a type parameter without a struct constraint writes no shared storage.</summary>
    private const string TYPE_PARAMETER_REFERENCE_IS_A_COPY =
        "issue 551: a receiver of a type parameter without a struct constraint is lowered as its value, so a member called through a " +
        "ref parameter of it, an ordinary interface method as the control shows as well as an instance operator, runs on a copy when the " +
        "type argument is a struct";

    /// <summary>Why a member called on a struct cell of an array names no one cell.</summary>
    private const string MEMBER_ON_A_CELL_NAMES_NO_CELL =
        "issue 547: a member called on a struct cell of an array, an ordinary method as the control shows as well as an instance " +
        "operator, reaches the cell through this as no one cell ([?]), while a field of the cell written directly names its cell, " +
        "so two calls on two different cells pair";

    /// <summary>A class whose instance operators, the checked ones beside the regular ones, update its value in place.</summary>
    private const string METER = """
        public sealed class Meter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator checked +=(int amount) { checked { Value += amount; } }
            public void operator ++() { Value++; }
            public void operator checked ++() { checked { Value++; } }
            public void operator --() { Value--; }
        }

        """;

    /// <summary>A struct whose instance operators update its value in place, and an ordinary member that does the same.</summary>
    private const string COUNTER = """
        public struct Counter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator ++() { Value++; }
            public void Bump(int amount) { Value += amount; }
        }

        """;

    /// <summary>A class with a static and an instance operator of each kind, which write two different fields.</summary>
    private const string BOTH = """
        public sealed class Both
        {
            public int Value;
            public int Total;
            public static Both operator +(Both both, int amount) { both.Total += amount; return both; }
            public void operator +=(int amount) { Value += amount; }
            public static Both operator ++(Both both) { both.Total++; return both; }
            public void operator ++() { Value++; }
        }

        """;

    /// <summary>A class with only static operators.</summary>
    private const string STATIC_ONLY = """
        public sealed class Plain
        {
            public int Total;
            public static Plain operator +(Plain plain, int amount) { plain.Total += amount; return plain; }
            public static Plain operator ++(Plain plain) { plain.Total++; return plain; }
        }

        """;

    /// <summary>An interface that declares the instance operators, a class that implements them, and a base class whose virtual
    /// operator a derived class overrides.</summary>
    private const string DISPATCHED = """
        public interface IMeter { void operator +=(int amount); void operator ++(); }
        public sealed class Gauge : IMeter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator ++() { Value++; }
        }
        public class Dial { public int Value; public virtual void operator +=(int amount) { Value += amount; } }
        public sealed class FineDial : Dial { public override void operator +=(int amount) { Value += 2 * amount; } }

        """;

    /// <summary>A struct of two fields whose operator updates one, held by a class an access through <c>?.</c> reaches, with a
    /// class-typed meter beside it.</summary>
    private const string BOXED_PAIR = """
        public struct Pair { public int X; public int Y; public void operator +=(int amount) { X += amount; } }
        public sealed class Crate { public Pair Pair; public Meter Meter = new(); }

        """;

    /// <summary>An interface whose struct implementation has an instance operator and an ordinary member that update its value.</summary>
    private const string TICK = """
        public interface ITick { void operator +=(int amount); void Bump(int amount); }
        public struct Tick : ITick { public int Value; public void operator +=(int amount) { Value += amount; } public void Bump(int amount) { Value += amount; } }

        """;

    /// <summary>Extension blocks whose instance operators update a class by value and a struct by <c>ref</c>, generic blocks constrained
    /// to a class and to a struct that do it through an interface, and classic extension methods with the same bodies.</summary>
    private const string EXTENDED = """
        public interface IBump { void Bump(int amount); }
        public sealed class Plate : IBump { public int Value; public void Bump(int amount) { Value += amount; } }
        public struct Slot : IBump { public int Value; public void Bump(int amount) { Value += amount; } }
        public sealed class Shelf { public Slot Slot; }
        public static class PlateOperators
        {
            extension(Plate plate)
            {
                public void operator +=(int amount) { plate.Value += amount; }
                public void operator ++() { plate.Value++; }
            }
            extension(ref Slot slot)
            {
                public void operator +=(int amount) { slot.Value += amount; }
                public void operator ++() { slot.Value++; }
            }
            extension<T>(T item) where T : class, IBump { public void operator *=(int amount) { item.Bump(amount); } }
            extension<T>(ref T item) where T : struct, IBump { public void operator -=(int amount) { item.Bump(amount); } }
            public static void Add(this ref Slot slot, int amount) { slot.Value += amount; }
            public static void Take<T>(this ref T item, int amount) where T : struct, IBump { item.Bump(amount); }
        }

        """;

    /// <summary>A struct that holds a plate and an extension block whose operator updates the plate through it, by <c>ref</c>.</summary>
    private const string RACKED = """
        public sealed class Plate { public int Value; }
        public struct Rack { public Plate Plate; }
        public static class RackOperators { extension(ref Rack rack) { public void operator +=(int amount) { rack.Plate.Value += amount; } } }

        """;

    /// <summary>The lost update of <c>Value</c> in two runs of <paramref name="member"/>.</summary>
    /// <param name="member">The member that updates it.</param>
    private static string ValueUpdatedIn(string member) => Updated("Value", member);

    /// <summary>The lost update of <paramref name="resource"/> in two runs of <paramref name="member"/>.</summary>
    /// <param name="resource">The resource, as <see cref="HostedServiceProbe.Resource"/> writes it.</param>
    /// <param name="member">The member that updates it.</param>
    private static string Updated(string resource, string member) =>
        $"DCA1002 unprotected {resource}: ReadModifyWrite {member} | ReadModifyWrite {member}";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        // A class: the target is only read; the operator's body updates the object.
        ["class-prefix-increment"] = (Both(METER + Holder("private Meter _meter = new();", "++_meter;")),
                                      [ValueUpdatedIn("Meter.op_IncrementAssignment")]),
        ["class-decrement"] = (Both(METER + Holder("private Meter _meter = new();", "_meter--;")), [ValueUpdatedIn("Meter.op_DecrementAssignment")]),
        ["class-compound-assignment-result-used"] = (Both(METER + Holder("private Meter _meter = new(); public Meter Last;", "Last = _meter += 1;")),
                                                     [ValueUpdatedIn("Meter.op_AdditionAssignment"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["class-prefix-increment-result-used"] = (Both(METER + Holder("private Meter _meter = new(); public Meter Last;", "Last = ++_meter;")),
                                                  [ValueUpdatedIn("Meter.op_IncrementAssignment"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["class-checked-compound-assignment"] = (Both(METER + Holder("private Meter _meter = new();", "checked { _meter += 1; }")),
                                                 [ValueUpdatedIn("Meter.op_CheckedAdditionAssignment")]),
        ["class-checked-increment"] = (Both(METER + Holder("private Meter _meter = new();", "checked { _meter++; }")),
                                       [ValueUpdatedIn("Meter.op_CheckedIncrementAssignment")]),
        ["class-static-field"] = (Both(METER + Holder("private static Meter s_meter = new();", "s_meter += 1;")),
                                  [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-local"] = (Both(METER + Holder("private Meter _meter = new();", "var meter = _meter; meter += 1;")),
                           [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-parameter"] = (Both(METER + Holder("private Meter _meter = new(); private static void Add(Meter meter) => meter += 1;", "Add(_meter);")),
                               [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-ref-parameter"] = (Both(METER + Holder("private Meter _meter = new(); private static void Add(ref Meter meter) => meter += 1;",
                                                       "Add(ref _meter);")),
                                   [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-array-element-member-control"] = (Both(METER + Holder("private readonly Meter[] _meters = [new()];", "_meters[0].Value++;")),
                                                  [ValueUpdatedIn("Holder.Run")]),
        ["class-array-element"] = (Both(METER + Holder("private readonly Meter[] _meters = [new()];", "_meters[0] += 1;")),
                                   [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-array-element-created-by-an-initializer"] = (Both(METER + Holder("private readonly Meter[] _meters = { new() };", "_meters[0] += 1;")),
                                                             [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-ref-returning-property"] = (Both(METER + Holder("private Meter _meter = new(); private ref Meter Current => ref _meter;", "Current += 1;")),
                                            [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-right-hand-side-that-branches"] = (Both(METER + Holder("private Meter _meter = new(); private bool _large;", "_meter += _large ? 10 : 1;")),
                                                   [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["class-primary-constructor-parameter-from-the-container"] = (new("Holder", METER + """
                public sealed class Holder(Meter meter) { public void Run() => meter += 1; }
                """, "_holder.Run();", "_holder.Run();", "services.AddSingleton<Meter>();"), [ValueUpdatedIn("Meter.op_AdditionAssignment")]),

        // A struct whose result is not used: the operator's body updates the variable's storage in place, as an ordinary member does.
        ["struct-member-control"] = (Both(COUNTER + Holder("private Counter _counter;", "_counter.Bump(1);")), [Updated("_counter.Value", "Counter.Bump")]),
        ["struct-compound-assignment"] = (Both(COUNTER + Holder("private Counter _counter;", "_counter += 1;")),
                                          [Updated("_counter.Value", "Counter.op_AdditionAssignment")]),
        ["struct-postfix-increment"] = (Both(COUNTER + Holder("private Counter _counter;", "_counter++;")),
                                        [Updated("_counter.Value", "Counter.op_IncrementAssignment")]),
        ["struct-prefix-increment"] = (Both(COUNTER + Holder("private Counter _counter;", "++_counter;")),
                                       [Updated("_counter.Value", "Counter.op_IncrementAssignment")]),
        ["struct-ref-parameter"] = (Both(COUNTER + Holder("private Counter _counter; private static void Add(ref Counter counter) => counter += 1;",
                                                          "Add(ref _counter);")),
                                    [Updated("_counter.Value", "Counter.op_AdditionAssignment")]),
        ["struct-array-element-member-control"] = (Both(COUNTER + Holder("private readonly Counter[] _counters = new Counter[1];", "_counters[0].Bump(1);")),
                                                   [Updated("_counters.Value.[0]", "Counter.Bump")]),
        ["struct-array-element"] = (Both(COUNTER + Holder("private readonly Counter[] _counters = new Counter[1];", "_counters[0] += 1;")),
                                    [Updated("_counters.Value.[0]", "Counter.op_AdditionAssignment")]),
        ["struct-distinct-array-elements-field-control"] = (Cells(COUNTER + Cell("private readonly Counter[] _counters = new Counter[2];",
                                                                                  "_counters[index].Value += 1;")), []),
        ["struct-distinct-array-elements-member-control"] = (Cells(COUNTER + Cell("private readonly Counter[] _counters = new Counter[2];",
                                                                                   "_counters[index].Bump(1);")), []),
        ["struct-distinct-array-elements"] = (Cells(COUNTER + Cell("private readonly Counter[] _counters = new Counter[2];", "_counters[index] += 1;")),
                                              []),
        ["struct-right-hand-side-that-branches"] = (Both(COUNTER + Holder("private Counter _counter; private bool _large;", "_counter += _large ? 10 : 1;")),
                                                    [Updated("_counter.Value", "Counter.op_AdditionAssignment")]),
        ["struct-local-copy"] = (Both(COUNTER + Holder("private Counter _counter;", "var counter = _counter; counter += 1;")), []),
        ["struct-primary-constructor-parameter-member-control"] = (Both(COUNTER + """
                public sealed class Holder(Counter counter) { public Holder() : this(default) { } public void Run() => counter.Bump(1); }
                """), [Updated("counter.Value", "Counter.Bump")]),
        ["struct-primary-constructor-parameter"] = (Both(COUNTER + """
                public sealed class Holder(Counter counter) { public Holder() : this(default) { } public void Run() => counter += 1; }
                """), [Updated("counter.Value", "Counter.op_AdditionAssignment")]),
        ["type-parameter-by-reference-member-control"] = (Both(TICK + Holder(
                "private Tick _tick; private static void Add<T>(ref T tick) where T : ITick => tick.Bump(1);", "Add(ref _tick);")),
                [Updated("_tick.Value", "Tick.Bump")]),
        ["type-parameter-by-reference"] = (Both(TICK + Holder(
                "private Tick _tick; private static void Add<T>(ref T tick) where T : ITick => tick += 1;", "Add(ref _tick);")),
                [Updated("_tick.Value", "Tick.op_AdditionAssignment")]),

        // A null-conditional assignment whose value the statement discards calls the operator in place, as a plain statement does
        // (C# 14 null-conditional assignment, checked by running the compiled statement); one whose value is used assigns the copy.
        ["null-conditional-class"] = (Both(METER + BOXED_PAIR + Holder("private Crate _crate = new();", "_crate?.Meter += 1;")),
                                      [ValueUpdatedIn("Meter.op_AdditionAssignment")]),
        ["null-conditional-struct"] = (Both(METER + BOXED_PAIR + Holder("private Crate _crate = new();", "_crate?.Pair += 1;")),
                                       [Updated("Pair.X", "Pair.op_AdditionAssignment")]),
        ["null-conditional-struct-against-another-field"] = (new("Holder", METER + BOXED_PAIR + """
                public sealed class Holder { private Crate _crate = new(); public void Add() => _crate?.Pair += 1; public void Touch() => _crate.Pair.Y++; }
                """, "_holder.Add();", "_holder.Touch();"), []),
        ["null-conditional-struct-right-hand-side-that-branches"] = (new("Holder", METER + BOXED_PAIR + """
                public sealed class Holder { private Crate _crate = new(); private bool _large; public void Add() => _crate?.Pair += _large ? 2 : 1; public void Touch() => _crate.Pair.Y++; }
                """, "_holder.Add();", "_holder.Add();"), [Updated("Pair.X", "Pair.op_AdditionAssignment")]),
        ["null-conditional-struct-result-used"] = (Both(METER + BOXED_PAIR + Holder("private Crate _crate = new(); public int Last;",
                                                                                       "Last = (_crate?.Pair += 1)?.X ?? 0;")),
                                                   [Updated("Pair", "Holder.Run"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),

        // A struct whose result is used: the variable is read into a copy and the copy assigned back; the body writes only the copy.
        ["struct-copy-assigned-back-control"] = (Both(COUNTER + Holder("private Counter _counter;", "var copy = _counter; copy.Bump(1); _counter = copy;")),
                                                 [Updated("_counter", "Holder.Run")]),
        ["struct-compound-assignment-result-used"] = (Both(COUNTER + Holder("private Counter _counter; public int Last;", "Last = (_counter += 1).Value;")),
                                                      [Updated("_counter", "Holder.Run"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["struct-prefix-increment-result-used"] = (Both(COUNTER + Holder("private Counter _counter; public int Last;", "Last = (++_counter).Value;")),
                                                   [Updated("_counter", "Holder.Run"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),

        // A class with both kinds: the instance operator wins wherever C# considers it; a used postfix increment takes the static one.
        ["both-compound-assignment"] = (Both(BOTH + Holder("private Both _both = new();", "_both += 1;")), [ValueUpdatedIn("Both.op_AdditionAssignment")]),
        ["both-compound-assignment-result-used"] = (Both(BOTH + Holder("private Both _both = new(); public Both Last;", "Last = _both += 1;")),
                                                    [ValueUpdatedIn("Both.op_AdditionAssignment"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["both-postfix-increment"] = (Both(BOTH + Holder("private Both _both = new();", "_both++;")), [ValueUpdatedIn("Both.op_IncrementAssignment")]),
        ["both-postfix-increment-result-used"] = (Both(BOTH + Holder("private Both _both = new(); public Both Last;", "Last = _both++;")),
                                                  [Updated("Total", "Both.op_Increment"), Updated("_both", "Holder.Run"),
                                                   "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["both-property"] = (Both(BOTH + Holder("public Both Current { get; set; } = new();", "Current += 1;")),
                             [Updated("Total", "Both.op_Addition"), Updated("Current", "Holder.Run")]),

        // Only static operators: the long form reads the variable, calls the operator and writes what it returns back.
        ["static-only-compound-assignment"] = (Both(STATIC_ONLY + Holder("private Plain _plain = new();", "_plain += 1;")),
                                               [Updated("Total", "Plain.op_Addition"), Updated("_plain", "Holder.Run")]),
        ["static-only-increment"] = (Both(STATIC_ONLY + Holder("private Plain _plain = new();", "_plain++;")),
                                     [Updated("Total", "Plain.op_Increment"), Updated("_plain", "Holder.Run")]),

        // Dispatched: the operator an interface declares, called through it or through a constrained type parameter, and an override.
        ["interface-field"] = (Both(DISPATCHED + Holder("private IMeter _meter = new Gauge();", "_meter += 1;")), [ValueUpdatedIn("Gauge.op_AdditionAssignment")]),
        ["interface-increment"] = (Both(DISPATCHED + Holder("private IMeter _meter = new Gauge();", "_meter++;")), [ValueUpdatedIn("Gauge.op_IncrementAssignment")]),
        ["type-parameter-constrained-to-the-interface"] = (Both(DISPATCHED + Holder(
                "private readonly Gauge _gauge = new(); private static void Add<T>(T meter) where T : IMeter => meter += 1;", "Add(_gauge);")),
                [ValueUpdatedIn("Gauge.op_AdditionAssignment")]),
        ["type-parameter-constrained-to-the-interface-result-used"] = (Both(DISPATCHED + Holder(
                "private readonly Gauge _gauge = new(); private static T Add<T>(T meter) where T : IMeter => meter += 1;", "Add(_gauge);")),
                [ValueUpdatedIn("Gauge.op_AdditionAssignment")]),
        ["type-parameter-constrained-to-a-class"] = (Both(DISPATCHED + Holder(
                "private readonly Gauge _gauge = new(); private static void Add<T>(T meter) where T : class, IMeter => meter += 1;", "Add(_gauge);")),
                [ValueUpdatedIn("Gauge.op_AdditionAssignment")]),
        ["override"] = (Both(DISPATCHED + Holder("private Dial _dial = new FineDial();", "_dial += 1;")), [ValueUpdatedIn("FineDial.op_AdditionAssignment")]),

        // An extension block's operator runs as its static method, the receiver its first parameter: a class by value, nothing assigned.
        ["extension-block-operator"] = (Both("""
                public sealed class Plate { public int Value; }
                public static class PlateOperators { extension(Plate plate) { public void operator +=(int amount) { plate.Value += amount; } } }
                """ + Holder("private Plate _plate = new();", "_plate += 1;")), [ValueUpdatedIn("PlateOperators.op_AdditionAssignment")]),
        ["extension-block-static-form-control"] = (Both(EXTENDED + Holder("private Plate _plate = new();", "PlateOperators.op_AdditionAssignment(_plate, 1);")),
                                                   [ValueUpdatedIn("PlateOperators.op_AdditionAssignment")]),
        ["extension-block-increment"] = (Both(EXTENDED + Holder("private Plate _plate = new();", "_plate++;")),
                                         [ValueUpdatedIn("PlateOperators.op_IncrementAssignment")]),
        ["extension-block-result-used"] = (Both(EXTENDED + Holder("private Plate _plate = new(); public Plate Last;", "Last = _plate += 1;")),
                                           [ValueUpdatedIn("PlateOperators.op_AdditionAssignment"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["extension-block-primary-constructor-parameter"] = (new("Holder", EXTENDED + """
                public sealed class Holder(Plate plate) { public void Run() => plate += 1; }
                """, "_holder.Run();", "_holder.Run();", "services.AddSingleton<Plate>();"), [ValueUpdatedIn("PlateOperators.op_AdditionAssignment")]),
        ["extension-block-class-type-parameter"] = (Both(EXTENDED + Holder("private Plate _plate = new();", "_plate *= 2;")), [ValueUpdatedIn("Plate.Bump")]),

        // A struct by ref: in place where the result is not used, on a copy assigned back where it is, as the compiled program runs.
        ["extension-block-struct-member-control"] = (Both(EXTENDED + Holder("private Slot _slot;", "_slot.Add(1);")),
                                                     [Updated("_slot.Value", "PlateOperators.Add")]),
        ["extension-block-struct-static-form-control"] = (Both(EXTENDED + Holder("private Slot _slot;", "PlateOperators.op_AdditionAssignment(ref _slot, 1);")),
                                                          [Updated("_slot.Value", "PlateOperators.op_AdditionAssignment")]),
        ["extension-block-struct"] = (Both(EXTENDED + Holder("private Slot _slot;", "_slot += 1;")),
                                      [Updated("_slot.Value", "PlateOperators.op_AdditionAssignment")]),
        ["extension-block-struct-prefix-increment"] = (Both(EXTENDED + Holder("private Slot _slot;", "++_slot;")),
                                                       [Updated("_slot.Value", "PlateOperators.op_IncrementAssignment")]),
        ["extension-block-struct-postfix-increment"] = (Both(EXTENDED + Holder("private Slot _slot;", "_slot++;")),
                                                        [Updated("_slot.Value", "PlateOperators.op_IncrementAssignment")]),
        ["extension-block-struct-branching-right-hand-side"] = (Both(EXTENDED + Holder("private Slot _slot; private bool _large;",
                                                                                        "_slot += _large ? 10 : 1;")),
                                                                [Updated("_slot.Value", "PlateOperators.op_AdditionAssignment")]),
        ["extension-block-struct-null-conditional"] = (Both(EXTENDED + Holder("private Shelf _shelf = new();", "_shelf?.Slot += 1;")),
                                                       [Updated("Slot.Value", "PlateOperators.op_AdditionAssignment")]),
        ["extension-block-struct-local-copy"] = (Both(EXTENDED + Holder("private Slot _slot;", "var slot = _slot; slot += 1;")), []),
        ["extension-block-struct-copy-assigned-back-control"] = (Both(EXTENDED + Holder("private Slot _slot;",
                                                                                         "var copy = _slot; PlateOperators.op_AdditionAssignment(ref copy, 1); _slot = copy;")),
                                                                 [Updated("_slot", "Holder.Run")]),
        ["extension-block-struct-result-used"] = (Both(EXTENDED + Holder("private Slot _slot; public int Last;", "Last = (_slot += 1).Value;")),
                                                  [Updated("_slot", "Holder.Run"), "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["extension-block-struct-held-object"] = (Both(RACKED + Holder("private Rack _rack = new() { Plate = new() };", "_rack += 1;")),
                                                  [ValueUpdatedIn("RackOperators.op_AdditionAssignment")]),
        ["extension-block-struct-held-object-copy-control"] = (Both(RACKED + Holder(
                "private Rack _rack = new() { Plate = new() }; public Plate Last;",
                "var copy = _rack; RackOperators.op_AdditionAssignment(ref copy, 1); _rack = copy; Last = copy.Plate;")),
                [ValueUpdatedIn("RackOperators.op_AdditionAssignment"), Updated("_rack", "Holder.Run"),
                 "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["extension-block-struct-held-object-result-used"] = (Both(RACKED + Holder(
                "private Rack _rack = new() { Plate = new() }; public Plate Last;", "Last = (_rack += 1).Plate;")),
                [ValueUpdatedIn("RackOperators.op_AdditionAssignment"), Updated("_rack", "Holder.Run"),
                 "DCA1001 unprotected Last: Write Holder.Run | Write Holder.Run"]),
        ["extension-block-struct-type-parameter-control"] = (Both(EXTENDED + Holder("private Slot _slot;", "_slot.Take(1);")),
                                                             [Updated("_slot.Value", "Slot.Bump")]),
        ["extension-block-struct-type-parameter"] = (Both(EXTENDED + Holder("private Slot _slot;", "_slot -= 1;")), [Updated("_slot.Value", "Slot.Bump")])
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["class-array-element-member-control"] = new([], COLLECTION_EXPRESSION_IS_UNSUPPORTED),
        ["class-array-element"] = new([], COLLECTION_EXPRESSION_IS_UNSUPPORTED),
        ["struct-array-element-member-control"] = new([Updated("_counters.Value.[?]", "Counter.Bump")], MEMBER_ON_A_CELL_NAMES_NO_CELL),
        ["struct-array-element"] = new([Updated("_counters.Value.[?]", "Counter.op_AdditionAssignment")], MEMBER_ON_A_CELL_NAMES_NO_CELL),
        ["struct-distinct-array-elements-member-control"] = new([Updated("_counters.Value.[?]", "Counter.Bump")], MEMBER_ON_A_CELL_NAMES_NO_CELL),
        ["struct-distinct-array-elements"] = new([Updated("_counters.Value.[?]", "Counter.op_AdditionAssignment")], MEMBER_ON_A_CELL_NAMES_NO_CELL),
        ["struct-primary-constructor-parameter-member-control"] = new([], PRIMARY_CONSTRUCTOR_STRUCT_IS_A_COPY),
        ["struct-primary-constructor-parameter"] = new([], PRIMARY_CONSTRUCTOR_STRUCT_IS_A_COPY),
        ["type-parameter-by-reference-member-control"] = new([], TYPE_PARAMETER_REFERENCE_IS_A_COPY),
        ["type-parameter-by-reference"] = new([], TYPE_PARAMETER_REFERENCE_IS_A_COPY)
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

        var answer = await probe.FindingsAsync();

        HostedServiceProbe.AssertAnswer(answer, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>Every compound assignment operator C# 14 lets a type declare as an instance operator is called on the object its
    /// class-typed target holds.</summary>
    /// <param name="token">The operator's token.</param>
    /// <param name="name">The operator method's metadata name.</param>
    [Theory]
    [InlineData("-=", "op_SubtractionAssignment")]
    [InlineData("*=", "op_MultiplicationAssignment")]
    [InlineData("/=", "op_DivisionAssignment")]
    [InlineData("%=", "op_ModulusAssignment")]
    [InlineData("&=", "op_BitwiseAndAssignment")]
    [InlineData("|=", "op_BitwiseOrAssignment")]
    [InlineData("^=", "op_ExclusiveOrAssignment")]
    [InlineData("<<=", "op_LeftShiftAssignment")]
    [InlineData(">>=", "op_RightShiftAssignment")]
    [InlineData(">>>=", "op_UnsignedRightShiftAssignment")]
    public async Task Every_compound_assignment_operator_updates_the_object_its_target_holds(string token, string name)
    {
        var probe = Both($$"""
            public sealed class Meter { public int Value; public void operator {{token}}(int amount) { Value {{token}} amount; } }
            """ + Holder("private Meter _meter = new();", $"_meter {token} 1;"));

        HostedServiceProbe.AssertAnswer(await probe.FindingsAsync(), [ValueUpdatedIn("Meter." + name)], null);
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A singleton <c>Holder</c> with members and a <c>Run</c> that runs a statement.</summary>
    /// <param name="members">The members besides <c>Run</c>.</param>
    /// <param name="statement">The statement <c>Run</c> runs.</param>
    private static string Holder(string members, string statement) =>
        $"public sealed class Holder {{ {members} public void Run() {{ {statement} }} }}";

    /// <summary>A singleton <c>Holder</c> with members and a <c>Run(int index)</c> that runs a statement.</summary>
    /// <param name="members">The members besides <c>Run</c>.</param>
    /// <param name="statement">The statement <c>Run</c> runs, which may use <c>index</c>.</param>
    private static string Cell(string members, string statement) =>
        $"public sealed class Holder {{ {members} public void Run(int index) {{ {statement} }} }}";

    /// <summary>A probe of a singleton <c>Holder</c> whose <c>Run</c> both hosted services call.</summary>
    /// <param name="types">The declaration of <c>Holder</c> and the types it uses.</param>
    private static HostedServiceProbe Both(string types) => new("Holder", types, "_holder.Run();", "_holder.Run();");

    /// <summary>A probe of a singleton <c>Holder</c> whose <c>Run(int)</c> one hosted service calls with 0 and the other with 1.</summary>
    /// <param name="types">The declaration of <c>Holder</c> and the types it uses.</param>
    private static HostedServiceProbe Cells(string types) => new("Holder", types, "_holder.Run(0);", "_holder.Run(1);");
}
